// SPDX-License-Identifier: GPL-3.0-or-later
// Windows' own mouse parameters, for the two toggles that flip them: activate a window when the pointer rests on it, and
// "enhance pointer precision". Changes are made for the session only (no write to the user's profile), so a crash
// or a sign-out puts Windows' saved values back by itself; the callers also give them back on turn-off and quit.
using System.Runtime.InteropServices;
using Notchly.Core.Tools;

namespace Notchly.App.Platform.Tools;

public static class MouseParameters
{
    private const uint SpiGetMouse = 0x0003, SpiSetMouse = 0x0004,
        SpiGetActiveTracking = 0x1000, SpiSetActiveTracking = 0x1001,
        SpiGetActiveTrackingZOrder = 0x100C, SpiSetActiveTrackingZOrder = 0x100D,
        SpiGetActiveTrackingTimeout = 0x2002, SpiSetActiveTrackingTimeout = 0x2003;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);

    // MARK: Pointer acceleration

    public static PointerAccelerationSupport.Original? ReadMouse()
    {
        var values = new int[3];
        return SystemParametersInfo(SpiGetMouse, 0, values, 0) ? new PointerAccelerationSupport.Original(values[0], values[1], values[2]) : null;
    }

    public static bool WriteMouse(PointerAccelerationSupport.Original values) =>
        SystemParametersInfo(SpiSetMouse, 0, [values.Threshold1, values.Threshold2, values.Acceleration], 0);

    // MARK: Hover activation

    public static FocusFollowsSupport.Original? ReadTracking()
    {
        var tracking = 0;
        var raise = 0;
        var timeout = 0;
        if (!SystemParametersInfo(SpiGetActiveTracking, 0, ref tracking, 0)) return null;
        SystemParametersInfo(SpiGetActiveTrackingZOrder, 0, ref raise, 0);
        SystemParametersInfo(SpiGetActiveTrackingTimeout, 0, ref timeout, 0);
        return new FocusFollowsSupport.Original(tracking != 0, raise != 0, timeout);
    }

    /// <summary>The three settings are values, not pointers, for the "set" calls.</summary>
    public static bool WriteTracking(FocusFollowsSupport.Original values)
    {
        var ok = SystemParametersInfo(SpiSetActiveTrackingTimeout, 0, new IntPtr(values.Milliseconds), 0);
        ok &= SystemParametersInfo(SpiSetActiveTrackingZOrder, 0, values.Raise ? new IntPtr(1) : IntPtr.Zero, 0);
        ok &= SystemParametersInfo(SpiSetActiveTracking, 0, values.Tracking ? new IntPtr(1) : IntPtr.Zero, 0);
        return ok;
    }
}
