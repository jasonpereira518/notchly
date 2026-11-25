// SPDX-License-Identifier: GPL-3.0-or-later
// The pure decisions of the monitor, ported from upstream Services/Metrics and Services/SystemMonitor: CPU tick maths,
// memory pressure, which network interfaces count, disk and battery rules, external USB devices, alerts, the tray
// readout's thresholds and the island page's card layout. Windows differences are noted where a rule changed.
using System.Globalization;
using Notchly.Core.Features;

namespace Notchly.Core.Monitor;

// MARK: CPU

/// <summary>Cumulative busy and total ticks since boot (the counters the CPU reader differences).</summary>
public readonly record struct CpuTicks(ulong Busy, ulong Total)
{
    /// <summary>The busy share between two readings, or null when nothing advanced (or the counters went back).</summary>
    public static double? Usage(CpuTicks previous, CpuTicks current)
    {
        if (current.Total <= previous.Total || current.Busy < previous.Busy) return null;
        var total = current.Total - previous.Total;
        var busy = Math.Min(current.Busy - previous.Busy, total);
        return (double)busy / total;
    }
}

/// <summary>
/// The CPU reader's state (upstream readCPUUsage): the first read only sets a baseline; a read after a long gap
/// resets it instead of reporting the gap's average, and drops the value held from before the gap.
/// </summary>
public sealed class CpuUsageReader
{
    public const double MaximumGapSeconds = 12.5;
    private (CpuTicks Ticks, double Time)? _previous;

    public double? LastUsage { get; private set; }
    public double? LastReadAt { get; private set; }

    public double? Read(CpuTicks ticks, double now)
    {
        var previous = _previous;
        _previous = (ticks, now);
        if (previous is not { } before) return null;
        if (now - before.Time > MaximumGapSeconds)
        {
            LastUsage = null;
            LastReadAt = null;
            return null;
        }
        var usage = CpuTicks.Usage(before.Ticks, ticks);
        if (usage is null) return null;
        LastUsage = usage;
        LastReadAt = now;
        return usage;
    }

    public void Reset() { _previous = null; LastUsage = null; LastReadAt = null; }
}

// MARK: Memory

public enum MemoryPressure { Unknown, Normal, Warning, Critical }

public static class MemoryMath
{
    /// <summary>
    /// Windows has no pressure level, so it is derived: critical when under 5% of memory is available or the commit
    /// charge is at 95% of its limit, warning under 12% available or 85% committed.
    /// </summary>
    public static MemoryPressure Pressure(ulong totalBytes, ulong availableBytes, ulong commitBytes, ulong commitLimitBytes)
    {
        if (totalBytes == 0) return MemoryPressure.Unknown;
        var available = (double)availableBytes / totalBytes;
        var commit = commitLimitBytes > 0 ? (double)commitBytes / commitLimitBytes : 0;
        if (available < 0.05 || commit >= 0.95) return MemoryPressure.Critical;
        if (available < 0.12 || commit >= 0.85) return MemoryPressure.Warning;
        return MemoryPressure.Normal;
    }
}

// MARK: Network

public static class NetworkRules
{
    // IANA ifType values of the adapters that carry real traffic.
    private static readonly HashSet<int> PhysicalTypes = [6, 62, 69, 71, 117, 243, 244];
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "tap-", "tap windows", "wintun", "wireguard", "tailscale", "vpn",
        "loopback", "bluetooth", "wi-fi direct", "teredo", "isatap", "6to4", "pseudo", "npcap", "wsl", "tunnel", "ndis", "miniport",
    ];

    /// <summary>
    /// Whether an adapter counts toward real throughput. Loopback, tunnels and virtual adapters are left out so a VPN
    /// or a virtual switch does not double-count the traffic already seen on the physical adapter.
    /// </summary>
    public static bool Counts(string description, int interfaceType, bool isUp)
    {
        if (!isUp || string.IsNullOrWhiteSpace(description) || !PhysicalTypes.Contains(interfaceType)) return false;
        var lowered = description.ToLowerInvariant();
        return !VirtualMarkers.Any(lowered.Contains);
    }
}

// MARK: Disk

public static class DiskRules
{
    /// <summary>Capacity from a volume's total and free: used is total minus free, never negative.</summary>
    public static (ulong Free, ulong Used)? Capacity(ulong total, ulong? free)
    {
        if (total == 0 || free is not { } value) return null;
        var bounded = Math.Min(total, value);
        return (bounded, total - bounded);
    }

    /// <summary>A short label for a volume format; unknown tokens earn a tag only when they already read like an acronym.</summary>
    public static string? FileSystemLabel(string? type)
    {
        var token = type?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(token)) return null;
        switch (token)
        {
            case "ntfs": return "NTFS";
            case "refs": return "ReFS";
            case "exfat": return "exFAT";
            case "fat32": return "FAT32";
            case "fat": return "FAT";
            case "cdfs": return "CDFS";
            case "udf": return "UDF";
        }
        if (token.Length is < 2 or > 6 || !token.All(char.IsLetterOrDigit) || !token.Any(char.IsLetter)) return null;
        return token.ToUpperInvariant();
    }

    public const ulong NvmeDataUnitBytes = 512_000;

    /// <summary>NVMe data units (low and high words) as bytes; null on overflow.</summary>
    public static ulong? NvmeBytes(ulong? low, ulong? high)
    {
        if (low is not { } lowWord) return null;
        var highWord = high ?? 0;
        if (highWord > uint.MaxValue) return null;
        try { return checked((lowWord + (highWord << 32)) * NvmeDataUnitBytes); }
        catch (OverflowException) { return null; }
    }

    /// <summary>A SMART temperature in Celsius: kelvin readings are converted, nonsense is dropped.</summary>
    public static double? Celsius(ulong? raw)
    {
        if (raw is not { } value) return null;
        var number = (double)value;
        if (number > 150)
        {
            var celsius = number - 273.15;
            return celsius is >= -40 and <= 125 ? celsius : null;
        }
        return number is >= 1 and <= 125 ? number : null;
    }

    public static int? HealthPercent(ulong? percentageUsed) =>
        percentageUsed is { } used ? Math.Max(0, 100 - (int)Math.Min(used, 1000)) : null;

    /// <summary>A volume's menu text: "67%", "110 GB" (free) or "163 GB" (used); zero total reads 0%.</summary>
    public static string MenuText(string style, ulong total, ulong free)
    {
        var used = total - Math.Min(total, free);
        return style switch
        {
            "free" => MetricFormat.DiskBytes(free),
            "used" => MetricFormat.DiskBytes(used),
            _ => total == 0 ? "0%" : MetricFormat.Percent((double)used / total),
        };
    }
}

// MARK: Battery

public enum BatteryPowerState { Charging, ExternalPower, OnBattery, Unavailable }

public static class BatteryRules
{
    /// <summary>A charge in progress is named first, since it already implies external power.</summary>
    public static BatteryPowerState State(bool isCharging, bool externalConnected, bool hasBattery) =>
        isCharging ? BatteryPowerState.Charging : externalConnected ? BatteryPowerState.ExternalPower
            : hasBattery ? BatteryPowerState.OnBattery : BatteryPowerState.Unavailable;

    /// <summary>Time to empty only while running on the battery; negative values and the 65535 sentinel mean unknown.</summary>
    public static double? RemainingSeconds(int? timeToEmptyMinutes, bool externalConnected, bool isCharging)
    {
        if (externalConnected || isCharging || timeToEmptyMinutes is not { } minutes) return null;
        return minutes is >= 1 and < 7 * 24 * 60 ? minutes * 60.0 : null;
    }

    public static string? FormattedTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds / 60 >= int.MaxValue) return null;
        var totalMinutes = Math.Max(1, (int)(seconds / 60));
        return $"{totalMinutes / 60}h {totalMinutes % 60}m";
    }

    /// <summary>The glyph level for a charge: 100 (bolt) when plugged in, then 100, 75, 50, 25 or 0 by percent.</summary>
    public static (int Level, bool Bolt) Glyph(int percent, bool isCharging, bool externalConnected)
    {
        if (isCharging || externalConnected) return (100, true);
        return (percent >= 85 ? 100 : percent >= 60 ? 75 : percent >= 35 ? 50 : percent >= 10 ? 25 : 0, false);
    }

    /// <summary>Health from the full-charge capacity against the design capacity, capped at 100.</summary>
    public static int? HealthPercent(int? fullMilliwattHours, int? designMilliwattHours) =>
        fullMilliwattHours is > 0 && designMilliwattHours is > 0
            ? Math.Min(100, (int)Math.Round(fullMilliwattHours.Value * 100.0 / designMilliwattHours.Value)) : null;
}

// MARK: Connected devices

/// <summary>A USB device as Windows reports it.</summary>
public sealed record UsbDeviceInfo(int VendorId, int ProductId, int? DeviceClass, bool NonRemovable, string? Product, string? Vendor,
                                   string? Serial, ulong LocationId = 0, string? InstanceId = null);

public sealed record ConnectedUsbDevice(string Id, string Name, string? Vendor, int VendorId, int ProductId);

public static class UsbDeviceRules
{
    /// <summary>Hubs (class 9), billboard devices (class 17), root controllers (vendor and product 0) and internal hardware are not peripherals.</summary>
    public static ConnectedUsbDevice? Parse(UsbDeviceInfo info)
    {
        if (info.NonRemovable) return null;
        if (info.VendorId == 0 && info.ProductId == 0) return null;
        if (info.DeviceClass is 9 or 17) return null;
        var name = info.Product?.Trim() ?? "";
        var vendor = info.Vendor?.Trim() is { Length: > 0 } v ? v : null;
        var serial = info.Serial?.Trim() ?? "";
        var prefix = $"{info.VendorId}-{info.ProductId}";
        var id = !string.IsNullOrEmpty(info.InstanceId) ? $"{prefix}-{info.InstanceId}"
            : serial.Length > 0 ? $"{prefix}-{serial}"
            : info.LocationId != 0 ? $"{prefix}-loc{info.LocationId}" : $"{prefix}-{name}";
        return new ConnectedUsbDevice(id, name, vendor, info.VendorId, info.ProductId);
    }

    public static IReadOnlyList<ConnectedUsbDevice> Deduplicated(IEnumerable<ConnectedUsbDevice> devices)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return devices.Where(d => seen.Add(d.Id)).ToList();
    }
}

// MARK: Alerts

/// <summary>
/// Decides when a reading over its limit deserves a notification: it must hold across separate samples for
/// <see cref="SustainedSeconds"/>. <c>readAt</c> comes from a clock that stops during sleep.
/// </summary>
public sealed class SustainedAlertGate
{
    public const double SustainedSeconds = 12;
    private double? _heldSince, _lastReadingAt;

    public bool ShouldAlert(double? reading, double threshold, double? readAt)
    {
        if (reading is not { } value || readAt is not { } at || value < threshold)
        {
            Reset();
            return false;
        }
        if (at == _lastReadingAt) return false;
        _lastReadingAt = at;
        if (_heldSince is not { } since)
        {
            _heldSince = at;
            return false;
        }
        return at - since >= SustainedSeconds;
    }

    public void Reset() { _heldSince = null; _lastReadingAt = null; }
}

public enum MonitorAlertKind { Cpu, Memory, Disk, Battery }

public static class AlertRules
{
    public const int DefaultCpuThreshold = 90, DefaultDiskFreePercent = 10, DefaultBatteryPercent = 15, DefaultCooldownMinutes = 15;
    public static readonly IReadOnlyList<int> Cooldowns = [2, 5, 15, 30, 60];
    public const ulong MinimumDiskBytes = 10_000_000_000;

    /// <summary>A threshold within its range; anything outside falls back to the default.</summary>
    public static int SanitizedPercent(int value, int fallback, int min, int max) => value >= min && value <= max ? value : fallback;

    public static int CpuThreshold(int stored) => SanitizedPercent(stored, DefaultCpuThreshold, 50, 100);
    public static int DiskFreePercent(int stored) => SanitizedPercent(stored, DefaultDiskFreePercent, 5, 30);
    public static int BatteryPercent(int stored) => SanitizedPercent(stored, DefaultBatteryPercent, 5, 50);
    public static int CooldownMinutes(int stored) => Cooldowns.Contains(stored) ? stored : DefaultCooldownMinutes;

    /// <summary>The first disk of 10 GB or more whose free share is under the threshold.</summary>
    public static T? LowDisk<T>(IEnumerable<T> disks, Func<T, (ulong Total, ulong Free)> read, int thresholdPercent) where T : class =>
        disks.FirstOrDefault(disk =>
        {
            var (total, free) = read(disk);
            return total >= MinimumDiskBytes && (double)free / total * 100 < thresholdPercent;
        });

    /// <summary>A charge at or under the threshold while not charging; null otherwise.</summary>
    public static int? LowBattery(bool hasBattery, bool isCharging, int? chargePercent, int thresholdPercent) =>
        hasBattery && !isCharging && chargePercent is { } charge && charge <= thresholdPercent ? charge : null;

    /// <summary>Whether a kind may notify again: never within its cooldown of the last one.</summary>
    public static bool CooldownPassed(DateTime? lastSent, DateTime now, int cooldownMinutes) =>
        lastSent is not { } last || (now - last).TotalSeconds >= cooldownMinutes * 60.0;
}

// MARK: Tray readouts

public enum UsageLevel { Normal, Elevated, Critical }
public enum BatteryTone { Normal, Amber, Red }

public static class TrayReadout
{
    public const int DefaultMediumThreshold = 70, DefaultHighThreshold = 90, DefaultRedBattery = 10, DefaultAmberBattery = 20;

    /// <summary>Normal under the medium threshold, elevated from it, critical from the high one; medium clamps to 1…99, high above it up to 100.</summary>
    public static UsageLevel Level(double percent, int medium = DefaultMediumThreshold, int high = DefaultHighThreshold)
    {
        var (m, h) = Thresholds(medium, high);
        return percent >= h ? UsageLevel.Critical : percent >= m ? UsageLevel.Elevated : UsageLevel.Normal;
    }

    public static (int Medium, int High) Thresholds(int medium, int high)
    {
        var m = Math.Clamp(medium, 1, 99);
        return (m, Math.Clamp(high, m + 1, 100));
    }

    /// <summary>Red at or under the red threshold, amber at or under the early one, never on external power.</summary>
    public static BatteryTone BatteryColor(int percent, bool externalConnected, int red = DefaultRedBattery, int amber = DefaultAmberBattery) =>
        externalConnected ? BatteryTone.Normal : percent <= red ? BatteryTone.Red : percent <= amber ? BatteryTone.Amber : BatteryTone.Normal;

    /// <summary>The metrics a tray readout can show, in upstream's default order (the ones Windows can read).</summary>
    public static readonly IReadOnlyList<string> DefaultOrder =
        ["cpu", "gpu", "memory", "battery", "batteryTime", "peripheralBattery", "network", "diskUsage", "diskActivity", "connectedDevices"];

    public static string Label(string metric) => metric switch
    {
        "cpu" => "CPU", "gpu" => "GPU", "memory" => "RAM", "diskUsage" or "diskActivity" => "DSK", "network" => "NET", "battery" => "BAT",
        "batteryTime" => "LEFT", "peripheralBattery" => "ACC", "connectedDevices" => "USB",
        _ => metric.ToUpperInvariant(),
    };
}

// MARK: Island page

/// <summary>The System page's card grid (upstream NotchLayout.systemRowRanges and the card constants).</summary>
public static class SystemPageLayout
{
    public const double CardHeight = 72, CardWidth = 128, RowSpacing = 10, MeterHeight = 5, DetailGraphHeight = 38;
    public const double HotUsage = 0.85, LowBatteryPercent = 20, LowDiskFree = 0.10;

    public static double HoverInset(double width) => Math.Ceiling(Math.Max(0, width) * 0.015) + 1;

    public static int RailCapacity(double width, double itemWidth, double spacing) =>
        !double.IsFinite(width) || itemWidth <= 0 ? 1 : Math.Max(1, (int)Math.Floor((width + spacing) / (itemWidth + spacing)));

    /// <summary>Complete rows balanced across the width, in reading order, with no empty cells.</summary>
    public static IReadOnlyList<(int Start, int End)> RowRanges(int count, double width)
    {
        if (count <= 0) return [];
        var columns = RailCapacity(width, CardWidth, RowSpacing);
        var rows = (count + columns - 1) / columns;
        var baseCount = count / rows;
        var remainder = count % rows;
        return Enumerable.Range(0, rows).Select(row =>
        {
            var start = row * baseCount + Math.Min(row, remainder);
            return (start, start + baseCount + (row < remainder ? 1 : 0));
        }).ToList();
    }

    /// <summary>The page's height: its rows and spacing plus the hover inset, capped at the content budget (then it scrolls).</summary>
    public static double Height(int count, double width, double budget)
    {
        if (count <= 0) return Math.Min(140, budget);
        var rows = RowRanges(count, width - HoverInset(width) * 2).Count;
        return Math.Min(budget, rows * CardHeight + (rows - 1) * RowSpacing + HoverInset(width) * 2);
    }

    public static bool UsageIsHot(double fraction) => fraction >= HotUsage;
    public static bool BatteryIsLow(int percent, bool pluggedIn) => !pluggedIn && percent <= LowBatteryPercent;
    public static bool DiskIsLow(ulong total, ulong free) => total > 0 && (double)free / total < LowDiskFree;

    /// <summary>How many cards the page shows for the installed features (power draw gets its own card beside the battery).</summary>
    public static int CardCount(Func<AppFeature, bool> installed, bool hasBattery) =>
        new[] { AppFeature.MonitorCPU, AppFeature.MonitorGPU, AppFeature.MonitorMemory, AppFeature.MonitorDisk }.Count(installed)
        + (installed(AppFeature.MonitorNetwork) ? 1 : 0)
        + (hasBattery && installed(AppFeature.MonitorPower) ? 1 : 0)
        + (installed(AppFeature.MonitorPower) ? 1 : 0)
        + (installed(AppFeature.ConnectedDevices) ? 1 : 0);
}
