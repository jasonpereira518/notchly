// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchLyricsSupport.swift (enum NotchLyricsSupport
// and its MusicBrainz extension): LRCLIB/MusicBrainz request URLs, strict and catalog decoders,
// candidate scoring, text folding and the bounded LRC parser. Behaviour is pinned by the ported
// corpus tests (Tests/NotchLyricsCorpusTests.swift).
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Notchly.Core.Notch.Lyrics;

public static class NotchLyricsSupport
{
    public const int MaximumBytes = 128 * 1024;
    public const int MaximumLines = 2000;
    /// <summary>Search replies hold up to twenty complete records rather than one.</summary>
    public const int MaximumSearchBytes = 1024 * 1024;

    public sealed record Candidate(NotchLyrics Lyrics, int Score, double Distance, int? RecordId, string StableKey);

    public sealed record ArtistIdentityInfo(Guid Id, IReadOnlyList<string> Names, IReadOnlyList<string> QueryNames);

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // MARK: URLs

    public static Uri? LookupUrl(NotchMusicIdentity track)
    {
        var album = CatalogAlbum(track.Album);
        if (!new[] { track.Title, track.Artist }.All(v => v.Trim().Length > 0 && Utf8(v) <= 1024)
            || (album.Length > 0 && Utf8(album) > 1024)
            || !double.IsFinite(track.Duration) || track.Duration < 1 || track.Duration > 3600) return null;
        // The release only separates two recordings of one song; a player that reports none sends none.
        var items = new List<(string, string)> { ("track_name", track.Title), ("artist_name", track.Artist) };
        if (album.Length > 0) items.Add(("album_name", album));
        items.Add(("duration", track.Duration.ToString("0.0", CultureInfo.InvariantCulture)));
        return Url("https://lrclib.net/api/get", items);
    }

    /// <summary>
    /// The player's exact credits first. Fallbacks name the primary artist and normalise
    /// punctuation and Latin accents. Recording qualifiers (live, remix…) are never removed.
    /// </summary>
    public static IReadOnlyList<Uri> SearchUrls(NotchMusicIdentity track)
    {
        if (LookupUrl(track) is null) return [];
        var primary = ArtistNames(track.Artist).FirstOrDefault() ?? track.Artist;
        var variants = new[] { (track.Title, track.Artist), (track.Title, primary), (SearchText(track.Title), SearchText(primary)) };
        var urls = new List<Uri>();
        var seen = new HashSet<string>();
        foreach (var (title, artist) in variants)
        {
            if (title.Length == 0 || artist.Length == 0 || !seen.Add(title.ToLowerInvariant() + "\n" + artist.ToLowerInvariant())) continue;
            var url = Url("https://lrclib.net/api/search", [("track_name", title), ("artist_name", artist)]);
            if (!urls.Contains(url)) urls.Add(url);
        }
        return urls;
    }

    /// <summary>One broader query, only after precise searches have no eligible result.</summary>
    public static bool PrefersDiscovery(NotchMusicIdentity track) => CatalogTitle(track.Title) != track.Title;

    public static IReadOnlyList<Uri> DiscoveryUrls(NotchMusicIdentity track)
    {
        var primary = ArtistNames(track.Artist).FirstOrDefault() ?? track.Artist;
        if (Key(primary).Length == 0 || LookupUrl(track) is null) return [];
        var stripped = Regex.Replace(CatalogTitle(track.Title),
            @"\s*[\(\[][^\)\]]*(?:mix|edit|version|live|acoustic|instrumental|karaoke)[^\)\]]*[\)\]]", "", Insensitive).Trim();
        // An artist-only full-text query discovers word-boundary variants without a word splitter.
        return [stripped == track.Title
            ? Url("https://lrclib.net/api/search", [("q", primary)])
            : Url("https://lrclib.net/api/search", [("track_name", stripped), ("artist_name", primary)])];
    }

    // MARK: Decoding

    /// <summary>The strict /get decoder: title, artist, album and a duration within two seconds.</summary>
    public static NotchLyrics? Decode(byte[] data, NotchMusicIdentity track)
    {
        if (data.Length > MaximumBytes || ParseObject(data) is not { } obj) return null;
        if (!Equal(Str(obj, "trackName"), track.Title) || !Equal(Str(obj, "artistName"), track.Artist)
            || !AlbumMatches(Str(obj, "albumName"), track.Album)
            || Num(obj, "duration") is not { } duration || !double.IsFinite(duration)
            || Math.Abs(duration - track.Duration) > 2) return null;
        var plain = (Str(obj, "plainLyrics") ?? "").Trim();
        var instrumental = IsTrue(obj, "instrumental");
        var parsed = Parse(Str(obj, "syncedLyrics") ?? "", track.Duration);
        if (!instrumental && plain.Length == 0 && parsed.Count == 0) return null;
        return new NotchLyrics(instrumental ? [] : parsed, instrumental ? "" : plain, instrumental);
    }

    /// <summary>
    /// /get can return canonical punctuation and credit separators even for an exact query. Accept
    /// those cosmetic differences without relaxing its album or recording-length constraints.
    /// </summary>
    public static NotchLyrics? DecodeCatalogMatch(byte[] data, NotchMusicIdentity track)
    {
        if (data.Length > MaximumBytes || ParseNode(data) is not JsonObject obj) return null;
        if (obj["trackName"]?.GetValueKind() != JsonValueKind.String || obj["artistName"]?.GetValueKind() != JsonValueKind.String
            || obj["albumName"]?.GetValueKind() != JsonValueKind.String) return null;
        var title = obj["trackName"]!.GetValue<string>();
        var artist = obj["artistName"]!.GetValue<string>();
        var album = obj["albumName"]!.GetValue<string>();
        if (TitleKey(track.Title).Length == 0 || !TitlesMatch(title, track) || Key(track.Artist).Length == 0
            || !CreditsMatch(artist, track.Artist)
            || (track.Album.Trim().Length > 0 && Key(CatalogAlbum(album)) != Key(CatalogAlbum(track.Album)))) return null;
        obj["trackName"] = track.Title;
        obj["artistName"] = track.Artist;
        obj["albumName"] = track.Album;
        return Decode(JsonSerializer.SerializeToUtf8Bytes(obj, Compact), track);
    }

    public static IReadOnlyList<Candidate> SearchCandidates(byte[] data, NotchMusicIdentity track, double durationTolerance = 2,
                                                           IReadOnlyCollection<string>? verifiedArtistAliases = null)
    {
        var aliases = (verifiedArtistAliases ?? []).Select(Key).ToHashSet();
        if (data.Length > MaximumSearchBytes || ParseObjects(data) is not { Count: <= 20 } objects) return [];
        var result = new List<Candidate>();
        foreach (var obj in objects)
        {
            if (Str(obj, "trackName") is not { } title || Str(obj, "artistName") is not { } artist
                || Str(obj, "albumName") is not { } album
                || !TitlesMatch(title, track) || TitleKey(title).Length == 0
                || !SearchCreditsMatch(artist, track, aliases)
                || !RecordingKinds(album).SetEquals(RecordingKinds(track.Album))
                || Num(obj, "duration") is not { } duration || !double.IsFinite(duration)
                || Math.Abs(duration - track.Duration) > durationTolerance
                || JsonSerializer.SerializeToUtf8Bytes(obj, Compact).Length > MaximumBytes) continue;
            // Reuse the bounded parser and payload validation only after recording matching.
            var plain = (Str(obj, "plainLyrics") ?? "").Trim();
            var instrumental = IsTrue(obj, "instrumental");
            var lines = CatalogLines(Str(obj, "syncedLyrics") ?? "", track.Duration);
            if (!instrumental && plain.Length == 0 && lines.Count == 0) continue;
            var lyrics = new NotchLyrics(instrumental ? [] : lines, instrumental ? "" : plain, instrumental);
            var score = (Key(CatalogAlbum(album)) == Key(CatalogAlbum(track.Album)) ? 8 : 0)
                + (Equal(title, track.Title) ? 2 : 0)
                + (CreditsMatch(artist, track.Artist) || (ArtistNames(track.Artist).Count == 1 && aliases.Contains(Key(artist))) ? 4 : 0)
                + (lines.Count > 0 && !instrumental ? 1 : 0);
            var id = Int(obj, "id") is { } value && value > 0 ? value : (int?)null;
            var stable = plain + "\n" + string.Join("\n", lines.Select(l => l.Time.ToString("R", CultureInfo.InvariantCulture) + ":" + l.Text));
            result.Add(new Candidate(lyrics, score, Math.Abs(duration - track.Duration), id, stable));
        }
        return result;
    }

    /// <summary>A known artist/title with the wrong length cannot be repaired by renaming the artist.</summary>
    public static bool ContainsCatalogRecording(byte[] data, NotchMusicIdentity track)
    {
        if (data.Length > MaximumSearchBytes || ParseObjects(data) is not { Count: <= 20 } objects) return false;
        return objects.Any(obj => Str(obj, "trackName") is { } title && TitlesMatch(title, track)
                                  && Str(obj, "artistName") is { } artist && Str(obj, "albumName") is { } album
                                  && RecordingKinds(album).SetEquals(RecordingKinds(track.Album))
                                  && SearchCreditsMatch(artist, track));
    }

    /// <summary>All candidates passed recording checks: resolve ties consistently, never by API order.</summary>
    public static NotchLyrics? BestLyrics(IEnumerable<Candidate> candidates) =>
        candidates.OrderByDescending(c => c.Score)
                  .ThenBy(c => c.Distance)
                  .ThenBy(c => c.RecordId ?? int.MaxValue)
                  .ThenBy(c => c.StableKey, StringComparer.Ordinal)
                  .FirstOrDefault()?.Lyrics;

    /// <summary>Incorrect catalog durations can accompany a full LRC: keep plain text instead of a truncated timeline.</summary>
    private static IReadOnlyList<NotchLyricLine> CatalogLines(string source, double duration)
    {
        var lines = Parse(source, Math.Max(duration, 604_800));
        if ((lines.Count > 0 ? lines[^1].Time : 0) > duration + 2) return [];
        return lines.Where(l => l.Time <= duration).ToList();
    }

    // MARK: LRC

    public static IReadOnlyList<NotchLyricLine> Parse(string source, double duration)
    {
        if (Utf8(source) > MaximumBytes || !double.IsFinite(duration) || duration <= 0) return [];
        var entries = new List<NotchLyricLine>();
        var expandedBytes = 0;
        var fileOffset = 0.0;
        foreach (var rawLine in source.Split(NewlineCharacters, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().Trim('﻿').Trim();
            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']')
                && double.TryParse(line[8..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && Math.Abs(value) <= 60_000)
            {
                fileOffset = value / 1000;
                continue;
            }
            var text = line.AsSpan();
            var times = new List<double>();
            while (text.Length > 0 && text[0] == '[' && text.IndexOf(']') is var end and > 0)
            {
                if (Timestamp(text[1..end]) is not { } time) break;
                if (times.Count >= MaximumLines) return [];
                times.Add(time);
                text = text[(end + 1)..];
            }
            var words = text.ToString().Trim(' ', '\t');
            // Repeated time tags expand one line into many verses; bound that expansion first.
            var bytesPerEntry = Utf8(words) + (words.Length == 0 ? 0 : 1);
            // Empty timed lines end the preceding verse during an instrumental passage.
            foreach (var time in times)
            {
                if (entries.Count >= MaximumLines || bytesPerEntry > MaximumBytes - expandedBytes) return [];
                expandedBytes += bytesPerEntry;
                entries.Add(new NotchLyricLine(time, words));
            }
        }
        var adjusted = entries
            .Select((line, index) => (index, line: line with { Time = Math.Max(0, line.Time - fileOffset) }))
            .Where(e => e.line.Time <= duration)
            .OrderBy(e => e.line.Time).ThenBy(e => e.index)
            .ToList();
        var result = new List<NotchLyricLine>();
        double? groupTime = null;
        var groupWords = new List<string>();
        void FinishGroup()
        {
            if (groupTime is not { } t) return;
            result.Add(new NotchLyricLine(t, string.Join("\n", groupWords)));
            groupWords.Clear();
        }
        foreach (var (_, line) in adjusted)
        {
            if (line.Time != groupTime) { FinishGroup(); groupTime = line.Time; }
            if (line.Text.Length > 0) groupWords.Add(line.Text);
        }
        FinishGroup();
        return result.Any(l => l.Text.Length > 0) ? result : [];
    }

    private static readonly char[] NewlineCharacters = ['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    private static double? Timestamp(ReadOnlySpan<char> value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || value[(colon + 1)..].IndexOf(':') >= 0) return null;
        var minutesText = value[..colon];
        var secondsText = value[(colon + 1)..];
        foreach (var c in minutesText) if (!char.IsDigit(c)) return null;
        foreach (var c in secondsText) if (!char.IsDigit(c) && c != '.') return null;
        if (!double.TryParse(minutesText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds < 0 || seconds >= 60 || minutes > 10_080) return null;
        return minutes * 60 + seconds;
    }

    // MARK: Matching

    /// <summary>Featured credits are artist metadata, and Original Mix is the default version.</summary>
    private static string CatalogTitle(string title)
    {
        var withoutFeature = Regex.Replace(title, @"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s+[^\)\]]+[\)\]]", "", Insensitive);
        return Regex.Replace(withoutFeature, @"\s*(?:[\(\[]\s*original mix\s*[\)\]]|[-–—]\s*original mix)\s*$", "", Insensitive).Trim();
    }

    private static readonly Regex FeaturedPattern = new(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s+([^\)\]]+)[\)\]]", Insensitive);

    private static List<string> FeaturedNames(string title) =>
        FeaturedPattern.Matches(title).SelectMany(m => ArtistNames(m.Groups[1].Value)).ToList();

    private static bool TitlesMatch(string title, NotchMusicIdentity track)
    {
        if (TitleKey(track.Title).Length == 0 || TitleKey(title) != TitleKey(track.Title)) return false;
        var expected = FeaturedNames(track.Title).Concat(ArtistNames(track.Artist).Skip(1)).ToList();
        return FeaturedNames(title).All(guest => expected.Any(name =>
            Key(guest) == Key(name)
            || (SearchText(guest).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1
                && SearchText(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == SearchText(guest))));
    }

    private static string TitleKey(string title) => Key(CatalogTitle(title));

    /// <summary>Catalog credits may omit or reorder contributors, but one performer is not a whole collaboration.</summary>
    private static bool SearchCreditsMatch(string artist, NotchMusicIdentity track, IReadOnlySet<string>? aliases = null)
    {
        aliases ??= new HashSet<string>();
        if (ArtistNames(track.Artist).FirstOrDefault() is not { } primary) return false;
        if (Key(artist) == Key(track.Artist) || aliases.Contains(Key(artist))) return true;
        var names = ArtistNames(artist).Select(Key).ToHashSet();
        var expected = ArtistNames(track.Artist).Concat(FeaturedNames(track.Title)).Select(Key).ToHashSet();
        expected.UnionWith(aliases);
        return (names.Contains(Key(primary)) || names.Overlaps(aliases)) && names.IsSubsetOf(expected);
    }

    private static bool CreditsMatch(string first, string second) =>
        Key(first) == Key(second) || ArtistNames(first).Select(Key).ToHashSet().SetEquals(ArtistNames(second).Select(Key));

    private static readonly string[] RecordingWords =
        ["live", "concert", "концерт", "концертная", "remix", "remixes", "ремикс", "acoustic", "акустика", "instrumental", "karaoke"];

    private static HashSet<string> RecordingKinds(string album)
    {
        var words = SearchText(album).Split(' ').ToHashSet();
        return RecordingWords.Where(words.Contains).ToHashSet();
    }

    /// <summary>Apple Music names singles "Title - Single"; LRCLIB stores the release title alone.</summary>
    public static string CatalogAlbum(string album)
    {
        var trimmed = album.Trim();
        foreach (var suffix in new[] { " - Single", " - EP" })
        {
            if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length];
        }
        return trimmed;
    }

    /// <summary>A player that reports no album cannot disagree about one.</summary>
    private static bool AlbumMatches(string? value, string expected)
    {
        if (expected.Trim().Length == 0) return true;
        return Equal(value is null ? null : CatalogAlbum(value), CatalogAlbum(expected));
    }

    private static bool Equal(string? value, string expected)
    {
        if (value is null || expected.Length == 0) return false;
        return string.Compare(value.Trim(), expected.Trim(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase) == 0;
    }

    // MARK: Text folding

    /// <summary>Fold Latin accents and case without turning Cyrillic й into и; ё matches е.</summary>
    internal static string Folded(string text)
    {
        var builder = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var latin = true;
            foreach (var rune in element.EnumerateRunes()) if (rune.Value >= 0x0250) { latin = false; break; }
            builder.Append(latin ? StripMarks(element).ToLowerInvariant() : element.ToLowerInvariant());
        }
        return builder.ToString().Replace("ё", "е");
    }

    private static string StripMarks(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Words of letters, marks and numbers, as Foundation's alphanumerics character set.</summary>
    internal static string SearchText(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in Folded(text).EnumerateRunes())
        {
            if (IsAlphanumeric(rune)) current.Append(rune.ToString());
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return string.Join(" ", words);
    }

    private static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    internal static string Key(string text) => SearchText(text).Replace(" ", "");

    private static readonly Regex ArtistSeparators =
        new(@"[,;，、]|\s+[/&•]\s+|\s+(?:feat\.?|ft\.?|featuring)\s+", Insensitive);
    private static readonly Regex GroupPrefix = new(@"^группа\s+", Insensitive);

    internal static List<string> ArtistNames(string text) =>
        ArtistSeparators.Replace(text, "\n").Split('\n')
            .Select(part => GroupPrefix.Replace(part.Trim(), ""))
            .Where(part => part.Length > 0)
            .ToList();

    // MARK: MusicBrainz artist aliases (a last resort, never on every track)

    public static string ArtistCacheKey(NotchMusicIdentity track) =>
        Key(ArtistNames(track.Artist).FirstOrDefault() ?? track.Artist) + "|" + Key(CatalogAlbum(track.Album));

    private static string QuotedQuery(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static Uri? ArtistIdentityUrl(NotchMusicIdentity track)
    {
        if (ArtistNames(track.Artist).FirstOrDefault() is not { } primary || Key(primary).Length == 0 || Utf8(primary) > 256) return null;
        var phrase = QuotedQuery(primary);
        return Url("https://musicbrainz.org/ws/2/artist/", [("query", $"artist:{phrase} OR alias:{phrase}"), ("fmt", "json"), ("limit", "5")]);
    }

    /// <summary>Exact name/alias membership identifies candidates; search scores alone cannot.</summary>
    public static IReadOnlyList<ArtistIdentityInfo> ArtistIdentities(byte[] data, NotchMusicIdentity track, bool allowTruncated = false)
    {
        if (data.Length > MaximumBytes || ArtistNames(track.Artist).FirstOrDefault() is not { } primary || Key(primary).Length == 0
            || ParseObject(data) is not { } obj || Int(obj, "count") is not { } count || count < 0 || (count > 5 && !allowTruncated)
            || Objects(obj, "artists") is not { Count: <= 5 } artists) return [];
        var identities = new Dictionary<Guid, ArtistIdentityInfo>();
        foreach (var artist in artists)
        {
            if (Str(artist, "name") is not { } name || Utf8(name) > 256
                || Str(artist, "id") is not { } rawId || !Guid.TryParse(rawId, out var id)) continue;
            var aliases = Objects(artist, "aliases") ?? [];
            if (aliases.Count > 100) continue;
            var names = new[] { name, Str(artist, "sort-name") ?? "" }
                .Concat(aliases.Select(a => Str(a, "name")).OfType<string>())
                .Where(n => n.Length > 0 && Utf8(n) <= 256).ToList();
            if (!names.Any(n => Key(n) == Key(primary))) continue;
            var english = aliases.Where(a => Str(a, "locale") == "en" && IsTrue(a, "primary"))
                                 .Select(a => Str(a, "name")).OfType<string>()
                                 .Order(StringComparer.Ordinal).ToList();
            var queryNames = new List<string>();
            foreach (var value in english.Append(name))
            {
                if (Utf8(value) > 256 || Key(value) == Key(primary)) continue;
                if (!queryNames.Any(q => Key(q) == Key(value))) queryNames.Add(value);
            }
            identities[id] = new ArtistIdentityInfo(id, names, queryNames.Take(2).ToList());
        }
        return identities.Values.OrderBy(i => i.Id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal).ToList();
    }

    public static Uri? ArtistRecordingUrl(NotchMusicIdentity track, IReadOnlyList<ArtistIdentityInfo> candidates)
    {
        if (candidates.Count != 1 || Utf8(track.Title) > 256) return null;
        return Url("https://musicbrainz.org/ws/2/recording/",
            [("query", $"recording:{QuotedQuery(CatalogTitle(track.Title))} AND arid:{candidates[0].Id.ToString("D").ToLowerInvariant()}"),
             ("fmt", "json"), ("limit", "5")]);
    }

    public static ArtistIdentityInfo? RecordingArtistIdentity(byte[] data, NotchMusicIdentity track, IReadOnlyList<ArtistIdentityInfo> candidates)
    {
        if (candidates.Count != 1 || data.Length > MaximumBytes || ParseObject(data) is not { } obj
            || Int(obj, "count") is not { } count || count < 0 || Objects(obj, "recordings") is not { Count: <= 5 } recordings) return null;
        var source = candidates[0];
        var names = source.Names.ToList();
        var queries = source.QueryNames.ToList();
        var primary = ArtistNames(track.Artist).FirstOrDefault() ?? track.Artist;
        foreach (var recording in recordings)
        {
            if (Str(recording, "title") is not { } title || !TitlesMatch(title, track)
                || Num(recording, "length") is not { } length || !double.IsFinite(length) || Math.Abs(length / 1000 - track.Duration) > 2
                || Objects(recording, "artist-credit") is not { Count: 1 } credits
                || Child(credits[0], "artist") is not { } artist
                || Str(artist, "id") is not { } id || !Guid.TryParse(id, out var guid) || guid != source.Id
                || Str(credits[0], "name") is not { } credited || Key(credited).Length == 0 || Utf8(credited) > 256) continue;
            if (!names.Any(n => Key(n) == Key(credited))) names.Add(credited);
            if (Key(credited) != Key(primary) && !queries.Any(q => Key(q) == Key(credited))) queries.Add(credited);
        }
        if (queries.Count == 0) return null;
        return new ArtistIdentityInfo(source.Id, names, queries.Take(2).ToList());
    }

    public static Uri? ArtistAlbumUrl(NotchMusicIdentity track)
    {
        var album = CatalogAlbum(track.Album).Trim();
        if (album.Length == 0 || Utf8(album) > 256) return null;
        return Url("https://musicbrainz.org/ws/2/release-group/", [("query", QuotedQuery(album)), ("fmt", "json"), ("limit", "5")]);
    }

    /// <summary>An ambiguous short name needs a matching album credited to exactly one identity.</summary>
    public static ArtistIdentityInfo? ArtistIdentity(byte[] data, NotchMusicIdentity track, IReadOnlyList<ArtistIdentityInfo> candidates)
    {
        if (data.Length > MaximumBytes || ParseObject(data) is not { } obj || Int(obj, "count") is not { } count || count > 5
            || Objects(obj, "release-groups") is not { Count: <= 5 } groups) return null;
        var identities = new Dictionary<Guid, ArtistIdentityInfo>();
        foreach (var group in groups)
        {
            if (Str(group, "title") is not { } title || Key(title) != Key(CatalogAlbum(track.Album))
                || Objects(group, "artist-credit") is not { Count: 1 } credits
                || Child(credits[0], "artist") is not { } artist
                || Str(artist, "id") is not { } rawId || !Guid.TryParse(rawId, out var id)) continue;
            var nested = new JsonObject { ["count"] = 1, ["artists"] = new JsonArray(JsonNode.Parse(artist.GetRawText())) };
            var identity = candidates.FirstOrDefault(c => c.Id == id)
                ?? ArtistIdentities(JsonSerializer.SerializeToUtf8Bytes(nested, Compact), track).FirstOrDefault();
            if (identity is not null) identities[id] = identity;
        }
        return identities.Count == 1 ? identities.Values.First() : null;
    }

    public static IReadOnlyList<Uri> AliasSearchUrls(NotchMusicIdentity track, ArtistIdentityInfo identity) =>
        identity.QueryNames.Select(name => Url("https://lrclib.net/api/search", [("track_name", track.Title), ("artist_name", name)])).ToList();

    // MARK: JSON and URL helpers

    private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);

    private static Uri Url(string baseUrl, IEnumerable<(string Name, string Value)> items) =>
        new(baseUrl + "?" + string.Join("&", items.Select(i => Uri.EscapeDataString(i.Name) + "=" + Uri.EscapeDataString(i.Value))));

    private static JsonNode? ParseNode(byte[] data)
    {
        try { return JsonNode.Parse(data); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? ParseObject(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A top-level array of objects; any other element rejects the whole reply.</summary>
    private static List<JsonElement>? ParseObjects(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<JsonElement>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                list.Add(item.Clone());
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static JsonElement? Child(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static List<JsonElement>? Objects(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var list = new List<JsonElement>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            list.Add(item);
        }
        return list;
    }
}
