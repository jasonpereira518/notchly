// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/RecentCaptureStore.swift (the history.json model and its
// "only a readable index permits writes" rules) plus the pure policy of RecentCaptureService and ScreenshotSupport
// (the cap, the cache file naming, the missing-file prune). File access goes through IRecentCaptureStorage.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notchly.Core.Capture;

public enum RecentCaptureKind { Screenshot, Recording }

/// <summary>
/// One history row. A screenshot lives in the cache folder as <see cref="ScreenshotName"/>; a recording stays where it is and
/// only its path and a small thumbnail are kept. The anchor is where the capture was taken (top-left origin, screen pixels).
/// </summary>
public sealed record RecentCaptureEntry(
    Guid Id,
    RecentCaptureKind Kind,
    DateTimeOffset CreatedAt,
    string? ScreenshotName = null,
    string? RecordingPath = null,
    string? ThumbnailName = null,
    double? Scale = null,
    double? AnchorX = null,
    double? AnchorY = null,
    double? AnchorWidth = null,
    double? AnchorHeight = null);

public enum IndexReadStatus { Ok, Missing, Unreadable }

public readonly record struct IndexRead(IndexReadStatus Status, string? Text = null);

/// <summary>The folder the history lives in. Implementations must refuse links and write atomically.</summary>
public interface IRecentCaptureStorage
{
    /// <summary>history.json: its text, or why there is none.</summary>
    IndexRead ReadIndex();

    /// <summary>Replaces history.json atomically; false when the write failed.</summary>
    bool WriteIndex(string json);

    /// <summary>Every file name in the folder, or null when the folder cannot be read.</summary>
    IReadOnlyList<string>? ListFiles();

    /// <summary>Deletes one regular file by name (never a link, never outside the folder). Best effort.</summary>
    void DeleteFile(string name);
}

/// <summary>The pure rules around the history: names, cap, prune.</summary>
public static class RecentCapturePolicy
{
    public const int Limit = 12;
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const string IndexFileName = "history.json";

    public static string ScreenshotFileName(Guid id) => $"{id:D}.png";

    public static string ThumbnailFileName(Guid id) => $"{id:D}-thumbnail.png";

    /// <summary>Only the app's own "uuid.png" and "uuid-thumbnail.png" names are cache files that cleanup may touch.</summary>
    public static bool IsCacheFileName(string name)
    {
        if (!IsSafeName(name) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^4];
        if (Guid.TryParseExact(stem, "D", out _)) return true;
        const string suffix = "-thumbnail";
        return stem.EndsWith(suffix, StringComparison.Ordinal)
               && Guid.TryParseExact(stem[..^suffix.Length], "D", out _);
    }

    /// <summary>A bare file name: no folders, no drive, no dot segments.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name != "." && name != ".."
        && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOf('\0') < 0;

    /// <summary>
    /// The newest bounded set: the first twelve ids, and past the first screenshot, no more screenshots once the total
    /// size would pass 256 MB. Ids with no known size (recordings, unreadable files) always stay.
    /// </summary>
    public static IReadOnlyList<Guid> CappedIds(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, long>? screenshotBytes = null)
    {
        var kept = new List<Guid>();
        long bytes = 0;
        var keptScreenshot = false;
        foreach (var id in ids.Take(Limit))
        {
            if (screenshotBytes is null || !screenshotBytes.TryGetValue(id, out var size))
            {
                kept.Add(id);
                continue;
            }
            var safe = Math.Max(0, size);
            if (keptScreenshot && bytes + safe > MaximumBytes) continue;
            kept.Add(id);
            keptScreenshot = true;
            bytes += safe;
        }
        return kept;
    }
}

/// <summary>history.json: encoding, and lossy decoding that keeps every row it can read.</summary>
public static class RecentCaptureIndex
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    /// <summary>
    /// The rows of an index, newest first. A text that is not a JSON array is unreadable (null); inside an array, a row that
    /// cannot be read is dropped and counted rather than losing the whole history.
    /// </summary>
    public static IReadOnlyList<RecentCaptureEntry>? Decode(string? json, out int dropped)
    {
        dropped = 0;
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not JsonArray rows) return null;
        var entries = new List<RecentCaptureEntry>();
        foreach (var row in rows)
        {
            if (TryReadEntry(row as JsonObject) is { } entry) entries.Add(entry);
            else dropped++;
        }
        return entries.OrderByDescending(entry => entry.CreatedAt).ToList();
    }

    public static string Encode(IEnumerable<RecentCaptureEntry> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            var node = new JsonObject
            {
                ["id"] = entry.Id.ToString("D"),
                ["kind"] = entry.Kind == RecentCaptureKind.Recording ? "recording" : "screenshot",
                ["createdAt"] = entry.CreatedAt.ToUnixTimeMilliseconds() / 1000.0,
            };
            Put(node, "screenshotName", entry.ScreenshotName);
            Put(node, "recordingPath", entry.RecordingPath);
            Put(node, "thumbnailName", entry.ThumbnailName);
            Put(node, "scale", entry.Scale);
            Put(node, "anchorX", entry.AnchorX);
            Put(node, "anchorY", entry.AnchorY);
            Put(node, "anchorWidth", entry.AnchorWidth);
            Put(node, "anchorHeight", entry.AnchorHeight);
            array.Add(node);
        }
        return array.ToJsonString(WriteOptions);
    }

    private static void Put(JsonObject node, string key, string? value)
    {
        if (value is not null) node[key] = value;
    }

    private static void Put(JsonObject node, string key, double? value)
    {
        if (value is { } number && double.IsFinite(number)) node[key] = number;
    }

    private static RecentCaptureEntry? TryReadEntry(JsonObject? row)
    {
        if (row is null) return null;
        try
        {
            if (!Guid.TryParseExact(Text(row, "id"), "D", out var id)) return null;
            var kind = Text(row, "kind") switch
            {
                "screenshot" => RecentCaptureKind.Screenshot,
                "recording" => RecentCaptureKind.Recording,
                _ => (RecentCaptureKind?)null,
            };
            if (kind is not { } known || Number(row, "createdAt") is not { } seconds) return null;
            return new RecentCaptureEntry(
                id, known, DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)),
                Text(row, "screenshotName"), Text(row, "recordingPath"), Text(row, "thumbnailName"),
                Number(row, "scale"), Number(row, "anchorX"), Number(row, "anchorY"),
                Number(row, "anchorWidth"), Number(row, "anchorHeight"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>
/// The history in memory with upstream's safety rules: only a readable index permits writes, and only a committed index permits
/// cleanup of the files it no longer references. Not thread safe; the owner serialises access like upstream's queue.
/// </summary>
public sealed class RecentCaptureStore(IRecentCaptureStorage? storage)
{
    private readonly IRecentCaptureStorage? _storage = storage;
    private bool _loaded;
    private string? _persistedJson;

    /// <summary>Newest first.</summary>
    public List<RecentCaptureEntry> Entries { get; set; } = [];

    public bool IsLoaded => _loaded;

    /// <summary>
    /// Reads the index once. A corrupt or unreadable index, or a missing one next to existing captures, refuses to load: an
    /// empty history must never authorise erasing files that may still be wanted.
    /// </summary>
    public bool LoadIfNeeded()
    {
        if (_loaded) return true;
        if (_storage is null) return false;
        var read = _storage.ReadIndex();
        IReadOnlyList<RecentCaptureEntry> decoded;
        switch (read.Status)
        {
            case IndexReadStatus.Ok:
                if (RecentCaptureIndex.Decode(read.Text, out _) is not { } entries) return false;
                decoded = entries;
                _persistedJson = RecentCaptureIndex.Encode(entries);
                break;
            case IndexReadStatus.Missing:
                if (_storage.ListFiles() is not { } files || files.Any(RecentCapturePolicy.IsCacheFileName)) return false;
                decoded = [];
                break;
            default:
                return false;
        }
        Entries = decoded.OrderByDescending(entry => entry.CreatedAt).ToList();
        _loaded = true;
        return true;
    }

    /// <summary>
    /// Writes the index when it changed, then deletes the cache files no entry references. A failed write keeps every file.
    /// </summary>
    public bool Persist()
    {
        if (!_loaded || _storage is null) return false;
        var json = RecentCaptureIndex.Encode(Entries);
        if (json != _persistedJson)
        {
            if (!_storage.WriteIndex(json)) return false;
            _persistedJson = json;
        }
        RemoveOrphanedCacheFiles();
        return true;
    }

    private void RemoveOrphanedCacheFiles()
    {
        var referenced = Entries.SelectMany(entry => new[] { entry.ScreenshotName, entry.ThumbnailName })
            .Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        if (_storage?.ListFiles() is not { } files) return;
        foreach (var file in files)
            if (RecentCapturePolicy.IsCacheFileName(file) && !referenced.Contains(file)) _storage.DeleteFile(file);
    }

    // MARK: - Editing the history

    /// <summary>Whether a row's file is still there: the cache file for a screenshot, the original file for a recording.</summary>
    public bool EntryExists(RecentCaptureEntry entry, Func<string, bool> screenshotExists, Func<string, bool>? recordingExists = null)
    {
        switch (entry.Kind)
        {
            case RecentCaptureKind.Screenshot:
                return RecentCapturePolicy.IsSafeName(entry.ScreenshotName) && screenshotExists(entry.ScreenshotName!);
            default:
                return !string.IsNullOrEmpty(entry.RecordingPath) && (recordingExists ?? screenshotExists)(entry.RecordingPath);
        }
    }

    /// <summary>Drops rows whose file vanished, then applies the cap (sizes of screenshots, when known, count toward the byte limit).</summary>
    public void PruneMissing(Func<string, bool> screenshotExists, Func<string, bool>? recordingExists = null,
        Func<RecentCaptureEntry, long?>? screenshotBytes = null)
    {
        var kept = Entries.Where(entry => EntryExists(entry, screenshotExists, recordingExists)).ToList();
        Entries = Capped(kept, screenshotBytes);
    }

    /// <summary>
    /// Adds a row at the front (newest first). A recording already in the history is replaced, the way opening the same video twice
    /// keeps one row; the list is then capped.
    /// </summary>
    public void Prepend(RecentCaptureEntry entry, Func<RecentCaptureEntry, long?>? screenshotBytes = null)
    {
        if (entry.RecordingPath is { } path)
            Entries.RemoveAll(existing => string.Equals(existing.RecordingPath, path, StringComparison.OrdinalIgnoreCase));
        Entries.Add(entry);
        Entries = Capped(Entries.OrderByDescending(e => e.CreatedAt).ToList(), screenshotBytes);
    }

    /// <summary>Removes one row; false when it was not there.</summary>
    public bool Remove(Guid id) => Entries.RemoveAll(entry => entry.Id == id) > 0;

    /// <summary>Clears the whole history.</summary>
    public void Clear() => Entries.Clear();

    private static List<RecentCaptureEntry> Capped(List<RecentCaptureEntry> entries, Func<RecentCaptureEntry, long?>? screenshotBytes)
    {
        Dictionary<Guid, long> sizes = [];
        if (screenshotBytes is not null)
            foreach (var entry in entries.Where(e => e.Kind == RecentCaptureKind.Screenshot))
                if (screenshotBytes(entry) is { } size) sizes[entry.Id] = size;
        var keep = RecentCapturePolicy.CappedIds(entries.Select(e => e.Id), sizes).ToHashSet();
        return entries.Where(entry => keep.Contains(entry.Id)).ToList();
    }
}
