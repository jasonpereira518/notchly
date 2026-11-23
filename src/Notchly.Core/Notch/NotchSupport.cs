// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchSupport.swift (enum NotchSupport):
// delays, page order, battery warnings and the close-on-exit rule.
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Notch;

public static class NotchSupport
{
    public const double DefaultHoverDelay = 0.25;
    public const double MinHoverDelay = 0.10, MaxHoverDelay = 1.0;
    /// <summary>Upstream NotchQuickAccessLayout.hoverExitDelay.</summary>
    public const double DefaultCloseDelay = 0.18;
    public const double MinCloseDelay = 0.10, MaxCloseDelay = 2.0;
    public const int DefaultLowBatteryThreshold = 10;
    public const int DefaultEarlyBatteryThreshold = 20;

    public static double SanitizedHoverDelay(double value) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, MinHoverDelay, MaxHoverDelay) : DefaultHoverDelay;

    public static double SanitizedCloseDelay(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinCloseDelay, MaxCloseDelay) : DefaultCloseDelay;

    public static int SanitizedLowBatteryThreshold(int value) => Math.Clamp(value, 1, 99);
    public static int SanitizedEarlyBatteryThreshold(int value) => Math.Clamp(value, 2, 100);

    public static BatteryWarning BatteryWarningFor(int? percent, bool externalConnected, bool tint, int threshold,
                                                   bool early = false, int earlyThreshold = DefaultEarlyBatteryThreshold)
    {
        if (!tint || externalConnected || percent is not { } p) return BatteryWarning.None;
        if (p <= SanitizedLowBatteryThreshold(threshold)) return BatteryWarning.Low;
        if (early && p <= SanitizedEarlyBatteryThreshold(earlyThreshold)) return BatteryWarning.Early;
        return BatteryWarning.None;
    }

    /// <summary>Only an island the pointer opened (or a peek) closes when the pointer leaves.</summary>
    public static bool ClosesOnPointerExit(bool expanded, bool peeking, bool openedByHover) =>
        peeking || (expanded && openedByHover);

    /// <summary>Stored order first, then every other page; hidden pages removed.</summary>
    public static IReadOnlyList<NotchModule> Modules(string order, string hidden)
    {
        var hiddenSet = Split(hidden).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = Split(order).Select(raw => Enum.TryParse<NotchModule>(raw, true, out var m) ? m : (NotchModule?)null)
                                 .OfType<NotchModule>();
        var seen = new HashSet<NotchModule>();
        return stored.Concat(Enum.GetValues<NotchModule>())
                     .Where(m => seen.Add(m) && !hiddenSet.Contains(NotchEnum.Raw(m)))
                     .ToList();
    }

    /// <summary>Whether a page has anything to show, given installed features (NotchModule.isAvailable).</summary>
    public static bool IsAvailable(NotchModule module, Func<AppFeature, bool> installed) => module switch
    {
        NotchModule.Controls or NotchModule.Music => true,
        NotchModule.Timer => installed(AppFeature.NotchTimer),
        NotchModule.Camera => installed(AppFeature.CameraPreview),
        NotchModule.Downloads => installed(AppFeature.NotchDownloads),
        NotchModule.Notifications => installed(AppFeature.NotchNotifications),
        NotchModule.Calendar => installed(AppFeature.NotchCalendar),
        NotchModule.Mixer => installed(AppFeature.Mixer),
        NotchModule.Tools => installed(AppFeature.QuickLauncher),
        NotchModule.Clipboard => installed(AppFeature.ClipboardHistory),
        NotchModule.Captures => installed(AppFeature.Screenshot) || installed(AppFeature.ScreenRecorder)
                                || installed(AppFeature.ScreenOCR) || installed(AppFeature.ColorPicker),
        NotchModule.Files => installed(AppFeature.Shelf),
        NotchModule.Scratchpad => installed(AppFeature.Scratchpad),
        NotchModule.Agents => installed(AppFeature.NotchAgents),
        NotchModule.Watch => installed(AppFeature.NotchWatch),
        NotchModule.System => new[] { AppFeature.MonitorCPU, AppFeature.MonitorGPU, AppFeature.MonitorMemory,
                                      AppFeature.MonitorNetwork, AppFeature.MonitorDisk, AppFeature.MonitorPower,
                                      AppFeature.FanControl, AppFeature.ConnectedDevices }.Any(installed),
        _ => false,
    };

    public static bool IsEnabled(SettingsStore store) =>
        store.IsAvailable(AppFeature.Notch) && store.GetBool(DefaultsKey.NotchEnabled);

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
