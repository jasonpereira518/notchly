// SPDX-License-Identifier: GPL-3.0-or-later
// Mirrors how upstream picks the pinned answer row: CommandBarCatalog.answerEntry(for:bar:) in
// Sources/Vorssaint/Services/CommandBar/CommandBarCatalog.swift. Platform-free.
using System.Globalization;
using Notchly.Core.Tools;

namespace Notchly.Core.CommandBar;

public enum CommandBarAnswerKind { Math, Units, Color, Date }

/// <summary>
/// One answer row: <see cref="Title"/> is the answer, <see cref="Detail"/> the line under it (empty when the row shows only the
/// copy hint), <see cref="CopyText"/> what Return copies. <see cref="Color"/> carries the swatch for a colour answer.
/// </summary>
public sealed record CommandBarAnswer(CommandBarAnswerKind Kind, string Title, string Detail, string CopyText, ColorValue? Color = null);

public static class CommandBarAnswers
{
    /// <summary>
    /// The pinned first row when what was typed is a sum, a conversion, a colour or a date question, or null. The order is upstream's:
    /// a sum, a unit conversion, a colour conversion, a bare colour, and dates last, since the others are stricter and "3" alone must
    /// never become a date.
    /// </summary>
    public static CommandBarAnswer? TryAnswer(string query, CultureInfo culture, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (CommandBarMath.Evaluate(query, culture) is { } math)
        {
            var expression = query.Trim();
            if (expression.EndsWith('=')) expression = expression[..^1];
            // An unfinished expression shows how it was read: "(2+3" becomes "(2+3)".
            var completed = math.ClosingBrackets.Length == 0 ? "" : expression.Trim(' ', '\t') + math.ClosingBrackets;
            return new CommandBarAnswer(CommandBarAnswerKind.Math, math.Formatted, completed, math.Formatted);
        }
        if (CommandBarUnits.Convert(query, culture) is { } units)
            return new CommandBarAnswer(CommandBarAnswerKind.Units, units.Formatted, "", units.Formatted);
        if (CommandBarColors.Convert(query) is { } converted)
            return new CommandBarAnswer(CommandBarAnswerKind.Color, converted.Formatted, "", converted.Formatted, converted.Color);
        if (ColorValue.Parse(query) is { } color)
        {
            var value = query.Trim();
            return new CommandBarAnswer(CommandBarAnswerKind.Color, value, "", value, color);
        }
        if (CommandBarDates.Evaluate(query, now, zone, culture) is { } dated)
            return new CommandBarAnswer(CommandBarAnswerKind.Date, dated.Formatted, dated.Detail, dated.Formatted);
        return null;
    }
}
