// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ScratchpadSupport.swift (the pad/document model, retention, sizes, shortcuts,
// naming and the Markdown preview blocks). The Markdown edit engine is in ScratchpadMark.cs, the file store in ScratchpadStore.cs.
// Platform-free. Upstream reads the preview with Foundation's Markdown parser; there is none here, so ScratchpadMarkdownParser below is a
// small CommonMark-style block parser (quotes, lists, tasks, headings, fences, indented code) with a simple inline pass.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Notchly.Core.Tools;

/// <summary>
/// How long each scratchpad keeps text that nobody edits. The check runs only when the panel opens, against the stored edit dates,
/// so the feature needs no timer at all.
/// </summary>
public enum ScratchpadRetention { Never, Day, Week, Month }

public static class ScratchpadRetentions
{
    /// <summary>The stored raw value: "never", "day", "week" or "month".</summary>
    public static string RawValue(ScratchpadRetention retention) => retention switch
    {
        ScratchpadRetention.Day => "day",
        ScratchpadRetention.Week => "week",
        ScratchpadRetention.Month => "month",
        _ => "never",
    };

    /// <summary>How long the text may sit unedited before it clears; null keeps it forever.</summary>
    public static TimeSpan? MaxIdleInterval(ScratchpadRetention retention) => retention switch
    {
        ScratchpadRetention.Day => TimeSpan.FromSeconds(86_400),
        ScratchpadRetention.Week => TimeSpan.FromSeconds(7 * 86_400),
        ScratchpadRetention.Month => TimeSpan.FromSeconds(30 * 86_400),
        _ => null,
    };

    /// <summary>Corrupt or unknown stored values fall back to keeping the text.</summary>
    public static ScratchpadRetention Sanitized(string? rawValue) => rawValue switch
    {
        "day" => ScratchpadRetention.Day,
        "week" => ScratchpadRetention.Week,
        "month" => ScratchpadRetention.Month,
        _ => ScratchpadRetention.Never,
    };
}

/// <summary>The kinds of block the preview draws.</summary>
public enum ScratchpadBlockType { Paragraph, Heading, UnorderedListItem, TaskListItem, OrderedListItem, Quote, Code, ThematicBreak }

/// <summary>
/// What a preview block is. <c>Level</c> is a heading's level, <c>Depth</c> the list or quote depth, <c>Ordinal</c> an ordered item's
/// number, and for a task <c>Checked</c> and <c>Box</c>, the UTF-16 offset of its "[" in the text, so a click edits the very box it
/// was drawn from.
/// </summary>
public readonly record struct ScratchpadBlockKind(
    ScratchpadBlockType Type, int Level = 0, int Depth = 0, int Ordinal = 0, bool Checked = false, int Box = 0)
{
    public static ScratchpadBlockKind Paragraph => new(ScratchpadBlockType.Paragraph);
    public static ScratchpadBlockKind Code => new(ScratchpadBlockType.Code);
    public static ScratchpadBlockKind ThematicBreak => new(ScratchpadBlockType.ThematicBreak);
    public static ScratchpadBlockKind Heading(int level) => new(ScratchpadBlockType.Heading, Level: level);
    public static ScratchpadBlockKind UnorderedListItem(int depth) => new(ScratchpadBlockType.UnorderedListItem, Depth: depth);
    public static ScratchpadBlockKind TaskListItem(int depth, bool isChecked, int box) =>
        new(ScratchpadBlockType.TaskListItem, Depth: depth, Checked: isChecked, Box: box);
    public static ScratchpadBlockKind OrderedListItem(int ordinal, int depth) =>
        new(ScratchpadBlockType.OrderedListItem, Depth: depth, Ordinal: ordinal);
    public static ScratchpadBlockKind Quote(int depth) => new(ScratchpadBlockType.Quote, Depth: depth);
}

/// <summary>A run of inline text with its formatting.</summary>
public sealed record ScratchpadInlineRun(
    string Text, bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Code = false, string? Link = null);

/// <summary>One block of the preview.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="ContainerId">The outermost list or quote the block sits in; blocks of one list share it.</param>
/// <param name="Runs">The block's text with its inline formatting.</param>
/// <param name="QuoteDepth">The quotes around a list item, which its reading keeps ahead of its marker; a quote's own paragraphs carry their depth in <c>Kind</c>.</param>
public sealed record ScratchpadMarkdownBlock(
    ScratchpadBlockKind Kind, int? ContainerId, IReadOnlyList<ScratchpadInlineRun> Runs, int QuoteDepth = 0)
{
    /// <summary>The block's text without formatting.</summary>
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

public sealed record ScratchpadPad(Guid Id, string Name, string Text, DateTimeOffset? ModifiedAt);

/// <summary>
/// The whole scratchpad state travels as one small document. Stable ids keep selection independent from names, while array order
/// is the tab order. Immutable: every change returns a new document, or null when the rule says no.
/// </summary>
public sealed class ScratchpadDocument : IEquatable<ScratchpadDocument>
{
    /// <summary>Keeps the tab strip and Settings backup deliberately small without imposing a limit on the text inside any pad.</summary>
    public const int MaximumPadCount = 12;
    public const int MaximumNameLength = 40;

    public IReadOnlyList<ScratchpadPad> Pads { get; }
    public Guid SelectedId { get; }

    public ScratchpadDocument(IEnumerable<ScratchpadPad> pads, Guid selectedId)
    {
        Pads = pads.ToArray();
        SelectedId = selectedId;
    }

    public ScratchpadPad? SelectedPad => Pads.FirstOrDefault(p => p.Id == SelectedId);

    public static ScratchpadDocument Initial(string defaultName, Guid? id = null, string text = "", DateTimeOffset? modifiedAt = null)
    {
        string name = ScratchpadSupport.NextPadName(defaultName, []);
        var pad = new ScratchpadPad(id ?? Guid.NewGuid(), name, text, text.Length == 0 ? null : modifiedAt);
        return new ScratchpadDocument([pad], pad.Id);
    }

    // ---- JSON: {"pads":[{"id","name","text","modifiedAt"}],"selectedID"} as upstream's Codable writes it --------------------

    /// <summary>Swift's default Date coding is seconds since this instant.</summary>
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Decodes stored bytes and sanitises the result; null when the data is missing or not a valid document.</summary>
    public static ScratchpadDocument? Decoded(byte[]? data, string defaultName)
    {
        if (data == null) return null;
        try { return Decode(data).Sanitized(defaultName); }
        catch (JsonException) { return null; }
    }

    /// <summary>Decodes stored bytes without sanitising; throws <see cref="JsonException"/> for anything that is not a document.</summary>
    public static ScratchpadDocument Decode(byte[] data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The scratchpad document is not an object.");
        if (!root.TryGetProperty("pads", out var padsElement) || padsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The scratchpad document has no pads.");
        var pads = new List<ScratchpadPad>();
        foreach (var element in padsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) throw new JsonException("A pad is not an object.");
            pads.Add(new ScratchpadPad(
                RequiredGuid(element, "id"),
                RequiredString(element, "name"),
                RequiredString(element, "text"),
                OptionalDate(element, "modifiedAt")));
        }
        return new ScratchpadDocument(pads, RequiredGuid(root, "selectedID"));
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"Missing string \"{name}\".");

    private static Guid RequiredGuid(JsonElement element, string name) =>
        Guid.TryParse(RequiredString(element, name), out var guid) ? guid : throw new JsonException($"Invalid id \"{name}\".");

    /// <summary>A number is upstream's seconds since 2001-01-01; an ISO-8601 string is accepted too. Missing or null is none.</summary>
    private static DateTimeOffset? OptionalDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds) && double.IsFinite(seconds))
        {
            double ticks = Math.Round(seconds * TimeSpan.TicksPerSecond);
            if (Math.Abs(ticks) < 3.0e18)
            {
                long total = ReferenceDate.UtcTicks + (long)ticks;
                if (total >= DateTime.MinValue.Ticks && total <= DateTime.MaxValue.Ticks)
                    return RoundToMicroseconds(new DateTimeOffset(total, TimeSpan.Zero));
            }
            throw new JsonException("The date is out of range.");
        }
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        throw new JsonException($"Invalid date \"{name}\".");
    }

    /// <summary>
    /// A stored date is a double of seconds, which cannot hold a .NET tick exactly this far from 2001; dates are kept to the microsecond
    /// so a document reads back equal to the one that was written.
    /// </summary>
    internal static DateTimeOffset? RoundToMicroseconds(DateTimeOffset? date) =>
        date is { } d ? RoundToMicroseconds(d) : null;

    private static DateTimeOffset RoundToMicroseconds(DateTimeOffset date) =>
        new((date.UtcTicks + 5) / 10 * 10, TimeSpan.Zero);

    /// <summary>The document as JSON bytes (UTF-8).</summary>
    public byte[] Encoded()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("pads");
            foreach (var pad in Pads)
            {
                writer.WriteStartObject();
                writer.WriteString("id", Upper(pad.Id));
                writer.WriteString("name", pad.Name);
                writer.WriteString("text", pad.Text);
                if (pad.ModifiedAt is { } modified)
                    writer.WriteNumber("modifiedAt", (modified.UtcTicks - ReferenceDate.UtcTicks) / (double)TimeSpan.TicksPerSecond);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("selectedID", Upper(SelectedId));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    // ---- Rules ---------------------------------------------------------------------------------------------------------

    /// <summary>Clean names, no repeated ids, at most 12 pads, text-less pads undated, a valid selection; never empty.</summary>
    public ScratchpadDocument Sanitized(string defaultName)
    {
        var seen = new HashSet<Guid>();
        var cleanPads = new List<ScratchpadPad>();
        foreach (var pad in Pads.Take(MaximumPadCount))
        {
            if (!seen.Add(pad.Id)) continue;
            string fallback = ScratchpadSupport.NextPadName(defaultName, cleanPads.Select(p => p.Name));
            string name = ScratchpadSupport.SanitizedPadName(pad.Name);
            cleanPads.Add(new ScratchpadPad(pad.Id, name.Length == 0 ? fallback : name, pad.Text, pad.Text.Length == 0 ? null : RoundToMicroseconds(pad.ModifiedAt)));
        }
        if (cleanPads.Count == 0) return Initial(defaultName);
        Guid selection = cleanPads.Any(p => p.Id == SelectedId) ? SelectedId : cleanPads[0].Id;
        return new ScratchpadDocument(cleanPads, selection);
    }

    /// <summary>A new empty pad appended and selected; null at the twelve-pad limit.</summary>
    public ScratchpadDocument? AddingPad(string defaultName, Guid? id = null)
    {
        if (Pads.Count >= MaximumPadCount) return null;
        Guid padId = id ?? Guid.NewGuid();
        string name = ScratchpadSupport.NextPadName(defaultName, Pads.Select(p => p.Name));
        return new ScratchpadDocument(Pads.Append(new ScratchpadPad(padId, name, "", null)), padId);
    }

    /// <summary>The document with this pad selected; null when there is no such pad.</summary>
    public ScratchpadDocument? Selecting(Guid id) =>
        Pads.Any(p => p.Id == id) ? new ScratchpadDocument(Pads, id) : null;

    /// <summary>The document with the pad renamed (whitespace folded, 40 characters at most); null for an empty name or unknown pad.</summary>
    public ScratchpadDocument? Renaming(Guid id, string proposedName)
    {
        string name = ScratchpadSupport.SanitizedPadName(proposedName);
        if (name.Length == 0 || !Pads.Any(p => p.Id == id)) return null;
        return new ScratchpadDocument(Pads.Select(p => p.Id == id ? p with { Name = name } : p), SelectedId);
    }

    /// <summary>The document without the pad, selecting its nearest neighbour when it was selected; null for the last pad or an unknown one.</summary>
    public ScratchpadDocument? Removing(Guid id)
    {
        if (Pads.Count <= 1) return null;
        int index = -1;
        for (int i = 0; i < Pads.Count; i++)
        {
            if (Pads[i].Id == id) { index = i; break; }
        }
        if (index < 0) return null;
        var remaining = Pads.Where((_, i) => i != index).ToArray();
        Guid selected = SelectedId == id ? remaining[Math.Min(index, remaining.Length - 1)].Id : SelectedId;
        return new ScratchpadDocument(remaining, selected);
    }

    /// <summary>The document with the selected pad's text replaced (an empty text clears its date); this document when nothing changes.</summary>
    public ScratchpadDocument UpdatingSelectedText(string text, DateTimeOffset modifiedAt)
    {
        int index = -1;
        for (int i = 0; i < Pads.Count; i++)
        {
            if (Pads[i].Id == SelectedId) { index = i; break; }
        }
        if (index < 0 || Pads[index].Text == text) return this;
        return new ScratchpadDocument(
            Pads.Select((p, i) => i == index ? p with { Text = text, ModifiedAt = text.Length == 0 ? null : modifiedAt } : p),
            SelectedId);
    }

    /// <summary>The document with every pad whose own text expired cleared.</summary>
    public ScratchpadDocument ApplyingRetention(ScratchpadRetention retention, DateTimeOffset now) =>
        new(Pads.Select(p => ScratchpadSupport.ShouldClear(p.ModifiedAt, now, retention) ? p with { Text = "", ModifiedAt = null } : p),
            SelectedId);

    public bool Equals(ScratchpadDocument? other) =>
        other != null && SelectedId == other.SelectedId && Pads.SequenceEqual(other.Pads);

    public override bool Equals(object? obj) => Equals(obj as ScratchpadDocument);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SelectedId);
        foreach (var pad in Pads) hash.Add(pad);
        return hash.ToHashCode();
    }
}

/// <summary>What a pad keyboard shortcut does.</summary>
public enum ScratchpadShortcutAction { CreatePad, CloseSelectedPad, HidePad, Find, FindNext, FindPrevious }

/// <summary>
/// What the keyboard means while the pad has focus. Tabs mirror the browser: Ctrl-T opens one and Ctrl-W closes one, or hides the
/// pad when only the last tab remains; Ctrl-F is find, Ctrl-G and Shift-Ctrl-G step through its matches. Upstream's Command is Ctrl
/// here: <c>ctrl</c> is Ctrl without Alt (and the Windows key); Shift is its own flag because only G takes it.
/// </summary>
public static class ScratchpadFocusedShortcut
{
    public static ScratchpadShortcutAction? Action(
        string? charactersIgnoringModifiers, bool ctrl, bool shift = false, bool canCreatePad = true, bool canClosePad = true)
    {
        if (!ctrl) return null;
        string? key = charactersIgnoringModifiers?.ToLowerInvariant();
        if (key == "g") return shift ? ScratchpadShortcutAction.FindPrevious : ScratchpadShortcutAction.FindNext;
        if (shift) return null;
        return key switch
        {
            "t" => canCreatePad ? ScratchpadShortcutAction.CreatePad : null,
            "w" => canClosePad ? ScratchpadShortcutAction.CloseSelectedPad : ScratchpadShortcutAction.HidePad,
            "f" => ScratchpadShortcutAction.Find,
            _ => null,
        };
    }
}

public static partial class ScratchpadSupport
{
    /// <summary>The fill sits over the existing material: zero preserves the familiar frosted pad, one fully covers what is behind the window.</summary>
    public const double BackgroundOpacityMin = 0;
    public const double BackgroundOpacityMax = 1;

    /// <summary>
    /// The size the whole pad draws at. It is a preference rather than a mark: plain text cannot carry a point size, and the heading
    /// levels step up from whatever this is set to. Untouched, the pad keeps the 13 it drew at before there was a choice.
    /// </summary>
    public const double DefaultTextSize = 13;
    public const double TextSizeMin = 10;
    public const double TextSizeMax = 22;

    public static double SanitizedTextSize(double value)
    {
        if (!double.IsFinite(value)) return DefaultTextSize;
        return Math.Min(Math.Max(Math.Round(value, MidpointRounding.AwayFromZero), TextSizeMin), TextSizeMax);
    }

    public static double SanitizedBackgroundOpacity(double value)
    {
        if (!double.IsFinite(value)) return BackgroundOpacityMax;
        return Math.Min(Math.Max(value, BackgroundOpacityMin), BackgroundOpacityMax);
    }

    public static bool DismissesOnOutsideClick(bool isPinned, bool exportModalActive) => !isPinned && !exportModalActive;

    public static string SanitizedPadName(string name)
    {
        string joined = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var info = new StringInfo(joined);
        // Upstream bounds the name in Characters (grapheme clusters), not UTF-16 units.
        return info.LengthInTextElements <= ScratchpadDocument.MaximumNameLength
            ? joined
            : info.SubstringByTextElements(0, ScratchpadDocument.MaximumNameLength);
    }

    public static string NextPadName(string defaultName, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToList();
        string baseName = SanitizedPadName(defaultName);
        string safeBase = baseName.Length == 0 ? "Scratchpad" : baseName;
        var used = new HashSet<string>(names);
        string firstName = $"{safeBase} 1";
        if (!used.Contains(safeBase) && !used.Contains(firstName)) return firstName;
        for (int number = 2; number <= ScratchpadDocument.MaximumPadCount; number++)
        {
            string candidate = $"{safeBase} {number}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"{safeBase} {names.Count + 1}";
    }

    public static bool RequiresCloseConfirmation(ScratchpadPad pad) => pad.Text.Length > 0;

    /// <summary>
    /// Whether the saved text expired: it only clears when a retention period is chosen and the last edit is older than that period.
    /// No saved text (or a clock that moved backwards) never clears.
    /// </summary>
    public static bool ShouldClear(DateTimeOffset? lastEdited, DateTimeOffset now, ScratchpadRetention retention)
    {
        if (ScratchpadRetentions.MaxIdleInterval(retention) is not { } limit || lastEdited is not { } edited) return false;
        return now - edited > limit;
    }

    /// <summary>Suggested name for the exported file, like "Scratchpad 2026-07-17.txt" (the local date).</summary>
    public static string ExportFileName(string title, DateTimeOffset date)
    {
        string safeTitle = SanitizedPadName(title).Replace('/', '-').Replace(':', '-');
        return $"{safeTitle} {date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.txt";
    }

    /// <summary>The URL scheme upstream gives a task's checkbox link in the preview.</summary>
    public const string TaskScheme = "vorssaint-task";

    /// <summary>
    /// Rendering is on demand and never changes the stored plain text: headings, lists, tasks, quotes and code become blocks that
    /// each carry their inline runs. A task knows the UTF-16 offset of its box in <paramref name="text"/>.
    /// </summary>
    public static IReadOnlyList<ScratchpadMarkdownBlock> MarkdownPreview(string text)
    {
        if (text.Length == 0) return [];
        var blocks = new ScratchpadMarkdownParser(text).Parse();
        return blocks.Count == 0
            ? [new ScratchpadMarkdownBlock(ScratchpadBlockKind.Paragraph, null, [new ScratchpadInlineRun(text)])]
            : blocks;
    }

    /// <summary>
    /// The checkbox opening a list item's text: "[ ]", "[x]" or "[X]", and the bare "[]" that is easy to type, followed by a space or
    /// the end.
    /// </summary>
    internal static (bool Checked, int Length)? TaskMarker(string text)
    {
        string chars = text.Length > 4 ? text[..4] : text;
        bool Closes(int at) => chars.Length == at || chars[at] == ' ' || chars[at] == '\t' || chars[at] == '\n';
        if (chars.Length == 0 || chars[0] != '[') return null;
        if (chars.Length > 1 && chars[1] == ']' && Closes(2)) return (false, 2);
        if (chars.Length > 2 && chars[2] == ']' && Closes(3))
        {
            if (chars[1] == ' ') return (false, 3);
            if (chars[1] == 'x' || chars[1] == 'X') return (true, 3);
        }
        return null;
    }
}

/// <summary>
/// A small CommonMark-style block parser for the preview: block quotes, bullet and numbered lists (nesting, lazy continuation lines),
/// tasks, ATX and setext headings, fenced and indented code and thematic breaks, with line counting done here (\n, \r\n and a lone \r
/// all end a line) so a task's box offset is exact whatever the text holds.
/// </summary>
internal sealed class ScratchpadMarkdownParser
{
    private enum NodeKind { Quote, List, Item }

    private sealed class Node
    {
        public NodeKind Kind;
        public int Id;
        public bool Ordered;
        public char Delimiter;      // a list's bullet character or ordered delimiter
        public int ItemCount;
        public int ContentIndent;   // an item's
        public bool StartedBlank;
        public bool HasChild;
        public int Ordinal;
    }

    private enum LeafKind { None, Paragraph, Fenced, Indented }

    /// <summary>The rest of one line, read in columns so tabs stop at multiples of four.</summary>
    private sealed class Cursor
    {
        public readonly string T;
        public int Pos;
        public int Col;
        public int Virt;   // columns of a tab that were only partly consumed

        public Cursor(string text) { T = text; }
        public Cursor Clone() => (Cursor)MemberwiseClone();
        public int Peek => Pos < T.Length ? T[Pos] : -1;
        public bool RestIsBlank()
        {
            for (int i = Pos; i < T.Length; i++)
            {
                if (T[i] != ' ' && T[i] != '\t') return false;
            }
            return true;
        }

        /// <summary>Columns of whitespace from here.</summary>
        public int Indent()
        {
            int columns = Virt, col = Col;
            for (int p = Pos; p < T.Length; p++)
            {
                if (T[p] == ' ') { columns++; col++; }
                else if (T[p] == '\t') { int advance = 4 - col % 4; columns += advance; col += advance; }
                else break;
            }
            return columns;
        }

        public void Skip(int columns)
        {
            while (columns > 0)
            {
                if (Virt > 0) { int d = Math.Min(Virt, columns); Virt -= d; columns -= d; continue; }
                if (Pos < T.Length && T[Pos] == ' ') { Pos++; Col++; columns--; }
                else if (Pos < T.Length && T[Pos] == '\t')
                {
                    int advance = 4 - Col % 4;
                    Pos++;
                    Col += advance;
                    if (advance > columns) { Virt += advance - columns; columns = 0; }
                    else columns -= advance;
                }
                else break;
            }
        }

        public void SkipAllIndent()
        {
            Virt = 0;
            while (Pos < T.Length && (T[Pos] == ' ' || T[Pos] == '\t'))
            {
                Col += T[Pos] == '\t' ? 4 - Col % 4 : 1;
                Pos++;
            }
        }

        public void Advance(int chars) { Pos += chars; Col += chars; }
        public string Rest => new string(' ', Virt) + T[Pos..];
    }

    private readonly string _source;
    private readonly List<Node> _stack = [];
    private readonly List<ScratchpadMarkdownBlock> _blocks = [];
    private readonly List<(int Offset, string Text)> _leafLines = [];
    private int _nextId = 1;
    private LeafKind _leaf;
    private Node[] _leafAncestry = [];
    private char _fenceChar;
    private int _fenceLength;
    private int _fenceIndent;

    public ScratchpadMarkdownParser(string source) { _source = source; }

    public List<ScratchpadMarkdownBlock> Parse()
    {
        int start = 0;
        while (start < _source.Length)
        {
            int end = start;
            while (end < _source.Length && _source[end] != '\n' && _source[end] != '\r') end++;
            ProcessLine(start, _source[start..end]);
            start = end;
            if (start < _source.Length)
                start += _source[start] == '\r' && start + 1 < _source.Length && _source[start + 1] == '\n' ? 2 : 1;
        }
        CloseLeaf();
        return _blocks;
    }

    // ---- Lines ---------------------------------------------------------------------------------------------------------

    private void ProcessLine(int lineStart, string text)
    {
        var cur = new Cursor(text);
        int matched = 0;
        for (; matched < _stack.Count; matched++)
        {
            var node = _stack[matched];
            if (node.Kind == NodeKind.List) continue;
            if (node.Kind == NodeKind.Quote)
            {
                if (cur.Indent() <= 3)
                {
                    var probe = cur.Clone();
                    probe.SkipAllIndent();
                    if (probe.Peek == '>')
                    {
                        cur.SkipAllIndent();
                        cur.Advance(1);
                        cur.Skip(1);   // the one space a quote marker may carry
                        continue;
                    }
                }
                break;
            }
            if (cur.RestIsBlank())
            {
                if (node.StartedBlank && !node.HasChild) break;
                continue;
            }
            if (cur.Indent() >= node.ContentIndent)
            {
                cur.Skip(node.ContentIndent);
                continue;
            }
            break;
        }

        if (_leaf == LeafKind.Fenced)
        {
            if (matched == _stack.Count) { FencedLine(cur); return; }
            CloseLeaf();
        }
        if (_leaf == LeafKind.Indented)
        {
            if (matched == _stack.Count && (cur.RestIsBlank() || cur.Indent() >= 4)) { IndentedLine(cur); return; }
            CloseLeaf();
        }

        bool started = false;
        while (true)
        {
            if (cur.RestIsBlank())
            {
                if (_leaf == LeafKind.Paragraph) CloseLeaf();
                CloseUnmatched(ref matched);
                return;
            }
            int indent = cur.Indent();
            if (indent >= 4)
            {
                if (_leaf == LeafKind.Paragraph) { AppendParagraphLine(cur, lineStart); return; }
                CloseLeaf();
                CloseUnmatched(ref matched);
                PopLists();
                MarkChild();
                cur.Skip(4);
                StartLeaf(LeafKind.Indented);
                _leafLines.Add((0, cur.Rest));
                return;
            }
            cur.SkipAllIndent();
            int c = cur.Peek;

            if (c == '>')
            {
                CloseLeaf();
                CloseUnmatched(ref matched);
                PopLists();
                MarkChild();
                _stack.Add(new Node { Kind = NodeKind.Quote, Id = _nextId++ });
                matched = _stack.Count;
                cur.Advance(1);
                cur.Skip(1);
                started = true;
                continue;
            }

            if (c == '#' && TryAtxHeading(cur, out int level, out string headingText))
            {
                CloseLeaf();
                CloseUnmatched(ref matched);
                PopLists();
                MarkChild();
                EmitHeading(level, headingText);
                return;
            }

            if ((c == '`' || c == '~') && TryFenceStart(cur, out char fenceChar, out int fenceLength))
            {
                CloseLeaf();
                CloseUnmatched(ref matched);
                PopLists();
                MarkChild();
                _fenceChar = fenceChar;
                _fenceLength = fenceLength;
                _fenceIndent = indent;
                StartLeaf(LeafKind.Fenced);
                return;
            }

            if ((c == '=' || c == '-') && _leaf == LeafKind.Paragraph && matched == _stack.Count && !started
                && IsSetextUnderline(cur, (char)c))
            {
                string paragraph = ParagraphText();
                EmitHeading(c == '=' ? 1 : 2, paragraph);
                _leaf = LeafKind.None;
                _leafLines.Clear();
                return;
            }

            if ((c == '-' || c == '*' || c == '_') && IsThematicBreak(cur, (char)c))
            {
                CloseLeaf();
                CloseUnmatched(ref matched);
                PopLists();
                MarkChild();
                Add(ScratchpadBlockKind.ThematicBreak, null, [], 0);
                return;
            }

            if (TryListMarker(cur, indent, started, matched, out bool ordered, out char delimiter, out int markerWidth, out int spaces))
            {
                CloseLeaf();
                CloseUnmatched(ref matched);
                cur.Pos += markerWidth;
                cur.Col += markerWidth;
                bool blank = cur.RestIsBlank();
                cur.Skip(spaces);
                StartItem(ordered, delimiter, indent + markerWidth + spaces, blank);
                matched = _stack.Count;
                started = true;
                continue;
            }

            break;
        }

        // Text. A paragraph already open takes it, as a lazy continuation line when its containers did not all match.
        if (_leaf == LeafKind.Paragraph) { AppendParagraphLine(cur, lineStart); return; }
        CloseUnmatched(ref matched);
        PopLists();
        MarkChild();
        StartLeaf(LeafKind.Paragraph);
        AppendParagraphLine(cur, lineStart);
    }

    // ---- Block starts ----------------------------------------------------------------------------------------------------

    private static bool TryAtxHeading(Cursor cur, out int level, out string text)
    {
        level = 0;
        text = "";
        int n = 0;
        while (cur.Pos + n < cur.T.Length && cur.T[cur.Pos + n] == '#') n++;
        if (n < 1 || n > 6) return false;
        int after = cur.Pos + n;
        if (after < cur.T.Length && cur.T[after] != ' ' && cur.T[after] != '\t') return false;
        string rest = cur.T[after..].Trim(' ', '\t');
        int k = rest.Length;
        while (k > 0 && rest[k - 1] == '#') k--;
        if (k == 0) rest = "";
        else if (k < rest.Length && (rest[k - 1] == ' ' || rest[k - 1] == '\t')) rest = rest[..k].TrimEnd(' ', '\t');
        level = n;
        text = rest;
        return true;
    }

    private static bool TryFenceStart(Cursor cur, out char fenceChar, out int length)
    {
        fenceChar = (char)cur.Peek;
        length = 0;
        while (cur.Pos + length < cur.T.Length && cur.T[cur.Pos + length] == fenceChar) length++;
        if (length < 3) return false;
        return fenceChar != '`' || cur.T.IndexOf('`', cur.Pos + length) < 0;
    }

    private static bool IsSetextUnderline(Cursor cur, char c)
    {
        int i = cur.Pos;
        while (i < cur.T.Length && cur.T[i] == c) i++;
        if (i == cur.Pos) return false;
        for (; i < cur.T.Length; i++)
        {
            if (cur.T[i] != ' ' && cur.T[i] != '\t') return false;
        }
        return true;
    }

    private static bool IsThematicBreak(Cursor cur, char c)
    {
        int count = 0;
        for (int i = cur.Pos; i < cur.T.Length; i++)
        {
            if (cur.T[i] == c) count++;
            else if (cur.T[i] != ' ' && cur.T[i] != '\t') return false;
        }
        return count >= 3;
    }

    private bool TryListMarker(Cursor cur, int indent, bool started, int matched,
        out bool ordered, out char delimiter, out int markerWidth, out int spaces)
    {
        ordered = false;
        delimiter = '\0';
        markerWidth = 0;
        spaces = 0;
        string t = cur.T;
        int p = cur.Pos;
        int number = 0;
        if (p < t.Length && (t[p] == '-' || t[p] == '+' || t[p] == '*'))
        {
            delimiter = t[p];
            markerWidth = 1;
        }
        else
        {
            int digits = 0;
            while (p + digits < t.Length && char.IsAsciiDigit(t[p + digits]) && digits < 9) digits++;
            if (digits == 0 || p + digits >= t.Length || (t[p + digits] != '.' && t[p + digits] != ')')) return false;
            ordered = true;
            delimiter = t[p + digits];
            markerWidth = digits + 1;
            number = int.Parse(t.AsSpan(p, digits), CultureInfo.InvariantCulture);
        }
        int after = p + markerWidth;
        if (after < t.Length && t[after] != ' ' && t[after] != '\t') return false;

        var probe = cur.Clone();
        probe.Pos += markerWidth;
        probe.Col += markerWidth;
        bool blank = probe.RestIsBlank();
        // A list may interrupt a paragraph only with a non-empty item, and a numbered one only starting at 1.
        if (_leaf == LeafKind.Paragraph && matched == _stack.Count && !started && (blank || (ordered && number != 1)))
            return false;
        int width = probe.Indent();
        spaces = blank ? 1 : (width >= 5 ? 1 : width);
        return true;
    }

    private void StartItem(bool ordered, char delimiter, int contentIndent, bool startedBlank)
    {
        var top = _stack.Count > 0 ? _stack[^1] : null;
        Node list;
        if (top is { Kind: NodeKind.List } && top.Ordered == ordered && top.Delimiter == delimiter)
        {
            list = top;
        }
        else
        {
            if (top is { Kind: NodeKind.List }) _stack.RemoveAt(_stack.Count - 1);
            MarkChild();
            list = new Node { Kind = NodeKind.List, Id = _nextId++, Ordered = ordered, Delimiter = delimiter };
            _stack.Add(list);
        }
        list.ItemCount++;
        _stack.Add(new Node { Kind = NodeKind.Item, Ordinal = list.ItemCount, ContentIndent = contentIndent, StartedBlank = startedBlank });
    }

    // ---- Stack ---------------------------------------------------------------------------------------------------------

    private void CloseUnmatched(ref int matched)
    {
        if (_stack.Count > matched)
        {
            CloseLeaf();
            _stack.RemoveRange(matched, _stack.Count - matched);
        }
        matched = _stack.Count;
    }

    /// <summary>A list holds only items, so anything else added after one leaves the list.</summary>
    private void PopLists()
    {
        while (_stack.Count > 0 && _stack[^1].Kind == NodeKind.List) _stack.RemoveAt(_stack.Count - 1);
    }

    private void MarkChild()
    {
        if (_stack.Count > 0 && _stack[^1].Kind == NodeKind.Item) _stack[^1].HasChild = true;
    }

    // ---- Leaves --------------------------------------------------------------------------------------------------------

    private void StartLeaf(LeafKind kind)
    {
        _leaf = kind;
        _leafAncestry = _stack.ToArray();
        _leafLines.Clear();
    }

    private void AppendParagraphLine(Cursor cur, int lineStart)
    {
        cur.SkipAllIndent();
        _leafLines.Add((lineStart + cur.Pos, cur.T[cur.Pos..]));
    }

    private void FencedLine(Cursor cur)
    {
        if (cur.Indent() <= 3)
        {
            var probe = cur.Clone();
            probe.SkipAllIndent();
            int run = 0;
            while (probe.Pos + run < probe.T.Length && probe.T[probe.Pos + run] == _fenceChar) run++;
            if (run >= _fenceLength)
            {
                probe.Advance(run);
                if (probe.RestIsBlank())
                {
                    CloseLeaf();
                    return;
                }
            }
        }
        cur.Skip(Math.Min(_fenceIndent, cur.Indent()));
        _leafLines.Add((0, cur.Rest));
    }

    private void IndentedLine(Cursor cur)
    {
        cur.Skip(Math.Min(4, cur.Indent()));
        _leafLines.Add((0, cur.RestIsBlank() ? "" : cur.Rest));
    }

    private string ParagraphText()
    {
        string raw = string.Join('\n', _leafLines.Select(l => l.Text));
        return raw.TrimEnd(' ', '\t');
    }

    private void CloseLeaf()
    {
        var kind = _leaf;
        _leaf = LeafKind.None;
        switch (kind)
        {
            case LeafKind.Paragraph:
                EmitParagraph();
                break;
            case LeafKind.Fenced:
            case LeafKind.Indented:
                {
                    var lines = _leafLines.Select(l => l.Text).ToList();
                    if (kind == LeafKind.Indented)
                    {
                        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                    }
                    string code = string.Join('\n', lines).TrimEnd('\n', '\r');
                    Add(ScratchpadBlockKind.Code, null, code.Length == 0 ? [] : [new ScratchpadInlineRun(code, Code: true)], 0);
                    break;
                }
        }
        _leafLines.Clear();
    }

    private void EmitParagraph()
    {
        string raw = ParagraphText();
        int offset = _leafLines[0].Offset;
        var (kind, containerId, quoteDepth) = Present(_leafAncestry);
        // A task is a bullet item whose text opens with a box written out in the source: a bracket escaped, encoded or put in code or
        // emphasis never starts the raw text, so it is only text.
        if (kind.Type == ScratchpadBlockType.UnorderedListItem
            && ScratchpadSupport.TaskMarker(raw) is { } marker
            && ScratchpadSupport.TaskBox(offset, _source) is { } written
            && written.Checked == marker.Checked && written.Length == marker.Length)
        {
            kind = ScratchpadBlockKind.TaskListItem(kind.Depth, marker.Checked, offset);
            int skip = marker.Length;
            if (skip < raw.Length && (raw[skip] == ' ' || raw[skip] == '\t' || raw[skip] == '\n')) skip++;
            raw = raw[skip..];
        }
        _blocks.Add(new ScratchpadMarkdownBlock(kind, containerId, ScratchpadInlineParser.Parse(raw), quoteDepth));
    }

    private void EmitHeading(int level, string text) =>
        _blocks.Add(new ScratchpadMarkdownBlock(ScratchpadBlockKind.Heading(level), null, ScratchpadInlineParser.Parse(text), 0));

    private void Add(ScratchpadBlockKind kind, int? containerId, IReadOnlyList<ScratchpadInlineRun> runs, int quoteDepth) =>
        _blocks.Add(new ScratchpadMarkdownBlock(kind, containerId, runs, quoteDepth));

    /// <summary>
    /// What a paragraph is, from the containers around it. Walked from the innermost out, so a quote met after a list holds that list,
    /// and its items keep the quote around their marker.
    /// </summary>
    private static (ScratchpadBlockKind Kind, int? ContainerId, int QuoteDepth) Present(Node[] ancestry)
    {
        int listDepth = 0;
        bool? listIsOrdered = null;
        int listOrdinal = 1;
        int quoteDepth = 0;
        int listQuoteDepth = 0;
        int? containerId = null;
        for (int i = ancestry.Length - 1; i >= 0; i--)
        {
            var node = ancestry[i];
            switch (node.Kind)
            {
                case NodeKind.Item:
                    if (listDepth == 0) listOrdinal = node.Ordinal;
                    break;
                case NodeKind.List:
                    listDepth++;
                    listIsOrdered ??= node.Ordered;
                    containerId = node.Id;
                    break;
                case NodeKind.Quote:
                    quoteDepth++;
                    if (listDepth > 0) listQuoteDepth++;
                    containerId = node.Id;
                    break;
            }
        }
        if (listIsOrdered == true)
            return (ScratchpadBlockKind.OrderedListItem(listOrdinal, Math.Max(1, listDepth)), containerId, listQuoteDepth);
        if (listIsOrdered == false)
            return (ScratchpadBlockKind.UnorderedListItem(Math.Max(1, listDepth)), containerId, listQuoteDepth);
        if (quoteDepth > 0) return (ScratchpadBlockKind.Quote(quoteDepth), containerId, 0);
        return (ScratchpadBlockKind.Paragraph, null, 0);
    }
}

/// <summary>Inline Markdown: escapes, entities, code spans, links, images (as their text), bare links, ** * _ and ~~.</summary>
internal static class ScratchpadInlineParser
{
    private readonly record struct Style(bool Bold, bool Italic, bool Strike, bool Code, string? Link);

    public static List<ScratchpadInlineRun> Parse(string text)
    {
        var runs = new List<ScratchpadInlineRun>();
        ParseSpan(text, 0, text.Length, default, runs);
        return runs;
    }

    private static void Emit(List<ScratchpadInlineRun> runs, string text, Style style)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0)
        {
            var last = runs[^1];
            if (last.Bold == style.Bold && last.Italic == style.Italic && last.Strikethrough == style.Strike
                && last.Code == style.Code && last.Link == style.Link)
            {
                runs[^1] = last with { Text = last.Text + text };
                return;
            }
        }
        runs.Add(new ScratchpadInlineRun(text, style.Bold, style.Italic, style.Strike, style.Code, style.Link));
    }

    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static void ParseSpan(string s, int start, int end, Style style, List<ScratchpadInlineRun> runs)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            Emit(runs, sb.ToString(), style);
            sb.Clear();
        }
        void TrimSpaces()
        {
            int n = sb.Length;
            while (n > 0 && sb[n - 1] == ' ') n--;
            sb.Length = n;
        }

        int i = start;
        while (i < end)
        {
            char c = s[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < end && s[i + 1] == '\n')
                    {
                        TrimSpaces();
                        sb.Append('\n');
                        i += 2;
                        continue;
                    }
                    if (i + 1 < end && AsciiPunctuation.Contains(s[i + 1]))
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                    continue;

                case '\n':
                    {
                        int spaces = 0;
                        while (spaces < sb.Length && sb[sb.Length - 1 - spaces] == ' ') spaces++;
                        TrimSpaces();
                        sb.Append(spaces >= 2 ? '\n' : ' ');   // a hard break keeps its line, a soft one reads as a space
                        i++;
                        continue;
                    }

                case '`':
                    {
                        int n = RunLength(s, i, end, '`');
                        int close = FindBacktickClose(s, i + n, end, n);
                        if (close < 0)
                        {
                            sb.Append('`', n);
                            i += n;
                            continue;
                        }
                        string content = s[(i + n)..close].Replace('\n', ' ');
                        if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0)
                            content = content[1..^1];
                        Flush();
                        Emit(runs, content, style with { Code = true });
                        i = close + n;
                        continue;
                    }

                case '!':
                case '[':
                    {
                        bool image = c == '!';
                        int open = image ? i + 1 : i;
                        if (open < end && s[open] == '[' && TryLink(s, open, end, out int closeBracket, out string destination, out int after))
                        {
                            Flush();
                            ParseSpan(s, open + 1, closeBracket, image ? style : style with { Link = destination }, runs);
                            i = after;
                            continue;
                        }
                        sb.Append(c);
                        i++;
                        continue;
                    }

                case '<':
                    {
                        int close = s.IndexOf('>', i + 1, end - i - 1);
                        if (close > i + 1)
                        {
                            string inner = s[(i + 1)..close];
                            if (!inner.Any(char.IsWhiteSpace)
                                && (inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                    || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                    || inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                            {
                                Flush();
                                Emit(runs, inner, style with { Link = inner });
                                i = close + 1;
                                continue;
                            }
                        }
                        sb.Append(c);
                        i++;
                        continue;
                    }

                case '&':
                    {
                        int semicolon = s.IndexOf(';', i + 1, Math.Min(end - i - 1, 12));
                        if (semicolon > i + 1)
                        {
                            string entity = s[i..(semicolon + 1)];
                            string decoded = WebUtility.HtmlDecode(entity);
                            if (decoded != entity)
                            {
                                sb.Append(decoded);
                                i = semicolon + 1;
                                continue;
                            }
                        }
                        sb.Append(c);
                        i++;
                        continue;
                    }

                case '*':
                case '_':
                case '~':
                    {
                        int n = RunLength(s, i, end, c);
                        int next = TryEmphasis(s, start, i, end, n, style, runs, sb, Flush);
                        if (next > 0)
                        {
                            i = next;
                            continue;
                        }
                        sb.Append(c, n);
                        i += n;
                        continue;
                    }

                case 'h':
                    if (style.Link == null && (i == start || !char.IsLetterOrDigit(s[i - 1]))
                        && (string.CompareOrdinal(s, i, "http://", 0, 7) == 0 || string.CompareOrdinal(s, i, "https://", 0, 8) == 0))
                    {
                        int j = i;
                        while (j < end && !char.IsWhiteSpace(s[j]) && s[j] != '<') j++;
                        while (j > i && ".,;:!?'\"".Contains(s[j - 1])) j--;
                        string url = s[i..j];
                        if (url.Length > 8)
                        {
                            Flush();
                            Emit(runs, url, style with { Link = url });
                            i = j;
                            continue;
                        }
                    }
                    sb.Append(c);
                    i++;
                    continue;

                default:
                    sb.Append(c);
                    i++;
                    continue;
            }
        }
        Flush();
    }

    private static int RunLength(string s, int index, int end, char c)
    {
        int n = 0;
        while (index + n < end && s[index + n] == c) n++;
        return n;
    }

    /// <summary>The start of the next run of exactly <paramref name="length"/> backticks at or after <paramref name="from"/>, or -1.</summary>
    private static int FindBacktickClose(string s, int from, int end, int length)
    {
        int j = from;
        while (j < end)
        {
            if (s[j] != '`') { j++; continue; }
            int run = RunLength(s, j, end, '`');
            if (run == length) return j;
            j += run;
        }
        return -1;
    }

    private static bool TryLink(string s, int open, int end, out int closeBracket, out string destination, out int after)
    {
        closeBracket = 0;
        destination = "";
        after = 0;
        int depth = 1;
        int j = open + 1;
        while (j < end)
        {
            char ch = s[j];
            if (ch == '\\') { j += 2; continue; }
            if (ch == '`')
            {
                int n = RunLength(s, j, end, '`');
                int close = FindBacktickClose(s, j + n, end, n);
                j = close < 0 ? j + n : close + n;
                continue;
            }
            if (ch == '[') depth++;
            else if (ch == ']' && --depth == 0) break;
            j++;
        }
        if (depth != 0 || j + 1 >= end || s[j + 1] != '(') return false;
        int p = j + 2;
        int parentheses = 1;
        while (p < end)
        {
            if (s[p] == '\\') { p += 2; continue; }
            if (s[p] == '(') parentheses++;
            else if (s[p] == ')' && --parentheses == 0) break;
            p++;
        }
        if (parentheses != 0 || p >= end) return false;
        string inner = s[(j + 2)..p].Trim();
        if (inner.StartsWith('<') && inner.IndexOf('>') is > 0 and var angle)
            inner = inner[1..angle];
        else
        {
            int space = inner.IndexOfAny([' ', '\t', '\n']);
            if (space >= 0) inner = inner[..space];
        }
        var unescaped = new StringBuilder();
        for (int k = 0; k < inner.Length; k++)
        {
            if (inner[k] == '\\' && k + 1 < inner.Length && AsciiPunctuation.Contains(inner[k + 1])) k++;
            unescaped.Append(inner[k]);
        }
        closeBracket = j;
        destination = unescaped.ToString();
        after = p + 1;
        return true;
    }

    /// <summary>
    /// Emphasis opened by the run of <paramref name="n"/> delimiters at <paramref name="i"/>: the new index when a closer was found
    /// (everything up to it is already emitted), or 0 when the run is only text.
    /// </summary>
    private static int TryEmphasis(string s, int start, int i, int end, int n, Style style, List<ScratchpadInlineRun> runs,
        StringBuilder sb, Action flush)
    {
        char c = s[i];
        char before = i > start ? s[i - 1] : ' ';
        char afterRun = i + n < end ? s[i + n] : ' ';
        if (char.IsWhiteSpace(afterRun)) return 0;
        if (c == '_' && char.IsLetterOrDigit(before)) return 0;
        for (int m = c == '~' ? 2 : Math.Min(n, 3); m >= 1; m--)
        {
            if (c == '~' && n != 2) return 0;
            int lead = n - m;
            int from = i + n;
            int closer = FindCloser(s, from, end, c, m);
            if (closer < 0) continue;
            sb.Append(c, lead);
            flush();
            var inner = c == '~'
                ? style with { Strike = true }
                : m == 1 ? style with { Italic = true }
                : m == 2 ? style with { Bold = true }
                : style with { Bold = true, Italic = true };
            ParseSpan(s, from, closer, inner, runs);
            return closer + m;
        }
        return 0;
    }

    private static int FindCloser(string s, int from, int end, char c, int m)
    {
        int j = from;
        while (j < end)
        {
            char ch = s[j];
            if (ch == '\\') { j += 2; continue; }
            if (ch == '`')
            {
                int n = RunLength(s, j, end, '`');
                int close = FindBacktickClose(s, j + n, end, n);
                j = close < 0 ? j + n : close + n;
                continue;
            }
            if (ch != c) { j++; continue; }
            int run = RunLength(s, j, end, c);
            bool runFits = c == '~' ? run == 2 : m == 1 ? run == 1 : run >= m;
            bool afterOk = c != '_' || j + run >= end || !char.IsLetterOrDigit(s[j + run]);
            if (runFits && afterOk && j > from && !char.IsWhiteSpace(s[j - 1])) return j;
            j += run;
        }
        return -1;
    }
}
