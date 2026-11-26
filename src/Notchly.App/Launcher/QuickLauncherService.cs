// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/QuickTools/QuickLauncherService.swift: the shortcut that opens a palette of tiles for the tools you reach
// for most. The rules (selection, keys, edit mode, hosted panels) are Core's QuickLauncherModel; this is the Windows host: what each
// tile runs, which tiles exist here, the window, and routing the shortcut to the island's Tools page when the island is on.
using System.Windows.Threading;
using Notchly.App.Island;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core.Camera;
using Notchly.Core.Features;
using Notchly.Core.Launcher;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;

namespace Notchly.App.Launcher;

internal sealed class QuickLauncherService : IFeatureService, IQuickLauncherHost
{
    /// <summary>The tiles that do something on Windows today; the rest of the 17 wait for their features.</summary>
    private static readonly HashSet<QuickLauncherItem> Implemented =
    [
        QuickLauncherItem.KeepAwake, QuickLauncherItem.Toggles, QuickLauncherItem.MicMute, QuickLauncherItem.ScreenOcr, QuickLauncherItem.ColorPicker,
        QuickLauncherItem.Clipboard, QuickLauncherItem.WindowLayout, QuickLauncherItem.Cleaning, QuickLauncherItem.UrlCleaner,
        QuickLauncherItem.CameraPreview, QuickLauncherItem.Scratchpad,
    ];

    private readonly App _app;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _registration;
    private string? _registered;
    private QuickLauncherWindow? _window;

    public QuickLauncherService(App app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
        Model = new QuickLauncherModel(this, app.Settings);
    }

    public App App => _app;
    public QuickLauncherModel Model { get; }
    public bool ShortcutUnavailable { get; private set; }
    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>The window in front when the launcher opened, which tiles that act on "the window you were in" go back to.</summary>
    public IntPtr PreviousWindow { get; private set; }

    public void Sync()
    {
        var wanted = _app.Settings.GetBool(DefaultsKey.QuickLauncherShortcutEnabled) ? _app.Settings.GetString(DefaultsKey.QuickLauncherShortcut) : "";
        if (wanted == _registered) return;
        _registration?.Dispose();
        _registration = null;
        _registered = wanted;
        ShortcutUnavailable = false;
        if (wanted.Length == 0 || Shortcut.Parse(wanted) is not { } shortcut) return;
        _registration = _app.Hotkeys.Register(shortcut, () => _dispatcher.BeginInvoke(Toggle));
        ShortcutUnavailable = _registration is null;
    }

    // MARK: Opening

    /// <summary>The shortcut: the island's Tools page when the island is on and wants it, else the floating panel.</summary>
    public void Toggle()
    {
        if (_app.Settings.GetBool(DefaultsKey.NotchQuickPanel) && _app.Features.Get<IslandService>(AppFeature.Notch) is { } island)
        {
            if (island.IsOpenOn(NotchModule.Tools)) { island.Collapse(); return; }
            Model.PrepareForPresentation();
            if (island.OpenPage(NotchModule.Tools)) return;
        }
        if (IsOpen) { _window!.Close(); return; }
        var front = WindowInventory.Foreground();
        PreviousWindow = front != IntPtr.Zero && ForegroundApps.Describe(front)?.ProcessId != (uint)Environment.ProcessId ? front : IntPtr.Zero;
        Model.PrepareForPresentation();
        _window = new QuickLauncherWindow(this);
        _window.Closed += (_, _) => { _window = null; Model.IsEditing = false; };
        _window.Open();
    }

    // MARK: IQuickLauncherHost

    public void Hide()
    {
        _window?.Close();
        // In the island the Tools page collapses with the launcher.
        if (_app.Features.Get<IslandService>(AppFeature.Notch) is { } island && island.IsOpenOn(NotchModule.Tools)) island.Collapse();
    }

    public void Schedule(double delaySeconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch (Exception e) { Log.Warning(e, "A launcher tile failed"); }
        };
        timer.Start();
    }

    public bool IsAvailable(QuickLauncherItem item) => Implemented.Contains(item) && _app.Settings.IsAvailable(item.Feature());

    public bool Perform(QuickLauncherAction action)
    {
        switch (action)
        {
            case QuickLauncherAction.KeepAwakeToggle: _app.Features.Get<Energy.KeepAwakeService>(AppFeature.KeepAwake)?.Toggle(); return true;
            case QuickLauncherAction.MicMuteToggle: _app.Features.Get<Sound.MicMuteService>(AppFeature.MicMute)?.Toggle(); return true;
            case QuickLauncherAction.ScreenOcrCapture: _app.Features.Get<ScreenTextService>(AppFeature.ScreenOCR)?.Capture(); return true;
            case QuickLauncherAction.ColorPickerPick: _app.Features.Get<ColorPickerService>(AppFeature.ColorPicker)?.Pick(); return true;
            case QuickLauncherAction.CameraShowInIsland:
                // The camera goes into the island when that is enabled: no second window.
                return CameraPreviewSupport.IsEnabled(_app.Settings) && _app.Features.Get<IslandService>(AppFeature.Notch) is { } island && island.OpenPage(NotchModule.Camera);
            case QuickLauncherAction.CameraShow: _app.Features.Get<CameraWindowService>(AppFeature.CameraPreview)?.Toggle(); return true;
            case QuickLauncherAction.ScratchpadShow: _app.Features.Get<ScratchpadService>(AppFeature.Scratchpad)?.Toggle(); return true;
            case QuickLauncherAction.ClipboardShowHistory: _app.ShowClipboardWindow(); return true;
            case QuickLauncherAction.CleaningActivate: _app.Features.Get<CleaningModeService>(AppFeature.CleaningMode)?.Start(); return true;
            default: return false;
        }
    }

    /// <summary>Brings the window the launcher was opened over back to the front.</summary>
    public async Task RestoreFocusAsync()
    {
        if (PreviousWindow == IntPtr.Zero) return;
        WindowInventory.Activate(PreviousWindow);
        await Task.Delay(140);
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _window?.Close();
    }
}
