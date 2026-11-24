// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/Notch/NotchWatchService.swift: it keeps the chosen area, reads it
// every second or two, feeds the closed island and the page, and says when the rule is met. Windows-specific:
// windows are followed by their HWND through Windows.Graphics.Capture, a desktop area is a plain screen copy,
// text comes from Windows.Media.Ocr, and a minimized or cloaked window or a locked session counts as hidden.
using System.Globalization;
using System.Media;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Ocr;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Island.Watch;

/// <summary>
/// The area a watch reads: a part of one window, in pixels from its frame's top-left corner, followed wherever the
/// window goes, even covered; or, with <see cref="Window"/> zero, a fixed part of the display in screen pixels.
/// </summary>
public sealed record WatchTarget(IntPtr Window, RectD Crop, string AppName, string? WindowTitle)
{
    public bool IsDisplayArea => Window == IntPtr.Zero;

    /// <summary>The app and its window's title, as the page's header shows them.</summary>
    public string Title => WindowTitle is { Length: > 0 } title && title != AppName ? $"{AppName} · {title}" : AppName;
}

public enum WatchState
{
    Idle,
    Watching,
    /// <summary>The window is there but cannot be read now, as when it is minimized or the session is locked.
    /// Watching continues when it can.</summary>
    Hidden,
    Finished,
}

public sealed class WatchService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly Func<bool> _enabled;
    private readonly NotchWatchChoices _choices = new();
    private NotchWatchTracker _tracker = new(NotchWatchCondition.Changes);
    private int _generation;
    private bool _looping, _locked, _disposed;
    private WindowCapture? _capture;
    private IntPtr _captured;
    private AreaPicker? _picker;
    private byte[]? _fingerprint;
    private string? _signature;
    private DateTime? _readAt;

    /// <param name="enabled">Whether Watch is on; by default its settings say (NotchWatchSupport.IsEnabled).</param>
    public WatchService(Dispatcher dispatcher, SettingsStore settings, Func<bool>? enabled = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _enabled = enabled ?? (() => NotchWatchSupport.IsEnabled(settings));
        Condition = NotchWatchSupport.SavedCondition(settings);
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Anything the page or the closed island shows changed.</summary>
    public event Action? Changed;
    /// <summary>The rule was met, or the window closed.</summary>
    public event Action<WatchTarget, NotchWatchOutcome>? Finished;
    /// <summary>An area was chosen and is now watched (upstream opens the island on the Watch page).</summary>
    public event Action? Chosen;

    public WatchTarget? Target { get; private set; }
    public WatchState State { get; private set; }
    public NotchWatchOutcome? Outcome { get; private set; }
    /// <summary>The area as last read, for the page and an area without text in the closed island.</summary>
    public BitmapSource? Preview { get; private set; }
    public string Text { get; private set; } = "";
    public string Headline { get; private set; } = "";
    /// <summary>The line chosen to show in the island; null picks one automatically.</summary>
    public int? HeadlineLine { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public NotchWatchCondition Condition { get; private set; }
    public string MatchText { get; private set; } = "";
    public double? MatchNumber { get; private set; }

    /// <summary>The page is on screen: read more often so the preview feels live.</summary>
    public bool PageVisible
    {
        get => _pageVisible;
        set
        {
            if (_pageVisible == value) return;
            _pageVisible = value;
            if (value && IsActive) RestartLoop();
        }
    }
    private bool _pageVisible;

    public bool IsActive => Target is not null && State is WatchState.Watching or WatchState.Hidden;
    /// <summary>An area without text shows itself in the closed island instead.</summary>
    public bool ShowsThumbnail => Headline.Length == 0 && Preview is not null;
    public bool IsSelecting => _picker is not null;

    public bool CanWatchAgain => Target is { } target && State == WatchState.Finished
                                 && (target.IsDisplayArea || IsWindow(target.Window));

    /// <summary>Turned off, the watch stops; still on, a watch whose loop ended starts reading again.</summary>
    public void Sync()
    {
        if (!_enabled()) { Stop(); return; }
        if (IsActive && !_looping) RestartLoop();
    }

    // MARK: Choosing

    /// <summary>Opens the area picker on <paramref name="display"/> (upstream chooseArea).</summary>
    public void ChooseArea(DisplayInfo display)
    {
        if (!_enabled() || _picker is not null) return;
        var choice = _choices.Begin();
        var picker = new AreaPicker(display);
        _picker = picker;
        picker.Completed += area =>
        {
            if (!_choices.Accepts(choice, _enabled())) return;
            _picker = null;
            if (area is { } chosen) Watch(chosen);
        };
        picker.Open();
    }

    /// <summary>The window under the middle of the area becomes the target, or the area itself with only the desktop
    /// under it (upstream watch(_:choice:)).</summary>
    private void Watch(RECT area)
    {
        var rect = new RectD(area.Left, area.Top, area.Width, area.Height);
        var windows = PickableWindows();
        var picked = NotchWatchSupport.WindowCrop(rect, windows.Select(w => (w.Handle, w.Bounds)));
        WatchTarget target;
        if (picked is { } hit && !IsDesktop(hit.Id))
        {
            var app = ForegroundApps.Describe(hit.Id);
            var title = WindowText(hit.Id);
            target = new WatchTarget(hit.Id, hit.Crop, app?.Name is { Length: > 0 } name ? name : "Window", title.Length > 0 ? title : null);
        }
        else target = new WatchTarget(IntPtr.Zero, rect, "Watch", null);
        Log.Information("Watch: watching {Kind} ({Width}×{Height} px)", target.IsDisplayArea ? "part of the display" : "a window",
                        (int)target.Crop.Width, (int)target.Crop.Height);
        // Another area has other lines: its reading starts automatic again.
        HeadlineLine = null;
        Begin(target);
        Chosen?.Invoke();
    }

    /// <summary>Top-level windows a person can see, front to back, without Notchly's own.</summary>
    private static List<(IntPtr Handle, RectD Bounds)> PickableWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<(IntPtr, RectD)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) return true;
            // Click-through overlays are not what the person pointed at.
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0) return true;
            if (FrameBounds(hwnd) is not { Width: > 0, Height: > 0 } frame) return true;
            list.Add((hwnd, new RectD(frame.Left, frame.Top, frame.Width, frame.Height)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsDesktop(IntPtr hwnd) => ClassName(hwnd) is "Progman" or "WorkerW";

    /// <summary>The window's visible frame in screen pixels, without its invisible resize borders.</summary>
    private static RECT? FrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            return frame;
        return GetWindowRect(hwnd, out frame) ? frame : null;
    }

    // MARK: Watching

    /// <summary>Starts watching <paramref name="target"/> from what it shows now.</summary>
    public void Begin(WatchTarget target)
    {
        if (!_enabled()) return;
        // Another area must not show the last one's picture while it is first read.
        if (target != Target) Preview = null;
        if (target.Window != _captured) DropCapture();
        Target = target;
        State = WatchState.Watching;
        Outcome = null;
        StartedAt = DateTime.Now;
        _fingerprint = null;
        _signature = null;
        _readAt = null;
        Text = "";
        Headline = "";
        _tracker = MakeTracker();
        RestartLoop();
        Changed?.Invoke();
    }

    public void WatchAgain()
    {
        if (Target is not { } target || !_enabled()) return;
        if (!target.IsDisplayArea && !IsWindow(target.Window)) return;
        Begin(target);
    }

    public void Stop()
    {
        _choices.Cancel();
        var picker = _picker;
        _picker = null;
        picker?.Cancel();
        CancelLoop();
        DropCapture();
        var had = Target is not null || State != WatchState.Idle;
        Target = null;
        State = WatchState.Idle;
        Outcome = null;
        Preview = null;
        Text = "";
        Headline = "";
        StartedAt = null;
        HeadlineLine = null;
        _fingerprint = null;
        _signature = null;
        _readAt = null;
        if (had) Changed?.Invoke();
    }

    public void SetHeadlineLine(int? line)
    {
        if (line == HeadlineLine) return;
        HeadlineLine = line;
        Headline = NotchWatchSupport.Headline(Text, line);
        Changed?.Invoke();
    }

    public void SetCondition(NotchWatchCondition condition)
    {
        if (condition == Condition) return;
        Condition = condition;
        _settings.Set(DefaultsKey.NotchWatchCondition, NotchWatchSupport.Raw(condition));
        ResetTracker();
        Changed?.Invoke();
    }

    /// <summary>Typed words and numbers apply once confirmed, so a half-typed "1" on the way to 100 cannot end the
    /// watch early.</summary>
    public void SetMatchText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed == MatchText) return;
        MatchText = trimmed;
        if (Condition == NotchWatchCondition.Contains) ResetTracker();
        Changed?.Invoke();
    }

    /// <summary>Read as Windows' region writes numbers, so 1.000 is a thousand where a comma starts the decimals.</summary>
    public void SetMatchNumber(string text)
    {
        var value = NotchWatchSupport.TypedNumber(text, CultureInfo.CurrentCulture);
        if (value == MatchNumber) return;
        MatchNumber = value;
        if (Condition == NotchWatchCondition.Reaches) ResetTracker();
        Changed?.Invoke();
    }

    /// <summary>A new rule starts from what the area shows now.</summary>
    private void ResetTracker()
    {
        _tracker = MakeTracker();
        if (!IsActive || State != WatchState.Watching || _signature is null) return;
        if (_tracker.Observe(_signature, Text, DateTime.Now) is { } outcome) Finish(outcome);
    }

    private NotchWatchTracker MakeTracker() =>
        NotchWatchTracker.For(Condition, MatchText, MatchNumber, CultureInfo.CurrentCulture);

    private void RestartLoop()
    {
        CancelLoop();
        var generation = _generation;
        _looping = true;
        _ = Loop(generation);
    }

    private async Task Loop(int generation)
    {
        try
        {
            while (generation == _generation && IsActive && !_disposed)
            {
                await Read(generation);
                if (generation != _generation || !IsActive) return;
                var interval = PageVisible ? NotchWatchSupport.VisibleInterval : NotchWatchSupport.BackgroundInterval;
                await Task.Delay(TimeSpan.FromSeconds(interval));
            }
        }
        catch (Exception e) { Log.Warning(e, "Watch: reading stopped"); }
        finally { if (generation == _generation) _looping = false; }
    }

    private void CancelLoop()
    {
        _generation++;
        _looping = false;
    }

    private void DropCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _captured = IntPtr.Zero;
    }

    /// <summary>One reading of the area (upstream read()).</summary>
    private async Task Read(int generation)
    {
        if (Target is not { } target) return;
        CapturedImage? image;
        if (!target.IsDisplayArea)
        {
            if (!IsWindow(target.Window) || _capture?.Closed == true) { Finish(new NotchWatchOutcome.Closed()); return; }
            if (_locked || IsIconic(target.Window) || IsCloaked(target.Window)) { SetState(WatchState.Hidden); return; }
            if (_capture is null)
            {
                var borderless = await WindowCapture.AllowBorderlessAsync();
                if (generation != _generation || !IsActive) return;
                _capture = WindowCapture.Start(target.Window, borderless);
                _captured = target.Window;
                if (_capture is null) { SetState(WatchState.Hidden); return; }
            }
            var capture = _capture;
            var whole = await Task.Run(capture.ReadAsync);
            if (generation != _generation || !IsActive) return;
            // No frame yet: the capture has only just begun.
            if (whole is null) return;
            image = FrameBounds(target.Window) is { Width: > 0, Height: > 0 } frame
                    && NotchWatchSupport.PixelCrop(target.Crop, new SizeD(frame.Width, frame.Height), new SizeD(whole.Width, whole.Height)) is { } crop
                ? whole.Crop(crop) : null;
        }
        else
        {
            if (_locked) { SetState(WatchState.Hidden); return; }
            var area = target.Crop;
            var rect = new RECT { Left = (int)area.X, Top = (int)area.Y, Right = (int)(area.X + area.Width), Bottom = (int)(area.Y + area.Height) };
            image = await Task.Run(() => ScreenRegion.Read(rect));
            if (generation != _generation || !IsActive) return;
        }
        if (image is null) { SetState(WatchState.Hidden); return; }
        SetState(WatchState.Watching);
        var picture = NotchWatchSupport.Fingerprint(image.Pixels.AsSpan(image.Offset), image.Width, image.Height, image.Stride);
        var now = DateTime.Now;
        if (_signature is not null && NotchWatchSupport.SameFingerprint(picture, _fingerprint)
            && _readAt is { } readAt && (now - readAt).TotalSeconds < NotchWatchSupport.RereadInterval)
        {
            // The area looks as it did: no need to read it again, but it confirms a change and lets time settle it.
            if (_tracker.Observe(_signature, Text, now) is { } still) Finish(still);
            return;
        }
        var recognized = await Task.Run(() => TextRecognizer.ReadAsync(image));
        // Kept only once read: a reading dropped halfway must not leave the area marked as read.
        if (generation != _generation || !IsActive) return;
        _fingerprint = picture;
        _readAt = DateTime.Now;
        Preview = PreviewImage(image);
        Text = recognized;
        Headline = NotchWatchSupport.Headline(recognized, HeadlineLine);
        _signature = NotchWatchSupport.Signature(recognized, picture);
        Changed?.Invoke();
        if (_tracker.Observe(_signature, recognized, DateTime.Now) is { } outcome) Finish(outcome);
    }

    private void SetState(WatchState state)
    {
        if (State == state || !IsActive) return;
        State = state;
        Changed?.Invoke();
    }

    private static BitmapSource PreviewImage(CapturedImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null,
                                         image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void Finish(NotchWatchOutcome outcome)
    {
        if (Target is not { } target || !IsActive) return;
        CancelLoop();
        if (outcome is NotchWatchOutcome.Closed) DropCapture();
        State = WatchState.Finished;
        Outcome = outcome;
        Log.Information("Watch: {Outcome}", outcome.Title);
        if (_settings.GetBool(DefaultsKey.NotchWatchSound)) SystemSounds.Asterisk.Play();
        Changed?.Invoke();
        Finished?.Invoke(target, outcome);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            _locked = true;
            SetState(WatchState.Hidden);
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _locked = false;
    });

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
