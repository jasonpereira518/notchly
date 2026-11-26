// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickToolsSupport.swift (joinedRecognizedText, joinedBarcodePayloads,
// openableURL) and the outcome rule of ScreenTextService: what a region of the screen turned out to hold, and how its lines and codes
// become one text. Windows differences: positions are normalised with the origin at the TOP left (Windows OCR reports pixels downward,
// where Vision reports upward), so a row is y * 50 where upstream's is (1 - y) * 50.
namespace Notchly.Core.Tools;

/// <summary>One recognised line of screen text and where it starts, as fractions of the area (0 = left / top).</summary>
public readonly record struct RecognizedLine(string Text, double X, double Y);

/// <summary>One decoded 2D code and where it sits, as fractions of the area (0 = left / top).</summary>
public readonly record struct DecodedBarcode(string Payload, double X, double Y);

public enum ScreenTextKind { Empty, Text, Qr }

/// <summary>What a captured region held: a code (it wins), else text, else nothing.</summary>
public readonly record struct ScreenTextOutcome(ScreenTextKind Kind, string Value);

public static class ScreenTextSupport
{
    /// <summary>Rows closer than this many 1/50ths of the height are one visual row.</summary>
    private const double RowTolerance = 0.5, RowsPerHeight = 50;

    private static IEnumerable<T> InReadingOrder<T>(IEnumerable<T> items, Func<T, double> x, Func<T, double> y) =>
        items.OrderBy(i => i, Comparer<T>.Create((a, b) =>
        {
            // Tiny baseline wobbles must not shuffle the words of one line: bucket rows, then order by x.
            var rowA = y(a) * RowsPerHeight;
            var rowB = y(b) * RowsPerHeight;
            return Math.Abs(rowA - rowB) >= RowTolerance ? rowA.CompareTo(rowB) : x(a).CompareTo(x(b));
        }));

    /// <summary>
    /// Joins recognised lines in natural reading order: top to bottom, left to right within a visual row. Blank lines are dropped.
    /// With <paramref name="removingLineBreaks"/> the lines are trimmed and joined with a space, except at a CJK seam.
    /// </summary>
    public static string JoinRecognizedText(IEnumerable<RecognizedLine> lines, bool removingLineBreaks)
    {
        var texts = InReadingOrder(lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList(), l => l.X, l => l.Y).Select(l => l.Text).ToList();
        if (!removingLineBreaks) return string.Join("\n", texts);

        var joined = "";
        foreach (var line in texts.Select(t => t.Trim()))
        {
            if (joined.Length == 0) { joined = line; continue; }
            var tight = line.Length > 0 && IsTightScript(LastScalar(joined)) && IsTightScript(FirstScalar(line));
            joined += tight ? line : " " + line;
        }
        return joined;
    }

    private static int FirstScalar(string text) => char.ConvertToUtf32(text, 0);

    private static int LastScalar(string text) =>
        char.IsLowSurrogate(text[^1]) && text.Length > 1 ? char.ConvertToUtf32(text, text.Length - 2) : text[^1];

    // CJK punctuation, kana, Han. Hangul syllables and halfwidth Hangul jamo stay out on purpose because Korean keeps its word spaces.
    private static bool IsTightScript(int scalar) =>
        scalar is >= 0x3000 and <= 0x312F or >= 0x3190 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFF01 and <= 0xFF9F or >= 0x20000 and <= 0x2FA1F;

    /// <summary>Joins decoded codes in reading order, one per line, blank payloads dropped.</summary>
    public static string JoinBarcodePayloads(IEnumerable<DecodedBarcode> codes) =>
        string.Join("\n", InReadingOrder(codes.Where(c => !string.IsNullOrWhiteSpace(c.Payload)).ToList(), c => c.X, c => c.Y).Select(c => c.Payload));

    /// <summary>
    /// The payload as a web link for the optional open action. Limited to http and https on purpose: a scanned code must never be able
    /// to launch an arbitrary URL scheme (mailto, tel, custom app schemes and so on).
    /// </summary>
    public static Uri? OpenableUrl(string? payload)
    {
        var trimmed = payload?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        return (scheme == "http" || scheme == "https") && uri.Host.Length > 0 ? uri : null;
    }

    /// <summary>A code wins over text, and a blank result of either is nothing (upstream <c>ScreenTextService.outcome</c>).</summary>
    public static ScreenTextOutcome Outcome(string? qrPayloads, string? text)
    {
        if (!string.IsNullOrWhiteSpace(qrPayloads)) return new ScreenTextOutcome(ScreenTextKind.Qr, qrPayloads!);
        return !string.IsNullOrWhiteSpace(text) ? new ScreenTextOutcome(ScreenTextKind.Text, text!) : new ScreenTextOutcome(ScreenTextKind.Empty, "");
    }

    /// <summary>
    /// Whether a recognition result may still be written: only the newest request, still switched on, and the clipboard unchanged
    /// since the request began (a newer copy by the user is never overwritten).
    /// </summary>
    public static bool MayWrite(int generation, int current, bool enabled, long clipboardAtStart, long clipboardNow) =>
        enabled && generation == current && clipboardAtStart == clipboardNow;
}
