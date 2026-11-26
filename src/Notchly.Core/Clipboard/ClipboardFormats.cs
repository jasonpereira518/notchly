// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's pasteboard type checks (ConcealedType, TransientType, file and image types): which clipboard formats
// mean "do not record this" and which mean "this is not just text". Names are the registered clipboard format names.
using System.Text;

namespace Notchly.Core.Clipboard;

public static class ClipboardFormats
{
    /// <summary>Set by password managers and the like: tools that watch the clipboard must leave this content alone.</summary>
    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";
    public const string ViewerIgnore = "Clipboard Viewer Ignore";
    public const string Html = "HTML Format";
    public const string Rtf = "Rich Text Format";
    public const string UnicodeText = "UnicodeText";

    private static readonly string[] FileFormats = ["FileDrop", "FileNameW", "FileName", "Shell IDList Array", "FileGroupDescriptorW", "FileContents"];
    private static readonly string[] ImageFormats = ["PNG", "Format17", "DeviceIndependentBitmap", "Bitmap", "image/png", "GIF", "JFIF"];
    private static readonly string[] TextFormats = ["UnicodeText", "Text", "OEMText", "System.String", "Locale", "UniformResourceLocatorW", "UniformResourceLocator", "HTML Format"];

    public static bool HasFiles(IEnumerable<string> formats) => formats.Any(f => FileFormats.Contains(f, StringComparer.OrdinalIgnoreCase));
    public static bool HasImage(IEnumerable<string> formats) => formats.Any(f => ImageFormats.Contains(f, StringComparer.OrdinalIgnoreCase));
    public static bool HasText(IEnumerable<string> formats) => formats.Any(f => f is "UnicodeText" or "Text" or "OEMText" or "System.String");

    /// <summary>
    /// Whether the content asks not to be recorded: it carries the monitoring exclusion or the viewer-ignore format, or its history or
    /// cloud flag is zero.
    /// </summary>
    public static bool IsExcluded(IEnumerable<string> formats, int? canIncludeInHistory = null, int? canUploadToCloud = null)
    {
        if (canIncludeInHistory == 0 || canUploadToCloud == 0) return true;
        return formats.Any(f => f.Equals(ExcludeFromMonitoring, StringComparison.OrdinalIgnoreCase) || f.Equals(ViewerIgnore, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a clipboard holds only what a copied link holds (text, a link, HTML), so the URL cleaner may rewrite it: nothing excluded,
    /// no files, no image, and some text.
    /// </summary>
    public static bool IsPlainLinkContent(IEnumerable<string> formats, int? canIncludeInHistory = null, int? canUploadToCloud = null)
    {
        var list = formats.ToList();
        if (IsExcluded(list, canIncludeInHistory, canUploadToCloud) || HasFiles(list) || HasImage(list)) return false;
        return HasText(list) && list.All(f => TextFormats.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The HTML inside a CF_HTML block: the bytes between its StartFragment and EndFragment offsets (UTF-8), or null if the header is unreadable.</summary>
    public static string? HtmlFragment(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        var text = Encoding.UTF8.GetString(data);
        int? Offset(string key)
        {
            var at = text.IndexOf(key + ":", StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + key.Length + 1;
            var end = start;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            return int.TryParse(text[start..end], out var value) ? value : null;
        }
        if (Offset("StartFragment") is not { } from || Offset("EndFragment") is not { } to || from < 0 || to > data.Length || to < from) return null;
        return Encoding.UTF8.GetString(data, from, to - from);
    }
}
