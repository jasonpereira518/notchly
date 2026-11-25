// SPDX-License-Identifier: GPL-3.0-or-later
// What Spotify's web API says about the player, the queue, the playlists and the devices, read into small records.
// A Windows addition (upstream reads the Mac player's own queue). Every reader takes the reply text and gives null or
// an empty list for anything it cannot use, so a changed reply degrades the page instead of breaking it.
using System.Text.Json;

namespace Notchly.Core.Notch.Music;

public sealed record NotchQueueItem(string Uri, string Title, string Artist, string? ArtUrl, double DurationSeconds);

public sealed record SpotifyPlayerState(bool IsPlaying, bool Shuffle, NotchRepeatMode Repeat, string? TrackUri, string? DeviceId);

public sealed record SpotifyPlaylist(string Id, string Uri, string Name, string? ArtUrl, int TrackCount, string Owner);

public sealed record SpotifyDevice(string Id, string Name, bool IsActive, bool IsRestricted);

public static class SpotifyModels
{
    /// <summary>Liked Songs has no playlist of its own: it plays as the user's "collection" context.</summary>
    public static string LikedSongsUri(string userId) => $"spotify:user:{userId}:collection";

    /// <summary>The picture closest to `wanted` pixels wide without being smaller, else the largest there is.</summary>
    public static string? PickArt(JsonElement images, int wanted = 64)
    {
        if (images.ValueKind != JsonValueKind.Array) return null;
        string? best = null, largest = null;
        int bestWidth = int.MaxValue, largestWidth = -1;
        foreach (var image in images.EnumerateArray())
        {
            var url = Text(image, "url");
            if (string.IsNullOrEmpty(url)) continue;
            var width = image.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var n) ? n : 0;
            if (width > largestWidth) (largest, largestWidth) = (url, width);
            if (width >= wanted && width < bestWidth) (best, bestWidth) = (url, width);
        }
        return best ?? largest;
    }

    /// <summary>
    /// The songs after the current one from `GET /me/player/queue`. Podcast episodes and ads are left out, since the
    /// page shows songs; so are entries without a name.
    /// </summary>
    public static IReadOnlyList<NotchQueueItem> ParseQueue(string? json)
    {
        using var document = Parse(json);
        if (document is null || !document.RootElement.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array) return [];
        var items = new List<NotchQueueItem>();
        foreach (var entry in queue.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "type") != "track") continue;
            var title = Text(entry, "name");
            var uri = Text(entry, "uri");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(uri)) continue;
            var artists = entry.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? string.Join(", ", list.EnumerateArray().Select(a => Text(a, "name")).Where(n => !string.IsNullOrEmpty(n)))
                : "";
            var art = entry.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object && album.TryGetProperty("images", out var images)
                ? PickArt(images) : null;
            var milliseconds = entry.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var ms) ? ms : 0;
            items.Add(new NotchQueueItem(uri, title, artists, art, milliseconds / 1000));
        }
        return items;
    }

    /// <summary>`GET /me/player`. Null for the empty answer (nothing is playing anywhere) or an unreadable one.</summary>
    public static SpotifyPlayerState? ParsePlayer(string? json)
    {
        using var document = Parse(json);
        if (document is null) return null;
        var root = document.RootElement;
        var item = root.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? Text(i, "uri") : null;
        var device = root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object ? Text(d, "id") : null;
        return new SpotifyPlayerState(
            root.TryGetProperty("is_playing", out var playing) && playing.ValueKind == JsonValueKind.True,
            root.TryGetProperty("shuffle_state", out var shuffle) && shuffle.ValueKind == JsonValueKind.True,
            NotchRepeatSupport.FromSpotify(Text(root, "repeat_state")),
            string.IsNullOrEmpty(item) ? null : item,
            string.IsNullOrEmpty(device) ? null : device);
    }

    /// <summary>`GET /me/playlists`: those with an id, a uri and a name.</summary>
    public static IReadOnlyList<SpotifyPlaylist> ParsePlaylists(string? json)
    {
        using var document = Parse(json);
        if (document is null || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var playlists = new List<SpotifyPlaylist>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = Text(entry, "id");
            var uri = Text(entry, "uri");
            var name = Text(entry, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(uri) || string.IsNullOrWhiteSpace(name)) continue;
            // Spotify renamed the track list to `items` and still sends `tracks` for now: take whichever carries a total.
            var count = Total(entry, "items") ?? Total(entry, "tracks") ?? 0;
            var owner = entry.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? Text(o, "display_name") ?? "" : "";
            var art = entry.TryGetProperty("images", out var images) ? PickArt(images) : null;
            playlists.Add(new SpotifyPlaylist(id, uri, name, art, count, owner));
        }
        return playlists;
    }

    /// <summary>`GET /me/player/devices`.</summary>
    public static IReadOnlyList<SpotifyDevice> ParseDevices(string? json)
    {
        using var document = Parse(json);
        if (document is null || !document.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return [];
        var list = new List<SpotifyDevice>();
        foreach (var entry in devices.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = Text(entry, "id");
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new SpotifyDevice(id, Text(entry, "name") ?? "Spotify",
                entry.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
                entry.TryGetProperty("is_restricted", out var r) && r.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>
    /// The device to start a playlist on: the active one, else the first that accepts commands. Null when Spotify is
    /// running nowhere, and the app has to be started first.
    /// </summary>
    public static SpotifyDevice? PickDevice(IReadOnlyList<SpotifyDevice> devices) =>
        devices.FirstOrDefault(d => d.IsActive && !d.IsRestricted) ?? devices.FirstOrDefault(d => !d.IsRestricted);

    /// <summary>`GET /me/library/contains` answers a list of booleans in the order asked.</summary>
    public static bool? ParseContains(string? json)
    {
        using var document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    /// <summary>The `id` of `GET /me`, which names Liked Songs' context.</summary>
    public static string? ParseUserId(string? json)
    {
        using var document = Parse(json);
        return document is null ? null : Text(document.RootElement, "id");
    }

    /// <summary>A track URI is the only kind that can be starred; a podcast episode or a local file cannot.</summary>
    public static bool IsStarrable(string? uri) => uri is not null && uri.StartsWith("spotify:track:", StringComparison.Ordinal);

    private static int? Total(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) ? count : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }
}
