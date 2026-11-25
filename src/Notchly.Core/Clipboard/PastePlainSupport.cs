// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/PastePlainService.swift (plainText(from:)) and QuickToolsSupport.swift
// (isMediaTextFallback): what a plain paste pastes. Windows differences: no "Paste and Match Style" menu step, and a clipboard
// either holds files, holds media, or holds text in up to three flavours (plain, rich text, HTML).
using System.Text.RegularExpressions;

namespace Notchly.Core.Clipboard;

/// <summary>What the plain paste does: paste the text given, or let an ordinary Ctrl+V through.</summary>
public readonly record struct PlainPastePlan(string? Text)
{
    public bool Forward => Text is null;
    public static PlainPastePlan ForwardPaste => new(null);
    public static PlainPastePlan Paste(string text) => new(text);
}

public static partial class PastePlainSupport
{
    private static readonly string[] MediaExtensions =
    [
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "svg", "ico", "raw", "avif",
        "mp4", "mov", "m4v", "avi", "mkv", "webm", "mp3", "m4a", "wav", "aac", "flac", "ogg", "pdf",
    ];

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex SchemeRegex();

    /// <summary>
    /// Whether a text on a clipboard with media is only the stand-in an app writes next to it (a file name, a link, a data URL), and not
    /// text the user copied: empty text is; text with a space in it is not; data and blob URLs and anything with a scheme are; and a
    /// bare name ending in a media extension is.
    /// </summary>
    public static bool IsMediaTextFallback(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return true;
        if (trimmed.Any(char.IsWhiteSpace)) return false;
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (SchemeRegex().IsMatch(trimmed)) return true;
        var dot = trimmed.LastIndexOf('.');
        if (dot <= 0 || dot == trimmed.Length - 1 || trimmed.IndexOfAny(['/', '\\']) >= 0) return false;
        return MediaExtensions.Contains(trimmed[(dot + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files or a promise: the original paste goes through. Media: the plain text is used only when it is not just the media's stand-in
    /// (or when rich text accompanies it). Otherwise plain text, else the text of the rich text, else the text of the HTML.
    /// </summary>
    public static PlainPastePlan Plan(bool hasFiles, bool hasMedia, string? plain, bool hasRichText, string? richTextText, string? htmlText)
    {
        if (hasFiles) return PlainPastePlan.ForwardPaste;
        if (hasMedia)
            return !string.IsNullOrWhiteSpace(plain) && (!IsMediaTextFallback(plain) || hasRichText) ? PlainPastePlan.Paste(plain!) : PlainPastePlan.ForwardPaste;
        foreach (var candidate in new[] { plain, richTextText, htmlText })
            if (!string.IsNullOrEmpty(candidate)) return PlainPastePlan.Paste(candidate);
        return PlainPastePlan.ForwardPaste;
    }

    // MARK: Text from HTML

    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();
    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEndRegex();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex AnyTagRegex();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
    [GeneratedRegex(@"\t+(?=\n|$)")]
    private static partial Regex TrailingTabsRegex();

    /// <summary>The text of a piece of HTML: tags gone, line ends where blocks and breaks were, a tab between table cells, entities decoded.</summary>
    public static string TextOfHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = ScriptRegex().Replace(html, "");
        text = BreakRegex().Replace(text, "\n");
        text = BlockEndRegex().Replace(text, "\n");
        text = CellEndRegex().Replace(text, "\t");
        text = AnyTagRegex().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Replace('\u00A0', ' ');
        return BlankLinesRegex().Replace(TrailingTabsRegex().Replace(text, ""), "\n\n").Trim();
    }

    // MARK: The transient write

    public const int ModifierPolls = 100, ModifierPollMs = 15;
    public const int SettleMs = 60;
    public const int RestoreAfterMs = 500;

    /// <summary>The clipboard is put back only when nobody has changed it since the plain text was written.</summary>
    public static bool ShouldRestore(long writtenCount, long currentCount) => writtenCount == currentCount;
}
