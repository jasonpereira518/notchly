// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/NotchTests.swift (calendarContracts, calendarMonthContracts)
// plus the Windows feed reader: an iCal feed with a series, an excluded date, a moved occurrence, an
// all-day event, another time zone and a cancelled event.
using System.Globalization;
using Notchly.Core.Calendar;

namespace Notchly.Core.Tests;

public class CalendarTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0); // a Monday
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly IReadOnlySet<string> NoneChosen = new HashSet<string>();

    private static CalendarEvent Event(string id, double startMinutes, double endMinutes, bool allDay = false) =>
        new(id, id, "c", "Calendar", Now.AddMinutes(startMinutes), Now.AddMinutes(endMinutes), allDay) { CountdownKey = "key-" + id };

    private static CalendarCountdown? CountdownFor(IEnumerable<CalendarEvent> events, bool starts = true, bool ends = false,
                                                   IReadOnlySet<string>? chosen = null, double leadMinutes = 60, double at = 0) =>
        CalendarSupport.Countdown(events, Now.AddMinutes(at), starts, ends, chosen ?? NoneChosen, TimeSpan.FromMinutes(leadMinutes));

    [Fact]
    public void TheLeadKeepsToItsPresetsAndRange()
    {
        Assert.Equal(new[] { 15, 30, 60, 120 }, CalendarSupport.CountdownLeadPresets);
        Assert.Equal(5, CalendarSupport.CountdownLeadMinutes(1));
        Assert.Equal(240, CalendarSupport.CountdownLeadMinutes(500));
        Assert.Equal(TimeSpan.FromMinutes(240), CalendarSupport.CountdownLeadTime(9999));
        Assert.Equal(Hour, CalendarSupport.TimeLeftLeadTime);
    }

    [Fact]
    public void AStartCountsDownOnlyWithinTheLead()
    {
        var in45 = Event("in45", 45, 75);
        Assert.Equal(in45, CountdownFor([in45])?.Event);
        Assert.Null(CountdownFor([in45], leadMinutes: 30));
        Assert.Null(CountdownFor([Event("in90", 90, 120)]));
        Assert.Equal(Now.AddMinutes(15), CalendarSupport.CountdownTransition([in45], Now, true, false, NoneChosen, TimeSpan.FromMinutes(30)));
        Assert.Equal(Now.AddMinutes(45), CalendarSupport.CountdownTransition([in45], Now, true, false, NoneChosen, Hour));
        Assert.Null(CountdownFor([Event("allday", 30, 60 * 24, allDay: true)]));
        Assert.Equal("edge", CountdownFor([Event("edge", 60, 61)])?.Event.Id); // exactly at the lead
    }

    [Fact]
    public void TheEventUnderWayCountsDownOnlyInItsLastHour()
    {
        var meeting = Event("meeting", -30, 30);
        var afterGap = Event("after gap", 60, 90);
        Assert.Equal(new CalendarCountdown(afterGap, false), CountdownFor([meeting, afterGap]));
        Assert.Equal(new CalendarCountdown(meeting, true), CountdownFor([meeting, afterGap], ends: true));
        // A start that coincides with an end leaves the event under way.
        var backToBack = Event("back to back", 30, 90);
        Assert.Equal(new CalendarCountdown(meeting, true), CountdownFor([meeting, backToBack], ends: true));

        var longOne = Event("long", -10, 170);
        Assert.Null(CountdownFor([longOne], starts: false, ends: true));
        Assert.Equal(Now.AddMinutes(110), CalendarSupport.CountdownTransition([longOne], Now, false, true, NoneChosen, Hour));
        Assert.False(new CalendarCountdown(afterGap, true).IsShown(Now, Hour)); // not before it starts
        Assert.Equal(meeting.End, CountdownFor([meeting], starts: false, ends: true, leadMinutes: 15)?.Target);
    }

    [Fact]
    public void AChosenEventCountsDownWhileTheCountdownForEveryEventIsOff()
    {
        var soon = Event("soon", 30, 60);
        Assert.Null(CountdownFor([soon], starts: false));
        Assert.Equal(soon, CountdownFor([soon], starts: false, chosen: new HashSet<string> { soon.CountdownKey })?.Event);
        Assert.True(CalendarSupport.IsChosen(soon, new HashSet<string> { "key-soon" }));
        Assert.False(CalendarSupport.IsChosen(soon with { CountdownKey = "" }, new HashSet<string> { "" }));
    }

    [Fact]
    public void ChoicesFollowMovedEventsAndAreForgottenOnceOver()
    {
        var moved = Event("moved", 120, 200);
        var choices = new Dictionary<string, DateTime> { ["key-moved"] = Now.AddMinutes(180), ["key-gone"] = Now.AddMinutes(-1) };
        var refreshed = CalendarSupport.RefreshedChoices(choices, [moved], Now);
        Assert.NotNull(refreshed);
        Assert.Equal(new[] { "key-moved" }, refreshed!.Keys);
        Assert.Equal(moved.End, refreshed["key-moved"]);
        Assert.Null(CalendarSupport.RefreshedChoices(refreshed, [moved], Now));
    }

    [Fact]
    public void TheMonthGridAndWeekStripStartOnTheRegionsFirstWeekday()
    {
        var sundayFirst = CalendarSupport.MonthDays(new DateTime(2026, 11, 15), DayOfWeek.Sunday);
        Assert.Equal(42, sundayFirst.Count);
        Assert.Equal(new DateTime(2026, 11, 1), sundayFirst[0]);
        var mondayFirst = CalendarSupport.MonthDays(new DateTime(2026, 11, 15), DayOfWeek.Monday);
        Assert.Equal(new DateTime(2026, 10, 26), mondayFirst[0]);
        Assert.Equal(new DateTime(2026, 12, 6), mondayFirst[^1]);
        var week = CalendarSupport.WeekDays(new DateTime(2026, 11, 25, 15, 0, 0), DayOfWeek.Sunday);
        Assert.Equal(new DateTime(2026, 11, 22), week[0]);
        Assert.Equal(new DateTime(2026, 11, 28), week[^1]);
        Assert.All(week, d => Assert.Contains(d, CalendarSupport.MonthDays(new DateTime(2026, 11, 25), DayOfWeek.Sunday)));
        Assert.True(CalendarSupport.StartsWeek(new DateTime(2026, 11, 22), DayOfWeek.Sunday));
    }

    [Fact]
    public void WeekNumbersFollowTheRegionsCalendar()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        var germany = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal(1, CalendarSupport.WeekNumber(new DateTime(2026, 1, 1), us));
        Assert.Equal(1, CalendarSupport.WeekNumber(new DateTime(2026, 1, 1), germany)); // Thursday: ISO week 1
        Assert.Equal(53, CalendarSupport.WeekNumber(new DateTime(2026, 12, 31), germany));
        // A Sunday-first row shares one number across its days.
        var row = CalendarSupport.WeekDays(new DateTime(2026, 11, 25), DayOfWeek.Sunday);
        Assert.Single(row.Select(d => CalendarSupport.WeekNumber(d, us)).Distinct());
    }

    [Fact]
    public void TheReadCoversTheWeekAheadOrTheShownMonthsGrid()
    {
        Assert.Equal(new DateInterval(Now.Date, Now.Date.AddDays(7)), CalendarSupport.ReadInterval(null, Now, DayOfWeek.Sunday));
        var thisMonth = CalendarSupport.ReadInterval(Now, Now, DayOfWeek.Sunday);
        Assert.Equal(new DateTime(2026, 11, 1), thisMonth.Start);
        Assert.Equal(new DateTime(2026, 12, 13), thisMonth.End);
        var december = CalendarSupport.ReadInterval(new DateTime(2026, 12, 1), Now, DayOfWeek.Sunday);
        Assert.True(CalendarSupport.NeedsCurrentRead(december, CalendarSupport.ReadInterval(null, Now, DayOfWeek.Sunday), true));
        Assert.False(CalendarSupport.NeedsCurrentRead(december, CalendarSupport.ReadInterval(null, Now, DayOfWeek.Sunday), false));
        Assert.False(CalendarSupport.NeedsCurrentRead(thisMonth, CalendarSupport.ReadInterval(null, Now, DayOfWeek.Sunday), true));
    }

    [Fact]
    public void TheAgendaReadsEndsAsExclusiveAndPutsAllDayFirst()
    {
        var lateNight = new CalendarEvent("late", "late", "c", "C", Now.Date.AddHours(23), Now.Date.AddDays(1), false);
        var allDay = new CalendarEvent("trip", "trip", "c", "C", Now.Date, Now.Date.AddDays(1), true);
        var morning = new CalendarEvent("morning", "m", "c", "C", Now.Date.AddHours(9), Now.Date.AddHours(10), false);
        Assert.Equal(new[] { "trip", "morning", "late" }, CalendarSupport.EventsOn([lateNight, morning, allDay], Now).Select(e => e.Id));
        Assert.Empty(CalendarSupport.EventsOn([lateNight, allDay], Now.AddDays(1)));

        var current = Event("current", -10, 20);
        var later = Event("later", 5, 30);
        var duplicate = later with { Title = "dup" };
        var broken = Event("broken", 50, 40);
        Assert.Equal(new[] { "current", "later" }, CalendarSupport.Ordered([later, current, duplicate, broken]).Select(e => e.Id));
        Assert.Equal(current, CalendarSupport.Next([current, later], Now));
        Assert.Equal(later, CalendarSupport.Next([current, later], Now.AddMinutes(25)));
        Assert.Null(CalendarSupport.Next([allDay], Now));
        Assert.Equal(Now.AddMinutes(5), CalendarSupport.NextRefresh([current, later], Now));
        Assert.Equal(Now.AddMinutes(15), CalendarSupport.NextRefresh([], Now));
    }

    [Fact]
    public void TheClosedIslandsTexts()
    {
        Assert.Equal("60:00", CalendarSupport.CountdownText(Now.Add(Hour), Now));
        Assert.Equal("0:59", CalendarSupport.CountdownText(Now.AddSeconds(59), Now));
        Assert.Equal("1:00", CalendarSupport.CountdownText(Now.AddSeconds(59.2), Now));
        Assert.Equal("0:00", CalendarSupport.CountdownText(Now.AddSeconds(-5), Now));
        Assert.Equal("00:00", CalendarSupport.CountdownShape(TimeSpan.FromMinutes(30)));
        Assert.Equal("000:00", CalendarSupport.CountdownShape(TimeSpan.FromMinutes(120)));
        var us = CultureInfo.GetCultureInfo("en-US");
        var next = new CalendarCountdown(Event("next", 60, 90), false);
        Assert.Equal("·\u200911:00 AM", CalendarSupport.TimeText(next, us));
        Assert.Equal("→\u200911:30 AM", CalendarSupport.TimeText(next with { Ongoing = true }, us));
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/ical/abc/private-x/basic.ics", "https://calendar.google.com/calendar/r/day/2026/11/23")]
    [InlineData("https://outlook.live.com/owa/calendar/1/2/calendar.ics", "https://outlook.live.com/calendar/0/view/day/2026/11/23")]
    [InlineData("https://outlook.office365.com/owa/calendar/1/2/calendar.ics", "https://outlook.office.com/calendar/view/day/2026/11/23")]
    [InlineData("https://school.instructure.com/feeds/calendars/user_1.ics", null)]
    [InlineData("C:\\Users\\me\\term.ics", null)]
    public void EventsOpenTheWebCalendarsDayWhereItHasOne(string feed, string? link) =>
        Assert.Equal(link, CalendarSupport.DayLink(feed, Now));

    [Theory]
    [InlineData("webcal://p01-caldav.icloud.com/published/2/abc", "https://p01-caldav.icloud.com/published/2/abc")]
    [InlineData("  https://calendar.google.com/x/basic.ics ", "https://calendar.google.com/x/basic.ics")]
    [InlineData("http://example.com/feed.ics", null)]
    [InlineData("ftp://example.com/feed.ics", null)]
    [InlineData("not a link", null)]
    public void FeedLinksTravelOnlyOverHttps(string input, string? normalized) =>
        Assert.Equal(normalized, IcsReader.NormalizeFeedUrl(input));

    private const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:test
        X-WR-CALNAME:Classes
        BEGIN:VEVENT
        UID:weekly@x
        DTSTART;TZID=America/New_York:20261102T090000
        DTEND;TZID=America/New_York:20261102T100000
        RRULE:FREQ=WEEKLY;COUNT=5
        EXDATE;TZID=America/New_York:20261109T090000
        SUMMARY:Lecture
        LOCATION:Hall 2
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly@x
        RECURRENCE-ID;TZID=America/New_York:20261116T090000
        DTSTART;TZID=America/New_York:20261116T130000
        DTEND;TZID=America/New_York:20261116T140000
        SUMMARY:Lecture (moved)
        END:VEVENT
        BEGIN:VEVENT
        UID:allday@x
        DTSTART;VALUE=DATE:20261120
        DTEND;VALUE=DATE:20261122
        SUMMARY:Trip
        END:VEVENT
        BEGIN:VEVENT
        UID:tokyo@x
        DTSTART;TZID=Asia/Tokyo:20261118T100000
        DTEND;TZID=Asia/Tokyo:20261118T110000
        SUMMARY:Tokyo call
        URL:https://example.com/call
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled@x
        DTSTART:20261119T150000Z
        DTEND:20261119T153000Z
        SUMMARY:Cancelled
        STATUS:CANCELLED
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly CalendarFeed Classes = new("feed1", "Classes", "https://calendar.google.com/x/basic.ics", CalendarColor.Palette[1]);

    [Fact]
    public void AFeedsSeriesExclusionsMovesZonesAndAllDayEventsReadAsTheCalendarShowsThem()
    {
        var calendar = IcsReader.Parse(Feed.Replace("\n", "\r\n"));
        Assert.NotNull(calendar);
        Assert.Equal("Classes", IcsReader.CalendarName(calendar!));
        var events = IcsReader.Occurrences(calendar!, new DateInterval(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1)), Classes, Eastern);
        var titles = events.OrderBy(e => e.Start).Select(e => $"{e.Title}@{e.Start:MM-dd HH:mm}").ToList();
        Assert.Equal(new[]
        {
            "Lecture@11-02 09:00", "Lecture (moved)@11-16 13:00", "Tokyo call@11-17 20:00", "Trip@11-20 00:00",
            "Lecture@11-23 09:00", "Lecture@11-30 09:00",
        }, titles);
        var trip = events.Single(e => e.Title == "Trip");
        Assert.True(trip.AllDay);
        Assert.Equal(new DateTime(2026, 11, 22), trip.End);
        Assert.Equal("Hall 2", events.First(e => e.Title == "Lecture").Location);
        Assert.Equal("https://example.com/call", events.Single(e => e.Title == "Tokyo call").Url);
        Assert.All(events, e => Assert.Equal(CalendarColor.Palette[1], e.Color));

        // One-off events are chosen by UID; each occurrence of a series by when it first fell.
        Assert.Equal("feed1/tokyo@x", events.Single(e => e.Title == "Tokyo call").CountdownKey);
        var moved = events.Single(e => e.Title == "Lecture (moved)");
        Assert.Equal("feed1/weekly@x@" + new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), moved.CountdownKey);
        Assert.Equal(events.Select(e => e.CountdownKey).Distinct().Count(), events.Count);

        // The same feed read again names the same events.
        var again = IcsReader.Occurrences(IcsReader.Parse(Feed.Replace("\n", "\r\n"))!, new DateInterval(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1)), Classes, Eastern);
        Assert.Equal(events.Select(e => (e.Id, e.CountdownKey)), again.Select(e => (e.Id, e.CountdownKey)));
    }

    [Fact]
    public void OnlyTheIntervalIsReadAndBrokenFeedsAreRefused()
    {
        var calendar = IcsReader.Parse(Feed.Replace("\n", "\r\n"))!;
        var week = IcsReader.Occurrences(calendar, new DateInterval(new DateTime(2026, 11, 21), new DateTime(2026, 11, 28)), Classes, Eastern);
        // The trip ends on the 22nd (exclusive), so it still reaches the 21st.
        Assert.Equal(new[] { "Trip", "Lecture" }, week.OrderBy(e => e.Start).Select(e => e.Title));
        Assert.Null(IcsReader.Parse("this is not a calendar"));
    }
}
