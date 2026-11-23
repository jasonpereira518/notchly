// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's EventKit reads (NotchCalendarService's reader): an iCal feed's text
// becomes the occurrences inside a read interval, through Ical.Net. Series are expanded only over that
// interval; excluded dates and moved occurrences follow the feed; cancelled events are left out.
using CalendarEvent_ = Ical.Net.CalendarComponents.CalendarEvent;
using Ical.Net;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace Notchly.Core.Calendar;

public static class IcsReader
{
    /// <summary>Upper bound on occurrences read for one calendar and interval.</summary>
    public const int MaximumOccurrences = 5000;
    /// <summary>Events that began this long before the interval can still reach into it (trips, terms).</summary>
    private static readonly TimeSpan LongestReach = TimeSpan.FromDays(62);

    /// <summary>
    /// A feed link as fetched: webcal links are served over HTTPS; anything that is not HTTPS is refused,
    /// since the link is a secret that must not travel in clear text.
    /// </summary>
    public static string? NormalizeFeedUrl(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcal://".Length..];
        else if (text.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcals://".Length..];
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
            ? uri.AbsoluteUri : null;
    }

    public static IcalCalendar? Parse(string text)
    {
        try { return IcalCalendar.Load(text); }
        // Whatever a malformed feed makes the parser throw, the feed is refused rather than the app.
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>The name the feed gives its calendar (X-WR-CALNAME), if any.</summary>
    public static string? CalendarName(IcalCalendar calendar) =>
        calendar.Properties["X-WR-CALNAME"]?.Value?.ToString()?.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>The occurrences that overlap <paramref name="interval"/>, in <paramref name="zone"/>'s wall time.</summary>
    public static IReadOnlyList<CalendarEvent> Occurrences(IcalCalendar calendar, DateInterval interval, CalendarFeed feed,
                                                           TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(interval.Start - LongestReach, DateTimeKind.Unspecified), zone);
        var until = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(interval.End, DateTimeKind.Unspecified), zone);
        var events = new List<CalendarEvent>();
        try
        {
            foreach (var occurrence in calendar.GetOccurrences<CalendarEvent_>(new CalDateTime(from, "UTC", true))
                         .TakeWhileBefore(new CalDateTime(until, "UTC", true)))
            {
                if (events.Count >= MaximumOccurrences) break;
                if (Convert(occurrence, feed, zone) is { } converted && converted.End > interval.Start && converted.Start < interval.End)
                    events.Add(converted);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or NullReferenceException)
        {
            // A broken rule in one feed leaves what was read so far.
        }
        return events;
    }

    private static CalendarEvent? Convert(Occurrence occurrence, CalendarFeed feed, TimeZoneInfo zone)
    {
        if (occurrence.Source is not CalendarEvent_ source) return null;
        if (string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return null;
        var period = occurrence.Period;
        var startValue = period.StartTime;
        var endValue = period.EffectiveEndTime;
        var allDay = !startValue.HasTime;
        DateTime start, end;
        if (allDay)
        {
            start = startValue.Date.ToDateTime(TimeOnly.MinValue);
            end = endValue is { } e ? e.Date.ToDateTime(TimeOnly.MinValue) : start.AddDays(1);
            if (end <= start) end = start.AddDays(1);
        }
        else
        {
            start = WallTime(startValue, zone);
            end = endValue is { } e ? WallTime(e, zone) : start;
        }
        var uid = string.IsNullOrWhiteSpace(source.Uid) ? $"{feed.Id}:{source.Summary}:{start:O}" : source.Uid!;
#pragma warning disable CS0618 // RecurrenceId still names the occurrence an override replaces.
        var overrideOf = source.RecurrenceId;
#pragma warning restore CS0618
        var recurring = source.RecurrenceRule is not null || overrideOf is not null;
        DateTime? occurrenceUtc = recurring ? (overrideOf ?? startValue).AsUtc : null;
        return new CalendarEvent($"{feed.Id}:{uid}:{start.Ticks}", source.Summary?.Trim() ?? "", feed.Id, feed.Name, start, end, allDay)
        {
            Location = source.Location?.Trim() ?? "",
            Color = feed.Color,
            Url = source.Url is { IsAbsoluteUri: true } url && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                ? url.AbsoluteUri : null,
            CountdownKey = CalendarSupport.CountdownKey($"{feed.Id}/{uid}", occurrenceUtc),
        };
    }

    /// <summary>Times with a zone or in UTC convert to local wall time; floating times are already wall time.</summary>
    private static DateTime WallTime(CalDateTime value, TimeZoneInfo zone) =>
        value.TzId is null && !value.IsUtc
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified)
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc), zone);
}
