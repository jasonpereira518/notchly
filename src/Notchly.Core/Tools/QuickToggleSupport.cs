// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickTogglesSupport.swift (the toggles half): the rows, their
// order and visibility, which feature gates each, when a drive is offered for ejecting, the Empty Recycle Bin
// confirmation and how a run's state clears. Windows differences: no keyboard light (Windows has no API), a
// hidden-files, desktop-icon and theme read from their registry values, and three added rows (refresh rate, Wi-Fi,
// Bluetooth) that the roadmap asks for.
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Tools;

public static class QuickToggleSupport
{
    /// <summary>The ids are upstream's raw values; the added Windows rows come last.</summary>
    public static readonly string[] DefaultOrder =
    [
        "darkMode", "micMute", "emptyTrash", "quitAllApps", "ejectDisks", "hiddenFiles", "desktopIcons", "lockScreen",
        "displayOff", "screenSaver", "refreshRate", "wifi", "bluetooth",
    ];

    public static string VisibilityKey(string id) => id switch
    {
        "darkMode" => DefaultsKey.PanelToggleDarkMode,
        // Upstream's legacy storage name, kept so a backup moves across.
        "micMute" => DefaultsKey.PanelUtilityMicMute,
        "emptyTrash" => DefaultsKey.PanelToggleEmptyTrash,
        "quitAllApps" => DefaultsKey.PanelToggleQuitAllApps,
        "ejectDisks" => DefaultsKey.PanelToggleEjectDisks,
        "hiddenFiles" => DefaultsKey.PanelToggleHiddenFiles,
        "desktopIcons" => DefaultsKey.PanelToggleDesktopIcons,
        "lockScreen" => DefaultsKey.PanelToggleLockScreen,
        "displayOff" => DefaultsKey.PanelToggleDisplayOff,
        "screenSaver" => DefaultsKey.PanelToggleScreenSaver,
        "refreshRate" => DefaultsKey.PanelToggleRefreshRate,
        "wifi" => DefaultsKey.PanelToggleWifi,
        "bluetooth" => DefaultsKey.PanelToggleBluetooth,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>The feature that has to be installed for a row: the microphone and quit rows follow their own features.</summary>
    public static AppFeature GatedBy(string id) => id switch
    {
        "micMute" => AppFeature.MicMute,
        "quitAllApps" => AppFeature.QuitApps,
        _ => AppFeature.QuickToggles,
    };

    /// <summary>
    /// The saved order: known ids once each in the saved order, then any the saved order lacks in the default order.
    /// An empty or unreadable value is the default order.
    /// </summary>
    public static IReadOnlyList<string> SanitizedOrder(string? stored)
    {
        var saved = (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(DefaultOrder.Contains).Distinct().ToList();
        return saved.Concat(DefaultOrder.Where(id => !saved.Contains(id))).ToList();
    }

    /// <summary>The rows to show: the saved order, each one switched on and with its feature installed.</summary>
    public static IReadOnlyList<string> VisibleRows(SettingsStore settings) =>
        SanitizedOrder(settings.GetString(DefaultsKey.PanelToggleOrder))
            .Where(id => settings.GetBool(VisibilityKey(id)) && settings.IsAvailable(GatedBy(id))).ToList();

    // MARK: Run state

    public enum RunState { Idle, Running, Failed, NeedsPermission }

    /// <summary>A failure shows for this long and then clears; a missing permission stays until fixed.</summary>
    public const double FailureDisplaySeconds = 2.4;

    public static bool AutoClears(RunState state) => state == RunState.Failed;

    // MARK: Windows settings as flags

    /// <summary>Explorer's Hidden value: 1 shows hidden files, 2 hides them.</summary>
    public static bool HiddenFilesShown(int? hidden) => hidden == 1;

    /// <summary>The desktop's HideIcons value: 1 hides the icons; anything else, including no value, shows them.</summary>
    public static bool DesktopIconsShown(int? hideIcons) => hideIcons != 1;

    /// <summary>AppsUseLightTheme is 0 when apps are dark.</summary>
    public static bool DarkModeOn(int? appsUseLightTheme) => appsUseLightTheme == 0;

    // MARK: Ejecting

    /// <summary>What Windows says about a drive.</summary>
    public readonly record struct DriveFacts(bool IsLocal, bool IsSystem, bool IsExternalBus, bool IsRemovable, bool IsEjectable);

    /// <summary>
    /// A drive is offered for ejecting when it is local, not the one Windows runs from, and external, removable or
    /// ejectable: a USB disk is offered even though Windows calls it fixed media.
    /// </summary>
    public static bool ShouldOfferEject(DriveFacts drive) =>
        drive.IsLocal && !drive.IsSystem && (drive.IsExternalBus || drive.IsRemovable || drive.IsEjectable);

    /// <summary>An exclusion matches by name, volume id or path, ignoring case; the path may carry a trailing slash.</summary>
    public static bool IsExcluded(IEnumerable<string> exclusions, string name, string? volumeId, string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return exclusions.Select(e => e.Trim()).Where(e => e.Length > 0).Any(e =>
            string.Equals(e, name, StringComparison.OrdinalIgnoreCase)
            || volumeId is not null && string.Equals(e, volumeId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    // MARK: Refresh rate

    /// <summary>The two rates the toggle moves between: the highest the display offers and 60 Hz.</summary>
    public static int? NextRefreshRate(int current, IReadOnlyCollection<int> available)
    {
        if (available.Count == 0) return null;
        var highest = available.Max();
        var lowest = available.Contains(60) ? 60 : available.Min();
        if (highest == lowest) return null;
        return current >= highest ? lowest : highest;
    }

    // MARK: Empty Recycle Bin confirmation

    public enum ConfirmChoice { Confirm, Cancel, None }

    /// <summary>Return confirms and Esc cancels; nothing else does anything.</summary>
    public static ConfirmChoice ChoiceForKey(string key) => key switch
    {
        "\r" => ConfirmChoice.Confirm,
        "\u001b" => ConfirmChoice.Cancel,
        _ => ConfirmChoice.None,
    };

    /// <summary>A confirmation opens only for an action that is available and not already running.</summary>
    public static bool OpensConfirmation(bool available, RunState state) => available && state != RunState.Running;
}
