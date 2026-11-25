// SPDX-License-Identifier: GPL-3.0-or-later
// What the monitor's readings say, in words and numbers, shared by the island page, the panel and the tray readouts.
using Notchly.Core.Monitor;
using Notchly.Core.Power;
using Notchly.Core.Settings;
using Notchly.App.Platform.Monitor;

namespace Notchly.App.Monitor;

internal static class MonitorText
{
    public static bool InBits(SettingsStore settings) => MetricFormat.InBits(settings.GetString(DefaultsKey.NetworkSpeedUnit));

    public static string MemoryValue(MemoryReading memory, SettingsStore settings)
    {
        var shown = MetricFormat.SelectedMemory(memory.Used, memory.App, settings.GetString(DefaultsKey.MonitorMemoryMetric));
        return MetricFormat.Bytes(shown);
    }

    public static double MemoryFraction(MemoryReading memory, SettingsStore settings) =>
        memory.Total == 0 ? 0 : (double)MetricFormat.SelectedMemory(memory.Used, memory.App, settings.GetString(DefaultsKey.MonitorMemoryMetric)) / memory.Total;

    public static string Pressure(MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Normal => "Normal", MemoryPressure.Warning => "Warning", MemoryPressure.Critical => "Critical", _ => "Unknown",
    };

    public static string BatteryState(PowerReading power) => BatteryRules.State(power.IsCharging, power.ExternalConnected, power.HasBattery) switch
    {
        BatteryPowerState.Charging => "Charging",
        BatteryPowerState.ExternalPower => "Plugged in",
        BatteryPowerState.OnBattery => RemainingText(power) ?? "On battery",
        _ => "No battery",
    };

    public static string? RemainingText(PowerReading power)
    {
        var seconds = BatteryRules.RemainingSeconds(power.TimeToEmptyMinutes, power.ExternalConnected, power.IsCharging);
        return seconds is { } s && BatteryRules.FormattedTime(s) is { } text ? $"{text} left" : null;
    }

    /// <summary>The battery's own flow (Windows reports nothing about the rest of the PC's draw).</summary>
    public static string PowerFlow(PowerReading power) =>
        BatteryDetails.Flow(new BatteryReading(power.Percent, power.ExternalConnected, power.IsCharging, power.RateMilliwatts, power.RemainingMilliwattHours,
            power.FullMilliwattHours, power.DesignMilliwattHours));

    /// <summary>The power card: the battery's watts and which way they flow, or what it is doing when nothing flows.</summary>
    public static (string Value, string? Detail) PowerCard(PowerReading power)
    {
        if (power.RateMilliwatts is { } rate && Math.Abs(rate) >= BatteryDetails.IdleMilliwatts)
            return (BatteryDetails.Watts(rate), rate > 0 ? "charging" : "in use");
        return ("Idle", power.ExternalConnected ? "on charger" : "on battery");
    }

    public static string ShortAdapter(string name) =>
        name.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.Ordinal).Replace("Laptop GPU", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ").Trim();

    public static DiskVolume? SystemVolume(IReadOnlyList<DiskVolume> volumes) => volumes.FirstOrDefault(v => v.IsSystem) ?? volumes.FirstOrDefault();
}
