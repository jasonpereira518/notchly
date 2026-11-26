// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotService + ScreenCaptureService: the hotkeys (area, scrolling, full screen, edit last, history, edit the
// clipboard picture), the chooser overlay, the countdown, taking the picture, and what happens after: copy, save with the naming
// rules, history, shelf, the quick preview, the editor. The decisions (routing, naming, timing, placement) are Core's.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Ocr;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core;
using Notchly.Core.Capture;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Screenshots;

internal sealed class ScreenshotService : IFeatureService
{
    private enum Role { Area, Scrolling, FullScreen, LastCapture, History, Clipboard }

    private static readonly (Role Role, string Enabled, string Key)[] Roles =
    [
        (Role.Area, DefaultsKey.ScreenshotShortcutEnabled, DefaultsKey.ScreenshotShortcut),
        (Role.Scrolling, DefaultsKey.ScreenshotScrollingShortcutEnabled, DefaultsKey.ScreenshotScrollingShortcut),
        (Role.FullScreen, DefaultsKey.ScreenshotFullScreenShortcutEnabled, DefaultsKey.ScreenshotFullScreenShortcut),
        (Role.LastCapture, DefaultsKey.ScreenshotLastCaptureShortcutEnabled, DefaultsKey.ScreenshotLastCaptureShortcut),
        (Role.History, DefaultsKey.RecentCapturesShortcutEnabled, DefaultsKey.RecentCapturesShortcut),
        (Role.Clipboard, DefaultsKey.ScreenshotClipboardShortcutEnabled, DefaultsKey.ScreenshotClipboardShortcut),
    ];

    private readonly App _app;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Role, (string Value, IDisposable? Handle)> _registered = [];
    private readonly LastRegionStore _regions = new();
    private readonly CaptureCountdown _countdown;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScreenshotOverlay? _overlay;
    private CountdownHud? _hud;
    private QuickPreviewWindow? _preview;
    private bool _disposed;

    public ScreenshotService(App app)
    {
        _app = app;
        _settings = app.Settings;
        _dispatcher = app.Dispatcher;
        _countdown = new CaptureCountdown(remaining =>
        {
            _hud ??= new CountdownHud();
            _hud.Show(remaining);
        });
        _tick.Tick += (_, _) => { _countdown.Tick(); if (!_countdown.IsActive) { _tick.Stop(); _hud?.Close(); _hud = null; } };
        History = app.Captures;
    }

    public CaptureHistory History { get; }
    internal SettingsStore Settings => _settings;
    /// <summary>Whether a shortcut is wanted but another app owns it (by the settings page's order of roles).</summary>
    public bool ShortcutUnavailable(int role) => _registered.TryGetValue((Role)role, out var r) && r.Value.Length > 0 && r.Handle is null;

    public void Sync()
    {
        foreach (var (role, enabled, key) in Roles)
        {
            var wanted = _settings.GetBool(enabled) ? _settings.GetString(key) : "";
            if (_registered.TryGetValue(role, out var current) && current.Value == wanted) continue;
            current.Handle?.Dispose();
            IDisposable? handle = null;
            if (wanted.Length > 0 && Shortcut.Parse(wanted) is { } shortcut)
            {
                var captured = role;
                handle = _app.Hotkeys.Register(shortcut, () => _dispatcher.BeginInvoke(() => Run(captured)));
            }
            _registered[role] = (wanted, handle);
        }
    }

    private void Run(Role role)
    {
        switch (role)
        {
            case Role.Area: CaptureArea(fromShortcut: true, ScreenCaptureTool.Screenshot); break;
            case Role.Scrolling: CaptureArea(fromShortcut: true, ScreenCaptureTool.Scrolling); break;
            case Role.FullScreen: CaptureFullScreen(); break;
            case Role.LastCapture: EditLast(); break;
            case Role.History: ShowHistory(); break;
            case Role.Clipboard: EditClipboard(); break;
        }
    }

    // MARK: Capturing

    /// <summary>Opens the chooser. A second call while it (or a countdown) is up closes it.</summary>
    public void CaptureArea(bool fromShortcut = false, ScreenCaptureTool tool = ScreenCaptureTool.Screenshot)
    {
        if (_overlay is not null) { _overlay.Cancel(); return; }
        if (_countdown.Cancel()) { _tick.Stop(); _hud?.Close(); _hud = null; return; }
        _preview?.Close();
        var available = ScreenCaptureTools.Available(f => _settings.IsAvailable(f))
            .Where(t => t != ScreenCaptureTool.Recording).ToList();
        if (!available.Contains(tool)) tool = available.FirstOrDefault(ScreenCaptureTool.Screenshot);
        var shows = tool.ShowsCaptureMenu(fromShortcut, key => _settings.IsSaved(key) ? _settings.GetBool(key) : null);
        var delay = ScreenshotSupport.SanitizedDelay(_settings.GetInt(DefaultsKey.ScreenshotDelay));
        var policy = ScreenshotSupport.UnifiedCapturePolicyFor(tool, _settings.GetBool(DefaultsKey.ScreenshotFreeze),
            _settings.GetBool(DefaultsKey.ScreenshotIncludePointer), _settings.GetBool(DefaultsKey.ScreenshotHideOwnWindows));
        var options = new ScreenshotOverlayOptions
        {
            Tools = shows ? available : [tool], Tool = tool,
            Frozen = policy.Freeze && delay == 0,
            HighlightWindows = _settings.GetBool(DefaultsKey.ScreenshotHighlightWindows),
            ShowLastRegion = _settings.GetBool(DefaultsKey.ScreenshotShowLastRegion),
            LoupeStartsOn = _settings.GetBool(DefaultsKey.ScreenshotLoupeStartsOn),
            RememberZoom = _settings.GetBool(DefaultsKey.ScreenshotLoupeRememberZoom),
            DefaultZoom = _settings.GetDouble(DefaultsKey.ScreenshotLoupeDefaultZoom),
            LastZoom = _settings.GetDouble(DefaultsKey.ScreenshotLoupeLastZoom),
            SteppedZoom = _settings.GetBool(DefaultsKey.ScreenshotLoupeSteppedZoomByDefault),
            IncludeOwnWindows = !_settings.GetBool(DefaultsKey.ScreenshotHideOwnWindows),
            Regions = _regions,
        };
        var overlay = new ScreenshotOverlay(options);
        ScreenshotPick? pick = null;
        ScreenCaptureTool? chosen = null;
        overlay.Picked += p => pick = p;
        overlay.ToolChosen += t => chosen = t;
        overlay.ZoomChanged += zoom => { if (options.RememberZoom) _settings.Set(DefaultsKey.ScreenshotLoupeLastZoom, zoom); };
        overlay.Closed += () =>
        {
            _overlay = null;
            if (pick is not null) _dispatcher.BeginInvoke(() => Begin(pick, delay));
            else if (chosen is { } next) _dispatcher.BeginInvoke(() => Hand(next));
        };
        _overlay = overlay;
        try
        {
            if (!overlay.Open()) { _overlay = null; QuickToolHud.Show("The screen could not be read"); }
        }
        catch (Exception e)
        {
            _overlay = null;
            Log.Warning(e, "The screenshot chooser could not open");
        }
    }

    private void Hand(ScreenCaptureTool tool)
    {
        if (tool == ScreenCaptureTool.Text) _app.Features.Get<ScreenTextService>(AppFeature.ScreenOCR)?.Capture();
        else if (tool == ScreenCaptureTool.Color) _app.Features.Get<ColorPickerService>(AppFeature.ColorPicker)?.Pick();
    }

    /// <summary>The whole display the pointer is on, after the countdown.</summary>
    public void CaptureFullScreen()
    {
        if (_overlay is not null) _overlay.Cancel();
        if (_countdown.Cancel()) { _tick.Stop(); _hud?.Close(); _hud = null; return; }
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        if (display is null) return;
        var pick = new ScreenshotPick(display.Bounds, null, null, display.DeviceName, new RectD(0, 0, display.Bounds.Width / display.Scale, display.Bounds.Height / display.Scale), ScreenCaptureTool.Screenshot);
        Begin(pick, ScreenshotSupport.SanitizedDelay(_settings.GetInt(DefaultsKey.ScreenshotDelay)));
    }

    private void Begin(ScreenshotPick pick, int delay)
    {
        if (pick.Tool == ScreenCaptureTool.Scrolling) { StartScrolling(pick, delay); return; }
        if (delay > 0)
        {
            _countdown.Start(delay, () => _ = GrabAsync(pick));
            _tick.Start();
        }
        else _ = GrabAsync(pick);
    }

    /// <summary>Provided by the scrolling capture (commit 11).</summary>
    private void StartScrolling(ScreenshotPick pick, int delay) => QuickToolHud.Show("Scrolling capture is not available yet");

    private async Task GrabAsync(ScreenshotPick pick)
    {
        try
        {
            CapturedImage? image = pick.Image;
            // Live capture: a moment for the overlay (and the countdown) to leave the screen.
            if (image is null || pick.Window is not null) await Task.Delay(160);
            if (pick.Window is { } window && WindowCapture.IsSupported)
                image = await ReadWindowAsync(window) ?? image;
            image ??= ScreenRegion.Read(pick.Area, includeLayered: true);
            if (image is null) { QuickToolHud.Show("The screen could not be read"); return; }
            var display = Displays.AtPoint((pick.Area.Left + pick.Area.Right) / 2, (pick.Area.Top + pick.Area.Bottom) / 2);
            var scale = display?.Scale ?? 1;
            if (_settings.GetBool(DefaultsKey.ScreenshotIncludePointer) && pick.Window is null)
                PointerOverlay.Draw(image.Pixels, image.Width, image.Height, image.Stride, pick.Area);
            if (_settings.GetBool(DefaultsKey.ScreenshotDownscale) && scale > 1.01) { image = image.Scaled(1 / scale); scale = 1; }
            await _dispatcher.InvokeAsync(() => Deliver(image, scale, pick.Area));
        }
        catch (Exception e)
        {
            Log.Warning(e, "The screenshot failed");
            QuickToolHud.Show("The screenshot failed");
        }
    }

    private static async Task<CapturedImage?> ReadWindowAsync(CapturableWindow window)
    {
        using var capture = WindowCapture.Start(window.Handle, borderless: await WindowCapture.AllowBorderlessAsync());
        if (capture is null) return null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await capture.ReadAsync() is { } image) return image;
            await Task.Delay(40);
        }
        return null;
    }

    // MARK: After the capture

    /// <summary>Routes a finished capture by the default action: copy, save, edit, then the quick preview.</summary>
    public void Deliver(CapturedImage image, double scale, Native.RECT area)
    {
        var action = ScreenshotDefaultActions.Current(_settings.GetString(DefaultsKey.ScreenshotDefaultAction));
        var plan = ScreenshotRoutePlan.For(_settings.GetBool(DefaultsKey.ScreenshotCopyToClipboard), action);
        var copied = false;
        string? savedPath = null;
        if (plan.CopyToClipboardFirst || plan.CopiesAsAction) copied = CopyToClipboard(image, scale);
        if (plan.Saves) savedPath = SaveToFolder(image, scale);
        var entry = History.Add(image, scale, area);
        var historyPath = entry is null ? null : History.PathOf(entry);
        var shelfOn = _settings.IsAvailable(AppFeature.Shelf);
        if (plan.OffersShelf && ScreenshotSupport.AddsCapturesToShelf(_settings.GetBool(DefaultsKey.ScreenshotAddToShelf), shelfOn, shelfOn)
            && (savedPath ?? historyPath) is { } shelfFile)
            _app.Shelf.AddFiles([shelfFile]);
        if (plan.OpensEditor) { OpenEditor(image, scale, area); return; }
        var presentation = ScreenshotSupport.QuickPreviewPresentationFor(action, savedPath is not null, copied,
            _settings.GetBool(DefaultsKey.ScreenshotPreviewEnabled), _settings.GetInt(DefaultsKey.ScreenshotPreviewDuration));
        if (presentation.IsShown) ShowPreview(image, scale, area, presentation.DismissInterval, savedPath ?? historyPath, savedPath is not null, copied);
        else if (savedPath is not null) QuickToolHud.Show("Saved " + Path.GetFileName(savedPath));
        else if (copied) QuickToolHud.Show("Screenshot copied");
    }

    private void ShowPreview(CapturedImage image, double scale, Native.RECT area, double? interval, string? file, bool saved, bool copied)
    {
        _preview?.Close();
        var status = saved && file is not null ? "Saved as " + Path.GetFileName(file) : copied ? "Copied to the clipboard" : null;
        var position = QuickPreviewPositions.Current(_settings.GetString(DefaultsKey.ScreenshotPreviewPosition));
        var actions = new QuickPreviewActions(
            Edit: () => OpenEditor(image, scale, area),
            Pin: () => Pin(image, scale, area),
            Copy: () => { if (CopyToClipboard(image, scale)) QuickToolHud.Show("Screenshot copied"); },
            Save: () => { if (SaveToFolder(image, scale) is { } path) QuickToolHud.Show("Saved " + Path.GetFileName(path)); },
            Discard: () => { },
            Share: () => ShareImage(image, scale, file),
            Text: _settings.IsAvailable(AppFeature.ScreenOCR) ? () => _ = CopyTextAsync(image) : null);
        var preview = new QuickPreviewWindow(image, interval, _settings.GetBool(DefaultsKey.ScreenshotPreviewTakesFocus), status, file, actions);
        preview.Closed += (_, _) => { if (_preview == preview) _preview = null; };
        _preview = preview;
        preview.ShowNear(area, position);
    }

    // MARK: Actions

    public bool CopyToClipboard(CapturedImage image, double scale)
    {
        try
        {
            var png = ImageCodec.EncodePng(image, scale);
            return new Pasteboard.Win32ClipboardAccess().WriteImage(png, excludeFromMonitoring: false).Ok;
        }
        catch (Exception e)
        {
            Log.Warning(e, "The screenshot could not be copied");
            return false;
        }
    }

    /// <summary>Saves with the naming rules; the path, or null when the folder could not be written (and the file number is given back).</summary>
    public string? SaveToFolder(CapturedImage image, double scale)
    {
        var store = new SettingsNumberStore(_settings);
        var (bytes, extension) = ImageCodec.EncodeFor(image, "png", scale);
        var destination = ScreenshotNaming.ResolveDestination(
            _settings.GetString(DefaultsKey.ScreenshotSaveFolder), _settings.GetString(DefaultsKey.ScreenshotSaveSubfolder),
            _settings.GetString(DefaultsKey.ScreenshotFileNamePattern), "Screenshot", DateTime.Now, extension, store,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.Exists, CreateDirectory, File.Exists);
        try
        {
            Directory.CreateDirectory(destination.Folder);
            File.WriteAllBytes(destination.FullPath, bytes);
            return destination.FullPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "The screenshot could not be saved");
            if (destination.ConsumedNumber is { } consumed) ScreenshotNaming.RewindNumber(store, consumed);
            QuickToolHud.Show("The screenshot could not be saved");
            return null;
        }
    }

    private static bool CreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Pin(CapturedImage image, double scale, Native.RECT area)
    {
        var pin = new PinWindow(image, scale, i => CopyToClipboard(i, scale), i => SaveToFolder(i, scale));
        pin.ShowAt(area);
    }

    private void ShareImage(CapturedImage image, double scale, string? file)
    {
        file ??= SaveToTemp(image, scale);
        if (file is not null) ShellActions.Share(IntPtr.Zero, [file], null, null);
    }

    private static string? SaveToTemp(CapturedImage image, double scale)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            File.WriteAllBytes(path, ImageCodec.EncodePng(image, scale));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task CopyTextAsync(CapturedImage image)
    {
        try
        {
            var text = ScreenTextSupport.JoinRecognizedText(await TextRecognizer.ReadLinesAsync(image), false);
            await _dispatcher.InvokeAsync(() =>
                QuickToolHud.Show(text.Length > 0 && ClipboardWrite.TrySetText(text) ? "Text copied" : "No text found"));
        }
        catch (Exception e) { Log.Warning(e, "Copy text from a screenshot failed"); }
    }

    // MARK: Editing and history

    public void OpenEditor(CapturedImage image, double scale, Native.RECT? area = null) => ScreenshotEditorWindow.Open(this, image, scale);

    public void EditLast()
    {
        var entry = History.Entries().FirstOrDefault(e => e.Kind == RecentCaptureKind.Screenshot);
        if (entry is null || History.PathOf(entry) is not { } path || ImageCodec.DecodeFile(path) is not { } image)
        {
            QuickToolHud.Show("No recent screenshot");
            return;
        }
        OpenEditor(image, entry.Scale ?? 1);
    }

    public void EditClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) { QuickToolHud.Show("No picture on the clipboard"); return; }
            OpenEditor(ImageCodec.FromBitmap(System.Windows.Clipboard.GetImage()), 1);
        }
        catch (System.Runtime.InteropServices.COMException) { QuickToolHud.Show("The clipboard is busy"); }
    }

    public void ShowHistory() => HistoryWindow.Show(this);

    public void Dispose()
    {
        _disposed = true;
        foreach (var (_, handle) in _registered.Values) handle?.Dispose();
        _registered.Clear();
        _overlay?.Cancel();
        _preview?.Close();
        _hud?.Close();
        _tick.Stop();
    }
}

/// <summary>The file counter of the naming pattern, kept in settings.</summary>
internal sealed class SettingsNumberStore(SettingsStore settings) : IScreenshotNumberStore
{
    public int Next
    {
        get => settings.GetInt(DefaultsKey.ScreenshotFileNumberNext);
        set => settings.Set(DefaultsKey.ScreenshotFileNumberNext, value);
    }
}

/// <summary>The big number of a capture countdown, in the middle of the display the pointer is on; never part of a capture.</summary>
internal sealed class CountdownHud : Window
{
    private readonly System.Windows.Controls.TextBlock _text = new()
    {
        FontSize = 96, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    public CountdownHud()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 180;
        Height = 180;
        Left = -32000;
        Top = -32000;
        Content = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(90), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0)), Child = _text,
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.AddExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
            Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);
            Native.GetCursorPos(out var p);
            var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().First();
            var side = (int)(180 * display.Scale);
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, display.Bounds.Left + (display.Bounds.Width - side) / 2,
                display.Bounds.Top + (display.Bounds.Height - side) / 2, 0, 0, 0x0001 | 0x0010);
        };
    }

    public new void Show(int remaining)
    {
        _text.Text = remaining.ToString();
        if (!IsVisible) base.Show();
    }
}
