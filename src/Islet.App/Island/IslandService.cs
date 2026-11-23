// SPDX-License-Identifier: GPL-3.0-or-later
// The Dynamic Island's controller: the Windows counterpart of upstream Services/Notch/NotchService.swift.
// It keeps the presentation state (closed, hover emphasis, notice, live activity, open page),
// drives the analytic springs every rendered frame, and feeds the window from the media,
// volume, power and timer sources. Pure decisions live in Islet.Core.Notch.
using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Globalization;
using System.Runtime.CompilerServices;
using Islet.App.Island.Files;
using Islet.App.Island.Lyrics;
using Islet.App.Platform;
using Islet.App.Platform.Audio;
using Islet.App.Platform.Input;
using Islet.App.Platform.Media;
using Islet.App.Platform.Power;
using Islet.App.Platform.Shell;
using Islet.App.Platform.Windowing;
using Islet.App.Shell;
using Islet.Core.Features;
using Islet.Core.Notch;
using Islet.Core.Notch.Lyrics;
using Islet.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace Islet.App.Island;

public sealed class IslandService : IFeatureService
{
    private enum ClosedContent { Bare, Music, Timer, Notice, Drop, Open }
    private enum NoticeKind { Volume, Brightness, Microphone, Battery, Track, Timer }

    private sealed record Notice(NoticeKind Kind, string Glyph, double? Level, string? Text, ImageSource? Art,
                                 double Leading, double Trailing, double Seconds, Color? Tint = null);

    private static readonly NotchModule[] ImplementedPages = [NotchModule.Controls, NotchModule.Music, NotchModule.Files, NotchModule.Timer];
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A), Red = Color.FromRgb(0xFF, 0x45, 0x3A),
                                  Green = Color.FromRgb(0x30, 0xD1, 0x58), Amber = Color.FromRgb(0xFF, 0xD6, 0x0A);

    private readonly SettingsStore _settings;
    private readonly InputHookService _input;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly NotchGestureSupport _gesture = new();
    private readonly NotchTimerSession _timer = new();

    private NotchWindow? _window;
    private LyricsService? _lyrics;
    private LiveEqualizerService? _equalizer;
    private ShelfService? _shelf;
    private FilesPage? _files;
    // Drag-reveal: a low-level mouse hook notices a held button near the notch (hook thread fields).
    private sealed record RevealZone(int Left, int Top, int Right, int Bottom);
    private volatile RevealZone? _revealZone;
    private IntPtr _islandHandle;
    private IDisposable? _dragHook;
    private bool _hookDown, _hookFromIsland, _hookNear;
    private int _hookDownX, _hookDownY;
    private bool _dragPlaceholder;
    private DispatcherTimer? _revealTimer;
    private bool _lyricsOpen, _modalOpen;
    private int? _lyricsActive = -1;
    private string _lyricsKey = "";
    private readonly List<TextBlock> _lyricLines = [];
    private TextBlock? _lyricsWaiting;
    private DispatcherTimer? _lyricsTimer, _scrollTimer;
    private double _scrollFrom, _scrollTo, _scrollStart;
    private AppBarStrip? _strip;
    private MediaSessionService? _media;
    private VolumeService? _volume;
    private PowerService? _power;
    private ForegroundWatcher? _foreground;
    private IDisposable? _volumeKeys;
    private DispatcherTimer? _hoverTimer, _closeTimer, _noticeTimer, _pollTimer, _tickTimer, _barsTimer;

    private DisplayInfo? _display;
    private NotchGeometry? _geometry;
    private string _placementKey = "";

    private bool _running, _expanded, _openedByHover, _focusTaken, _inside, _hoverEmphasized, _hiddenInFullscreen;
    private bool _updatingControls, _seeking;
    private NotchModule _page = NotchModule.Controls;
    private Notice? _notice;
    private NotchHoverState _hoverState;
    private ClosedContent _shown = ClosedContent.Bare;
    private double _startedAt;

    // Motion
    private NotchMotionTimeline? _timeline;
    private SizeD _drawn = SizeD.Zero, _target = SizeD.Zero;
    private double _shiftFrom, _shiftTo, _shiftBegin;
    private bool _framesOn;

    public IslandService(SettingsStore settings, InputHookService input, Dispatcher dispatcher)
    {
        _settings = settings;
        _input = input;
        _dispatcher = dispatcher;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    public string StatusText => !_running ? "Off."
        : _hiddenInFullscreen ? "Hidden while a full-screen app runs."
        : $"Showing on the {(_display?.IsBuiltIn == true ? "built-in" : "main")} display.";

    // MARK: Lifecycle (upstream syncWithPreferences)

    public void Sync()
    {
        var enabled = NotchSupport.IsEnabled(_settings);
        if (enabled && !_running) Start();
        else if (!enabled && _running) Stop();
        if (_running) ApplySettings();
    }

    private void Start()
    {
        _running = true;
        _startedAt = Now;
        var window = new NotchWindow();
        _window = window;
        window.Surface.MouseEnter += (_, _) => OnPointer(true);
        window.Surface.MouseLeave += (_, _) => OnPointer(false);
        window.Surface.MouseLeftButtonUp += OnClick;
        window.Surface.PreviewMouseWheel += OnWheel;
        window.HorizontalWheel += OnHorizontalWheel;
        window.Deactivated += (_, _) => { if (_modalOpen) return; Log.Debug("Island deactivated (expanded {Expanded}, focus {Focus})", _expanded, _focusTaken); if (_expanded && _focusTaken) Collapse(); };
        // Escape closes lyrics before the island, as upstream.
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_lyricsOpen && _page == NotchModule.Music) SetLyricsOpen(false);
            else if (_page == NotchModule.Files && _shelf?.Store.Selection.Count > 0) _shelf.ClearSelection();
            else Collapse();
        };
        WireControls(window);
        window.Show();

        _media = new MediaSessionService(_dispatcher);
        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;
        _lyrics = new LyricsService(_settings);
        _equalizer = new LiveEqualizerService(_settings, _dispatcher, () => _media?.Current);
        _shelf = new ShelfService(_dispatcher);
        _files = new FilesPage(window, _shelf, _settings, modal => _modalOpen = modal, Collapse);
        _files.Render();
        window.Surface.AllowDrop = true;
        window.Surface.DragEnter += OnDragOver;
        window.Surface.DragOver += OnDragOver;
        window.Surface.Drop += OnDrop;
        _lyrics.Changed += RenderLyrics;
        _ = _media.StartAsync();
        try
        {
            _volume = new VolumeService(_dispatcher);
            _volume.VolumeChanged += OnVolumeChanged;
            _volume.MicrophoneMuteChanged += OnMicrophoneChanged;
        }
        catch (Exception e) { Log.Warning(e, "Audio endpoints unavailable"); }
        _power = new PowerService(_dispatcher);
        _power.BatteryChanged += OnBatteryChanged;
        _power.BrightnessChanged += OnBrightnessChanged;
        _foreground = new ForegroundWatcher();
        _foreground.ForegroundChanged += _ => { _window?.ReassertTopmost(); CheckFullscreen(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        _hoverTimer = NewTimer(OnHoverTimer);
        _closeTimer = NewTimer(OnCloseTimer);
        _noticeTimer = NewTimer(OnNoticeTimer);
        _pollTimer = NewTimer(CheckFullscreen, TimeSpan.FromSeconds(1));
        _tickTimer = NewTimer(OnTick, TimeSpan.FromMilliseconds(250));
        _barsTimer = NewTimer(OnBars, TimeSpan.FromMilliseconds(33));
        _lyricsTimer = NewTimer(OnLyricsTimer);
        _revealTimer = NewTimer(EndReveal, TimeSpan.FromMilliseconds(350));
        _scrollTimer = NewTimer(OnScrollFrame, TimeSpan.FromMilliseconds(15));
        _tickTimer.Start();
        Log.Information("Island started");
    }

    private void Stop()
    {
        _running = false;
        StopFrames();
        foreach (var timer in new[] { _hoverTimer, _closeTimer, _noticeTimer, _pollTimer, _tickTimer, _barsTimer, _lyricsTimer, _scrollTimer, _revealTimer }) timer?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _volumeKeys?.Dispose(); _volumeKeys = null;
        _foreground?.Dispose(); _foreground = null;
        _media?.Dispose(); _media = null;
        _lyrics?.Dispose(); _lyrics = null;
        _equalizer?.Dispose(); _equalizer = null;
        _dragHook?.Dispose(); _dragHook = null;
        _revealZone = null;
        _shelf?.Dispose(); _shelf = null; _files = null;
        _dragPlaceholder = false;
        _volume?.Dispose(); _volume = null;
        _power?.Dispose(); _power = null;
        _strip?.Close(); _strip = null;
        _window?.Close(); _window = null;
        _expanded = false; _notice = null; _drawn = SizeD.Zero; _target = SizeD.Zero; _placementKey = "";
        Log.Information("Island stopped");
    }

    public void Dispose() { if (_running) Stop(); }

    private DispatcherTimer NewTimer(Action tick, TimeSpan? interval = null)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = interval ?? TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    // MARK: Settings

    private void ApplySettings()
    {
        if (_window is null) return;
        var display = Displays.Choose(NotchEnum.Parse(_settings.GetString(DefaultsKey.NotchDisplay), NotchDisplay.Automatic));
        if (display is null) return;
        var layout = NotchEnum.Parse(_settings.GetString(DefaultsKey.NotchSize), NotchSize.Spacious);
        var silhouette = NotchEnum.Parse(_settings.GetString(DefaultsKey.NotchSilhouette), NotchSilhouette.Notch);
        var geometry = new NotchGeometry(display.LocalDips, _settings.GetDouble(DefaultsKey.NotchHeight, 24), layout,
            _settings.GetDouble(DefaultsKey.NotchCustomWidth), _settings.GetDouble(DefaultsKey.NotchCustomHeight), silhouette,
            new NotchCapsuleFit(_settings.GetDouble(DefaultsKey.NotchCapsuleFitWidth), _settings.GetDouble(DefaultsKey.NotchCapsuleFitHeight),
                                _settings.GetDouble(DefaultsKey.NotchCapsuleFitDrop)));
        var offset = _settings.GetDouble(DefaultsKey.NotchOffsetX);
        var key = $"{display.DeviceName}|{display.Bounds}|{display.Scale}|{geometry}|{offset}";
        _display = display;
        _geometry = geometry;
        if (key != _placementKey)
        {
            _placementKey = key;
            var envelope = geometry.Envelope;
            var room = new SizeD(Math.Min(display.LocalDips.Width, envelope.Width + 24), Math.Min(display.LocalDips.Height, envelope.Height + 8));
            _window.Place(display, room, offset);
            _drawn = SizeD.Zero; // snap to the new geometry
        }

        // The reserved strip (AppBar) along the top edge.
        var reserve = _settings.GetBool(DefaultsKey.NotchReserveStrip);
        if (reserve && _strip is null)
        {
            _strip = new AppBarStrip(display, geometry.StripHeight + geometry.FloatingDrop);
            _strip.Show();
            _window.ReassertTopmost();
        }
        else if (reserve && _strip is not null) _strip.Update(display, geometry.StripHeight + geometry.FloatingDrop);
        else if (!reserve && _strip is not null) { _strip.Close(); _strip = null; }

        _window.SetShownInCaptures(_settings.GetBool(DefaultsKey.NotchShowInCaptures));
        if (_media is not null) _media.PreferredPlayer = _settings.GetString(DefaultsKey.NotchPreferredPlayer);

        var swallow = _settings.GetBool(DefaultsKey.NotchSwallowVolumeKeys) && _settings.GetBool(DefaultsKey.NotchVolume);
        if (swallow && _volumeKeys is null) _volumeKeys = _input.SubscribeKeyboard(OnVolumeKey);
        else if (!swallow && _volumeKeys is not null) { _volumeKeys.Dispose(); _volumeKeys = null; }

        if (_settings.GetBool(DefaultsKey.NotchHideInFullscreen)) _pollTimer?.Start();
        else { _pollTimer?.Stop(); _hiddenInFullscreen = false; }

        BuildTabs();
        BuildTimerPresets();
        UpdatePages();
        SyncLyrics();
        UpdateRevealZone(display, geometry, offset);
        SyncDragReveal();
        Refresh(animated: _drawn != SizeD.Zero);
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        _placementKey = "";
        if (_running) ApplySettings();
    });

    // MARK: Presentation

    /// <summary>The open island around the visible page, as upstream's expandedSize(module:).</summary>
    private SizeD ExpandedSize()
    {
        if (_geometry is not { } g || _window is not { } w) return _geometry?.Expanded ?? SizeD.Zero;
        FrameworkElement page = _page switch
        {
            NotchModule.Music => w.MusicPlayer,
            NotchModule.Files => w.FilesPage,
            NotchModule.Timer => w.TimerPage,
            _ => w.HomePage,
        };
        return g.ExpandedSize(w.MeasurePage(page, g.ContentWidth) + 4, LyricsShown ? g.MusicExtrasHeight : 0);
    }

    private NotchCompactActivity? CurrentActivity()
    {
        var running = new List<NotchCompactActivity>();
        if (_timer.HasSession && _settings.IsAvailable(AppFeature.NotchTimer) && _settings.GetBool(DefaultsKey.NotchTimerEnabled)
            && !_settings.GetBool(DefaultsKey.NotchHideTimerCountdown))
            running.Add(NotchCompactActivity.Timer);
        if (_media?.Current is { IsPlaying: true } && _settings.GetBool(DefaultsKey.NotchShowPlayingMusic)
            && _settings.GetString(DefaultsKey.NotchIdleContent, "music") == "music")
            running.Add(NotchCompactActivity.Music);
        return NotchActivities.Prioritized(running).Cast<NotchCompactActivity?>().FirstOrDefault();
    }

    /// <summary>Works out the size, centre shift and content the island should show, then moves there.</summary>
    private void Refresh(bool animated = true)
    {
        if (!_running || _window is null || _geometry is not { } g) return;
        var hidden = _hiddenInFullscreen && !_expanded;
        _window.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;

        SizeD size;
        double shift = 0;
        ClosedContent content;
        if (_expanded)
        {
            size = ExpandedSize();
            _window.LayoutOpenLayer(size, g);
            content = ClosedContent.Open;
        }
        else if (_dragPlaceholder)
        {
            size = g.DropPlaceholder;
            content = ClosedContent.Drop;
        }
        else if (_notice is { } notice)
        {
            size = g.NoticeSize(notice.Leading, notice.Trailing);
            shift = g.NoticeShift(notice.Leading, notice.Trailing);
            content = ClosedContent.Notice;
        }
        else if (CurrentActivity() is { } activity)
        {
            size = activity == NotchCompactActivity.Timer ? g.TimerStrip(52) : g.MusicStrip;
            content = activity == NotchCompactActivity.Timer ? ClosedContent.Timer : ClosedContent.Music;
        }
        else
        {
            size = _settings.GetBool(DefaultsKey.NotchHideUntilHover) && !_inside
                ? new SizeD(g.CameraWidth, 4) : g.RestingSize(showsContent: false);
            content = ClosedContent.Bare;
        }
        if (_hoverEmphasized && !_expanded && _notice is null && !_dragPlaceholder) size = g.HoverEmphasis(size);

        ShowClosedContent(content, g);
        SyncEqualizer();
        UpdateWarningTint();
        MoveTo(size, shift, animated && !hidden);
    }

    private void ShowClosedContent(ClosedContent content, NotchGeometry g)
    {
        if (_window is not { } w) return;
        _shown = content;
        var body = g.StripBodyHeight;
        w.StripArt.Visibility = Visibility.Collapsed;
        w.StripIcon.Visibility = Visibility.Collapsed;
        w.StripBars.Visibility = Visibility.Collapsed;
        w.StripText.Visibility = Visibility.Collapsed;
        w.StripLevel.Visibility = Visibility.Collapsed;
        w.DropPlaceholder.Visibility = content == ClosedContent.Drop ? Visibility.Visible : Visibility.Collapsed;
        w.StripText.Margin = new Thickness(8, 0, 8, 0);
        w.StripIcon.Foreground = Brushes.White;
        w.StripText.Foreground = Brushes.White;
        _barsTimer?.Stop();

        switch (content)
        {
            case ClosedContent.Music when _media?.Current is { } playing:
                ShowArt(w, playing.Artwork, body);
                w.StripBars.Visibility = Visibility.Visible;
                foreach (Border bar in w.StripBars.Children) bar.Background = Brush(playing.ArtworkTint ?? Colors.White);
                _barsTimer?.Start();
                break;
            case ClosedContent.Timer:
                w.StripIcon.Text = _timer.CountsUp ? "" : "";
                w.StripIcon.Foreground = Brush(_timer.IsRunning ? Orange : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
                w.StripIcon.Visibility = Visibility.Visible;
                w.StripText.Text = TimerStripText();
                w.StripText.Foreground = Brush(_timer.IsRunning ? Orange : Colors.White);
                w.StripText.Visibility = Visibility.Visible;
                break;
            case ClosedContent.Notice when _notice is { } notice:
                if (notice.Art is not null) ShowArt(w, notice.Art, body);
                else
                {
                    w.StripIcon.Text = notice.Glyph;
                    w.StripIcon.Foreground = Brush(notice.Tint ?? Colors.White);
                    w.StripIcon.Visibility = Visibility.Visible;
                }
                if (notice.Level is { } level)
                {
                    w.StripLevel.Visibility = Visibility.Visible;
                    w.StripLevelFill.Width = Math.Clamp(level, 0, 1) * w.StripLevel.Width;
                    w.StripText.Text = Math.Round(level * 100).ToString("0");
                    w.StripText.Margin = new Thickness(8 + w.StripLevel.Width + 8, 0, 8, 0);
                    w.StripText.Visibility = Visibility.Visible;
                }
                else if (notice.Text is { } text)
                {
                    w.StripText.Text = text;
                    w.StripText.Foreground = Brush(notice.Tint ?? Colors.White);
                    w.StripText.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    /// <summary>The live equalizer reads only while the bars it moves are on screen.</summary>
    private void SyncEqualizer() =>
        _equalizer?.SetVisible(_shown == ClosedContent.Music && !_hiddenInFullscreen && !_expanded, _media?.Current);

    private static void ShowArt(NotchWindow w, ImageSource? art, double body)
    {
        var side = Math.Max(10, body - 6);
        w.StripArt.Width = side;
        w.StripArt.Height = side;
        w.StripArt.CornerRadius = new CornerRadius(side * 0.28);
        w.StripArtBrush.ImageSource = art;
        w.StripArt.Background = art is null ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) : w.StripArt.Background;
        w.StripArt.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void UpdateWarningTint()
    {
        if (_window is null) return;
        var battery = _power?.Battery ?? default;
        var warning = NotchSupport.BatteryWarningFor(battery.HasBattery ? battery.Percent : null, battery.PluggedIn,
            _settings.GetBool(DefaultsKey.NotchLowBatteryTint), _settings.GetInt(DefaultsKey.NotchLowBatteryThreshold, 10),
            _settings.GetBool(DefaultsKey.NotchLowBatteryEarly), _settings.GetInt(DefaultsKey.NotchLowBatteryEarlyThreshold, 20));
        _window.Shape.Stroke = warning switch { BatteryWarning.Low => Brush(Red), BatteryWarning.Early => Brush(Amber), _ => null };
        _window.Shape.StrokeThickness = warning == BatteryWarning.None ? 0 : 1.5;
    }

    // MARK: Motion (upstream NotchMotion / NotchMotionTimeline, sampled every frame)

    private double CurrentShift(double now) =>
        _shiftTo + (_shiftFrom - _shiftTo) * (1 - NotchMotion.SteadyWidth.Progress(now - _shiftBegin));

    private void MoveTo(SizeD size, double shift, bool animated)
    {
        if (_window is null || _geometry is null) return;
        var now = Now;
        if (!animated || _drawn == SizeD.Zero)
        {
            StopFrames();
            _timeline = null;
            _drawn = _target = size;
            _shiftFrom = _shiftTo = shift;
            _window.ApplyFrame(size, shift, _geometry, _geometry.CameraWidth);
            return;
        }
        if (size == _target && shift == _shiftTo) return;
        _timeline = _timeline is { } t && !t.IsSettled(now)
            ? t.Continued(size, now) ?? new NotchMotionTimeline(_drawn, size, now)
            : new NotchMotionTimeline(_drawn, size, now);
        _shiftFrom = _framesOn ? CurrentShift(now) : _shiftTo;
        _shiftTo = shift;
        _shiftBegin = now;
        _target = size;
        StartFrames();
    }

    private void StartFrames()
    {
        if (_framesOn) return;
        _framesOn = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_framesOn) return;
        _framesOn = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_window is null || _geometry is null || _timeline is null) { StopFrames(); return; }
        var now = Now;
        var size = _timeline.SizeAt(now);
        var shift = CurrentShift(now);
        _drawn = size;
        _window.ApplyFrame(size, shift, _geometry, _geometry.CameraWidth);
        if (_timeline.IsSettled(now) && Math.Abs(shift - _shiftTo) < 0.25)
        {
            _drawn = _target;
            _window.ApplyFrame(_target, _shiftTo, _geometry, _geometry.CameraWidth);
            StopFrames();
        }
    }

    // MARK: Opening and closing

    public void Open(bool takeFocus, bool byHover = false)
    {
        Log.Debug("Island open (focus {Focus}, hover {Hover})", takeFocus, byHover);
        if (!_running || _window is null || _geometry is null) return;
        _notice = null;
        _noticeTimer?.Stop();
        _hoverTimer?.Stop();
        _closeTimer?.Stop();
        if (!_expanded)
        {
            _expanded = true;
            _openedByHover = byHover;
            _hoverEmphasized = false;
            _hoverState.Open();
            ShowPage(_page);
            UpdatePages();
            SyncLyrics();
            _window.ShowOpenLayer(true);
        }
        else if (!byHover) _openedByHover = false;
        if (takeFocus)
        {
            _focusTaken = true;
            _window.TakeFocus();
        }
        Refresh();
    }

    /// <summary>Opens the island without taking focus, or closes it (Islet.exe --toggle-island).</summary>
    public void Toggle()
    {
        if (_expanded) Collapse();
        else Open(takeFocus: false);
    }

    public void Collapse()
    {
        Log.Debug("Island collapse requested (expanded {Expanded})", _expanded);
        if (!_expanded || _window is null || _geometry is null) return;
        _expanded = false;
        _openedByHover = false;
        if (_focusTaken)
        {
            _focusTaken = false;
            _window.SetActivatable(false);
        }
        _hoverState.Close(_inside);
        _window.ShowOpenLayer(false);
        SyncLyrics();
        Refresh();
    }

    private void OnPointer(bool inside)
    {
        _inside = inside;
        _hoverState.Update(inside);
        var opensOnHover = _settings.GetBool(DefaultsKey.NotchOpenOnHover) && _settings.GetBool(DefaultsKey.NotchHoverExpands);
        if (inside)
        {
            _closeTimer?.Stop();
            var emphasize = !_expanded && _notice is null && !opensOnHover && !_hiddenInFullscreen;
            if (emphasize != _hoverEmphasized) { _hoverEmphasized = emphasize; Refresh(); }
            else if (_settings.GetBool(DefaultsKey.NotchHideUntilHover)) Refresh();
            if (!_expanded && !_hoverState.Suppressed && opensOnHover && _hoverTimer is not null)
            {
                _hoverTimer.Interval = TimeSpan.FromSeconds(NotchSupport.SanitizedHoverDelay(_settings.GetDouble(DefaultsKey.NotchHoverDelay)));
                _hoverTimer.Start();
            }
        }
        else
        {
            _hoverTimer?.Stop();
            if (_hoverEmphasized) { _hoverEmphasized = false; Refresh(); }
            else if (_settings.GetBool(DefaultsKey.NotchHideUntilHover)) Refresh();
            if (NotchSupport.ClosesOnPointerExit(_expanded, peeking: false, _openedByHover) && _closeTimer is not null)
            {
                _closeTimer.Interval = TimeSpan.FromSeconds(_expanded
                    ? NotchSupport.SanitizedCloseDelay(_settings.GetDouble(DefaultsKey.NotchCloseDelay)) : 0.12);
                _closeTimer.Start();
            }
        }
    }

    private void OnHoverTimer()
    {
        _hoverTimer?.Stop();
        if (_inside && !_expanded && !_hoverState.Suppressed) Open(takeFocus: false, byHover: true);
    }

    private void OnCloseTimer()
    {
        _closeTimer?.Stop();
        if (_modalOpen) return;
        if (!_inside && NotchSupport.ClosesOnPointerExit(_expanded, false, _openedByHover)) Collapse();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        Log.Debug("Island click on {Source}", e.OriginalSource?.GetType().Name);
        if (e.OriginalSource is DependencyObject source && IsInsideControl(source)) return;
        if (!_expanded)
        {
            Open(takeFocus: true);
            e.Handled = true;
        }
        else if (_openedByHover)
        {
            // A click keeps an island the pointer opened.
            Open(takeFocus: true);
            e.Handled = true;
        }
    }

    private static bool IsInsideControl(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or Slider or Thumb) return true;
        return false;
    }

    // MARK: Gestures (NotchGestureSupport, fed with Windows wheel messages)

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!GesturesEnabled || _geometry is null || _window is null) return;
        if (e.OriginalSource is DependencyObject source && IsInsideControl(source)) return;
        var precise = e.Delta % 120 != 0;
        var inHeader = e.GetPosition(_window.Surface).Y <= _geometry.PageTopInset;
        // Over the lyrics the wheel scrolls them instead of closing the island.
        if (_expanded && !inHeader && e.OriginalSource is DependencyObject node && IsInside(node, _window.LyricsScroller)) return;
        var action = _gesture.Handle(0, -e.Delta / 120.0 * 24, Now, began: false, ended: false, momentum: false, precise,
            hasPhase: false,
            allowVertical: NotchGestureSupport.AllowsVertical(_expanded, inHeader, _page == NotchModule.Music, control: false, scroll: false),
            allowHorizontal: true, expanded: _expanded);
        Perform(action);
        e.Handled = true;
    }

    private void OnHorizontalWheel(int delta)
    {
        if (!GesturesEnabled) return;
        var action = _gesture.Handle(-delta / 120.0 * 24, 0, Now, false, false, false, precise: delta % 120 != 0,
            hasPhase: false, allowVertical: false, allowHorizontal: true, expanded: _expanded);
        Perform(action);
    }

    private bool GesturesEnabled =>
        _settings.IsAvailable(AppFeature.NotchGestures) && _settings.GetBool(DefaultsKey.NotchGesturesEnabled);

    private void Perform(NotchGestureSupport.Action? action)
    {
        switch (action)
        {
            case NotchGestureSupport.Action.Open: Open(takeFocus: false, byHover: true); break;
            case NotchGestureSupport.Action.Close: Collapse(); break;
            case NotchGestureSupport.Action.NextTrack: _media?.Next(); break;
            case NotchGestureSupport.Action.PreviousTrack: _media?.Previous(); break;
        }
    }

    // MARK: Full screen

    private void CheckFullscreen()
    {
        if (!_running || _display is null) return;
        var hide = _settings.GetBool(DefaultsKey.NotchHideInFullscreen) && FullscreenDetector.IsFullscreen(_display);
        if (hide == _hiddenInFullscreen) return;
        _hiddenInFullscreen = hide;
        Log.Debug("Full screen {State}: {Window}", hide ? "entered" : "left", FullscreenDetector.ForegroundDescription());
        if (hide) Collapse();
        SyncDragReveal();
        Refresh(animated: false);
    }

    // MARK: Notices

    private void ShowNotice(Notice notice)
    {
        if (!_running || _expanded || _hiddenInFullscreen || _noticeTimer is null) return;
        _notice = notice;
        _hoverEmphasized = false;
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(notice.Seconds);
        _noticeTimer.Start();
        Refresh();
    }

    private void OnNoticeTimer()
    {
        _noticeTimer?.Stop();
        _notice = null;
        Refresh();
    }

    private bool Starting => Now - _startedAt < 2.5;

    private void OnVolumeChanged(double level, bool muted)
    {
        UpdateVolumeControls();
        if (!_settings.GetBool(DefaultsKey.NotchVolume) || Starting) return;
        var glyph = muted || level <= 0 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        ShowNotice(new Notice(NoticeKind.Volume, glyph, muted ? 0 : level, null, null, 44, 108, 1.6));
    }

    private void OnMicrophoneChanged(bool muted)
    {
        if (!_settings.GetBool(DefaultsKey.NotchMicrophone) || Starting) return;
        ShowNotice(new Notice(NoticeKind.Microphone, muted ? "" : "", null, muted ? "Muted" : "Mic on", null,
                              44, 72, 1.6, muted ? Red : null));
    }

    private void OnBrightnessChanged(int level)
    {
        if (!_settings.GetBool(DefaultsKey.NotchBrightness) || Starting) return;
        ShowNotice(new Notice(NoticeKind.Brightness, "", level / 100.0, null, null, 44, 108, 1.6));
    }

    private void OnBatteryChanged(BatteryState previous, BatteryState current)
    {
        UpdatePages();
        UpdateWarningTint();
        if (!current.HasBattery || !_settings.GetBool(DefaultsKey.NotchBattery) || Starting) return;
        if (previous.PluggedIn != current.PluggedIn)
        {
            ShowNotice(new Notice(NoticeKind.Battery, BatteryGlyph(current), null,
                                  current.PluggedIn ? $"{current.Percent}%  Charging" : $"{current.Percent}%",
                                  null, 44, current.PluggedIn ? 110 : 60, 2.5, current.PluggedIn ? Green : null));
            return;
        }
        var threshold = NotchSupport.SanitizedLowBatteryThreshold(_settings.GetInt(DefaultsKey.NotchLowBatteryThreshold, 10));
        if (!current.PluggedIn && previous.Percent > threshold && current.Percent <= threshold)
            ShowNotice(new Notice(NoticeKind.Battery, BatteryGlyph(current), null, $"{current.Percent}%  Low", null, 44, 96, 4, Red));
    }

    private static string BatteryGlyph(BatteryState battery) =>
        battery.Charging || battery.PluggedIn ? ""
        : battery.Percent >= 95 ? "" : char.ConvertFromUtf32(0xE850 + Math.Clamp(battery.Percent / 10, 0, 9));

    private void OnTrackChanged(NowPlaying playing)
    {
        if (!_settings.GetBool(DefaultsKey.NotchTrackChange) || !_settings.GetBool(DefaultsKey.NotchShowPlayingMusic)
            || !playing.IsPlaying || Starting) return;
        var text = string.IsNullOrEmpty(playing.Artist) ? playing.Title : $"{playing.Title} · {playing.Artist}";
        var trailing = Math.Min(NotchGeometry.NoticeWingRange.Max + 60, 16 + text.Length * 6.5);
        ShowNotice(new Notice(NoticeKind.Track, "", null, text, playing.Artwork, 44, trailing, 3));
    }

    private bool OnVolumeKey(in KeyEvent e)
    {
        // VK_VOLUME_MUTE / DOWN / UP. Swallowed, so Windows shows no flyout; the island does.
        if (e.IsInjected || e.VirtualKey is < 0xAD or > 0xAF) return false;
        if (e.IsDown)
        {
            var key = e.VirtualKey;
            _dispatcher.BeginInvoke(() =>
            {
                if (key == 0xAD) _volume?.ToggleMute();
                else _volume?.Step(key == 0xAF ? 1 : -1);
            });
        }
        return true;
    }

    // MARK: Media

    private void OnMediaChanged()
    {
        _lyrics?.PlaybackChanged(CurrentIdentity());
        UpdatePages();
        SyncLyrics();
        Refresh();
    }

    private void OnBars()
    {
        if (_window is null || _shown != ClosedContent.Music) { _barsTimer?.Stop(); return; }
        var playing = _media?.Current?.IsPlaying == true;
        var max = Math.Max(6, Math.Min(16, (_geometry?.StripBodyHeight ?? 20) - NotchLayout.CompactEdgeGap * 2));
        var t = Now;
        var i = 0;
        var count = _window.StripBars.Children.Count;
        var levels = _equalizer?.Levels;
        foreach (Border bar in _window.StripBars.Children)
        {
            // The player's real levels with the live equalizer, otherwise the usual synthetic motion.
            var value = levels is { Length: > 0 }
                ? Math.Max(0.06, levels[NotchAudioLevelSupport.BarIndex(i, count, levels.Length)])
                : Math.Clamp(0.5 + 0.25 * Math.Sin(t * (5.1 + i * 1.7) + i * 1.3) + 0.25 * Math.Sin(t * (2.3 + i * 0.9) + i * 2.1), 0.08, 1);
            bar.Height = playing ? 3 + (max - 3) * value : 3;
            i++;
        }
    }

    // MARK: Lyrics (upstream NotchLyricsView and the music page's extras)

    private const string OnlineHint = "While lyrics are open, send the song title, artist, album and duration to lrclib.net. " +
        "Imported lyrics stay on this PC. Song titles, artist names and albums may also be checked with musicbrainz.org.";
    private static readonly Brush DimVerse = Brush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    private bool LyricsAvailable => _lyrics?.IsEnabled == true && _media?.Current is not null;
    private bool LyricsShown => _lyricsOpen && LyricsAvailable && _page == NotchModule.Music;

    private NotchMusicIdentity? CurrentIdentity() => _media?.Current is { } playing
        ? new NotchMusicIdentity(playing.Title, playing.Artist, playing.Album, playing.Duration.TotalSeconds, playing.SourceAppId)
        : null;

    private void SetLyricsOpen(bool open)
    {
        _lyricsOpen = open;
        SyncLyrics();
        Refresh();
    }

    /// <summary>Shows or hides the lyrics button and panel, and tells the service what is visible.</summary>
    private void SyncLyrics()
    {
        if (_window is not { } w || _lyrics is null) return;
        w.LyricsButton.Visibility = _page == NotchModule.Music && LyricsAvailable ? Visibility.Visible : Visibility.Collapsed;
        w.LyricsButton.IsChecked = _lyricsOpen;
        w.LyricsPanel.Visibility = LyricsShown ? Visibility.Visible : Visibility.Collapsed;
        if (_geometry is { } g) w.LyricsPanel.Height = Math.Max(0, g.MusicExtrasHeight - 12);
        _lyrics.Update(CurrentIdentity(), LyricsShown && _expanded);
        RenderLyrics();
    }

    private void RenderLyrics()
    {
        if (_window is not { } w || _lyrics is null) return;
        var lyrics = _lyrics.Lyrics;
        var playing = _media?.Current;
        var hasPosition = playing is { Duration.TotalSeconds: > 0 };
        if (lyrics is { Instrumental: false } && (lyrics.Lines.Count > 0 || lyrics.Plain.Length > 0))
        {
            var synced = lyrics.Lines.Count > 0 && hasPosition;
            w.LyricsMessage.Visibility = Visibility.Collapsed;
            w.LyricsScroller.Visibility = Visibility.Visible;
            w.LyricsTiming.Visibility = synced ? Visibility.Visible : Visibility.Collapsed;
            w.LyricsOffset.Content = _lyrics.Offset.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);
            var key = RuntimeHelpers.GetHashCode(lyrics) + (synced ? ":synced" : ":plain");
            if (key != _lyricsKey)
            {
                _lyricsKey = key;
                BuildLyricLines(lyrics, synced, hasPosition);
            }
            UpdateLyricsHighlight();
            ArmLyricsTimer();
            return;
        }
        _lyricsKey = "";
        _lyricLines.Clear();
        _lyricsWaiting = null;
        w.LyricsLines.Children.Clear();
        w.LyricsScroller.Visibility = Visibility.Collapsed;
        w.LyricsTiming.Visibility = Visibility.Collapsed;
        w.LyricsMessage.Visibility = Visibility.Visible;
        _lyricsTimer?.Stop();
        var online = _settings.GetBool(DefaultsKey.NotchLyricsOnline);
        var (icon, message, primary) = lyrics is { Instrumental: true } ? ("", "Instrumental", (string?)null)
            : _lyrics.State switch
            {
                LyricsState.Loading => ("", "Loading lyrics…", null),
                LyricsState.Failed => ("", "Could not load the lyrics." + (online ? "" : "\n" + OnlineHint), online ? "Try again" : "Find lyrics online"),
                LyricsState.Unavailable => ("", "No matching lyrics for this recording.", online ? "Try again" : "Find lyrics online"),
                _ => ("", OnlineHint, online ? null : "Find lyrics online"),
            };
        w.LyricsMessageIcon.Text = icon;
        w.LyricsMessageText.Text = message;
        w.LyricsPrimary.Content = primary;
        w.LyricsPrimary.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Every verse keeps one size, so lines never rewrap as the song moves; the sung one lights up.</summary>
    private void BuildLyricLines(NotchLyrics lyrics, bool synced, bool hasPosition)
    {
        if (_window is not { } w) return;
        _lyricLines.Clear();
        w.LyricsLines.Children.Clear();
        _lyricsActive = -1;
        _lyricsWaiting = null;
        w.LyricsScroller.ScrollToVerticalOffset(0);
        if (!synced)
        {
            if (!hasPosition && lyrics.Lines.Count > 0)
                w.LyricsLines.Children.Add(new TextBlock { Text = "The player does not share its position.", FontSize = 11, Foreground = DimVerse, Margin = new Thickness(0, 0, 0, 8) });
            var text = lyrics.Plain.Length > 0 ? lyrics.Plain : string.Join("\n", lyrics.Lines.Select(l => l.Text));
            w.LyricsLines.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            return;
        }
        _lyricsWaiting = new TextBlock { Text = "Waiting for the first verse", FontSize = 11, Foreground = DimVerse, Margin = new Thickness(0, 0, 0, 12) };
        w.LyricsLines.Children.Add(_lyricsWaiting);
        foreach (var line in lyrics.Lines)
        {
            var block = new TextBlock
            {
                Text = line.Text.Length == 0 ? "♪" : line.Text,
                FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                Foreground = DimVerse, Margin = new Thickness(0, 0, 0, 12),
            };
            _lyricLines.Add(block);
            w.LyricsLines.Children.Add(block);
        }
    }

    private void UpdateLyricsHighlight()
    {
        if (_window is not { } w || _lyrics?.Lyrics is not { } lyrics || _lyricLines.Count != lyrics.Lines.Count || _lyricLines.Count == 0
            || _media?.Current is not { } playing || !LyricsShown) return;
        var active = lyrics.ActiveIndex(playing.CurrentPosition.TotalSeconds, _lyrics.Offset);
        if (active == _lyricsActive) return;
        if (_lyricsActive is int previous && previous >= 0 && previous < _lyricLines.Count) _lyricLines[previous].Foreground = DimVerse;
        _lyricsActive = active;
        if (_lyricsWaiting is not null) _lyricsWaiting.Visibility = active is null ? Visibility.Visible : Visibility.Collapsed;
        if (active is not int index) { ScrollLyricsTo(0); return; }
        _lyricLines[index].Foreground = Brushes.White;
        w.LyricsLines.UpdateLayout();
        var top = _lyricLines[index].TransformToAncestor(w.LyricsLines).Transform(new Point(0, 0)).Y;
        ScrollLyricsTo(Math.Max(0, top - w.LyricsScroller.ViewportHeight / 3));
    }

    /// <summary>The highlight changes only at verse boundaries: one timer for the next one, none while paused.</summary>
    private void ArmLyricsTimer()
    {
        _lyricsTimer?.Stop();
        if (_lyricsTimer is null || !LyricsShown || !_expanded || _lyrics?.Lyrics is not { Lines.Count: > 0 } lyrics
            || _media?.Current is not { IsPlaying: true } playing || playing.Duration.TotalSeconds <= 0) return;
        var position = playing.CurrentPosition.TotalSeconds;
        var rate = playing.PlaybackRate > 0 ? playing.PlaybackRate : 1;
        if (lyrics.NextChange(position, _lyrics.Offset, playing.Duration.TotalSeconds) is not { } next) return;
        _lyricsTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, (next - position) / rate + 0.005));
        _lyricsTimer.Start();
    }

    private void OnLyricsTimer()
    {
        _lyricsTimer?.Stop();
        UpdateLyricsHighlight();
        ArmLyricsTimer();
    }

    private void ScrollLyricsTo(double offset)
    {
        if (_window is null || _scrollTimer is null) return;
        _scrollFrom = _window.LyricsScroller.VerticalOffset;
        _scrollTo = offset;
        _scrollStart = Now;
        _scrollTimer.Start();
    }

    private void OnScrollFrame()
    {
        if (_window is null) { _scrollTimer?.Stop(); return; }
        var t = Math.Min(1, (Now - _scrollStart) / 0.3);
        var eased = 1 - Math.Pow(1 - t, 3);
        _window.LyricsScroller.ScrollToVerticalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (t >= 1) _scrollTimer?.Stop();
    }

    private void OnLyricsPrimary()
    {
        if (!_settings.GetBool(DefaultsKey.NotchLyricsOnline)) _settings.Set(DefaultsKey.NotchLyricsOnline, true);
        else _lyrics?.Retry();
    }

    private void ImportLyrics()
    {
        if (_lyrics is null || _window is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose timed lyrics for the current recording",
            Filter = "Lyrics (*.lrc;*.txt)|*.lrc;*.txt|All files (*.*)|*.*",
        };
        _modalOpen = true;
        try
        {
            if (dialog.ShowDialog(_window) == true) _lyrics.Import(dialog.FileName);
        }
        finally { _modalOpen = false; }
    }

    private static bool IsInside(DependencyObject node, DependencyObject target)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, target)) return true;
        return false;
    }

    // MARK: Files (drops onto the island and drag-reveal)

    private bool FilesAvailable => _shelf is not null && Pages().Contains(NotchModule.Files);

    private bool RevealEnabled => FilesAvailable && _settings.GetBool(DefaultsKey.NotchDragReveal) && !_hiddenInFullscreen;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_files?.DraggingOut == true || _shelf is null || !FilesAvailable || !_shelf.CanAccept(e.Data))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : e.AllowedEffects;
        _revealTimer?.Stop();
        if (!_expanded && !_dragPlaceholder) { _dragPlaceholder = true; Refresh(); }
    }

    /// <summary>An accepted drop adds to the shelf, clears the placeholder and opens Files without focus.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_files?.DraggingOut == true || _shelf is null || !FilesAvailable) return;
        var added = _shelf.AddDrop(e.Data);
        _dragPlaceholder = false;
        _revealTimer?.Stop();
        if (added is not null)
        {
            _page = NotchModule.Files;
            if (_expanded) ShowPage(NotchModule.Files);
            else Open(takeFocus: false, byHover: true);
        }
        Refresh();
    }

    /// <summary>Where a held button counts as bringing something to the notch, in screen pixels.</summary>
    private void UpdateRevealZone(DisplayInfo display, NotchGeometry geometry, double offset)
    {
        var scale = display.Scale;
        var centre = display.Bounds.Left + display.Bounds.Width / 2.0 + offset * scale;
        var halfWidth = (geometry.DropPlaceholder.Width / 2 + 80) * scale;
        _revealZone = new RevealZone((int)(centre - halfWidth), display.Bounds.Top, (int)(centre + halfWidth),
                                     display.Bounds.Top + (int)((geometry.DropPlaceholder.Height + 80) * scale));
        _islandHandle = _window?.Handle ?? IntPtr.Zero;
    }

    /// <summary>The mouse hook runs only while drag-reveal can show something.</summary>
    private void SyncDragReveal()
    {
        var wanted = _running && RevealEnabled;
        if (wanted && _dragHook is null) _dragHook = _input.SubscribeMouse(OnMouseHook);
        else if (!wanted && _dragHook is not null)
        {
            _dragHook.Dispose();
            _dragHook = null;
            if (_dragPlaceholder) { _dragPlaceholder = false; Refresh(); }
        }
    }

    /// <summary>Hook thread: must return at once and never swallows input.</summary>
    private bool OnMouseHook(in MouseEvent e)
    {
        switch (e.Message)
        {
            case 0x0201: // WM_LBUTTONDOWN
                _hookDown = true;
                _hookDownX = e.X;
                _hookDownY = e.Y;
                // A tile dragged out of the island is the island's own drag.
                _hookFromIsland = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = e.X, Y = e.Y }), 2) == _islandHandle;
                break;
            case 0x0202: // WM_LBUTTONUP
                if (!_hookDown) break;
                _hookDown = false;
                if (_hookNear) { _hookNear = false; _dispatcher.BeginInvoke(() => _revealTimer?.Start()); }
                break;
            case 0x0200: // WM_MOUSEMOVE
                if (!_hookDown || _hookFromIsland || _revealZone is not { } zone) break;
                if (Math.Abs(e.X - _hookDownX) < 8 && Math.Abs(e.Y - _hookDownY) < 8) break;
                var near = e.X >= zone.Left && e.X <= zone.Right && e.Y >= zone.Top && e.Y <= zone.Bottom;
                if (near == _hookNear) break;
                _hookNear = near;
                _dispatcher.BeginInvoke(() => OnRevealNear(near));
                break;
        }
        return false;
    }

    private void OnRevealNear(bool near)
    {
        if (near)
        {
            _revealTimer?.Stop();
            if (!_expanded && RevealEnabled && !_dragPlaceholder) { _dragPlaceholder = true; Refresh(); }
        }
        else _revealTimer?.Start();
    }

    /// <summary>Released or moved away without a drop: the placeholder goes.</summary>
    private void EndReveal()
    {
        _revealTimer?.Stop();
        if (!_dragPlaceholder || _hookNear) return;
        _dragPlaceholder = false;
        Refresh();
    }

    // MARK: Timer (upstream NotchTimerService)

    public void StartTimer(int minutes)
    {
        _timer.Cancel();
        _settings.Set(DefaultsKey.NotchTimerMinutes, NotchTimerSupport.TimerMinutes(minutes));
        _settings.Set(DefaultsKey.NotchTimerMode, "timer");
        _timer.Start(NotchTimerMode.Timer, minutes, Now);
        UpdatePages();
        Refresh();
    }

    private NotchPomodoroConfiguration PomodoroConfiguration() => new(
        _settings.GetInt(DefaultsKey.NotchPomodoroFocusMinutes, 25), _settings.GetInt(DefaultsKey.NotchPomodoroShortBreakMinutes, 5),
        _settings.GetInt(DefaultsKey.NotchPomodoroLongBreakMinutes, 15), _settings.GetInt(DefaultsKey.NotchPomodoroLongBreakInterval, 4),
        _settings.GetInt(DefaultsKey.NotchPomodoroTotalSessions, 4));

    private NotchTimerMode SelectedTimerMode => NotchEnum.Parse(_settings.GetString(DefaultsKey.NotchTimerMode), NotchTimerMode.Timer);

    private void OnTimerPrimary()
    {
        var now = Now;
        if (_timer.IsRunning) _timer.Pause(now);
        else if (_timer.IsPaused) _timer.Resume(now);
        else if (_timer.CanStartNext) _timer.StartNext(now);
        else
        {
            _timer.Cancel();
            _timer.Start(SelectedTimerMode, _settings.GetInt(DefaultsKey.NotchTimerMinutes, 15), now, PomodoroConfiguration());
        }
        UpdatePages();
        Refresh();
    }

    private void OnTick()
    {
        if (!_running) return;
        if (_timer.IsRunning && _timer.FinishIfDue(Now)) OnTimerFinished();
        if (_shown == ClosedContent.Timer && _window is not null) _window.StripText.Text = TimerStripText();
        if (_expanded) UpdateLivePages();
    }

    private void OnTimerFinished()
    {
        if (_settings.GetBool(DefaultsKey.NotchTimerSoundEnabled)) SystemSounds.Exclamation.Play();
        var title = _timer.Mode == NotchTimerMode.Pomodoro ? NotchTimerSupport.PhaseTitle(_timer.Phase) + " done" : "Time's up";
        _notice = null;
        ShowNotice(new Notice(NoticeKind.Timer, "", null, title, null, 44, 100, 5, Orange));
        UpdatePages();
    }

    private string TimerStripText() =>
        _timer.Completed ? "Done" : NotchTimerSupport.CompactText(_timer, Now);

    // MARK: Pages

    private void WireControls(NotchWindow w)
    {
        w.CloseButton.Click += (_, _) => Collapse();
        w.SettingsButton.Click += (_, _) => { Collapse(); App.Current.ShowSettings(SettingsPage.Island); };
        w.HomePlayPause.Click += (_, _) => _media?.TogglePlayPause();
        w.HomePrevious.Click += (_, _) => _media?.Previous();
        w.HomeNext.Click += (_, _) => _media?.Next();
        w.MusicPlayPause.Click += (_, _) => _media?.TogglePlayPause();
        w.MusicPrevious.Click += (_, _) => _media?.Previous();
        w.MusicNext.Click += (_, _) => _media?.Next();
        w.HomeMute.Click += (_, _) => _volume?.ToggleMute();
        w.HomeTimerChip.Click += (_, _) => ShowPage(NotchModule.Timer);
        w.HomeVolume.ValueChanged += (_, e) => { if (!_updatingControls && _volume is not null) _volume.Volume = e.NewValue / 100; };
        w.MusicTimeline.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        w.MusicTimeline.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            _media?.Seek(TimeSpan.FromSeconds(w.MusicTimeline.Value));
        };
        w.TimerPrimary.Click += (_, _) => OnTimerPrimary();
        w.LyricsButton.Click += (_, _) => SetLyricsOpen(w.LyricsButton.IsChecked == true);
        w.LyricsPrimary.Click += (_, _) => OnLyricsPrimary();
        w.LyricsImport.Click += (_, _) => ImportLyrics();
        w.LyricsImportIcon.Click += (_, _) => ImportLyrics();
        w.LyricsEarlier.Click += (_, _) => _lyrics?.AdjustOffset(-0.25);
        w.LyricsLater.Click += (_, _) => _lyrics?.AdjustOffset(0.25);
        w.LyricsOffset.Click += (_, _) => _lyrics?.ResetOffset();
        w.TimerNext.Click += (_, _) => { _timer.StartNext(Now); UpdatePages(); Refresh(); };
        w.TimerReset.Click += (_, _) => { _timer.Cancel(); UpdatePages(); Refresh(); };
        w.ModeTimer.Checked += (_, _) => SelectTimerMode(NotchTimerMode.Timer);
        w.ModePomodoro.Checked += (_, _) => SelectTimerMode(NotchTimerMode.Pomodoro);
        w.ModeStopwatch.Checked += (_, _) => SelectTimerMode(NotchTimerMode.Stopwatch);
    }

    private void SelectTimerMode(NotchTimerMode mode)
    {
        if (_updatingControls) return;
        _settings.Set(DefaultsKey.NotchTimerMode, NotchEnum.Raw(mode));
        if (!_timer.IsRunning && !_timer.IsPaused) _timer.Cancel();
        BuildTimerPresets();
        UpdatePages();
        Refresh();
    }

    private IReadOnlyList<NotchModule> Pages()
    {
        bool Installed(AppFeature f) => _settings.IsAvailable(f);
        return NotchSupport.Modules(_settings.GetString(DefaultsKey.NotchModuleOrder), _settings.GetString(DefaultsKey.NotchHiddenModules))
            .Where(m => ImplementedPages.Contains(m) && NotchSupport.IsAvailable(m, Installed)
                        && (m != NotchModule.Timer || _settings.GetBool(DefaultsKey.NotchTimerEnabled))
                        && (m != NotchModule.Files || _settings.GetBool(DefaultsKey.NotchShelf)))
            .ToList();
    }

    private void BuildTabs()
    {
        if (_window is null) return;
        _window.Tabs.Children.Clear();
        foreach (var module in Pages())
        {
            var (glyph, tip) = module switch
            {
                NotchModule.Music => ("", "Music"),
                NotchModule.Files => ("\uE8B7", "Files"),
                NotchModule.Timer => ("", "Timer"),
                _ => ("", "Home"),
            };
            var tab = new RadioButton
            {
                Content = glyph, ToolTip = $"{tip}  ({char.ToUpperInvariant(module.ShortcutKey())})", GroupName = "IslandPages",
                Style = (Style)_window.Resources["TabButton"], Tag = module, IsChecked = module == _page,
            };
            tab.Checked += (_, _) => ShowPage(module);
            _window.Tabs.Children.Add(tab);
        }
    }

    private void ShowPage(NotchModule module)
    {
        if (_window is null) return;
        if (!Pages().Contains(module)) module = NotchModule.Controls;
        _page = module;
        _window.HomePage.Visibility = module == NotchModule.Controls ? Visibility.Visible : Visibility.Collapsed;
        _window.MusicPage.Visibility = module == NotchModule.Music ? Visibility.Visible : Visibility.Collapsed;
        _window.TimerPage.Visibility = module == NotchModule.Timer ? Visibility.Visible : Visibility.Collapsed;
        _window.FilesPage.Visibility = module == NotchModule.Files ? Visibility.Visible : Visibility.Collapsed;
        if (module == NotchModule.Files && _geometry is { } g) _files?.Layout(g.ContentBudget);
        SyncLyrics();
        foreach (RadioButton tab in _window.Tabs.Children) tab.IsChecked = (NotchModule)tab.Tag == module;
        UpdatePages();
        if (_expanded) Refresh();
    }

    private void BuildTimerPresets()
    {
        if (_window is null) return;
        _window.TimerPresets.Children.Clear();
        if (SelectedTimerMode != NotchTimerMode.Timer) return;
        foreach (var minutes in new[] { 1, 5, 10, 15, 25, 45, 60 })
        {
            var chip = new Button { Content = $"{minutes} min", Style = (Style)_window.Resources["ChipButton"] };
            chip.Click += (_, _) => StartTimer(minutes);
            _window.TimerPresets.Children.Add(chip);
        }
    }

    private void UpdateVolumeControls()
    {
        if (_window is null || _volume is null) return;
        _updatingControls = true;
        var level = _volume.Volume;
        _window.HomeVolume.Value = level * 100;
        _window.HomeVolumeText.Text = _volume.Muted ? "Muted" : $"{Math.Round(level * 100)}%";
        _window.HomeMute.Content = _volume.Muted ? "" : "";
        _window.HomeOutput.Text = _volume.OutputName;
        _updatingControls = false;
    }

    private void UpdatePages()
    {
        if (_window is not { } w) return;
        _updatingControls = true;
        var playing = _media?.Current;
        var tint = Brush(playing?.ArtworkTint ?? Colors.White);
        w.Resources["Accent"] = tint;

        w.HomeTitle.Text = playing?.Title ?? "Nothing playing";
        w.HomeArtist.Text = playing?.Artist ?? "Play something in Spotify, a browser or another player.";
        w.HomeArt.ImageSource = playing?.Artwork;
        w.HomePlayPause.Content = playing?.IsPlaying == true ? "" : "";
        w.HomePlayPause.IsEnabled = playing?.CanPlayPause == true;
        w.HomeNext.IsEnabled = playing?.CanGoNext == true;
        w.HomePrevious.IsEnabled = playing?.CanGoPrevious == true;

        w.MusicTitle.Text = playing?.Title ?? "Nothing playing";
        w.MusicArtist.Text = playing?.Artist ?? "";
        w.MusicSource.Text = playing is null ? "" : $"From {playing.SourceName}";
        w.MusicArt.ImageSource = playing?.Artwork;
        w.MusicPlayPause.Content = playing?.IsPlaying == true ? "" : "";
        w.MusicPlayPause.IsEnabled = playing?.CanPlayPause == true;
        w.MusicNext.IsEnabled = playing?.CanGoNext == true;
        w.MusicPrevious.IsEnabled = playing?.CanGoPrevious == true;
        w.MusicTimeline.IsEnabled = playing?.CanSeek == true;

        var battery = _power?.Battery ?? default;
        w.HomeBattery.Text = !battery.HasBattery ? "No battery" : $"{battery.Percent}%{(battery.Charging ? " · charging" : battery.PluggedIn ? " · plugged in" : "")}";
        w.HomeBatteryIcon.Text = BatteryGlyph(battery);

        var mode = _timer.HasSession ? _timer.Mode : SelectedTimerMode;
        w.ModeTimer.IsChecked = mode == NotchTimerMode.Timer;
        w.ModePomodoro.IsChecked = mode == NotchTimerMode.Pomodoro;
        w.ModeStopwatch.IsChecked = mode == NotchTimerMode.Stopwatch;
        var locked = _timer.IsRunning || _timer.IsPaused;
        w.ModeTimer.IsEnabled = w.ModePomodoro.IsEnabled = w.ModeStopwatch.IsEnabled = !locked;
        w.TimerPrimary.Content = _timer.IsRunning ? "Pause" : _timer.IsPaused ? "Resume"
            : _timer.CanStartNext ? $"Start {NotchTimerSupport.PhaseTitle(_timer.NextPhase).ToLowerInvariant()}"
            : _timer.Completed ? "Restart" : "Start";
        w.TimerReset.IsEnabled = _timer.HasSession;
        w.TimerPresets.Visibility = _timer.HasSession || mode != NotchTimerMode.Timer ? Visibility.Collapsed : Visibility.Visible;
        _updatingControls = false;
        UpdateVolumeControls();
        UpdateLivePages();
    }

    /// <summary>Readings that move on their own: the timer's clock and the song's position.</summary>
    private void UpdateLivePages()
    {
        if (_window is not { } w) return;
        var now = Now;
        var mode = _timer.HasSession ? _timer.Mode : SelectedTimerMode;
        if (_timer.HasSession) w.TimerClock.Text = NotchTimerSupport.ClockText(_timer, now);
        else
        {
            var config = PomodoroConfiguration();
            w.TimerClock.Text = mode switch
            {
                NotchTimerMode.Pomodoro => NotchTimerSupport.ClockText(config.FocusMinutes * 60),
                NotchTimerMode.Stopwatch => NotchTimerSupport.StopwatchText(0),
                _ => NotchTimerSupport.ClockText(_settings.GetInt(DefaultsKey.NotchTimerMinutes, 15) * 60),
            };
        }
        w.TimerPhase.Text = _timer.Mode == NotchTimerMode.Pomodoro && _timer.HasSession
            ? $"{NotchTimerSupport.PhaseTitle(_timer.Phase)} · session {_timer.SessionNumber} of {_timer.Configuration.TotalSessions}"
            : mode == NotchTimerMode.Pomodoro ? $"{PomodoroConfiguration().FocusMinutes} min focus, {PomodoroConfiguration().ShortBreakMinutes} min breaks" : "";
        w.HomeTimerText.Text = _timer.HasSession ? (_timer.Completed ? "Done" : NotchTimerSupport.ClockText(_timer, now)) : "Timer";

        UpdateLyricsHighlight();
        if (_media?.Current is { } playing && !_seeking)
        {
            var duration = playing.Duration.TotalSeconds;
            var position = playing.CurrentPosition.TotalSeconds;
            w.MusicTimeline.Maximum = Math.Max(1, duration);
            w.MusicTimeline.Value = Math.Clamp(position, 0, w.MusicTimeline.Maximum);
            w.MusicElapsed.Text = Format(position);
            w.MusicRemaining.Text = duration > 0 ? "-" + Format(Math.Max(0, duration - position)) : "";
        }
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
