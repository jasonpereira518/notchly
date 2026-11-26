// SPDX-License-Identifier: GPL-3.0-or-later
// The home page's battery panel and speaker menu.
using Notchly.Core.Audio;
using Notchly.Core.Power;

namespace Notchly.Core.Tests;

public class HomeControlsTests
{
    [Fact]
    public void ChargingSaysWhatGoesIn()
    {
        var r = new BatteryReading(40, true, true, RateMilliwatts: 45_210, RemainingMilliwattHours: 24_000, FullMilliwattHours: 59_270);
        Assert.Equal("Charging at 45 W", BatteryDetails.Flow(r));
        Assert.Equal("+45 W", BatteryDetails.ShortFlow(r));
        // 35.27 Wh to go at 45.21 W: 47 minutes.
        Assert.Equal("Full in 47 min", BatteryDetails.TimeLeft(r));
    }

    [Fact]
    public void InUseSaysWhatComesOut()
    {
        var r = new BatteryReading(80, false, false, RateMilliwatts: -12_400, RemainingMilliwattHours: 47_000, FullMilliwattHours: 59_270);
        Assert.Equal("Using 12.4 W", BatteryDetails.Flow(r));
        Assert.Equal("−12.4 W", BatteryDetails.ShortFlow(r));
        Assert.Equal("3 h 47 min left", BatteryDetails.TimeLeft(r));
    }

    [Fact]
    public void AFullBatteryOnTheChargerIsIdle()
    {
        var r = new BatteryReading(100, true, false, RateMilliwatts: 0, RemainingMilliwattHours: 59_270, FullMilliwattHours: 59_270, DesignMilliwattHours: 83_000);
        Assert.Equal("On charger · not charging", BatteryDetails.Flow(r));
        Assert.Null(BatteryDetails.ShortFlow(r));
        Assert.Null(BatteryDetails.TimeLeft(r));
        Assert.Equal(71, BatteryDetails.HealthPercent(r));
        Assert.Equal(["Wall", "Battery", "Charge", "Health"], BatteryDetails.Rows(r).Select(row => row.Label));
        Assert.Equal("59.3 of 59.3 Wh", BatteryDetails.Charge(r));
        Assert.Equal("71% of new (83 Wh)", BatteryDetails.Health(r));
    }

    [Fact]
    public void OnTheBatteryWhatLeavesItIsTheSystemsDraw()
    {
        var r = new BatteryReading(80, false, false, RateMilliwatts: -12_400);
        var rows = BatteryDetails.Rows(r, new ChipPower(7_300, 5_100, 50));
        Assert.Equal([("Wall", "Unplugged"), ("Battery", "12.4 W out"), ("System", "12.4 W"), ("Chip", "7.3 W · cores 5.1 W")], rows);
    }

    [Fact]
    public void OnTheChargerTheWallAndSystemAreNotGuessed()
    {
        var r = new BatteryReading(60, true, true, RateMilliwatts: 45_000);
        var rows = BatteryDetails.Rows(r, new ChipPower(31_555, 20_766, 57));
        Assert.Equal(("Wall", "Not reported by Windows"), rows[0]);
        Assert.Equal(("Battery", "45 W in"), rows[1]);
        Assert.DoesNotContain(rows, row => row.Label == "System");
        Assert.Equal("32 W · cores 21 W", BatteryDetails.Chip(new ChipPower(31_555, 20_766, 57)));
    }

    [Fact]
    public void WhatWindowsDidNotReportIsLeftOut()
    {
        var r = new BatteryReading(55, false, false);
        Assert.Equal("On battery", BatteryDetails.Flow(r));
        Assert.Equal([("Wall", "Unplugged"), ("Battery", "Idle")], BatteryDetails.Rows(r));
    }

    [Fact]
    public void HealthNeverPassesFull()
    {
        var r = new BatteryReading(100, true, false, FullMilliwattHours: 86_000, DesignMilliwattHours: 83_000);
        Assert.Equal(100, BatteryDetails.HealthPercent(r));
    }

    [Theory]
    [InlineData(0.004, "1 min")]
    [InlineData(0.75, "45 min")]
    [InlineData(2.0, "2 h")]
    [InlineData(2.0834, "2 h 5 min")]
    [InlineData(60, "over 48 h")]
    public void Durations(double hours, string text) => Assert.Equal(text, BatteryDetails.Duration(hours));

    [Fact]
    public void SpeakersAreListedByNameWithTheCurrentOneTicked()
    {
        var choices = AudioOutputMenu.Choices(
            [new("{b}", "Speaker (Realtek(R) Audio)"), new("{a}", "Headphones (AirPods)"), new("{b}", "duplicate")], "{B}");
        Assert.Equal(["Headphones (AirPods)", "Speaker (Realtek(R) Audio)"], choices.Select(c => c.Name));
        Assert.Equal([false, true], choices.Select(c => c.Current));
    }
}
