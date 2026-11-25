// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Finder/FinderPasteImageSupport.swift: pasting an image into a folder makes a PNG there.
// Windows differences: clipboard formats are Windows names, and the folder is a File Explorer window's.
using System.Globalization;

namespace Notchly.Core.Clipboard;

public static class PasteImageSupport
{
    public const long MaxBytes = 64L * 1024 * 1024;

    public const string Png = "PNG", Dib = "DeviceIndependentBitmap", DibV5 = "Format17", Bitmap = "Bitmap";
    private static readonly string[] FileFormats = ["FileDrop", "FileNameW", "FileName", "Shell IDList Array"];
    private static readonly string[] Preference = [Png, DibV5, Dib, Bitmap];

    /// <summary>The image format to read: none when files are on the clipboard (Explorer pastes those itself), else PNG, then a bitmap.</summary>
    public static string? PreferredFormat(IEnumerable<string> formats)
    {
        var list = formats.ToList();
        if (list.Any(f => FileFormats.Contains(f, StringComparer.OrdinalIgnoreCase))) return null;
        return Preference.FirstOrDefault(p => list.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The name of a pasted image: <c>Pasted_Image_20260125_103015.png</c>.</summary>
    public static string FileName(DateTime when) => "Pasted_Image_" + when.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";

    /// <summary>The name, or <c>name 2.png</c>, <c>name 3.png</c> and so on when the folder already has it.</summary>
    public static string UniqueName(string fileName, Func<string, bool> exists)
    {
        if (!exists(fileName)) return fileName;
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem} {n}{extension}";
            if (!exists(candidate)) return candidate;
        }
        return $"{stem} {Guid.NewGuid():N}{extension}";
    }

    public static bool WithinCap(long bytes) => bytes is > 0 and <= MaxBytes;

    /// <summary>Folders that are not real places on disk (This PC, Home, the Recycle Bin) get an ordinary paste.</summary>
    public static bool IsWritableFolderPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) && !path.StartsWith("::", StringComparison.Ordinal)
        && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
}
