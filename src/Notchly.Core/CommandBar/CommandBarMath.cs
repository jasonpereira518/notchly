// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarMath.swift: the command bar's inline calculator.
// Platform-free. Swift's Locale/NumberFormatter become a CultureInfo; the separators can also be passed in so tests are exact.
using System.Globalization;

namespace Notchly.Core.CommandBar;

/// <summary>
/// The command bar's inline calculator. It is deliberately strict: input must be entirely mathematical, so commands and searches
/// are never answered as sums.
/// </summary>
public static class CommandBarMath
{
    /// <summary>Words that read as "percent OF a number" across the languages the app speaks. Parser vocabulary, not visible text.</summary>
    private static readonly HashSet<string> OfWords = ["of", "de", "da", "do", "von", "di", "del", "dal"];

    public sealed record Result(
        /// <summary>The value, formatted the way the culture writes numbers.</summary>
        string Formatted,
        double Value,
        /// <summary>Closing brackets supplied virtually while evaluating an unfinished expression.</summary>
        string ClosingBrackets);

    /// <summary>Evaluates a complete mathematical expression with the culture's own separators.</summary>
    public static Result? Evaluate(string input, CultureInfo culture) =>
        Evaluate(input, culture.NumberFormat.NumberDecimalSeparator, culture.NumberFormat.NumberGroupSeparator, culture);

    /// <summary>
    /// Evaluates a complete mathematical expression, supplying only missing closing brackets. Trigonometric functions use radians.
    /// The separators are how this person types numbers; the culture is how the answer is written back.
    /// </summary>
    public static Result? Evaluate(string input, string decimalSeparator, string groupingSeparator, CultureInfo culture)
    {
        if (Parse(input, decimalSeparator, groupingSeparator) is not { } parsed) return null;
        var value = SignificantRounded(parsed.Operand.Value);
        if (!double.IsFinite(value) || Format(value, culture) is not { } formatted) return null;
        return new Result(formatted, value, parsed.ClosingBrackets);
    }

    /// <summary>
    /// Produces an ungrouped, locale-aware number suitable for putting an answer back into the calculator. The shortest round-trip
    /// spelling avoids exposing binary noise; brackets preserve negative values under powers.
    /// </summary>
    public static string ReusableExpression(Result result, string decimalSeparator = ".")
    {
        var number = result.Value == 0 ? 0 : result.Value;
        var ascii = AnswerNumbers.ShortestText(number);
        if (ascii.EndsWith(".0", StringComparison.Ordinal)) ascii = ascii[..^2];
        var separator = decimalSeparator.Length == 0 ? "." : decimalSeparator;
        var localized = ascii.Replace(".", separator, StringComparison.Ordinal);
        return number < 0 ? $"({localized})" : localized;
    }

    public static string ReusableExpression(Result result, CultureInfo culture) =>
        ReusableExpression(result, culture.NumberFormat.NumberDecimalSeparator);

    /// <summary>Rounds away floating point noise (0.1 + 0.2 must read as 0.3) and writes the number with the culture's separators.</summary>
    public static string? Format(double value, CultureInfo culture)
    {
        var rounded = SignificantRounded(value);
        if (!double.IsFinite(rounded)) return null;
        var magnitude = Math.Abs(rounded);
        if (rounded != 0 && (magnitude >= 1e12 || magnitude < 1e-6))
        {
            // Plain digits would print a billionth as "0" and a huge product as a wall of zeros; both read as a wrong answer.
            return AnswerNumbers.Scientific(rounded, 8, culture);
        }
        // -0 is an answer no one asked for.
        return AnswerNumbers.Fixed(rounded == 0 ? 0 : rounded, 8, culture);
    }

    /// <summary>
    /// True for "2026-07-27", "27/07/2026" and "10:30": groups of digits held together by a single kind of separator, with nothing
    /// else around them.
    /// </summary>
    public static bool LooksLikeDateOrTime(string input)
    {
        foreach (var separator in new[] { '-', '/', ':' })
        {
            var parts = input.Split(separator);
            if (parts.Length is < 2 or > 3) continue;
            var allDigits = parts.All(part =>
            {
                // A date is written closed up. Numbers spaced around a minus or a slash are a sum: "100 - 20 - 30" is not a day.
                var digits = separator == ':' ? part.Trim(' ', '\t') : part;
                return digits.Length != 0 && digits.Length <= 4 && digits.All(char.IsNumber);
            });
            if (!allDigits) continue;
            // Two plain numbers around a minus really can be a subtraction, so only the shapes that read as a date or a time are refused.
            if (separator == ':') return true;
            if (parts.Length == 3) return true;
        }
        return false;
    }

    /// <summary>
    /// Keeps ten significant digits so ordinary decimal arithmetic loses binary noise, but never fewer than a large number's whole
    /// digits plus two decimals: 12345678901 + 1 must not read as 12,345,678,900. Fifteen is the most, since that many significant
    /// digits always survive a round trip through a Double.
    /// </summary>
    private static double SignificantRounded(double value)
    {
        if (value == 0 || !double.IsFinite(value)) return value;
        var magnitude = Math.Floor(Math.Log10(Math.Abs(value)));
        var digits = Math.Min(15, Math.Max(10, magnitude + 3));
        var exponent = digits - magnitude - 1;
        if (exponent < 0)
        {
            // A power of ten below one is never exact in binary, so scaling by it puts noise back: 2^56 would reuse as
            // 7.2057594037927896e+16. Powers above one are exact up to 10^22, which keeps the digits clean for answers below 10^37.
            var divisor = Math.Pow(10.0, -exponent);
            var rounded = RoundAway(value / divisor) * divisor;
            return double.IsFinite(rounded) ? rounded : value;
        }
        var factor = Math.Pow(10.0, exponent);
        if (!double.IsFinite(factor) || factor == 0) return value;
        var scaled = RoundAway(value * factor);
        if (!double.IsFinite(scaled)) return value;
        return scaled / factor;
    }

    /// <summary>Swift's <c>rounded()</c>: halves go away from zero.</summary>
    private static double RoundAway(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    // MARK: Input validation and tokens

    private enum Bracket { Round, Square }

    private static char Closing(Bracket bracket) => bracket == Bracket.Round ? ')' : ']';

    private enum Kind { Number, Constant, Function, Plus, Minus, Times, Divide, Power, Percent, OfWord, Left, Right }

    /// <summary>
    /// Named scientific functions: sqrt, abs, sin, cos, tan, asin, acos, atan, ln, log/log10, exp, floor, ceil and round.
    /// Trigonometric functions use radians; log and log10 are base 10.
    /// </summary>
    private enum Fn { Sqrt, Abs, Sin, Cos, Tan, Asin, Acos, Atan, Ln, Log, Exp, Floor, Ceil, Round }

    private static readonly Dictionary<string, Fn> FunctionNames = new()
    {
        ["sqrt"] = Fn.Sqrt, ["abs"] = Fn.Abs, ["sin"] = Fn.Sin, ["cos"] = Fn.Cos, ["tan"] = Fn.Tan, ["asin"] = Fn.Asin,
        ["acos"] = Fn.Acos, ["atan"] = Fn.Atan, ["ln"] = Fn.Ln, ["log"] = Fn.Log, ["log10"] = Fn.Log, ["exp"] = Fn.Exp,
        ["floor"] = Fn.Floor, ["ceil"] = Fn.Ceil, ["round"] = Fn.Round,
    };

    /// <summary>Rejects undefined domains and overflow rather than showing invalid answers.</summary>
    private static double? Apply(Fn function, double value)
    {
        double result;
        switch (function)
        {
            case Fn.Sqrt:
                if (value < 0) return null;
                result = Math.Sqrt(value);
                break;
            case Fn.Abs: result = Math.Abs(value); break;
            case Fn.Sin: result = Math.Sin(value); break;
            case Fn.Cos: result = Math.Cos(value); break;
            case Fn.Tan: result = Math.Tan(value); break;
            case Fn.Asin:
                if (value is < -1 or > 1) return null;
                result = Math.Asin(value);
                break;
            case Fn.Acos:
                if (value is < -1 or > 1) return null;
                result = Math.Acos(value);
                break;
            case Fn.Atan: result = Math.Atan(value); break;
            case Fn.Ln:
                if (value <= 0) return null;
                result = Math.Log(value);
                break;
            case Fn.Log:
                if (value <= 0) return null;
                result = Math.Log10(value);
                break;
            case Fn.Exp: result = Math.Exp(value); break;
            case Fn.Floor: result = Math.Floor(value); break;
            case Fn.Ceil: result = Math.Ceiling(value); break;
            default: result = RoundAway(value); break;
        }
        return double.IsFinite(result) ? result : null;
    }

    private readonly record struct Token(Kind Kind, double Value = 0, Fn Function = default, Bracket Bracket = default)
    {
        public bool IsExplicitOperation => Kind is Kind.Plus or Kind.Minus or Kind.Times or Kind.Divide or Kind.Power or Kind.OfWord;
        public bool EndsImplicitProduct => Kind is Kind.Number or Kind.Constant or Kind.Right;
        public bool StartsImplicitProduct => Kind is Kind.Constant or Kind.Function or Kind.Left;
    }

    private sealed record ParsedInput(Operand Operand, string ClosingBrackets);

    /// <summary>
    /// How this person writes numbers, plus the one other separator people type. The alternate is whichever of "." and "," is not
    /// the decimal point, so both work everywhere. A culture that groups thousands with a space or an apostrophe keeps that
    /// character as grouping only.
    /// </summary>
    private readonly struct Separators
    {
        public char Decimal { get; }
        public char Alternate { get; }
        public char? GroupingOnly { get; }

        public Separators(string decimalSeparator, string groupingSeparator)
        {
            var dec = decimalSeparator.Length > 0 ? decimalSeparator[0] : '.';
            var grouping = groupingSeparator.Length > 0 ? groupingSeparator[0] : ',';
            var groupingIsPunctuation = grouping is '.' or ',';
            Decimal = dec;
            if (groupingIsPunctuation && grouping != dec)
            {
                Alternate = grouping;
                GroupingOnly = null;
            }
            else
            {
                Alternate = dec == ',' ? '.' : ',';
                GroupingOnly = grouping == dec ? null : grouping;
            }
        }

        public bool IsPart(char character) =>
            char.IsNumber(character) || character == Decimal || character == Alternate || character == GroupingOnly;
    }

    /// <summary>The one validation/parsing path shared by evaluation and bracket hints.</summary>
    private static ParsedInput? Parse(string input, string decimalSeparator, string groupingSeparator)
    {
        if (ExpressionWithoutTrailingEquals(input) is not { } expression) return null;
        if (new System.Globalization.StringInfo(expression).LengthInTextElements > 120 || expression.Length == 0
            || LooksLikeDateOrTime(expression)) return null;
        var separators = new Separators(decimalSeparator, groupingSeparator);
        if (Tokenize(expression, separators) is not { } tokens || !IsCalculation(tokens)
            || VirtualClosings(tokens) is not { } closings) return null;
        tokens.AddRange(closings.Select(b => new Token(Kind.Right, Bracket: b)));
        var parser = new Parser(tokens);
        if (parser.ParseExpression() is not { } operand || !parser.IsAtEnd || !double.IsFinite(operand.Value)) return null;
        return new ParsedInput(operand, new string(closings.Select(Closing).ToArray()));
    }

    /// <summary>Accepts one optional trailing equals sign without accepting assignments or equations.</summary>
    private static string? ExpressionWithoutTrailingEquals(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        var equals = trimmed.IndexOf('=');
        if (equals < 0) return trimmed;
        if (equals != trimmed.Length - 1) return null;
        var expression = trimmed[..equals].Trim();
        return expression.Length == 0 ? null : expression;
    }

    /// <summary>Requires mathematical intent rather than promoting every lone number to an answer.</summary>
    private static bool IsCalculation(List<Token> tokens)
    {
        if (tokens.Any(t => t.IsExplicitOperation) || tokens.Any(t => t.Kind == Kind.Function)) return true;
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].EndsImplicitProduct && tokens[i + 1].StartsImplicitProduct) return true;
        return false;
    }

    /// <summary>Rejects mismatches and surplus closers, then returns only the matching closers that may safely be appended.</summary>
    private static List<Bracket>? VirtualClosings(List<Token> tokens)
    {
        var stack = new List<Bracket>();
        foreach (var token in tokens)
        {
            if (token.Kind == Kind.Left) stack.Add(token.Bracket);
            else if (token.Kind == Kind.Right)
            {
                if (stack.Count == 0 || stack[^1] != token.Bracket) return null;
                stack.RemoveAt(stack.Count - 1);
            }
        }
        stack.Reverse();
        return stack;
    }

    /// <summary>Recognizes only supported mathematical symbols, numbers, and identifiers.</summary>
    private static List<Token>? Tokenize(string input, Separators separators)
    {
        var tokens = new List<Token>();
        var index = 0;

        // Reads names without swallowing the operand in 2x3; log10 alone has a numeric suffix.
        string ReadWord()
        {
            var start = index;
            while (index < input.Length && char.IsLetter(input[index])) index++;
            var word = input[start..index].ToLowerInvariant();
            if (word == "log")
            {
                var digits = index;
                while (index < input.Length && char.IsNumber(input[index])) index++;
                word += input[digits..index];
            }
            return word;
        }

        while (index < input.Length)
        {
            var character = input[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }
            if (separators.IsPart(character))
            {
                if (ReadNumber(input, ref index, separators) is not { } number) return null;
                tokens.Add(new Token(Kind.Number, number));
                continue;
            }
            if (character == 'π')
            {
                tokens.Add(new Token(Kind.Constant, Math.PI));
                index++;
                continue;
            }
            if (char.IsLetter(character))
            {
                var word = ReadWord();
                if (word == "x") tokens.Add(new Token(Kind.Times));
                else if (word == "pi") tokens.Add(new Token(Kind.Constant, Math.PI));
                else if (word == "e") tokens.Add(new Token(Kind.Constant, Math.Exp(1)));
                else if (FunctionNames.TryGetValue(word, out var function)) tokens.Add(new Token(Kind.Function, Function: function));
                else if (OfWords.Contains(word)) tokens.Add(new Token(Kind.OfWord));
                else return null;
                continue;
            }
            index++;
            switch (character)
            {
                case '+': tokens.Add(new Token(Kind.Plus)); break;
                case '-' or '−': tokens.Add(new Token(Kind.Minus)); break;      // also the real minus sign
                case '*' or '×': tokens.Add(new Token(Kind.Times)); break;      // and the multiplication sign
                case '/' or '÷': tokens.Add(new Token(Kind.Divide)); break;
                case '^': tokens.Add(new Token(Kind.Power)); break;
                case '%': tokens.Add(new Token(Kind.Percent)); break;
                case '(': tokens.Add(new Token(Kind.Left, Bracket: Bracket.Round)); break;
                case '[': tokens.Add(new Token(Kind.Left, Bracket: Bracket.Square)); break;
                case ')': tokens.Add(new Token(Kind.Right, Bracket: Bracket.Round)); break;
                case ']': tokens.Add(new Token(Kind.Right, Bracket: Bracket.Square)); break;
                default: return null;
            }
        }
        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>
    /// Reads one number, deciding what each separator means. When the decimal and the alternate both appear, the last one is the
    /// decimal point. When only the alternate appears, it is grouping only if it looks the part: groups of exactly three digits. A
    /// grouping-only space or apostrophe must always look the part. An optional ASCII scientific exponent follows.
    /// </summary>
    private static double? ReadNumber(string characters, ref int index, Separators separators)
    {
        var written = new System.Text.StringBuilder();
        while (index < characters.Length)
        {
            var character = characters[index];
            if (!separators.IsPart(character)) break;
            // A grouping character only groups when a digit follows it, so a no-break space after the last digit is read as a plain space.
            if (character == separators.GroupingOnly && !(index + 1 < characters.Length && char.IsNumber(characters[index + 1]))) break;
            written.Append(character);
            index++;
        }
        if (written.Length == 0) return null;

        var raw = written.ToString();
        if (separators.GroupingOnly is { } groupingOnly && raw.Contains(groupingOnly))
        {
            var integerEnd = raw.IndexOfAny([separators.Decimal, separators.Alternate]);
            var integerPart = integerEnd < 0 ? raw : raw[..integerEnd];
            if (!LooksLikeGrouping(integerPart, groupingOnly) || raw[integerPart.Length..].Contains(groupingOnly)) return null;
            raw = raw.Replace(groupingOnly.ToString(), "", StringComparison.Ordinal);
        }
        var decimalSeparator = separators.Decimal;
        var alternateSeparator = separators.Alternate;

        var exponent = "";
        if (index < characters.Length && characters[index] is 'e' or 'E')
        {
            var exponentIndex = index + 1;
            if (exponentIndex < characters.Length && characters[exponentIndex] is '+' or '-') exponentIndex++;
            var digitsStart = exponentIndex;
            while (exponentIndex < characters.Length && char.IsNumber(characters[exponentIndex])) exponentIndex++;
            if (exponentIndex > digitsStart)
            {
                exponent = characters[index..exponentIndex];
                index = exponentIndex;
            }
        }

        var hasDecimal = raw.Contains(decimalSeparator);
        var hasAlternate = decimalSeparator != alternateSeparator && raw.Contains(alternateSeparator);
        var normalized = raw;
        if (hasDecimal && hasAlternate)
        {
            var lastDecimal = raw.LastIndexOf(decimalSeparator);
            var lastAlternate = raw.LastIndexOf(alternateSeparator);
            if (lastAlternate > lastDecimal)
            {
                normalized = raw.Replace(decimalSeparator.ToString(), "", StringComparison.Ordinal)
                                .Replace(alternateSeparator.ToString(), ".", StringComparison.Ordinal);
            }
            else
            {
                normalized = raw.Replace(alternateSeparator.ToString(), "", StringComparison.Ordinal)
                                .Replace(decimalSeparator.ToString(), ".", StringComparison.Ordinal);
            }
        }
        else if (hasAlternate)
        {
            normalized = LooksLikeGrouping(raw, alternateSeparator)
                ? raw.Replace(alternateSeparator.ToString(), "", StringComparison.Ordinal)
                : raw.Replace(alternateSeparator.ToString(), ".", StringComparison.Ordinal);
        }
        else if (hasDecimal)
        {
            normalized = raw.Replace(decimalSeparator.ToString(), ".", StringComparison.Ordinal);
        }
        if (normalized.Count(c => c == '.') > 1) return null;
        return AnswerNumbers.TryParseDouble(normalized + exponent, allowExponent: true, out var value) && double.IsFinite(value) ? value : null;
    }

    /// <summary>Distinguishes grouped thousands from a decimal written with the alternate separator. Thousands never start at zero, so 0.125 always has decimals.</summary>
    private static bool LooksLikeGrouping(string raw, char separator)
    {
        var parts = raw.Split(separator);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[0].Length > 3 || parts[0][0] == '0') return false;
        return parts.Skip(1).All(part => part.Length == 3);
    }

    // MARK: Parser

    private readonly record struct Operand(double Value, double? PercentRaw);

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _depth;

        public bool IsAtEnd => _index >= tokens.Count;

        private Token? Peek() => _index < tokens.Count ? tokens[_index] : null;

        private bool PeekIs(Kind kind) => _index < tokens.Count && tokens[_index].Kind == kind;

        /// <summary>Parses addition and subtraction, retaining calculator-style relative percentages.</summary>
        public Operand? ParseExpression()
        {
            if (_depth >= 32 || ParseTerm() is not { } first) return null;
            var left = first;
            while (Peek() is { } token && token.Kind is Kind.Plus or Kind.Minus)
            {
                _index++;
                if (ParseTerm() is not { } right) return null;
                var delta = right.PercentRaw is { } percent ? left.Value * percent / 100 : right.Value;
                var value = token.Kind == Kind.Plus ? left.Value + delta : left.Value - delta;
                if (!double.IsFinite(value)) return null;
                left = new Operand(value, null);
            }
            return left;
        }

        /// <summary>Parses explicit and implicit products, division, and percentage-of expressions.</summary>
        private Operand? ParseTerm()
        {
            if (ParseFactor() is not { } first) return null;
            var left = first;
            while (Peek() is { } token)
            {
                var implicitProduct = token.StartsImplicitProduct;
                if (!(implicitProduct || token.Kind is Kind.Times or Kind.Divide or Kind.OfWord)) break;
                if (!implicitProduct) _index++;
                if (token.Kind == Kind.OfWord && left.PercentRaw is null) return null;
                if (ParseFactor() is not { } right) return null;
                double value;
                if (token.Kind == Kind.Divide)
                {
                    if (right.Value == 0) return null;
                    value = left.Value / right.Value;
                }
                else value = left.Value * right.Value;
                if (!double.IsFinite(value)) return null;
                left = new Operand(value, null);
            }
            return left;
        }

        /// <summary>A sign applies to the whole power, so -2^2 is -4.</summary>
        private Operand? ParseFactor()
        {
            if (Peek() is { } token && token.Kind is Kind.Minus or Kind.Plus)
            {
                _index++;
                if (ParseFactor() is not { } operand) return null;
                return token.Kind == Kind.Minus ? new Operand(-operand.Value, -operand.PercentRaw) : operand;
            }
            return ParsePower();
        }

        /// <summary>Groups powers to the right, so 2^3^2 is 2^(3^2).</summary>
        private Operand? ParsePower()
        {
            if (ParsePrimary() is not { } basis) return null;
            if (!PeekIs(Kind.Power)) return basis;
            _index++;
            if (ParseFactor() is not { } exponent) return null;
            var value = Math.Pow(basis.Value, exponent.Value);
            return double.IsFinite(value) ? new Operand(value, null) : null;
        }

        /// <summary>Reads a number, constant, bracketed expression, or function with an optional percent.</summary>
        private Operand? ParsePrimary()
        {
            if (Peek() is not { } token) return null;
            Operand basis;
            switch (token.Kind)
            {
                case Kind.Number or Kind.Constant:
                    _index++;
                    basis = new Operand(token.Value, null);
                    break;
                case Kind.Left:
                    if (ParseBracketed() is not { } grouped) return null;
                    basis = new Operand(grouped, null);
                    break;
                case Kind.Function:
                    _index++;
                    if (ParseBracketed() is not { } argument || Apply(token.Function, argument) is not { } applied) return null;
                    basis = new Operand(applied, null);
                    break;
                default:
                    return null;
            }
            if (PeekIs(Kind.Percent))
            {
                _index++;
                return new Operand(basis.Value / 100, basis.Value);
            }
            return basis;
        }

        /// <summary>Shares bracket matching and nesting limits between groups and function arguments.</summary>
        private double? ParseBracketed()
        {
            if (Peek() is not { Kind: Kind.Left } left) return null;
            _index++;
            _depth++;
            try
            {
                if (_depth >= 32 || ParseExpression() is not { } inner) return null;
                if (Peek() is not { Kind: Kind.Right } right || right.Bracket != left.Bracket) return null;
                _index++;
                return inner.Value;
            }
            finally { _depth--; }
        }
    }
}

/// <summary>
/// Number reading and writing shared by the command bar's answer engines. Swift's NumberFormatter rounds the shortest decimal
/// spelling of a Double, so the same is done here through <see cref="decimal"/> rather than formatting the binary value.
/// </summary>
internal static class AnswerNumbers
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A plain ASCII number, with an exponent when the caller allows one. Swift's Double("...") reads the same shapes.</summary>
    public static bool TryParseDouble(string text, bool allowExponent, out double value)
    {
        var styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (allowExponent) styles |= NumberStyles.AllowExponent;
        return double.TryParse(text, styles, Invariant, out value);
    }

    private static bool TryDecimal(double value, out decimal result) =>
        decimal.TryParse(value.ToString("R", Invariant), NumberStyles.Float, Invariant, out result);

    private static string Pattern(int fractionDigits) => fractionDigits > 0 ? "#,##0." + new string('#', fractionDigits) : "#,##0";

    /// <summary>Grouped decimal digits with at most <paramref name="maxFraction"/> decimals (NumberFormatter's .decimal style).</summary>
    public static string Fixed(double value, int maxFraction, CultureInfo culture)
    {
        if (!TryDecimal(value, out var exact)) return value.ToString("N" + maxFraction, culture);
        var rounded = Math.Round(exact, Math.Min(maxFraction, 28), MidpointRounding.ToEven);
        if (rounded == 0) rounded = 0m;
        return rounded.ToString(Pattern(maxFraction), culture);
    }

    /// <summary>At most <paramref name="maxSignificant"/> significant digits, still written in plain digits (usesSignificantDigits).</summary>
    public static string Significant(double value, int maxSignificant, CultureInfo culture)
    {
        if (value == 0) return "0";
        var place = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        var decimals = maxSignificant - 1 - place;
        if (decimals < 0 || decimals > 28 || !TryDecimal(value, out var exact))
            return Scientific(value, maxSignificant, culture);
        var rounded = Math.Round(exact, decimals, MidpointRounding.ToEven);
        if (rounded == 0) rounded = 0m;
        return rounded.ToString(Pattern(decimals), culture);
    }

    /// <summary>Scientific notation with an "e" and no padding, as NumberFormatter writes it: 1e-9, 1.2676506e30.</summary>
    public static string Scientific(double value, int maxSignificant, CultureInfo culture)
    {
        var text = value.ToString("E" + (maxSignificant - 1), Invariant);
        var e = text.IndexOf('E');
        var mantissa = text[..e];
        var exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, Invariant);
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
        var format = culture.NumberFormat;
        mantissa = mantissa.Replace(".", format.NumberDecimalSeparator, StringComparison.Ordinal);
        return (negative ? format.NegativeSign : "") + mantissa + "e" + (exponent < 0 ? format.NegativeSign : "")
               + Math.Abs(exponent).ToString(Invariant);
    }

    /// <summary>
    /// Swift's <c>String(double)</c>: the shortest digits that read back as the same value, plain between 1e-4 and 2^54 and in
    /// exponent form (<c>1e-05</c>, <c>7.20575940379279e+16</c>) outside that range.
    /// </summary>
    public static string ShortestText(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0) return sign + "0.0";
        var text = Math.Abs(value).ToString("R", Invariant);
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, Invariant);
            text = text[..e];
        }
        var dot = text.IndexOf('.');
        var integerLength = dot < 0 ? text.Length : dot;
        var all = text.Replace(".", "", StringComparison.Ordinal);
        var leading = all.Length - all.TrimStart('0').Length;
        var digits = all.TrimStart('0').TrimEnd('0');
        // value = 0.DIGITS x 10^point
        var point = integerLength - leading + exponent;
        if (point < -3 || Math.Abs(value) > 18014398509481984.0)
        {
            var mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            var power = point - 1;
            return sign + mantissa + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString("00", Invariant);
        }
        if (point <= 0) return sign + "0." + new string('0', -point) + digits;
        if (digits.Length <= point) return sign + digits + new string('0', point - digits.Length) + ".0";
        return sign + digits[..point] + "." + digits[point..];
    }
}
