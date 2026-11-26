// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarColors.swift: colour conversion for the command bar
// ("#a2b3b4 to rgb"). Builds on the existing Notchly.Core.Tools.ColorValue (parsing and writing). Platform-free.
using System.Text;
using Notchly.Core.Tools;

namespace Notchly.Core.CommandBar;

/// <summary>Colour conversion for the command bar: "#a2b3b4 to rgb". Strict like <see cref="CommandBarUnits"/>: anything else is left to the search.</summary>
public static class CommandBarColors
{
    public sealed record Result(ColorValue Color, string Formatted);

    /// <summary>The <c>a</c> forms always write alpha; the others only for a translucent colour.</summary>
    private static readonly Dictionary<string, (ColorCopyFormat Format, bool AlwaysAlpha)> Targets = new()
    {
        ["hex"] = (ColorCopyFormat.Hex, false),
        ["rgb"] = (ColorCopyFormat.Rgb, false), ["rgba"] = (ColorCopyFormat.Rgb, true),
        ["hsl"] = (ColorCopyFormat.Hsl, false), ["hsla"] = (ColorCopyFormat.Hsl, true),
        ["swift"] = (ColorCopyFormat.SwiftUi, false), ["swiftui"] = (ColorCopyFormat.SwiftUi, false),
    };

    /// <summary>The conversion for <paramref name="input"/>, or null when it is not one.</summary>
    public static Result? Convert(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > ColorValue.MaxLength + 16) return null;
        var tokens = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3
            || !CommandBarUnits.ConversionWords.Contains(tokens[^2].ToLowerInvariant())
            || !Targets.TryGetValue(tokens[^1].ToLowerInvariant(), out var target)
            || ColorValue.Parse(string.Join(' ', tokens[..^2])) is not { } color)
            return null;
        double? alpha = target.AlwaysAlpha || color.Alpha < 1 ? color.Alpha : null;
        return new Result(color, ColorValue.Format(color.Red, color.Green, color.Blue, alpha, target.Format));
    }
}
