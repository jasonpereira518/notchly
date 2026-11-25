// SPDX-License-Identifier: GPL-3.0-or-later
// The three ways to change a display's brightness (upstream builds a route per display the same way): the built-in
// panel through WMI, an external monitor through DDC/CI (VCP code 0x10, the monitor's own backlight), and a
// software dim for displays that do neither, drawn as a click-through black overlay. Levels are 0...1 here; the
// callers do the stepping and the extended-dimming split (Notchly.Core.Energy.BrightnessSupport).
using System.Management;
using System.Runtime.InteropServices;
using Notchly.Core.Energy;
using Serilog;

namespace Notchly.App.Platform.Display;

/// <summary>The built-in panel's backlight, 0...100 through WMI.</summary>
public static class InternalPanel
{
    public static bool Exists()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get()) { item.Dispose(); return true; }
        }
        catch (Exception e) { Log.Debug(e, "No WMI brightness"); }
        return false;
    }

    /// <summary>
    /// The monitor hardware ids Windows can set the backlight of (such as "BOE0B7D"). On a hybrid-graphics laptop the
    /// panel can be reached through a path that is not marked internal, so a display with one of these ids is the panel.
    /// </summary>
    public static HashSet<string> HardwareIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get())
                using (item)
                {
                    var parts = Convert.ToString(item["InstanceName"])?.Split('\\');
                    if (parts is { Length: > 1 }) ids.Add(parts[1]);
                }
        }
        catch (Exception e) { Log.Debug(e, "No WMI brightness ids"); }
        return ids;
    }

    public static double? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get())
                using (item) return Math.Clamp(Convert.ToInt32(item["CurrentBrightness"]) / 100.0, 0, 1);
        }
        catch (Exception e) { Log.Debug(e, "Brightness could not be read"); }
        return null;
    }

    public static bool Write(double level)
    {
        try
        {
            var percent = (byte)Math.Round(BrightnessSupport.Clamp(level) * 100);
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            var wrote = false;
            foreach (ManagementObject monitor in searcher.Get())
                using (monitor) { monitor.InvokeMethod("WmiSetBrightness", [(uint)1, percent]); wrote = true; }
            return wrote;
        }
        catch (Exception e) { Log.Debug(e, "Brightness could not be set"); return false; }
    }
}

/// <summary>An external monitor's backlight over DDC/CI. Each call can take a few hundred milliseconds.</summary>
public sealed class DdcMonitor : IDisposable
{
    private const byte Luminance = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);

    private readonly PhysicalMonitor[] _monitors;
    private int _max;

    private DdcMonitor(PhysicalMonitor[] monitors) => _monitors = monitors;

    /// <summary>The monitor behind a display handle, or null when it does not answer DDC/CI.</summary>
    public static DdcMonitor? Open(IntPtr monitor)
    {
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return null;
            var list = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, list)) return null;
            var ddc = new DdcMonitor(list);
            if (ddc.Read() is null) { ddc.Dispose(); return null; }
            return ddc;
        }
        catch (Exception e) { Log.Debug(e, "DDC could not be opened"); return null; }
    }

    /// <summary>The level 0...1, or null when the monitor did not answer.</summary>
    public double? Read()
    {
        foreach (var monitor in _monitors)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetVCPFeatureAndVCPFeatureReply(monitor.Handle, Luminance, IntPtr.Zero, out var current, out var max))
                {
                    _max = (int)max;
                    return BrightnessSupport.Normalized((int)current, _max);
                }
                Thread.Sleep(20);
            }
        }
        return null;
    }

    public bool Write(double level)
    {
        var value = (uint)BrightnessSupport.DeviceValue(level, _max);
        var wrote = false;
        foreach (var monitor in _monitors)
        {
            for (var attempt = 0; attempt < 2 && !wrote; attempt++)
            {
                wrote = SetVCPFeature(monitor.Handle, Luminance, value);
                if (!wrote) Thread.Sleep(20);
            }
        }
        return wrote;
    }

    public void Dispose()
    {
        try { DestroyPhysicalMonitors((uint)_monitors.Length, _monitors); } catch { /* the handles end with the process */ }
    }
}

/// <summary>The monitor attached to a display: its name and a stable id (the PnP device id, which survives a re-plug).</summary>
public static class MonitorIdentity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    /// <summary>The hardware id inside a device id: "MONITOR\BOE0B7D\{guid}\0001" gives "BOE0B7D".</summary>
    public static string? HardwareId(string deviceId) => deviceId.Split('\\') is { Length: > 1 } parts ? parts[1] : null;

    public static (string Id, string Name) Of(string displayDeviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(displayDeviceName, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceId)
            ? (device.DeviceId, string.IsNullOrWhiteSpace(device.DeviceString) ? displayDeviceName : device.DeviceString)
            : (displayDeviceName, displayDeviceName);
    }
}
