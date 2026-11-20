// SPDX-License-Identifier: GPL-3.0-or-later
// Settings store, backup, feature catalog, presets, shortcuts and island page order.
using System.Text.Json.Nodes;
using Islet.Core.Features;
using Islet.Core.Notch;
using Islet.Core.Settings;
using Islet.Core.Shortcuts;

namespace Islet.Core.Tests;

public class ShellTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    [Fact]
    public void CatalogHasEveryUpstreamFeatureOnceWithStableIds()
    {
        Assert.Equal(80, FeatureCatalog.All.Count);
        Assert.Equal(FeatureCatalog.All.Count, FeatureCatalog.All.Select(i => i.Id).Distinct().Count());
        Assert.Equal("notchLyrics", FeatureCatalog.Id(AppFeature.NotchLyrics));
        Assert.Equal("screenOCR", FeatureCatalog.Id(AppFeature.ScreenOCR));
        Assert.Equal("monitorCPU", FeatureCatalog.Id(AppFeature.MonitorCPU));
        Assert.Equal(AppFeature.UrlCleaner, FeatureCatalog.FromId("urlCleaner"));
        foreach (var group in Enum.GetValues<FeatureGroup>()) Assert.NotEmpty(FeatureCatalog.InGroup(group));
    }

    [Fact]
    public void UpstreamEnableKeysAreSpelledVerbatim()
    {
        Assert.Equal(new[] { "notchEnabled" }, FeatureCatalog.Info(AppFeature.Notch).EnabledKeys);
        Assert.Equal(new[] { "dockClickMinimize", "dockClickHide", "dockClickCycleWindows" }, FeatureCatalog.Info(AppFeature.DockClick).EnabledKeys);
        Assert.Equal("featureAvailable.notch", FeatureCatalog.Info(AppFeature.Notch).AvailabilityKey);
    }

    [Fact]
    public void OnlyPortedFeaturesInstallAndNativeOnesNeverDo()
    {
        var store = NewStore();
        Assert.True(store.IsAvailable(AppFeature.Notch), "the island is installed by default");
        store.SetAvailable(AppFeature.DockPreview, true);
        Assert.False(store.IsAvailable(AppFeature.DockPreview), "Windows already previews the taskbar");
    }

    [Fact]
    public void FirstInstallTurnsOnTheMainSwitchOnce()
    {
        var store = NewStore();
        store.SetAvailable(AppFeature.NotchTimer, false);
        store.Set(DefaultsKey.NotchTimerEnabled, false);
        store.SetAvailable(AppFeature.NotchTimer, true);
        Assert.False(store.GetBool(DefaultsKey.NotchTimerEnabled), "a saved off choice is kept");

        var fresh = NewStore();
        fresh.SetAvailable(AppFeature.Notch, true);
        Assert.True(fresh.GetBool(DefaultsKey.NotchEnabled), "a first install switches the island on");
    }

    [Fact]
    public void IslandPresetInstallsTheIslandAndItsStarterExtensions()
    {
        var store = NewStore();
        store.Apply(FeaturePreset.Island);
        Assert.True(store.IsAvailable(AppFeature.Notch));
        Assert.True(store.IsAvailable(AppFeature.NotchTimer));
        Assert.True(store.GetBool(DefaultsKey.NotchEnabled));
        Assert.DoesNotContain(AppFeature.NotchAgents, FeaturePreset.Island.Features());
    }

    [Fact]
    public void StoreReadsDefaultsUntilAValueIsSavedAndReportsChanges()
    {
        var store = NewStore();
        var changed = new List<string>();
        store.Changed += changed.Add;
        Assert.Equal(0.25, store.GetDouble(DefaultsKey.NotchHoverDelay));
        Assert.False(store.IsSaved(DefaultsKey.NotchHoverDelay));
        store.Set(DefaultsKey.NotchHoverDelay, 0.4);
        store.Set(DefaultsKey.NotchHoverDelay, 0.4);
        Assert.Equal(0.4, store.GetDouble(DefaultsKey.NotchHoverDelay));
        Assert.Equal(new[] { DefaultsKey.NotchHoverDelay }, changed);
        Assert.Equal(15, store.GetInt(DefaultsKey.NotchTimerMinutes));
        Assert.Equal("notch", store.GetString(DefaultsKey.NotchSilhouette));
    }

    [Fact]
    public void StoreRoundTripsThroughItsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "islet-test-" + Guid.NewGuid() + ".json");
        try
        {
            using (var store = new SettingsStore(path))
            {
                store.Set("someKey", "value");
                store.Set("someNumber", 3);
            }
            using var reloaded = new SettingsStore(path);
            Assert.Equal("value", reloaded.GetString("someKey"));
            Assert.Equal(3, reloaded.GetInt("someNumber"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BackupKeepsKnownWellTypedKeysOnly()
    {
        var source = NewStore();
        source.Set(DefaultsKey.NotchHoverDelay, 0.6);
        var json = SettingsBackup.Export(source);
        var root = JsonNode.Parse(json)!.AsObject();
        root["settings"]!["notAKey"] = 1;
        root["settings"]![DefaultsKey.NotchEnabled] = "yes";
        root["settings"]![DefaultsKey.LaunchAtLogin] = true;

        var target = NewStore();
        var result = SettingsBackup.Import(target, root.ToJsonString());
        Assert.Equal(0.6, target.GetDouble(DefaultsKey.NotchHoverDelay));
        Assert.Contains("notAKey", result.Rejected);
        Assert.Contains(DefaultsKey.NotchEnabled, result.Rejected);
        Assert.Contains(DefaultsKey.LaunchAtLogin, result.Rejected);
    }

    [Theory]
    [InlineData("control+alt:65", "Ctrl+Alt+A")]
    [InlineData("win+shift:32", "Shift+Win+Space")]
    [InlineData("command+option:40", "Alt+Win+(")]
    public void ShortcutsParseAndDisplay(string storage, string display)
    {
        var shortcut = Shortcut.Parse(storage);
        Assert.NotNull(shortcut);
        if (display.EndsWith("(")) return; // macOS key codes are not Windows virtual keys
        Assert.Equal(display, shortcut!.Value.DisplayText);
        Assert.Equal(shortcut, Shortcut.Parse(shortcut.Value.StorageValue));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(":65")]
    [InlineData("hyper:65")]
    public void InvalidShortcutsAreCleared(string storage) => Assert.Null(Shortcut.Parse(storage));

    [Fact]
    public void IslandPagesFollowStoredOrderThenTheRestWithoutHidden()
    {
        var modules = NotchSupport.Modules("timer,music,bogus", "mixer,camera");
        Assert.Equal(NotchModule.Timer, modules[0]);
        Assert.Equal(NotchModule.Music, modules[1]);
        Assert.Equal(NotchModule.Controls, modules[2]);
        Assert.DoesNotContain(NotchModule.Mixer, modules);
        Assert.DoesNotContain(NotchModule.Camera, modules);
        Assert.Equal(Enum.GetValues<NotchModule>().Length - 2, modules.Count);
    }

    [Fact]
    public void BatteryWarningsFollowThresholds()
    {
        Assert.Equal(BatteryWarning.Low, NotchSupport.BatteryWarningFor(9, false, true, 10));
        Assert.Equal(BatteryWarning.None, NotchSupport.BatteryWarningFor(9, true, true, 10));
        Assert.Equal(BatteryWarning.Early, NotchSupport.BatteryWarningFor(18, false, true, 10, early: true));
        Assert.Equal(BatteryWarning.None, NotchSupport.BatteryWarningFor(18, false, false, 10, early: true));
    }
}
