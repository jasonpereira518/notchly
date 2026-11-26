// SPDX-License-Identifier: GPL-3.0-or-later
// Port of what upstream Services/CommandBar/CommandBarCatalog.swift builds on every open: the rows the bar can offer right now, with
// availability, live state and what each one does. Rebuilt per open so a feature turned on or off is seen at once. Windows replaces the
// Mac-only rows (Finder tricks, AppleScript, Wi-Fi through CoreWLAN) with what Windows has; rows for features not built yet (screenshot,
// recorder, cleaner, uninstaller) are simply not there.
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using Notchly.App.Platform;
using Notchly.App.Platform.Apps;
using Notchly.App.Platform.Monitor;
using Notchly.App.Platform.Notifications;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Tools;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core.CommandBar;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Notchly.Core.Windowing;

namespace Notchly.App.CommandBar;

internal sealed partial class CommandBarRows(CommandBarService host)
{
    private App App => host.App;
    private SettingsStore Settings => App.Settings;

    private bool Installed(AppFeature feature) => Settings.IsAvailable(feature);
    private T? Service<T>(AppFeature feature) where T : class, IFeatureService => App.Features.Get<T>(feature);

    private CommandBarEntry Action(string id, string title, string subtitle, string glyph, Action<int?> run, string keywords = "") =>
        new(id, id, title, subtitle, CommandBarIcon.Glyph(glyph), run) { Keywords = keywords };

    private CommandBarEntry ForFeature(AppFeature feature, string id, string title, Action<int?> run, string keywords = "")
    {
        var info = FeatureCatalog.Info(feature);
        return Action(id, title, GroupName(info.Group), info.Glyph, run, keywords);
    }

    private static string GroupName(FeatureGroup group) => group switch
    {
        FeatureGroup.Tools => "Tools", FeatureGroup.EnergyDisplay => "Energy", FeatureGroup.Sound => "Sound", FeatureGroup.Monitor => "System",
        FeatureGroup.WindowsDock => "Windows", FeatureGroup.ClipboardFiles => "Clipboard", FeatureGroup.MouseKeyboard => "Input", _ => group.ToString(),
    };

    // MARK: Notchly's own actions

    public IReadOnlyList<CommandBarEntry> Actions()
    {
        var rows = new List<CommandBarEntry>();
        if (Installed(AppFeature.ColorPicker))
            rows.Add(ForFeature(AppFeature.ColorPicker, "action.colorPicker", "Pick color", _ => Service<ColorPickerService>(AppFeature.ColorPicker)?.Pick(), "eyedropper hex rgb"));
        if (Installed(AppFeature.ScreenOCR))
            rows.Add(ForFeature(AppFeature.ScreenOCR, "action.screenOCR", "Copy text from screen", _ => Service<ScreenTextService>(AppFeature.ScreenOCR)?.Capture(), "ocr qr scan recognize"));
        if (Installed(AppFeature.Scratchpad))
            rows.Add(ForFeature(AppFeature.Scratchpad, "action.scratchpad", "Scratchpad", _ => Service<ScratchpadService>(AppFeature.Scratchpad)?.Toggle(), "notes"));
        if (Installed(AppFeature.CameraPreview))
            rows.Add(ForFeature(AppFeature.CameraPreview, "action.cameraPreview", "Camera preview", _ => Service<CameraWindowService>(AppFeature.CameraPreview)?.Toggle(), "mirror webcam"));
        if (Installed(AppFeature.CleaningMode))
            rows.Add(ForFeature(AppFeature.CleaningMode, "action.cleaningMode", "Cleaning mode", _ => Service<CleaningModeService>(AppFeature.CleaningMode)?.Start(), "lock keyboard wipe"));
        if (Installed(AppFeature.Wallpaper))
            rows.Add(ForFeature(AppFeature.Wallpaper, "action.wallpaper", "Wallpaper", _ => Service<WallpaperService>(AppFeature.Wallpaper)?.Open(), "background picture"));
        if (Installed(AppFeature.ClipboardHistory))
            rows.Add(ForFeature(AppFeature.ClipboardHistory, "action.clipboardWindow", "Clipboard history", _ => App.ShowClipboardWindow(), "copied paste"));
        if (Installed(AppFeature.TextSnippets))
            rows.Add(ForFeature(AppFeature.TextSnippets, "action.snippetLibrary", "Snippet library", _ => Service<KeyboardInput.SnippetService>(AppFeature.TextSnippets)?.ShowLibrary(), "text expansion insert"));
        if (Installed(AppFeature.UrlCleaner))
            rows.Add(ForFeature(AppFeature.UrlCleaner, "action.cleanURL", "Clean the copied link", _ => CleanCopiedLink(), "tracking utm"));
        if (Installed(AppFeature.QuitApps))
        {
            rows.Add(ForFeature(AppFeature.QuitApps, "action.quitAllApps", "Quit all apps", _ => Service<QuitAppsService>(AppFeature.QuitApps)?.Show(QuitAppsScope.All), "close everything"));
            rows.Add(ForFeature(AppFeature.QuitApps, "action.quitAllExceptFront", "Quit all except the front app", _ => Service<QuitAppsService>(AppFeature.QuitApps)?.Show(QuitAppsScope.AllExceptFront)));
        }
        if (Installed(AppFeature.KeepAwake) && Service<Energy.KeepAwakeService>(AppFeature.KeepAwake) is { } awake)
        {
            var toggle = ForFeature(AppFeature.KeepAwake, "action.keepAwake", awake.Active ? "Stop keeping awake" : "Keep awake", _ => awake.Toggle(), "caffeine sleep display");
            rows.Add(toggle with { IsActive = awake.Active });
            foreach (var minutes in new[] { 15, 30, 60, 120, 240, 480 })
            {
                var span = minutes < 60 ? $"{minutes} minutes" : minutes == 60 ? "1 hour" : $"{minutes / 60} hours";
                rows.Add(ForFeature(AppFeature.KeepAwake, $"action.keepAwake.{minutes}", CommandBarStrings.Format(CommandBarStrings.KeepAwakeForFormat, span), _ => awake.Activate(minutes), "caffeine"));
            }
        }
        if (Installed(AppFeature.MicMute) && Service<Sound.MicMuteService>(AppFeature.MicMute) is { } mic)
            rows.Add(ForFeature(AppFeature.MicMute, "action.micMute", mic.Active ? "Unmute microphone" : "Mute microphone", _ => mic.Toggle(), "microphone") with { IsActive = mic.Active });
        rows.AddRange(QuickToggles());
        rows.AddRange(Layouts());
        rows.AddRange(Power());
        rows.Add(Action("action.openSettings", "Open Settings", "Notchly", FeatureCatalog.Info(AppFeature.Notch).Glyph, _ => App.ShowSettings(SettingsPage.General), "preferences options"));
        return rows;
    }

    private IEnumerable<CommandBarEntry> QuickToggles()
    {
        if (!Installed(AppFeature.QuickToggles) || Service<QuickTogglesService>(AppFeature.QuickToggles) is not { } service) yield break;
        service.Refresh();
        foreach (var info in QuickTogglesService.All)
        {
            if (!service.IsSupported(info.Id)) continue;
            var on = service.IsOn(info.Id) == true;
            var row = Action("action." + info.Id, info.Title, "Tools", info.Glyph, _ => service.Run(info.Id));
            yield return row with { IsActive = on };
        }
    }

    private IEnumerable<CommandBarEntry> Layouts()
    {
        if (!Installed(AppFeature.WindowLayout) || Service<Layout.WindowLayoutService>(AppFeature.WindowLayout) is not { } service) yield break;
        foreach (var layout in Enum.GetValues<WindowLayoutAction>())
        {
            var title = Spaced(layout.ToString());
            yield return Action("action.layout." + WindowLayoutSupport.RawName(layout), title, "Windows", FeatureCatalog.Info(AppFeature.WindowLayout).Glyph,
                n => _ = RunLayoutAsync(service, layout), "snap move resize " + title.ToLowerInvariant());
        }
    }

    /// <summary>The window the bar was opened over is brought back first: layouts act on the window in front.</summary>
    private async Task RunLayoutAsync(Layout.WindowLayoutService service, WindowLayoutAction action)
    {
        await host.RestoreFocusAsync();
        await service.ApplyAsync(action);
    }

    private static string Spaced(string pascal)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant().Replace("display", "display");
    }

    private IEnumerable<CommandBarEntry> Power()
    {
        var glyph = FeatureCatalog.Info(AppFeature.Notch).Glyph;
        yield return Action("action.power.sleep", CommandBarStrings.PowerSleep, "Power", glyph, _ => SetSuspendState(false, false, false), "suspend");
        yield return Action("action.power.restart", CommandBarStrings.PowerRestart, "Power", glyph, _ => Shutdown("/r /t 0")) with { ConfirmationPrompt = CommandBarStrings.PowerRestartConfirm };
        yield return Action("action.power.shutDown", CommandBarStrings.PowerShutDown, "Power", glyph, _ => Shutdown("/s /t 0")) with { ConfirmationPrompt = CommandBarStrings.PowerShutDownConfirm };
        yield return Action("action.power.logOut", CommandBarStrings.PowerLogOut, "Power", glyph, _ => Shutdown("/l"), "log off") with { ConfirmationPrompt = CommandBarStrings.PowerLogOutConfirm };
    }

    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    private static void Shutdown(string arguments) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });

    private static void CleanCopiedLink()
    {
        var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        if (UrlCleaning.Clean(text) is { } cleaned && cleaned.Url != text.Trim()) ClipboardWrite.TrySetText(cleaned.Url);
    }

    // MARK: Notchly's settings pages and Windows' own

    public IReadOnlyList<CommandBarEntry> SettingsPages()
    {
        var rows = new List<CommandBarEntry>();
        string[] titles = ["General", "Features", "Dynamic Island", "Sound", "System monitor", "Energy", "Windows", "Tools", "Processes", "Mouse", "Keyboard", "Clipboard and files", "About"];
        var pages = Enum.GetValues<SettingsPage>();
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            rows.Add(Action("settings." + page, "Notchly settings: " + titles[i], "Settings", FeatureCatalog.Info(AppFeature.Notch).Glyph, _ => App.ShowSettings(page), "preferences " + titles[i].ToLowerInvariant()));
        }
        return rows;
    }

    public IReadOnlyList<CommandBarEntry> WindowsSettings() =>
        CommandBarSettingsPages.All.Select(page => Action("macsettings." + page.Id, page.Title, "Windows Settings", "\uE713", _ => ShellActions.Open(page.Uri), page.Keywords)).ToList();

    // MARK: Apps, windows, quit

    public IReadOnlyList<CommandBarEntry> Apps() => InstalledApps.List().Select(app =>
        new CommandBarEntry("app." + app.AppUserModelId, "app." + app.AppUserModelId, app.Name, CommandBarStrings.KindApp, CommandBarIcon.App(app.ShellPath),
            _ => { if (!AppLauncher.Open(app.AppUserModelId)) ShellActions.Open(app.ShellPath); })).ToList();

    public IReadOnlyList<CommandBarEntry> Windows()
    {
        var rows = new List<CommandBarEntry>();
        foreach (var window in WindowInventory.List(false))
        {
            if (window.Pid == Environment.ProcessId) continue;
            var title = string.IsNullOrWhiteSpace(window.Title) ? window.AppName : window.Title;
            var handle = window.Window;
            rows.Add(new CommandBarEntry("window." + handle.ToInt64(), "window." + (window.ExePath ?? window.AppName) + "|" + title, title,
                    CommandBarStrings.KindWindow + " · " + window.AppName, CommandBarIcon.App(window.ExePath ?? ""), _ => WindowInventory.Activate(handle))
                { CountsUsage = false, Keywords = window.AppName });
        }
        return rows;
    }

    public IReadOnlyList<CommandBarEntry> QuitApps()
    {
        var rows = new List<CommandBarEntry>();
        foreach (var app in OpenApps.List().Where(a => QuitAppsSupport.ListsApp(a.Candidate)))
        {
            var captured = app;
            rows.Add(new CommandBarEntry("quit." + app.Id, "quit." + app.Id, CommandBarStrings.Format(CommandBarStrings.QuitFormat, app.Name), "Quit an app",
                    CommandBarIcon.App(app.ExePath ?? ""), _ => { if (QuitAppsSupport.CanQuitRow(captured.Id)) OpenApps.Quit(captured); })
                { ConfirmationPrompt = CommandBarStrings.Format(CommandBarStrings.QuitConfirmFormat, app.Name), CountsUsage = false });
        }
        return rows;
    }

    // MARK: Folders and answers about this PC

    public IReadOnlyList<CommandBarEntry> Folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new (string Name, string Path)[]
        {
            ("Downloads", KnownFolders.Downloads), ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)), ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("Home", home),
        };
        return places.Where(p => p.Path.Length > 0 && Directory.Exists(p.Path)).Select(p =>
            new CommandBarEntry("folder." + p.Path, "folder." + p.Path, p.Name, p.Path, CommandBarIcon.File(p.Path), _ => ShellActions.Open(p.Path)) { RevealPath = p.Path }).ToList();
    }

    public IReadOnlyList<CommandBarEntry> Answers()
    {
        var rows = new List<CommandBarEntry>();
        var culture = CultureInfo.CurrentCulture;
        void Answer(string id, string title, string value)
        {
            if (value.Length == 0) return;
            rows.Add(new CommandBarEntry(id, id, title, CommandBarStrings.KindAnswer, CommandBarIcon.Glyph("\uE946"), _ => ClipboardWrite.TrySetText(value)) { AnswerValue = value, CountsUsage = false });
        }
        var now = DateTime.Now;
        Answer("answer.date", CommandBarStrings.AnswerDateLabel, now.ToString("D", culture));
        Answer("answer.time", CommandBarStrings.AnswerTimeLabel, now.ToString("t", culture));
        if (PowerSampler.Read() is { HasBattery: true } power)
            Answer("answer.battery", CommandBarStrings.AnswerBatteryLabel,
                $"{power.Percent}%" + (power.IsCharging ? " · " + CommandBarStrings.AnswerBatteryCharging : power.ExternalConnected ? " · " + CommandBarStrings.AnswerBatteryPlugged : ""));
        if (MemorySampler.Read() is { } memory)
            Answer("answer.memory", CommandBarStrings.AnswerMemoryLabel, CommandBarStrings.Format(CommandBarStrings.AnswerMemoryFormat, Bytes(memory.Used), Bytes(memory.Total)));
        try
        {
            var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            Answer("answer.storage", CommandBarStrings.AnswerStorageLabel, CommandBarStrings.Format(CommandBarStrings.AnswerStorageFormat, Bytes((ulong)system.AvailableFreeSpace), Bytes((ulong)system.TotalSize)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return rows;
    }

    private static string Bytes(ulong value) => value >= 1UL << 30 ? $"{value / (double)(1UL << 30):0.#} GB" : $"{value / (double)(1UL << 20):0} MB";
}
