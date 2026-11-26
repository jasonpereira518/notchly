// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-ins for upstream's PowerSampler (AppleSmartBattery/SMC) and USBDeviceSampler (IOKit): the battery from
// Windows' own report plus the battery driver's IOCTLs (cycle count, chemistry), and external USB devices from device
// enumeration. Windows exposes no system or adapter watts, so the power card shows the battery's own flow.
using System.Runtime.InteropServices;
using System.Text;
using Notchly.Core.Monitor;
using Serilog;
using Windows.Devices.Enumeration;

namespace Notchly.App.Platform.Monitor;

public sealed record PowerReading(bool HasBattery, int Percent, bool IsCharging, bool ExternalConnected, int? RateMilliwatts, int? RemainingMilliwattHours,
                                  int? FullMilliwattHours, int? DesignMilliwattHours, int? CycleCount, string? Chemistry, int? TimeToEmptyMinutes)
{
    public int? HealthPercent => BatteryRules.HealthPercent(FullMilliwattHours, DesignMilliwattHours);
}

public static class PowerSampler
{
    private static readonly Lazy<(int? Cycles, string? Chemistry)> Static = new(ReadBatteryInformation);

    /// <summary>The battery now, or null on a PC without one.</summary>
    public static PowerReading? Read()
    {
        // Flag 128 is "no battery" and 255 is "unknown": neither has anything to show.
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == 255 || (status.BatteryFlag & 128) != 0) return null;
        var percent = status.BatteryLifePercent == 255 ? 0 : status.BatteryLifePercent;
        var external = status.ACLineStatus == 1;
        var charging = (status.BatteryFlag & 8) != 0;
        int? rate = null, remaining = null, full = null, design = null;
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            rate = report.ChargeRateInMilliwatts;
            remaining = report.RemainingCapacityInMilliwattHours;
            full = report.FullChargeCapacityInMilliwattHours;
            design = report.DesignCapacityInMilliwattHours;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { Log.Debug(e, "Battery report unavailable"); }
        var (cycles, chemistry) = Static.Value;
        // BatteryLifeTime is seconds; 0xFFFFFFFF means unknown.
        int? minutes = status.BatteryLifeTime != 0xFFFFFFFF ? (int)(status.BatteryLifeTime / 60) : null;
        return new PowerReading(true, percent, charging, external, rate, remaining, full, design, cycles, chemistry, minutes);
    }

    // MARK: Battery driver

    private static (int? Cycles, string? Chemistry) ReadBatteryInformation()
    {
        var path = FirstBatteryPath();
        if (path is null) return (null, null);
        using var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (null, null);
        try
        {
            uint tag = 0, wait = 0;
            if (!DeviceIoControl(handle, 0x294040, ref wait, 4, out tag, 4, out _, IntPtr.Zero) || tag == 0) return (null, null);
            var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = 0, AtRate = 0 };
            if (!DeviceIoControl(handle, 0x294044, ref query, (uint)Marshal.SizeOf<BatteryQueryInformation>(), out BatteryInformation info,
                    (uint)Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero)) return (null, null);
            var chemistry = Encoding.ASCII.GetString(BitConverter.GetBytes(info.Chemistry)).Trim('\0', ' ');
            return (info.CycleCount > 0 ? (int)info.CycleCount : null, chemistry.Length > 0 ? chemistry : null);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { Log.Debug(e, "Battery driver unavailable"); return (null, null); }
    }

    private static string? FirstBatteryPath()
    {
        var guid = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                return SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, IntPtr.Zero) ? Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology, R1, R2, R3;
        public uint Chemistry, DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, ref BatteryQueryInformation input, uint inputSize, out BatteryInformation output, uint outputSize, out uint returned, IntPtr overlapped);
}

public static class UsbSampler
{
    private const string InstanceId = "System.Devices.DeviceInstanceId";
    private const string Container = "System.Devices.ContainerId";
    // Windows keeps a node for every device that was ever plugged in; only the ones that are here now count.
    private const string Present = "System.Devices.Present";
    // DEVPKEY_Device_BusReportedDeviceDesc: the product name the device itself reports.
    private const string ProductName = "{540B947E-8B40-45BC-A8A2-6A0B894CBDA2} 4";
    // The container id Windows gives hardware that is part of the PC itself (the webcam, the Bluetooth radio, a fingerprint reader).
    private static readonly Guid BuiltInContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>External USB peripherals: one entry per physical device (not per interface), never hubs, root controllers or the PC's own hardware.</summary>
    public static async Task<IReadOnlyList<ConnectedUsbDevice>> ReadAsync()
    {
        var devices = new List<ConnectedUsbDevice>();
        try
        {
            var found = await DeviceInformation.FindAllAsync("System.Devices.DeviceInstanceId:~<\"USB\\VID_\"", [InstanceId, Container, ProductName, Present],
                DeviceInformationKind.Device);
            foreach (var device in found)
            {
                var instance = device.Properties.TryGetValue(InstanceId, out var value) ? value as string : null;
                if (instance is null || !TryIds(instance, out var vendor, out var product, out var serial)) continue;
                if (!(device.Properties.TryGetValue(Present, out var here) && here is true)) continue;
                var container = device.Properties.TryGetValue(Container, out var c) && c is Guid guid ? guid : (Guid?)null;
                var name = device.Properties.TryGetValue(ProductName, out var n) && n is string product2 && product2.Trim().Length > 0 ? product2.Trim() : device.Name;
                var parsed = UsbDeviceRules.Parse(new UsbDeviceInfo(vendor, product, IsHub(name) ? 9 : null, container == BuiltInContainer, name, null, serial, 0, instance));
                if (parsed is not null) devices.Add(parsed);
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Debug(e, "USB devices could not be listed");
        }
        return UsbDeviceRules.Deduplicated(devices).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Hubs (the dock's, the laptop's) are plumbing, not peripherals.
    private static bool IsHub(string name) => name.Contains("hub", StringComparison.OrdinalIgnoreCase);

    // USB\VID_0781&PID_5583\4C530001234567890123 (a device; its interfaces carry &MI_ and are not counted)
    private static bool TryIds(string instance, out int vendor, out int product, out string? serial)
    {
        vendor = product = 0;
        serial = null;
        if (!instance.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) || instance.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = instance.Split('\\');
        if (parts.Length != 3) return false;
        var ids = parts[1].Split('&');
        if (ids.Length < 2 || !int.TryParse(ids[0].AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out vendor)
            || !int.TryParse(ids[1].AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out product)) return false;
        // A Windows-generated id (it contains &) is not a real serial number.
        serial = parts[2].Contains('&') ? null : parts[2];
        return true;
    }
}
