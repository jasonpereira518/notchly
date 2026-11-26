// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the picture plumbing of upstream's ScreenshotImageFile / ScreenshotImageImport, over WPF's WIC encoders and decoders:
// CapturedImage (top-down BGRA) to and from bitmaps, PNG and JPEG bytes with a density, thumbnails.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchly.Core.Capture;

namespace Notchly.App.Platform.Capture;

public static class ImageCodec
{
    public static BitmapSource ToBitmap(CapturedImage image, double dpi = 96)
    {
        var pixels = image.Pixels;
        if (image.Offset != 0)
        {
            pixels = new byte[image.Height * image.Stride];
            Buffer.BlockCopy(image.Pixels, image.Offset, pixels, 0, pixels.Length);
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, dpi, dpi, PixelFormats.Bgra32, null, pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static CapturedImage FromBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new CapturedImage(pixels, converted.PixelWidth, converted.PixelHeight, stride);
    }

    public static byte[] EncodePng(CapturedImage image, double scale = 1) => Encode(new PngBitmapEncoder(), ToBitmap(image, ScreenshotImageRules.Dpi(scale) * 96 / 72));

    public static byte[] EncodeJpeg(CapturedImage image, double scale = 1, int quality = 92)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        // JPEG has no alpha: the picture goes over white first so a transparent corner is not black.
        var bitmap = ToBitmap(image, ScreenshotImageRules.Dpi(scale) * 96 / 72);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }
        var target = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32);
        target.Render(visual);
        return Encode(encoder, target);
    }

    /// <summary>The bytes for the format the rules chose for this picture (JPEG only when it has no transparency).</summary>
    public static (byte[] Bytes, string Extension) EncodeFor(CapturedImage image, string preferredFormat, double scale)
    {
        var transparent = ScreenshotImageRules.HasTransparency(image.Pixels, image.Width, image.Height, image.Stride);
        var format = ScreenshotImageRules.ChooseFormat(ScreenshotImageRules.PreferredFormat(preferredFormat), transparent);
        return format == CaptureImageFormat.Jpeg ? (EncodeJpeg(image, scale), "jpg") : (EncodePng(image, scale), "png");
    }

    public static CapturedImage? Decode(byte[] bytes)
    {
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return FromBitmap(frame);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    public static CapturedImage? DecodeFile(string path)
    {
        try { return Decode(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A small copy for lists (upstream's thumbnail size), bilinear.</summary>
    public static CapturedImage Thumbnail(CapturedImage image)
    {
        var (width, height) = ScreenshotImageRules.ThumbnailSize(image.Width, image.Height);
        if (width >= image.Width) return image;
        return image.Scaled((double)width / image.Width);
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
