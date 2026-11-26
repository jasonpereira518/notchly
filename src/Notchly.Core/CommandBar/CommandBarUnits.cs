// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarUnits.swift: unit conversion for the command bar ("100 km to mi",
// "20c to f", "5 gb to mb"). Foundation's Measurement is replaced by the factor tables below. Platform-free.
using System.Globalization;
using System.Text;

namespace Notchly.Core.CommandBar;

/// <summary>
/// Unit conversion for the command bar. Deliberately strict. A conversion needs a measurement, one of the little words that mean
/// "to", and another unit of the SAME family. Anything else is left to the search, because a launcher that guesses at conversions
/// turns every sentence into a wrong answer.
/// </summary>
public static class CommandBarUnits
{
    public sealed record Result(
        /// <summary>The answer, already written the way the culture writes numbers.</summary>
        string Formatted,
        double Value);

    /// <summary>
    /// The words that mean "convert into", across the languages the app speaks. Parser vocabulary, not visible text.
    /// "in" is on this list and is also the symbol for inches. That collision is the classic trap, and it is why the parser only
    /// accepts "in" as a keyword when a real unit follows it and a number with a unit precedes it: "5 in to cm" reads the first as
    /// inches and the second as the verb.
    /// </summary>
    public static readonly IReadOnlySet<string> ConversionWords = new HashSet<string>
    {
        "to", "in", "into", "as", "em", "para", "pra", "en", "a", "nach", "zu", "à", "su", "->", ">", "→",
    };

    /// <summary>Everything the bar can convert, grouped so two units only meet when they measure the same thing.</summary>
    private enum Family { Temperature, Length, Mass, Data, Duration, Volume }

    /// <summary>One unit: how many base units it holds (meters, kilograms, bytes, seconds, liters) and the symbol it is shown with.</summary>
    private sealed class Unit(Family family, string symbol, double factor,
                              Func<double, double>? toBase = null, Func<double, double>? fromBase = null)
    {
        public Family Family { get; } = family;
        public string Symbol { get; } = symbol;
        public double Factor { get; } = factor;

        public double ToBase(double value) => toBase is null ? value * Factor : toBase(value);
        public double FromBase(double value) => fromBase is null ? value / Factor : fromBase(value);
    }

    // Temperature is held in degrees Celsius. Absolute readings, never increments: 20 °C is 68 °F.
    private static readonly Unit Celsius = new(Family.Temperature, "°C", 1);
    private static readonly Unit Fahrenheit = new(Family.Temperature, "°F", 1, v => (v - 32) * 5 / 9, c => c * 9 / 5 + 32);
    private static readonly Unit Kelvin = new(Family.Temperature, " K", 1, v => v - 273.15, c => c + 273.15);

    private static readonly Unit Millimeters = new(Family.Length, " mm", 0.001);
    private static readonly Unit Centimeters = new(Family.Length, " cm", 0.01);
    private static readonly Unit Meters = new(Family.Length, " m", 1);
    private static readonly Unit Kilometers = new(Family.Length, " km", 1000);
    private static readonly Unit Inches = new(Family.Length, " in", 0.0254);
    private static readonly Unit Feet = new(Family.Length, " ft", 0.3048);
    private static readonly Unit Yards = new(Family.Length, " yd", 0.9144);
    private static readonly Unit Miles = new(Family.Length, " mi", 1609.344);

    private static readonly Unit Milligrams = new(Family.Mass, " mg", 1e-6);
    private static readonly Unit Grams = new(Family.Mass, " g", 0.001);
    private static readonly Unit Kilograms = new(Family.Mass, " kg", 1);
    private static readonly Unit MetricTons = new(Family.Mass, " t", 1000);
    private static readonly Unit Ounces = new(Family.Mass, " oz", 0.028349523125);
    private static readonly Unit Pounds = new(Family.Mass, " lb", 0.45359237);

    // Data: decimal and binary are separate units on purpose, the whole reason someone asks is to settle which one they meant.
    private static readonly Unit Bits = new(Family.Data, " bit", 0.125);
    private static readonly Unit Bytes = new(Family.Data, " byte", 1);
    private static readonly Unit Kilobytes = new(Family.Data, " kB", 1e3);
    private static readonly Unit Megabytes = new(Family.Data, " MB", 1e6);
    private static readonly Unit Gigabytes = new(Family.Data, " GB", 1e9);
    private static readonly Unit Terabytes = new(Family.Data, " TB", 1e12);
    private static readonly Unit Kibibytes = new(Family.Data, " KiB", 1024.0);
    private static readonly Unit Mebibytes = new(Family.Data, " MiB", 1024.0 * 1024);
    private static readonly Unit Gibibytes = new(Family.Data, " GiB", 1024.0 * 1024 * 1024);
    private static readonly Unit Tebibytes = new(Family.Data, " TiB", 1024.0 * 1024 * 1024 * 1024);

    // Duration: larger spans are fixed multipliers of seconds (365 days for a year).
    private static readonly Unit Milliseconds = new(Family.Duration, " ms", 0.001);
    private static readonly Unit Seconds = new(Family.Duration, " sec", 1);
    private static readonly Unit Minutes = new(Family.Duration, " min", 60);
    private static readonly Unit Hours = new(Family.Duration, " hr", 3600);
    private static readonly Unit Days = new(Family.Duration, " d", 86_400);
    private static readonly Unit Weeks = new(Family.Duration, " wk", 604_800);
    private static readonly Unit Years = new(Family.Duration, " yr", 31_536_000);

    private static readonly Unit Milliliters = new(Family.Volume, " mL", 0.001);
    private static readonly Unit Liters = new(Family.Volume, " L", 1);
    private static readonly Unit Gallons = new(Family.Volume, " gal", 3.785411784);
    private static readonly Unit Quarts = new(Family.Volume, " qt", 0.946352946);
    private static readonly Unit FluidOunces = new(Family.Volume, " fl oz", 0.0295735295625);
    private static readonly Unit Cups = new(Family.Volume, " cup", 0.2365882365);

    /// <summary>
    /// The lexicon. Symbols are matched exactly after folding, never fuzzily: "mi" must be miles and nothing else, and a near miss has
    /// to fall through to the search rather than convert the wrong thing.
    /// </summary>
    private static readonly Dictionary<string, Unit> Lexicon = BuildLexicon();

    private static Dictionary<string, Unit> BuildLexicon()
    {
        var map = new Dictionary<string, Unit>(StringComparer.Ordinal);
        void Add(string[] names, Unit unit)
        {
            foreach (var name in names)
            {
                map[name] = unit;
                // Typed text is folded before it is looked up, so a name written with an accent has to be reachable folded too.
                map[AnswerText.Normalized(name)] = unit;
            }
        }
        Add(["c", "celsius", "°c", "centigrade"], Celsius);
        Add(["f", "fahrenheit", "°f"], Fahrenheit);
        Add(["k", "kelvin"], Kelvin);

        Add(["mm", "millimeter", "millimeters", "millimetre", "millimetres", "milimetro", "milimetros"], Millimeters);
        Add(["cm", "centimeter", "centimeters", "centimetre", "centimetres", "centimetro", "centimetros"], Centimeters);
        Add(["m", "meter", "meters", "metro", "metros", "metre", "metres"], Meters);
        Add(["km", "kilometer", "kilometers", "kilometre", "kilometres", "quilometro", "quilometros"], Kilometers);
        Add(["in", "inch", "inches", "polegada", "polegadas", "\"", "″", "”"], Inches);
        Add(["ft", "foot", "feet", "pe", "pes", "'", "′", "’"], Feet);
        Add(["yd", "yard", "yards", "jarda", "jardas"], Yards);
        Add(["mi", "mile", "miles", "milha", "milhas"], Miles);

        Add(["mg", "milligram", "milligrams"], Milligrams);
        Add(["g", "gram", "grams", "grama", "gramas"], Grams);
        Add(["kg", "kilogram", "kilograms", "quilo", "quilos"], Kilograms);
        Add(["t", "ton", "tons", "tonne", "tonelada", "toneladas"], MetricTons);
        Add(["oz", "ounce", "ounces", "onca", "oncas"], Ounces);
        Add(["lb", "lbs", "pound", "pounds", "libra", "libras"], Pounds);

        Add(["b", "byte", "bytes"], Bytes);
        Add(["kb", "kilobyte", "kilobytes"], Kilobytes);
        Add(["mb", "megabyte", "megabytes"], Megabytes);
        Add(["gb", "gigabyte", "gigabytes"], Gigabytes);
        Add(["tb", "terabyte", "terabytes"], Terabytes);
        Add(["kib", "kibibyte", "kibibytes"], Kibibytes);
        Add(["mib", "mebibyte", "mebibytes"], Mebibytes);
        Add(["gib", "gibibyte", "gibibytes"], Gibibytes);
        Add(["tib", "tebibyte", "tebibytes"], Tebibytes);
        Add(["bit", "bits"], Bits);

        Add(["ms", "millisecond", "milliseconds"], Milliseconds);
        Add(["s", "sec", "secs", "second", "seconds", "segundo", "segundos"], Seconds);
        Add(["min", "mins", "minute", "minutes", "minuto", "minutos"], Minutes);
        Add(["h", "hr", "hrs", "hour", "hours", "hora", "horas"], Hours);
        Add(["d", "day", "days", "dia", "dias"], Days);
        Add(["wk", "wks", "week", "weeks", "semana", "semanas"], Weeks);
        Add(["yr", "yrs", "year", "years", "ano", "anos"], Years);

        Add(["ml", "milliliter", "milliliters", "millilitre", "millilitres", "mililitro", "mililitros"], Milliliters);
        Add(["l", "liter", "liters", "litre", "litres", "litro", "litros"], Liters);
        Add(["gal", "gallon", "gallons", "galao", "galoes"], Gallons);
        Add(["qt", "quart", "quarts"], Quarts);
        Add(["floz", "fl oz", "fluidounce", "fluidounces", "fluid ounce", "fluid ounces"], FluidOunces);
        Add(["cup", "cups", "xicara", "xicaras"], Cups);
        return map;
    }

    /// <summary>The conversion for <paramref name="input"/> with the culture's own separators, or null when it is not one.</summary>
    public static Result? Convert(string input, CultureInfo culture) =>
        Convert(input, culture.NumberFormat.NumberDecimalSeparator, culture.NumberFormat.NumberGroupSeparator, culture);

    /// <summary>The conversion for <paramref name="input"/>, or null when it is not one.</summary>
    public static Result? Convert(string input, string decimalSeparator, string groupingSeparator, CultureInfo culture)
    {
        var words = AnswerText.Normalized(input).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 3 or > 8) return null;

        // A connector is a whole word. In "5ft 11in in cm" only the second "in" separates the measurement from its target.
        for (var position = words.Length - 2; position >= 1; position--)
        {
            if (!ConversionWords.Contains(words[position])) continue;
            if (!Lexicon.TryGetValue(string.Join(' ', words[(position + 1)..]), out var target)) continue;
            if (ParseValue(string.Join(' ', words[..position]), decimalSeparator, groupingSeparator) is not { } source) continue;
            if (source.Unit.Family != target.Family) continue;
            var converted = target.FromBase(source.Unit.ToBase(source.Value));
            if (!double.IsFinite(converted)) continue;
            var formatted = FeetAndInches(converted, target, culture) ?? Format(converted, target, culture);
            return new Result(formatted, converted);
        }
        return null;
    }

    private static bool IsNumberCharacter(char c) => char.IsNumber(c) || c is '.' or ',' or '-';

    private static (double Value, Unit Unit)? ParseValue(string text, string decimalSeparator, string groupingSeparator)
    {
        var rest = text;

        double? ReadNumber()
        {
            var count = 0;
            while (count < rest.Length && IsNumberCharacter(rest[count])) count++;
            var digits = rest[..count];
            rest = rest[count..].TrimStart();
            return Number(digits, decimalSeparator, groupingSeparator);
        }

        if (ReadNumber() is not { } value) return null;
        if (Lexicon.TryGetValue(rest, out var direct)) return (value, direct);

        // Only whole feet can introduce an inch remainder. The inch mark is optional in height shorthand such as "5ft 11" or "5′11".
        var nameLength = 0;
        while (nameLength < rest.Length && !char.IsWhiteSpace(rest[nameLength]) && !IsNumberCharacter(rest[nameLength])) nameLength++;
        if (!Lexicon.TryGetValue(rest[..nameLength], out var feet) || feet != Feet || value != Math.Truncate(value)) return null;
        rest = rest[nameLength..].TrimStart();
        // An apostrophe followed by three digits can be a thousands separator.
        if (rest.StartsWith('-') || rest.TakeWhile(char.IsNumber).Count() > 2) return null;
        if (ReadNumber() is not { } inches || inches is < 0 or >= 12) return null;
        if (rest.Length != 0 && !(Lexicon.TryGetValue(rest, out var inchUnit) && inchUnit == Inches)) return null;
        var totalFeet = Math.Abs(value) + inches / 12;
        return (double.IsNegative(value) ? -totalFeet : totalFeet, feet);
    }

    /// <summary>
    /// Like the calculator, accepts an alternate decimal separator unless it forms grouped thousands. When both separators occur, the
    /// last is decimal. Numeric components never include a space or an apostrophe, so where thousands are grouped with one of those the
    /// alternate is whichever of "." and "," is not the decimal, as in the calculator.
    /// </summary>
    private static double? Number(string token, string decimalSeparator, string groupingSeparator)
    {
        var groupsWithPunctuation = groupingSeparator is "." or "," && groupingSeparator != decimalSeparator;
        var alternate = groupsWithPunctuation ? groupingSeparator : decimalSeparator == "," ? "." : ",";
        var normalized = token;
        var hasDecimal = token.Contains(decimalSeparator, StringComparison.Ordinal);
        var hasAlternate = token.Contains(alternate, StringComparison.Ordinal);
        if (hasDecimal && hasAlternate)
        {
            var lastDecimal = token.LastIndexOf(decimalSeparator, StringComparison.Ordinal);
            var lastAlternate = token.LastIndexOf(alternate, StringComparison.Ordinal);
            if (lastAlternate > lastDecimal)
            {
                normalized = token.Replace(decimalSeparator, "", StringComparison.Ordinal).Replace(alternate, ".", StringComparison.Ordinal);
            }
            else
            {
                normalized = token.Replace(alternate, "", StringComparison.Ordinal).Replace(decimalSeparator, ".", StringComparison.Ordinal);
            }
        }
        else if (hasAlternate)
        {
            // No grouped number opens with a 0 group, so "0,250" is a decimal.
            var unsigned = token.StartsWith('-') ? token[1..] : token;
            var groups = unsigned.Split(alternate);
            var isGrouping = groups.Length >= 2 && groups[0].Length is >= 1 and <= 3 && !groups[0].StartsWith('0')
                             && groups.Skip(1).All(group => group.Length == 3);
            normalized = token.Replace(alternate, isGrouping ? "" : ".", StringComparison.Ordinal);
        }
        else if (hasDecimal)
        {
            normalized = token.Replace(decimalSeparator, ".", StringComparison.Ordinal);
        }
        if (normalized.Length == 0 || normalized.Count(c => c == '.') > 1 || !normalized.All(c => char.IsNumber(c) || c is '.' or '-'))
            return null;
        return AnswerNumbers.TryParseDouble(normalized, allowExponent: false, out var value) ? value : null;
    }

    /// <summary>
    /// Mixed units are useful once there is a whole foot to show. Smaller and negative values stay in decimal feet so the conversion
    /// keeps its meaning.
    /// </summary>
    private static string? FeetAndInches(double value, Unit unit, CultureInfo culture)
    {
        if (unit != Feet || value < 1) return null;

        var wholeFeet = Math.Floor(value);
        var rawInches = (value - wholeFeet) * 12;
        var scale = Math.Pow(10, FractionDigits(rawInches));
        var inches = Math.Round(rawInches * scale, MidpointRounding.AwayFromZero) / scale;
        if (inches >= 12)
        {
            wholeFeet += 1;
            inches = 0;
        }

        var feet = Format(wholeFeet, Feet, culture);
        if (inches == 0) return feet;
        return $"{feet} {Format(inches, Inches, culture).TrimStart()}";
    }

    /// <summary>The unit asked for is the unit shown; nobody types "to mb" hoping for gigabytes.</summary>
    private static string Format(double value, Unit unit, CultureInfo culture)
    {
        var fractionDigits = FractionDigits(value);
        var magnitude = Math.Abs(value);
        // Four decimals would print a millionth as "0", which reads as a wrong answer, so a tiny value keeps its significant digits
        // instead. Temperature offsets can leave a tiny rounding remainder at zero; keep their existing decimal precision instead
        // of exposing it.
        var number = magnitude != 0 && magnitude < 0.001 && unit.Family != Family.Temperature
            ? AnswerNumbers.Significant(value, 4, culture)
            : AnswerNumbers.Fixed(value, fractionDigits, culture);
        return number + unit.Symbol;
    }

    /// <summary>Small numbers keep their decimals, big ones lose the noise.</summary>
    private static int FractionDigits(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude == 0) return 0;
        if (magnitude < 1) return 4;
        if (magnitude < 100) return 2;
        return 1;
    }
}

/// <summary>
/// Text folding shared by the command bar's answer engines (upstream <c>CommandBarSearch.normalized</c>): case, accent and width
/// differences never matter, invisible format characters are dropped, and any run of whitespace becomes one space. Folded without a
/// culture on purpose: Turkish lowercases a capital I to a dotless one.
/// </summary>
internal static class AnswerText
{
    public static string Normalized(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            // The characters that take up no space and that nobody can type: direction marks, zero-width joiners, soft hyphens.
            if (raw >= '­' && char.GetUnicodeCategory(raw) == UnicodeCategory.Format) continue;
            // Full-width forms fold onto their ordinary ones; the ideographic space is a space.
            var c = raw switch { >= '！' and <= '～' => (char)(raw - 0xFEE0), '　' => ' ', _ => raw };
            builder.Append(c);
        }
        var text = builder.ToString();
        try
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var stripped = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
                if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) stripped.Append(c);
            text = stripped.ToString().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not valid Unicode (a lone surrogate): keep the text as it is.
        }
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }
}
