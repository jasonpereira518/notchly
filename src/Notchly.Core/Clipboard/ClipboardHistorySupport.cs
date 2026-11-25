// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Clipboard/ClipboardHistorySupport.swift: the rules of the clipboard history that need no
// clipboard. What is worth keeping, how a re-copy lands, what a limit or retention drops, whether a text looks like a credential, and
// how a search reads. Windows differences: the change count is the clipboard sequence number, a source is a program name, and a batch
// pastes as text or files (the clipboard has no rich-text container for images).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly.Core.Clipboard;

public enum PasteMode { Text, Files }

public static partial class ClipboardHistorySupport
{
    // MARK: Limits

    public const int MaxCharacters = 1_000_000;
    public const long MaxStoredTextBytes = 64L * 1024 * 1024;
    public const int MaxCopiedFiles = 100;
    public const long MaxImageBytes = 16L * 1024 * 1024;
    public const long MaxRawImageBytes = 64L * 1024 * 1024;
    public const int RowPreviewLength = 2000;
    public const int TooltipLength = 200;
    public const int SearchIndexLength = 4096;
    public const int DefaultLimit = 50;
    public const int DefaultRetentionDays = 0;
    /// <summary>A prior clipboard that comes back this soon is touched, not recorded again (dictation tools do this).</summary>
    public const double RestoreWindowSeconds = 3;

    public static readonly int[] Limits = [20, 50, 100, 250, 500, 1000, 10000, 0];
    public static readonly int[] RetentionDays = [1, 7, 30, 90, 365, 0];

    /// <summary>The number of recent entries kept; 0 is unlimited, and a value that is not on the list is the default.</summary>
    public static int SanitizedLimit(int limit) => Limits.Contains(limit) ? limit : DefaultLimit;

    /// <summary>The age in days after which recent entries go; 0 is forever, and a value that is not on the list is forever.</summary>
    public static int SanitizedRetention(int days) => RetentionDays.Contains(days) ? days : DefaultRetentionDays;

    public static string LimitLabel(int limit) => limit == 0 ? "Unlimited" : limit.ToString(CultureInfo.InvariantCulture);

    public static string RetentionLabel(int days) => days switch { 0 => "Forever", 1 => "1 day", _ => $"{days} days" };

    // MARK: What is kept

    /// <summary>The text to store: trimmed, never empty, never past the character cap; null when it should not be kept.</summary>
    public static string? StorableText(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Length > MaxCharacters ? null : trimmed;
    }

    /// <summary>The files to store: in order, at most the cap, none when the list is empty.</summary>
    public static IReadOnlyList<string>? StorableFiles(IEnumerable<string>? paths)
    {
        var list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list is { Count: > 0 and <= MaxCopiedFiles } ? list : null;
    }

    /// <summary>Whether two entries hold the same content: trimmed text, the same image bytes, or the same files in the same order.</summary>
    public static bool MatchesContent(ClipboardEntry a, ClipboardEntry b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            ClipboardKind.Text => string.Equals(a.Text.Trim(), b.Text.Trim(), StringComparison.Ordinal),
            ClipboardKind.Image => a.ImageHash is not null && a.ImageHash == b.ImageHash,
            _ => a.FilePaths.SequenceEqual(b.FilePaths, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The entries after a copy: a repeat replaces the old entry (keeping its id, pin and image file) and comes first in its block.</summary>
    public static IReadOnlyList<ClipboardEntry> Recording(IReadOnlyList<ClipboardEntry> entries, ClipboardEntry incoming)
    {
        var result = entries.ToList();
        var at = result.FindIndex(e => MatchesContent(e, incoming));
        var entry = incoming;
        if (at >= 0)
        {
            var old = result[at];
            entry = incoming with { Id = old.Id, PinnedAt = old.PinnedAt, ImageFile = old.ImageFile ?? incoming.ImageFile };
            result.RemoveAt(at);
        }
        var pinned = result.Count(e => e.IsPinned);
        result.Insert(entry.IsPinned ? 0 : pinned, entry);
        return result;
    }

    /// <summary>Pinned entries first, then the rest, each block in its own order.</summary>
    public static IReadOnlyList<ClipboardEntry> Ordered(IEnumerable<ClipboardEntry> entries)
    {
        var list = entries.ToList();
        return [.. list.Where(e => e.IsPinned), .. list.Where(e => !e.IsPinned)];
    }

    /// <summary>
    /// What stays: every pinned entry, then recent entries within the limit, no older than the retention (except those in
    /// <paramref name="exempt"/>, such as one open in an editor), and while the text of all of it fits the byte budget.
    /// </summary>
    public static IReadOnlyList<ClipboardEntry> Retained(IReadOnlyList<ClipboardEntry> entries, int limit, int retentionDays, DateTimeOffset now,
                                                         long textByteLimit = MaxStoredTextBytes, IReadOnlySet<Guid>? exempt = null)
    {
        limit = SanitizedLimit(limit);
        retentionDays = SanitizedRetention(retentionDays);
        var pinned = entries.Where(e => e.IsPinned).ToList();
        var recent = entries.Where(e => !e.IsPinned);
        if (retentionDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(retentionDays);
            recent = recent.Where(e => e.CopiedAt >= cutoff || exempt?.Contains(e.Id) == true);
        }
        var recentList = recent.ToList();
        if (limit > 0 && recentList.Count > limit) recentList = recentList.Take(limit).ToList();
        var used = pinned.Sum(TextBytes);
        var kept = new List<ClipboardEntry>();
        foreach (var entry in recentList)
        {
            var size = TextBytes(entry);
            if (used + size > textByteLimit) break;
            used += size;
            kept.Add(entry);
        }
        return [.. pinned, .. kept];
    }

    private static long TextBytes(ClipboardEntry entry) => entry.Kind == ClipboardKind.Text ? Encoding.UTF8.GetByteCount(entry.Text) : 0;

    // MARK: Pinning and moving

    /// <summary>Pinning puts the entry first among the pinned; unpinning makes it the newest recent entry.</summary>
    public static IReadOnlyList<ClipboardEntry> TogglePin(IReadOnlyList<ClipboardEntry> entries, Guid id, DateTimeOffset now)
    {
        var at = entries.ToList().FindIndex(e => e.Id == id);
        if (at < 0) return entries;
        var list = entries.ToList();
        var entry = list[at];
        list.RemoveAt(at);
        if (entry.IsPinned)
        {
            entry = entry with { PinnedAt = null, CopiedAt = now };
            list.Insert(list.Count(e => e.IsPinned), entry);
        }
        else list.Insert(0, entry with { PinnedAt = now });
        return list;
    }

    /// <summary>Moves an entry one place up (-1) or down (+1) within its own block.</summary>
    public static IReadOnlyList<ClipboardEntry> Move(IReadOnlyList<ClipboardEntry> entries, Guid id, int direction)
    {
        var list = entries.ToList();
        var at = list.FindIndex(e => e.Id == id);
        var to = at + Math.Sign(direction);
        if (at < 0 || direction == 0 || to < 0 || to >= list.Count || list[to].IsPinned != list[at].IsPinned) return entries;
        (list[at], list[to]) = (list[to], list[at]);
        return list;
    }

    // MARK: Telling a new copy from the same one

    /// <summary>
    /// Whether a sequence number read from the clipboard is a new copy. A number below the one at the start means the counter restarted;
    /// otherwise it counts only when it is past the last one seen. Returns the number to remember, or null for nothing new.
    /// </summary>
    public static long? Accepted(long read, long since, long last)
    {
        if (read < since) return read;
        return read > last ? read : null;
    }

    // MARK: Which text

    private static readonly Regex SchemePrefix = SchemePrefixRegex();
    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.\-]*:)?/{0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePrefixRegex();

    public static bool IsWebUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace)) return false;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
    }

    private static string StripScheme(string text) => SchemePrefix.Replace(text.Trim(), "").TrimEnd('/');

    /// <summary>
    /// The text to record. A lone web link is kept as the link. When the clipboard also holds a separate link and the plain text is
    /// only that link without its scheme or slashes, the link wins. Anything else is the plain text.
    /// </summary>
    public static string PreferredText(string plain, string? webUrl)
    {
        var trimmed = plain.Trim();
        if (IsWebUrl(trimmed)) return trimmed;
        if (webUrl is not null && IsWebUrl(webUrl.Trim()) && trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace)
            && (trimmed.StartsWith("//", StringComparison.Ordinal) || string.Equals(StripScheme(trimmed), StripScheme(webUrl), StringComparison.OrdinalIgnoreCase)))
            return webUrl.Trim();
        return plain;
    }

    // MARK: Sensitive text

    [GeneratedRegex(@"[\w-]*(?:password|passwd|secret|token|api[_-]?key|private[_-]?key|authorization)(?:[_-]?(?:access[_-]?)?key(?:[_-]?base)?|[_-]?confirmation|[_-]?\d{1,3})?[""']?[ \t]*[:=][ \t]*\S", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?:^|\s)--[\w-]*?(?:password|passwd|secret|token|api[_-]?key|private[_-]?key|authorization)(?:[ \t]+|=)\S", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialOptionRegex();

    [GeneratedRegex(@"^\{?[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();

    /// <summary>
    /// Whether a text looks like a credential: an assignment such as <c>password=…</c>, <c>--token value</c> or <c>Authorization: …</c>, or (apart
    /// from web links and GUIDs) a 20 to 160 character token with no spaces that mixes a letter, a digit and a symbol.
    /// </summary>
    public static bool LooksSensitive(string text)
    {
        var trimmed = text.Trim();
        if (CredentialAssignmentRegex().IsMatch(trimmed) || CredentialOptionRegex().IsMatch(trimmed)) return true;
        if (IsWebUrl(trimmed) || GuidRegex().IsMatch(trimmed)) return false;
        if (trimmed.Length is < 20 or > 160 || trimmed.Any(char.IsWhiteSpace)) return false;
        return trimmed.Any(char.IsLetter) && trimmed.Any(char.IsDigit) && trimmed.Any(c => !char.IsLetterOrDigit(c));
    }

    // MARK: Search

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>What a search reads for an entry: its text, or its file names, or its source.</summary>
    public static string SearchText(ClipboardEntry entry)
    {
        var text = entry.Kind switch
        {
            ClipboardKind.Text => entry.Text,
            ClipboardKind.Files => string.Join(" ", entry.FilePaths.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p)),
            _ => "image " + entry.ImageWidth + "x" + entry.ImageHeight,
        };
        return text.Length > SearchIndexLength ? text[..SearchIndexLength] : text;
    }

    public static string[] Tokens(string query) => Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Entries holding every word of the query (ignoring case, accents and width), those with more words starting a word first,
    /// otherwise in their own order. An empty query keeps everything.
    /// </summary>
    public static IReadOnlyList<ClipboardEntry> Search(IReadOnlyList<ClipboardEntry> entries, string query)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0) return entries;
        var ranked = new List<(ClipboardEntry Entry, int Score, int Order)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var haystack = Fold(SearchText(entries[i]));
            if (!tokens.All(haystack.Contains)) continue;
            var score = tokens.Count(t => WordStartHit(haystack, t));
            ranked.Add((entries[i], score, i));
        }
        return ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Order).Select(r => r.Entry).ToList();
    }

    private static bool WordStartHit(string haystack, string token)
    {
        for (var at = haystack.IndexOf(token, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(token, at + 1, StringComparison.Ordinal))
            if (at == 0 || !char.IsLetterOrDigit(haystack[at - 1])) return true;
        return false;
    }

    // MARK: Showing and pasting

    /// <summary>How long ago an entry was copied, as a card says it: "now", "5 min", "3 h", "2 d", then the date.</summary>
    public static string Age(DateTimeOffset copied, DateTimeOffset now)
    {
        var span = now - copied;
        if (span < TimeSpan.FromMinutes(1)) return "now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h";
        if (span < TimeSpan.FromDays(7)) return $"{(int)span.TotalDays} d";
        return copied.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string RowPreview(string text) => Truncated(text, RowPreviewLength);
    public static string Tooltip(string text) => Truncated(text, TooltipLength);

    private static string Truncated(string text, int length) => text.Length > length ? text[..length] + "…" : text;

    /// <summary>Several entries paste as files when all of them are files, otherwise as text (a file's text is its path).</summary>
    public static PasteMode ModeFor(IReadOnlyList<ClipboardEntry> entries) =>
        entries.Count > 0 && entries.All(e => e.Kind == ClipboardKind.Files) ? PasteMode.Files : PasteMode.Text;

    public static IReadOnlyList<string> BatchFiles(IReadOnlyList<ClipboardEntry> entries) =>
        entries.Where(e => e.Kind == ClipboardKind.Files).SelectMany(e => e.FilePaths).ToList();

    /// <summary>The text of a batch: each text entry, and each file's path, one per line in the order chosen; images give nothing.</summary>
    public static string BatchText(IReadOnlyList<ClipboardEntry> entries) =>
        string.Join("\n", entries.SelectMany(e => e.Kind switch
        {
            ClipboardKind.Text => [e.Text],
            ClipboardKind.Files => e.FilePaths,
            _ => (IEnumerable<string>)[],
        }));

    /// <summary>Remote-desktop clients get a copy only: a paste would go to the other machine.</summary>
    public static bool IsCopyOnlyApp(string? exe) =>
        exe is not null && exe.Equals("mstsc.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The 1 to 9 keys pick by position; anything else is not a pick.</summary>
    public static int? PositionForDigit(int digit, int count) => digit is >= 1 and <= 9 && digit <= count ? digit - 1 : null;

    /// <summary>The program names on a list of ignored apps: trimmed, no repeats, no blanks.</summary>
    public static IReadOnlyList<string> NormalizedIgnoredApps(IEnumerable<string> apps) =>
        apps.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsIgnored(string? exe, IEnumerable<string> ignored) =>
        !string.IsNullOrEmpty(exe) && ignored.Any(i => string.Equals(i.Trim(), exe, StringComparison.OrdinalIgnoreCase));
}
