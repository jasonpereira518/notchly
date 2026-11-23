// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the camera contracts in upstream Tests (NotchLayout.cameraPreviewSize and NotchCameraSupport.canPresent),
// plus the Windows defaults: the page starts on and the chosen camera stays on this PC.
using Notchly.Core.Camera;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class CameraTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    [Fact]
    public void PreviewFillsAsMuchOfThePageAsItsShapeAllows()
    {
        var wide = NotchLayout.CameraPreviewSize(560, 264);
        Assert.Equal(264 * 16 / 9.0, wide.Width, 6);
        Assert.Equal(469.3, wide.Width, 1);
        Assert.Equal(264, wide.Height);

        var fourThree = NotchLayout.CameraPreviewSize(400, 300);
        Assert.Equal(400, fourThree.Width);
        Assert.Equal(300, fourThree.Height);

        var tall = NotchLayout.CameraPreviewSize(200, 400);
        Assert.Equal(200, tall.Width);
        Assert.Equal(150, tall.Height);

        Assert.Equal(SizeD.Zero, NotchLayout.CameraPreviewSize(-10, double.NaN));
    }

    [Fact]
    public void CameraRunsOnlyWhileTheIslandIsOpenOnItsPage()
    {
        Assert.True(CameraPreviewSupport.CanPresent(enabled: true, expanded: true, NotchModule.Camera));
        Assert.False(CameraPreviewSupport.CanPresent(enabled: false, expanded: true, NotchModule.Camera));
        Assert.False(CameraPreviewSupport.CanPresent(enabled: true, expanded: false, NotchModule.Camera));
        Assert.False(CameraPreviewSupport.CanPresent(enabled: true, expanded: true, NotchModule.Music));
        Assert.False(CameraPreviewSupport.CanPresent(enabled: true, expanded: true, NotchModule.Camera, sessionActive: false));
    }

    [Fact]
    public void CameraPageNeedsTheIslandTheFeatureItsSwitchAndItsPage()
    {
        var store = NewStore();
        Assert.False(CameraPreviewSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchEnabled, true);
        Assert.True(CameraPreviewSupport.IsEnabled(store));

        store.Set(DefaultsKey.NotchHiddenModules, "camera");
        Assert.False(CameraPreviewSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchHiddenModules, "");

        store.Set(DefaultsKey.NotchCameraEnabled, false);
        Assert.False(CameraPreviewSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchCameraEnabled, true);

        store.Set(FeatureCatalog.Info(AppFeature.CameraPreview).AvailabilityKey, false);
        Assert.False(CameraPreviewSupport.IsEnabled(store));
        store.Set(FeatureCatalog.Info(AppFeature.CameraPreview).AvailabilityKey, true);

        store.Set(DefaultsKey.NotchEnabled, false);
        Assert.False(CameraPreviewSupport.IsEnabled(store));
    }

    [Fact]
    public void CameraStartsOnAndItsDeviceStaysOnThisPc()
    {
        var defaults = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(true, defaults[DefaultsKey.NotchCameraEnabled]);
        Assert.Equal("", defaults[DefaultsKey.NotchCameraDevice]);
        Assert.Contains(DefaultsKey.NotchCameraDevice, SettingsBackup.MachineStateKeys);
        var info = FeatureCatalog.Info(AppFeature.CameraPreview);
        Assert.Equal(WindowsStatus.Ported, info.Status);
        Assert.True(info.InstalledByDefault);
    }
}
