// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Tests/NotchLyricsCorpusTests.swift (offline replay of captured LRCLIB and
// MusicBrainz replies; lyrics are synthetic fingerprints) and the cases of
// Tests/NotchLyricsTimelineTests.swift, plus parser and memory contracts.
using System.Text.Encodings.Web;
using System.Text.Json;
using Notchly.Core.Notch.Lyrics;

namespace Notchly.Core.Tests;

public class NotchLyricsCorpusTests
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static byte[] Bytes(JsonElement element) => JsonSerializer.SerializeToUtf8Bytes(element, Compact);

    [Theory]
    [InlineData("NotchLyricsCorpus.json", 87, 60, 85, 85)]
    [InlineData("NotchLyricsLessMainstreamCorpus.json", 50, 25, 39, 39)]
    [InlineData("NotchLyricsMultilingualCorpus.json", 59, 32, 55, 56)]
    public void CapturedCohortRetainsItsReviewedTotals(string file, int expectedCount, int expectedBefore, int expectedAfter, int expectedWider)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        var rows = document.RootElement.EnumerateArray().ToList();
        int before = 0, after = 0, wider = 0;
        var mismatches = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var metadata = row.GetProperty("track");
            string? Text(string name) => metadata.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var track = new NotchMusicIdentity(Text("title"), Text("artist"), Text("album"), metadata.GetProperty("duration").GetDouble());

            var exactData = Bytes(row.GetProperty("exact"));
            var exact = NotchLyricsSupport.Decode(exactData, track);
            var canonical = exact ?? NotchLyricsSupport.DecodeCatalogMatch(exactData, track);
            var candidates = new List<NotchLyricsSupport.Candidate>();
            var widerCandidates = new List<NotchLyricsSupport.Candidate>();
            foreach (var group in new[] { "searches", "discoverySearches" })
            {
                if (!row.TryGetProperty(group, out var searches) || searches.ValueKind != JsonValueKind.Array) continue;
                foreach (var search in searches.EnumerateArray())
                {
                    var data = Bytes(search.GetProperty("response"));
                    candidates.AddRange(NotchLyricsSupport.SearchCandidates(data, track));
                    widerCandidates.AddRange(NotchLyricsSupport.SearchCandidates(data, track, durationTolerance: 5));
                }
            }
            var aliasCandidates = new List<NotchLyricsSupport.Candidate>();
            var aliasWider = new List<NotchLyricsSupport.Candidate>();
            if (row.TryGetProperty("artistMetadata", out var artistMetadata))
            {
                var broad = artistMetadata.ValueKind == JsonValueKind.Object && artistMetadata.TryGetProperty("count", out var c)
                            && c.ValueKind == JsonValueKind.Number && c.GetDouble() > 5;
                var artists = NotchLyricsSupport.ArtistIdentities(Bytes(artistMetadata), track, allowTruncated: broad);
                var identity = artists.Count == 1 && !broad ? artists[0] : null;
                if (artists.Count == 1 && artists[0].QueryNames.Count == 0 && row.TryGetProperty("recordingMetadata", out var recording))
                    identity = NotchLyricsSupport.RecordingArtistIdentity(Bytes(recording), track, artists);
                else if ((artists.Count > 1 || broad) && row.TryGetProperty("albumMetadata", out var album))
                    identity = NotchLyricsSupport.ArtistIdentity(Bytes(album), track, artists);
                if (identity is not null && row.TryGetProperty("aliasSearches", out var aliasSearches) && aliasSearches.ValueKind == JsonValueKind.Array)
                {
                    foreach (var search in aliasSearches.EnumerateArray())
                    {
                        var data = Bytes(search.GetProperty("response"));
                        aliasCandidates.AddRange(NotchLyricsSupport.SearchCandidates(data, track, verifiedArtistAliases: identity.Names.ToList()));
                        aliasWider.AddRange(NotchLyricsSupport.SearchCandidates(data, track, durationTolerance: 5, verifiedArtistAliases: identity.Names.ToList()));
                    }
                }
            }
            var recovered = canonical ?? NotchLyricsSupport.BestLyrics(candidates)
                ?? (candidates.Count == 0 ? NotchLyricsSupport.BestLyrics(aliasCandidates) : null);
            var recoveredWider = canonical ?? NotchLyricsSupport.BestLyrics(widerCandidates)
                ?? (widerCandidates.Count == 0 ? NotchLyricsSupport.BestLyrics(aliasWider) : null);
            before += exact is null ? 0 : 1;
            after += recovered is null ? 0 : 1;
            wider += recoveredWider is null ? 0 : 1;

            bool Flag(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            if ((exact is not null) != Flag("expectedBefore") || (recovered is not null) != Flag("expectedAfter")
                || (recoveredWider is not null) != Flag("expectedAfterFive"))
                mismatches.Add($"#{index} {track.Title} / {track.Artist}: before {exact is not null}, after {recovered is not null}, wider {recoveredWider is not null}");
            if (Flag("expectedPrevious") && recovered is null) mismatches.Add($"#{index} {track.Title}: previously resolved record lost");
            if (exact is not null && !exact.Equivalent(recovered)) mismatches.Add($"#{index} {track.Title}: exact match not retained");
            if (expectedCount == 87 && index >= 80 && recovered is null) mismatches.Add($"#{index} {track.Title}: desktop sample unmatched");
        }
        Assert.True(mismatches.Count == 0, "Reviewed match decisions changed:\n" + string.Join("\n", mismatches));
        Assert.Equal(expectedCount, rows.Count);
        Assert.Equal(expectedBefore, before);
        Assert.Equal(expectedAfter, after);
        Assert.Equal(expectedWider, wider);
    }
}

public class NotchLyricsTests
{
    private static NotchLyrics Verses(double[] times, bool blankLast = false) =>
        new(times.Select((t, i) => new NotchLyricLine(t, blankLast && i == times.Length - 1 ? "" : "Verse")).ToList(), "", false);

    [Fact]
    public void TimelineReachesTheFinalVerseAndThenStops()
    {
        var lyrics = Verses([0, 0.08, 0.16]);
        Assert.Equal(2, lyrics.ActiveIndex(0.2));
        Assert.Null(lyrics.NextChange(0.17, 0, 10));
        Assert.Equal(0.16, lyrics.NextChange(0.1, 0, 10));
    }

    [Fact]
    public void ASingleFutureVerseLightsUpWhenItsTimeArrives()
    {
        var lyrics = Verses([0.08]);
        Assert.Null(lyrics.ActiveIndex(0.05));
        Assert.Equal(0.08, lyrics.NextChange(0, 0, 10));
        Assert.Equal(0, lyrics.ActiveIndex(0.08));
    }

    [Fact]
    public void AFinalInstrumentalGapClearsThePrecedingVerse()
    {
        var lyrics = Verses([0, 0.08], blankLast: true);
        var active = lyrics.ActiveIndex(0.1);
        Assert.Equal(1, active);
        Assert.Equal("", lyrics.Lines[active!.Value].Text);
    }

    [Fact]
    public void TimingAdjustmentDeliversTheLastReachableVerse()
    {
        var lyrics = Verses([0, 0.08, 0.16]);
        Assert.Equal(1, lyrics.ActiveIndex(0.2, offset: 0.05));
        Assert.Null(lyrics.NextChange(0.14, 0.05, 0.2));
    }

    [Fact]
    public void ParserHandlesOffsetRepeatedStampsAndBlankLines()
    {
        const string source = "﻿[ar:Someone]\n[offset:500]\n[00:01.00][00:03.50]Chorus\n[00:02.00]Verse\n[00:04.00]\n[00:01.00]Echo\r\n";
        var lines = NotchLyricsSupport.Parse(source, duration: 60);
        Assert.Equal(new[] { 0.5, 1.5, 3.0, 3.5 }, lines.Select(l => Math.Round(l.Time, 3)));
        Assert.Equal("Chorus\nEcho", lines[0].Text);
        Assert.Equal("Verse", lines[1].Text);
        Assert.Equal("Chorus", lines[2].Text);
        Assert.Equal("", lines[3].Text);
    }

    [Fact]
    public void ParserDropsVersesPastTheEndAndRejectsTextWithoutTimes()
    {
        Assert.Single(NotchLyricsSupport.Parse("[00:01.00]In\n[05:00.00]Out", duration: 60));
        Assert.Empty(NotchLyricsSupport.Parse("Just words\nwithout times", duration: 60));
        Assert.Empty(NotchLyricsSupport.Parse("[00:01.00]", duration: 60));
        Assert.Empty(NotchLyricsSupport.Parse("[00:61.00]Bad seconds", duration: 600));
        Assert.Empty(NotchLyricsSupport.Parse("[00:01.00]x", duration: 0));
    }

    [Fact]
    public void ParserBoundsLineExpansion()
    {
        var stamps = string.Concat(Enumerable.Range(0, 2001).Select(i => $"[00:{i % 60:00}.{i % 100:00}]"));
        Assert.Empty(NotchLyricsSupport.Parse(stamps + "Too many", duration: 600));
        Assert.Empty(NotchLyricsSupport.Parse(new string('a', NotchLyricsSupport.MaximumBytes + 1), duration: 600));
    }

    [Fact]
    public void MemoryKeepsOneRecordingAndQuarterSecondOffsets()
    {
        var memory = new NotchLyricsMemory();
        var song = new NotchMusicIdentity("Song", "Artist", "Album", 200.4);
        Assert.Equal(200, song.Duration);
        memory.AdjustOffset(1);
        Assert.Equal(0, memory.Offset);
        memory.Select(song);
        memory.AdjustOffset(0.3);
        Assert.Equal(0.25, memory.Offset);
        memory.AdjustOffset(50);
        Assert.Equal(10, memory.Offset);
        Assert.True(memory.Replace(Verses([1]), song));
        Assert.Equal(0, memory.Offset);
        Assert.False(memory.Replace(null, new NotchMusicIdentity("Other", "Artist", "Album", 200)));
        memory.Select(new NotchMusicIdentity("Other", "Artist", "Album", 200));
        Assert.Null(memory.Lyrics);
    }

    [Fact]
    public void LookupSendsTheRecordingAndRefusesUnusableMetadata()
    {
        var url = NotchLyricsSupport.LookupUrl(new NotchMusicIdentity("Song & Dance", "Artist", "Album - Single", 215.2));
        Assert.Equal("https://lrclib.net/api/get?track_name=Song%20%26%20Dance&artist_name=Artist&album_name=Album&duration=215.0",
                     url!.AbsoluteUri);
        Assert.Null(NotchLyricsSupport.LookupUrl(new NotchMusicIdentity("Song", "", "", 200)));
        Assert.Null(NotchLyricsSupport.LookupUrl(new NotchMusicIdentity("Song", "Artist", "", 0)));
        Assert.Null(NotchLyricsSupport.LookupUrl(new NotchMusicIdentity("Song", "Artist", "", 4000)));
    }

    [Fact]
    public void FoldingMatchesLatinAccentsButKeepsCyrillicLetters()
    {
        Assert.Equal("beyonce", NotchLyricsSupport.Key("Beyoncé"));
        Assert.Equal("еж", NotchLyricsSupport.Key("Ёж"));
        Assert.NotEqual(NotchLyricsSupport.Key("й"), NotchLyricsSupport.Key("и"));
        Assert.Equal(new[] { "A", "B", "C" }, NotchLyricsSupport.ArtistNames("A, B feat. C"));
    }
}
