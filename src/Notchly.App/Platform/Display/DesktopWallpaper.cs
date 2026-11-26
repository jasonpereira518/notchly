// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: what upstream's WallpaperService does by patching the wallpaper agent's Store.plist. Windows has a supported shell
// object for it, IDesktopWallpaper, which sets the picture of one monitor or of all of them and reports the current ones. When the
// object is missing, SystemParametersInfo sets one picture for every monitor. Use from a thread that may call COM (the UI thread).
using System.Runtime.InteropServices;
using Serilog;

namespace Notchly.App.Platform.Display;

/// <summary>A monitor of the wallpaper object: its id (a device path) and where it is, in screen pixels.</summary>
public sealed record WallpaperMonitor(string Id, int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public static class DesktopWallpaper
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        Rect GetMonitorRect([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
    }

    private static readonly Guid ClassId = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    private static IDesktopWallpaper? Create()
    {
        try { return (IDesktopWallpaper?)Activator.CreateInstance(Type.GetTypeFromCLSID(ClassId)!); }
        catch (Exception e)
        {
            Log.Warning(e, "The desktop wallpaper object is not available");
            return null;
        }
    }

    /// <summary>The monitors the wallpaper object knows, with where each sits. Empty when it is not available.</summary>
    public static IReadOnlyList<WallpaperMonitor> Monitors()
    {
        if (Create() is not { } wallpaper) return [];
        try
        {
            var list = new List<WallpaperMonitor>();
            var count = wallpaper.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var id = wallpaper.GetMonitorDevicePathAt(i);
                if (string.IsNullOrEmpty(id)) continue;
                try
                {
                    var rect = wallpaper.GetMonitorRect(id);
                    list.Add(new WallpaperMonitor(id, rect.Left, rect.Top, rect.Right, rect.Bottom));
                }
                catch (COMException) { }
            }
            return list;
        }
        catch (COMException e)
        {
            Log.Warning(e, "The monitors could not be listed");
            return [];
        }
    }

    /// <summary>The picture a monitor shows (null id: the first), or "" when it has none (a slide show, a colour).</summary>
    public static string Current(string? monitorId = null)
    {
        if (Create() is not { } wallpaper) return "";
        try { return wallpaper.GetWallpaper(monitorId) ?? ""; }
        catch (COMException) { return ""; }
    }

    /// <summary>Sets the picture of one monitor, or of all of them with a null id. False when Windows refused.</summary>
    public static bool Set(string? monitorId, string path)
    {
        if (Create() is { } wallpaper)
        {
            try
            {
                wallpaper.SetWallpaper(monitorId, path);
                return true;
            }
            catch (COMException e) { Log.Warning(e, "The wallpaper could not be set"); return false; }
        }
        // Without the object only one picture for every monitor is possible.
        return monitorId is null && SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x01 | 0x02 /* update the profile, tell everyone */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
}
