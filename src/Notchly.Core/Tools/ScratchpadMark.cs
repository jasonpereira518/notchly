// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the Markdown edit engine in upstream Sources/Vorssaint/Services/QuickTools/ScratchpadSupport.swift (ScratchpadMark,
// ScratchpadMarkEdit, ScratchpadSupport.edit(applying:), lineBreakEdit, taskBox/taskToggleEdit, completesEmptyTaskBox). Platform-free.
// All offsets are UTF-16 code units, which is what .NET strings and a WPF TextBox selection use.
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly.Core.Tools;

/// <summary>
/// A mark the formatting toolbar can apply. Each one is written as the Markdown a user would have typed by hand, so the pad
/// stays plain text and the preview parser needs nothing new to understand it.
/// </summary>
public enum ScratchpadMark { Bold, Italic, Strikethrough, Heading, Bullet, Numbered, Quote, Code, Link }

public static class ScratchpadMarks
{
    /// <summary>Every mark, in upstream's declaration order.</summary>
    public static IReadOnlyList<ScratchpadMark> All { get; } = Enum.GetValues<ScratchpadMark>();

    /// <summary>Upstream's raw value ("bold", "italic", ...).</summary>
    public static string RawValue(ScratchpadMark mark) => mark switch
    {
        ScratchpadMark.Bold => "bold",
        ScratchpadMark.Italic => "italic",
        ScratchpadMark.Strikethrough => "strikethrough",
        ScratchpadMark.Heading => "heading",
        ScratchpadMark.Bullet => "bullet",
        ScratchpadMark.Numbered => "numbered",
        ScratchpadMark.Quote => "quote",
        ScratchpadMark.Code => "code",
        _ => "link",
    };

    /// <summary>Inline marks wrap the selection; null for the line marks and the link.</summary>
    public static string? Wrap(ScratchpadMark mark) => mark switch
    {
        ScratchpadMark.Bold => "**",
        ScratchpadMark.Italic => "*",
        ScratchpadMark.Strikethrough => "~~",
        ScratchpadMark.Code => "`",
        _ => null,
    };

    /// <summary>
    /// Line marks prefix every line the selection touches, and clicking steps along this list before coming back off.
    /// A bullet is only on or off, but a heading has levels.
    /// </summary>
    public static IReadOnlyList<string> LineCycle(ScratchpadMark mark) => mark switch
    {
        ScratchpadMark.Heading => ["# ", "## ", "### "],
        ScratchpadMark.Bullet => ["- "],
        ScratchpadMark.Numbered => ["1. "],
        ScratchpadMark.Quote => ["> "],
        _ => [],
    };

    /// <summary>Ordered items are numbered as they are written, so each line gets its own prefix.</summary>
    public static bool IsNumbered(ScratchpadMark mark) => mark == ScratchpadMark.Numbered;

    /// <summary>A letter drawn in place of an icon, where one says the thing better.</summary>
    public static string? Glyph(ScratchpadMark mark) => mark == ScratchpadMark.Heading ? "H" : null;
}

/// <summary>A range of the text, in UTF-16 units.</summary>
public readonly record struct ScratchpadRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// One replacement, ready for the text box: what to swap out, what to put there, and where the selection belongs afterwards.
/// </summary>
public readonly record struct ScratchpadMarkEdit(ScratchpadRange Range, string Replacement, ScratchpadRange Selection)
{
    /// <summary>The text after the replacement.</summary>
    public string ApplyTo(string text) => ScratchpadSupport.Replace(text, Range, Replacement);
}

/// <summary>A plain replacement (a task toggle).</summary>
public readonly record struct ScratchpadReplacement(ScratchpadRange Range, string Text)
{
    public string ApplyTo(string text) => ScratchpadSupport.Replace(text, Range, Text);
}

/// <summary>What a line break does: the range replaced, the text put there and the caret offset after the edit.</summary>
public readonly record struct ScratchpadLineBreak(ScratchpadRange Range, string Replacement, int Caret)
{
    public string ApplyTo(string text) => ScratchpadSupport.Replace(text, Range, Replacement);
}

/// <summary>A task box found in the text: whether it is checked, and how long it is ("[]" is 2, "[ ]" and "[x]" are 3).</summary>
public readonly record struct ScratchpadTaskBox(bool Checked, int Length);

public static partial class ScratchpadSupport
{
    /// <summary>
    /// What a link needs next is the address, so it goes in as a placeholder that arrives selected: the first keystroke after
    /// the click replaces it.
    /// </summary>
    public const string LinkPlaceholder = "url";

    /// <summary>Replaces <paramref name="range"/> of <paramref name="text"/>, clamping the range to the text.</summary>
    public static string Replace(string text, ScratchpadRange range, string replacement)
    {
        int start = Math.Min(Math.Max(range.Start, 0), text.Length);
        int length = Math.Min(Math.Max(range.Length, 0), text.Length - start);
        return string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + length));
    }

    /// <summary>
    /// Every mark toggles: applying one to text that already carries it takes it back off, so a second click on the same
    /// button undoes the first. The selection may be stale; it is clamped to the text first.
    /// </summary>
    public static ScratchpadMarkEdit Edit(ScratchpadMark mark, string text, ScratchpadRange selection)
    {
        int location = Math.Min(Math.Max(selection.Start, 0), text.Length);
        int length = Math.Min(Math.Max(selection.Length, 0), text.Length - location);
        var safe = new ScratchpadRange(location, length);
        if (mark == ScratchpadMark.Link)
            return LinkEdit(text, safe);
        string? wrap = ScratchpadMarks.Wrap(mark);
        // A drag across words picks up the space after them, and Markdown will not close a mark against a space,
        // so "**word **" would never render.
        if (wrap != null)
            safe = TrimmingSpace(safe, text);
        if (mark == ScratchpadMark.Italic)
            return ItalicEdit(text, safe);
        if (wrap != null)
            return InlineEdit(wrap, text, safe);
        return LinePrefixEdit(mark, text, safe);
    }

    private static ScratchpadRange TrimmingSpace(ScratchpadRange selection, string text)
    {
        int start = selection.Start;
        int end = selection.End;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        // Nothing but space selected: there are no words to keep the marks on.
        return start == end ? selection : new ScratchpadRange(start, end - start);
    }

    private static string Sub(string text, ScratchpadRange range) => text.Substring(range.Start, range.Length);

    // ---- Link ----------------------------------------------------------------------------------------------------

    private static ScratchpadMarkEdit LinkEdit(string ns, ScratchpadRange selection)
    {
        // A second click means here what it means for every other mark: take it back off. The first click leaves the
        // address selected, not the whole link, so the link has to be recognised from anywhere inside it.
        var existing = EnclosingLink(ns, selection);
        if (existing is { } found)
        {
            if (found.IsImage)
                return new ScratchpadMarkEdit(selection, Sub(ns, selection), selection);
            string label = Sub(ns, found.Label);
            return new ScratchpadMarkEdit(found.Whole, label, new ScratchpadRange(found.Whole.Start, label.Length));
        }
        string selected = Sub(ns, selection);
        int opening = selected.Length + 3;   // "[" + text + "]("
        return new ScratchpadMarkEdit(
            selection,
            $"[{selected}]({LinkPlaceholder})",
            new ScratchpadRange(selection.Start + opening, LinkPlaceholder.Length));
    }

    private readonly record struct EnclosingLinkInfo(ScratchpadRange Whole, ScratchpadRange Label, bool IsImage);

    /// <summary>
    /// The link the selection sits in, with the range of its words. A caret has to be strictly inside one: resting it
    /// against either edge is where someone is about to write a new link, not undo the one behind them.
    /// </summary>
    private static EnclosingLinkInfo? EnclosingLink(string ns, ScratchpadRange selection)
    {
        for (int start = 0; start < ns.Length; start++)
        {
            if (ns[start] != '[' || IsEscaped(start, ns)) continue;
            int labelEnd = start + 1;
            int brackets = 1;
            while (labelEnd < ns.Length && brackets > 0)
            {
                if (!IsEscaped(labelEnd, ns))
                {
                    if (ns[labelEnd] == '[') brackets++;
                    if (ns[labelEnd] == ']') brackets--;
                }
                labelEnd++;
            }
            if (brackets != 0 || labelEnd >= ns.Length || ns[labelEnd] != '(') continue;
            int end = labelEnd + 1;
            int parentheses = 1;
            while (end < ns.Length && parentheses > 0)
            {
                if (!IsEscaped(end, ns))
                {
                    if (ns[end] == '(') parentheses++;
                    if (ns[end] == ')') parentheses--;
                }
                end++;
            }
            if (parentheses != 0) continue;
            var whole = new ScratchpadRange(start, end - start);
            if (selection.Length == 0)
            {
                if (!(selection.Start > whole.Start && selection.Start < end)) continue;
            }
            else if (!(selection.Start >= whole.Start && selection.Start + selection.Length <= end))
            {
                continue;
            }
            bool isImage = start > 0 && ns[start - 1] == '!' && !IsEscaped(start - 1, ns);
            return new EnclosingLinkInfo(whole, new ScratchpadRange(start + 1, labelEnd - start - 2), isImage);
        }
        return null;
    }

    private static bool IsEscaped(int index, string ns)
    {
        int cursor = index;
        while (cursor > 0 && ns[cursor - 1] == '\\') cursor--;
        return (index - cursor) % 2 == 1;
    }

    // ---- Inline marks --------------------------------------------------------------------------------------------

    private static ScratchpadMarkEdit InlineEdit(string wrap, string ns, ScratchpadRange selection)
    {
        int markerLength = wrap.Length;
        string selected = Sub(ns, selection);

        // The markers sit inside the selection, which is what a selection dragged across the whole marked span looks like.
        if (selected.Length >= wrap.Length * 2
            && selected.StartsWith(wrap, StringComparison.Ordinal) && selected.EndsWith(wrap, StringComparison.Ordinal))
        {
            string inner = selected.Substring(wrap.Length, selected.Length - wrap.Length * 2);
            return ExistingInlineEdit(wrap, inner, selection, selection);
        }

        // Or just outside it, which is where the second click lands: applying a mark leaves the inner text selected,
        // not the markers.
        var before = new ScratchpadRange(selection.Start - markerLength, markerLength);
        var after = new ScratchpadRange(selection.End, markerLength);
        if (before.Start >= 0 && after.End <= ns.Length
            && string.CompareOrdinal(ns, before.Start, wrap, 0, markerLength) == 0
            && string.CompareOrdinal(ns, after.Start, wrap, 0, markerLength) == 0)
        {
            return ExistingInlineEdit(
                wrap, selected,
                new ScratchpadRange(before.Start, markerLength * 2 + selection.Length),
                selection);
        }

        // With no selection the pair still goes in, with the caret between the halves, so the button can be clicked
        // before the words are typed.
        return new ScratchpadMarkEdit(
            selection,
            wrap + selected + wrap,
            new ScratchpadRange(selection.Start + markerLength, selection.Length));
    }

    /// <summary>
    /// Adjacent marked spans are not one wrapper. Joining their content under one pair gives the whole selection the mark;
    /// the next click removes it. Leave ambiguous or escaped delimiters alone rather than break the text.
    /// </summary>
    private static ScratchpadMarkEdit ExistingInlineEdit(string wrap, string inner, ScratchpadRange whole, ScratchpadRange selection)
    {
        string[] pieces = inner.Split(wrap);
        if (pieces.Length == 1)
            return new ScratchpadMarkEdit(whole, inner, new ScratchpadRange(whole.Start, inner.Length));
        string original = wrap + inner + wrap;
        bool hasOtherMarkup = wrap == "`"
            ? inner.StartsWith('`') || inner.EndsWith('`')
            : inner.Contains('`') || inner.Contains("](", StringComparison.Ordinal);
        if ((pieces.Length - 1) % 2 != 0 || inner.Contains("\\" + wrap, StringComparison.Ordinal) || hasOtherMarkup)
            return new ScratchpadMarkEdit(whole, original, selection);
        string joined = string.Concat(pieces);
        return new ScratchpadMarkEdit(
            whole, wrap + joined + wrap,
            new ScratchpadRange(whole.Start + wrap.Length, joined.Length));
    }

    /// <summary>
    /// Italic's marker is a single star, which is also half of bold's pair, so no one character says whether a star belongs to
    /// italic. What settles it is how many stars run together: an odd run has an italic star on the inside of however many
    /// bold pairs sit outside it.
    /// </summary>
    private static ScratchpadMarkEdit ItalicEdit(string ns, ScratchpadRange selection)
    {
        string selected = Sub(ns, selection);
        int leading = 0;
        while (leading < selected.Length && selected[leading] == '*') leading++;
        int trailing = 0;
        while (trailing < selected.Length && selected[selected.Length - 1 - trailing] == '*') trailing++;

        // The stars are inside the selection, which is what selecting the whole marked span looks like. Only the
        // innermost pair is italic's.
        if (leading % 2 == 1 && trailing % 2 == 1 && selected.Length > leading + trailing - 1)
        {
            string inner = selected.Substring(1, selected.Length - 2);
            if (leading == 1 && trailing == 1 && inner.Contains('*') && !inner.Contains("**", StringComparison.Ordinal))
                return ExistingInlineEdit("*", inner, selection, selection);
            if (inner.Contains("***", StringComparison.Ordinal))
                return new ScratchpadMarkEdit(selection, selected, selection);
            return new ScratchpadMarkEdit(selection, inner, new ScratchpadRange(selection.Start, inner.Length));
        }

        // Or just outside it, which is where the second click lands, since the first leaves the words selected and the
        // markers around them.
        if (StarRunEndingAt(ns, selection.Start) % 2 == 1 && StarRunStartingAt(ns, selection.End) % 2 == 1)
        {
            var outer = new ScratchpadRange(selection.Start - 1, selection.Length + 2);
            if (StarRunEndingAt(ns, selection.Start) == 1
                && StarRunStartingAt(ns, selection.End) == 1
                && selected.Contains('*') && !selected.Contains("**", StringComparison.Ordinal))
            {
                return ExistingInlineEdit("*", selected, outer, selection);
            }
            if (selected.Contains("***", StringComparison.Ordinal))
                return new ScratchpadMarkEdit(outer, Sub(ns, outer), selection);
            return new ScratchpadMarkEdit(outer, selected, new ScratchpadRange(outer.Start, selection.Length));
        }

        return new ScratchpadMarkEdit(
            selection,
            "*" + selected + "*",
            new ScratchpadRange(selection.Start + 1, selection.Length));
    }

    private static int StarRunEndingAt(string ns, int index)
    {
        int count = 0;
        int cursor = index - 1;
        while (cursor >= 0 && cursor < ns.Length && ns[cursor] == '*')
        {
            count++;
            cursor--;
        }
        return count;
    }

    private static int StarRunStartingAt(string ns, int index)
    {
        int count = 0;
        int cursor = index;
        while (cursor < ns.Length && ns[cursor] == '*')
        {
            count++;
            cursor++;
        }
        return count;
    }

    // ---- Line marks ----------------------------------------------------------------------------------------------

    private static bool IsLineTerminator(char c) => c == '\n' || c == '\r';

    /// <summary>
    /// The lines the range touches, terminators included (NSString.lineRange). A non-empty range that ends just after a line
    /// terminator does not reach into the next line. \n, \r and \r\n end lines.
    /// </summary>
    internal static ScratchpadRange LineRange(string text, ScratchpadRange range)
    {
        int start = Math.Min(range.Start, text.Length);
        while (start > 0 && !IsLineTerminator(text[start - 1])) start--;
        int end = range.Length > 0 ? Math.Min(range.End, text.Length) - 1 : Math.Min(range.Start, text.Length);
        while (end < text.Length && !IsLineTerminator(text[end])) end++;
        if (end < text.Length)
            end += text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
        return new ScratchpadRange(start, end - start);
    }

    private static ScratchpadMarkEdit LinePrefixEdit(ScratchpadMark mark, string ns, ScratchpadRange selection)
    {
        var cycle = ScratchpadMarks.LineCycle(mark);
        if (cycle.Count == 0)
            return new ScratchpadMarkEdit(selection, "", selection);
        var lineRange = LineRange(ns, selection);
        string block = Sub(ns, lineRange);

        // lineRange keeps the line terminator. Splitting with it still attached invents a trailing empty line that would
        // collect a prefix of its own.
        string terminator = block.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : block.EndsWith('\n') ? "\n"
            : block.EndsWith('\r') ? "\r" : "";
        string body = block[..^terminator.Length];
        var lines = new List<string>();
        var separators = new List<string>();   // separators[i] follows lines[i], except after the last line
        int lineStart = 0;
        while (true)
        {
            int j = lineStart;
            while (j < body.Length && !IsLineTerminator(body[j])) j++;
            lines.Add(body[lineStart..j]);
            if (j >= body.Length) break;
            string separator = body[j] == '\r' && j + 1 < body.Length && body[j + 1] == '\n' ? "\r\n" : body[j].ToString();
            separators.Add(separator);
            lineStart = j + separator.Length;
        }

        // Indentation is what nests a list, so the mark goes after it and the line keeps its depth rather than growing
        // "-   - text".
        static int IndentLength(string line)
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            return n;
        }
        // Blank lines are skipped rather than marked, except in a pad that is blank altogether, where the click is how
        // the first line gets started.
        var written = lines.Select(l => l[IndentLength(l)..]).Where(l => l.Length > 0).ToList();
        // Where the lines already sit in the cycle, and so what the click means. Past the last step the mark comes
        // off, so a button clicked until something looks right always has an empty rung to land on.
        int? step = null;
        if (ScratchpadMarks.IsNumbered(mark))
        {
            step = written.Count > 0 && written.All(l => NumberedPrefix(l) != null) ? 0 : null;
        }
        else
        {
            for (int i = 0; i < cycle.Count; i++)
            {
                string prefix = cycle[i];
                if (written.Count > 0 && written.All(l => l.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    step = i;
                    break;
                }
            }
        }
        string next = step is { } s ? (s + 1 < cycle.Count ? cycle[s + 1] : "") : cycle[0];
        int ordinal = 0;
        var updated = new List<string>();
        foreach (string line in lines)
        {
            string lead = line[..IndentLength(line)];
            string rest = line[lead.Length..];
            if (rest.Length == 0 && lines.Count > 1)
            {
                updated.Add(line);
                continue;
            }
            string stripped = StrippingLineMark(rest);
            if (next.Length == 0)
            {
                updated.Add(lead + stripped);
                continue;
            }
            if (!ScratchpadMarks.IsNumbered(mark))
            {
                updated.Add(lead + next + stripped);
                continue;
            }
            ordinal++;
            updated.Add(lead + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + stripped);
        }

        var replacement = new StringBuilder();
        for (int i = 0; i < updated.Count; i++)
        {
            replacement.Append(updated[i]);
            if (i < separators.Count) replacement.Append(separators[i]);
        }
        replacement.Append(terminator);
        string replacementText = replacement.ToString();
        int firstDelta = updated[0].Length - lines[0].Length;
        int totalDelta = replacementText.Length - block.Length;
        // Both ends move with the prefixes before them. The start is held at the line's start when a removed prefix would
        // pull it past that, and the end is worked out on its own so that hold is not added to the length, which ran the
        // selection into the text after the lines.
        int newStart = Math.Max(lineRange.Start, selection.Start + firstDelta);
        int newEnd = Math.Max(newStart, selection.End + totalDelta);
        return new ScratchpadMarkEdit(lineRange, replacementText, new ScratchpadRange(newStart, newEnd - newStart));
    }

    /// <summary>
    /// A line already carrying a line mark has it replaced rather than stacked, whichever mark it was: a heading cannot
    /// grow into "# ## text", and asking a bullet for a heading gives a heading, not "# - text".
    /// </summary>
    private static string StrippingLineMark(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#') hashes++;
        if (hashes > 0 && hashes <= 6 && hashes < line.Length && line[hashes] == ' ')
            return line[(hashes + 1)..];
        if (line.Length > 0 && "-*+>".Contains(line[0]) && line.Length > 1 && line[1] == ' ')
            return line[2..];
        if (NumberedPrefix(line) is { } numbered)
            return line[numbered.Length..];
        return line;
    }

    /// <summary>"12. " and the like, however many digits the list has run to.</summary>
    private static string? NumberedPrefix(string line)
    {
        int digits = 0;
        while (digits < line.Length && char.IsNumber(line[digits])) digits++;
        if (digits == 0 || digits + 1 >= line.Length || line[digits] != '.' || line[digits + 1] != ' ') return null;
        return line[..(digits + 2)];
    }

    // ---- Task boxes ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The box whose "[" sits at UTF-16 offset <paramref name="box"/> of <paramref name="text"/>: "[ ]", "[x]", "[X]" or the
    /// bare "[]", or null when no box starts there.
    /// </summary>
    public static ScratchpadTaskBox? TaskBox(int box, string text)
    {
        char? Character(int offset) => box + offset < text.Length && box + offset >= 0 ? text[box + offset] : null;
        if (box < 0 || Character(0) != '[') return null;
        if (Character(1) == ']') return new ScratchpadTaskBox(false, 2);
        if (Character(1) is not { } mark || (mark != ' ' && mark != 'x' && mark != 'X') || Character(2) != ']') return null;
        return new ScratchpadTaskBox(mark != ' ', 3);
    }

    /// <summary>
    /// The edit that checks or unchecks the box whose "[" sits at UTF-16 offset <paramref name="box"/>, or null when the text
    /// holds no box there any more.
    /// </summary>
    public static ScratchpadReplacement? TaskToggleEdit(int box, string text)
    {
        if (TaskBox(box, text) is not { } current) return null;
        if (current.Length == 2) return new ScratchpadReplacement(new ScratchpadRange(box + 1, 0), "x");
        return new ScratchpadReplacement(new ScratchpadRange(box + 1, 1), current.Checked ? " " : "x");
    }

    private static readonly Regex ListMarker = new(
        @"^([ \t]*(?:>[ \t]*)*)([-*+]|\d+[.)])([ \t]+)(\[[ xX]?\](?:[ \t]+|$))?", RegexOptions.CultureInvariant);

    /// <summary>
    /// What Shift-Return does with the caret at <paramref name="selection"/>: in a list item it opens the next one, with the same
    /// marker, a fresh unchecked box for a task and the next number for a numbered item; on an empty item it ends the list
    /// instead; anywhere else it is a plain line break. <c>Range</c> is what is replaced and <c>Caret</c> the offset after the edit.
    /// <paramref name="newline"/> is the line break written; by default "\r\n" when the text already uses it, else "\n".
    /// </summary>
    public static ScratchpadLineBreak LineBreakEdit(string text, ScratchpadRange selection, string? newline = null)
    {
        newline ??= text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var plain = new ScratchpadLineBreak(selection, newline, selection.Start + newline.Length);
        if (selection.Length != 0 || selection.Start > text.Length || selection.Start < 0) return plain;
        var line = LineRange(text, new ScratchpadRange(selection.Start, 0));
        var content = line;
        while (content.Length > 0 && IsLineTerminator(text[content.End - 1]))
            content = new ScratchpadRange(content.Start, content.Length - 1);
        string lineText = Sub(text, content);
        var match = ListMarker.Match(lineText);
        if (!match.Success) return plain;
        int markerEnd = content.Start + match.Length;
        if (selection.Start < markerEnd) return plain;
        string prefix = match.Groups[1].Value;
        string marker = match.Groups[2].Value;
        string spacing = match.Groups[3].Value;
        bool hasBox = match.Groups[4].Success;
        if (selection.Start == content.End
            && (match.Length == lineText.Length || lineText[match.Length..].Trim(' ', '\t').Length == 0))
        {
            // An empty item: the list ends, and the marker goes.
            return new ScratchpadLineBreak(content, "", content.Start);
        }
        string nextMarker = marker;
        // The largest number has no next one; its item repeats it.
        if (long.TryParse(marker.AsSpan(0, marker.Length - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long digits) && digits < long.MaxValue)
        {
            nextMarker = (digits + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + marker[^1];
        }
        string replacement = newline + prefix + nextMarker + spacing + (hasBox ? "[ ] " : "");
        return new ScratchpadLineBreak(selection, replacement, selection.Start + replacement.Length);
    }

    private static readonly Regex TaskLine = new(@"^[\s>]*[-*+][ \t]+\[( |x|X)?\]([ \t]|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="before"/>, the text of a line up to the caret, ends with the "[" of a list item's checkbox, so that
    /// a "]" typed next closes an empty box: "- [", "  * [", "> - [".
    /// </summary>
    public static bool CompletesEmptyTaskBox(string before) =>
        before.EndsWith('[') && TaskLine.IsMatch(before + " ]");
}
