// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Metrics/MonitorSamplingPolicy.swift and the sampling plan of SystemMonitor.swift: which
// readings are taken, how often, and when nothing needs sampling at all. Temperature and fan kinds arrive with the
// elevated helper (M12); power draw has no Windows reading.
using Notchly.Core.Features;

namespace Notchly.Core.Monitor;

public enum MonitorSamplingKind
{
    Cpu, Memory, Network, Disk, Power, PeripheralBattery, GpuUsage,
    /// <summary>GPU usage pinned to the tray: it moves as fast as CPU usage, so it keeps the chosen interval.</summary>
    GpuUsageTray,
    ConnectedDevices,
}

public static class MonitorSamplingPolicy
{
    public static bool ShouldSample(MonitorSamplingKind kind, int tick, int intervalSeconds, bool foreground,
                                    IReadOnlySet<MonitorSamplingKind>? forced = null) =>
        forced?.Contains(kind) == true || tick % SampleStride(kind, intervalSeconds, foreground) == 0;

    public static int SampleStride(MonitorSamplingKind kind, int intervalSeconds, bool foreground) =>
        Math.Max(1, (int)Math.Ceiling(TargetSeconds(kind, foreground) / Math.Max(1, intervalSeconds)));

    /// <summary>The timer's cadence in base ticks: the GCD of the strides, so each kind still lands on its own.</summary>
    public static int WakeTicks(IEnumerable<MonitorSamplingKind> kinds, int intervalSeconds, bool foreground) =>
        Math.Max(1, kinds.Aggregate(0, (partial, kind) => Gcd(partial, SampleStride(kind, intervalSeconds, foreground))));

    /// <summary>Rounds a tick up onto the wake grid after a cadence change, so a stride multiple stays reachable.</summary>
    public static int AlignedTick(int tick, int wakeTicks)
    {
        if (wakeTicks <= 1) return tick;
        var remainder = tick % wakeTicks;
        return remainder == 0 ? tick : tick + (wakeTicks - remainder);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static double TargetSeconds(MonitorSamplingKind kind, bool foreground)
    {
        if (foreground)
            return kind switch { MonitorSamplingKind.PeripheralBattery => 15, MonitorSamplingKind.ConnectedDevices => 2, _ => 1 };
        return kind switch
        {
            MonitorSamplingKind.Cpu or MonitorSamplingKind.Memory or MonitorSamplingKind.Network or MonitorSamplingKind.GpuUsageTray => 1,
            MonitorSamplingKind.GpuUsage => 10,
            MonitorSamplingKind.ConnectedDevices => 10,
            MonitorSamplingKind.Power => 15,
            // Must stay well under the disk gap (15 s) after timer slop, or background samples would be discarded.
            MonitorSamplingKind.Disk => 10,
            MonitorSamplingKind.PeripheralBattery => 60,
            _ => 1,
        };
    }
}

/// <summary>What is asking for readings right now.</summary>
public sealed record MonitorDemand(
    IReadOnlySet<AppFeature> Installed,
    bool PanelOpen = false, bool IslandPageOpen = false, MetricDetail? IslandDetail = null,
    bool PanelSystem = true, bool PanelCpuRow = true, bool PanelGpuRow = true, bool PanelMemoryRow = true, bool PanelDevices = true,
    bool PanelCoresOpen = false, bool PanelNetwork = true, bool PanelDisk = true, bool PanelPower = true, bool PanelPeripherals = false,
    IReadOnlySet<string>? TrayMetrics = null, IReadOnlySet<MonitorAlertKind>? Alerts = null, bool HasBattery = true);

public enum MetricDetail { Cpu, Gpu, Memory, Network, Disk, Battery, Power, Devices }

/// <summary>The readings to take: a kind is sampled only when its feature is installed and something shows or watches it.</summary>
public sealed record SamplingPlan(IReadOnlySet<MonitorSamplingKind> Kinds, bool PerCore, bool Foreground)
{
    public bool IsIdle => Kinds.Count == 0;

    public static SamplingPlan Compute(MonitorDemand d)
    {
        var tray = d.TrayMetrics ?? new HashSet<string>();
        var alerts = d.Alerts ?? new HashSet<MonitorAlertKind>();
        var kinds = new HashSet<MonitorSamplingKind>();
        bool Has(AppFeature f) => d.Installed.Contains(f);
        bool Panel(bool section, bool row = true) => d.PanelOpen && section && row;
        var island = d.IslandPageOpen;

        if (Has(AppFeature.MonitorCPU) && (Panel(d.PanelSystem, d.PanelCpuRow) || island || tray.Contains("cpu") || alerts.Contains(MonitorAlertKind.Cpu)))
            kinds.Add(MonitorSamplingKind.Cpu);
        if (Has(AppFeature.MonitorGPU))
        {
            if (Panel(d.PanelSystem, d.PanelGpuRow) || island) kinds.Add(MonitorSamplingKind.GpuUsage);
            else if (tray.Contains("gpu")) kinds.Add(MonitorSamplingKind.GpuUsageTray);
        }
        if (Has(AppFeature.MonitorMemory) && (Panel(d.PanelSystem, d.PanelMemoryRow) || island || tray.Contains("memory") || alerts.Contains(MonitorAlertKind.Memory)))
            kinds.Add(MonitorSamplingKind.Memory);
        if (Has(AppFeature.MonitorNetwork) && (Panel(d.PanelNetwork) || island || tray.Contains("network")))
            kinds.Add(MonitorSamplingKind.Network);
        if (Has(AppFeature.MonitorDisk) && (Panel(d.PanelDisk) || island || tray.Contains("diskUsage") || tray.Contains("diskActivity") || alerts.Contains(MonitorAlertKind.Disk)))
            kinds.Add(MonitorSamplingKind.Disk);
        if (Has(AppFeature.MonitorPower) && d.HasBattery
            && (Panel(d.PanelPower) || island || tray.Contains("battery") || tray.Contains("batteryTime") || alerts.Contains(MonitorAlertKind.Battery)))
            kinds.Add(MonitorSamplingKind.Power);
        // Devices are read only when a surface shows them: the System card, the devices detail, or the island's card.
        if (Has(AppFeature.ConnectedDevices) && (Panel(d.PanelSystem, d.PanelDevices) || island || tray.Contains("connectedDevices")))
            kinds.Add(MonitorSamplingKind.ConnectedDevices);
        // Peripheral batteries: the power card's row, the tray option, or the battery detail (even with the row hidden).
        if (Has(AppFeature.MonitorPower) && (Panel(d.PanelPower, d.PanelPeripherals) || tray.Contains("peripheralBattery") || d.IslandDetail == MetricDetail.Battery))
            kinds.Add(MonitorSamplingKind.PeripheralBattery);

        var perCore = Has(AppFeature.MonitorCPU) && (d.PanelCoresOpen && Panel(d.PanelSystem, d.PanelCpuRow) || d.IslandDetail == MetricDetail.Cpu);
        return new SamplingPlan(kinds, perCore, d.PanelOpen || island);
    }
}
