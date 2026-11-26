// SPDX-License-Identifier: GPL-3.0-or-later
// Settings store, backup, feature catalog, presets, shortcuts and island page order.
using System.Text.Json.Nodes;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;

namespace Notchly.Core.Tests;

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
    public void AFeatureReachingWindowsStartsInstalledOnceAndThenKeepsTheUsersChoice()
    {
        var store = NewStore();
        // An old preset marked the shelf unavailable while it could not be installed.
        store.Set(FeatureCatalog.Info(AppFeature.Shelf).AvailabilityKey, false);
        Assert.Equal(new[] { AppFeature.Shelf }, store.AdoptNewlyPorted());
        Assert.True(store.IsAvailable(AppFeature.Shelf));
        store.SetAvailable(AppFeature.Shelf, false);
        Assert.Empty(store.AdoptNewlyPorted());
        Assert.False(store.IsAvailable(AppFeature.Shelf));
    }

    [Fact]
    public void TurningOnTheAgentsSwitchInstallsTheAgents()
    {
        var store = NewStore();
        Assert.False(store.IsAvailable(AppFeature.NotchAgents));
        store.Set(DefaultsKey.NotchAgentsEnabled, true);
        Assert.True(store.InstallForSwitch(DefaultsKey.NotchAgentsEnabled));
        Assert.True(store.IsAvailable(AppFeature.NotchAgents));
        Assert.True(store.GetBool(DefaultsKey.NotchAgentsEnabled));
        Assert.False(store.InstallForSwitch(DefaultsKey.NotchAgentsEnabled));
    }

    [Fact]
    public void ASwitchThatIsOffOrNotAnIslandSwitchInstallsNothing()
    {
        var store = NewStore();
        Assert.False(store.InstallForSwitch(DefaultsKey.NotchAgentsEnabled));
        store.Set(DefaultsKey.NotchVolume, true);
        Assert.False(store.InstallForSwitch(DefaultsKey.NotchVolume));
        Assert.False(store.IsAvailable(AppFeature.NotchAgents));
    }

    [Fact]
    public void AgentsSwitchedOnWithoutBeingInstalledAreInstalledOnceAndAnUninstallThenSticks()
    {
        var store = NewStore();
        store.Set(DefaultsKey.NotchAgentsEnabled, true);
        Assert.Equal(new[] { AppFeature.NotchAgents }, store.InstallSwitchedOnExtensions());
        Assert.True(store.IsAvailable(AppFeature.NotchAgents));
        store.SetAvailable(AppFeature.NotchAgents, false);
        Assert.Empty(store.InstallSwitchedOnExtensions());
        Assert.False(store.IsAvailable(AppFeature.NotchAgents));
    }

    [Fact]
    public void AnAgentsSwitchNeverTurnedOnInstallsNothingOnLaunch()
    {
        var store = NewStore();
        Assert.Empty(store.InstallSwitchedOnExtensions());
        Assert.False(store.IsAvailable(AppFeature.NotchAgents));
    }

    [Fact]
    public void TheCompanionSwitchInstallsItsExtensionWhenTurnedOn()
    {
        var store = NewStore();
        store.Set(DefaultsKey.NotchMascotEnabled, true);
        Assert.True(store.InstallForSwitch(DefaultsKey.NotchMascotEnabled));
        Assert.True(store.IsAvailable(AppFeature.NotchMascot));
    }

    [Fact]
    public void ACompanionSwitchLeftOnBeforeItInstalledIsHonouredAfterTheFirstRepair()
    {
        var store = NewStore();
        store.Set(DefaultsKey.IslandSwitchesInstalled, true);
        store.Set(DefaultsKey.NotchMascotEnabled, true);
        store.Set(DefaultsKey.NotchAgentsEnabled, true);
        Assert.Equal(new[] { AppFeature.NotchMascot }, store.InstallSwitchedOnExtensions());
        Assert.False(store.IsAvailable(AppFeature.NotchAgents));
        Assert.Empty(store.InstallSwitchedOnExtensions());
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
        var path = Path.Combine(Path.GetTempPath(), "notchly-test-" + Guid.NewGuid() + ".json");
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

    [Fact]
    public void BackupFromBeforeTheRenameStillImports()
    {
        var json = $$"""{ "isletBackupVersion": 1, "isletBackupAppVersion": "0.1.0", "settings": { "{{DefaultsKey.NotchHoverDelay}}": 0.6 } }""";
        var target = NewStore();
        var result = SettingsBackup.Import(target, json);
        Assert.Equal(1, result.Applied);
        Assert.Equal(0.6, target.GetDouble(DefaultsKey.NotchHoverDelay));
    }

    [Fact]
    public void LegacyFolderIsCopiedOnceAndKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "notchly-migration-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "Islet");
        var current = Path.Combine(root, "Notchly");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "nested"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"a\":1}");
            File.WriteAllText(Path.Combine(legacy, "nested", "x.txt"), "x");

            Assert.True(LegacyMigration.CopyFolderOnce(legacy, current));
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(current, "settings.json")));
            Assert.True(File.Exists(Path.Combine(current, "nested", "x.txt")));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
            Assert.False(Directory.Exists(current + ".migrating"));

            // Later launches leave the new folder alone, even if the old one changes.
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"a\":2}");
            Assert.False(LegacyMigration.CopyFolderOnce(legacy, current));
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(current, "settings.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NoLegacyFolderMeansNothingToCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "notchly-migration-" + Guid.NewGuid().ToString("N"));
        Assert.False(LegacyMigration.CopyFolderOnce(Path.Combine(root, "Islet"), Path.Combine(root, "Notchly")));
        Assert.False(Directory.Exists(Path.Combine(root, "Notchly")));
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
