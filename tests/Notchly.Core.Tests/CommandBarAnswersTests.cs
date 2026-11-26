// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/CommandBarFeatureTests.swift: the command bar calculator (about lines 302-499), colour conversion
// (1294-1346), unit conversion (1348-1621) and dates (2277-2323). Checks that grep Swift source or need the service are not ported.
using System.Globalization;
using Notchly.Core.CommandBar;
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class CommandBarAnswersTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo BrazilianPortuguese = CultureInfo.GetCultureInfo("pt-BR");

    // MARK: Calculator

    private static string? Math(string input, string decimalSeparator = ".", string groupingSeparator = ",") =>
        CommandBarMath.Evaluate(input, decimalSeparator, groupingSeparator, English)?.Formatted;

    private static double? MathValue(string input, string decimalSeparator = ".", string groupingSeparator = ",") =>
        CommandBarMath.Evaluate(input, decimalSeparator, groupingSeparator, English)?.Value;

    [Fact]
    public void TheCalculatorAnswersEverydaySums()
    {
        Assert.Equal("4", Math("2+2"));
        Assert.Equal("45", Math("10 * 4.5"));
        Assert.Equal("20", Math("(2+3)*4"));
        Assert.Equal("14", Math("2+3*4"));
        Assert.Equal("2.5", Math("10/4"));
        Assert.Equal("512", Math("2^3^2"));
        Assert.Equal("-3", Math("-5+2"));
        Assert.Equal("6", Math("--5+1"));
        Assert.Equal("960", Math("1920/2"));
        Assert.True(MathValue("0.1+0.2") == 0.3 && Math("0.1+0.2") == "0.3", "floating point noise never reaches the eye");
        Assert.Equal("1,001", Math("1,000+1"));
        Assert.True(Math("2 x 3") == "6" && Math("10 ÷ 2") == "5" && Math("2 × 3") == "6");
        Assert.True(Math("2(3+4)") == "14" && Math("(1+2)(3+4)") == "21", "a number against a parenthesis multiplies");
        Assert.Equal("-4", Math("-2^2"));
        Assert.True(Math("2^0.5")?.StartsWith("1.41421") == true);
        Assert.Equal("1e-9", Math("1/1000000000"));
        Assert.True(Math("2^100")?.Contains('e') == true, "a huge answer switches notation instead of vanishing");
        Assert.Equal("10", Math("7+3="));
    }

    [Fact]
    public void DatesTimesAndAssignmentsAreNeverSums()
    {
        Assert.True(Math("2026-07-27") is null && Math("27/07/2026") is null && Math("10:30") is null);
        Assert.Equal("50", Math("100-50"));
        Assert.True(Math("100 - 20 - 30") == "50" && Math("2 - 1 - 1") == "0" && Math("8 / 2 / 2") == "2");
        Assert.True(Math("10-5-3") is null && Math("8/2/2") is null && Math("10 : 30") is null);
        Assert.True(Math("SDL_VIDEODRIVER=") is null && Math("x=5") is null);
    }

    [Fact]
    public void PercentagesReadAsTheyAreSaid()
    {
        Assert.Equal("552", Math("480+15%"));
        Assert.Equal("408", Math("480-15%"));
        Assert.Equal("96", Math("20% of 480"));
        Assert.Equal("96", Math("20% de 480"));
        Assert.Null(Math("50%"));
        Assert.Equal("20", Math("200*10%"));
    }

    [Fact]
    public void SearchesAndCommandsStaySearches()
    {
        Assert.Null(Math("5"));
        Assert.Null(Math("hello"));
        Assert.Null(Math("1password"));
        Assert.Null(Math("volume 20"));
        Assert.Null(Math("brilho 40"));
        Assert.Null(Math("10/0"));
        Assert.True(Math("2+") is null && Math("(2+3") == "5" && Math("2++") is null);
        Assert.Equal("-6", Math("2 * -3"));
        Assert.Null(Math(new string('(', 60) + "1" + new string(')', 60)));
        Assert.Null(Math("e-mail"));
    }

    [Fact]
    public void LargeNumbersKeepEveryDigitTheyPrint()
    {
        Assert.True(Math("9999999999999999*99")?.Contains('e') == true);
        Assert.True(Math("12345678901+1") == "12,345,678,902" && Math("987654321098+1") == "987,654,321,099"
                    && Math("2^39") == "549,755,813,888" && Math("10000000000+0.5") == "10,000,000,000.5"
                    && Math("1234567890.25+0") == "1,234,567,890.25");
    }

    private static string? Reusable(string expression, string decimalSeparator = ".") =>
        CommandBarMath.Evaluate(expression, ".", ",", English) is { } result
            ? CommandBarMath.ReusableExpression(result, decimalSeparator)
            : null;

    [Fact]
    public void ReusingAnAnswerKeepsItsDigitsWithoutBinaryNoise()
    {
        Assert.Equal("0.1", Reusable("100000.1-100000"));
        Assert.Equal("1234567890124", Reusable("1234567890123+1"));
        Assert.Equal("7.20575940379279e+16", Reusable("2^56"));
    }

    [Fact]
    public void AnswersFollowTheLocaleTheyAreWrittenIn()
    {
        Assert.Equal("1.235,5", CommandBarMath.Evaluate("1.234,5 + 1", ",", ".", BrazilianPortuguese)?.Formatted);
        Assert.Equal(3, CommandBarMath.Evaluate("1,5*2", ",", ".", BrazilianPortuguese)?.Value);
        Assert.Equal("1,501", Math("1.500+1", ",", "."));
        // The culture's own separators are the default.
        Assert.Equal("1.235,5", CommandBarMath.Evaluate("1.234,5 + 1", BrazilianPortuguese)?.Formatted);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" ")]
    public void SpaceGroupedLocalesStillAcceptDotsAndCommas(string grouping)
    {
        Assert.True(MathValue("1.5+1", ",", grouping) == 2.5 && MathValue("0.1+0.2", ",", grouping) == 0.3);
        Assert.True(MathValue("1.500+1", ",", grouping) == 1501 && MathValue("1.234,5+1", ",", grouping) == 1235.5
                    && MathValue("1,234.5+1", ",", grouping) == 1235.5);
        Assert.Equal(1235.5, MathValue($"1{grouping}234,5+1", ",", grouping));
        Assert.Null(MathValue($"1{grouping}5+1", ",", grouping));
        Assert.True(MathValue($"1{grouping}234,5{grouping}67+1", ",", grouping) is null
                    && MathValue($"1{grouping}234.5{grouping}67+1", ",", grouping) is null);
    }

    [Fact]
    public void ApostropheGroupedLocalesAcceptACommaDecimal()
    {
        Assert.True(MathValue("1,5+1", ".", "'") == 2.5 && MathValue("1,234.5+1", ".", "'") == 1235.5
                    && MathValue("1'234.5+1", ".", "'") == 1235.5);
        Assert.True(MathValue("1'234.5'67+1", ".", "'") is null && MathValue("1'234,5'67+1", ".", "'") is null);
        Assert.Null(MathValue("1.2.3+1", ",", " "));
    }

    [Fact]
    public void AGroupingSpaceOrApostropheGroupsOnlyWhenDigitsFollow()
    {
        Assert.Equal(3, MathValue("1 + 2", ",", " "));
        Assert.Equal(30, MathValue("200*15 %", ",", " "));
        Assert.Equal(6, MathValue("2 (3)", ",", " "));
        Assert.Equal(3.5, MathValue("1,5 + 2", ",", " "));
        Assert.Equal(1235, MathValue("1 234 + 1", ",", " "));
        Assert.Null(MathValue("1 5+1", ",", " "));
    }

    [Theory]
    [InlineData(",", " ")]
    [InlineData(",", " ")]
    [InlineData(".", "'")]
    [InlineData(",", ".")]
    [InlineData(".", ",")]
    public void ThousandsNeverStartAtZero(string decimalSeparator, string grouping)
    {
        var alternate = decimalSeparator == "," ? "." : ",";
        Assert.True(MathValue($"0{alternate}125*8", decimalSeparator, grouping) == 1
                    && MathValue($"0{alternate}500+1", decimalSeparator, grouping) == 1.5);
    }

    [Fact]
    public void AGroupingSpaceAfterALoneZeroIsNotThousands() =>
        Assert.Null(MathValue("0 125+1", ",", " "));

    [Fact]
    public void MissingClosingBracketsAreSuppliedVirtually()
    {
        Assert.True(CommandBarMath.Evaluate("([2+3", English)?.ClosingBrackets == "])" && CommandBarMath.Evaluate("2+3", English)?.ClosingBrackets == "");
        Assert.True(MathValue("sqrt(81") == 9 && MathValue("2*(3+[4") == 14);
    }

    [Theory]
    [InlineData("(2+3]")]
    [InlineData("2+3)")]
    [InlineData("2*(3+")]
    [InlineData("sqrt(")]
    [InlineData("sin2")]
    [InlineData("log100(2)")]
    [InlineData("sqrt(-1)")]
    [InlineData("log(0)")]
    [InlineData("acos(2)")]
    [InlineData("1e309+0")]
    [InlineData("7=+3")]
    [InlineData("7+3==")]
    public void InvalidCalculatorInputHasNeitherAnAnswerNorGhostBrackets(string expression)
    {
        Assert.Null(MathValue(expression));
        Assert.Null(CommandBarMath.Evaluate(expression, English)?.ClosingBrackets);
    }

    [Theory]
    [InlineData("sqrt(9)+abs(-3)", 6.0)]
    [InlineData("sin(pi/2)+cos(0)+tan(0)", 2.0)]
    [InlineData("asin(1)+acos(1)+atan(1)", System.Math.PI * 0.75)]
    [InlineData("ln(exp(1))+log(100)+log10(100)", 5.0)]
    [InlineData("floor(1.9)+ceil(1.1)+round(1.5)", 5.0)]
    [InlineData("2pi", 2 * System.Math.PI)]
    [InlineData("π+e", System.Math.PI + 2.718281828459045)]
    [InlineData("2x3+2 x 4", 14.0)]
    [InlineData("2(3)+(2)(3)+2[3]", 18.0)]
    [InlineData("1e-9*1e9", 1.0)]
    public void TheScientificCalculatorEvaluates(string expression, double expected)
    {
        var value = MathValue(expression);
        Assert.True(value is { } v && System.Math.Abs(v - expected) < 1e-7, $"{expression} gave {value}");
    }

    [Fact]
    public void ScientificMantissasRespectDecimalCommaLocales() =>
        Assert.Equal(0.003, MathValue("1,5e-3*2", ",", "."));

    [Theory]
    [InlineData("sin(1e-13)*1e13", 1.0)]
    [InlineData("tan(1e-13)*1e13", 1.0)]
    [InlineData("sin(-1e-13)*1e13", -1.0)]
    [InlineData("1/sin(1e-13)", 1e13)]
    [InlineData("cos(pi/2+1e-13)/cos(pi/2+1e-13)", 1.0)]
    public void SmallTrigonometricValuesRemainAvailableToTheRestOfTheCalculation(string expression, double expected) =>
        Assert.Equal(expected, MathValue(expression));

    [Theory]
    [InlineData("0.1+0.2")]
    [InlineData("1/3")]
    [InlineData("-2^2")]
    [InlineData("1e-9+0")]
    [InlineData("2^100")]
    public void AReusedAnswerKeepsItsStoredValue(string expression)
    {
        var result = CommandBarMath.Evaluate(expression, ".", ",", English);
        Assert.NotNull(result);
        var reusable = CommandBarMath.ReusableExpression(result, ".");
        Assert.Equal(result.Value, MathValue(reusable + "+0"));
        if (result.Value == 0.3) Assert.Equal("0.3", reusable);
        if (result.Value == -4) Assert.Equal(16, MathValue(reusable + "^2"));
        var comma = CommandBarMath.ReusableExpression(result, ",");
        Assert.Equal(result.Value, MathValue(comma + "+0", ",", "."));
    }

    // MARK: Colors

    [Theory]
    [InlineData("#a2b3b4 to rgb", "rgb(162, 179, 180)")]
    [InlineData("#A2B3B4 in HSL", "hsl(183, 11%, 67%)")]
    [InlineData("rgb(0, 188, 125) to hex", "#00BC7D")]
    [InlineData("rgb(255 0 0) as hsl", "hsl(0, 100%, 50%)")]
    [InlineData("hsl(120, 100%, 25%) to rgb", "rgb(0, 128, 0)")]
    [InlineData("#fff to rgba", "rgba(255, 255, 255, 1)")]
    [InlineData("#fff to hsla", "hsla(0, 0%, 100%, 1)")]
    [InlineData("rgba(255, 0, 0, 0.5) to hex", "#FF000080")]
    [InlineData("#00000080 to rgb", "rgba(0, 0, 0, 0.502)")]
    [InlineData("hsla(0, 100%, 50%, 25%) to hsl", "hsla(0, 100%, 50%, 0.25)")]
    [InlineData("#f80 nach rgb", "rgb(255, 136, 0)")]
    [InlineData("#336699 to swift", "Color(red: 0.200, green: 0.400, blue: 0.600)")]
    [InlineData("rgba(255, 0, 0, 0.5) to SwiftUI", "Color(red: 1.000, green: 0.000, blue: 0.000, opacity: 0.500)")]
    [InlineData("Color(red: 0.200, green: 0.400, blue: 0.600) to hex", "#336699")]
    [InlineData("Color(red: 1.000, green: 0.000, blue: 0.000, opacity: 0.500) to rgb", "rgba(255, 0, 0, 0.5)")]
    [InlineData("Color(red:0.2,green:0.4,blue:0.6) to hex", "#336699")]
    [InlineData("#00000001 to rgba", "rgba(0, 0, 0, 0.004)")]
    [InlineData("#000000fe to rgba", "rgba(0, 0, 0, 0.996)")]
    public void AColourConverts(string input, string expected) =>
        Assert.Equal(expected, CommandBarColors.Convert(input)?.Formatted);

    [Fact]
    public void NonConversionsStayASearch()
    {
        foreach (var input in new[]
                 {
                     "#a2b3b4", "#a2b3b4 to", "#a2b3b4 rgb", "#a2b3b4 to cmyk", "a2b3b4 to rgb", "brand #a2b3b4 to rgb",
                     "rgb(300, 0, 0) to hex", "5 km to mi", new string(' ', 110) + "#fff to rgb",
                 })
            Assert.Null(CommandBarColors.Convert(input));
    }

    [Fact]
    public void EveryEightBitAlphaSurvivesATripThroughRgbaHslaAndSwiftUi()
    {
        var losses = new List<string>();
        for (var alphaByte = 0; alphaByte <= 255; alphaByte++)
        {
            var hex = $"#336699{alphaByte:X2}";
            foreach (var target in new[] { "rgba", "hsla", "swift" })
            {
                var there = CommandBarColors.Convert(hex + " to " + target)?.Formatted;
                var back = there is null ? null : CommandBarColors.Convert(there + " to hex")?.Formatted;
                // An opaque colour comes back without the alpha pair.
                var expected = alphaByte == 255 ? "#336699" : hex;
                if (back != expected) losses.Add($"{hex} -> {there ?? "nil"} -> {back ?? "nil"}");
            }
        }
        Assert.True(losses.Count == 0, string.Join("; ", losses.Take(4)));
    }

    [Fact]
    public void AlphaIsWrittenOnlyWhenAskedFor()
    {
        Assert.Equal("#FF0000", ColorValue.Format(1, 0, 0, null, ColorCopyFormat.Hex));
        Assert.Equal("Color(red: 1.000, green: 0.000, blue: 0.000, opacity: 0.500)", ColorValue.Format(1, 0, 0, 0.5, ColorCopyFormat.SwiftUi));
    }

    // MARK: Units

    private static string? Units(string input) => CommandBarUnits.Convert(input, ".", ",", English)?.Formatted;
    private static double? UnitValue(string input) => CommandBarUnits.Convert(input, ".", ",", English)?.Value;

    private static void AssertUnit(string input, double expected, double tolerance = 0.001)
    {
        var value = UnitValue(input);
        Assert.True(value is { } v && System.Math.Abs(v - expected) < tolerance, $"{input} gave {value}, wanted {expected}");
    }

    [Theory]
    [InlineData("100 km to mi", 62.1371)]
    [InlineData("20 c to f", 68)]
    [InlineData("0 c to f", 32)]
    [InlineData("5 gb to mb", 5000)]
    [InlineData("1 gib to mib", 1024)]
    [InlineData("100km to mi", 62.1371)]
    [InlineData("5 in to cm", 12.7)]
    public void EverydayConversions(string input, double expected) => AssertUnit(input, expected);

    [Theory]
    [InlineData("5ft 11 in cm")]
    [InlineData("5ft 11in in cm")]
    [InlineData("5ft11in to cm")]
    [InlineData("5 ft 11 in to cm")]
    [InlineData("5 feet 11 inches to cm")]
    [InlineData("5'11\" in cm")]
    [InlineData("5′ 11″ in cm")]
    [InlineData("5’11” in cm")]
    public void FiveFeetElevenInchesConverts(string query) => AssertUnit(query, 180.34);

    [Theory]
    [InlineData("5' in cm", 152.4)]
    [InlineData("5′ in cm", 152.4)]
    [InlineData("11in in cm", 27.94)]
    [InlineData("11″ in cm", 27.94)]
    [InlineData("-5ft 11in to in", -71)]
    [InlineData("-0′6″ to in", -6)]
    [InlineData("5.5ft to in", 66)]
    [InlineData("0ft 0in to cm", 0)]
    public void HeightShorthandConverts(string query, double expected) => AssertUnit(query, expected);

    [Fact]
    public void MixedLengthsUseTheSameLocalizedNamesAndDecimalParser()
    {
        var result = CommandBarUnits.Convert("5 pés 11,5 polegadas em cm", ",", ".", BrazilianPortuguese);
        Assert.True(result is not null && System.Math.Abs(result.Value - 181.61) < 0.001);
    }

    [Theory]
    [InlineData("5ft 11in")]
    [InlineData("5ft 11in cm")]
    [InlineData("5ft 11in in kg")]
    [InlineData("5in 11ft in cm")]
    [InlineData("5m 11 in cm")]
    [InlineData("5ft 11cm in cm")]
    [InlineData("5ft 11in 1in in cm")]
    [InlineData("5ft 11in in cm today")]
    [InlineData("5.5ft 1in to cm")]
    [InlineData("5ft -1in to cm")]
    [InlineData("5ft 12in to cm")]
    [InlineData("1'003 in cm")]
    public void MalformedHeightsStayASearch(string query) => Assert.Null(Units(query));

    [Fact]
    public void AnAttachedInchMarkCannotReplaceTheConnector()
    {
        Assert.Null(Units("5ft 11in cm"));
        Assert.NotNull(Units("5 in to cm"));
        // A height reads as the same answer as the equivalent inches.
        var title = Units("71in in cm");
        Assert.NotNull(title);
        foreach (var query in new[] { "5ft 11 in cm", "5ft 11in in cm", "5'11\" in cm" })
            Assert.Equal(title, CommandBarAnswers.TryAnswer(query, English, Now, TimeZoneInfo.Utc)?.Title);
    }

    [Theory]
    [InlineData("2 h to min", 120)]
    [InlineData("1d to hr", 24)]
    [InlineData("2 weeks to days", 14)]
    [InlineData("1 year to min", 525_600)]
    [InlineData("1 yr to d", 365)]
    [InlineData("3 wk to d", 21)]
    [InlineData("24 h to d", 1)]
    [InlineData("14 d to wk", 2)]
    [InlineData("365 d to yr", 1)]
    [InlineData("2 wks to days", 14)]
    [InlineData("14 d to wks", 2)]
    [InlineData("2 yrs to d", 730)]
    [InlineData("730 d to yrs", 2)]
    [InlineData("2 weeks in days", 14)]
    [InlineData("1d → hr", 24)]
    [InlineData("0.5 day to h", 12)]
    [InlineData("-1 d to h", -24)]
    [InlineData("1 dia to h", 24)]
    [InlineData("1 week to d", 7)]
    [InlineData("1 semana to d", 7)]
    [InlineData("2 years to d", 730)]
    [InlineData("2 anos to d", 730)]
    [InlineData("1 wk to hr", 168)]
    [InlineData("168 hr to wk", 1)]
    [InlineData("1 d to min", 1440)]
    [InlineData("1 d to s", 86_400)]
    [InlineData("1 yr to s", 31_536_000)]
    [InlineData("1 kg to lb", 2.20462)]
    [InlineData("1.5 l to ml", 1500)]
    public void DurationsMassAndVolumeConvert(string input, double expected) => AssertUnit(input, expected);

    [Theory]
    [InlineData("2 semanas para dias", 14)]
    [InlineData("1 ano para dias", 365)]
    [InlineData("365 dias para anos", 1)]
    [InlineData("3 dias para horas", 72)]
    [InlineData("1,5 dia para horas", 36)]
    [InlineData("1,5 m to cm", 150)]
    public void PortugueseUnitsConvertWithCommaDecimals(string input, double expected)
    {
        var result = CommandBarUnits.Convert(input, ",", ".", BrazilianPortuguese);
        Assert.True(result is not null && System.Math.Abs(result.Value - expected) < 0.001, $"{input} gave {result?.Value}");
    }

    [Fact]
    public void CustomDurationUnitsFormatSuccessfully()
    {
        Assert.True(Units("1d to hr") == Units("24 hr to hr") && Units("24 hr to d") == "1 d" && Units("14 d to wk") == "2 wk"
                    && Units("1 yr to d") == "365 d" && Units("365 d to yr") == "1 yr");
    }

    [Fact]
    public void DifferentFamiliesNeverMeet()
    {
        Assert.Null(Units("100 km to kg"));
        Assert.True(Units("1 day to kg") is null && Units("1 year to m") is null);
    }

    [Fact]
    public void BritishSpellingsConvertLikeTheAmericanOnes()
    {
        Assert.NotNull(Units("100 kilometres to miles"));
        Assert.True(Units("100 kilometres to miles") == Units("100 kilometers to miles")
                    && Units("5 centimetres to inches") == Units("5 cm to in")
                    && Units("3 millimetres to inches") == Units("3 mm to in")
                    && Units("500 millilitres to cups") == Units("500 ml to cups")
                    && Units("1 kilometre to miles") == Units("1 kilometer to miles")
                    && Units("1 centimetre to inches") == Units("1 cm to in")
                    && Units("1 millimetre to inches") == Units("1 mm to in")
                    && Units("1 millilitre to cups") == Units("1 ml to cups"));
    }

    [Fact]
    public void FluidOuncesWrittenAsTwoWordsConvert()
    {
        Assert.NotNull(Units("8 fl oz to ml"));
        AssertUnit("5fl oz in ml", 147.8675);
        Assert.True(Units("8 fl oz to ml") == Units("8 floz to ml") && Units("8 fluid ounces to ml") == Units("8 floz to ml")
                    && Units("1 fluid ounce to ml") == Units("1 floz to ml") && Units("236 ml to fl oz") == Units("236 ml to floz"));
    }

    [Fact]
    public void WithoutTheWordOrAUnitThereIsNoConversion()
    {
        Assert.Null(Units("100 km"));
        Assert.True(Units("to") is null && Units("in") is null);
        Assert.Null(Units("safari to dock"));
        Assert.True(Units("day to night") is null && Units("year to date") is null && Units("day to day") is null && Units("week to week") is null);
        Assert.Null(Units("5 xyz to cm"));
        Assert.Null(Units("minutes to read the article"));
    }

    // A first group of 0 is never thousands, so "0,250" is a quarter where the dot is decimal, while "1,050" still groups.
    private static readonly (string Number, double Expected)[] DotDecimalInputs =
    [
        ("1.5", 150.0), ("1,5", 150.0), ("-1,5", -150.0), ("1,500", 150_000.0), ("-123,456", -12_345_600.0), ("1,234.5", 123_450.0),
        ("1.234,5", 123_450.0), ("1,234,567", 123_456_700.0), ("0,250", 25.0), ("-0,500", -50.0), ("1,050", 105_000.0),
    ];

    private static readonly (string Number, double Expected)[] CommaDecimalInputs =
    [
        ("1,5", 150.0), ("1.5", 150.0), ("-1.5", -150.0), ("1.500", 150_000.0), ("-123.456", -12_345_600.0), ("1.234,5", 123_450.0),
        ("1,234.5", 123_450.0), ("1.234.567", 123_456_700.0), ("0.250", 25.0), ("-0.500", -50.0), ("1.050", 105_000.0),
    ];

    // de_CH groups thousands with an apostrophe, pt_PT with a no-break space and fr_FR with a narrow one. There the alternate is
    // whichever of "." and "," is not the decimal, the same as in the calculator.
    [Theory]
    [InlineData("en_US", ".", ",", true)]
    [InlineData("de_CH", ".", "'", true)]
    [InlineData("de_DE", ",", ".", false)]
    [InlineData("pt_PT", ",", " ", false)]
    [InlineData("fr_FR", ",", " ", false)]
    public void NumbersAreReadTheWayEachRegionWritesThem(string region, string decimalSeparator, string grouping, bool dotDecimal)
    {
        foreach (var (number, expected) in dotDecimal ? DotDecimalInputs : CommaDecimalInputs)
        {
            var converted = CommandBarUnits.Convert($"{number} m to cm", decimalSeparator, grouping, English);
            Assert.True(converted is not null && System.Math.Abs(converted.Value - expected) < 0.001,
                        $"unit conversion in {region} reads {number} as {expected} cm, got {converted?.Value}");
        }
        foreach (var number in new[] { "1,,5", "1..5", "--1", "1-5" })
            Assert.Null(CommandBarUnits.Convert($"{number} m to cm", decimalSeparator, grouping, English));
    }

    // What this decides is the split into whole feet and leftover inches, and the number format; the unit words are the symbol table's.
    private static string[] UnitNumbers(string? text) =>
        System.Text.RegularExpressions.Regex.Split(text ?? "", "[^0-9.,-]").Where(part => part.Any(char.IsAsciiDigit)).ToArray();

    [Fact]
    public void FeetAndInchesKeepPreciseFeetAndInches()
    {
        Assert.Equal(["5", "10.87"], UnitNumbers(Units("180 cm to ft")));
        Assert.Equal(["5", "8.9"], UnitNumbers(Units("1.75 m to ft")));
        Assert.Equal(["6"], UnitNumbers(Units("6 ft to ft")));
        Assert.Equal(["6"], UnitNumbers(Units("5.9999 ft to ft")));
        Assert.Equal(["0.0656"], UnitNumbers(Units("2 cm to ft")));
        Assert.Equal(["-5.91"], UnitNumbers(Units("-180 cm to ft")));
        Assert.Equal(["70.87"], UnitNumbers(Units("180 cm to in")));
        Assert.Equal(["5", "10,87"],
                     UnitNumbers(CommandBarUnits.Convert("180 cm para pes", ",", ".", BrazilianPortuguese)?.Formatted));
    }

    [Theory]
    [InlineData("1 mm to km", 1e-6)]
    [InlineData("1 mg to t", 1e-9)]
    [InlineData("3 ms to h", 3 / 3_600_000.0)]
    public void ATinyNumberIsNotZero(string input, double expected)
    {
        var shown = Units(input)?.Split(' ')[0];
        Assert.True(shown is not null && double.TryParse(shown, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && System.Math.Abs(number - expected) <= expected * 0.01, $"{input} shows {expected}, not {Units(input)}");
    }

    [Fact]
    public void ZeroReadsAsPlainZero() => Assert.Equal(["0"], UnitNumbers(Units("0 mm to km")));

    [Theory]
    [InlineData("32 f to c")]
    [InlineData("-459.67 f to k")]
    [InlineData("273.15 k to c")]
    public void TemperatureOffsetNoiseRoundsBackToZero(string input)
    {
        var numbers = UnitNumbers(Units(input));
        Assert.True(numbers.Length == 1 && double.Parse(numbers[0], CultureInfo.InvariantCulture) == 0, $"{input} gave {Units(input)}");
    }

    [Fact]
    public void SmallTemperaturesKeepFourDecimals() => Assert.Equal(["0.0001"], UnitNumbers(Units("0.0001 c to c")));

    // MARK: Dates

    // 2026-07-28, a Tuesday.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_785_240_000);

    private static string? Dated(string input, CultureInfo? culture = null) =>
        CommandBarDates.Evaluate(input, Now, TimeZoneInfo.Utc, culture ?? English)?.Formatted;

    [Fact]
    public void RelativeDatesAreAnswered()
    {
        Assert.Equal("August 18, 2026", Dated("in 3 weeks"));
        Assert.Equal("7 de agosto de 2026", Dated("daqui 10 dias", BrazilianPortuguese));
        Assert.True(Dated("3 days ago") == "July 25, 2026" && Dated("ha 3 dias") == "July 25, 2026");
        Assert.True(Dated("today + 10 days") == "August 7, 2026" && Dated("today - 10 days") == "July 18, 2026");
        Assert.True(Dated("in -9223372036854775808 days") is null && Dated("-9223372036854775808 days ago") is null);
        Assert.True(Dated("3 days") is null && Dated("2+2") is null && Dated("100 km to mi") is null);
        Assert.Equal("Tuesday", CommandBarDates.Evaluate("in 3 weeks", Now, TimeZoneInfo.Utc, English)?.Detail);
    }

    [Fact]
    public void HowFarAWrittenDateIsIsCountedInWholeDays()
    {
        Assert.True(Dated("days until 12/25")?.Contains("150") == true);
        // 581 days from 28 July 2026 is 29 February 2028; 216 would be 1 March 2027.
        Assert.Equal("581 days", Dated("days until 2/29"));
    }

    [Fact]
    public void TheClockSomewhereElseComesFromTheTimeZoneDatabase()
    {
        Assert.True(CommandBarDates.Evaluate("time in tokyo", Now, TimeZoneInfo.Utc, English)?.Detail.StartsWith("Tokyo") == true);
        Assert.True(CommandBarDates.Evaluate("hora em londres", Now, TimeZoneInfo.Utc, English)?.Detail.StartsWith("London") == true);
        Assert.Null(CommandBarDates.Evaluate("time", Now, TimeZoneInfo.Utc, English));
    }

    [Theory]
    [InlineData("1password")]
    [InlineData("2 monitors")]
    [InlineData("3 tags")]
    [InlineData("notes")]
    [InlineData("day one")]
    [InlineData("5 minutes")]
    [InlineData("2026-07-28")]
    [InlineData("the 3 body problem")]
    public void AnythingASearchMightBeIsNotADate(string innocent) =>
        Assert.Null(CommandBarDates.Evaluate(innocent, Now, TimeZoneInfo.Utc, English));

    // MARK: The facade picks the answer row

    [Fact]
    public void TheFacadePicksTheAnswerTheWayUpstreamDoes()
    {
        var sum = CommandBarAnswers.TryAnswer("(2+3", English, Now, TimeZoneInfo.Utc);
        Assert.Equal(new CommandBarAnswer(CommandBarAnswerKind.Math, "5", "(2+3)", "5"), sum);
        Assert.Equal(CommandBarAnswerKind.Units, CommandBarAnswers.TryAnswer("100 km to mi", English, Now, TimeZoneInfo.Utc)?.Kind);
        var converted = CommandBarAnswers.TryAnswer("#a2b3b4 to rgb", English, Now, TimeZoneInfo.Utc);
        Assert.True(converted is { Kind: CommandBarAnswerKind.Color, CopyText: "rgb(162, 179, 180)", Color: not null });
        var preview = CommandBarAnswers.TryAnswer("  #cafe ", English, Now, TimeZoneInfo.Utc);
        Assert.True(preview is { Kind: CommandBarAnswerKind.Color, Title: "#cafe", CopyText: "#cafe" });
        var date = CommandBarAnswers.TryAnswer("in 3 weeks", English, Now, TimeZoneInfo.Utc);
        Assert.True(date is { Kind: CommandBarAnswerKind.Date, Title: "August 18, 2026", Detail: "Tuesday" });
        Assert.Null(CommandBarAnswers.TryAnswer("3", English, Now, TimeZoneInfo.Utc));
        Assert.Null(CommandBarAnswers.TryAnswer("safari", English, Now, TimeZoneInfo.Utc));
    }
}
