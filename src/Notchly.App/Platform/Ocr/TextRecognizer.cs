// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the text recognition upstream Services/Notch/NotchWatchService.swift asks of Vision through
// ScreenTextService. Windows' own engine (Windows.Media.Ocr) reads the area on this PC, in the person's language
// when Windows has it installed and in English otherwise; nothing leaves the PC.
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Notchly.App.Platform.Capture;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Notchly.Core.Tools;
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
        var result = await RecognizeAsync(area);
        return result is null ? "" : string.Join("\n", result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
    }

    /// <summary>
    /// The area's lines with where each starts, as fractions of the area (0 = left / top), so they can be joined in reading order
    /// (Notchly.Core.Tools.ScreenTextSupport). Empty when the area holds no text.
    /// </summary>
    public static async Task<IReadOnlyList<RecognizedLine>> ReadLinesAsync(CapturedImage area)
    {
        var result = await RecognizeAsync(area);
        if (result is null) return [];
        var lines = new List<RecognizedLine>();
        foreach (var line in result.Lines)
        {
            var first = line.Words.FirstOrDefault();
            if (first is null || string.IsNullOrWhiteSpace(line.Text)) continue;
            var scale = RecognitionScale(area.Width, area.Height);
            var width = Math.Max(1, Math.Round(area.Width * scale));
            var height = Math.Max(1, Math.Round(area.Height * scale));
            lines.Add(new RecognizedLine(line.Text.Trim(), first.BoundingRect.X / width, first.BoundingRect.Y / height));
        }
        return lines;
    }

    private static async Task<OcrResult?> RecognizeAsync(CapturedImage area)
    {
        if (Engine.Value is not { } engine) return null;
        var source = area.Scaled(RecognitionScale(area.Width, area.Height));
        if (source.Width > OcrEngine.MaxImageDimension || source.Height > OcrEngine.MaxImageDimension) return null;
        var packed = source.Stride == source.Width * 4 && source.Offset == 0 ? source : source.Crop(new RectD(0, 0, source.Width, source.Height));
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(packed.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                                                               packed.Width, packed.Height, BitmapAlphaMode.Premultiplied);
        return await engine.RecognizeAsync(bitmap);
    }

    /// <summary>
    /// The words of one band of a capture with their boxes in the capture's pixels (the band starts at <paramref name="offsetX"/>,
    /// <paramref name="offsetY"/>), grouped by line, for the editor's selectable text and text-only blur. Null when the engine cannot read it.
    /// </summary>
    public static async Task<IReadOnlyList<BandWord>?> ReadWordsAsync(CapturedImage band, double offsetX, double offsetY)
    {
        var result = await RecognizeAsync(band);
        if (result is null) return Engine.Value is null ? null : [];
        var scale = RecognitionScale(band.Width, band.Height);
        var words = new List<BandWord>();
        var lineIndex = 0;
        foreach (var line in result.Lines)
        {
            var rects = line.Words.Select(w => new RectD(offsetX + w.BoundingRect.X / scale, offsetY + w.BoundingRect.Y / scale,
                                                         w.BoundingRect.Width / scale, w.BoundingRect.Height / scale)).ToList();
            if (rects.Count == 0) continue;
            var left = rects.Min(r => r.X);
            var top = rects.Min(r => r.Y);
            var box = new RectD(left, top, rects.Max(r => r.Right) - left, rects.Max(r => r.Bottom) - top);
            for (var i = 0; i < rects.Count; i++) words.Add(new BandWord(line.Words[i].Text, rects[i], box, lineIndex));
            lineIndex++;
        }
        return words;
    }
}
