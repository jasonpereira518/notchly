// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the style value types in upstream Services/QuickTools/ScreenshotSupport.swift ("Annotation model"):
// ColorID, StrokeID, text sizes, spotlight dim, BlurStrength, ArrowStyleID, BlurStyleID and StickerID.
namespace Notchly.Core.Capture;

/// <summary>Upstream raw values (lower camel case) for the editor's enums, parsed strictly so "5" never becomes a case.</summary>
public static class EnumRaw
{
    public static string Of<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static T? Parse<T>(string? raw) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var value in Enum.GetValues<T>())
        {
            if (Of(value) == raw) return value;
        }
        return null;
    }
}

public enum AnnotationColor { Red, Orange, Yellow, Green, Blue, Purple, Black, White }

public static class AnnotationColors
{
    public static string Raw(this AnnotationColor color) => EnumRaw.Of(color);

    /// <summary>sRGB components, shared by live rendering and export.</summary>
    public static (double Red, double Green, double Blue) Components(this AnnotationColor color) => color switch
    {
        AnnotationColor.Red => (0.93, 0.26, 0.21),
        AnnotationColor.Orange => (1.00, 0.58, 0.00),
        AnnotationColor.Yellow => (1.00, 0.80, 0.00),
        AnnotationColor.Green => (0.20, 0.78, 0.35),
        AnnotationColor.Blue => (0.04, 0.52, 1.00),
        AnnotationColor.Purple => (0.69, 0.32, 0.87),
        AnnotationColor.Black => (0.09, 0.09, 0.11),
        _ => (1.00, 1.00, 1.00),
    };

    public static AnnotationColor Sanitized(string? raw) => EnumRaw.Parse<AnnotationColor>(raw) ?? AnnotationColor.Red;
}

public enum StrokeWidth { Small, Medium, Large }

public static class StrokeWidths
{
    public static string Raw(this StrokeWidth stroke) => EnumRaw.Of(stroke);

    /// <summary>Line width in image points at 1x; export multiplies by the image scale.</summary>
    public static double Width(this StrokeWidth stroke) => stroke switch
    {
        StrokeWidth.Small => 2,
        StrokeWidth.Medium => 4,
        _ => 7,
    };

    public static StrokeWidth Sanitized(string? raw) => EnumRaw.Parse<StrokeWidth>(raw) ?? StrokeWidth.Medium;
}

public enum ArrowStyle { Filled, Outline, Open, DoubleEnded, Scribbly }

public static class ArrowStyles
{
    public static string Raw(this ArrowStyle style) => EnumRaw.Of(style);
    public static ArrowStyle Sanitized(string? raw) => EnumRaw.Parse<ArrowStyle>(raw) ?? ArrowStyle.Filled;
}

/// <summary>What a blur area does to what is under it. Pixelate was the only style before, so it stays the default.</summary>
public enum BlurStyle { Pixelate, Blur, Erase }

public static class BlurStyles
{
    public static string Raw(this BlurStyle style) => EnumRaw.Of(style);
    public static BlurStyle Sanitized(string? raw) => EnumRaw.Parse<BlurStyle>(raw) ?? BlurStyle.Pixelate;

    /// <summary>Erasing paints the surroundings over the area, so it has no strength.</summary>
    public static bool UsesStrength(this BlurStyle style) => style != BlurStyle.Erase;
}

public enum StickerKind { Check, Cross, Star, Heart, ThumbsUp, ThumbsDown, Smile, Laugh, Party, Fire, Warning, Eyes }

public static class Stickers
{
    public static string Raw(this StickerKind sticker) => EnumRaw.Of(sticker);

    public static string Glyph(this StickerKind sticker) => sticker switch
    {
        StickerKind.Check => "✅",
        StickerKind.Cross => "❌",
        StickerKind.Star => "⭐️",
        StickerKind.Heart => "❤️",
        StickerKind.ThumbsUp => "\U0001F44D",
        StickerKind.ThumbsDown => "\U0001F44E",
        StickerKind.Smile => "\U0001F600",
        StickerKind.Laugh => "\U0001F602",
        StickerKind.Party => "\U0001F389",
        StickerKind.Fire => "\U0001F525",
        StickerKind.Warning => "⚠️",
        _ => "\U0001F440",
    };

    public static StickerKind Sanitized(string? raw) => EnumRaw.Parse<StickerKind>(raw) ?? StickerKind.Check;
}

/// <summary>
/// How hard a blur hides what is under it, from 1 (lightest) to 5 (heaviest). Level 3 is the strength blurs always had.
/// The screenshot pixelate tool and video blurs share the scale so a level means the same thing in both editors.
/// </summary>
public static class BlurStrength
{
    public const int MinLevel = 1, MaxLevel = 5, DefaultLevel = 3;

    public static int Sanitized(int level) => Math.Min(Math.Max(level, MinLevel), MaxLevel);

    /// <summary>
    /// Where a new capture's pixelate tool starts: the remembered level, but never a light one. Levels 1 and 2 make
    /// blocks smaller than a line of text, which can stay readable, so they are picked area by area instead.
    /// </summary>
    public static int StartingLevel(int remembered) => Math.Max(Sanitized(remembered), DefaultLevel);

    /// <summary>What the level does to the mosaic block, relative to level 3.</summary>
    public static double BlockFactor(int level) => Sanitized(level) switch
    {
        1 => 0.4,
        2 => 0.65,
        4 => 1.5,
        5 => 2.2,
        _ => 1,
    };
}

/// <summary>Text point sizes, the spotlight dim and the other numeric style helpers of upstream ScreenshotSupport.</summary>
public static class AnnotationSizes
{
    /// <summary>Text point sizes at 1x, offered as presets; text has its own size instead of borrowing the shape thickness.</summary>
    public static readonly IReadOnlyList<int> TextSizes = [10, 12, 14, 16, 19, 24, 28, 36, 48, 64, 72, 96];

    public const int DefaultTextSize = 19;

    public static int SanitizedTextSize(int size) =>
        size == 0 ? DefaultTextSize : Math.Min(Math.Max(size, TextSizes[0]), TextSizes[^1]);

    /// <summary>The preset one step away from <paramref name="size"/>, or null at either end.</summary>
    public static int? SteppedTextSize(int size, bool up)
    {
        if (up)
        {
            foreach (var preset in TextSizes) if (preset > size) return preset;
            return null;
        }
        for (int i = TextSizes.Count - 1; i >= 0; i--) if (TextSizes[i] < size) return TextSizes[i];
        return null;
    }

    /// <summary>How dark a spotlight turns everything outside its regions, from 0 (untouched) to 1 (black).</summary>
    public const double DefaultSpotlightDim = 0.5;

    /// <summary>A stored or passed dim kept within 0...1; an unreadable one falls back to the default.</summary>
    public static double SanitizedSpotlightDim(double strength) =>
        double.IsFinite(strength) ? Math.Min(Math.Max(strength, 0), 1) : DefaultSpotlightDim;

    /// <summary>The dim the editor's control settles on: the nearest 5% step, so a continuous slider needs no tick marks.</summary>
    public static double SteppedSpotlightDim(double strength) =>
        Math.Round(SanitizedSpotlightDim(strength) * 20, MidpointRounding.AwayFromZero) / 20;

    /// <summary>
    /// Pixelation block size in image pixels, scaled to the capture so small crops and full screens redact equally well.
    /// From the default level up the mosaic carries no legible detail; levels 1 and 2 are lighter.
    /// </summary>
    public static int PixelBlockSize(Notch.SizeD imageSize, int level = BlurStrength.DefaultLevel)
    {
        int baseSize = Math.Max(10, (int)(Math.Min(imageSize.Width, imageSize.Height) / 55));
        return Math.Max(2, (int)RectMath.Rounded(baseSize * BlurStrength.BlockFactor(level)));
    }
}
