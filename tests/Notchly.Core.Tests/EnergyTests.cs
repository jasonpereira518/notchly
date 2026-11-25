// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream's keep awake, brightness and Bluetooth-on-sleep specs: PreferencesFeatureTests (keep awake
// defaults and automation), KeepAwakeTimerHandoffTests, NotchActivityTests, KeepAwakeLidSleepTests,
// KeepAwakeDimmingTests, FeatureCatalogTests (brightness and Bluetooth), BrightnessStepTests.
using Notchly.Core.Energy;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using static Notchly.Core.Energy.KeepAwakeAutomation;

namespace Notchly.Core.Tests;

public class EnergyTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    // MARK: Defaults

    [Fact]
    public void KeepAwakeDefaultsMatchUpstream()
    {
        var s = NewStore();
        Assert.Equal(0, s.GetInt(DefaultsKey.DefaultDurationMinutes, -1));
        Assert.Equal(10, s.GetInt(DefaultsKey.BatteryLimitPercent, -1));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeAutoStart));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeAllowDisplaySleep));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeExternalDisplay));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeConnectedToPower));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeRunningApps));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeAutomationRequireAll));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakePauseWhenLocked));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeSwitchUsesUntil));
        Assert.Equal(0.0, s.GetDouble(DefaultsKey.KeepAwakeUntilTime, -1));
        Assert.False(s.GetBool(DefaultsKey.KeepAwakeMouseJiggleEnabled));
        Assert.Equal(5, s.GetInt(DefaultsKey.KeepAwakeMouseJiggleIntervalMinutes, -1));
        Assert.True(s.GetBool(DefaultsKey.HotkeyEnabled));
        Assert.False(s.GetBool(DefaultsKey.ClamshellPreferred));
        Assert.False(s.GetBool(DefaultsKey.DimScreenOnLidClose));
        Assert.False(s.GetBool(DefaultsKey.NotchKeepAwakeActivity));
    }

    [Fact]
    public void BrightnessAndBluetoothDefaultsMatchUpstream()
    {
        var s = NewStore();
        Assert.False(s.GetBool(DefaultsKey.BrightnessControlEnabled));
        Assert.Equal("standard", s.GetString(DefaultsKey.BrightnessKeyStep));
        Assert.False(s.GetBool(DefaultsKey.DisplayBrightnessShortcutsEnabled));
        Assert.True(s.GetBool(DefaultsKey.NotchBrightness));
        Assert.True(s.GetBool(DefaultsKey.PanelShowBrightness));
        Assert.False(s.GetBool(DefaultsKey.BluetoothSleepEnabled));
        Assert.True(s.GetBool(DefaultsKey.BluetoothSleepRestoreOnWake));
        Assert.False(s.GetBool(DefaultsKey.BluetoothSleepRestorePending));
    }

    [Fact]
    public void ShortcutDefaultsParse()
    {
        var s = NewStore();
        foreach (var key in new[] { DefaultsKey.KeepAwakeShortcut, DefaultsKey.DisplayBrightnessDecreaseShortcut, DefaultsKey.DisplayBrightnessIncreaseShortcut })
            Assert.NotNull(Shortcut.Parse(s.GetString(key)));
        Assert.Equal("Ctrl+Alt+Win+K", Shortcut.Parse(s.GetString(DefaultsKey.KeepAwakeShortcut))!.Value.DisplayText);
    }

    [Fact]
    public void MachineStateStaysOutOfBackups()
    {
        var s = NewStore();
        s.Set(DefaultsKey.KeepAwakeSessionEnd, "1;15");
        s.Set(DefaultsKey.LidActionBackup, "1;1");
        s.Set(DefaultsKey.BluetoothSleepRestorePending, true);
        s.Set(DefaultsKey.BatteryLimitPercent, 20);
        var export = SettingsBackup.Export(s);
        Assert.DoesNotContain("keepAwakeSessionEnd", export);
        Assert.DoesNotContain("lidActionBackup", export);
        Assert.DoesNotContain("bluetoothSleepRestorePending", export);
        Assert.Contains("batteryLimitPercent", export);
    }

    [Fact]
    public void AllowedValuesSanitize()
    {
        Assert.Equal(60, KeepAwakeSupport.SanitizedDuration(60));
        Assert.Equal(0, KeepAwakeSupport.SanitizedDuration(7));
        Assert.Equal(20, KeepAwakeSupport.SanitizedBatteryLimit(20));
        Assert.Equal(10, KeepAwakeSupport.SanitizedBatteryLimit(12));
        Assert.Equal(10, KeepAwakeSupport.SanitizedJiggleInterval(10));
        Assert.Equal(5, KeepAwakeSupport.SanitizedJiggleInterval(3));
        Assert.Equal([15, 30, 60, 120, 240, 480, 0], KeepAwakeSupport.ChipDurations);
    }

    // MARK: Battery

    [Fact]
    public void BatteryRuleEndsOnlyOnBatteryAtOrUnderTheLimit()
    {
        Assert.True(KeepAwakeSupport.BatteryEndsSession(true, 10, 10));
        Assert.True(KeepAwakeSupport.BatteryEndsSession(true, 4, 10));
        Assert.False(KeepAwakeSupport.BatteryEndsSession(true, 11, 10));
        Assert.False(KeepAwakeSupport.BatteryEndsSession(false, 3, 10));
        Assert.False(KeepAwakeSupport.BatteryEndsSession(true, 1, 0));
        Assert.Equal(8, KeepAwakeSupport.BatteryProtectionPercent(true, true, 8, 10));
        Assert.Null(KeepAwakeSupport.BatteryProtectionPercent(false, true, 8, 10));
        Assert.Null(KeepAwakeSupport.BatteryProtectionPercent(true, false, 8, 10));
    }

    [Fact]
    public void JiggleToleranceIsATenthCappedAtTenSeconds()
    {
        Assert.Equal(6, KeepAwakeSupport.JiggleToleranceSeconds(1), 6);
        Assert.Equal(10, KeepAwakeSupport.JiggleToleranceSeconds(5), 6);
        Assert.Equal(10, KeepAwakeSupport.JiggleToleranceSeconds(15), 6);
    }

    // MARK: Until and extend

    [Fact]
    public void UntilTimeLandsTomorrowWhenItHasPassed()
    {
        var now = new DateTime(2026, 11, 25, 10, 0, 30);
        Assert.Equal(new DateTime(2026, 11, 26, 10, 0, 0), KeepAwakeSupport.ResolveUntil(now, 10, 0));
        Assert.Equal(new DateTime(2026, 11, 25, 10, 1, 0), KeepAwakeSupport.ResolveUntil(now, 10, 1));
        Assert.Equal(new DateTime(2026, 11, 26, 9, 0, 0), KeepAwakeSupport.ResolveUntil(now, 9, 0));
        Assert.Equal(new DateTime(2026, 11, 25, 23, 59, 0), KeepAwakeSupport.ResolveUntil(now, 23, 59));
    }

    [Fact]
    public void ExtendAddsToWhicheverIsLater()
    {
        var now = new DateTime(2026, 11, 25, 10, 0, 0);
        Assert.Equal(now.AddMinutes(45), KeepAwakeSupport.ExtendedEnd(now.AddMinutes(30), now, 15));
        Assert.Equal(now.AddMinutes(30), KeepAwakeSupport.ExtendedEnd(now.AddMinutes(-5), now, 30));
    }

    // MARK: Saved session

    [Fact]
    public void ASavedSessionResumesWithItsRemainingTime()
    {
        var stored = KeepAwakeSupport.StoredSession.Parse(new KeepAwakeSupport.StoredSession(2000, 15).Storage);
        Assert.Equal(new KeepAwakeSupport.StoredSession(2000, 15), stored);
        var timed = KeepAwakeSupport.Restore(stored, 1500);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Timed, timed.Kind);
        Assert.Equal(500, timed.RemainingSeconds, 6);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Drop, KeepAwakeSupport.Restore(stored, 2000).Kind);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Drop, KeepAwakeSupport.Restore(stored, 2500).Kind);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Indefinite, KeepAwakeSupport.Restore(new KeepAwakeSupport.StoredSession(0, null), 99).Kind);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Drop, KeepAwakeSupport.Restore(null, 99).Kind);
        // Out of range and unreadable values are dropped.
        Assert.Equal(KeepAwakeSupport.RestoreKind.Drop, KeepAwakeSupport.Restore(new KeepAwakeSupport.StoredSession(1e12, null), 99).Kind);
        Assert.Equal(KeepAwakeSupport.RestoreKind.Drop, KeepAwakeSupport.Restore(new KeepAwakeSupport.StoredSession(-5, null), 99).Kind);
        Assert.Null(KeepAwakeSupport.StoredSession.Parse("nonsense"));
        Assert.Null(KeepAwakeSupport.StoredSession.Parse(""));
    }

    [Fact]
    public void OnlyManualSessionsPersistAndOnlyTimerAndBatteryNotify()
    {
        Assert.True(KeepAwakeSupport.ShouldPersist(KeepAwakeSupport.SessionTrigger.Manual));
        Assert.False(KeepAwakeSupport.ShouldPersist(KeepAwakeSupport.SessionTrigger.Launch));
        Assert.False(KeepAwakeSupport.ShouldPersist(KeepAwakeSupport.SessionTrigger.Automation));
        Assert.True(KeepAwakeSupport.NotifiesOnEnd(KeepAwakeSupport.EndReason.Timer));
        Assert.True(KeepAwakeSupport.NotifiesOnEnd(KeepAwakeSupport.EndReason.Battery));
        Assert.False(KeepAwakeSupport.NotifiesOnEnd(KeepAwakeSupport.EndReason.Manual));
        Assert.False(KeepAwakeSupport.NotifiesOnEnd(KeepAwakeSupport.EndReason.Quit));
    }

    // MARK: Countdown text

    [Fact]
    public void CountdownTextFormats()
    {
        Assert.Equal("∞", KeepAwakeSupport.TrayCountdown(null));
        Assert.Equal("∞", KeepAwakeSupport.TrayCountdown(double.PositiveInfinity));
        Assert.Equal("1 min", KeepAwakeSupport.TrayCountdown(5));
        Assert.Equal("45 min", KeepAwakeSupport.TrayCountdown(45 * 60));
        Assert.Equal("1:00", KeepAwakeSupport.TrayCountdown(3600));
        Assert.Equal("2:05", KeepAwakeSupport.TrayCountdown(2 * 3600 + 5 * 60));
        Assert.Equal("4:09", KeepAwakeSupport.ChipCountdown(249));
        Assert.Equal("1:00:05", KeepAwakeSupport.ChipCountdown(3605));
        Assert.Equal("1 h 05 min", KeepAwakeSupport.RemainingText(3900));
        Assert.Equal("4 min 09 s", KeepAwakeSupport.RemainingText(249));
        Assert.Equal("9 s", KeepAwakeSupport.RemainingText(9));
    }

    // MARK: Automation

    [Fact]
    public void ExternalDisplayAndAppsRules()
    {
        Assert.False(HasExternalDisplay([]));
        Assert.False(HasExternalDisplay([true]));
        Assert.True(HasExternalDisplay([true, false]));
        Assert.False(SelectedAppsAreRunning([], ["code.exe"]));
        Assert.True(SelectedAppsAreRunning(["Code.exe"], ["code.exe", "chrome.exe"]));
        Assert.False(SelectedAppsAreRunning(["steam.exe"], ["code.exe"]));
    }

    [Fact]
    public void MatchingAndEnabledConditions()
    {
        Assert.Equal([Condition.ExternalDisplay, Condition.Power, Condition.RunningApps],
            MatchingConditions(true, true, true, true, true, true));
        Assert.Equal([Condition.Power], MatchingConditions(true, false, true, true, false, true));
        // A rule that is off never matches, even when its condition is true.
        Assert.Empty(MatchingConditions(false, true, false, true, false, true));
        // "Running apps" with nothing chosen is not an enabled rule.
        Assert.Equal([Condition.ExternalDisplay], EnabledConditions(true, false, true, false));
        Assert.Equal([Condition.ExternalDisplay, Condition.RunningApps], EnabledConditions(true, false, true, true));
    }

    [Fact]
    public void AnyVersusAll()
    {
        var enabled = new[] { Condition.ExternalDisplay, Condition.Power };
        Assert.True(ConditionsSatisfied([Condition.ExternalDisplay], enabled, requireAll: false));
        Assert.False(ConditionsSatisfied([Condition.ExternalDisplay], enabled, requireAll: true));
        Assert.True(ConditionsSatisfied(enabled, enabled, requireAll: true));
        Assert.False(ConditionsSatisfied([], [], requireAll: true));
        Assert.False(ConditionsSatisfied([], enabled, requireAll: false));
    }

    [Fact]
    public void ActionTable()
    {
        // Rules hold, nothing running: start.
        Assert.Equal(AutomationAction.Activate, Action(true, true, false, false));
        // Rules hold and a session is already going: leave it.
        Assert.Equal(AutomationAction.None, Action(true, true, true, false));
        Assert.Equal(AutomationAction.None, Action(true, true, true, true));
        // Rules fail: only an automatic session ends.
        Assert.Equal(AutomationAction.Deactivate, Action(true, false, true, true));
        Assert.Equal(AutomationAction.None, Action(true, false, true, false));
        Assert.Equal(AutomationAction.None, Action(true, false, false, false));
        // The feature gone ends an automatic session whatever the rules say.
        Assert.Equal(AutomationAction.Deactivate, Action(false, true, true, true));
        Assert.Equal(AutomationAction.None, Action(false, true, false, false));
        // A manual stop of an automatic session holds until the rules clear.
        Assert.Equal(AutomationAction.None, Action(true, true, false, false, suppressed: true));
        Assert.True(StillSuppressed(true, true));
        Assert.False(StillSuppressed(true, false));
        Assert.False(StillSuppressed(false, true));
    }

    [Fact]
    public void UnpluggingEndsAnAllSessionButNotAnAnySession()
    {
        var enabled = EnabledConditions(true, true, false, false);
        var afterUnplug = MatchingConditions(true, true, true, false, false, false);
        Assert.Equal(AutomationAction.Deactivate, Action(true, ConditionsSatisfied(afterUnplug, enabled, requireAll: true), true, true));
        Assert.Equal(AutomationAction.None, Action(true, ConditionsSatisfied(afterUnplug, enabled, requireAll: false), true, true));
    }

    [Fact]
    public void TimerHandoff()
    {
        const KeepAwakeSupport.EndReason timer = KeepAwakeSupport.EndReason.Timer;
        // The rules hold when the timer ends: carry on as an automatic session.
        Assert.True(ShouldHandOver(timer, false, true, false, false, true));
        // Never for a user stop, a battery end, an automatic session, a suppressed one, a drained battery or a gone feature.
        Assert.False(ShouldHandOver(KeepAwakeSupport.EndReason.Manual, false, true, false, false, true));
        Assert.False(ShouldHandOver(KeepAwakeSupport.EndReason.Battery, false, true, false, false, true));
        Assert.False(ShouldHandOver(timer, true, true, false, false, true));
        Assert.False(ShouldHandOver(timer, false, true, true, false, true));
        Assert.False(ShouldHandOver(timer, false, true, false, true, true));
        Assert.False(ShouldHandOver(timer, false, false, false, false, true));
        Assert.False(ShouldHandOver(timer, false, true, false, false, false));
        // All needs every rule: with only one of two true, nothing carries over; Any carries over.
        var enabled = new[] { Condition.ExternalDisplay, Condition.Power };
        var matching = new[] { Condition.ExternalDisplay };
        Assert.False(ShouldHandOver(timer, false, true, false, false, ConditionsSatisfied(matching, enabled, true)));
        Assert.True(ShouldHandOver(timer, false, true, false, false, ConditionsSatisfied(matching, enabled, false)));
    }

    // MARK: Island activity

    [Fact]
    public void CompactTextRoundsUpAndSwitchesToHours()
    {
        Assert.Equal("30m", NotchKeepAwakeSupport.CompactText(30 * 60));
        Assert.Equal("30m", NotchKeepAwakeSupport.CompactText(30 * 60 - 0.5));
        Assert.Equal("30m", NotchKeepAwakeSupport.CompactText(29 * 60 + 1));
        Assert.Equal("29m", NotchKeepAwakeSupport.CompactText(29 * 60));
        Assert.Equal("1h00", NotchKeepAwakeSupport.CompactText(3600));
        Assert.Equal("1h01", NotchKeepAwakeSupport.CompactText(3601));
        Assert.Equal("8h00", NotchKeepAwakeSupport.CompactText(28800));
        Assert.Equal("26h05", NotchKeepAwakeSupport.CompactText(26 * 3600 + 5 * 60));
        Assert.Equal("59m", NotchKeepAwakeSupport.CompactText(59 * 60));
        Assert.Equal("1m", NotchKeepAwakeSupport.CompactText(1));
        Assert.Equal("1m", NotchKeepAwakeSupport.CompactText(0));
        Assert.Equal("1m", NotchKeepAwakeSupport.CompactText(-30));
        Assert.Equal("1m", NotchKeepAwakeSupport.CompactText(double.PositiveInfinity));
        Assert.Equal("1m", NotchKeepAwakeSupport.CompactText(double.NaN));
    }

    [Fact]
    public void TheStripTicksOncePerMinuteOnTheMinute()
    {
        foreach (var remaining in new[] { 1.0, 59.5, 60, 61, 1799.5, 3600, 3601, 28800 })
        {
            const double end = 100000;
            var now = end - remaining;
            var start = NotchKeepAwakeSupport.TickStart(end, now);
            var next = start + 60;
            Assert.True(start <= now, $"start {remaining}");
            Assert.True(next > now, $"next {remaining}");
            var text = NotchKeepAwakeSupport.CompactText(end - now);
            // Stable until just before the next tick, one less at the tick.
            Assert.Equal(text, NotchKeepAwakeSupport.CompactText(end - (next - 0.001)));
            var minutes = NotchKeepAwakeSupport.MinutesLeft(end - now);
            if (minutes > 1) Assert.Equal(minutes - 1, NotchKeepAwakeSupport.MinutesLeft(end - next));
        }
    }

    [Fact]
    public void TheActivityShowsOnlyWhenEverythingIsOn()
    {
        Assert.True(NotchKeepAwakeSupport.ShowsActivity(true, true, true));
        Assert.False(NotchKeepAwakeSupport.ShowsActivity(true, true, false));
        Assert.False(NotchKeepAwakeSupport.ShowsActivity(true, false, true));
        Assert.False(NotchKeepAwakeSupport.ShowsActivity(false, true, true));
    }

    // MARK: Lid

    [Fact]
    public void SleepAfterTheLidActionIsBackNeedsAClosedLidAndKnownFacts()
    {
        Assert.True(LidRules.SleepIsAllowed(true, false, true));
        Assert.True(LidRules.SleepIsAllowed(true, false, false));
        // Docked on mains power stays running; on battery it sleeps.
        Assert.False(LidRules.SleepIsAllowed(true, true, true));
        Assert.True(LidRules.SleepIsAllowed(true, true, false));
        // Unknown values never authorise it.
        Assert.False(LidRules.SleepIsAllowed(null, false, true));
        Assert.False(LidRules.SleepIsAllowed(true, null, false));
        Assert.False(LidRules.SleepIsAllowed(true, true, null));
        Assert.False(LidRules.SleepIsAllowed(false, false, false));
        Assert.Equal(10, LidRules.SleepRetries);
    }

    [Fact]
    public void LidDimmingSavesAndRestores()
    {
        Assert.Equal(new LidRules.DimAction(LidRules.DimKind.Dim, 0.6), LidRules.LidClosed(0.6));
        Assert.Equal(LidRules.DimKind.None, LidRules.LidClosed(null).Kind);
        Assert.Equal(LidRules.DimKind.None, LidRules.LidClosed(0).Kind);
        Assert.Equal(new LidRules.DimAction(LidRules.DimKind.Restore, 0.6), LidRules.Restoring(0.6));
        Assert.Equal(LidRules.DimKind.None, LidRules.Restoring(null).Kind);
        Assert.Equal(LidRules.DimKind.None, LidRules.Restoring(0).Kind);
    }

    [Fact]
    public void TheLidActionBackupRoundTripsAndRejectsGarbage()
    {
        var backup = new LidRules.LidActionBackup(1, 2);
        Assert.Equal(backup, LidRules.LidActionBackup.Parse(backup.Storage));
        Assert.Null(LidRules.LidActionBackup.Parse(""));
        Assert.Null(LidRules.LidActionBackup.Parse("1"));
        Assert.Null(LidRules.LidActionBackup.Parse("1;x"));
        Assert.Null(LidRules.LidActionBackup.Parse("9;1"));
    }

    // MARK: Brightness

    [Fact]
    public void KeyStepsParseAndLimit()
    {
        Assert.Equal(BrightnessKeyStep.Standard, BrightnessSupport.ParseStep(null));
        Assert.Equal(BrightnessKeyStep.Standard, BrightnessSupport.ParseStep("nonsense"));
        Assert.Equal(BrightnessKeyStep.Half, BrightnessSupport.ParseStep("half"));
        Assert.Equal(BrightnessKeyStep.Quarter, BrightnessSupport.ParseStep("quarter"));
        Assert.Equal(1.0 / 16, BrightnessSupport.Fraction(BrightnessKeyStep.Standard));
        Assert.Equal(1.0 / 32, BrightnessSupport.Fraction(BrightnessKeyStep.Half));
        Assert.Equal(1.0 / 64, BrightnessSupport.Fraction(BrightnessKeyStep.Quarter));
        Assert.Equal(1.0 / 32, BrightnessSupport.Limited(BrightnessKeyStep.Half, 1.0 / 16), 9);
        Assert.Equal(-1.0 / 64, BrightnessSupport.Limited(BrightnessKeyStep.Quarter, -1.0 / 16), 9);
        // A finer press is never made coarser.
        Assert.Equal(1.0 / 64, BrightnessSupport.Limited(BrightnessKeyStep.Standard, 1.0 / 64), 9);
        Assert.Equal("quarter", BrightnessSupport.StepName(BrightnessKeyStep.Quarter));
    }

    [Fact]
    public void SteppingClampsAndPercentRounds()
    {
        Assert.Equal(1.0, BrightnessSupport.SteppedLevel(0.97, 1.0 / 16));
        Assert.Equal(0.0, BrightnessSupport.SteppedLevel(0.03, -1.0 / 16));
        Assert.Equal(0.5625, BrightnessSupport.SteppedLevel(0.5, 0.0625), 9);
        Assert.Equal(50, BrightnessSupport.WholePercent(0.5));
        Assert.Equal(0, BrightnessSupport.WholePercent(double.NaN));
        Assert.Equal(100, BrightnessSupport.WholePercent(1.7));
        Assert.Equal(0, BrightnessSupport.FilledSegments(0));
        Assert.Equal(1, BrightnessSupport.FilledSegments(0.001));
        Assert.Equal(16, BrightnessSupport.FilledSegments(1));
        Assert.Equal(8, BrightnessSupport.FilledSegments(0.5));
        Assert.Equal(9, BrightnessSupport.FilledSegments(0.51));
    }

    [Fact]
    public void ExtendedDimmingSplitsTheBottomQuarter()
    {
        Assert.Equal(new BrightnessSupport.Components(0, 0), BrightnessSupport.ExtendedDimming(0));
        Assert.Equal(new BrightnessSupport.Components(0, 1), BrightnessSupport.ExtendedDimming(0.25));
        Assert.Equal(0.5, BrightnessSupport.ExtendedDimming(0.625).Hardware, 9);
        Assert.Equal(1, BrightnessSupport.ExtendedDimming(0.625).Picture);
        Assert.Equal(new BrightnessSupport.Components(1, 1), BrightnessSupport.ExtendedDimming(1));
        Assert.Equal(0.5, BrightnessSupport.ExtendedDimming(0.125).Picture, 9);
        // The inverse maps back.
        foreach (var level in new[] { 0.0, 0.1, 0.25, 0.4, 0.625, 1.0 })
        {
            var parts = BrightnessSupport.ExtendedDimming(level);
            Assert.Equal(level, BrightnessSupport.LevelFrom(parts.Hardware, parts.Picture), 9);
        }
    }

    [Fact]
    public void SoftwareDimmingRules()
    {
        Assert.Equal(0.25, BrightnessSupport.ReconnectedDimLevel(0.0));
        Assert.Equal(0.25, BrightnessSupport.ReconnectedDimLevel(0.1));
        Assert.Equal(0.7, BrightnessSupport.ReconnectedDimLevel(0.7));
        Assert.Equal(1.0, BrightnessSupport.ReconnectedDimLevel(1.4));
        Assert.Equal(1.0, BrightnessSupport.SoftwareDimToRestore(0.7, false));
        Assert.Equal(0.4, BrightnessSupport.SoftwareDimToRestore(0.4, true));
        Assert.Equal(1.0, BrightnessSupport.SoftwareDimFactor(1.4));
        Assert.Equal(0.0, BrightnessSupport.SoftwareDimFactor(-0.3));
        Assert.Equal(0.5, BrightnessSupport.SoftwareDimFactor(0.5));
        // The overlay is clear at full and never fully opaque.
        Assert.Equal(0.0, BrightnessSupport.OverlayOpacity(1));
        Assert.Equal(0.5, BrightnessSupport.OverlayOpacity(0.5), 9);
        Assert.Equal(BrightnessSupport.MaxOverlayOpacity, BrightnessSupport.OverlayOpacity(0));
    }

    [Fact]
    public void DdcValueMath()
    {
        Assert.Equal(80, BrightnessSupport.DeviceValue(0.8, 100));
        Assert.Equal(40, BrightnessSupport.DeviceValue(0.8, 50));
        // A monitor reporting no maximum is 0 to 100.
        Assert.Equal(60, BrightnessSupport.DeviceValue(0.6, 0));
        Assert.Equal(0.8, BrightnessSupport.Normalized(80, 100), 9);
        Assert.Equal(1.0, BrightnessSupport.Normalized(120, 100));
        Assert.Equal(0.5, BrightnessSupport.Normalized(50, 0), 9);
    }

    [Fact]
    public void RememberedLevelsAreTrustedBriefly()
    {
        Assert.True(BrightnessSupport.TrustsRememberedLevel(0));
        Assert.True(BrightnessSupport.TrustsRememberedLevel(3));
        Assert.False(BrightnessSupport.TrustsRememberedLevel(3.1));
        Assert.False(BrightnessSupport.TrustsRememberedLevel(-1));
        Assert.False(BrightnessSupport.TrustsRememberedLevel(null));
        Assert.True(BrightnessSupport.ShouldRememberInternalLevel(0.4));
        Assert.False(BrightnessSupport.ShouldRememberInternalLevel(0));
        Assert.False(BrightnessSupport.ShouldRememberInternalLevel(null));
    }

    [Fact]
    public void ShortcutsActOnTheRightDisplay()
    {
        string[] eligible = ["a", "b"];
        Assert.Equal("b", BrightnessSupport.ShortcutDisplay(true, "b", "a", eligible));
        Assert.Equal("a", BrightnessSupport.ShortcutDisplay(false, "b", "a", eligible));
        Assert.Equal("a", BrightnessSupport.ShortcutDisplay(true, null, "a", eligible));
        Assert.Null(BrightnessSupport.ShortcutDisplay(true, "c", "a", eligible));
        Assert.Null(BrightnessSupport.ShortcutDisplay(false, "a", null, eligible));
    }

    // MARK: Bluetooth

    [Fact]
    public void BluetoothPutsBackOnlyWhatItTook()
    {
        Assert.Equal(new BluetoothSleepSupport.Plan(true, true), BluetoothSleepSupport.SleepPlan(true, true));
        Assert.Equal(new BluetoothSleepSupport.Plan(true, false), BluetoothSleepSupport.SleepPlan(true, false));
        Assert.Equal(new BluetoothSleepSupport.Plan(false, false), BluetoothSleepSupport.SleepPlan(false, true));
        Assert.True(BluetoothSleepSupport.Restores(true, () => false));
        Assert.False(BluetoothSleepSupport.Restores(false, () => false));
        Assert.False(BluetoothSleepSupport.Restores(true, () => true));
    }

    [Fact]
    public void ALaunchThatOwesNothingNeverReadsTheRadio()
    {
        var reads = 0;
        Assert.False(BluetoothSleepSupport.Restores(false, () => { reads++; return false; }));
        Assert.Equal(0, reads);
        Assert.True(BluetoothSleepSupport.Restores(true, () => { reads++; return false; }));
        Assert.Equal(1, reads);
    }
}
