// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the backdrop and watermark checks of upstream ScreenshotFeatureTests (~1587-1594, 2870-3240: padding, corner, blur, style codec,
// presets, transparentCornerRadius, watermark style, font size, image width, placement, the picture watermark square), of
// ScreenshotWatermarkTests (the portable backup and the rounded placement) and of the renderExport geometry (size, 1x downscale).
// Drawing, shadows and pixel colours need CoreGraphics upstream and are the WPF renderer's job.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class ScreenshotBeautifyTests
{
    private static BackdropStyle Solid => new()
    {
        Kind = BackdropKind.Solid, Colors = [[0.2, 0.4, 0.9]], Padding = 0.3, CornerRadius = 0.2, Blur = 0.6,
    };

    // MARK: - Presets and geometry

    [Fact]
    public void EveryGradientHasItsTwoStopsAndNoneHasNone()
    {
        foreach (var id in Enum.GetValues<BackdropId>().Where(id => id != BackdropId.None))
            Assert.Equal(2, BackdropIds.Stops(id).Count);
        Assert.Empty(BackdropIds.Stops(BackdropId.None));
    }

    [Fact]
    public void TheFivePresetsKeepUpstreamsColours()
    {
        Assert.Equal((0.20, 0.47, 0.96), BackdropIds.Stops(BackdropId.Ocean)[0]);
        Assert.Equal((0.45, 0.83, 0.98), BackdropIds.Stops(BackdropId.Ocean)[1]);
        Assert.Equal((0.99, 0.36, 0.42), BackdropIds.Stops(BackdropId.Sunset)[0]);
        Assert.Equal((0.62, 0.87, 0.50), BackdropIds.Stops(BackdropId.Forest)[1]);
        Assert.Equal((0.66, 0.32, 0.95), BackdropIds.Stops(BackdropId.Candy)[0]);
        Assert.Equal((0.55, 0.60, 0.70), BackdropIds.Stops(BackdropId.Graphite)[1]);
    }

    [Fact]
    public void PresetIdsParseByTheirRawNames()
    {
        Assert.Equal(BackdropId.Ocean, BackdropIds.Parse("ocean"));
        Assert.Equal(BackdropId.None, BackdropIds.Parse("none"));
        Assert.Null(BackdropIds.Parse("bogus"));
        Assert.Null(BackdropIds.Parse(null));
        Assert.Equal("graphite", BackdropIds.Raw(BackdropId.Graphite));
    }

    [Fact]
    public void BackdropPaddingKeepsAFloorForTinyCaptures() =>
        Assert.Equal(24, BackdropGeometry.Padding(new SizeD(100, 100), 0.5));

    [Fact]
    public void TheMarginSliderGrowsThePadding() =>
        Assert.Equal(Math.Round(4000 * 0.175), BackdropGeometry.Padding(new SizeD(4000, 4000), 1));

    [Fact]
    public void MarginZeroStillKeepsASmallFrame() =>
        Assert.Equal(140, BackdropGeometry.Padding(new SizeD(4000, 4000), 0));

    [Fact]
    public void PaddingFollowsTheShortSide() =>
        Assert.Equal(Math.Round(900 * (0.035 + 0.14 * 0.5)), BackdropGeometry.Padding(new SizeD(1600, 900), 0.5));

    [Theory]
    [InlineData(-3, 35)]
    [InlineData(9, 175)]
    public void PaddingClampsItsSlider(double factor, double expected) =>
        Assert.Equal(expected, BackdropGeometry.Padding(new SizeD(1000, 1000), factor));

    [Fact]
    public void CardCornerRadiusClampsAndScalesWithTheShortSide()
    {
        var size = new SizeD(1600, 900);
        Assert.Equal(0, BackdropGeometry.CardCornerRadius(size, 0));
        Assert.Equal(180, BackdropGeometry.CardCornerRadius(size, 1));
        Assert.Equal(180, BackdropGeometry.CardCornerRadius(size, 2));
        Assert.Equal(90, BackdropGeometry.CardCornerRadius(size, 0.5));
        Assert.Equal(0, BackdropGeometry.CardCornerRadius(size, -1));
    }

    [Fact]
    public void BackgroundBlurScalesWithTheCanvasAndClampsItsSlider()
    {
        Assert.InRange(BackdropGeometry.BlurRadius(new SizeD(1600, 900), 1), 31.499, 31.501);
        Assert.Equal(0, BackdropGeometry.BlurRadius(new SizeD(1600, 900), -1));
        Assert.Equal(0, BackdropGeometry.BlurRadius(new SizeD(1600, 900), 0));
        Assert.InRange(BackdropGeometry.BlurRadius(new SizeD(1600, 900), 9), 31.499, 31.501);
    }

    [Fact]
    public void TheGradientRunsCornerToCorner()
    {
        var (start, end) = BackdropGeometry.GradientEndpoints(new SizeD(300, 200));
        Assert.Equal(new PointD(0, 0), start);
        Assert.Equal(new PointD(300, 200), end);
    }

    [Fact]
    public void ABackdropPictureFillsTheCanvasCentred()
    {
        // A 400x100 picture on a 200x200 canvas scales to 800x200, cropped on both sides.
        Assert.Equal(new RectD(-300, 0, 800, 200), BackdropGeometry.AspectFill(new SizeD(400, 100), new SizeD(200, 200)));
        // A tall picture overflows the top and bottom instead.
        Assert.Equal(new RectD(0, -150, 200, 500), BackdropGeometry.AspectFill(new SizeD(100, 250), new SizeD(200, 200)));
        Assert.Equal(new RectD(0, 0, 200, 200), BackdropGeometry.AspectFill(new SizeD(10, 10), new SizeD(200, 200)));
        Assert.Null(BackdropGeometry.AspectFill(new SizeD(0, 10), new SizeD(200, 200)));
    }

    // MARK: - Style codec

    [Fact]
    public void ABackdropStyleRoundTripsThroughJson() =>
        Assert.Equal(Solid, BackdropStyle.Decoded(Solid.Encoded()));

    [Fact]
    public void BackgroundsSavedBeforeBlurKeepDecoding()
    {
        var style = BackdropStyle.Decoded("""{"kind":"solid","colors":[[0.2,0.4,0.9]],"padding":0.3,"cornerRadius":0.2}""");
        Assert.Equal(BackdropKind.Solid, style.Kind);
        Assert.Equal(0, style.Blur);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"kind":"hologram","padding":0.5,"cornerRadius":0}""")]
    [InlineData("""{"kind":"solid","padding":"wide","cornerRadius":0}""")]
    [InlineData("""{"kind":"solid","colors":"red","padding":0.5,"cornerRadius":0}""")]
    [InlineData("""{"kind":"solid","colors":[[0,"x",1]],"padding":0.5,"cornerRadius":0}""")]
    [InlineData("""{"padding":0.5,"cornerRadius":0}""")]
    [InlineData("""{"kind":"solid","colors":[[0.1,0.2,0.3]],"cornerRadius":0}""")]
    public void AMissingOrBrokenStyleFallsBackToNone(string? raw) =>
        Assert.Equal(BackdropKind.None, BackdropStyle.Decoded(raw).Kind);

    [Fact]
    public void TheDefaultBackdropLeavesCaptureCornersUnchanged()
    {
        var plain = new BackdropStyle();
        Assert.Equal(0, plain.CornerRadius);
        Assert.Equal(0.5, plain.Padding);
        Assert.Equal(0, plain.Blur);
    }

    [Fact]
    public void ASolidStyleWithoutColoursDemotesToNone() =>
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Solid, Colors = null }.Sanitized().Kind);

    [Fact]
    public void ASolidStyleNeedsExactlyOneColourAndAGradientTwo()
    {
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[0, 0, 0], [1, 1, 1]] }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [[0, 0, 0]] }.Sanitized().Kind);
        Assert.Equal(BackdropKind.Gradient, new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [[0, 0, 0], [1, 1, 1]] }.Sanitized().Kind);
    }

    [Fact]
    public void AColourWithTheWrongNumberOfComponentsDemotes()
    {
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[0, 0]] }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[0, 0, 0, 1]] }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[0, double.NaN, 0]] }.Sanitized().Kind);
    }

    [Fact]
    public void BackdropSlidersClampToTheirRange()
    {
        var wild = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean", Padding = 9, CornerRadius = -3, Blur = 8 }.Sanitized();
        Assert.Equal(1, wild.Padding);
        Assert.Equal(0, wild.CornerRadius);
        Assert.Equal(1, wild.Blur);
        Assert.Equal(BackdropKind.Preset, wild.Kind);
    }

    [Fact]
    public void NonFiniteSlidersResetToTheirDefaults()
    {
        var broken = new BackdropStyle { Padding = double.NaN, CornerRadius = double.PositiveInfinity, Blur = double.NegativeInfinity }.Sanitized();
        Assert.Equal(0.5, broken.Padding);
        Assert.Equal(0.1, broken.CornerRadius);
        Assert.Equal(0, broken.Blur);
    }

    [Fact]
    public void UnknownPresetsAndMissingImagePathsDemoteToNone()
    {
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "bogus" }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "none" }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Preset }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Image, ImagePath = null }.Sanitized().Kind);
        Assert.Equal(BackdropKind.None, new BackdropStyle { Kind = BackdropKind.Image, ImagePath = "" }.Sanitized().Kind);
        Assert.Equal(BackdropKind.Image, new BackdropStyle { Kind = BackdropKind.Image, ImagePath = "C:\\w.png" }.Sanitized().Kind);
    }

    [Fact]
    public void GradientColoursClampComponentByComponent()
    {
        var style = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [[0, 2, -1], [0.5, 0.5, 0.5]] }.Sanitized();
        Assert.Equal([0, 1, 0], style.Colors![0]);
        Assert.Equal([0.5, 0.5, 0.5], style.Colors[1]);
    }

    [Fact]
    public void ADemotedStyleDropsItsLeftovers()
    {
        var demoted = new BackdropStyle { Kind = BackdropKind.Solid, PresetId = "ocean", ImagePath = "x", Colors = null }.Sanitized();
        Assert.Null(demoted.PresetId);
        Assert.Null(demoted.ImagePath);
        Assert.Null(demoted.Colors);
    }

    [Fact]
    public void EveryKindRoundTripsThroughItsJson()
    {
        BackdropStyle[] styles =
        [
            new() { Kind = BackdropKind.Preset, PresetId = "sunset", Padding = 0.1, CornerRadius = 0.9, Blur = 0.25 },
            new() { Kind = BackdropKind.Gradient, Colors = [[1, 0, 0], [0, 0, 1]], Padding = 1 },
            new() { Kind = BackdropKind.Image, ImagePath = "C:\\Users\\me\\wall paper.png", Blur = 1 },
            new() { Kind = BackdropKind.None },
        ];
        foreach (var style in styles) Assert.Equal(style, BackdropStyle.Decoded(style.Encoded()));
    }

    [Fact]
    public void TheJsonCarriesUpstreamsKeyNames()
    {
        var json = Solid.Encoded();
        foreach (var key in new[] { "\"kind\":\"solid\"", "\"colors\"", "\"padding\"", "\"cornerRadius\"", "\"blur\"" })
            Assert.Contains(key, json);
        Assert.Contains("\"presetID\":\"ocean\"", new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean" }.Encoded());
        Assert.Contains("\"imagePath\"", new BackdropStyle { Kind = BackdropKind.Image, ImagePath = "p" }.Encoded());
    }

    [Fact]
    public void StylesWithEqualColoursAreEqual()
    {
        Assert.Equal(Solid, Solid with { });
        Assert.Equal(Solid, Solid with { Colors = [[0.2, 0.4, 0.9]] });
        Assert.NotEqual(Solid, Solid with { Colors = [[0.2, 0.4, 0.8]] });
        Assert.NotEqual(Solid, Solid with { Blur = 0 });
    }

    [Fact]
    public void AStyleThatCannotBeWrittenEncodesToNothing() =>
        Assert.Equal("", new BackdropStyle { Padding = double.NaN }.Encoded());

    // MARK: - Backdrop presets

    [Fact]
    public void SavedBackdropsRoundTripThroughJson()
    {
        BackdropStyle[] list = [Solid, new() { Kind = BackdropKind.Gradient, Colors = [[1, 0, 0], [0, 0, 1]] }];
        Assert.Equal(list, ScreenshotPresets.DecodeBackdrops(ScreenshotPresets.EncodeBackdrops(list)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("junk")]
    [InlineData("{}")]
    public void BrokenPresetListsDecodeToEmpty(string? raw) => Assert.Empty(ScreenshotPresets.DecodeBackdrops(raw));

    [Fact]
    public void SavedBackdropsCapAtThePresetLimit()
    {
        var overflow = Enumerable.Repeat(Solid, 40).ToList();
        Assert.Equal(ScreenshotPresets.Limit, ScreenshotPresets.DecodeBackdrops(ScreenshotPresets.EncodeBackdrops(overflow)).Count);
        Assert.Equal(12, ScreenshotPresets.Limit);
    }

    [Fact]
    public void TheNewestPresetsSurviveTheCap()
    {
        var list = Enumerable.Range(0, 15).Select(i => new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[i / 20.0, 0, 0]] }).ToList();
        var back = ScreenshotPresets.DecodeBackdrops(ScreenshotPresets.EncodeBackdrops(list));
        Assert.Equal(list.TakeLast(12), back);
    }

    [Fact]
    public void APresetThatSanitisesToNoneIsLeftOut()
    {
        var raw = ScreenshotPresets.EncodeBackdrops([Solid, new BackdropStyle { Kind = BackdropKind.None }, new BackdropStyle { Kind = BackdropKind.Solid }]);
        Assert.Equal([Solid], ScreenshotPresets.DecodeBackdrops(raw));
    }

    [Fact]
    public void OneUnreadableEntryDoesNotCostTheWholeList()
    {
        var raw = """[{"kind":"solid","colors":[[0,0,0]],"padding":0.5,"cornerRadius":0},{"kind":"nonsense"},7,{"kind":"preset","presetID":"candy","padding":1,"cornerRadius":0.5}]""";
        var presets = ScreenshotPresets.DecodeBackdrops(raw);
        Assert.Equal(2, presets.Count);
        Assert.Equal("candy", presets[1].PresetId);
    }

    // MARK: - Corner reading

    private static (byte[] Pixels, int Stride) RoundedCard(int width, int height, double radius)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cx = x + 0.5 < radius ? radius : x + 0.5 > width - radius ? width - radius : x + 0.5;
                var cy = y + 0.5 < radius ? radius : y + 0.5 > height - radius ? height - radius : y + 0.5;
                var inside = (x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) <= radius * radius;
                if (inside) pixels[y * stride + x * 4 + 3] = 255;
            }
        }
        return (pixels, stride);
    }

    [Fact]
    public void AnOpaqueCaptureHasNoRoundingOfItsOwn()
    {
        var (pixels, stride) = RoundedCard(400, 300, 0);
        Assert.Equal(0, BackdropGeometry.TransparentCornerRadius(pixels, 400, 300, stride));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(20)]
    [InlineData(10)]
    public void AWindowsOwnCornerRadiusIsReadFromItsAlpha(double radius)
    {
        var (pixels, stride) = RoundedCard(400, 300, radius);
        Assert.InRange(BackdropGeometry.TransparentCornerRadius(pixels, 400, 300, stride), radius - 3, radius + 3);
    }

    [Fact]
    public void TheLeastRoundedCornerWins()
    {
        var (pixels, stride) = RoundedCard(400, 300, 40);
        // A mark drawn into the top-left corner opaques it, so the card reads as square there.
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 30; x++) pixels[y * stride + x * 4 + 3] = 255;
        Assert.Equal(0, BackdropGeometry.TransparentCornerRadius(pixels, 400, 300, stride));
    }

    [Fact]
    public void AFullyTransparentPictureHasNoCornerToRead()
    {
        var pixels = new byte[40 * 40 * 4];
        Assert.Equal(0, BackdropGeometry.TransparentCornerRadius(pixels, 40, 40, 160));
    }

    [Fact]
    public void ATinyPictureReadsAsSquare() =>
        Assert.Equal(0, BackdropGeometry.TransparentCornerRadius(new byte[3 * 3 * 4], 3, 3, 12));

    // MARK: - Export layout

    [Fact]
    public void WithoutABackdropTheCardIsTheWholePicture()
    {
        var layout = ExportLayout.Compute(new SizeD(8, 6), new BackdropStyle(), 2, hasFill: false, downscaleTo1x: false);
        Assert.Equal(new SizeD(8, 6), layout.Canvas);
        Assert.Equal(new RectD(0, 0, 8, 6), layout.Card);
        Assert.Equal(new SizeD(8, 6), layout.Output);
        Assert.Equal(2, layout.OutputScale);
        Assert.Null(layout.Shadow);
        Assert.Equal(0, layout.Padding);
    }

    [Fact]
    public void ARetinaExportKeepsItsPixelsAndItsDensity()
    {
        var layout = ExportLayout.Compute(new SizeD(8, 6), new BackdropStyle(), 2, false, false);
        Assert.Equal(8, layout.Output.Width);
        Assert.Equal(2, layout.OutputScale);
    }

    [Fact]
    public void TheOneXOptionHalvesThePixelsAndReportsOneX()
    {
        var layout = ExportLayout.Compute(new SizeD(8, 6), new BackdropStyle(), 2, false, true);
        Assert.Equal(new SizeD(4, 3), layout.Output);
        Assert.Equal(1, layout.OutputScale);
    }

    [Fact]
    public void TheOneXOptionDoesNothingAtOneX()
    {
        var layout = ExportLayout.Compute(new SizeD(8, 6), new BackdropStyle(), 1, false, true);
        Assert.Equal(new SizeD(8, 6), layout.Output);
        Assert.Equal(1, layout.OutputScale);
    }

    [Fact]
    public void ABackdropPadsTheCaptureOnEverySide()
    {
        var style = new BackdropStyle { Kind = BackdropKind.Solid, Colors = [[1, 1, 1]], Padding = 1, CornerRadius = 0 };
        var layout = ExportLayout.Compute(new SizeD(400, 300), style, 1, hasFill: true, downscaleTo1x: false);
        var padding = BackdropGeometry.Padding(new SizeD(400, 300), 1);
        Assert.Equal(padding, layout.Padding);
        Assert.Equal(new SizeD(400 + padding * 2, 300 + padding * 2), layout.Canvas);
        Assert.Equal(new RectD(padding, padding, 400, 300), layout.Card);
        Assert.Equal(layout.Canvas, layout.Output);
    }

    [Fact]
    public void TheCanvasTruncatesToWholePixels()
    {
        var layout = ExportLayout.Compute(new SizeD(400.7, 300.4), new BackdropStyle { Padding = 0 }, 1, true, false);
        Assert.Equal(Math.Truncate(400.7 + layout.Padding * 2), layout.Canvas.Width);
        Assert.Equal(Math.Truncate(300.4 + layout.Padding * 2), layout.Canvas.Height);
    }

    [Fact]
    public void TheShadowFollowsTheScale()
    {
        var one = ExportLayout.Compute(new SizeD(400, 300), new BackdropStyle(), 1, true, false).Shadow!.Value;
        var two = ExportLayout.Compute(new SizeD(400, 300), new BackdropStyle(), 2, true, false).Shadow!.Value;
        Assert.Equal(new BackdropShadow(6, 22, 0.4), one);
        Assert.Equal(new BackdropShadow(12, 44, 0.4), two);
    }

    [Fact]
    public void TheBlurRadiusFollowsTheCanvasAndOnlyExistsWithABlur()
    {
        var style = new BackdropStyle { Blur = 1 };
        var layout = ExportLayout.Compute(new SizeD(1600, 900), style, 1, true, false);
        Assert.Equal(BackdropGeometry.BlurRadius(layout.Canvas, 1), layout.BlurRadius);
        Assert.Equal(0, ExportLayout.Compute(new SizeD(1600, 900), new BackdropStyle { Blur = 0 }, 1, true, false).BlurRadius);
        Assert.Equal(0, ExportLayout.Compute(new SizeD(1600, 900), style, 1, hasFill: false, false).BlurRadius);
    }

    [Fact]
    public void ARoundedExportWithoutABackdropOnlyGainsTransparentCorners()
    {
        var layout = ExportLayout.Compute(new SizeD(1600, 900), new BackdropStyle { CornerRadius = 1 }, 1, false, false);
        Assert.Equal(180, layout.CornerRadius);
        Assert.Equal(new SizeD(1600, 900), layout.Canvas);
    }

    [Fact]
    public void TheShadowOutlineFollowsTheWindowsOwnCorner()
    {
        var layout = ExportLayout.Compute(new SizeD(400, 300), new BackdropStyle { CornerRadius = 0 }, 1, true, false, transparentCorner: 40);
        Assert.Equal(0, layout.CornerRadius);
        Assert.Equal(40, layout.OutlineCorner);
        var rounder = ExportLayout.Compute(new SizeD(400, 300), new BackdropStyle { CornerRadius = 1 }, 1, true, false, transparentCorner: 10);
        Assert.Equal(60, rounder.OutlineCorner);
    }

    [Fact]
    public void ABackdropExportAtTwoXDownscalesTheWholeCanvas()
    {
        var layout = ExportLayout.Compute(new SizeD(800, 600), new BackdropStyle { Padding = 0.5 }, 2, true, true);
        Assert.Equal(1, layout.OutputScale);
        Assert.Equal(Math.Round(layout.Canvas.Width / 2, MidpointRounding.AwayFromZero), layout.Output.Width);
    }

    // MARK: - Watermark styles

    private static WatermarkStyle TextMark => new()
    {
        Kind = WatermarkKind.Text, Text = "  Vorssaint  ", Color = "blue", Anchor = WatermarkAnchor.TopLeading,
        Size = 0.5, Opacity = 0.3, Rotation = 30,
    };

    [Fact]
    public void AWatermarkStyleRoundTripsThroughJsonTrimmed()
    {
        var back = WatermarkStyle.Decoded(TextMark.Encoded());
        Assert.Equal(TextMark.Sanitized(), back);
        Assert.Equal("Vorssaint", back.Text);
        Assert.Equal(WatermarkAnchor.TopLeading, back.Anchor);
        Assert.Equal(30, back.Rotation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"kind":"text","text":"x"}""")]
    [InlineData("""{"kind":"text","text":"x","color":"white","anchor":"nowhere","size":0.3,"opacity":0.4,"rotation":0}""")]
    [InlineData("""{"kind":"sticker","text":"x","color":"white","anchor":"top","size":0.3,"opacity":0.4,"rotation":0}""")]
    [InlineData("""{"kind":"text","text":5,"color":"white","anchor":"top","size":0.3,"opacity":0.4,"rotation":0}""")]
    public void AMissingOrBrokenWatermarkFallsBackToNone(string? raw) =>
        Assert.Equal(WatermarkKind.None, WatermarkStyle.Decoded(raw).Kind);

    [Fact]
    public void AWatermarkWithoutItsTextOrPictureDemotes()
    {
        Assert.Equal(WatermarkKind.None, new WatermarkStyle { Kind = WatermarkKind.Text, Text = "   " }.Sanitized().Kind);
        Assert.Equal(WatermarkKind.None, new WatermarkStyle { Kind = WatermarkKind.Image, ImagePath = null }.Sanitized().Kind);
        Assert.Equal(WatermarkKind.None, new WatermarkStyle { Kind = WatermarkKind.Image, ImagePath = "" }.Sanitized().Kind);
        Assert.Equal(WatermarkKind.Image, new WatermarkStyle { Kind = WatermarkKind.Image, ImagePath = "C:\\l.png" }.Sanitized().Kind);
    }

    [Fact]
    public void WatermarkSlidersClampAndAnUnknownColourReadsAsWhite()
    {
        var wild = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "x", Color = "bogus", Size = 7, Opacity = 0, Rotation = 400 }.Sanitized();
        Assert.Equal("white", wild.Color);
        Assert.Equal(1, wild.Size);
        Assert.Equal(0.05, wild.Opacity);
        Assert.Equal(90, wild.Rotation);
        Assert.Equal(-90, new WatermarkStyle { Rotation = -400 }.Sanitized().Rotation);
        Assert.Equal(1, new WatermarkStyle { Opacity = 3 }.Sanitized().Opacity);
    }

    [Fact]
    public void NonFiniteWatermarkSlidersResetToTheirDefaults()
    {
        var broken = new WatermarkStyle
        {
            Kind = WatermarkKind.Text, Text = "x", Size = double.NaN, Opacity = double.PositiveInfinity, Rotation = double.NegativeInfinity,
        }.Sanitized();
        Assert.Equal(0.3, broken.Size);
        Assert.Equal(0.4, broken.Opacity);
        Assert.Equal(0, broken.Rotation);
    }

    [Fact]
    public void WatermarkTextIsCapped()
    {
        var long500 = new WatermarkStyle { Kind = WatermarkKind.Text, Text = new string('a', 500) }.Sanitized();
        Assert.Equal(WatermarkStyle.TextLimit, long500.Text.Length);
        Assert.Equal(120, WatermarkStyle.TextLimit);
    }

    [Fact]
    public void TheTextCapNeverCutsAnEmojiInHalf()
    {
        var text = new string('a', 119) + "\U0001F600" + "tail";
        var capped = new WatermarkStyle { Kind = WatermarkKind.Text, Text = text }.Sanitized().Text;
        Assert.Equal(new string('a', 119) + "\U0001F600", capped);
    }

    [Fact]
    public void TheWatermarkDefaultsMatchUpstream()
    {
        var plain = new WatermarkStyle();
        Assert.Equal(WatermarkKind.None, plain.Kind);
        Assert.Equal("white", plain.Color);
        Assert.Equal(WatermarkAnchor.BottomTrailing, plain.Anchor);
        Assert.Equal(0.3, plain.Size);
        Assert.Equal(0.4, plain.Opacity);
        Assert.Equal(0, plain.Rotation);
    }

    [Fact]
    public void EveryAnchorRoundTripsByItsRawName()
    {
        foreach (var anchor in Enum.GetValues<WatermarkAnchor>())
        {
            Assert.Equal(anchor, WatermarkAnchors.Parse(WatermarkAnchors.Raw(anchor)));
            var style = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "x", Anchor = anchor };
            Assert.Equal(anchor, WatermarkStyle.Decoded(style.Encoded()).Anchor);
        }
        Assert.Equal("topLeading", WatermarkAnchors.Raw(WatermarkAnchor.TopLeading));
        Assert.Equal("bottomTrailing", WatermarkAnchors.Raw(WatermarkAnchor.BottomTrailing));
        Assert.Null(WatermarkAnchors.Parse("TOP"));
    }

    [Fact]
    public void ThereAreNineDistinctPlacesAndEightColours()
    {
        Assert.Equal(9, Enum.GetValues<WatermarkAnchor>().Select(a => a.UnitPoint()).Distinct().Count());
        Assert.Equal(8, WatermarkColors.Names.Count);
        Assert.Equal(8, WatermarkColors.Names.Select(WatermarkColors.Value).Distinct().Count());
    }

    [Fact]
    public void TheUnitPointsCoverTheGrid()
    {
        Assert.Equal(new PointD(0, 0), WatermarkAnchor.TopLeading.UnitPoint());
        Assert.Equal(new PointD(0.5, 0), WatermarkAnchor.Top.UnitPoint());
        Assert.Equal(new PointD(1, 0), WatermarkAnchor.TopTrailing.UnitPoint());
        Assert.Equal(new PointD(0, 0.5), WatermarkAnchor.Leading.UnitPoint());
        Assert.Equal(new PointD(0.5, 0.5), WatermarkAnchor.Center.UnitPoint());
        Assert.Equal(new PointD(1, 0.5), WatermarkAnchor.Trailing.UnitPoint());
        Assert.Equal(new PointD(0, 1), WatermarkAnchor.BottomLeading.UnitPoint());
        Assert.Equal(new PointD(0.5, 1), WatermarkAnchor.Bottom.UnitPoint());
        Assert.Equal(new PointD(1, 1), WatermarkAnchor.BottomTrailing.UnitPoint());
    }

    [Fact]
    public void WatermarkColoursKeepUpstreamsComponents()
    {
        Assert.Equal(1, WatermarkColors.Value("white").Red);
        Assert.Equal(0.09, WatermarkColors.Value("black").Red);
        Assert.Equal(0.04, WatermarkColors.Value("blue").Red);
        Assert.Equal(1.00, WatermarkColors.Value("blue").Blue);
        Assert.Equal(WatermarkColors.Value("white"), WatermarkColors.Value("bogus"));
        Assert.True(WatermarkColors.IsKnown("purple"));
        Assert.False(WatermarkColors.IsKnown("pink"));
        Assert.False(WatermarkColors.IsKnown(null));
    }

    [Fact]
    public void ACaptionAnchorPlacesContentWithAFivePercentMargin()
    {
        var canvas = new SizeD(1000, 600);
        var content = new SizeD(200, 50);
        Assert.Equal(new PointD(30, 30), WatermarkAnchor.TopLeading.Origin(content, canvas));
        Assert.Equal(new PointD(770, 520), WatermarkAnchor.BottomTrailing.Origin(content, canvas));
        Assert.Equal(new PointD(400, 275), WatermarkAnchor.Center.Origin(content, canvas));
    }

    // MARK: - Watermark presets

    [Fact]
    public void SavedWatermarksRoundTripWithTheirPlacement()
    {
        WatermarkStyle[] presets =
        [
            TextMark.Sanitized(),
            new() { Kind = WatermarkKind.Image, ImagePath = "/logo.png", Anchor = WatermarkAnchor.Center, Size = 0.8, Opacity = 0.2, Rotation = -45 },
        ];
        Assert.Equal(presets, ScreenshotPresets.DecodeWatermarks(ScreenshotPresets.EncodeWatermarks(presets)));
    }

    [Fact]
    public void BrokenWatermarkListsAndEmptyMarksDecodeToNothing()
    {
        Assert.Empty(ScreenshotPresets.DecodeWatermarks("junk"));
        Assert.Empty(ScreenshotPresets.DecodeWatermarks(null));
        Assert.Empty(ScreenshotPresets.DecodeWatermarks(ScreenshotPresets.EncodeWatermarks([new WatermarkStyle { Kind = WatermarkKind.Text, Text = " " }])));
    }

    [Fact]
    public void SavedWatermarksCapAtThePresetsLimit() =>
        Assert.Equal(ScreenshotPresets.Limit,
            ScreenshotPresets.DecodeWatermarks(ScreenshotPresets.EncodeWatermarks(Enumerable.Repeat(TextMark.Sanitized(), 40))).Count);

    // MARK: - Watermark sizes and placement

    [Fact]
    public void WatermarkTextScalesWithTheShortSideAndKeepsALegibleFloor()
    {
        var hd = new SizeD(1920, 1080);
        Assert.Equal(22, WatermarkGeometry.FontSize(hd, 0));
        Assert.Equal(173, WatermarkGeometry.FontSize(hd, 1));
        Assert.Equal(8, WatermarkGeometry.FontSize(new SizeD(20, 20), 0));
        Assert.Equal(173, WatermarkGeometry.FontSize(hd, 7));
    }

    [Fact]
    public void AWatermarkPictureSpansFromACornerMarkToHalfTheWidth()
    {
        var size = new SizeD(1000, 500);
        Assert.Equal(50, WatermarkGeometry.ImageWidth(size, 0));
        Assert.Equal(500, WatermarkGeometry.ImageWidth(size, 1));
        Assert.Equal(500, WatermarkGeometry.ImageWidth(size, 2));
        Assert.Equal(275, WatermarkGeometry.ImageWidth(size, 0.5));
    }

    [Fact]
    public void ThePictureMarksHeightFollowsItsProportions()
    {
        Assert.Equal(new SizeD(60, 30), WatermarkGeometry.ImageContentSize(new SizeD(120, 80), 1, new SizeD(200, 100)));
        Assert.Equal(SizeD.Zero, WatermarkGeometry.ImageContentSize(new SizeD(120, 80), 1, new SizeD(0, 100)));
    }

    private static readonly SizeD MarkCanvas = new(1000, 600);
    private static readonly SizeD MarkSize = new(200, 50);

    [Fact]
    public void AnUprightMarkSitsAgainstTheBottomRightMargin()
    {
        var placement = WatermarkGeometry.Placement(MarkSize, 0, WatermarkAnchor.BottomTrailing, MarkCanvas)!.Value;
        Assert.Equal(1, placement.Fit);
        Assert.Equal(new PointD(870, 545), placement.Center);
    }

    [Fact]
    public void ACentredMarkSitsInTheMiddle() =>
        Assert.Equal(new PointD(500, 300), WatermarkGeometry.Placement(MarkSize, 0, WatermarkAnchor.Center, MarkCanvas)!.Value.Center);

    [Fact]
    public void ATurnedMarkIsPlacedByItsTurnedBounds()
    {
        var turned = WatermarkGeometry.Placement(MarkSize, 90, WatermarkAnchor.TopLeading, MarkCanvas)!.Value;
        Assert.Equal(55, turned.Center.X, 6);
        Assert.Equal(130, turned.Center.Y, 6);
    }

    [Fact]
    public void AMarkWiderThanTheCaptureShrinksToTheMargins()
    {
        var wide = WatermarkGeometry.Placement(new SizeD(2000, 100), 0, WatermarkAnchor.BottomTrailing, MarkCanvas)!.Value;
        Assert.InRange(wide.Fit, 0.46, 0.48);
        Assert.Equal(500, wide.Center.X, 6);
    }

    [Fact]
    public void AnEmptyOrNonFiniteMarkHasNoPlacement()
    {
        Assert.Null(WatermarkGeometry.Placement(SizeD.Zero, 0, WatermarkAnchor.Center, MarkCanvas));
        Assert.Null(WatermarkGeometry.Placement(MarkSize, double.NaN, WatermarkAnchor.Center, MarkCanvas));
        Assert.Null(WatermarkGeometry.Placement(new SizeD(double.PositiveInfinity, 5), 0, WatermarkAnchor.Center, MarkCanvas));
        Assert.Null(WatermarkGeometry.Placement(MarkSize, 0, WatermarkAnchor.Center, SizeD.Zero));
    }

    [Fact]
    public void SquareCapturesKeepTheOriginalWatermarkSpacing() =>
        Assert.Equal(new PointD(35, 20),
            WatermarkGeometry.Placement(new SizeD(50, 20), 0, WatermarkAnchor.TopLeading, new SizeD(200, 200))!.Value.Center);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(1000)]
    public void RoundedPlacementStaysUsableAfterACropTo(double side)
    {
        var tiny = new SizeD(side, side);
        var placement = WatermarkGeometry.Placement(new SizeD(2000, 100), 45, WatermarkAnchor.BottomTrailing, tiny,
            BackdropGeometry.CardCornerRadius(tiny, 1))!.Value;
        Assert.InRange(placement.Fit, double.Epsilon, 1);
        Assert.True(double.IsFinite(placement.Center.X) && double.IsFinite(placement.Center.Y));
    }

    [Fact]
    public void ARoundedCardPushesTheMarkInPastItsCorner()
    {
        var square = WatermarkGeometry.Placement(MarkSize, 0, WatermarkAnchor.TopLeading, MarkCanvas)!.Value;
        var rounded = WatermarkGeometry.Placement(MarkSize, 0, WatermarkAnchor.TopLeading, MarkCanvas, 120)!.Value;
        Assert.True(rounded.Center.X > square.Center.X && rounded.Center.Y > square.Center.Y);
        // The margin is ceil(120 * (1 - sqrt 0.5)) + 1 = 37 pixels.
        Assert.Equal(37 + 100, rounded.Center.X, 6);
    }

    [Fact]
    public void ARoundedMarginNeverExceedsFortyFivePercentOfTheShortSide()
    {
        // On a 2 pixel capture the corner inset would be 2 pixels; the margin stops at 0.9 so some room is left.
        var placement = WatermarkGeometry.Placement(new SizeD(1, 1), 0, WatermarkAnchor.TopLeading, new SizeD(2, 2), 1)!.Value;
        Assert.Equal(0.2, placement.Fit, 6);
        Assert.Equal(1.0, placement.Center.X, 6);
    }

    [Theory]
    [InlineData(WatermarkAnchor.TopLeading)]
    [InlineData(WatermarkAnchor.Top)]
    [InlineData(WatermarkAnchor.TopTrailing)]
    [InlineData(WatermarkAnchor.Leading)]
    [InlineData(WatermarkAnchor.Center)]
    [InlineData(WatermarkAnchor.Trailing)]
    [InlineData(WatermarkAnchor.BottomLeading)]
    [InlineData(WatermarkAnchor.Bottom)]
    [InlineData(WatermarkAnchor.BottomTrailing)]
    public void EveryTiltedMarkInEveryPlaceStaysInsideTheCapture(WatermarkAnchor anchor)
    {
        var canvas = new SizeD(200, 200);
        foreach (var angle in new[] { -45.0, 0, 45, 90 })
        {
            var content = new SizeD(50, 20);
            var placement = WatermarkGeometry.Placement(content, angle, anchor, canvas, BackdropGeometry.CardCornerRadius(canvas, 1))!.Value;
            var radians = angle * Math.PI / 180;
            var halfW = (Math.Abs(content.Width * Math.Cos(radians)) + Math.Abs(content.Height * Math.Sin(radians))) * placement.Fit / 2;
            var halfH = (Math.Abs(content.Width * Math.Sin(radians)) + Math.Abs(content.Height * Math.Cos(radians))) * placement.Fit / 2;
            Assert.True(placement.Center.X - halfW >= -1e-9 && placement.Center.X + halfW <= 200 + 1e-9, $"{anchor} {angle} horizontal");
            Assert.True(placement.Center.Y - halfH >= -1e-9 && placement.Center.Y + halfH <= 200 + 1e-9, $"{anchor} {angle} vertical");
        }
    }

    [Fact]
    public void APictureWatermarkCoversExactlyItsPlacedSquare()
    {
        // 120x80 capture, a 4x4 picture at width factor 1 (half the capture, 60 pixels), top-left behind a 5 percent margin of 4.
        var capture = new SizeD(120, 80);
        var content = WatermarkGeometry.ImageContentSize(capture, 1, new SizeD(4, 4));
        Assert.Equal(new SizeD(60, 60), content);
        var placement = WatermarkGeometry.Placement(content, 0, WatermarkAnchor.TopLeading, capture)!.Value;
        var rect = WatermarkGeometry.ContentRect(placement, content);
        Assert.Equal(new RectD(4, 4, 60, 60), rect);
        Assert.True(rect.Contains(new PointD(34, 10)) && rect.Contains(new PointD(62, 62)));
        Assert.False(rect.Contains(new PointD(66, 34)) || rect.Contains(new PointD(34, 70)) || rect.Contains(new PointD(100, 70)));
    }

    [Fact]
    public void AShrunkMarksContentRectShrinksWithIt()
    {
        var placement = WatermarkGeometry.Placement(new SizeD(2000, 100), 0, WatermarkAnchor.Center, MarkCanvas)!.Value;
        var rect = WatermarkGeometry.ContentRect(placement, new SizeD(2000, 100));
        Assert.Equal(2000 * placement.Fit, rect.Width, 6);
        Assert.Equal(500, rect.X + rect.Width / 2, 6);
    }

    // MARK: - Portable settings

    private static WatermarkStyle Local => new() { Kind = WatermarkKind.Image, ImagePath = "/local/PRIVATE-LOGO.png", Anchor = WatermarkAnchor.TopLeading };

    private static WatermarkStyle Incoming => Local with { ImagePath = "/foreign/UNTRUSTED-LOGO.png", Opacity = 0.7 };

    private static WatermarkStyle TextPreset => new() { Kind = WatermarkKind.Text, Text = "Portable", Color = "blue", Rotation = 20, ImagePath = "/foreign/UNTRUSTED-LOGO.png" };

    [Fact]
    public void ExportAndImportRemoveActiveAndInactivePicturePaths()
    {
        var style = WatermarkPortability.PortableStyle(Incoming.Encoded());
        var presets = WatermarkPortability.PortablePresets(ScreenshotPresets.EncodeWatermarks([Incoming, TextPreset]));
        Assert.DoesNotContain("UNTRUSTED-LOGO", style);
        Assert.DoesNotContain("UNTRUSTED-LOGO", presets);
        var kept = ScreenshotPresets.DecodeWatermarks(presets);
        Assert.Single(kept);
        Assert.Equal("Portable", kept[0].Text);
        Assert.Null(kept[0].ImagePath);
        Assert.Equal(20, kept[0].Rotation);
    }

    [Fact]
    public void AnotherPcRestoresAppearanceWithoutSelectingAFile()
    {
        var raw = WatermarkPortability.PortableStyle(Incoming.Encoded());
        var restored = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(raw, null));
        Assert.Equal(WatermarkKind.None, restored.Kind);
        Assert.Null(restored.ImagePath);
        Assert.Equal(0.7, restored.Opacity);
    }

    [Fact]
    public void ARestoreCanUseOnlyThePictureAlreadyChosenOnThisPc()
    {
        var raw = WatermarkPortability.PortableStyle(Incoming.Encoded());
        var restored = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(raw, Local.Encoded()));
        Assert.Equal(WatermarkKind.Image, restored.Kind);
        Assert.Equal(Local.ImagePath, restored.ImagePath);
        Assert.Equal(Incoming.Opacity, restored.Opacity);
    }

    [Fact]
    public void TheFinalRestorationStepAlsoRejectsAnIncomingPath()
    {
        var direct = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(Incoming.Encoded(), null));
        Assert.Equal(WatermarkKind.None, direct.Kind);
        Assert.Null(direct.ImagePath);
    }

    [Fact]
    public void AnExplicitOffChoiceStaysOffWhileKeepingTheLocalFile()
    {
        var off = Local with { Kind = WatermarkKind.None };
        var restored = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(off.Encoded(), Local.Encoded()));
        Assert.Equal(WatermarkKind.None, restored.Kind);
        Assert.Equal(Local.ImagePath, restored.ImagePath);
    }

    [Fact]
    public void ChangingAnotherPreferenceAfterARestoreKeepsTheLocalPicture()
    {
        var off = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle((Local with { Kind = WatermarkKind.None }).Encoded(), Local.Encoded()));
        var text = TextPreset with { ImagePath = null };
        var sequence = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(text.Encoded(), off.Encoded()));
        sequence = WatermarkStyle.Decoded((sequence with { Opacity = 0.2 }).Encoded());
        Assert.Equal(WatermarkKind.Text, sequence.Kind);
        Assert.Equal("Portable", sequence.Text);
        Assert.Equal(Local.ImagePath, sequence.ImagePath);
        Assert.Equal(0.2, sequence.Opacity);
        Assert.Equal(Local.ImagePath, (sequence with { Kind = WatermarkKind.Image }).Sanitized().ImagePath);
    }

    [Fact]
    public void AnOlderBackupLeavesTheWatermarkOffWithoutForgettingTheLocalFile()
    {
        var older = WatermarkStyle.Decoded(WatermarkPortability.RestoredStyle(null, Local.Encoded()));
        Assert.Equal(WatermarkKind.None, older.Kind);
        Assert.Equal(Local.ImagePath, older.ImagePath);
    }

    [Fact]
    public void FullImportedPresetsCannotEvictALocalImagePreset()
    {
        var localPresets = ScreenshotPresets.EncodeWatermarks([Local]);
        var incoming = ScreenshotPresets.EncodeWatermarks(Enumerable.Range(0, 12).Select(i => new WatermarkStyle { Kind = WatermarkKind.Text, Text = $"Mark {i}" }));
        var restored = WatermarkPortability.RestoredPresets(incoming, localPresets);
        var presets = ScreenshotPresets.DecodeWatermarks(restored);
        Assert.Equal(12, presets.Count);
        Assert.Contains(Local.Sanitized(), presets);
        Assert.Equal("Mark 11", presets[10].Text);
        Assert.Equal("Mark 1", presets[0].Text);
    }

    [Fact]
    public void RepeatedRestorationDoesNotDuplicateImagePresets()
    {
        var localPresets = ScreenshotPresets.EncodeWatermarks([Local]);
        var incoming = ScreenshotPresets.EncodeWatermarks(Enumerable.Range(0, 12).Select(i => new WatermarkStyle { Kind = WatermarkKind.Text, Text = $"Mark {i}" }));
        var once = WatermarkPortability.RestoredPresets(incoming, localPresets);
        var twice = WatermarkPortability.RestoredPresets(incoming, once);
        Assert.Equal(ScreenshotPresets.DecodeWatermarks(once), ScreenshotPresets.DecodeWatermarks(twice));
    }

    [Fact]
    public void AnOlderBackupPreservesLocalImagePresets() =>
        Assert.Equal([Local.Sanitized()], ScreenshotPresets.DecodeWatermarks(WatermarkPortability.RestoredPresets(null, ScreenshotPresets.EncodeWatermarks([Local]))));

    [Fact]
    public void ForgedImagePresetsCannotAuthoriseAnyFile()
    {
        var forged = ScreenshotPresets.EncodeWatermarks([Incoming, TextPreset]);
        var restored = ScreenshotPresets.DecodeWatermarks(WatermarkPortability.RestoredPresets(forged, null));
        Assert.All(restored, preset =>
        {
            Assert.Equal(WatermarkKind.Text, preset.Kind);
            Assert.Null(preset.ImagePath);
        });
    }
}
