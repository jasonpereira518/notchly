// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchWatchSupport.swift: the rules, the tracker that decides
// when one is met, and the reading, number and area maths. Windows-specific: the region's number format comes
// from a CultureInfo, areas are in screen pixels, and the coarse picture is summed from BGRA bytes.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Notch;

/// <summary>What a watched area has to do before the island speaks up.</summary>
public enum NotchWatchCondition
{
    /// <summary>Its text changes, or its picture when it holds no text.</summary>
    Changes,
    /// <summary>It changed at least once and then held still, as a log or a progress bar does when the work ends.</summary>
    Settles,
    /// <summary>It shows the text the person typed, such as "Done".</summary>
    Contains,
    /// <summary>Its number reaches the one the person typed, counting up or down.</summary>
    Reaches,
}

/// <summary>Why a watch ended.</summary>
public abstract record NotchWatchOutcome
{
    public sealed record Changed : NotchWatchOutcome;
    public sealed record Settled : NotchWatchOutcome;
    public sealed record Shows(string Text) : NotchWatchOutcome;
    public sealed record Reached(string Text) : NotchWatchOutcome;
    public sealed record Closed : NotchWatchOutcome;

    /// <summary>Upstream NotchWatchStrings.outcome, in English.</summary>
    public string Title => this switch
    {
        Changed => "It changed",
        Settled => "It stopped changing",
        Shows s => $"It shows {s.Text}",
        Reached r => $"It reached {r.Text}",
        _ => "The window closed",
    };
}

/// <summary>
/// Follows one area reading after reading and decides when its condition is met. An area whose pixels did not
/// move is not read again: it comes back with its last signature, so a change that holds still is confirmed and a
/// quiet area can settle. <c>Observe(null, …)</c> only lets time pass.
/// </summary>
public sealed class NotchWatchTracker
{
    public const double SettleInterval = 30;
    /// <summary>How long a reading must keep differing before it counts as a change.</summary>
    public const double ChangeConfirmation = 3;

    public NotchWatchCondition Condition { get; }
    public string Text { get; }
    public double? Target { get; }
    /// <summary>How the watched app writes numbers: as the Windows region does.</summary>
    public string DecimalSeparator { get; }
    public bool GroupsWithSpace { get; }

    public string? Baseline { get; private set; }
    public DateTime? ChangedSince { get; private set; }
    public string? Current { get; private set; }
    public DateTime? LastChange { get; private set; }
    public bool SawChange { get; private set; }
    public double? Start { get; private set; }

    public NotchWatchTracker(NotchWatchCondition condition, string text = "", double? target = null,
                             string decimalSeparator = ".", bool groupsWithSpace = false)
    {
        Condition = condition;
        Text = text;
        Target = target;
        DecimalSeparator = decimalSeparator;
        GroupsWithSpace = groupsWithSpace;
    }

    /// <summary>A tracker that reads numbers as <paramref name="culture"/> writes them.</summary>
    public static NotchWatchTracker For(NotchWatchCondition condition, string text, double? target, CultureInfo culture) =>
        new(condition, text, target, culture.NumberFormat.NumberDecimalSeparator is { Length: > 0 } mark ? mark : ".",
            NotchWatchSupport.GroupsWithSpace(culture));

    /// <summary>Whether the rule can be met at all: a text to find or a number to reach must have been typed.</summary>
    public bool IsReady => Condition switch
    {
        NotchWatchCondition.Contains => NotchWatchSupport.Normalized(Text).Length > 0,
        NotchWatchCondition.Reaches => Target is not null,
        _ => true,
    };

    /// <summary>
    /// <paramref name="signature"/> compares two readings: the area's normalized text, or a coarse picture of it
    /// when it holds no text. <paramref name="reading"/> is the text as read, for finding words and numbers.
    /// </summary>
    public NotchWatchOutcome? Observe(string? signature, string reading, DateTime at)
    {
        if (!IsReady) return null;
        switch (Condition)
        {
            case NotchWatchCondition.Changes:
                if (signature is null) return null;
                if (Baseline is null) { Baseline = signature; return null; }
                if (NotchWatchSupport.SameSignature(signature, Baseline)) { ChangedSince = null; return null; }
                // A change counts once it outlasts a few seconds of readings, so a frame caught halfway through
                // redrawing or a blinking caret does not end the watch, while a reading that keeps moving does.
                if (ChangedSince is not { } since) { ChangedSince = at; return null; }
                return (at - since).TotalSeconds >= ChangeConfirmation ? new NotchWatchOutcome.Changed() : null;
            case NotchWatchCondition.Settles:
                if (signature is not null && !NotchWatchSupport.SameSignature(signature, Current))
                {
                    if (Current is not null) SawChange = true;
                    Current = signature;
                    LastChange = at;
                    return null;
                }
                return SawChange && LastChange is { } last && (at - last).TotalSeconds >= SettleInterval
                    ? new NotchWatchOutcome.Settled() : null;
            case NotchWatchCondition.Contains:
                return NotchWatchSupport.Normalized(reading).Contains(NotchWatchSupport.Normalized(Text), StringComparison.Ordinal)
                    ? new NotchWatchOutcome.Shows(Text) : null;
            default:
                if (Target is not { } target
                    || NotchWatchSupport.Number(reading, DecimalSeparator, GroupsWithSpace) is not { } value) return null;
                var shown = NotchWatchSupport.Headline(reading, GroupsWithSpace);
                if (Start is not { } start)
                {
                    Start = value;
                    return value == target ? new NotchWatchOutcome.Reached(shown) : null;
                }
                if (start < target && value >= target) return new NotchWatchOutcome.Reached(shown);
                if (start > target && value <= target) return new NotchWatchOutcome.Reached(shown);
                return null;
        }
    }
}

/// <summary>
/// A chosen area can still be preparing after its picker closes; stopping or choosing again makes that
/// preparation stale (upstream NotchWatchService.choiceGeneration).
/// </summary>
public sealed class NotchWatchChoices
{
    private int _generation;

    /// <summary>A new choice begins; any older one is now stale.</summary>
    public int Begin() => ++_generation;

    /// <summary>Stopping: every choice still on its way is stale.</summary>
    public void Cancel() => _generation++;

    /// <summary>A choice's result is used only while it is the newest one and Watch is still on.</summary>
    public bool Accepts(int choice, bool enabled) => enabled && choice == _generation;
}

public static class NotchWatchSupport
{
    /// <summary>How often the area is read: often while its page is open, so the preview feels live, and less
    /// while it only feeds the closed island.</summary>
    public const double VisibleInterval = 1, BackgroundInterval = 2;
    /// <summary>A still picture is read again this often: a few changed words in a large area can move its coarse
    /// picture too little to notice.</summary>
    public const double RereadInterval = 10;
    /// <summary>Text recognition gets slower with size and gains nothing past this.</summary>
    public const int MaximumRecognitionSide = 900;
    public const int HeadlineLength = 22;
    public static readonly (double Min, double Max) StripWingRange = (56, 150);
    /// <summary>An area without text shows itself, this wide, where its reading would be.</summary>
    public const double ThumbnailWidth = 40;
    /// <summary>The notice's time on screen, as a timer's (Windows: the island has no notification fallback yet).</summary>
    public const double NoticeSeconds = 6;

    public static bool IsEnabled(SettingsStore store) =>
        NotchSupport.IsEnabled(store) && store.IsAvailable(AppFeature.NotchWatch) && store.GetBool(DefaultsKey.NotchWatchEnabled)
        && NotchSupport.Modules(store.GetString(DefaultsKey.NotchModuleOrder), store.GetString(DefaultsKey.NotchHiddenModules))
                       .Contains(NotchModule.Watch);

    /// <summary>The rule last chosen; a first watch speaks up on any change.</summary>
    public static NotchWatchCondition SavedCondition(SettingsStore store) =>
        ParseCondition(store.GetString(DefaultsKey.NotchWatchCondition)) ?? NotchWatchCondition.Changes;

    public static NotchWatchCondition? ParseCondition(string raw) => raw switch
    {
        "changes" => NotchWatchCondition.Changes,
        "settles" => NotchWatchCondition.Settles,
        "contains" => NotchWatchCondition.Contains,
        "reaches" => NotchWatchCondition.Reaches,
        _ => null,
    };

    public static string Raw(NotchWatchCondition condition) => condition switch
    {
        NotchWatchCondition.Settles => "settles",
        NotchWatchCondition.Contains => "contains",
        NotchWatchCondition.Reaches => "reaches",
        _ => "changes",
    };

    /// <summary>Upstream NotchWatchStrings.condition, in English.</summary>
    public static string Title(NotchWatchCondition condition) => condition switch
    {
        NotchWatchCondition.Settles => "When it stops changing",
        NotchWatchCondition.Contains => "When it shows",
        NotchWatchCondition.Reaches => "When the number reaches",
        _ => "When it changes",
    };

    /// <summary>The strip's wing for a reading this wide, kept to 56–150 points.</summary>
    public static double FittedWing(double wing) =>
        Math.Min(StripWingRange.Max, Math.Max(StripWingRange.Min, double.IsFinite(wing) ? Math.Ceiling(wing) : 0));

    // MARK: Readings

    private static readonly char[] Newlines = ['\n', '\r', (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>For comparing and finding words: no case, accents, width or extra spaces.</summary>
    public static string Normalized(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) kept.Append(c);
        var folded = kept.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Join(' ', folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The area's lines, top to bottom, as a person can pick one of them.</summary>
    public static IReadOnlyList<string> Lines(string text) =>
        text.Split(Newlines).Select(TrimSpaces).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// The part of the text worth showing beside the camera: a percentage first, then a short first line as it is,
    /// then a clock or a number with its unit, and otherwise the first line cut short.
    /// </summary>
    public static string Headline(string text) => Headline(text, GroupsWithSpace(CultureInfo.CurrentCulture));

    public static string Headline(string text, bool groupsWithSpace)
    {
        var lines = Lines(text);
        if (lines.Count == 0) return "";
        var first = lines[0];
        var joined = string.Join(' ', lines);
        if (FirstMatch(PercentPattern(groupsWithSpace), text) is { } percent) return RemoveSpaces(percent);
        if (Length(first) <= HeadlineLength) return first;
        if (FirstMatch(ClockPattern, joined) is { } clock) return clock;
        if (FirstMatch(AmountPattern, joined) is { } amount) return amount;
        return Prefix(first, HeadlineLength - 1) + "…";
    }

    /// <summary>The line the person chose to show, cut to the strip's length, or the automatic reading while that
    /// line is missing.</summary>
    public static string Headline(string text, int? line)
    {
        var lines = Lines(text);
        if (line is not { } index || index < 0 || index >= lines.Count) return Headline(text);
        var chosen = lines[index];
        return Length(chosen) <= HeadlineLength ? chosen : Prefix(chosen, HeadlineLength - 1) + "…";
    }

    // MARK: Numbers

    /// <summary>
    /// The number a "reaches" rule compares: a percentage first, otherwise the first number in the text. Both
    /// decimal marks are understood; <paramref name="decimalSeparator"/> settles a lone mark before three digits,
    /// which groups thousands in one region and starts decimals in another. <paramref name="groupsWithSpace"/>
    /// also reads 10 000 as one number, as regions that group thousands with a space write it.
    /// </summary>
    public static double? Number(string text, string decimalSeparator = ".", bool groupsWithSpace = false)
    {
        if (FirstMatch(PercentPattern(groupsWithSpace), text) is { } percent)
            return Parse(percent[..^1], decimalSeparator);
        var pattern = groupsWithSpace ? SpacedNumberPattern + "|" + NumberPattern : NumberPattern;
        return FirstMatch(pattern, text) is { } raw ? Parse(raw, decimalSeparator) : null;
    }

    /// <summary>Whether the region groups thousands with a space (French, for one).</summary>
    public static bool GroupsWithSpace(CultureInfo culture)
    {
        var separator = culture.NumberFormat.NumberGroupSeparator;
        return separator.Length > 0 && separator.All(c => c != '\n' && char.IsWhiteSpace(c));
    }

    public static double? Parse(string raw, string decimalSeparator = ".")
    {
        var digits = RemoveSpaces(raw);
        var negative = digits.StartsWith('-') || digits.StartsWith('−');
        digits = digits.Trim('+', '-', '−');
        var lastDot = digits.LastIndexOf('.');
        var lastComma = digits.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
            // The later mark separates the decimals; the other groups thousands.
            digits = lastDot > lastComma ? digits.Replace(",", "") : digits.Replace(".", "").Replace(',', '.');
        else if (lastComma >= 0)
            digits = GroupsThousands(digits, ',', decimalSeparator) ? digits.Replace(",", "") : digits.Replace(',', '.');
        else if (lastDot >= 0 && GroupsThousands(digits, '.', decimalSeparator))
            digits = digits.Replace(".", "");
        if (digits.Length == 0 || !digits.All(c => c is >= '0' and <= '9' or '.')
            || !double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)) return null;
        return negative ? -value : value;
    }

    /// <summary>
    /// Whether the only mark in a number groups thousands: it appears more than once, as in 1.234.567, or once
    /// before exactly three digits, as in 1,234, unless the region writes decimals with it or the number starts at
    /// zero, as 0,125 does.
    /// </summary>
    private static bool GroupsThousands(string digits, char mark, string decimalSeparator)
    {
        var groups = digits.Split(mark);
        if (groups.Length > 2) return true;
        if (groups.Length != 2 || groups[1].Length != 3 || groups[0] == "0") return false;
        return decimalSeparator != mark.ToString();
    }

    /// <summary>A typed target as the page shows it again: without grouping, which some regions write with a space
    /// a number cannot be read across.</summary>
    public static string Formatted(double value, CultureInfo culture) => value.ToString("0.##", culture);

    /// <summary>A typed target, read as the page writes numbers.</summary>
    public static double? TypedNumber(string text, CultureInfo culture) =>
        Number(RemoveSpaces(text), culture.NumberFormat.NumberDecimalSeparator is { Length: > 0 } mark ? mark : ".");

    // MARK: Pictures

    public const int FingerprintSide = 16;

    /// <summary>
    /// The area as a 16 × 16 grid of coarse grey levels, from top-down BGRA pixels: small enough that antialiasing
    /// and compression noise read the same, sharp enough that a moving bar, a new line or a changed colour do not.
    /// </summary>
    public static byte[]? Fingerprint(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return null;
        const int side = FingerprintSide;
        var cells = new byte[side * side];
        for (var cy = 0; cy < side; cy++)
        {
            var y0 = cy * height / side;
            var y1 = Math.Max(y0 + 1, (cy + 1) * height / side);
            for (var cx = 0; cx < side; cx++)
            {
                var x0 = cx * width / side;
                var x1 = Math.Max(x0 + 1, (cx + 1) * width / side);
                double sum = 0;
                var count = 0;
                for (var y = y0; y < y1 && y < height; y++)
                {
                    var row = y * stride;
                    for (var x = x0; x < x1 && x < width; x++)
                    {
                        var i = row + x * 4;
                        sum += 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                        count++;
                    }
                }
                var grey = count == 0 ? 0 : (int)Math.Round(sum / count);
                cells[cy * side + cx] = (byte)(Math.Clamp(grey, 0, 255) / 16);
            }
        }
        return cells;
    }

    /// <summary>Whether two fingerprints show the same picture, allowing a few cells to cross one grey level, as a
    /// blinking caret or a shadow does.</summary>
    public static bool SameFingerprint(IReadOnlyList<byte>? lhs, IReadOnlyList<byte>? rhs)
    {
        if (lhs is null || rhs is null || lhs.Count != rhs.Count) return false;
        int moved = 0, nudged = 0;
        for (var i = 0; i < lhs.Count; i++)
        {
            var delta = Math.Abs(lhs[i] - rhs[i]);
            if (delta > 1) moved++;
            if (delta != 0) nudged++;
        }
        return moved == 0 && nudged <= 3;
    }

    private const string ImagePrefix = "image:";

    public static string? Signature(string text, IReadOnlyList<byte>? fingerprint)
    {
        var normalized = Normalized(text);
        if (normalized.Length > 0) return normalized;
        return fingerprint is null ? null : ImagePrefix + string.Join('.', fingerprint);
    }

    /// <summary>Whether two readings say the same: the same text, or pictures that differ no more than a blinking
    /// caret does.</summary>
    public static bool SameSignature(string? lhs, string? rhs)
    {
        if (lhs is null || rhs is null) return false;
        if (lhs == rhs) return true;
        if (!lhs.StartsWith(ImagePrefix, StringComparison.Ordinal) || !rhs.StartsWith(ImagePrefix, StringComparison.Ordinal)) return false;
        static byte[] Cells(string signature) => signature[ImagePrefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? (byte?)b : null)
            .OfType<byte>().ToArray();
        return SameFingerprint(Cells(lhs), Cells(rhs));
    }

    // MARK: Areas

    /// <summary>
    /// The part of a window's image <paramref name="crop"/> covers, <paramref name="crop"/> being in pixels from the
    /// window's top-left corner and <paramref name="windowSize"/> the window's size. Null when the area left the window.
    /// </summary>
    public static RectD? PixelCrop(RectD crop, SizeD windowSize, SizeD imageSize)
    {
        if (windowSize.Width <= 0 || windowSize.Height <= 0) return null;
        var scaleX = imageSize.Width / windowSize.Width;
        var scaleY = imageSize.Height / windowSize.Height;
        // Integral: the smallest whole-pixel rectangle around the scaled one.
        var left = Math.Floor(crop.X * scaleX);
        var top = Math.Floor(crop.Y * scaleY);
        var right = Math.Ceiling((crop.X + crop.Width) * scaleX);
        var bottom = Math.Ceiling((crop.Y + crop.Height) * scaleY);
        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, imageSize.Width);
        bottom = Math.Min(bottom, imageSize.Height);
        var width = right - left;
        var height = bottom - top;
        return width < 4 || height < 4 ? null : new RectD(left, top, width, height);
    }

    /// <summary>
    /// The window under the middle of a dragged area, front to back, and the area in pixels from that window's
    /// top-left corner, kept inside it.
    /// </summary>
    public static (T Id, RectD Crop)? WindowCrop<T>(RectD area, IEnumerable<(T Id, RectD Bounds)> windows)
    {
        var middle = new PointD(area.X + area.Width / 2, area.Y + area.Height / 2);
        foreach (var (id, bounds) in windows)
        {
            if (!bounds.Contains(middle)) continue;
            var left = Math.Max(area.X, bounds.X);
            var top = Math.Max(area.Y, bounds.Y);
            var right = Math.Min(area.Right, bounds.Right);
            var bottom = Math.Min(area.Bottom, bounds.Bottom);
            if (right - left < 8 || bottom - top < 8) return null;
            return (id, new RectD(left - bounds.X, top - bounds.Y, right - left, bottom - top));
        }
        return null;
    }

    // MARK: Patterns

    // Keep the whole percentage and its sign: matching only the last group turns −1 250% into a positive 250%.
    // Thousands spaced apart join only where the region writes them that way, as for any number, so "Step 2 100%"
    // stays 100%. The % sign must share the number's line.
    private static string PercentPattern(bool groupsWithSpace) =>
        "(?:" + (groupsWithSpace ? SpacedNumberPattern + "|" : "") + NumberPattern + ")[" + Gaps + "]?%";
    /// <summary>A space, a no-break space or a narrow no-break space, as regions group thousands.</summary>
    private static readonly string Gaps = new([' ', (char)0xA0, (char)0x202F]);
    private const string ClockPattern = @"\b\d{1,2}:\d{2}(?::\d{2})?\b";
    private const string AmountPattern = @"[-+]?\d[\d.,]*(?:\s?[A-Za-z]{1,3}\b)?";
    private const string Signs = "[-+−]?";
    private const string NumberPattern = Signs + @"\d[\d.,]*\d|" + Signs + @"\d";
    private static readonly string SpacedNumberPattern = Signs + @"\d{1,3}(?:[" + Gaps + @"]\d{3})+(?:[.,]\d+)?";

    private static string? FirstMatch(string pattern, string text)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? TrimSpaces(match.Value) : null;
    }

    /// <summary>Trims spaces and tabs, not line breaks (Swift's CharacterSet.whitespaces).</summary>
    private static string TrimSpaces(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    private static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private static string RemoveSpaces(string text) => new(text.Where(c => !IsSpace(c)).ToArray());

    private static int Length(string text) => new StringInfo(text).LengthInTextElements;

    private static string Prefix(string text, int elements)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= elements ? text : info.SubstringByTextElements(0, elements);
    }
}
