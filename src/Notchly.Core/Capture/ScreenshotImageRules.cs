// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure parts of upstream Services/QuickTools/ScreenshotImageFile.swift (format choice, transparency),
// ScreenshotImageImport.swift (size limit, orientation, scale), ScreenshotSupport.swift (captureScale(fromDPI:),
// clipboardImageScale, downscaledSize, copied-file prune policy) and RecentCaptureService (thumbnail size).
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>
/// The encodings the editor writes. Upstream also offers HEIC and PDF sources; Windows has no HEIC writer in the box, so both
/// of those come out as PNG, the same fallback upstream takes when a format is unsupported.
/// </summary>
public enum CaptureImageFormat { Png, Jpeg }

public static class ScreenshotImageRules
{
    // MARK: - Format choice

    /// <summary>A stored or picked format name; "jpeg"/"jpg" is JPEG and everything else (png, heic, pdf, unknown) is PNG.</summary>
    public static CaptureImageFormat PreferredFormat(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => CaptureImageFormat.Jpeg,
        _ => CaptureImageFormat.Png,
    };

    /// <summary>
    /// JPEG only when it was asked for and the picture has no transparency, since JPEG would flatten it; otherwise lossless PNG.
    /// </summary>
    public static CaptureImageFormat ChooseFormat(CaptureImageFormat preferred, bool hasTransparency) =>
        preferred == CaptureImageFormat.Jpeg && !hasTransparency ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;

    public static string FileExtension(CaptureImageFormat format) => format == CaptureImageFormat.Jpeg ? "jpg" : "png";

    public static string MimeType(CaptureImageFormat format) => format == CaptureImageFormat.Jpeg ? "image/jpeg" : "image/png";

    /// <summary>
    /// Whether any pixel is not fully opaque, so "even one transparent interior pixel switches JPEG output to PNG".
    /// <paramref name="bgra"/> is 8 bit BGRA, top row first; alpha is the fourth byte of each pixel.
    /// A format without an alpha channel (Bgr32) is opaque by definition.
    /// </summary>
    public static bool HasTransparency(byte[] bgra, int width, int height, int stride, bool hasAlphaChannel = true)
    {
        if (!hasAlphaChannel) return false;
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (height - 1) * stride + width * 4) return true;
        for (var row = 0; row < height; row++)
        {
            var offset = row * stride + 3;
            for (var column = 0; column < width; column++, offset += 4)
                if (bgra[offset] != 255) return true;
        }
        return false;
    }

    // MARK: - Density and scale

    /// <summary>Pixels per inch a file says for a capture at <paramref name="scale"/> pixels per point (scale x 72).</summary>
    public static double Dpi(double scale) => scale * 72;

    /// <summary>Restores the pixels-per-point scale stored as standard PNG DPI; null for missing, broken or implausible values.</summary>
    public static double? CaptureScale(double? dpi)
    {
        if (dpi is not { } value || !double.IsFinite(value)) return null;
        var scale = value / 72;
        return scale is >= 0.5 and <= 4 ? scale : null;
    }

    /// <summary>
    /// Uses the logical size carried by a copied image when it describes one consistent display scale. Imported files without
    /// that metadata edit at 1x.
    /// </summary>
    public static double ClipboardImageScale(SizeD pixelSize, SizeD pointSize)
    {
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0 || pointSize.Width <= 0 || pointSize.Height <= 0) return 1;
        var horizontal = pixelSize.Width / pointSize.Width;
        var vertical = pixelSize.Height / pointSize.Height;
        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical)
            || Math.Abs(horizontal - vertical) > Math.Max(horizontal, vertical) * 0.05) return 1;
        return CaptureScale((horizontal + vertical) / 2 * 72) ?? 1;
    }

    /// <summary>The size a picture takes on screen: pixels over the scale, in both axes.</summary>
    public static SizeD PointSize(SizeD pixelSize, double scale) =>
        scale > 0 && double.IsFinite(scale) ? new SizeD(pixelSize.Width / scale, pixelSize.Height / scale) : pixelSize;

    /// <summary>Pixel size after the optional 1x downscale of a Retina capture.</summary>
    public static SizeD DownscaledSize(SizeD pixelSize, double scale) =>
        scale > 1
            ? new SizeD(Math.Round(pixelSize.Width / scale, MidpointRounding.AwayFromZero),
                        Math.Round(pixelSize.Height / scale, MidpointRounding.AwayFromZero))
            : pixelSize;

    // MARK: - Thumbnails

    public const double ThumbnailMaximum = 360;

    /// <summary>The recent-captures thumbnail: the long side capped at 360 pixels, never enlarged, never below one pixel.</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height)
    {
        var longest = Math.Max(width, height);
        var factor = Math.Min(1.0, ThumbnailMaximum / Math.Max(1, longest));
        return (Math.Max(1, (int)Math.Round(width * factor, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(height * factor, MidpointRounding.AwayFromZero)));
    }

    // MARK: - Copied files

    public const int CopiedFileMaximumCount = 100;
    public const long CopiedFileMaximumBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan CopiedFileLifetime = TimeSpan.FromHours(24);

    /// <summary>Whether a copied capture of this size may be written at all.</summary>
    public static bool CopiedFileFits(long bytes) => bytes <= CopiedFileMaximumBytes;

    /// <summary>One copied capture in the cache folder.</summary>
    public readonly record struct CopiedFileCandidate(string Path, DateTime Date, long Bytes);

    private static string Standardized(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    private static bool SamePath(string a, string b) => string.Equals(Standardized(a), Standardized(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files to delete so the cache stays inside its count and size budget. The newest files are kept first, and
    /// <paramref name="current"/> is always kept (and counted) so a late stale render can never evict the published file.
    /// Nothing is pruned when <paramref name="current"/> is not among the files.
    /// </summary>
    public static IReadOnlyList<string> CopiedFilePruneVictims(IEnumerable<CopiedFileCandidate> files, string current,
        int maximumCount = CopiedFileMaximumCount, long maximumBytes = CopiedFileMaximumBytes)
    {
        var list = files.ToList();
        if (!list.Any(file => SamePath(file.Path, current))) return [];
        var currentFile = list.First(file => SamePath(file.Path, current));
        var count = 1;
        var bytes = Math.Max(0, currentFile.Bytes);
        var victims = new List<string>();
        var ordered = list.OrderByDescending(file => file.Date)
            .ThenBy(file => Standardized(file.Path), StringComparer.Ordinal);
        foreach (var file in ordered.Where(file => !SamePath(file.Path, current)))
        {
            if (count < maximumCount && file.Bytes >= 0 && bytes <= maximumBytes && file.Bytes <= maximumBytes - bytes)
            {
                count++;
                bytes += file.Bytes;
            }
            else victims.Add(file.Path);
        }
        return victims;
    }

    /// <summary>
    /// Everything a prune pass removes: files older than a day (never <paramref name="current"/>), then whatever the budget
    /// pushes out of the rest.
    /// </summary>
    public static IReadOnlyList<string> CopiedFilePrunePlan(IEnumerable<CopiedFileCandidate> files, string current, DateTime now)
    {
        var all = files.ToList();
        var cutoff = now - CopiedFileLifetime;
        var expired = all.Where(file => !SamePath(file.Path, current) && file.Date < cutoff).ToList();
        var remaining = all.Except(expired).ToList();
        return [.. expired.Select(file => file.Path), .. CopiedFilePruneVictims(remaining, current)];
    }

    // MARK: - Import

    /// <summary>Pictures larger than this on their long side ask before opening, since decoding them allocates a huge bitmap.</summary>
    public const int ImportMaximumDimension = 6000;

    public static bool ImportRequiresSizeChoice(double pixelWidth, double pixelHeight) =>
        Math.Max(pixelWidth, pixelHeight) > ImportMaximumDimension;

    /// <summary>
    /// A picture's size reduced so its long side fits <paramref name="maxDimension"/>; smaller pictures keep their size.
    /// Each side rounds to whole pixels and never drops below one (upstream <c>MediaImageResizeMode.targetSize</c>).
    /// </summary>
    public static SizeD ReducedSize(SizeD source, int maxDimension = ImportMaximumDimension)
    {
        var width = Math.Max(1, Math.Abs(source.Width));
        var height = Math.Max(1, Math.Abs(source.Height));
        var side = (double)Math.Max(1, maxDimension);
        var factor = Math.Min(1, side / Math.Max(width, height));
        return new SizeD(Math.Max(1, Math.Round(width * factor, MidpointRounding.AwayFromZero)),
                         Math.Max(1, Math.Round(height * factor, MidpointRounding.AwayFromZero)));
    }

    /// <summary>The long side an oversized import is decoded at.</summary>
    public static int ImportReducedMaximumDimension(SizeD pixelSize)
    {
        var size = ReducedSize(pixelSize);
        return (int)Math.Max(size.Width, size.Height);
    }

    /// <summary>EXIF orientations 5 to 8 turn the picture a quarter, so its width and height swap.</summary>
    public static SizeD OrientedPixelSize(double width, double height, int orientation) =>
        orientation is >= 5 and <= 8 ? new SizeD(height, width) : new SizeD(width, height);

    /// <summary>The editing scale of an imported file: from its DPI when both axes agree, otherwise 1x. A missing DPI reads as 72.</summary>
    public static double ImportScale(double pixelWidth, double pixelHeight, double? dpiX, double? dpiY)
    {
        var x = dpiX is > 0 ? dpiX.Value : 72;
        var y = dpiY is > 0 ? dpiY.Value : 72;
        return ClipboardImageScale(new SizeD(pixelWidth, pixelHeight),
                                   new SizeD(pixelWidth * 72 / x, pixelHeight * 72 / y));
    }
}
