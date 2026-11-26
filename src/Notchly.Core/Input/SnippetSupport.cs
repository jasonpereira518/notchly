// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Snippets/TextSnippetSupport.swift: the snippet model and its storage, trigger rules,
// the typed buffer, matching (longest trigger wins), how text is sent, and the {{date}}, {{time}}, {{datetime}}, time-zone and
// {{clipboard}} variables. Windows differences: date patterns are converted from Swift's to .NET's, and time zones are
// looked up with TimeZoneInfo, which understands the same IANA names.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Notchly.Core.Input;

public enum SnippetExpansion { Immediate, AfterDelimiter }

public sealed record TextSnippet
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "";
    [JsonPropertyName("replacement")] public string Replacement { get; init; } = "";
    [JsonPropertyName("expansion")] public string Expansion { get; init; } = "afterDelimiter";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("ignoresCase")] public bool IgnoresCase { get; init; }
    [JsonPropertyName("folder")] public string Folder { get; init; } = "";
    [JsonPropertyName("showsInLibrary")] public bool ShowsInLibrary { get; init; } = true;

    [JsonIgnore] public SnippetExpansion Mode => Expansion == "immediate" ? SnippetExpansion.Immediate : SnippetExpansion.AfterDelimiter;
}

public enum TriggerCheck { Valid, TooShort, TooLong, HasWhitespace, Duplicate }

public static class SnippetSupport
{
    public const int BufferLimit = 64;
    public const int MaxTrigger = 40;
    public const int MinTrigger = 2;
    public const int ChunkUnits = 20;

    // MARK: Storage

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static IReadOnlyList<TextSnippet> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<TextSnippet>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Encode(IEnumerable<TextSnippet> snippets) => JsonSerializer.Serialize(snippets.ToList(), Json);

    // MARK: Triggers

    /// <summary>A trigger has no whitespace and fits in 40 characters: spaces and line breaks are removed.</summary>
    public static string SanitizedTrigger(string trigger)
    {
        var builder = new StringBuilder();
        foreach (var c in trigger) if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.Length > MaxTrigger ? builder.ToString(0, MaxTrigger) : builder.ToString();
    }

    public static string SanitizedFolder(string folder) => folder.Trim();

    /// <summary>A trigger must be 2 to 40 characters with no whitespace, and not repeat another snippet's (ignoring case when either ignores it).</summary>
    public static TriggerCheck Check(string trigger, bool ignoresCase, IEnumerable<TextSnippet> others)
    {
        if (trigger.Any(char.IsWhiteSpace)) return TriggerCheck.HasWhitespace;
        if (trigger.Length < MinTrigger) return TriggerCheck.TooShort;
        if (trigger.Length > MaxTrigger) return TriggerCheck.TooLong;
        foreach (var other in others)
        {
            var comparison = ignoresCase || other.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(other.Trigger, trigger, comparison)) return TriggerCheck.Duplicate;
        }
        return TriggerCheck.Valid;
    }

    // MARK: The buffer

    /// <summary>What was typed since the last reset: the last 64 characters.</summary>
    public sealed class TypedBuffer
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public void Append(char c)
        {
            _text.Append(c);
            if (_text.Length > BufferLimit) _text.Remove(0, _text.Length - BufferLimit);
        }

        public void Backspace() { if (_text.Length > 0) _text.Length--; }
        public void Clear() => _text.Clear();
    }

    private static readonly HashSet<int> ResettingKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // page up and down, end, home, arrows
        0x1B, 0x2D, 0x2E,                               // escape, insert, delete
    ];

    /// <summary>A click, a Ctrl, Alt or Win chord, or a navigation key means the text being typed is no longer a trigger.</summary>
    public static bool ResetsBuffer(int virtualKey, bool ctrl, bool alt, bool win) => ctrl || alt || win || ResettingKeys.Contains(virtualKey);

    /// <summary>The delimiter a key types: Space, Tab or Return (as a carriage return); anything else is not one.</summary>
    public static char? Delimiter(int virtualKey) => virtualKey switch { 0x20 => ' ', 0x09 => '\t', 0x0D => '\r', _ => null };

    // MARK: Matching

    /// <summary>
    /// The snippet the text ends with: the longest enabled trigger of the given kind wins (so ";email2" beats ";email"), and
    /// a half-typed trigger or the other kind of snippet never matches.
    /// </summary>
    public static TextSnippet? Find(string typed, IEnumerable<TextSnippet> snippets, SnippetExpansion mode) =>
        snippets.Where(s => s.Enabled && s.Mode == mode && s.Trigger.Length > 0
                            && typed.EndsWith(s.Trigger, s.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(s => s.Trigger.Length).FirstOrDefault();

    /// <summary>
    /// How many backspaces erase what was typed of the trigger: the key that completed it is swallowed (the last character of an
    /// immediate trigger, or the delimiter), so it never reached the field.
    /// </summary>
    public static int DeleteCount(TextSnippet snippet) =>
        snippet.Mode == SnippetExpansion.Immediate ? Math.Max(0, snippet.Trigger.Length - 1) : snippet.Trigger.Length;

    // MARK: Sending

    /// <summary>The text in pieces of at most 20 UTF-16 units, never splitting a surrogate pair.</summary>
    public static IReadOnlyList<string> Chunks(string text, int units = ChunkUnits)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(units, text.Length - start);
            if (length < text.Length - start && char.IsHighSurrogate(text[start + length - 1])) length--;
            if (length <= 0) length = Math.Min(units, text.Length - start);
            chunks.Add(text.Substring(start, length));
            start += length;
        }
        return chunks;
    }

    /// <summary>A replacement with a line break is pasted: typing it would press Return in the field.</summary>
    public static bool ShouldPaste(string text) => text.Contains('\n') || text.Contains('\r');

    /// <summary>The pasted text with the delimiter that completed the trigger put back, a carriage return as a newline.</summary>
    public static string PastePayload(string text, char? delimiter) => delimiter is { } d ? text + (d == '\r' ? '\n' : d) : text;

    // MARK: Variables

    public static bool NeedsClipboard(string text) => text.Contains("{{clipboard}}", StringComparison.Ordinal);

    private static readonly Regex Tag = new(@"^(date|time|datetime)(?:-tz\(([^)]*)\))?(?::(.*))?$", RegexOptions.Singleline | RegexOptions.Compiled);
    private const string ClipboardMark = "CLIPBOARD";

    /// <summary>
    /// The replacement with its variables filled in: the date and time (in a style, a pattern or a named zone) and the clipboard.
    /// A tag that cannot be read is left exactly as typed; the clipboard goes in last, so what it holds is never read as a tag.
    /// </summary>
    public static string Expand(string replacement, DateTimeOffset now, string? clipboard = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var result = new StringBuilder();
        var i = 0;
        while (i < replacement.Length)
        {
            var open = replacement.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { result.Append(replacement, i, replacement.Length - i); break; }
            result.Append(replacement, i, open - i);
            var close = replacement.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { result.Append(replacement, open, replacement.Length - open); break; }
            var inner = replacement.Substring(open + 2, close - open - 2);
            var value = inner == "clipboard" ? ClipboardMark : Evaluate(inner, now, culture);
            result.Append(value ?? replacement.Substring(open, close + 2 - open));
            i = close + 2;
        }
        return result.ToString().Replace(ClipboardMark, clipboard ?? "", StringComparison.Ordinal);
    }

    private static string? Evaluate(string inner, DateTimeOffset now, CultureInfo culture)
    {
        var match = Tag.Match(inner);
        if (!match.Success) return null;
        var kind = match.Groups[1].Value;
        var zone = match.Groups[2].Success ? match.Groups[2].Value : null;
        var pattern = match.Groups[3].Success ? match.Groups[3].Value : null;
        // A colon with nothing after it is a mistake, not "no pattern".
        if (pattern is not null && pattern.Trim().Length == 0) return null;
        var moment = now;
        if (zone is not null)
        {
            try { moment = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
        }
        if (pattern is null)
            return kind switch
            {
                "date" => moment.ToString("MMM d, yyyy", culture),
                "time" => moment.ToString("t", culture),
                _ => moment.ToString("MMM d, yyyy", culture) + ", " + moment.ToString("t", culture),
            };
        try { return moment.ToString(ConvertPattern(pattern), culture); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A Swift date pattern (yyyy-MM-dd HH:mm, EEEE, a, SSS, 'text') as a .NET one: weekdays are d, AM and PM is tt, fractions are f.
    /// </summary>
    public static string ConvertPattern(string swift)
    {
        var result = new StringBuilder();
        for (var i = 0; i < swift.Length;)
        {
            var c = swift[i];
            if (c == '\'')
            {
                var end = swift.IndexOf('\'', i + 1);
                if (end < 0) { result.Append('\\').Append(c); i++; continue; }
                result.Append('\'').Append(swift, i + 1, end - i - 1).Append('\'');
                i = end + 1;
                continue;
            }
            if (!char.IsAsciiLetter(c)) { result.Append(c is ':' or '/' ? "\\" + c : c.ToString()); i++; continue; }
            var run = 1;
            while (i + run < swift.Length && swift[i + run] == c) run++;
            result.Append(c switch
            {
                'y' or 'M' or 'd' or 'H' or 'h' or 'm' or 's' => new string(c, run),
                'L' => new string('M', run),
                'E' or 'c' or 'e' => run >= 4 ? "dddd" : "ddd",
                'a' => "tt",
                'S' => new string('f', run),
                'Z' or 'z' or 'X' or 'x' => "zzz",
                'G' => "g",
                _ => string.Concat(Enumerable.Repeat("\\" + c, run)),
            });
            i += run;
        }
        var text = result.ToString();
        return text.Length == 1 ? "%" + text : text;
    }

    // MARK: The library

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>The query appears in the name, trigger, text or folder, ignoring case and accents.</summary>
    public static bool MatchesLibrary(TextSnippet snippet, string query)
    {
        var tokens = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;
        var haystack = Fold($"{snippet.Name} {snippet.Trigger} {snippet.Replacement} {snippet.Folder}");
        return tokens.All(haystack.Contains);
    }

    /// <summary>The library's groups: folders in alphabetical order, snippets with no folder last.</summary>
    public static IReadOnlyList<(string Folder, IReadOnlyList<TextSnippet> Snippets)> Grouped(IEnumerable<TextSnippet> snippets) =>
        snippets.Where(s => s.ShowsInLibrary).GroupBy(s => SanitizedFolder(s.Folder))
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<TextSnippet>)g.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList())).ToList();
}
