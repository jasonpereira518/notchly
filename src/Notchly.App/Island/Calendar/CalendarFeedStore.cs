// SPDX-License-Identifier: GPL-3.0-or-later
// The calendars added on this PC: each a private iCal link (Google, Outlook, Canvas, iCloud) or an .ics
// file. A link lets anyone read that calendar, so it is stored encrypted for this Windows user (DPAPI)
// in %APPDATA%\Notchly\calendars.json, never in settings.json, backups, the dotfile or the log.
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchly.App.Platform.Net;
using Notchly.Core;
using Notchly.Core.Calendar;
using Serilog;

namespace Notchly.App.Island.Calendar;

public static class CalendarFeedStore
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Notchly calendar feed");
    private static readonly object Gate = new();

    /// <summary>The calendars file; settable so a self-check never touches the real list.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppIdentity.DataDirectory, "calendars.json");

    /// <summary>Raised on the thread that changed the list.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<CalendarFeed> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            try
            {
                var feeds = new List<CalendarFeed>();
                foreach (var node in JsonNode.Parse(File.ReadAllText(FilePath))?["feeds"]?.AsArray() ?? [])
                {
                    if (node is null) continue;
                    var id = node["id"]?.GetValue<string>();
                    var protectedSource = node["source"]?.GetValue<string>();
                    if (id is null || protectedSource is null) continue;
                    string source;
                    try { source = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSource), Entropy, DataProtectionScope.CurrentUser)); }
                    catch (Exception e) when (e is CryptographicException or FormatException) { Log.Warning("A calendar could not be decrypted for this user"); continue; }
                    feeds.Add(new CalendarFeed(id, node["name"]?.GetValue<string>() ?? "Calendar", source,
                        CalendarColor.Parse(node["color"]?.GetValue<string>()) ?? CalendarColor.Fallback));
                }
                return feeds;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
            {
                Log.Warning(e, "Calendar list unreadable");
                return [];
            }
        }
    }

    private static void Save(IReadOnlyList<CalendarFeed> feeds)
    {
        lock (Gate)
        {
            var array = new JsonArray();
            foreach (var feed in feeds)
            {
                var secret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(feed.Source), Entropy, DataProtectionScope.CurrentUser));
                array.Add(new JsonObject { ["id"] = feed.Id, ["name"] = feed.Name, ["color"] = feed.Color.Hex, ["source"] = secret });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, new JsonObject { ["version"] = 1, ["feeds"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        Changed?.Invoke();
    }

    private static void Mutate(Func<List<CalendarFeed>, List<CalendarFeed>> change) => Save(change(Load().ToList()));

    public static void Rename(string id, string name) =>
        Mutate(list => list.Select(f => f.Id == id && name.Trim().Length > 0 ? f with { Name = name.Trim() } : f).ToList());

    public static void SetColor(string id, CalendarColor color) => Mutate(list => list.Select(f => f.Id == id ? f with { Color = color } : f).ToList());

    public static void Remove(string id) => Mutate(list => list.Where(f => f.Id != id).ToList());

    /// <summary>
    /// Reads a link or file once to check it is a calendar, then adds it with the feed's own name and the
    /// next colour. Returns a problem in plain words instead when it can't be used.
    /// </summary>
    public static async Task<(CalendarFeed? Feed, string? Problem)> AddAsync(string input, CancellationToken token = default)
    {
        var text = input.Trim().Trim('"');
        string source;
        if (File.Exists(text)) source = Path.GetFullPath(text);
        else if (IcsReader.NormalizeFeedUrl(text) is { } url) source = url;
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return (null, "That link isn't encrypted (http). Use the https or webcal link the calendar gives you.");
        else return (null, "Paste the calendar's iCal link (https or webcal), or choose an .ics file.");
        var existing = Load();
        if (existing.Any(f => string.Equals(f.Source, source, StringComparison.OrdinalIgnoreCase))) return (null, "This calendar is already added.");

        var probe = new CalendarFeed(Guid.NewGuid().ToString("N"), "Calendar", source, CalendarColor.Fallback);
        var read = await ReadAsync(probe, null, null, token);
        if (read.Problem is { } problem) return (null, problem);
        var calendar = read.Payload is { } bytes ? await Task.Run(() => IcsReader.Parse(Encoding.UTF8.GetString(bytes)), token) : null;
        if (calendar is null) return (null, "That link didn't return a calendar. Check that it's the iCal (.ics) address.");
        var name = IcsReader.CalendarName(calendar) ?? (probe.IsFile ? Path.GetFileNameWithoutExtension(source) : new Uri(source).Host);
        var feed = probe with { Name = name, Color = CalendarColor.Palette[existing.Count % CalendarColor.Palette.Count] };
        Save(existing.Append(feed).ToList());
        Log.Information("Added a calendar from {Where}", feed.IsFile ? "a file" : new Uri(source).Host);
        return (feed, null);
    }

    public sealed record ReadResult(byte[]? Payload, bool NotModified, string? ETag, DateTimeOffset? LastModified, string? Problem);

    public static async Task<ReadResult> ReadAsync(CalendarFeed feed, string? etag, DateTimeOffset? lastModified, CancellationToken token)
    {
        if (feed.IsFile)
        {
            try
            {
                var info = new FileInfo(feed.Source);
                if (!info.Exists) return new ReadResult(null, false, null, null, "The .ics file was moved or deleted");
                if (info.Length > MaximumBytes) return new ReadResult(null, false, null, null, "The calendar is too large");
                return new ReadResult(await File.ReadAllBytesAsync(feed.Source, token), false, null, null, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ReadResult(null, false, null, null, "The .ics file couldn't be read");
            }
        }
        var result = await BoundedDownloader.LoadFeedAsync(new Uri(feed.Source), MaximumBytes, etag, lastModified, token);
        return new ReadResult(result.Payload, result.NotModified, result.ETag, result.LastModified, result.Problem);
    }
}
