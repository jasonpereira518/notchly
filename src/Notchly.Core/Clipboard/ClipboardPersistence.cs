// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the file handling of upstream Sources/Vorssaint/Services/Clipboard/ClipboardHistoryService.swift: the history is a JSON array
// with a size ceiling, pinned entries written first, and a lenient reader. The file is Windows' own (dates are ISO text).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notchly.Core.Clipboard;

public static class ClipboardPersistence
{
    public const long MaxEncodedBytes = 96L * 1024 * 1024;

    private static string KindName(ClipboardKind kind) => kind switch { ClipboardKind.Image => "image", ClipboardKind.Files => "files", _ => "text" };

    private static ClipboardKind ParseKind(string? name) => name switch { "image" => ClipboardKind.Image, "files" => ClipboardKind.Files, _ => ClipboardKind.Text };

    private static JsonObject ToNode(ClipboardEntry e)
    {
        var node = new JsonObject
        {
            ["id"] = e.Id.ToString(),
            ["kind"] = KindName(e.Kind),
            ["copiedAt"] = e.CopiedAt.ToString("o"),
        };
        if (e.Text.Length > 0) node["text"] = e.Text;
        if (e.PinnedAt is { } pinned) node["pinnedAt"] = pinned.ToString("o");
        if (e.FilePaths.Count > 0) node["filePaths"] = new JsonArray(e.FilePaths.Select(p => (JsonNode?)p).ToArray());
        if (e.ImageFile is not null) node["imageFile"] = e.ImageFile;
        if (e.ImageHash is not null) node["imageHash"] = e.ImageHash;
        if (e.ImageWidth is { } w) node["imageWidth"] = w;
        if (e.ImageHeight is { } h) node["imageHeight"] = h;
        if (e.SourceApp is not null) node["sourceApp"] = e.SourceApp;
        if (e.SourcePath is not null) node["sourcePath"] = e.SourcePath;
        return node;
    }

    /// <summary>
    /// The file's text: pinned entries first, then the rest, and an entry that would take the file past the ceiling is left out
    /// (so the oldest recent entries go before any pinned one).
    /// </summary>
    public static string Encode(IReadOnlyList<ClipboardEntry> entries, long maxBytes = MaxEncodedBytes)
    {
        var array = new JsonArray();
        long used = 2;
        foreach (var entry in ClipboardHistorySupport.Ordered(entries))
        {
            var node = ToNode(entry);
            var size = Encoding.UTF8.GetByteCount(node.ToJsonString()) + 1;
            if (used + size > maxBytes) continue;
            used += size;
            array.Add(node);
        }
        return array.ToJsonString();
    }

    /// <summary>Whether every pinned entry would survive being written; a pin or edit that would lose one is refused.</summary>
    public static bool PinnedEntriesFit(IReadOnlyList<ClipboardEntry> entries, long maxBytes = MaxEncodedBytes)
    {
        long used = 2;
        foreach (var entry in entries.Where(e => e.IsPinned))
        {
            used += Encoding.UTF8.GetByteCount(ToNode(entry).ToJsonString()) + 1;
            if (used > maxBytes) return false;
        }
        return true;
    }

    /// <summary>
    /// The entries in a file's text, or null when it cannot be read (so the caller leaves the file alone). An entry that is missing its
    /// date, or the content its kind needs, is dropped on its own; a missing kind is text and a missing id is a new one.
    /// </summary>
    public static IReadOnlyList<ClipboardEntry>? Decode(string? json, long maxBytes = MaxEncodedBytes)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > maxBytes) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not JsonArray array) return null;

        var list = new List<ClipboardEntry>();
        foreach (var item in array)
        {
            if (item is not JsonObject o) continue;
            if (!DateTimeOffset.TryParse(o["copiedAt"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var copied)) continue;
            DateTimeOffset? pinned = DateTimeOffset.TryParse(o["pinnedAt"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var p) ? p : null;
            var kind = ParseKind(o["kind"]?.GetValue<string>());
            var text = o["text"]?.GetValue<string>() ?? "";
            var files = o["filePaths"] is JsonArray fa ? fa.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() : [];
            var image = o["imageFile"]?.GetValue<string>();
            var ok = kind switch
            {
                ClipboardKind.Text => text.Trim().Length > 0,
                ClipboardKind.Files => files.Count > 0,
                _ => !string.IsNullOrEmpty(image),
            };
            if (!ok) continue;
            list.Add(new ClipboardEntry
            {
                Id = Guid.TryParse(o["id"]?.GetValue<string>(), out var id) ? id : Guid.NewGuid(),
                Text = text, CopiedAt = copied, PinnedAt = pinned, Kind = kind, FilePaths = files, ImageFile = image,
                ImageHash = o["imageHash"]?.GetValue<string>(),
                ImageWidth = o["imageWidth"]?.GetValue<int>(), ImageHeight = o["imageHeight"]?.GetValue<int>(),
                SourceApp = o["sourceApp"]?.GetValue<string>(), SourcePath = o["sourcePath"]?.GetValue<string>(),
            });
        }
        return ClipboardHistorySupport.Ordered(list);
    }

    /// <summary>Image files in the folder that no entry refers to: they are swept after a write and when the history loads.</summary>
    public static IReadOnlyList<string> OrphanImages(IEnumerable<string> filesInFolder, IEnumerable<ClipboardEntry> entries)
    {
        var used = entries.Where(e => e.ImageFile is not null).Select(e => e.ImageFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return filesInFolder.Where(f => !used.Contains(f)).ToList();
    }
}
