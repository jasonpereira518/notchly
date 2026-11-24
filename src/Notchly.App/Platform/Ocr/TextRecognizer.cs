// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the text recognition upstream Services/Notch/NotchWatchService.swift asks of Vision through
// ScreenTextService. Windows' own engine (Windows.Media.Ocr) reads the area on this PC, in the person's language
// when Windows has it installed and in English otherwise; nothing leaves the PC.
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Notchly.App.Platform.Capture;
using Notchly.Core.Notch;
using Serilog;

namespace Notchly.App.Platform.Ocr;

public static class TextRecognizer
{
    private static readonly Lazy<OcrEngine?> Engine = new(CreateEngine);

    /// <summary>Whether Windows has a text recognition language to read with.</summary>
    public static bool IsAvailable => Engine.Value is not null;

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null && OcrEngine.IsLanguageSupported(new Language("en-US"))) engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            Log.Information("Watch: text recognition in {Language}", engine?.RecognizerLanguage?.LanguageTag ?? "none");
            return engine;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Watch: text recognition unavailable");
            return null;
        }
    }

    /// <summary>
    /// The size recognition reads an area at: at most 900 pixels on its longest side, as upstream, and twice as
    /// large when it is small (Windows-specific: its engine misses text only a few pixels tall).
    /// </summary>
    public static double RecognitionScale(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= 0) return 1;
        if (longest > NotchWatchSupport.MaximumRecognitionSide) return (double)NotchWatchSupport.MaximumRecognitionSide / longest;
        return longest * 2 <= NotchWatchSupport.MaximumRecognitionSide ? 2 : 1;
    }

    /// <summary>The area's text, one recognised line per line, top to bottom; empty when it holds none.</summary>
    public static async Task<string> ReadAsync(CapturedImage area)
    {
        if (Engine.Value is not { } engine) return "";
        var source = area.Scaled(RecognitionScale(area.Width, area.Height));
        if (source.Width > OcrEngine.MaxImageDimension || source.Height > OcrEngine.MaxImageDimension) return "";
        var packed = source.Stride == source.Width * 4 && source.Offset == 0 ? source : source.Crop(new RectD(0, 0, source.Width, source.Height));
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(packed.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                                                               packed.Width, packed.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
    }
}
