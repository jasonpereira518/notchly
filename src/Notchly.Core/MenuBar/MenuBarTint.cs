// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only (no upstream counterpart: the Mac's menu bar belongs to the system). The menu bar
// takes the colour along the top of the app in front, its text stays readable on it, and the
// island keeps an edge wherever it would otherwise melt into what is around it.
namespace Notchly.Core.MenuBar;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
}

public static class MenuBarTint
{
    /// <summary>Text on a light bar: the near-black macOS uses for menu titles.</summary>
    public static readonly Rgb DarkText = new(0x1D, 0x1D, 0x1F);

    /// <summary>Fewer sampled pixels than this give no colour.</summary>
    public const int MinimumPixels = 16;

    /// <summary>
    /// The colour most of a row of BGRA pixels share: pixels fall into buckets of close colours,
    /// and the fullest bucket's average wins, so text and icons along a title bar do not tint it.
    /// </summary>
    public static Rgb? Dominant(ReadOnlySpan<byte> bgra)
    {
        var count = bgra.Length / 4;
        if (count < MinimumPixels) return null;
        // 16 levels per channel: close shades share a bucket, distinct colours do not.
        var buckets = new Dictionary<int, (int Count, long B, long G, long R)>();
        for (var i = 0; i < count * 4; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var key = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            buckets.TryGetValue(key, out var bucket);
            buckets[key] = (bucket.Count + 1, bucket.B + b, bucket.G + g, bucket.R + r);
        }
        var best = buckets.Values.MaxBy(v => v.Count);
        return new Rgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    /// <summary>WCAG relative luminance, 0 for black to 1 for white.</summary>
    public static double Luminance(Rgb c)
    {
        static double Linear(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>Luminance above which a bar takes dark text.</summary>
    public const double LightThreshold = 0.3;

    /// <summary>
    /// Whether the bar takes dark text. Pure contrast ratios tip to dark text around 18%
    /// luminance, which puts black on saturated blues and greys; like system UIs, white is kept
    /// until the bar is clearly light.
    /// </summary>
    public static bool IsLight(Rgb bar) => Luminance(bar) > LightThreshold;

    /// <summary>The menu bar's text colour on `bar`.</summary>
    public static Rgb Text(Rgb bar) => IsLight(bar) ? DarkText : Rgb.White;

    /// <summary>
    /// How strongly the island draws its light edge: none where its black already stands out (a
    /// light bar), more the closer the bar is to black. Open, or with no menu bar, the island sits
    /// over anything, so it keeps a faint edge.
    /// </summary>
    public static double EdgeAlpha(Rgb? bar, bool open)
    {
        if (open || bar is not { } color) return 0.16;
        if (IsLight(color)) return 0;
        return Luminance(color) < 0.02 ? 0.26 : 0.18;
    }
}
