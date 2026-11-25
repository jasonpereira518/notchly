// SPDX-License-Identifier: GPL-3.0-or-later
// Startup, the Windows counterpart of upstream main.swift + AppDelegate.applicationDidFinishLaunching.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Notchly.App.Island;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Shell;
using Notchly.Core;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Serilog;
using Wpf.Ui.Appearance;

namespace Notchly.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public SettingsStore Settings { get; private set; } = null!;
    public InputHookService Input { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    /// <summary>When the input features step aside (a full-screen game, an administrator window).</summary>
    public InputGate InputGate { get; private set; } = null!;
    /// <summary>The one handler behind every scroll feature.</summary>
    public MouseInput.ScrollPipeline Scroll { get; private set; } = null!;
    /// <summary>Everything the input features send goes through this, marked as Notchly's own.</summary>
    public Notchly.Core.Input.IInputInjector Injector { get; private set; } = null!;
    /// <summary>Whether the focus is in a password field.</summary>
    public PasswordFieldProbe Passwords { get; private set; } = null!;
    /// <summary>The one clipboard watcher the clipboard features share; it listens only while one of them is on.</summary>
    public Pasteboard.ClipboardMonitor ClipboardWatch { get; private set; } = null!;
    public Monitor.SystemMonitor Monitor { get; private set; } = null!;
    public Monitor.MonitorAlertService MonitorAlerts { get; private set; } = null!;
    public TrayReadoutService TrayReadouts { get; private set; } = null!;
    public FeatureRuntime Features { get; private set; } = null!;
    public TrayIconService Tray { get; private set; } = null!;

    private SingleInstance? _instance;
    private SettingsWindow? _settingsWindow;
    private PanelWindow? _panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(AppIdentity.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppIdentity.LogDirectory, "notchly-.log"), rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 7)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        var background = e.Args.Contains("--background");
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Launching again opens Settings in the running Notchly, as reopening the Mac app does; an import
            // goes to the running Notchly, which owns the settings file.
            var import = Array.IndexOf(e.Args, "--import-settings");
            SingleInstance.SendToRunningInstance(import >= 0 && import + 1 < e.Args.Length ? "import " + Path.GetFullPath(e.Args[import + 1])
                : e.Args.Contains("--toggle-island") ? "island" : background ? "noop" : "settings");
            Shutdown();
            return;
        }
        _instance.RequestReceived += request => Dispatcher.BeginInvoke(() => HandleRequest(request));

        MigrateFromLegacyName();
        Settings = new SettingsStore(AppIdentity.SettingsFile);
        Settings.RegisterDefaults(Defaults.Registered);
        if (HandleCommandLine(e.Args)) { Shutdown(); return; }
        ApplyDotfile();
        ApplyTheme();
        Log.Information("{App} {Version} starting", AppIdentity.Name, AppIdentity.Version);
        Log.Information("Package identity: {Identity}; notification access: {Access}",
            Platform.Notifications.PackageIdentity.FullName ?? "none", Platform.Notifications.NotificationListener.Access);

        Input = new InputHookService();
        Hotkeys = new HotkeyService();
        InputGate = new InputGate(Settings, Dispatcher);
        Injector = new SendInputInjector();
        Passwords = new PasswordFieldProbe();
        Scroll = new MouseInput.ScrollPipeline(this);
        Features = new FeatureRuntime(Settings);
        ClipboardWatch = new Pasteboard.ClipboardMonitor(new Pasteboard.Win32ClipboardAccess(), Dispatcher);
        Features.Bind(AppFeature.Notch, () => new IslandService(Settings, Input, Dispatcher));
        Monitor = new Monitor.SystemMonitor(Settings, Dispatcher);
        TrayReadouts = new TrayReadoutService(this);
        MonitorAlerts = new Monitor.MonitorAlertService(Settings, Monitor, (title, body) => Tray?.Notify(title, body));
        foreach (var feature in new[] { AppFeature.MonitorCPU, AppFeature.MonitorGPU, AppFeature.MonitorMemory, AppFeature.MonitorNetwork,
                                        AppFeature.MonitorDisk, AppFeature.MonitorPower, AppFeature.ConnectedDevices })
            Features.Bind(feature, () => new Monitor.MonitorFeatureService(Monitor, MonitorAlerts, () => TrayReadouts?.Sync()));
        Features.Bind(AppFeature.Mixer, () => new Sound.MixerService(Settings, Dispatcher));
        Features.Bind(AppFeature.SoundOutputSwitcher, () => new Sound.OutputSwitcherService(Settings, Hotkeys));
        Features.Bind(AppFeature.AudioPriority, () => new Sound.AudioPriorityService(Settings, Dispatcher));
        Features.Bind(AppFeature.MicMute, () => new Sound.MicMuteService(Settings, Hotkeys, Dispatcher));
        Features.Bind(AppFeature.KeepAwake, () =>
        {
            var keepAwake = new Energy.KeepAwakeService(Settings, Hotkeys, Dispatcher);
            keepAwake.Ended += reason => Tray?.Notify("Keep awake ended",
                reason == Notchly.Core.Energy.KeepAwakeSupport.EndReason.Battery ? "The battery reached its limit, so Windows may sleep again." : "The session ran out, so Windows may sleep again.");
            return keepAwake;
        });
        Features.Bind(AppFeature.Brightness, () => new Energy.BrightnessService(Settings, Hotkeys, Dispatcher));
        Features.Bind(AppFeature.BluetoothSleep, () => new Energy.BluetoothSleepService(Settings));
        Features.Bind(AppFeature.WindowLayout, () => new Layout.WindowLayoutService(this));
        Features.Bind(AppFeature.Switcher, () => new Switcher.SwitcherService(this));
        foreach (var feature in new[] { AppFeature.ScrollInverter, AppFeature.ScrollHorizontal, AppFeature.ScrollZoom, AppFeature.SmoothScroll, AppFeature.LinearScroll, AppFeature.ScrollSpeed })
            Features.Bind(feature, () => new MouseInput.ScrollFeatureService(this));
        Features.Bind(AppFeature.MouseButtonShortcuts, () => new MouseInput.MouseButtonService(this));
        Features.Bind(AppFeature.MouseClickDebounce, () => new MouseInput.ClickDebounceService(this));
        Features.Bind(AppFeature.KeyboardDebounce, () => new MouseInput.KeyDebounceService(this));
        Features.Bind(AppFeature.SuperKey, () => new KeyboardInput.SuperKeyService(this));
        Features.Bind(AppFeature.TextSnippets, () => new KeyboardInput.SnippetService(this));
        Features.Bind(AppFeature.QuitWindowProtection, () => new KeyboardInput.QuitProtectionService(this));
        Features.Bind(AppFeature.ClipboardHistory, () => new Pasteboard.ClipboardHistoryService(Settings, ClipboardWatch, Dispatcher)
        {
            RegisterHotkey = Hotkeys.Register, ShortcutPressed = ClipboardShortcut,
        });
        Features.Bind(AppFeature.UrlCleaner, () => new Pasteboard.UrlCleanerService(Settings, ClipboardWatch));
        Features.Bind(AppFeature.QuickToggles, () => new Tools.QuickTogglesService(this, Settings, Dispatcher));
        Features.Bind(AppFeature.QuitApps, () => new Tools.QuitAppsService(Settings, Hotkeys, Dispatcher));
        Features.Bind(AppFeature.KillProcess, () => new Tools.KillProcessService(Settings));
        Features.Bind(AppFeature.PortManager, () => new Tools.PortManagerService(Settings, this));
        Features.Bind(AppFeature.FocusFollowsMouse, () => new Tools.FocusFollowsMouseService(Settings));
        Features.Bind(AppFeature.MouseAcceleration, () => new Tools.PointerAccelerationService(Settings));

        var firstRun = !Settings.GetBool(DefaultsKey.HasOnboarded);
        if (firstRun)
        {
            // The port's first run starts with the island, the feature it exists for.
            Settings.Apply(FeaturePreset.Island);
            Settings.Set(DefaultsKey.HasOnboarded, true);
        }

        foreach (var feature in Settings.AdoptNewlyPorted())
            Log.Information("Feature {Feature} is now available on Windows and was installed", FeatureCatalog.Id(feature));
        foreach (var feature in Settings.InstallSwitchedOnExtensions())
            Log.Information("Feature {Feature} was switched on in the island's settings and was installed", FeatureCatalog.Id(feature));
        Settings.Changed += key =>
        {
            Dispatcher.BeginInvoke(Features.SyncAll, DispatcherPriority.Background);
            if (key is DefaultsKey.LaunchAtLogin or DefaultsKey.RunElevatedAtLogin) SyncStartupEntries();
        };
        SyncStartupEntries();
        // A Bluetooth restore owed from before this launch is paid whether or not the feature is still installed.
        Energy.BluetoothSleepService.PayDebt(Settings);
        // A mouse setting left changed by a crash is given back unless its toggle is on and installed (which applies it again).
        if (!Settings.IsAvailable(AppFeature.FocusFollowsMouse) || !Settings.GetBool(DefaultsKey.FocusFollowsMouseEnabled)) Tools.FocusFollowsMouseService.Restore(Settings);
        if (!Settings.IsAvailable(AppFeature.MouseAcceleration) || !Settings.GetBool(DefaultsKey.MouseAccelerationDisabled)) Tools.PointerAccelerationService.Restore(Settings);
        Features.SyncAll();
        Tray = new TrayIconService(this);
        if (e.Args.Contains(NotificationSettings.SetupFlag)) ContinueNotificationSetup();
        else if (firstRun || !background) ShowSettings(firstRun ? SettingsPage.Features : SettingsPage.General);
    }

    /// <summary>
    /// Notchly restarted after setting up (or removing) its notification access, at the person's click in
    /// Settings: the island's settings open again and, now that Notchly has its identity, Windows asks once.
    /// </summary>
    private async void ContinueNotificationSetup()
    {
        Log.Information("Restarted for notifications: package identity {Identity}, access {Access}",
            Platform.Notifications.PackageIdentity.HasIdentity, Platform.Notifications.NotificationListener.Access);
        ShowSettings(SettingsPage.Island);
        if (Platform.Notifications.NotificationListener.Access == Platform.Notifications.NotificationAccess.Unspecified)
            await Platform.Notifications.NotificationListener.RequestAccessAsync();
    }

    private void HandleRequest(string request)
    {
        switch (request)
        {
            case "settings": ShowSettings(SettingsPage.General); break;
            case "island": Features.Get<IslandService>(AppFeature.Notch)?.Toggle(); break;
            case var import when import.StartsWith("import ", StringComparison.Ordinal):
                try
                {
                    var result = SettingsBackup.Import(Settings, File.ReadAllText(import["import ".Length..]));
                    Settings.Flush();
                    Log.Information("Imported {Count} settings", result.Applied);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
                {
                    Log.Warning(ex, "Settings import failed");
                }
                break;
        }
    }

    /// <summary>--export-settings &lt;file&gt; / --import-settings &lt;file&gt;, as upstream's CLI flags.</summary>
    private bool HandleCommandLine(string[] args)
    {
        var index = Array.IndexOf(args, "--export-settings");
        if (index >= 0 && index + 1 < args.Length)
        {
            File.WriteAllText(args[index + 1], SettingsBackup.Export(Settings));
            return true;
        }
        index = Array.IndexOf(args, "--import-settings");
        if (index >= 0 && index + 1 < args.Length)
        {
            SettingsBackup.Import(Settings, File.ReadAllText(args[index + 1]));
            Settings.Flush();
            return true;
        }
        return false;
    }

    /// <summary>Carries settings and the dotfile over from the working name, Islet, on first launch.</summary>
    private static void MigrateFromLegacyName()
    {
        (string From, string To)[] folders =
        [
            (AppIdentity.LegacyDataDirectory, AppIdentity.DataDirectory),
            (AppIdentity.LegacyDotfileDirectory, AppIdentity.DotfileDirectory),
        ];
        foreach (var (from, to) in folders)
        {
            try
            {
                if (LegacyMigration.CopyFolderOnce(from, to)) Log.Information("Copied {From} to {To}", from, to);
            }
            catch (Exception ex) { Log.Warning(ex, "Could not copy {From} to {To}", from, to); }
        }
    }

    private void ApplyDotfile()
    {
        var path = AppIdentity.DotfilePath;
        if (!File.Exists(path)) return;
        try
        {
            var result = SettingsBackup.Import(Settings, File.ReadAllText(path));
            Log.Information("Applied {Count} settings from {Path}", result.Applied, path);
        }
        catch (Exception ex) { Log.Warning(ex, "Ignoring dotfile {Path}", path); }
    }

    public void ApplyTheme()
    {
        var theme = Settings.GetString(DefaultsKey.AppTheme, "system") switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme);
    }

    /// <summary>The Run key and the elevated task agree: one of them starts Notchly at sign-in, never both.</summary>
    private void SyncStartupEntries()
    {
        var launch = Settings.GetBool(DefaultsKey.LaunchAtLogin);
        var elevated = Settings.GetBool(DefaultsKey.RunElevatedAtLogin);
        Task.Run(() =>
        {
            try { Platform.Shell.ElevatedStartup.SyncRunKey(launch, elevated && Platform.Shell.ElevatedStartup.Exists()); }
            catch (Exception ex) { Log.Warning(ex, "Startup entries could not be synced"); }
        });
    }

    /// <summary>Builds the open settings page again (a reorder, say).</summary>
    public void RefreshSettingsPage() => _settingsWindow?.Rebuild();

    private Pasteboard.ClipboardQuickWindow? _clipboardWindow;

    /// <summary>The history in a window over the app you are in; a second call brings the open one forward.</summary>
    public void ShowClipboardWindow()
    {
        if (Features.Get<Pasteboard.ClipboardHistoryService>(AppFeature.ClipboardHistory) is not { } history) return;
        if (_clipboardWindow is { IsVisible: true } open) { open.Activate(); return; }
        var front = Platform.Windowing.WindowInventory.Foreground();
        var target = front != IntPtr.Zero && Platform.Windowing.ForegroundApps.Describe(front)?.ProcessId != (uint)Environment.ProcessId ? front : IntPtr.Zero;
        _clipboardWindow = new Pasteboard.ClipboardQuickWindow(history, Settings, new Pasteboard.ClipboardPaster(Injector), target);
        _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
        _clipboardWindow.Show();
    }

    /// <summary>The history shortcut: the island's Clipboard page when it is there and wanted, else the window.</summary>
    private void ClipboardShortcut()
    {
        var island = Features.Get<IslandService>(AppFeature.Notch);
        if (Settings.GetBool(DefaultsKey.NotchClipboardWindow) && island?.OpenPage(Notchly.Core.Notch.NotchModule.Clipboard) == true) return;
        ShowClipboardWindow();
    }

    public void ShowSettings(SettingsPage page)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Navigate(page);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void TogglePanel()
    {
        _panel ??= new PanelWindow(this);
        _panel.Toggle();
    }

    public void Quit()
    {
        Log.Information("Quitting");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Features?.Dispose();
            ClipboardWatch?.Dispose();
            TrayReadouts?.Dispose();
            MonitorAlerts?.Dispose();
            Monitor?.Dispose();
            Tray?.Dispose();
            Scroll?.Dispose();
            Passwords?.Dispose();
            InputGate?.Dispose();
            Hotkeys?.Dispose();
            Input?.Dispose();
            Settings?.Dispose();
        }
        finally
        {
            _instance?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception");
        e.Handled = true;
    }
}
