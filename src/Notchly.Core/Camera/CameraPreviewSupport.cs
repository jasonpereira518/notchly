// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/QuickTools/CameraPreviewSupport.swift (NotchCameraSupport) and the states of
// Services/QuickTools/CameraPreviewService.swift. Windows-specific: there is no app panel or capture toolbar
// over the island, so presenting only needs the page open; a locked session stops the camera instead.
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Camera;

/// <summary>Where the camera mirror stands (upstream CameraPreviewService.PreviewState).</summary>
public enum CameraPreviewState
{
    /// <summary>Off: the hint and Open camera.</summary>
    Idle,
    /// <summary>Windows is asking for permission (kept for parity; desktop apps are never asked in-line).</summary>
    WaitingPermission,
    Starting,
    Running,
    /// <summary>Camera access is off in Windows' privacy settings.</summary>
    Denied,
    NoCamera,
    /// <summary>The camera failed or another app holds it on its own.</summary>
    Unavailable,
}

public static class CameraPreviewSupport
{
    // Upstream NotchActivityStrings.enUS and CameraPreviewFeatureStrings.enUS; Windows settings stand in for System Settings.
    public const string Title = "Camera mirror";
    public const string Hint = "Open a live mirror here. The camera stops when you leave this view.";
    public const string Start = "Open camera";
    public const string Stop = "Stop camera";
    public const string Unavailable = "The camera could not start. Try opening it again.";
    public const string NoCamera = "No camera detected";
    public const string MenuLabel = "Camera";
    public const string Denied = "Camera access is off in Windows settings";
    public const string DeniedDetail = "Allow desktop apps to use your camera, then open it again.";

    /// <summary>The island's Camera page is on (upstream NotchCameraSupport.isEnabled).</summary>
    public static bool IsEnabled(SettingsStore store) =>
        NotchSupport.IsEnabled(store) && store.IsAvailable(AppFeature.CameraPreview)
        && store.GetBool(DefaultsKey.NotchCameraEnabled)
        && NotchSupport.Modules(store.GetString(DefaultsKey.NotchModuleOrder), store.GetString(DefaultsKey.NotchHiddenModules))
                       .Contains(NotchModule.Camera);

    /// <summary>
    /// The camera runs only while the island is open on the Camera page (upstream canPresent): leaving the page,
    /// closing the island or locking the PC stops it.
    /// </summary>
    public static bool CanPresent(bool enabled, bool expanded, NotchModule selected, bool sessionActive = true) =>
        enabled && expanded && selected == NotchModule.Camera && sessionActive;
}
