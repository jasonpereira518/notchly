// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only checks for how the screenshot settings travel: the keys exist with upstream's defaults, a backup carries the look of a
// backdrop or watermark but never a picture path, and the hotkey roles do not collide with each other.
using Notchly.Core.Capture;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Xunit;

namespace Notchly.Core.Tests;

public class ScreenshotSettingsTests
{
    private static SettingsStore Store()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    [Fact]
    public void TheCaptureKeysHaveUpstreamsDefaults()
    {
        var store = Store();
        Assert.True(store.GetBool(DefaultsKey.ScreenshotFreeze));
        Assert.True(store.GetBool(DefaultsKey.ScreenshotHideOwnWindows));
        Assert.False(store.GetBool(DefaultsKey.ScreenshotIncludePointer));
        Assert.False(store.GetBool(DefaultsKey.ScreenshotShortcutEnabled));
        Assert.Equal(3, store.GetInt(DefaultsKey.ScreenshotPreviewDuration));
        Assert.Equal(19, store.GetInt(DefaultsKey.ScreenshotLastTextSize));
        Assert.Equal("arrow", store.GetString(DefaultsKey.ScreenshotLastTool));
        Assert.Equal("[]", store.GetString(DefaultsKey.ScreenshotBackdropPresets));
    }

    [Fact]
    public void EveryCaptureShortcutIsOffUntilTurnedOn()
    {
        var store = Store();
        string[] enabled =
        [
            DefaultsKey.ScreenshotShortcutEnabled, DefaultsKey.ScreenshotScrollingShortcutEnabled, DefaultsKey.ScreenshotFullScreenShortcutEnabled,
            DefaultsKey.ScreenshotLastCaptureShortcutEnabled, DefaultsKey.RecentCapturesShortcutEnabled, DefaultsKey.ScreenshotClipboardShortcutEnabled,
        ];
        Assert.All(enabled, key => Assert.False(store.GetBool(key)));
    }

    [Fact]
    public void AScreenshotFeatureIsPorted()
    {
        Assert.Equal(WindowsStatus.Ported, FeatureCatalog.Info(AppFeature.Screenshot).Status);
        Assert.True(FeatureCatalog.Info(AppFeature.Screenshot).CanInstall);
    }

    [Fact]
    public void ABackupKeepsAWatermarkLookButNotItsPicture()
    {
        var store = Store();
        store.Set(DefaultsKey.ScreenshotWatermarkStyle, new WatermarkStyle { Kind = WatermarkKind.Image, ImagePath = @"C:\Private\logo.png", Opacity = 0.6 }.Encoded());
        var json = SettingsBackup.Export(store);
        Assert.DoesNotContain("logo.png", json);
        Assert.Contains("watermark", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABackupDropsAPictureBackdrop()
    {
        var store = Store();
        store.Set(DefaultsKey.ScreenshotBackdropStyle, new BackdropStyle { Kind = BackdropKind.Image, ImagePath = @"C:\Private\sky.jpg" }.Encoded());
        Assert.DoesNotContain("sky.jpg", SettingsBackup.Export(store));
    }

    [Fact]
    public void AnImportedPictureBackdropKeepsThisPcsOwnChoice()
    {
        var local = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean" }.Encoded();
        var restored = new BackdropStyle { Kind = BackdropKind.Image, ImagePath = "elsewhere.png" }.Encoded();
        Assert.Equal(local, BackdropPortability.RestoredStyle(restored, local));
        Assert.Equal(BackdropKind.Preset, BackdropStyle.Decoded(BackdropPortability.PortableStyle(local)).Kind);
    }

    [Fact]
    public void PictureBackdropPresetsAreLeftOutOfABackupAndKeptOnRestore()
    {
        var pictures = ScreenshotPresets.EncodeBackdrops([new BackdropStyle { Kind = BackdropKind.Image, ImagePath = "a.png" }, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[0.1, 0.2, 0.3]] }]);
        var portable = ScreenshotPresets.DecodeBackdrops(BackdropPortability.PortablePresets(pictures));
        Assert.Single(portable);
        Assert.Equal(BackdropKind.Solid, portable[0].Kind);
        var restored = ScreenshotPresets.DecodeBackdrops(BackdropPortability.RestoredPresets(BackdropPortability.PortablePresets(pictures), pictures));
        Assert.Contains(restored, style => style.Kind == BackdropKind.Image);
        Assert.Contains(restored, style => style.Kind == BackdropKind.Solid);
    }

    [Fact]
    public void ThisMachinesFolderAndRegionNeverTravel()
    {
        var store = Store();
        store.Set(DefaultsKey.ScreenshotSaveFolder, @"D:\Shots");
        store.Set(DefaultsKey.ScreenshotLastRegion, "x");
        var json = SettingsBackup.Export(store);
        Assert.DoesNotContain("D:\\\\Shots", json);
        Assert.DoesNotContain(DefaultsKey.ScreenshotLastRegion, json);
    }
}
