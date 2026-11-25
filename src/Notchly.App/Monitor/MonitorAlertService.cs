// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/SystemMonitor/MonitorAlertService.swift: optional notifications for sustained CPU load,
// critical memory, a nearly full disk and a low battery. Everything is off by default and throttled by a cooldown per
// kind; the decisions (thresholds, the 12 s gate, cooldowns) live in Notchly.Core.Monitor and are tested there.
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Monitor;

public sealed class MonitorAlertService(SettingsStore settings, SystemMonitor monitor, Action<string, string> notify) : IDisposable
{
    private readonly SustainedAlertGate _cpuGate = new();
    private readonly Dictionary<MonitorAlertKind, DateTime> _lastSent = [];
    private bool _attached;

    /// <summary>An alert was sent: its kind, title and body (the island shows it too).</summary>
    public event Action<MonitorAlertKind, string, string>? Alert;

    public void Sync()
    {
        var any = AnyEnabled();
        if (any && !_attached) { monitor.Updated += Evaluate; _attached = true; }
        else if (!any && _attached) { monitor.Updated -= Evaluate; _attached = false; _cpuGate.Reset(); }
    }

    private bool On(string key, AppFeature feature) => settings.GetBool(key) && settings.IsAvailable(feature);

    private bool AnyEnabled() =>
        On(DefaultsKey.MonitorAlertCPU, AppFeature.MonitorCPU) || On(DefaultsKey.MonitorAlertMemory, AppFeature.MonitorMemory)
        || On(DefaultsKey.MonitorAlertDisk, AppFeature.MonitorDisk) || On(DefaultsKey.MonitorAlertBattery, AppFeature.MonitorPower);

    private void Evaluate()
    {
        var snapshot = monitor.Snapshot;
        if (On(DefaultsKey.MonitorAlertCPU, AppFeature.MonitorCPU))
        {
            var threshold = AlertRules.CpuThreshold(settings.GetInt(DefaultsKey.MonitorAlertCPUThreshold, AlertRules.DefaultCpuThreshold));
            if (_cpuGate.ShouldAlert(snapshot.CpuUsage, threshold / 100.0, snapshot.CpuReadAt))
                Send(MonitorAlertKind.Cpu, "High CPU usage", $"The processor has been above {threshold}% for a while.");
        }
        else _cpuGate.Reset();

        if (On(DefaultsKey.MonitorAlertMemory, AppFeature.MonitorMemory) && snapshot.Memory?.Pressure == MemoryPressure.Critical)
            Send(MonitorAlertKind.Memory, "Memory is running out", "Windows is short of memory. Close some apps to keep things responsive.");

        if (On(DefaultsKey.MonitorAlertDisk, AppFeature.MonitorDisk))
        {
            var threshold = AlertRules.DiskFreePercent(settings.GetInt(DefaultsKey.MonitorAlertDiskFreePercent, AlertRules.DefaultDiskFreePercent));
            if (AlertRules.LowDisk(snapshot.Disks, d => (d.Total, d.Free), threshold) is { } disk)
                Send(MonitorAlertKind.Disk, "Disk almost full", $"{disk.Label} ({disk.Name}) has less than {threshold}% free.");
        }

        if (On(DefaultsKey.MonitorAlertBattery, AppFeature.MonitorPower) && snapshot.Power is { } power)
        {
            var threshold = AlertRules.BatteryPercent(settings.GetInt(DefaultsKey.MonitorAlertBatteryPercent, AlertRules.DefaultBatteryPercent));
            if (AlertRules.LowBattery(power.HasBattery, power.IsCharging, power.Percent, threshold) is { } charge)
                Send(MonitorAlertKind.Battery, "Battery is low", $"{charge}% left. Plug in the charger.");
        }
    }

    private void Send(MonitorAlertKind kind, string title, string body)
    {
        var now = DateTime.UtcNow;
        var cooldown = AlertRules.CooldownMinutes(settings.GetInt(DefaultsKey.MonitorAlertCooldownMinutes, AlertRules.DefaultCooldownMinutes));
        if (!AlertRules.CooldownPassed(_lastSent.TryGetValue(kind, out var last) ? last : null, now, cooldown)) return;
        _lastSent[kind] = now;
        Log.Information("Monitor alert: {Kind}", kind);
        notify(title, body);
        Alert?.Invoke(kind, title, body);
    }

    public void Dispose()
    {
        if (_attached) monitor.Updated -= Evaluate;
        _attached = false;
    }
}

/// <summary>A monitor feature's binding: one per feature, so installing or removing one re-plans what is sampled (planDidChange).</summary>
public sealed class MonitorFeatureService(SystemMonitor monitor, MonitorAlertService alerts) : Shell.IFeatureService
{
    public void Sync()
    {
        monitor.PlanChanged();
        alerts.Sync();
    }

    public void Dispose() => monitor.PlanChanged();
}
