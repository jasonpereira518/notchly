// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchLyricsSupport.swift (NotchMusicIdentity,
// NotchLyricLine, NotchLyrics, NotchLyricsMemory, NotchLyricsDownloadResult).
namespace Islet.Core.Notch.Lyrics;

/// <summary>The recording lyrics are looked up for. Duration is rounded to whole seconds.</summary>
public sealed record NotchMusicIdentity
{
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public double Duration { get; }
    public string? Source { get; }

    public NotchMusicIdentity(string? title, string? artist, string? album, double duration, string? source = null)
    {
        Title = title ?? "";
        Artist = artist ?? "";
        Album = album ?? "";
        Duration = double.IsFinite(duration) ? Math.Round(duration, MidpointRounding.AwayFromZero) : duration;
        Source = source;
    }
}

public sealed record NotchLyricLine(double Time, string Text);

public sealed record NotchLyrics(IReadOnlyList<NotchLyricLine> Lines, string Plain, bool Instrumental)
{
    /// <summary>The verse being sung at `position` seconds, after the user's timing `offset`.</summary>
    public int? ActiveIndex(double position, double offset = 0)
    {
        if (!double.IsFinite(position) || !double.IsFinite(offset)) return null;
        var time = position - offset;
        int lower = 0, upper = Lines.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (Lines[middle].Time <= time) lower = middle + 1; else upper = middle;
        }
        return lower == 0 ? null : lower - 1;
    }

    /// <summary>
    /// The next song position at which the highlight changes, or null when no reachable verse is
    /// left. Upstream builds a SwiftUI timeline of these boundaries; Islet arms one timer at a time.
    /// </summary>
    public double? NextChange(double position, double offset, double duration)
    {
        if (!double.IsFinite(position) || !double.IsFinite(offset)) return null;
        foreach (var line in Lines)
        {
            var target = line.Time + offset;
            if (target > position && target <= duration) return target;
        }
        return null;
    }

    public bool Equivalent(NotchLyrics? other) =>
        other is not null && Plain == other.Plain && Instrumental == other.Instrumental && Lines.SequenceEqual(other.Lines);
}

/// <summary>
/// At most one recording's lyrics and timing adjustment stay in memory. A different or absent
/// recording clears them.
/// </summary>
public sealed class NotchLyricsMemory
{
    public NotchMusicIdentity? Track { get; private set; }
    public NotchLyrics? Lyrics { get; private set; }
    public double Offset { get; private set; }

    public void Select(NotchMusicIdentity? next)
    {
        if (next == Track) return;
        Track = next;
        Lyrics = null;
        Offset = 0;
    }

    public bool Replace(NotchLyrics? lyrics, NotchMusicIdentity expected)
    {
        if (Track != expected) return false;
        Lyrics = lyrics;
        Offset = 0;
        return true;
    }

    /// <summary>Quarter-second steps, at most ten seconds either way.</summary>
    public void AdjustOffset(double amount)
    {
        if (Track is null || !double.IsFinite(amount)) return;
        Offset = Math.Min(10, Math.Max(-10, Math.Round((Offset + amount) * 4, MidpointRounding.AwayFromZero) / 4));
    }

    public void ResetOffset() => Offset = 0;

    public void Clear()
    {
        Track = null;
        Lyrics = null;
        Offset = 0;
    }
}

/// <summary>
/// A server-side lookup failure may still have working search results. Network failures and
/// client/rate-limit responses must not fan out into more requests.
/// </summary>
public abstract record NotchLyricsDownloadResult
{
    public sealed record Payload(byte[] Bytes) : NotchLyricsDownloadResult;
    public sealed record Missing : NotchLyricsDownloadResult;
    public sealed record ServiceUnavailable : NotchLyricsDownloadResult;
    public sealed record Failed : NotchLyricsDownloadResult;

    public byte[]? Data => this is Payload payload ? payload.Bytes : null;
    public bool IsFailure => this is Failed or ServiceUnavailable;
    public bool CanSearch => this is not Failed;
}
