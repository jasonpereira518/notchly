// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickTogglesService.swift: the rows of the panel's Quick toggles
// and what they do. A run shows as running while it works, a failure shows for a moment and clears, and the states
// that can be read (dark mode, hidden files, desktop icons, the radios, the refresh rate) are read when the panel opens.
using System.Windows;
using System.Windows.Threading;
using Notchly.App.Platform.Tools;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;
using Windows.Devices.Radios;
using static Notchly.Core.Tools.QuickToggleSupport;

namespace Notchly.App.Tools;

/// <summary>A quick toggle's name and symbol.</summary>
public sealed record ToggleInfo(string Id, string Title, string Glyph);

public sealed class QuickTogglesService(App app, SettingsStore settings, Dispatcher dispatcher) : IFeatureService
{
    public static readonly IReadOnlyList<ToggleInfo> All =
    [
        new("darkMode", "Dark mode", ""),
        new("micMute", "Microphone", ""),
        new("emptyTrash", "Recycle Bin", ""),
        new("quitAllApps", "Quit apps", ""),
        new("ejectDisks", "Eject drives", ""),
        new("hiddenFiles", "Hidden files", ""),
        new("desktopIcons", "Desktop icons", ""),
        new("lockScreen", "Lock", ""),
        new("displayOff", "Display off", ""),
        new("screenSaver", "Screen saver", ""),
        new("refreshRate", "Refresh rate", ""),
        new("wifi", "Wi-Fi", ""),
        new("bluetooth", "Bluetooth", ""),
    ];

    private readonly Dictionary<string, RunState> _states = [];
    private readonly Dictionary<string, bool?> _on = [];
    private string _refreshText = "";

    /// <summary>A state or a reading changed. On the UI thread.</summary>
    public event Action? Changed;
    /// <summary>A run needs the panel out of the way first (lock, display off, screen saver, the quit window).</summary>
    public event Action? DismissPanel;

    public void Sync() { }
    public void Dispose() { }

    public RunState StateOf(string id) => _states.GetValueOrDefault(id);
    public bool? IsOn(string id) => _on.GetValueOrDefault(id);
    /// <summary>The refresh-rate row's reading, such as "240 Hz".</summary>
    public string RefreshText => _refreshText;

    /// <summary>Reads what the rows show: cheap registry reads, then the radios in the background.</summary>
    public void Refresh()
    {
        _on["darkMode"] = QuickToggleActions.DarkModeOn();
        _on["hiddenFiles"] = QuickToggleActions.HiddenFilesShown();
        _on["desktopIcons"] = QuickToggleActions.DesktopIconsShown();
        _on["micMute"] = app?.Features.Get<Sound.MicMuteService>(AppFeature.MicMute)?.Active;
        if (DisplayForRate() is { } display && QuickToggleActions.RefreshRates(display.DeviceName) is { } rates) _refreshText = $"{rates.Current} Hz";
        Changed?.Invoke();
        _ = RefreshRadiosAsync();
    }

    private async Task RefreshRadiosAsync()
    {
        var wifi = await QuickToggleActions.RadioOnAsync(RadioKind.WiFi);
        var bluetooth = await QuickToggleActions.RadioOnAsync(RadioKind.Bluetooth);
        _ = dispatcher.BeginInvoke(() => { _on["wifi"] = wifi; _on["bluetooth"] = bluetooth; Changed?.Invoke(); });
    }

    /// <summary>Whether a row is worth showing on this PC: the radios and the rate need hardware that offers them.</summary>
    public bool IsSupported(string id) => id switch
    {
        "wifi" => _on.TryGetValue("wifi", out var wifi) ? wifi is not null : true,
        "bluetooth" => _on.TryGetValue("bluetooth", out var bluetooth) ? bluetooth is not null : true,
        "refreshRate" => DisplayForRate() is { } d && QuickToggleActions.RefreshRates(d.DeviceName) is { Available.Count: > 1 },
        _ => true,
    };

    private static DisplayInfo? DisplayForRate() => Displays.All().FirstOrDefault(d => d.IsBuiltIn) ?? Displays.All().FirstOrDefault(d => d.IsPrimary);

    /// <summary>Runs a row. A failure shows for a moment; a command that needs the panel gone dismisses it first.</summary>
    public async void Run(string id)
    {
        if (StateOf(id) == RunState.Running) return;
        SetState(id, RunState.Running);
        var ok = false;
        try { ok = await Execute(id); }
        catch (Exception e) { Log.Warning(e, "Quick toggle {Id} failed", id); }
        if (ok) SetState(id, RunState.Idle);
        else
        {
            SetState(id, RunState.Failed);
            await Task.Delay(TimeSpan.FromSeconds(FailureDisplaySeconds));
            if (StateOf(id) == RunState.Failed) SetState(id, RunState.Idle);
        }
        Refresh();
    }

    private void SetState(string id, RunState state)
    {
        _states[id] = state;
        Changed?.Invoke();
    }

    private async Task<bool> Execute(string id)
    {
        switch (id)
        {
            case "darkMode": return QuickToggleActions.SetDarkMode(QuickToggleActions.DarkModeOn() != true);
            case "micMute":
                if (app.Features.Get<Sound.MicMuteService>(AppFeature.MicMute) is not { } mic) return false;
                mic.Toggle();
                return true;
            case "emptyTrash":
                DismissPanel?.Invoke();
                if (!OpensConfirmation(true, StateOf(id)) || !Confirm("Empty the Recycle Bin?", "Everything in it will be deleted for good.")) return true;
                return QuickToggleActions.EmptyRecycleBin();
            case "quitAllApps":
                DismissPanel?.Invoke();
                app.Features.Get<QuitAppsService>(AppFeature.QuitApps)?.Show(QuitAppsScope.All);
                return true;
            case "ejectDisks":
                var volumes = QuickToggleActions.EjectableVolumes(settings.GetList(DefaultsKey.DiskEjectExcludedVolumes));
                if (volumes.Count == 0) return false;
                return volumes.Select(v => QuickToggleActions.Eject(v.Root)).Aggregate(true, (a, b) => a && b);
            case "hiddenFiles": return QuickToggleActions.SetHiddenFiles(QuickToggleActions.HiddenFilesShown() != true);
            case "desktopIcons": return QuickToggleActions.SetDesktopIcons(!QuickToggleActions.DesktopIconsShown());
            case "lockScreen": DismissPanel?.Invoke(); await Task.Delay(150); return QuickToggleActions.Lock();
            case "displayOff": DismissPanel?.Invoke(); await Task.Delay(150); return QuickToggleActions.DisplayOff();
            case "screenSaver": DismissPanel?.Invoke(); await Task.Delay(150); return QuickToggleActions.ScreenSaver();
            case "refreshRate":
                if (DisplayForRate() is not { } display || QuickToggleActions.RefreshRates(display.DeviceName) is not { } rates) return false;
                return NextRefreshRate(rates.Current, rates.Available) is { } next && QuickToggleActions.SetRefreshRate(display.DeviceName, next);
            case "wifi": return await ToggleRadio(RadioKind.WiFi, "wifi");
            case "bluetooth": return await ToggleRadio(RadioKind.Bluetooth, "bluetooth");
        }
        return false;
    }

    private async Task<bool> ToggleRadio(RadioKind kind, string id)
    {
        var on = await QuickToggleActions.RadioOnAsync(kind);
        return on is { } now && await QuickToggleActions.SetRadioAsync(kind, !now);
    }

    /// <summary>Return confirms and Esc cancels, as upstream's alert does.</summary>
    private static bool Confirm(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.OK) == MessageBoxResult.OK;
}
