// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchCalendarSupport.swift: the countdown and its
// windows, chosen countdowns, the week strip and month grid, read intervals, agenda order and the texts
// beside the camera; plus the web day views that stand in for Calendar.app on Windows.
using System.Globalization;

namespace Notchly.Core.Calendar;

public readonly record struct DateInterval(DateTime Start, DateTime End);

public static class CalendarSupport
{
    /// <summary>How long before its end the event under way counts down.</summary>
    public static readonly TimeSpan TimeLeftLeadTime = TimeSpan.FromHours(1);
    public const int DefaultCountdownLeadMinutes = 60;
    public static readonly IReadOnlyList<int> CountdownLeadPresets = [15, 30, 60, 120];
    public const int CountdownLeadMinimum = 5, CountdownLeadMaximum = 240;

    public static int CountdownLeadMinutes(int minutes) => Math.Clamp(minutes, CountdownLeadMinimum, CountdownLeadMaximum);

    public static TimeSpan CountdownLeadTime(int storedMinutes) => TimeSpan.FromMinutes(CountdownLeadMinutes(storedMinutes));

    // MARK: Days

    private static DateTime WeekStart(DateTime day, DayOfWeek firstDay) =>
        day.Date.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7));

    /// <summary>The 42 days of the month grid around <paramref name="date"/>'s month, from the first weekday.</summary>
    public static IReadOnlyList<DateTime> MonthDays(DateTime date, DayOfWeek firstDay)
    {
        var start = WeekStart(new DateTime(date.Year, date.Month, 1), firstDay);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToList();
    }

    /// <summary>The seven days around <paramref name="date"/>; every week lies inside the grid MonthDays reads.</summary>
    public static IReadOnlyList<DateTime> WeekDays(DateTime date, DayOfWeek firstDay)
    {
        var start = WeekStart(date, firstDay);
        return Enumerable.Range(0, 7).Select(i => start.AddDays(i)).ToList();
    }

    public static bool StartsWeek(DateTime date, DayOfWeek firstDay) => date.DayOfWeek == firstDay;

    /// <summary>The row's number in the month grid, numbered by the same calendar that starts its rows.</summary>
    public static int WeekNumber(DateTime date, CultureInfo culture) =>
        culture.Calendar.GetWeekOfYear(date, culture.DateTimeFormat.CalendarWeekRule, culture.DateTimeFormat.FirstDayOfWeek);

    /// <summary>Today and the next seven days, or a month's whole grid (reaching the week ahead in this month).</summary>
    public static DateInterval ReadInterval(DateTime? month, DateTime now, DayOfWeek firstDay)
    {
        var today = now.Date;
        var weekEnd = today.AddDays(7);
        if (month is { } shown)
        {
            var days = MonthDays(shown, firstDay);
            var end = days[^1].AddDays(1);
            var current = shown.Year == now.Year && shown.Month == now.Month;
            return new DateInterval(days[0], current && weekEnd > end ? weekEnd : end);
        }
        return new DateInterval(today, weekEnd);
    }

    public static bool NeedsCurrentRead(DateInterval visible, DateInterval current, bool countdownEnabled) =>
        countdownEnabled && (visible.Start > current.Start || visible.End < current.End);

    // MARK: Agenda

    /// <summary>A day's events, end dates exclusive: all-day first, then by start, then id.</summary>
    public static IReadOnlyList<CalendarEvent> EventsOn(IEnumerable<CalendarEvent> events, DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        return events.Where(e => e.Start < end && e.End > start)
            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<CalendarEvent> Ordered(IEnumerable<CalendarEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return events.Where(e => e.End > e.Start && seen.Add(e.Id))
            .OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<CalendarEvent> Upcoming(IEnumerable<CalendarEvent> events, DateTime now) =>
        Ordered(events).Where(e => e.End > now).ToList();

    public static CalendarEvent? Next(IEnumerable<CalendarEvent> events, DateTime now) =>
        Upcoming(events, now).FirstOrDefault(e => !e.AllDay);

    /// <summary>The next start or end of anything upcoming, midnight, or a quarter of an hour, whichever is first.</summary>
    public static DateTime NextRefresh(IEnumerable<CalendarEvent> events, DateTime now)
    {
        var candidates = Upcoming(events, now).SelectMany(e => new[] { e.Start, e.End })
            .Append(now.Date.AddDays(1)).Append(now.AddMinutes(15));
        return candidates.Where(d => d > now).DefaultIfEmpty(now.AddMinutes(15)).Min();
    }

    // MARK: Countdown

    /// <summary>
    /// Names the event a countdown was chosen for across refreshes, edits and restarts: its UID or, in a
    /// series, one occurrence by the moment it first fell on, which moving that occurrence leaves unchanged.
    /// </summary>
    public static string CountdownKey(string identifier, DateTime? occurrenceUtc) =>
        occurrenceUtc is { } occurrence
            ? identifier + "@" + new DateTimeOffset(DateTime.SpecifyKind(occurrence, DateTimeKind.Utc)).ToUnixTimeSeconds()
            : identifier;

    public static bool IsChosen(CalendarEvent calendarEvent, IReadOnlySet<string> chosen) =>
        calendarEvent.CountdownKey.Length > 0 && chosen.Contains(calendarEvent.CountdownKey);

    /// <summary>
    /// The choices after a read: an event read again keeps its current end, so a moved event stays chosen,
    /// and a choice whose event has ended is forgotten. Null when nothing changes.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime>? RefreshedChoices(IReadOnlyDictionary<string, DateTime> choices,
                                                                         IEnumerable<CalendarEvent> events, DateTime now)
    {
        var refreshed = new Dictionary<string, DateTime>(choices, StringComparer.Ordinal);
        foreach (var e in events)
            if (refreshed.ContainsKey(e.CountdownKey)) refreshed[e.CountdownKey] = e.End;
        foreach (var ended in refreshed.Where(c => c.Value <= now).Select(c => c.Key).ToList()) refreshed.Remove(ended);
        var unchanged = refreshed.Count == choices.Count && refreshed.All(c => choices.TryGetValue(c.Key, out var end) && end == c.Value);
        return unchanged ? null : refreshed;
    }

    private static IEnumerable<bool> FollowedMoments(CalendarEvent e, bool starts, bool ends, IReadOnlySet<string> chosen)
    {
        if (starts || IsChosen(e, chosen)) yield return false;
        if (ends) yield return true;
    }

    /// <summary>
    /// The nearer of the moments the island follows: a timed event's start (with the countdown on, or for
    /// an event chosen from its menu) and, with time left on, the end of one in progress. A start that
    /// coincides with an end leaves the event under way.
    /// </summary>
    public static CalendarCountdown? Countdown(IEnumerable<CalendarEvent> events, DateTime now, bool starts, bool ends,
                                               IReadOnlySet<string> chosen, TimeSpan lead) =>
        Ordered(events).Where(e => !e.AllDay)
            .SelectMany(e => FollowedMoments(e, starts, ends, chosen).Select(ongoing => new CalendarCountdown(e, ongoing)))
            .Where(c => c.IsShown(now, lead))
            .OrderBy(c => c.Target).ThenBy(c => c.Ongoing ? 0 : 1)
            .FirstOrDefault();

    /// <summary>When the window before each followed moment opens or closes next.</summary>
    public static DateTime? CountdownTransition(IEnumerable<CalendarEvent> events, DateTime now, bool starts, bool ends,
                                                IReadOnlySet<string> chosen, TimeSpan lead)
    {
        var moments = Ordered(events).Where(e => !e.AllDay)
            .SelectMany(e => FollowedMoments(e, starts, ends, chosen).SelectMany(ongoing => ongoing
                ? new[] { e.Start > e.End - TimeLeftLeadTime ? e.Start : e.End - TimeLeftLeadTime, e.End }
                : new[] { e.Start - lead, e.Start }))
            .Where(d => d > now).ToList();
        return moments.Count == 0 ? null : moments.Min();
    }

    /// <summary>"59:59": minutes and seconds left, rounded up.</summary>
    public static string CountdownText(DateTime until, DateTime now)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((until - now).TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>One clock shape for the selected lead, so counting through 100 minutes keeps the island's width.</summary>
    public static string CountdownShape(TimeSpan lead)
    {
        var minutes = Math.Min(CountdownLeadMaximum, Math.Max(60, Math.Ceiling(lead.TotalMinutes)));
        return new string('0', Math.Max(2, ((int)minutes).ToString(CultureInfo.InvariantCulture).Length)) + ":00";
    }

    /// <summary>"· 3:30 PM" when the event starts, or "→ 4:00 PM" when the one under way ends.</summary>
    public static string TimeText(CalendarCountdown countdown, CultureInfo culture) =>
        (countdown.Ongoing ? "→ " : "· ")
        + countdown.Target.ToString(culture.DateTimeFormat.ShortTimePattern, culture).Replace(' ', ' ');

    // MARK: Opening an event

    /// <summary>
    /// Windows has no Calendar.app: the web calendar's day view for feeds whose site has one (Google,
    /// Outlook), else null.
    /// </summary>
    public static string? DayLink(string feedSource, DateTime day)
    {
        if (!Uri.TryCreate(feedSource, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = $"{day.Year}/{day.Month}/{day.Day}";
        if (host == "calendar.google.com" || host.EndsWith(".google.com", StringComparison.Ordinal))
            return $"https://calendar.google.com/calendar/r/day/{path}";
        if (host is "outlook.live.com") return $"https://outlook.live.com/calendar/0/view/day/{path}";
        if (host is "outlook.office365.com" or "outlook.office.com") return $"https://outlook.office.com/calendar/view/day/{path}";
        return null;
    }
}
