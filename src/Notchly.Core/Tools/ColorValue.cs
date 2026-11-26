// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/ColorValue.swift: a colour read from text that is only a colour value, and a colour
// written out in the copy format the picker is set to. Platform-free.
using System.Globalization;

namespace Notchly.Core.Tools;

/// <summary>How a colour is written out.</summary>
public enum ColorCopyFormat { Hex, Rgb, Hsl, SwiftUi }

public static class ColorCopyFormats
{
    public static string Raw(ColorCopyFormat format) => format switch
    {
        ColorCopyFormat.Rgb => "rgb", ColorCopyFormat.Hsl => "hsl", ColorCopyFormat.SwiftUi => "swiftui", _ => "hex",
    };

    /// <summary>A stored format; anything unknown is hex (upstream <c>ColorCopyFormat.sanitized</c>).</summary>
    public static ColorCopyFormat Sanitized(string? raw) => raw switch
    {
        "rgb" => ColorCopyFormat.Rgb, "hsl" => ColorCopyFormat.Hsl, "swiftui" => ColorCopyFormat.SwiftUi, _ => ColorCopyFormat.Hex,
    };

    /// <summary>Short technical label, not translated (upstream <c>label</c>).</summary>
    public static string Label(ColorCopyFormat format) => format switch
    {
        ColorCopyFormat.Rgb => "RGB", ColorCopyFormat.Hsl => "HSL", ColorCopyFormat.SwiftUi => "SwiftUI", _ => "HEX",
    };
}

/// <summary>
/// A colour read from text that is only a colour value. The accepted forms are the CSS ones designers copy and the ones the picker
/// writes: <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>rgb()</c>, <c>rgba()</c>, <c>hsl()</c>, <c>hsla()</c> and
/// SwiftUI's <c>Color(red:green:blue:opacity:)</c>. The value must be the whole entry: a colour inside a longer text is not one, and
/// a bare <c>RRGGBB</c> would also match plain numbers and hashes.
/// </summary>
public readonly record struct ColorValue(double Red, double Green, double Blue, double Alpha = 1)
{
    /// <summary>Longer than any accepted form with generous spacing; the cap keeps a render from scanning a large entry.</summary>
    public const int MaxLength = 96;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ColorValue? Parse(string? text)
    {
        if (text is null || System.Text.Encoding.UTF8.GetByteCount(text) > MaxLength) return null;
        var value = text.Trim().ToLowerInvariant();
        if (value.StartsWith('#')) return FromHex(value[1..]);
        if (Arguments(value, "rgba", "rgb") is { } rgb) return FromRgb(rgb);
        if (Arguments(value, "hsla", "hsl") is { } hsl) return FromHsl(hsl);
        if (value.StartsWith("color(", StringComparison.Ordinal) && value.EndsWith(')')) return FromSwiftUi(value[6..^1]);
        return null;
    }

    private static ColorValue? FromHex(string digits)
    {
        if (digits.Length is not (3 or 4 or 6 or 8) || !digits.All(Uri.IsHexDigit)) return null;
        var expanded = digits.Length <= 4 ? string.Concat(digits.Select(c => new string(c, 2))) : digits;
        var value = Convert.ToUInt64(expanded, 16);
        var hasAlpha = expanded.Length == 8;
        var rgb = hasAlpha ? value >> 8 : value;
        return new ColorValue(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0,
                              hasAlpha ? (value & 0xFF) / 255.0 : 1);
    }

    private static ColorValue? FromRgb(List<string> arguments)
    {
        if (arguments.Count is < 3 or > 4) return null;
        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (Percentage(arguments[i]) is { } percent) channels[i] = percent;
            else if (Number(arguments[i]) is { } number && number is >= 0 and <= 255) channels[i] = number / 255;
            else return null;
        }
        if (!ReadAlpha(arguments.Count > 3 ? arguments[3] : null, out var alpha)) return null;
        return new ColorValue(channels[0], channels[1], channels[2], alpha);
    }

    /// <summary><c>red: 0.2, green: 0.4, blue: 0.6</c>, optionally followed by <c>opacity: 0.5</c>, with or without spaces around the labels.</summary>
    private static ColorValue? FromSwiftUi(string arguments)
    {
        string[] labels = ["red", "green", "blue", "opacity"];
        var pairs = arguments.Split(',').Select(p => p.Split(':', 2)).ToList();
        if (pairs.Count is < 3 or > 4) return null;
        var components = new List<double>();
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.Length != 2 || pair[0].Trim() != labels[i] || Number(pair[1].Trim()) is not { } value || value is < 0 or > 1) return null;
            components.Add(value);
        }
        return new ColorValue(components[0], components[1], components[2], components.Count == 4 ? components[3] : 1);
    }

    private static ColorValue? FromHsl(List<string> arguments)
    {
        if (arguments.Count is < 3 or > 4) return null;
        var hueText = arguments[0].EndsWith("deg", StringComparison.Ordinal) ? arguments[0][..^3] : arguments[0];
        if (Number(hueText) is not { } hue || Percentage(arguments[1]) is not { } saturation || Percentage(arguments[2]) is not { } lightness
            || !ReadAlpha(arguments.Count > 3 ? arguments[3] : null, out var alpha)) return null;
        var h = ((hue % 360) + 360) % 360 / 360;
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double Channel(double offset)
        {
            var k = (offset + h * 12) % 12;
            return lightness - chroma / 2 * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        return new ColorValue(Channel(0), Channel(8), Channel(4), alpha);
    }

    /// <summary>The arguments of <c>name(...)</c>, split on commas, spaces and the slash CSS puts before the alpha value.</summary>
    private static List<string>? Arguments(string value, params string[] names)
    {
        if (!value.EndsWith(')')) return null;
        var name = names.FirstOrDefault(n => value.StartsWith(n + "(", StringComparison.Ordinal));
        if (name is null) return null;
        var inner = value[(name.Length + 1)..^1];
        return inner.Split([',', '/', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static double? Number(string text)
    {
        if (text.Length == 0) return null;
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(text, styles, Invariant, out var value) && double.IsFinite(value) ? value : null;
    }

    /// <summary>A <c>0%</c>…<c>100%</c> value as a fraction.</summary>
    private static double? Percentage(string text)
    {
        if (!text.EndsWith('%') || Number(text[..^1]) is not { } value || value is < 0 or > 100) return null;
        return value / 100;
    }

    /// <summary>Opaque when absent; otherwise a 0…1 number or a percentage.</summary>
    private static bool ReadAlpha(string? text, out double alpha)
    {
        alpha = 1;
        if (text is null) return true;
        if (Percentage(text) is { } percent) { alpha = percent; return true; }
        if (Number(text) is { } value && value is >= 0 and <= 1) { alpha = value; return true; }
        return false;
    }

    // MARK: Writing a colour out

    private static int Byte(double component) => (int)Math.Round(component * 255, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Formats sRGB components (0…1) in the chosen copy format. Components out of range are clamped so extended-gamut samples never
    /// produce invalid strings. <paramref name="bareHex"/> drops the leading # (some design tools reject pasted values that carry it); it
    /// only affects hex. A non-null alpha writes the alpha form.
    /// </summary>
    public static string Format(double red, double green, double blue, double? alpha, ColorCopyFormat format, bool bareHex = false)
    {
        var r = Math.Clamp(red, 0, 1);
        var g = Math.Clamp(green, 0, 1);
        var b = Math.Clamp(blue, 0, 1);
        double? a = alpha is { } raw ? Math.Clamp(raw, 0, 1) : null;
        // Three decimals are the fewest that bring every 8-bit alpha back.
        var alphaText = a is { } av ? (Math.Round(av * 1000, MidpointRounding.AwayFromZero) / 1000).ToString("0.###", Invariant) : null;
        switch (format)
        {
            case ColorCopyFormat.Hex:
            {
                var hex = string.Format(Invariant, bareHex ? "{0:X2}{1:X2}{2:X2}" : "#{0:X2}{1:X2}{2:X2}", Byte(r), Byte(g), Byte(b));
                return a is { } ah ? hex + Byte(ah).ToString("X2", Invariant) : hex;
            }
            case ColorCopyFormat.Rgb:
            {
                var channels = string.Format(Invariant, "{0}, {1}, {2}", Byte(r), Byte(g), Byte(b));
                return alphaText is null ? $"rgb({channels})" : $"rgba({channels}, {alphaText})";
            }
            case ColorCopyFormat.Hsl:
            {
                var (h, s, l) = ToHsl(r, g, b);
                var channels = string.Format(Invariant, "{0}, {1}%, {2}%", (int)Math.Round(h, MidpointRounding.AwayFromZero),
                                             (int)Math.Round(s * 100, MidpointRounding.AwayFromZero), (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
                return alphaText is null ? $"hsl({channels})" : $"hsla({channels}, {alphaText})";
            }
            default:
            {
                // Source code, not prose: a comma here would paste something that does not compile in any region.
                var rgb = string.Format(Invariant, "red: {0:F3}, green: {1:F3}, blue: {2:F3}", r, g, b);
                return a is { } ao ? $"Color({rgb}, opacity: {ao.ToString("F3", Invariant)})" : $"Color({rgb})";
            }
        }
    }

    public string ToText(ColorCopyFormat format, bool bareHex = false, bool withAlpha = false) =>
        Format(Red, Green, Blue, withAlpha || Alpha < 1 ? Alpha : null, format, bareHex);

    public static (double Hue, double Saturation, double Lightness) ToHsl(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        var lightness = (max + min) / 2;
        if (delta <= 0.000001) return (0, 0, lightness);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (max == red) hue = (green - blue) / delta % 6;
        else if (max == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Clamp(saturation, 0, 1), lightness);
    }
}
