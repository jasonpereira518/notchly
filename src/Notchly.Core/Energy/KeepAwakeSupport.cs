// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/KeepAwakeManager.swift (the pure parts), KeepAwakeAutomationSupport.swift
// and Services/Notch/NotchKeepAwakeSupport.swift: allowed values, the battery rule, "until" times, countdown text,
// the automation table and the island's compact activity, kept apart from the power calls so tests can pin them down.
// Windows differences: running-app rules match executable names (upstream matches bundle ids), and the stored
// "until" time is Unix seconds.
using System.Globalization;

namespace Notchly.Core.Energy;

public static class KeepAwakeSupport
{
    public static readonly int[] AllowedDurations = [0, 15, 30, 60, 120, 240, 480];
    public static readonly int[] AllowedBatteryLimits = [0, 5, 10, 15, 20];
    public static readonly int[] AllowedJiggleIntervals = [1, 2, 5, 10, 15];
    /// <summary>The order of the duration chips; 0 is the infinity chip.</summary>
    public static readonly int[] ChipDurations = [15, 30, 60, 120, 240, 480, 0];
    public static readonly int[] ExtendChoices = [15, 30, 60];

    public const int DefaultDurationMinutes = 0;
    public const int DefaultBatteryLimit = 10;
    public const int DefaultJiggleMinutes = 5;
    /// <summary>The battery is checked this often while a session runs.</summary>
    public const int BatteryWatchSeconds = 30;
    /// <summary>A stored session further away than this is a corrupt value and is dropped.</summary>
    public const double MaxStoredSeconds = 31 * 24 * 3600.0;

    public static int SanitizedDuration(int minutes) => AllowedDurations.Contains(minutes) ? minutes : DefaultDurationMinutes;
    public static int SanitizedBatteryLimit(int percent) => AllowedBatteryLimits.Contains(percent) ? percent : DefaultBatteryLimit;
    public static int SanitizedJiggleInterval(int minutes) => AllowedJiggleIntervals.Contains(minutes) ? minutes : DefaultJiggleMinutes;

    // MARK: Battery

    /// <summary>On battery and at or under the limit ends a session. A limit of 0 never does.</summary>
    public static bool BatteryEndsSession(bool onBattery, int percent, int limit) => limit > 0 && onBattery && percent <= limit;

    /// <summary>The percentage to show in the "plug in or lower the limit" hint while a start would be refused.</summary>
    public static int? BatteryProtectionPercent(bool hasBattery, bool onBattery, int percent, int limit) =>
        hasBattery && BatteryEndsSession(onBattery, percent, limit) ? percent : null;

    /// <summary>Jiggle tolerance: the smaller of 10 s and a tenth of the interval.</summary>
    public static double JiggleToleranceSeconds(int intervalMinutes) => Math.Min(10, intervalMinutes * 60 * 0.1);

    // MARK: Until and extend

    /// <summary>
    /// The next occurrence of a wall-clock time that is still ahead: a time that has just passed lands tomorrow,
    /// never "right now".
    /// </summary>
    public static DateTime ResolveUntil(DateTime now, int hour, int minute)
    {
        var candidate = new DateTime(now.Year, now.Month, now.Day, Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0, now.Kind);
        while (candidate <= now) candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>+15, +30 or +60 minutes on top of whichever is later, the end or now.</summary>
    public static DateTime ExtendedEnd(DateTime end, DateTime now, int minutes) => (end > now ? end : now).AddMinutes(minutes);

    // MARK: Persisted session

    /// <summary>What a session saved at quit looks like: an end instant (0 for indefinite) and the minutes picked.</summary>
    public readonly record struct StoredSession(double EndUnixSeconds, int? Minutes)
    {
        public string Storage => EndUnixSeconds.ToString("R", CultureInfo.InvariantCulture) + ";" + (Minutes?.ToString(CultureInfo.InvariantCulture) ?? "");

        public static StoredSession? Parse(string? storage)
        {
            if (string.IsNullOrWhiteSpace(storage)) return null;
            var parts = storage.Split(';');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) || !double.IsFinite(end)) return null;
            int? minutes = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
            return new StoredSession(end, minutes);
        }
    }

    public enum RestoreKind { Drop, Indefinite, Timed }

    public readonly record struct RestoredSession(RestoreKind Kind, double RemainingSeconds = 0);

    /// <summary>A timed session resumes with its remaining time; one whose end passed, or that is out of range, is dropped.</summary>
    public static RestoredSession Restore(StoredSession? stored, double nowUnixSeconds)
    {
        if (stored is not { } s) return new RestoredSession(RestoreKind.Drop);
        if (s.EndUnixSeconds == 0) return new RestoredSession(RestoreKind.Indefinite);
        var remaining = s.EndUnixSeconds - nowUnixSeconds;
        if (s.EndUnixSeconds < 0 || remaining <= 0 || remaining > MaxStoredSeconds) return new RestoredSession(RestoreKind.Drop);
        return new RestoredSession(RestoreKind.Timed, remaining);
    }

    public enum SessionTrigger { Manual, Launch, Automation }
    public enum EndReason { Manual, Timer, Battery, Quit }

    /// <summary>Only a session the user started is saved across quits.</summary>
    public static bool ShouldPersist(SessionTrigger trigger) => trigger == SessionTrigger.Manual;

    /// <summary>The timer and the battery ending a session are worth telling the user about; the rest they did themselves.</summary>
    public static bool NotifiesOnEnd(EndReason reason) => reason is EndReason.Timer or EndReason.Battery;

    // MARK: Countdown text

    /// <summary>The tray tooltip: "H:MM" from an hour up, otherwise "N min" (at least 1), and infinity for no end.</summary>
    public static string TrayCountdown(double? remainingSeconds)
    {
        if (remainingSeconds is not { } secs || !double.IsFinite(secs)) return "∞";
        var minutes = Math.Max(1, (int)Math.Ceiling(secs / 60));
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00}" : $"{minutes} min";
    }

    /// <summary>The chip countdown: "M:SS", or "H:MM:SS" from an hour up.</summary>
    public static string ChipCountdown(double remainingSeconds)
    {
        var total = Math.Max(0, (int)Math.Ceiling(remainingSeconds));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>"1 h 05 min", "4 min 09 s" or "9 s".</summary>
    public static string RemainingText(double remainingSeconds)
    {
        var total = Math.Max(0, (int)Math.Ceiling(remainingSeconds));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        if (h > 0) return $"{h} h {m:00} min";
        if (m > 0) return $"{m} min {s:00} s";
        return $"{s} s";
    }
}

/// <summary>The rules that start and stop a session by themselves.</summary>
public static class KeepAwakeAutomation
{
    public enum Condition { ExternalDisplay, Power, RunningApps }
    public enum AutomationAction { None, Activate, Deactivate }

    /// <summary>An external display is any display that is not built in.</summary>
    public static bool HasExternalDisplay(IEnumerable<bool> builtInFlags) => builtInFlags.Contains(false);

    /// <summary>An empty selection never counts; otherwise any selected app running does.</summary>
    public static bool SelectedAppsAreRunning(IReadOnlyCollection<string> selected, IEnumerable<string> running)
    {
        if (selected.Count == 0) return false;
        var set = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
        return selected.Any(set.Contains);
    }

    /// <summary>The conditions that are switched on and currently true.</summary>
    public static IReadOnlyList<Condition> MatchingConditions(bool externalOn, bool externalNow, bool powerOn, bool onPower, bool appsOn, bool appsRunning)
    {
        var list = new List<Condition>();
        if (externalOn && externalNow) list.Add(Condition.ExternalDisplay);
        if (powerOn && onPower) list.Add(Condition.Power);
        if (appsOn && appsRunning) list.Add(Condition.RunningApps);
        return list;
    }

    /// <summary>A "running apps" rule with no apps chosen is not a rule at all.</summary>
    public static IReadOnlyList<Condition> EnabledConditions(bool externalOn, bool powerOn, bool appsOn, bool hasSelectedApps)
    {
        var list = new List<Condition>();
        if (externalOn) list.Add(Condition.ExternalDisplay);
        if (powerOn) list.Add(Condition.Power);
        if (appsOn && hasSelectedApps) list.Add(Condition.RunningApps);
        return list;
    }

    /// <summary>Nothing matching is never enough; Any needs one match, All needs every enabled one.</summary>
    public static bool ConditionsSatisfied(IReadOnlyCollection<Condition> matching, IReadOnlyCollection<Condition> enabled, bool requireAll)
    {
        if (matching.Count == 0) return false;
        return !requireAll || enabled.All(matching.Contains);
    }

    /// <summary>
    /// What the automation should do now. Rules failing end only a session the rules started; a session the user
    /// started is theirs. After a manual stop of an automatic session the rules stay quiet until they clear once.
    /// </summary>
    public static AutomationAction Action(bool available, bool conditionsHold, bool sessionActive, bool sessionIsAutomatic, bool suppressed = false)
    {
        if (!available || !conditionsHold) return sessionActive && sessionIsAutomatic ? AutomationAction.Deactivate : AutomationAction.None;
        if (sessionActive || suppressed) return AutomationAction.None;
        return AutomationAction.Activate;
    }

    /// <summary>A manual stop of an automatic session holds only while the rules still hold.</summary>
    public static bool StillSuppressed(bool suppressed, bool conditionsHold) => suppressed && conditionsHold;

    /// <summary>
    /// When a timed session runs out while the rules hold, it carries on as an indefinite automatic session
    /// instead of ending: unless the end was the user's, the session was already automatic, the battery rule
    /// would end it at once, or the feature is gone.
    /// </summary>
    public static bool ShouldHandOver(KeepAwakeSupport.EndReason reason, bool wasAutomatic, bool available, bool suppressed, bool batteryBlocks, bool conditionsHold) =>
        reason == KeepAwakeSupport.EndReason.Timer && !wasAutomatic && available && !suppressed && !batteryBlocks && conditionsHold;
}

/// <summary>The island's compact keep-awake activity.</summary>
public static class NotchKeepAwakeSupport
{
    public static bool ShowsActivity(bool islandEnabled, bool featureAvailable, bool activitySetting) =>
        islandEnabled && featureAvailable && activitySetting;

    /// <summary>Whole minutes left, rounded up, never under 1; an endless or unreadable value reads as 1.</summary>
    public static int MinutesLeft(double remainingSeconds) =>
        !double.IsFinite(remainingSeconds) ? 1 : Math.Max(1, (int)Math.Ceiling(remainingSeconds / 60));

    /// <summary>"30m" under an hour, "1h35" from an hour up.</summary>
    public static string CompactText(double remainingSeconds)
    {
        var minutes = MinutesLeft(remainingSeconds);
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h{minutes % 60:00}";
    }

    /// <summary>The instant the reading last changed; the strip ticks again 60 s after it.</summary>
    public static double TickStart(double endSeconds, double nowSeconds) => endSeconds - MinutesLeft(endSeconds - nowSeconds) * 60.0;
}
