// SPDX-License-Identifier: GPL-3.0-or-later
// Startup, the Windows counterpart of upstream main.swift + AppDelegate.applicationDidFinishLaunching.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Islet.App.Island;
using Islet.App.Platform.Input;
using Islet.App.Platform.Shell;
using Islet.App.Shell;
using Islet.Core;
using Islet.Core.Features;
using Islet.Core.Settings;
using Serilog;
using Wpf.Ui.Appearance;

namespace Islet.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public SettingsStore Settings { get; private set; } = null!;
    public InputHookService Input { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
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
            .WriteTo.File(Path.Combine(AppIdentity.LogDirectory, "islet-.log"), rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 7)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        var background = e.Args.Contains("--background");
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Launching again opens Settings in the running Islet, as reopening the Mac app does.
            SingleInstance.SendToRunningInstance(e.Args.Contains("--toggle-island") ? "island" : background ? "noop" : "settings");
            Shutdown();
            return;
        }
        _instance.RequestReceived += request => Dispatcher.BeginInvoke(() => HandleRequest(request));

        Settings = new SettingsStore(AppIdentity.SettingsFile);
        Settings.RegisterDefaults(Defaults.Registered);
        if (HandleCommandLine(e.Args)) { Shutdown(); return; }
        ApplyDotfile();
        ApplyTheme();
        Log.Information("{App} {Version} starting", AppIdentity.Name, AppIdentity.Version);

        Input = new InputHookService();
        Hotkeys = new HotkeyService();
        Features = new FeatureRuntime(Settings);
        Features.Bind(AppFeature.Notch, () => new IslandService(Settings, Input, Dispatcher));

        var firstRun = !Settings.GetBool(DefaultsKey.HasOnboarded);
        if (firstRun)
        {
            // The port's first run starts with the island, the feature it exists for.
            Settings.Apply(FeaturePreset.Island);
            Settings.Set(DefaultsKey.HasOnboarded, true);
        }

        Settings.Changed += _ => Dispatcher.BeginInvoke(Features.SyncAll, DispatcherPriority.Background);
        Features.SyncAll();
        Tray = new TrayIconService(this);
        if (firstRun || !background) ShowSettings(firstRun ? SettingsPage.Features : SettingsPage.General);
    }

    private void HandleRequest(string request)
    {
        switch (request)
        {
            case "settings": ShowSettings(SettingsPage.General); break;
            case "island": Features.Get<IslandService>(AppFeature.Notch)?.Toggle(); break;
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
            Tray?.Dispose();
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
