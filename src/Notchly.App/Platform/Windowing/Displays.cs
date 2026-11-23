// SPDX-License-Identifier: GPL-3.0-or-later
// Monitors in physical pixels with their DPI, and which one is the laptop's built-in panel
// (the one with the webcam), found through QueryDisplayConfig's output technology.
using System.Runtime.InteropServices;
using Notchly.Core.Notch;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public sealed record DisplayInfo(IntPtr Handle, string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary,
                                 bool IsBuiltIn, double Scale)
{
    /// <summary>The monitor in DIPs with a top-left origin at the monitor's own corner.</summary>
    public RectD LocalDips => new(0, 0, Bounds.Width / Scale, Bounds.Height / Scale);
}

public static class Displays
{
    public static IReadOnlyList<DisplayInfo> All()
    {
        var builtIn = BuiltInDeviceNames();
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
                list.Add(new DisplayInfo(monitor, info.szDevice, info.rcMonitor, info.rcWork,
                                         (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                                         builtIn.Contains(info.szDevice), scale));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static DisplayInfo? AtPoint(int x, int y)
    {
        var handle = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return All().FirstOrDefault(d => d.Handle == handle);
    }

    /// <summary>Port of upstream NotchDisplay choice: automatic prefers the built-in panel.</summary>
    public static DisplayInfo? Choose(NotchDisplay choice)
    {
        var all = All();
        if (all.Count == 0) return null;
        var primary = all.FirstOrDefault(d => d.IsPrimary) ?? all[0];
        var builtIn = all.FirstOrDefault(d => d.IsBuiltIn);
        switch (choice)
        {
            case NotchDisplay.BuiltIn: return builtIn ?? primary;
            case NotchDisplay.Main: return primary;
            case NotchDisplay.Pointer:
            case NotchDisplay.All:
                GetCursorPos(out var p);
                return AtPoint(p.X, p.Y) ?? primary;
            default: return builtIn ?? primary;
        }
    }

    // MARK: QueryDisplayConfig

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000, OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11,
                       OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshNumerator; public uint refreshDenominator; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct MODE_INFO { [FieldOffset(0)] public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths,
                                                 ref uint modeCount, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME request);

    private static HashSet<string> BuiltInDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return names;
            var paths = new PATH_INFO[pathCount];
            var modes = new MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return names;
            foreach (var path in paths.Take((int)pathCount))
            {
                var tech = path.targetInfo.outputTechnology;
                if (tech is not (OUTPUT_TECHNOLOGY_INTERNAL or OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED or OUTPUT_TECHNOLOGY_UDI_EMBEDDED))
                    continue;
                var request = new SOURCE_DEVICE_NAME
                {
                    header = new DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref request) == 0) names.Add(request.viewGdiDeviceName);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older systems: "automatic" falls back to the primary display.
        }
        return names;
    }
}
