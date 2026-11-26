// SPDX-License-Identifier: GPL-3.0-or-later
// Songs starred inside Notchly, for players with no library of their own (a browser tab, Media Player). A Windows
// addition. Spotify stars go to Liked Songs instead and never reach this list.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchly.Core.Notch.Music;

public sealed record NotchStarEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist);

public sealed class NotchStarStore
{
    /// <summary>The most songs kept; the oldest stars drop off past this.</summary>
    public const int Limit = 2000;

    private readonly List<NotchStarEntry> _entries;

    public NotchStarStore(IEnumerable<NotchStarEntry>? entries = null) =>
        _entries = (entries ?? []).Where(e => e.Key.Length > 0).DistinctBy(e => e.Key).Take(Limit).ToList();

    /// <summary>Newest star first.</summary>
    public IReadOnlyList<NotchStarEntry> Entries => _entries;

    /// <summary>
    /// One song's identity: title and artist without case or extra spacing. Album and length are left out
    /// because players disagree on them for the same recording.
    /// </summary>
    public static string KeyFor(string? title, string? artist)
    {
        static string Clean(string? text) =>
            string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var song = Clean(title);
        return song.Length == 0 ? "" : $"{song}|{Clean(artist)}";
    }

    public bool Contains(string? title, string? artist)
    {
        var key = KeyFor(title, artist);
        return key.Length > 0 && _entries.Any(e => e.Key == key);
    }

    /// <summary>Stars the song or takes the star off; returns whether it is starred now. Nameless songs cannot be starred.</summary>
    public bool Toggle(string? title, string? artist)
    {
        var key = KeyFor(title, artist);
        if (key.Length == 0) return false;
        var at = _entries.FindIndex(e => e.Key == key);
        if (at >= 0)
        {
            _entries.RemoveAt(at);
            return false;
        }
        _entries.Insert(0, new NotchStarEntry(key, (title ?? "").Trim(), (artist ?? "").Trim()));
        if (_entries.Count > Limit) _entries.RemoveRange(Limit, _entries.Count - Limit);
        return true;
    }

    public void Clear() => _entries.Clear();

    public string ToJson() => JsonSerializer.Serialize(_entries);

    /// <summary>The list from its saved text; damaged or empty text gives an empty list.</summary>
    public static NotchStarStore FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new NotchStarStore();
        try { return new NotchStarStore(JsonSerializer.Deserialize<List<NotchStarEntry>>(json)?.Where(e => e is not null && e.Key is not null)); }
        catch (JsonException) { return new NotchStarStore(); }
    }
}
