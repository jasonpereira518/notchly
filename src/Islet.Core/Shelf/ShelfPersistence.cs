// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream ShelfSupport.swift (ShelfPersistedItem, ShelfStoreLoad, ShelfPersistenceSupport):
// the saved shelf, read lossily so one unreadable entry never empties the whole shelf.
// Windows has no file bookmarks, so a missing file cannot be healed; the unplugged-drive rule
// (a file on a drive that is not connected right now is kept) stays.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Islet.Core.Shelf;

public sealed record ShelfPersistedItem(Guid Id, ShelfKind Kind, string Title, string? Text = null, string? Url = null,
                                        string? Path = null, IReadOnlyList<ShelfPersistedItem>? Children = null,
                                        bool Pinned = false);

/// <summary>What the saved blob turned out to be. They need opposite handling.</summary>
public abstract record ShelfStoreLoad
{
    /// <summary>No blob yet, or a blob that decoded whole (possibly to an emptied shelf).</summary>
    public sealed record Items(IReadOnlyList<ShelfPersistedItem> Value) : ShelfStoreLoad;
    /// <summary>Decoded, but some entries could not be read: do not save back or sweep their files.</summary>
    public sealed record Partial(IReadOnlyList<ShelfPersistedItem> Value) : ShelfStoreLoad;
    /// <summary>Not a shelf list at all: leave it, and its files, alone.</summary>
    public sealed record Unreadable : ShelfStoreLoad;
}

public static class ShelfPersistence
{
    public const int MaxLeaves = 200;
    public const int MaxTextLength = 200_000;
    public const int MaxDepth = 4;

    public static string? BoundedLiveText(string text)
    {
        var bounded = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        return bounded.Trim().Length == 0 ? null : bounded;
    }

    public static bool CanAdd(int existingLeaves, int newLeaves) =>
        existingLeaves >= 0 && newLeaves > 0 && existingLeaves <= MaxLeaves - newLeaves;

    // MARK: Encoding

    public static string Encode(IEnumerable<ShelfPersistedItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(EncodeItem(item));
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject EncodeItem(ShelfPersistedItem item)
    {
        var obj = new JsonObject
        {
            ["id"] = item.Id.ToString("D").ToUpperInvariant(),
            ["kind"] = item.Kind.ToString().ToLowerInvariant(),
            ["title"] = item.Title,
        };
        if (item.Text is not null) obj["text"] = item.Text;
        if (item.Url is not null) obj["url"] = item.Url;
        if (item.Path is not null) obj["path"] = item.Path;
        if (item.Children is { } children)
        {
            var array = new JsonArray();
            foreach (var child in children) array.Add(EncodeItem(child));
            obj["children"] = array;
        }
        // Stored only when true, so the common case adds nothing.
        if (item.Pinned) obj["pinned"] = true;
        return obj;
    }

    // MARK: Decoding

    /// <summary>The only way into the saved shelf: a failed decode can never pass for an empty shelf.</summary>
    public static ShelfStoreLoad Load(string? json)
    {
        if (json is null) return new ShelfStoreLoad.Items([]);
        JsonArray? array;
        try { array = JsonNode.Parse(json) as JsonArray; }
        catch (JsonException) { return new ShelfStoreLoad.Unreadable(); }
        if (array is null) return new ShelfStoreLoad.Unreadable();
        var items = array.Select(DecodeItem).OfType<ShelfPersistedItem>().ToList();
        // A stored list where nothing survived is one this build cannot read, not an emptied shelf.
        if (array.Count > 0 && items.Count == 0) return new ShelfStoreLoad.Unreadable();
        return StoredCount(array) == ReadCount(items) ? new ShelfStoreLoad.Items(items) : new ShelfStoreLoad.Partial(items);
    }

    private static ShelfPersistedItem? DecodeItem(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        ShelfKind kind;
        switch (Str(obj, "kind"))
        {
            case "file": kind = ShelfKind.File; break;
            case "text": kind = ShelfKind.Text; break;
            case "link": kind = ShelfKind.Link; break;
            case "batch": kind = ShelfKind.Batch; break;
            default: return null; // an unknown kind drops only this entry
        }
        var id = Guid.TryParse(Str(obj, "id"), out var parsed) ? parsed : Guid.NewGuid();
        var children = obj["children"] is JsonArray childArray
            ? childArray.Select(DecodeItem).OfType<ShelfPersistedItem>().ToList()
            : null;
        var pinned = obj["pinned"] is JsonValue p && p.GetValueKind() == JsonValueKind.True;
        return new ShelfPersistedItem(id, kind, Str(obj, "title") ?? "", Str(obj, "text"), Str(obj, "url"), Str(obj, "path"), children, pinned);
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int StoredCount(JsonArray array) =>
        array.Sum(entry => 1 + (entry is JsonObject obj && obj["children"] is JsonArray children ? StoredCount(children) : 0));

    private static int ReadCount(IEnumerable<ShelfPersistedItem> items) =>
        items.Sum(item => 1 + ReadCount(item.Children ?? []));

    // MARK: Sanitizing

    /// <summary>
    /// For a path on a drive or network share, the root that must exist for the file's absence to
    /// mean anything. Callers treat a missing file under a missing root as still there: an external
    /// drive can connect after Islet starts, and pruning would lose the item for good.
    /// </summary>
    public static string? VolumeRoot(string path)
    {
        try { return System.IO.Path.GetPathRoot(path) is { Length: > 0 } root ? root : null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Drops entries that can no longer be honoured (missing files, blank text, invalid links),
    /// truncates text, collapses single-child piles (the survivor inherits the pin), drops empty
    /// piles, and stops at 200 leaves and depth 4.
    /// </summary>
    public static IReadOnlyList<ShelfPersistedItem> Sanitized(IReadOnlyList<ShelfPersistedItem> items, Func<string, bool> fileExists)
    {
        var remaining = MaxLeaves;
        return Sanitized(items, 0, ref remaining, fileExists);
    }

    private static List<ShelfPersistedItem> Sanitized(IReadOnlyList<ShelfPersistedItem> items, int depth, ref int remaining,
                                                      Func<string, bool> fileExists)
    {
        var result = new List<ShelfPersistedItem>();
        if (depth >= MaxDepth) return result;
        foreach (var item in items)
        {
            if (remaining <= 0) break;
            switch (item.Kind)
            {
                case ShelfKind.File:
                    if (string.IsNullOrEmpty(item.Path) || !fileExists(item.Path)) continue;
                    remaining--;
                    result.Add(new ShelfPersistedItem(item.Id, ShelfKind.File, item.Title, Path: item.Path, Pinned: item.Pinned));
                    break;
                case ShelfKind.Text:
                    if (item.Text is not { } text || text.Trim().Length == 0) continue;
                    remaining--;
                    result.Add(new ShelfPersistedItem(item.Id, ShelfKind.Text, item.Title,
                                                      Text: text.Length > MaxTextLength ? text[..MaxTextLength] : text, Pinned: item.Pinned));
                    break;
                case ShelfKind.Link:
                    if (item.Url is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.IsFile
                        || string.IsNullOrEmpty(url.Scheme)) continue;
                    remaining--;
                    result.Add(new ShelfPersistedItem(item.Id, ShelfKind.Link, item.Title, Url: raw, Pinned: item.Pinned));
                    break;
                case ShelfKind.Batch:
                    var children = Sanitized(item.Children ?? [], depth + 1, ref remaining, fileExists);
                    if (children.Count == 0) continue;
                    if (children.Count == 1)
                    {
                        result.Add(item.Pinned ? children[0] with { Pinned = true } : children[0]);
                        continue;
                    }
                    result.Add(new ShelfPersistedItem(item.Id, ShelfKind.Batch, item.Title, Children: children, Pinned: item.Pinned));
                    break;
            }
        }
        return result;
    }
}
