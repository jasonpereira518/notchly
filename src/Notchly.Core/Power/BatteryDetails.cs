// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: what the home page's battery panel says. Upstream shows power on its System page
// from the Mac's SMC (system draw, adapter input); Windows reports only the battery's own flow
// and capacities, so the panel says what goes into or out of the battery and nothing it can't read.
using System.Globalization;

namespace Notchly.Core.Power;

/// <summary>
/// One battery reading. `RateMilliwatts` is positive while charging and negative while the
/// battery is in use; the capacities are in milliwatt-hours. Any of them may be unknown.
/// </summary>
public readonly record struct BatteryReading(
    int Percent, bool PluggedIn, bool Charging,
    int? RateMilliwatts = null, int? RemainingMilliwattHours = null,
    int? FullMilliwattHours = null, int? DesignMilliwattHours = null);

/// <summary>The processor's power in milliwatts: the whole package, and its cores and integrated graphics where reported.</summary>
public readonly record struct ChipPower(int PackageMilliwatts, int? CoresMilliwatts = null, int? GraphicsMilliwatts = null);

public static class BatteryDetails
{
    /// <summary>Below this the battery is neither charging nor in use as far as anyone can tell.</summary>
    public const int IdleMilliwatts = 100;

    public static string Watts(int milliwatts) =>
        (Math.Abs(milliwatts) / 1000.0).ToString(Math.Abs(milliwatts) >= 20_000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " W";

    /// <summary>The battery's flow: "Charging at 45 W", "Using 12.4 W", or the charger's state.</summary>
    public static string Flow(BatteryReading r)
    {
        if (r.RateMilliwatts is { } rate && Math.Abs(rate) >= IdleMilliwatts)
            return rate > 0 ? $"Charging at {Watts(rate)}" : $"Using {Watts(rate)}";
        if (r.PluggedIn) return r.Charging ? "Charging" : "On charger · not charging";
        return "On battery";
    }

    /// <summary>The flow in a word or two for the chip: "+45 W", "−12.4 W"; null when idle or unknown.</summary>
    public static string? ShortFlow(BatteryReading r) =>
        r.RateMilliwatts is { } rate && Math.Abs(rate) >= IdleMilliwatts ? (rate > 0 ? "+" : "−") + Watts(rate) : null;

    /// <summary>"Full in 1 h 5 min" while charging, "3 h 10 min left" in use; null when it can't be said.</summary>
    public static string? TimeLeft(BatteryReading r)
    {
        if (r.RateMilliwatts is not { } rate || Math.Abs(rate) < IdleMilliwatts || r.RemainingMilliwattHours is not { } remaining) return null;
        if (rate > 0)
        {
            if (r.FullMilliwattHours is not { } full || full <= remaining) return null;
            return $"Full in {Duration((full - remaining) / (double)rate)}";
        }
        return $"{Duration(remaining / (double)-rate)} left";
    }

    /// <summary>"51.2 of 59.3 Wh"; null without both capacities.</summary>
    public static string? Charge(BatteryReading r) =>
        r.RemainingMilliwattHours is { } remaining && r.FullMilliwattHours is { } full && full > 0
            ? $"{Number(Math.Min(remaining, full))} of {WattHours(full)}"
            : null;

    /// <summary>How much the battery holds against when it was new, 0–100; null without both capacities.</summary>
    public static int? HealthPercent(BatteryReading r) =>
        r.FullMilliwattHours is { } full && r.DesignMilliwattHours is { } design && full > 0 && design > 0
            ? (int)Math.Round(Math.Min(100, full * 100.0 / design))
            : null;

    /// <summary>"71% of new (83 Wh)"; null without both capacities.</summary>
    public static string? Health(BatteryReading r) =>
        HealthPercent(r) is { } percent ? $"{percent}% of new ({WattHours(r.DesignMilliwattHours!.Value)})" : null;

    /// <summary>"Charging at 45 W" or "Using 12.4 W" for the battery alone; "Idle" when neither; null without a reading.</summary>
    public static string BatteryFlow(BatteryReading r) =>
        r.RateMilliwatts is { } rate && Math.Abs(rate) >= IdleMilliwatts
            ? rate > 0 ? $"{Watts(rate)} in" : $"{Watts(rate)} out"
            : r.PluggedIn ? (r.Charging ? "Charging" : "Not charging") : "Idle";

    /// <summary>Where the power comes from: Windows reports no wall or system figure, so the panel says so.</summary>
    public static string Wall(BatteryReading r) =>
        r.PluggedIn ? "Not reported by Windows" : "Unplugged";

    /// <summary>"31.6 W · cores 20.8 W · graphics 0.1 W"; the parts Windows did not report are left out.</summary>
    public static string Chip(ChipPower chip)
    {
        var text = Watts(chip.PackageMilliwatts);
        if (chip.CoresMilliwatts is { } cores) text += $" · cores {Watts(cores)}";
        if (chip.GraphicsMilliwatts is { } graphics && graphics >= IdleMilliwatts) text += $" · graphics {Watts(graphics)}";
        return text;
    }

    /// <summary>The panel's rows, in order, leaving out what Windows did not report.</summary>
    public static IReadOnlyList<(string Label, string Value)> Rows(BatteryReading r, ChipPower? chip = null)
    {
        var rows = new List<(string, string)> { ("Wall", Wall(r)), ("Battery", BatteryFlow(r)) };
        // On the battery alone, what leaves it is what everything draws.
        if (!r.PluggedIn && r.RateMilliwatts is { } rate && rate <= -IdleMilliwatts) rows.Add(("System", Watts(rate)));
        if (chip is { } c) rows.Add(("Chip", Chip(c)));
        if (TimeLeft(r) is { } time) rows.Add(("Time", time));
        if (Charge(r) is { } charge) rows.Add(("Charge", charge));
        if (Health(r) is { } health) rows.Add(("Health", health));
        return rows;
    }

    private static string Number(int milliwattHours) => (milliwattHours / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

    private static string WattHours(int milliwattHours) => Number(milliwattHours) + " Wh";

    /// <summary>Hours as "2 h 5 min", "45 min", or "1 min" at the least; past two days, "over 48 h".</summary>
    public static string Duration(double hours)
    {
        if (double.IsNaN(hours) || hours < 0) return "—";
        if (hours > 48) return "over 48 h";
        var minutes = Math.Max(1, (int)Math.Round(hours * 60));
        return minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }
}
