// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure rules of upstream Services/QuickTools/CameraPreviewService.swift for the floating mirror: where it sits, when an
// outside click or another app coming forward closes it, and what a change of cameras does. Upstream has no tests of these, so
// CameraWindowTests is new. Windows differences: coordinates run downward, and there is no per-app camera default, so the chosen
// camera is stored by the app (only on an explicit pick).
using Notchly.Core.Notch;

namespace Notchly.Core.Camera;

/// <summary>What a change in the list of cameras does to the open mirror (upstream <c>handleDevicesChanged</c>).</summary>
public enum CameraDeviceChange
{
    None,
    /// <summary>The chosen camera left: switch to the first one, without remembering it.</summary>
    SwitchToFirst,
    /// <summary>The chosen camera left and none remain.</summary>
    StopNoCamera,
    /// <summary>No camera was present and one arrived.</summary>
    Start,
}

public static class CameraWindowSupport
{
    public const double Width = 320, Height = 240, Radius = 14;
    /// <summary>How far below the top of the screen the mirror sits, so the eyes stay close to the lens.</summary>
    public const double TopGap = 48;
    public const double ScreenMargin = 16;
    /// <summary>After the permission prompt resolves, another app coming forward is ignored for this long.</summary>
    public const double PermissionGraceSeconds = 1.0;

    /// <summary>Upstream <c>position</c>: x-centred on the screen, 48 below its top, kept 16 inside on every side.</summary>
    public static PointD Origin(RectD screen, SizeD size)
    {
        var x = screen.X + screen.Width / 2 - size.Width / 2;
        var left = Math.Max(screen.X + ScreenMargin, Math.Min(x, screen.X + screen.Width - size.Width - ScreenMargin));
        var top = Math.Min(screen.Y + TopGap, screen.Y + screen.Height - size.Height - ScreenMargin);
        return new PointD(left, Math.Max(screen.Y + ScreenMargin, top));
    }

    /// <summary>While the permission prompt may be up, an outside click or the prompt's own activation must not close the mirror.</summary>
    public static bool DismissesOnOutsideInteraction(CameraPreviewState state) => state != CameraPreviewState.WaitingPermission;

    /// <summary>
    /// Another app coming forward closes the mirror, unless the mirror is waiting on permission, or the permission resolved less than
    /// a second ago (<paramref name="resolvedAgoSeconds"/>, null when it never waited).
    /// </summary>
    public static bool HidesWhenAnotherAppActivates(CameraPreviewState state, double? resolvedAgoSeconds) =>
        DismissesOnOutsideInteraction(state) && !(resolvedAgoSeconds is { } ago && ago < PermissionGraceSeconds);

    public static CameraDeviceChange OnDevicesChanged(CameraPreviewState state, int deviceCount, bool selectedStillPresent) => state switch
    {
        CameraPreviewState.Running or CameraPreviewState.Starting when !selectedStillPresent =>
            deviceCount > 0 ? CameraDeviceChange.SwitchToFirst : CameraDeviceChange.StopNoCamera,
        CameraPreviewState.NoCamera when deviceCount > 0 => CameraDeviceChange.Start,
        _ => CameraDeviceChange.None,
    };

    /// <summary>The camera to open: the stored one when it is still there, else the first. Null when there is none.</summary>
    public static string? Preferred(IReadOnlyList<string> deviceIds, string? stored) =>
        !string.IsNullOrEmpty(stored) && deviceIds.Contains(stored) ? stored : deviceIds.FirstOrDefault();

    /// <summary>The picker shows only when there is a choice.</summary>
    public static bool ShowsPicker(int deviceCount) => deviceCount > 1;
}
