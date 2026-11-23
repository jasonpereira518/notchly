// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchCalendarSupport.swift (NotchCalendarEvent,
// NotchCalendarCountdown, NotchCalendarColor) plus the Windows calendar feed: one private iCal link
// (Google, Outlook, Canvas, iCloud) or .ics file is one calendar.
namespace Notchly.Core.Calendar;

public readonly record struct CalendarColor(byte R, byte G, byte B)
{
    public static readonly CalendarColor Fallback = new(0x59, 0xA6, 0xFF);

    /// <summary>ICS feeds carry no colour, so each calendar gets one of these, in order, until changed.</summary>
    public static readonly IReadOnlyList<CalendarColor> Palette =
    [
        new(0x0A, 0x84, 0xFF), new(0x30, 0xD1, 0x58), new(0xFF, 0x45, 0x3A), new(0xFF, 0x9F, 0x0A),
        new(0xBF, 0x5A, 0xF2), new(0xFF, 0xD6, 0x0A), new(0x40, 0xC8, 0xE0),
    ];

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static CalendarColor? Parse(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
        return new CalendarColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>One occurrence, in local wall time. End is exclusive, all-day events included.</summary>
public sealed record CalendarEvent(string Id, string Title, string CalendarId, string CalendarName,
                                   DateTime Start, DateTime End, bool AllDay)
{
    public string Location { get; init; } = "";
    public CalendarColor Color { get; init; } = CalendarColor.Fallback;
    /// <summary>The event's own link, when the feed gives one.</summary>
    public string? Url { get; init; }
    /// <summary>How a countdown chosen from the event's menu remembers it; see CalendarSupport.CountdownKey.</summary>
    public string CountdownKey { get; init; } = "";
}

/// <summary>What the closed island counts down to: an event's start or, while it is happening, its end.</summary>
public sealed record CalendarCountdown(CalendarEvent Event, bool Ongoing)
{
    public DateTime Target => Ongoing ? Event.End : Event.Start;

    /// <summary>A start shows within the lead before it; an end during the hour before it, once its event has begun.</summary>
    public bool IsShown(DateTime now, TimeSpan lead) =>
        Target > now && Target - now <= (Ongoing ? CalendarSupport.TimeLeftLeadTime : lead) && (!Ongoing || Event.Start <= now);
}

/// <summary>A calendar added by its private iCal link or an .ics file. The link is a secret and is stored encrypted.</summary>
public sealed record CalendarFeed(string Id, string Name, string Source, CalendarColor Color)
{
    public bool IsFile => !Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
