// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream FeatureCatalog.swift (availability, enableOnFirstInstall, initialEnableKeys)
// and Core/FeaturePresets.swift.
using Islet.Core.Settings;

namespace Islet.Core.Features;

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
        // Windows-only: the island and the extensions it installs with.
        FeaturePreset.Island => InitialInstallGroup(AppFeature.Notch).ToHashSet(),
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
