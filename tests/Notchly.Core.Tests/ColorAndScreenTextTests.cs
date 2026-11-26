// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream ClipboardFeatureTests (colour swatch cases, about lines 826-885), SwitcherModelFeatureTests (colour picker
// formatter, about 5209-5232; screen OCR and QR joins, about 5262-5334) and the outcome contracts of ScreenTextCompletionTests.
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class ColorAndScreenTextTests
{
    private static void ExpectColor(string text, ColorValue? expected)
    {
        var actual = ColorValue.Parse(text);
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.InRange(Math.Abs(actual.Value.Red - expected.Value.Red), 0, 0.002);
        Assert.InRange(Math.Abs(actual.Value.Green - expected.Value.Green), 0, 0.002);
        Assert.InRange(Math.Abs(actual.Value.Blue - expected.Value.Blue), 0, 0.002);
        Assert.InRange(Math.Abs(actual.Value.Alpha - expected.Value.Alpha), 0, 0.002);
    }

    [Fact]
    public void SixDigitHexReadsAsItsColor() => ExpectColor("#00BC7D", new ColorValue(0, 188 / 255.0, 125 / 255.0));

    [Fact]
    public void WhitespaceAndLowerCaseStillRead() => ExpectColor("  #ffffff\n", new ColorValue(1, 1, 1));

    [Fact]
    public void ThreeDigitHexExpandsEachDigit() => ExpectColor("#f80", new ColorValue(1, 136 / 255.0, 0));

    [Fact]
    public void EightDigitHexCarriesAlpha() => ExpectColor("#00000080", new ColorValue(0, 0, 0, 128 / 255.0));

    [Fact]
    public void FourDigitHexCarriesAlpha() => ExpectColor("#f008", new ColorValue(1, 0, 0, 136 / 255.0));

    [Fact]
    public void PickersRgbFormatReads() => ExpectColor("rgb(0, 188, 125)", new ColorValue(0, 188 / 255.0, 125 / 255.0));

    [Fact]
    public void SpaceSeparatedRgbaWithSlashAlphaReads() => ExpectColor("rgba(255 0 0 / 50%)", new ColorValue(1, 0, 0, 0.5));

    [Fact]
    public void PickersHslFormatConverts() => ExpectColor("hsl(120, 100%, 25%)", new ColorValue(0, 0.5, 0));

    [Fact]
    public void NegativeHueWraps() => ExpectColor("hsl(-120deg 100% 50%)", new ColorValue(0, 0, 1));

    [Fact]
    public void HslWrittenByThePickerReadsBack() =>
        ExpectColor(ColorValue.Format(0.2, 0.4, 0.6, null, ColorCopyFormat.Hsl), new ColorValue(0.2, 0.4, 0.6));

    [Fact]
    public void SwiftUiCodeWrittenByThePickerReadsBack() =>
        ExpectColor(ColorValue.Format(0.2, 0.4, 0.6, null, ColorCopyFormat.SwiftUi), new ColorValue(0.2, 0.4, 0.6));

    [Fact]
    public void CompactSwiftUiCodeReads() => ExpectColor("Color(red:0.2,green:0.4,blue:0.6)", new ColorValue(0.2, 0.4, 0.6));

    [Fact]
    public void SwiftUiLabelsReadWithAnySpacing() => ExpectColor("Color(red:1, green:0,blue: 0,opacity:0.5)", new ColorValue(1, 0, 0, 0.5));

    [Fact]
    public void SwiftUiOpacityReadsAsAlpha() => ExpectColor("Color(red: 1, green: 0, blue: 0, opacity: 0.5)", new ColorValue(1, 0, 0, 0.5));

    [Fact]
    public void FullPrecisionXcodeCodeReads() =>
        ExpectColor("Color(red: 0.2509803922, green: 0.5019607843, blue: 0.7529411765, opacity: 0.5)", new ColorValue(0.251, 0.502, 0.753, 0.5));

    [Theory]
    [InlineData("Color(red: 1, green: 0)")]
    [InlineData("Color(green: 0, red: 1, blue: 0)")]
    [InlineData("Color(red: 2, green: 0, blue: 0)")]
    [InlineData("Color(.red)")]
    [InlineData("Color(red:1,green:0,blue:0,)")]
    [InlineData("Color(red 1, green 0, blue 0)")]
    [InlineData("00BC7D")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#00BC7D is the brand green")]
    [InlineData("color: #00BC7D")]
    [InlineData("rgb(256, 0, 0)")]
    [InlineData("rgb(0, 0)")]
    [InlineData("rgb(0, 0, 0, 2)")]
    [InlineData("hsl(0, 50, 50%)")]
    [InlineData("rgb(nan, 0, 0)")]
    [InlineData("#")]
    [InlineData("")]
    public void NotALoneColorValue(string text) => ExpectColor(text, null);

    [Fact]
    public void AHundredSpacesPastTheCapIsNotAColor() => Assert.Null(ColorValue.Parse(new string(' ', 100) + "#fff"));

    // MARK: The picker's formatter

    [Fact]
    public void RedAsHex() => Assert.Equal("#FF0000", ColorValue.Format(1, 0, 0, null, ColorCopyFormat.Hex));

    [Fact]
    public void ComponentsAsCssRgb() => Assert.Equal("rgb(51, 102, 153)", ColorValue.Format(0.2, 0.4, 0.6, null, ColorCopyFormat.Rgb));

    [Fact]
    public void RedAsHsl() => Assert.Equal("hsl(0, 100%, 50%)", ColorValue.Format(1, 0, 0, null, ColorCopyFormat.Hsl));

    [Fact]
    public void DarkGreenAsHsl() => Assert.Equal("hsl(120, 100%, 25%)", ColorValue.Format(0, 0.5, 0, null, ColorCopyFormat.Hsl));

    [Fact]
    public void ComponentsAsSwiftUiCode() =>
        Assert.Equal("Color(red: 0.250, green: 0.500, blue: 0.750)", ColorValue.Format(0.25, 0.5, 0.75, null, ColorCopyFormat.SwiftUi));

    [Fact]
    public void ExtendedGamutComponentsAreClamped() => Assert.Equal("#FF0080", ColorValue.Format(1.4, -0.2, 0.5, null, ColorCopyFormat.Hex));

    [Fact]
    public void UnknownStoredFormatFallsBackToHex() => Assert.Equal(ColorCopyFormat.Hex, ColorCopyFormats.Sanitized("banana"));

    [Fact]
    public void BareHexDropsTheHash() => Assert.Equal("FF0000", ColorValue.Format(1, 0, 0, null, ColorCopyFormat.Hex, bareHex: true));

    [Fact]
    public void BareHexLeavesOtherFormatsAlone() =>
        Assert.Equal("rgb(51, 102, 153)", ColorValue.Format(0.2, 0.4, 0.6, null, ColorCopyFormat.Rgb, bareHex: true));

    [Fact]
    public void AlphaWritesTheAlphaForms()
    {
        Assert.Equal("rgba(0, 0, 0, 0.502)", ColorValue.Format(0, 0, 0, 128 / 255.0, ColorCopyFormat.Rgb));
        Assert.Equal("rgba(0, 0, 0, 0.004)", ColorValue.Format(0, 0, 0, 1 / 255.0, ColorCopyFormat.Rgb));
        Assert.Equal("rgba(0, 0, 0, 0.996)", ColorValue.Format(0, 0, 0, 254 / 255.0, ColorCopyFormat.Rgb));
        Assert.Equal("#00000080", ColorValue.Format(0, 0, 0, 128 / 255.0, ColorCopyFormat.Hex));
        Assert.Equal("hsla(0, 100%, 50%, 0.5)", ColorValue.Format(1, 0, 0, 0.5, ColorCopyFormat.Hsl));
    }

    [Fact]
    public void EveryEightBitAlphaComesBackThroughEveryAlphaForm()
    {
        foreach (var format in new[] { ColorCopyFormat.Hex, ColorCopyFormat.Rgb, ColorCopyFormat.Hsl, ColorCopyFormat.SwiftUi })
            for (var alpha = 0; alpha <= 255; alpha++)
            {
                var text = ColorValue.Format(0.2, 0.4, 0.6, alpha / 255.0, format);
                var read = ColorValue.Parse(text);
                Assert.NotNull(read);
                Assert.Equal(alpha, (int)Math.Round(read.Value.Alpha * 255, MidpointRounding.AwayFromZero));
            }
    }

    [Fact]
    public void FormatIsNotAffectedByTheCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Color(red: 0.250, green: 0.500, blue: 0.750)", ColorValue.Format(0.25, 0.5, 0.75, null, ColorCopyFormat.SwiftUi));
            Assert.Equal("rgba(0, 0, 0, 0.5)", ColorValue.Format(0, 0, 0, 0.5, ColorCopyFormat.Rgb));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    // MARK: Screen text

    // Upstream's lines use Vision's bottom-left origin (y 0.8, 0.81, 0.4); Windows reports from the top, so the same page is 1 - y.
    private static readonly RecognizedLine[] Lines =
    [
        new("world", 0.5, 0.2), new("hello", 0.1, 0.19), new("below", 0.1, 0.6), new("   ", 0.2, 0.4),
    ];

    [Fact]
    public void LinesJoinTopToBottomLeftToRightDroppingBlanks() =>
        Assert.Equal("hello\nworld\nbelow", ScreenTextSupport.JoinRecognizedText(Lines, removingLineBreaks: false));

    [Fact]
    public void LinesCanJoinWithSpaces() =>
        Assert.Equal("hello world below", ScreenTextSupport.JoinRecognizedText(Lines, removingLineBreaks: true));

    private static string Pair(string first, string second, bool remove) =>
        ScreenTextSupport.JoinRecognizedText([new(first, 0.1, 0.2), new(second, 0.1, 0.6)], remove);

    [Theory]
    [InlineData("这是", "测试", "这是测试")]
    [InlineData("これは", "テストです", "これはテストです")]
    [InlineData("これは", "ﾃｽﾄです", "これはﾃｽﾄです")]
    [InlineData("이것은", "테스트입니다", "이것은 테스트입니다")]
    [InlineData("ㅋㅋ", "ㅎㅎ", "ㅋㅋ ㅎㅎ")]
    [InlineData("version", "版本", "version 版本")]
    [InlineData("Hello ", " world", "Hello world")]
    [InlineData("这是 ", " 测试", "这是测试")]
    [InlineData("이것은 ", " 테스트", "이것은 테스트")]
    public void LineSeamsWhenBreaksAreRemoved(string first, string second, string expected) =>
        Assert.Equal(expected, Pair(first, second, true));

    [Fact]
    public void ChineseLineBreaksStayWhenRemovalIsOff() => Assert.Equal("这是\n测试", Pair("这是", "测试", false));

    [Fact]
    public void BoundaryWhitespaceStaysWhenRemovalIsOff() => Assert.Equal("这是 \n 测试", Pair("这是 ", " 测试", false));

    [Fact]
    public void NoLinesJoinToNothing() => Assert.Equal("", ScreenTextSupport.JoinRecognizedText([], false));

    [Fact]
    public void CodesJoinTopToBottomLeftToRightDroppingBlanks()
    {
        DecodedBarcode[] codes = [new("second", 0.6, 0.2), new("first", 0.1, 0.19), new("bottom", 0.1, 0.7), new("  ", 0.2, 0.5)];
        Assert.Equal("first\nsecond\nbottom", ScreenTextSupport.JoinBarcodePayloads(codes));
        Assert.Equal("", ScreenTextSupport.JoinBarcodePayloads([]));
    }

    [Fact]
    public void OnlyWebLinksAreOffered()
    {
        Assert.Equal("https://example.com/menu", ScreenTextSupport.OpenableUrl("https://example.com/menu")?.AbsoluteUri);
        Assert.Equal("example.com", ScreenTextSupport.OpenableUrl(" http://example.com ")?.Host);
        Assert.Null(ScreenTextSupport.OpenableUrl("WIFI:S:Net;T:WPA;P:secret;;"));
        Assert.Null(ScreenTextSupport.OpenableUrl("mailto:a@b.com"));
        Assert.Null(ScreenTextSupport.OpenableUrl("just some text"));
        Assert.Null(ScreenTextSupport.OpenableUrl("example.com"));
        Assert.Null(ScreenTextSupport.OpenableUrl("file:///C:/Windows/notepad.exe"));
    }

    [Fact]
    public void ACodeWinsOverText()
    {
        Assert.Equal(new ScreenTextOutcome(ScreenTextKind.Qr, "https://x.example"), ScreenTextSupport.Outcome("https://x.example", "some text"));
        Assert.Equal(new ScreenTextOutcome(ScreenTextKind.Text, "some text"), ScreenTextSupport.Outcome(null, "some text"));
        Assert.Equal(ScreenTextKind.Empty, ScreenTextSupport.Outcome("  ", "\n").Kind);
    }

    [Fact]
    public void OnlyTheNewestRequestWritesAndNeverOverANewerCopy()
    {
        Assert.True(ScreenTextSupport.MayWrite(3, 3, true, 10, 10));
        Assert.False(ScreenTextSupport.MayWrite(2, 3, true, 10, 10));
        Assert.False(ScreenTextSupport.MayWrite(3, 3, false, 10, 10));
        Assert.False(ScreenTextSupport.MayWrite(3, 3, true, 10, 11));
    }
}
