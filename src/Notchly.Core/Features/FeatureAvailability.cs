// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream FeatureCatalog.swift (availability, enableOnFirstInstall, initialEnableKeys)
// and Core/FeaturePresets.swift.
using Notchly.Core.Settings;

namespace Notchly.Core.Features;

public enum FeaturePreset { Essential, Windows, Battery, Island }

public static class FeatureAvailability
{
    public static bool IsAvailable(this SettingsStore store, AppFeature feature)
    {
        var info = FeatureCatalog.Info(feature);
        return info.CanInstall && store.GetBool(info.AvailabilityKey);
    }

    /// <summary>Any of the feature's own switches is on, or it has none (an on-demand tool).</summary>
    public static bool IsEngaged(this SettingsStore store, AppFeature feature)
    {
        var keys = FeatureCatalog.Info(feature).EnabledKeys;
        return store.IsAvailable(feature) && (keys.Count == 0 || keys.Any(store.GetBool));
    }

    /// <summary>
    /// Installs or uninstalls a feature. Uninstalling never deletes its settings. A first install
    /// turns on its main switch when no choice was ever saved (upstream enableOnFirstInstall).
    /// </summary>
    public static void SetAvailable(this SettingsStore store, AppFeature feature, bool available)
    {
        var info = FeatureCatalog.Info(feature);
        if (available && !info.CanInstall) return;
        if (available) EnableOnFirstInstall(store, info);
        store.Set(info.AvailabilityKey, available);
    }

    /// <summary>The island installs with its initial extensions, as upstream's initialInstallGroup.</summary>
    public static IEnumerable<AppFeature> InitialInstallGroup(AppFeature feature) =>
        feature == AppFeature.Notch
            ? new[] { feature }.Concat(FeatureCatalog.DynamicIslandInitialExtensions)
            : [feature];

    private static IEnumerable<string> InitialEnableKeys(FeatureInfo info) => info.Feature switch
    {
        AppFeature.WindowLayout => [DefaultsKey.WindowLayoutShortcutsEnabled],
        AppFeature.AudioPriority => info.EnabledKeys,
        // The live equalizer starts off: only the user's own switch turns it on.
        AppFeature.NotchLiveEqualizer => [],
        // These two change how the PC behaves without being asked (Bluetooth off at every sleep, pointer acceleration
        // off): they install with their switch off, and only the user's own switch turns them on.
        AppFeature.BluetoothSleep or AppFeature.MouseAcceleration => [],
        _ => info.EnabledKeys.Take(1),
    };

    private static void EnableOnFirstInstall(SettingsStore store, FeatureInfo info)
    {
        IEnumerable<string> choices = info.Feature == AppFeature.WindowLayout
            ? [DefaultsKey.WindowLayoutShortcutsEnabled, DefaultsKey.WindowDirectionalEnabled,
               DefaultsKey.PointerDisplayEnabled, DefaultsKey.WindowEdgeSnapEnabled, DefaultsKey.WindowGestureEnabled]
            : info.EnabledKeys;
        if (choices.Any(store.IsSaved)) return;
        foreach (var key in InitialEnableKeys(info)) store.Set(key, true);
    }

    // MARK: Features reaching Windows

    /// <summary>Ported before the record of ported features existed (M0–M3 lyrics and live equalizer).</summary>
    private static readonly string[] PortedBaseline = ["notch", "notchGestures", "notchTimer", "notchLyrics", "notchLiveEqualizer"];

    /// <summary>
    /// A feature that becomes installable on Windows starts the way upstream installs it by default,
    /// once: a choice saved while it could not be installed (a preset marking it unavailable) was no
    /// real choice. Features already known keep whatever the user chose. Returns those adopted.
    /// </summary>
    public static IReadOnlyList<AppFeature> AdoptNewlyPorted(this SettingsStore store)
    {
        var seen = store.IsSaved(DefaultsKey.PortedFeaturesSeen)
            ? store.GetList(DefaultsKey.PortedFeaturesSeen).ToHashSet()
            : PortedBaseline.ToHashSet();
        var adopted = new List<AppFeature>();
        foreach (var info in FeatureCatalog.All.Where(i => i.CanInstall && !seen.Contains(i.Id)))
        {
            if (info.InstalledByDefault && !store.GetBool(info.AvailabilityKey))
            {
                store.SetAvailable(info.Feature, true);
                adopted.Add(info.Feature);
            }
        }
        store.SetList(DefaultsKey.PortedFeaturesSeen, FeatureCatalog.All.Where(i => i.CanInstall).Select(i => i.Id));
        return adopted;
    }

    // MARK: The island's own switches (Windows-only)

    /// <summary>
    /// The island's Settings page shows each extension's main switch whether or not the extension
    /// is installed. Turning one on there means the person wants it, so it installs what it needs.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AppFeature> IslandSwitches = new Dictionary<string, AppFeature>
    {
        [DefaultsKey.NotchDownloadsEnabled] = AppFeature.NotchDownloads,
        [DefaultsKey.NotchCalendarEnabled] = AppFeature.NotchCalendar,
        [DefaultsKey.NotchNotificationsEnabled] = AppFeature.NotchNotifications,
        [DefaultsKey.NotchAgentsEnabled] = AppFeature.NotchAgents,
    };

    /// <summary>Installs the extension behind an island switch that was just turned on; false when nothing changed.</summary>
    public static bool InstallForSwitch(this SettingsStore store, string key)
    {
        if (!IslandSwitches.TryGetValue(key, out var feature) || !store.GetBool(key) || store.IsAvailable(feature)
            || !FeatureCatalog.Info(feature).CanInstall) return false;
        store.SetAvailable(feature, true);
        return true;
    }

    /// <summary>
    /// Once: switches turned on before they installed their extension (the AI agents, opt-in on
    /// Windows) are honoured. After that, uninstalling an extension keeps it uninstalled.
    /// </summary>
    public static IReadOnlyList<AppFeature> InstallSwitchedOnExtensions(this SettingsStore store)
    {
        if (store.IsSaved(DefaultsKey.IslandSwitchesInstalled)) return [];
        var installed = IslandSwitches.Where(s => store.IsSaved(s.Key) && store.InstallForSwitch(s.Key)).Select(s => s.Value).ToList();
        store.Set(DefaultsKey.IslandSwitchesInstalled, true);
        return installed;
    }

    // MARK: Presets

    public static IReadOnlySet<AppFeature> Features(this FeaturePreset preset) => preset switch
    {
        FeaturePreset.Essential => new HashSet<AppFeature>
        {
            AppFeature.Mixer, AppFeature.KeepAwake, AppFeature.MonitorCPU, AppFeature.MonitorGPU,
            AppFeature.MonitorMemory, AppFeature.MonitorNetwork, AppFeature.MonitorDisk, AppFeature.MonitorPower,
        },
        FeaturePreset.Windows => new HashSet<AppFeature>
        {
            AppFeature.Switcher, AppFeature.WindowLayout, AppFeature.DockPreview, AppFeature.DockClick, AppFeature.WindowMaximizer,
        },
        FeaturePreset.Battery => new HashSet<AppFeature> { AppFeature.MonitorCPU, AppFeature.MonitorMemory, AppFeature.MonitorPower },
        // Windows-only: the island, the extensions it installs with, and the shelf behind its Files page.
        FeaturePreset.Island => InitialInstallGroup(AppFeature.Notch).Append(AppFeature.Shelf).ToHashSet(),
        _ => new HashSet<AppFeature>(),
    };

    public static IReadOnlyList<string> EnableKeys(this FeaturePreset preset) => preset switch
    {
        FeaturePreset.Windows => [DefaultsKey.SwitcherEnabled, DefaultsKey.DockPreviewEnabled,
                                  DefaultsKey.DockClickMinimize, DefaultsKey.WindowMaximizeEnabled],
        FeaturePreset.Island => [DefaultsKey.NotchEnabled],
        _ => [],
    };

    /// <summary>
    /// Installs and engages the preset's features and uninstalls the rest. Nothing is deleted:
    /// every feature keeps its settings and comes back with one click.
    /// </summary>
    public static void Apply(this SettingsStore store, FeaturePreset preset)
    {
        var selected = preset.Features();
        foreach (var info in FeatureCatalog.All)
            store.SetAvailable(info.Feature, selected.Contains(info.Feature));
        foreach (var key in preset.EnableKeys()) store.Set(key, true);
    }
}
