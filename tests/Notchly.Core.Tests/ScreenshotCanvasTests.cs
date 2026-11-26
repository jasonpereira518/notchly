// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the pure ranges of upstream Tests/ScreenshotFeatureTests.swift that cover the editor's annotation model: tools
// and their order, styles, counters, layer order, selection style, handles, crop snapping, arrows, segment distance, drag
// tolerance and the recognized-word helpers. Coordinates are image points, top-left origin, as upstream's editor uses.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

internal static class CanvasTestKit
{
    public static PointD P(double x, double y) => new(x, y);
    public static RectD R(double x, double y, double w, double h) => new(x, y, w, h);
    public static ImageAsset Image(int w, int h) => new(w, h);

    public static CanvasModel Model(int w = 100, int h = 100, double scale = 1, CanvasPreferences? prefs = null,
                                    ICanvasHost? host = null) =>
        new(Image(w, h), scale, prefs, host);

    public static Annotation Mark(ScreenshotTool tool, RectD rect = default, IEnumerable<PointD>? points = null) =>
        new(tool, rect, points);
}

public class ScreenshotCanvasToolTests
{
    private static readonly ScreenshotTool[] DefaultOrder =
    [
        ScreenshotTool.Select, ScreenshotTool.Arrow, ScreenshotTool.Pixelate, ScreenshotTool.Crop, ScreenshotTool.Text,
        ScreenshotTool.Sticker, ScreenshotTool.Rect, ScreenshotTool.Highlight, ScreenshotTool.Freehand, ScreenshotTool.Line,
        ScreenshotTool.Ellipse, ScreenshotTool.Counter, ScreenshotTool.Redact, ScreenshotTool.Spotlight,
        ScreenshotTool.ExpandCanvas,
    ];

    [Fact]
    public void TheRailLeadsWithTheNineMostUsefulNumberedTools()
    {
        Assert.Equal(15, ScreenshotTools.EditorTools.Count);
        Assert.Equal(DefaultOrder, ScreenshotTools.EditorTools);
        Assert.Equal(DefaultOrder.Take(9), ScreenshotTools.EditorTools.Take(9));
    }

    [Fact]
    public void ImageContentIsNotARailTool()
    {
        Assert.DoesNotContain(ScreenshotTool.Image, ScreenshotTools.EditorTools);
        Assert.Equal(16, ScreenshotTools.All.Count);
        Assert.Equal(ScreenshotTool.Image, ScreenshotTools.All[^1]);
    }

    [Fact]
    public void TheDefaultOrderStorageIsTheCanonicalRailOrder()
    {
        Assert.Equal("select,arrow,pixelate,crop,text,sticker,rect,highlight,freehand,line,ellipse,counter,redact,spotlight,expandCanvas",
                     ScreenshotTools.DefaultOrderStorage);
    }

    [Theory]
    [InlineData(ScreenshotTool.Select, "select")]
    [InlineData(ScreenshotTool.ExpandCanvas, "expandCanvas")]
    [InlineData(ScreenshotTool.Freehand, "freehand")]
    [InlineData(ScreenshotTool.Image, "image")]
    [InlineData(ScreenshotTool.Pixelate, "pixelate")]
    public void RawValuesAreUpstreamLowerCamelCase(ScreenshotTool tool, string raw)
    {
        Assert.Equal(raw, tool.Raw());
        Assert.Equal(tool, ScreenshotTools.FromRaw(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Select")]
    [InlineData("5")]
    [InlineData("nonsense")]
    public void UnknownRawValuesNeverBecomeATool(string? raw) => Assert.Null(ScreenshotTools.FromRaw(raw));

    [Theory]
    [InlineData(ScreenshotTool.Rect, true)]
    [InlineData(ScreenshotTool.Ellipse, true)]
    [InlineData(ScreenshotTool.Highlight, true)]
    [InlineData(ScreenshotTool.Pixelate, true)]
    [InlineData(ScreenshotTool.Redact, true)]
    [InlineData(ScreenshotTool.Spotlight, true)]
    [InlineData(ScreenshotTool.Select, false)]
    [InlineData(ScreenshotTool.Arrow, false)]
    [InlineData(ScreenshotTool.Line, false)]
    [InlineData(ScreenshotTool.Freehand, false)]
    [InlineData(ScreenshotTool.Text, false)]
    [InlineData(ScreenshotTool.Sticker, false)]
    [InlineData(ScreenshotTool.Counter, false)]
    [InlineData(ScreenshotTool.Crop, false)]
    [InlineData(ScreenshotTool.ExpandCanvas, false)]
    [InlineData(ScreenshotTool.Image, false)]
    public void OnlyAreaToolsDragARect(ScreenshotTool tool, bool drags) => Assert.Equal(drags, tool.DragsRect());

    [Theory]
    [InlineData(ScreenshotTool.Rect, true)]
    [InlineData(ScreenshotTool.Spotlight, true)]
    [InlineData(ScreenshotTool.Sticker, true)]
    [InlineData(ScreenshotTool.Image, true)]
    [InlineData(ScreenshotTool.Arrow, false)]
    [InlineData(ScreenshotTool.Text, false)]
    [InlineData(ScreenshotTool.Counter, false)]
    [InlineData(ScreenshotTool.Freehand, false)]
    public void RectangularMarksResizeWithHandles(ScreenshotTool tool, bool handles) =>
        Assert.Equal(handles, tool.ResizesWithHandles());

    [Fact]
    public void SpotlightUsesRectangleHandlesAndHasNoStyleControls()
    {
        Assert.True(ScreenshotTool.Spotlight.DragsRect());
        Assert.True(ScreenshotTool.Spotlight.ResizesWithHandles());
        Assert.Equal(new SelectionStyle(null, null, null),
                     AnnotationSupport.SelectionStyleFor(new Annotation(ScreenshotTool.Spotlight)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("select")]
    [InlineData("crop")]
    [InlineData("expandCanvas")]
    [InlineData("image")]
    public void ANewEditorOpensWithArrowInsteadOfAWholeCaptureTool(string? raw) =>
        Assert.Equal(ScreenshotTool.Arrow, ScreenshotTools.StartingTool(raw));

    [Fact]
    public void ANewEditorOpensWithTheDrawingToolUsedLast()
    {
        Assert.Equal(ScreenshotTool.Pixelate, ScreenshotTools.StartingTool("pixelate"));
        Assert.Equal(ScreenshotTool.Text, ScreenshotTools.StartingTool("text"));
    }

    [Fact]
    public void ASavedOrderDropsInvalidDuplicatesAndAppendsMissingTools()
    {
        var order = ScreenshotTools.Ordered("crop,arrow,arrow,invalid");
        Assert.Equal([ScreenshotTool.Crop, ScreenshotTool.Arrow], order.Take(2));
        Assert.Equal(15, order.Count);
        Assert.Equal(15, order.Distinct().Count());
    }

    [Fact]
    public void AStoredInternalContentIdCannotBecomeATool() =>
        Assert.Equal([ScreenshotTool.Crop, ScreenshotTool.Arrow], ScreenshotTools.Ordered("crop,image,arrow").Take(2));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",,,")]
    public void AnEmptyOrderIsTheCanonicalOne(string? raw) => Assert.Equal(DefaultOrder, ScreenshotTools.Ordered(raw));

    [Theory]
    [InlineData(1, ScreenshotTool.Select)]
    [InlineData(2, ScreenshotTool.Arrow)]
    [InlineData(3, ScreenshotTool.Pixelate)]
    [InlineData(4, ScreenshotTool.Crop)]
    [InlineData(5, ScreenshotTool.Text)]
    [InlineData(6, ScreenshotTool.Sticker)]
    [InlineData(7, ScreenshotTool.Rect)]
    [InlineData(8, ScreenshotTool.Highlight)]
    [InlineData(9, ScreenshotTool.Freehand)]
    public void NumberKeysOneThroughNineFollowTheToolOrder(int number, ScreenshotTool expected) =>
        Assert.Equal(expected, ScreenshotTools.ShortcutTool(number, null, true));

    [Fact]
    public void NumberKeysCanBeDisabledTogetherAndStopAtNine()
    {
        Assert.Null(ScreenshotTools.ShortcutTool(1, null, false));
        Assert.Null(ScreenshotTools.ShortcutTool(10, null, true));
        Assert.Null(ScreenshotTools.ShortcutTool(0, null, true));
        Assert.Null(ScreenshotTools.ShortcutTool(-1, null, true));
    }

    [Fact]
    public void NumberKeysFollowACustomOrder()
    {
        Assert.Equal(ScreenshotTool.Crop, ScreenshotTools.ShortcutTool(1, "crop,arrow", true));
        Assert.Equal(ScreenshotTool.Arrow, ScreenshotTools.ShortcutTool(2, "crop,arrow", true));
        Assert.Equal(ScreenshotTool.Select, ScreenshotTools.ShortcutTool(3, "crop,arrow", true));
    }

    [Fact]
    public void TheRailExposesOnlyTheFirstNineConfiguredShortcutNumbers()
    {
        Assert.Equal(2, ScreenshotTools.ShortcutNumber(ScreenshotTool.Crop, "arrow,crop", true));
        Assert.Null(ScreenshotTools.ShortcutNumber(ScreenshotTool.Redact, null, true));
        Assert.Null(ScreenshotTools.ShortcutNumber(ScreenshotTool.Crop, "arrow,crop", false));
        Assert.Equal(9, ScreenshotTools.ShortcutNumber(ScreenshotTool.Freehand, null, true));
        Assert.Null(ScreenshotTools.ShortcutNumber(ScreenshotTool.Line, null, true));
    }

    [Fact]
    public void TheShortcutMenuAssignsANumberedSlotOrRemovesAToolFromOneThroughNine()
    {
        var cropFirst = ScreenshotTools.AssigningShortcut(1, ScreenshotTool.Crop, null);
        var selectNone = ScreenshotTools.AssigningShortcut(null, ScreenshotTool.Select, null);
        Assert.Equal(ScreenshotTool.Crop, cropFirst.First());
        Assert.Equal(1, ScreenshotTools.ShortcutNumber(ScreenshotTool.Crop, ScreenshotTools.OrderStorage(cropFirst), true));
        Assert.Equal(9, selectNone.IndexOf(ScreenshotTool.Select));
        Assert.Null(ScreenshotTools.ShortcutNumber(ScreenshotTool.Select, ScreenshotTools.OrderStorage(selectNone), true));
    }

    [Fact]
    public void ClearingAToolAlreadyBelowTheNinthSlotChangesNothing() =>
        Assert.Equal(DefaultOrder, ScreenshotTools.AssigningShortcut(null, ScreenshotTool.Counter, null));

    [Fact]
    public void AssigningAnOutOfRangeNumberActsLikeClearing()
    {
        var order = ScreenshotTools.AssigningShortcut(12, ScreenshotTool.Arrow, null);
        Assert.Equal(9, order.IndexOf(ScreenshotTool.Arrow));
    }

    [Fact]
    public void ReassigningKeepsEveryToolExactlyOnce()
    {
        var order = ScreenshotTools.AssigningShortcut(3, ScreenshotTool.Spotlight, null);
        Assert.Equal(15, order.Count);
        Assert.Equal(15, order.Distinct().Count());
        Assert.Equal(ScreenshotTool.Spotlight, order[2]);
    }

    [Fact]
    public void ReassigningAnImageToolIsIgnored()
    {
        var order = ScreenshotTools.AssigningShortcut(1, ScreenshotTool.Image, null);
        Assert.Equal(DefaultOrder, order);
    }

    [Fact]
    public void TheDefaultSettingsRegistrationMatchesTheCanonicalOrder()
    {
        // The registered default of the tool order setting is upstream's Tool.defaultOrderStorage.
        Assert.Equal(ScreenshotTools.DefaultOrderStorage, ScreenshotTools.OrderStorage(ScreenshotTools.EditorTools));
    }
}

public class ScreenshotCanvasStyleTests
{
    [Fact]
    public void StyleChoicesSanitizeToSafeDefaults()
    {
        Assert.Equal(AnnotationColor.Red, AnnotationColors.Sanitized("bogus"));
        Assert.Equal(StrokeWidth.Medium, StrokeWidths.Sanitized(null));
        Assert.Equal(ArrowStyle.Filled, ArrowStyles.Sanitized("unknown"));
        Assert.Equal(BlurStyle.Pixelate, BlurStyles.Sanitized("smudge"));
        Assert.Equal(StickerKind.Check, Stickers.Sanitized("bogus"));
    }

    [Fact]
    public void StyleChoicesParseTheirOwnRawValues()
    {
        foreach (var color in Enum.GetValues<AnnotationColor>()) Assert.Equal(color, AnnotationColors.Sanitized(color.Raw()));
        foreach (var stroke in Enum.GetValues<StrokeWidth>()) Assert.Equal(stroke, StrokeWidths.Sanitized(stroke.Raw()));
        foreach (var style in Enum.GetValues<ArrowStyle>()) Assert.Equal(style, ArrowStyles.Sanitized(style.Raw()));
        foreach (var style in Enum.GetValues<BlurStyle>()) Assert.Equal(style, BlurStyles.Sanitized(style.Raw()));
        foreach (var sticker in Enum.GetValues<StickerKind>()) Assert.Equal(sticker, Stickers.Sanitized(sticker.Raw()));
    }

    [Fact]
    public void ThereAreEightColoursInTheCanonicalOrder()
    {
        Assert.Equal(["red", "orange", "yellow", "green", "blue", "purple", "black", "white"],
                     Enum.GetValues<AnnotationColor>().Select(c => c.Raw()));
    }

    [Theory]
    [InlineData(AnnotationColor.Red, 0.93, 0.26, 0.21)]
    [InlineData(AnnotationColor.Orange, 1.00, 0.58, 0.00)]
    [InlineData(AnnotationColor.Yellow, 1.00, 0.80, 0.00)]
    [InlineData(AnnotationColor.Green, 0.20, 0.78, 0.35)]
    [InlineData(AnnotationColor.Blue, 0.04, 0.52, 1.00)]
    [InlineData(AnnotationColor.Purple, 0.69, 0.32, 0.87)]
    [InlineData(AnnotationColor.Black, 0.09, 0.09, 0.11)]
    [InlineData(AnnotationColor.White, 1.00, 1.00, 1.00)]
    public void ColourComponentsAreShared(AnnotationColor color, double r, double g, double b)
    {
        var c = color.Components();
        Assert.Equal((r, g, b), (c.Red, c.Green, c.Blue));
    }

    [Theory]
    [InlineData(StrokeWidth.Small, 2)]
    [InlineData(StrokeWidth.Medium, 4)]
    [InlineData(StrokeWidth.Large, 7)]
    public void StrokeWidthsAreTwoFourAndSeven(StrokeWidth stroke, double width) => Assert.Equal(width, stroke.Width());

    [Fact]
    public void ThereAreFiveArrowStylesAndSolidIsTheFallback()
    {
        Assert.Equal([ArrowStyle.Filled, ArrowStyle.Outline, ArrowStyle.Open, ArrowStyle.DoubleEnded, ArrowStyle.Scribbly],
                     Enum.GetValues<ArrowStyle>());
        Assert.Equal("doubleEnded", ArrowStyle.DoubleEnded.Raw());
    }

    [Fact]
    public void BlurAreasStartAsPixelateAndOnlyErasingHasNoStrength()
    {
        Assert.Equal(BlurStyle.Pixelate, BlurStyles.Sanitized(null));
        Assert.Equal(BlurStyle.Erase, BlurStyles.Sanitized("erase"));
        Assert.True(BlurStyle.Pixelate.UsesStrength());
        Assert.True(BlurStyle.Blur.UsesStrength());
        Assert.False(BlurStyle.Erase.UsesStrength());
    }

    [Fact]
    public void ThereAreTwelveStickersWithGlyphs()
    {
        Assert.Equal(12, Enum.GetValues<StickerKind>().Length);
        Assert.All(Enum.GetValues<StickerKind>(), s => Assert.False(string.IsNullOrEmpty(s.Glyph())));
        Assert.Equal("thumbsUp", StickerKind.ThumbsUp.Raw());
    }

    [Theory]
    [InlineData(0, 19)]
    [InlineData(2, 10)]
    [InlineData(500, 96)]
    [InlineData(19, 19)]
    [InlineData(-4, 10)]
    [InlineData(37, 37)]
    public void AStoredTextSizeStaysInsideTheOfferedRange(int stored, int expected) =>
        Assert.Equal(expected, AnnotationSizes.SanitizedTextSize(stored));

    [Theory]
    [InlineData(19, true, 24)]
    [InlineData(19, false, 16)]
    [InlineData(20, false, 19)]
    [InlineData(96, true, null)]
    [InlineData(10, false, null)]
    [InlineData(10, true, 12)]
    [InlineData(72, true, 96)]
    public void TheSizeButtonsStepThroughThePresetsAndStopAtTheEnds(int from, bool up, int? expected) =>
        Assert.Equal(expected, AnnotationSizes.SteppedTextSize(from, up));

    [Fact]
    public void TextPresetsAreTheTwelveUpstreamSizes() =>
        Assert.Equal([10, 12, 14, 16, 19, 24, 28, 36, 48, 64, 72, 96], AnnotationSizes.TextSizes);

    [Theory]
    [InlineData(1.4, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(0.3, 0.3)]
    public void TheSpotlightDimStaysWithinRange(double input, double expected) =>
        Assert.Equal(expected, AnnotationSizes.SanitizedSpotlightDim(input));

    [Fact]
    public void AnUnreadableSpotlightDimFallsBackToTheDefault()
    {
        Assert.Equal(0.5, AnnotationSizes.SanitizedSpotlightDim(double.PositiveInfinity));
        Assert.Equal(0.5, AnnotationSizes.SanitizedSpotlightDim(double.NaN));
        Assert.Equal(0.5, AnnotationSizes.DefaultSpotlightDim);
    }

    [Theory]
    [InlineData(0.52, 0.5)]
    [InlineData(0.68, 0.7)]
    [InlineData(-0.2, 0.0)]
    [InlineData(0.35, 0.35)]
    [InlineData(1.3, 1.0)]
    public void TheDimSliderSettlesOnTheNearestFivePercentStep(double input, double expected) =>
        Assert.Equal(expected, AnnotationSizes.SteppedSpotlightDim(input), 10);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 5)]
    [InlineData(3, 3)]
    [InlineData(-3, 1)]
    public void BlurLevelsStayInRange(int level, int expected) => Assert.Equal(expected, BlurStrength.Sanitized(level));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ANewCaptureNeverStartsPixelatingAtALightLevel(int remembered) =>
        Assert.Equal(BlurStrength.DefaultLevel, BlurStrength.StartingLevel(remembered));

    [Theory]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    public void AHeavyRememberedLevelCarriesOver(int remembered, int expected) =>
        Assert.Equal(expected, BlurStrength.StartingLevel(remembered));

    [Theory]
    [InlineData(1, 0.4)]
    [InlineData(2, 0.65)]
    [InlineData(3, 1.0)]
    [InlineData(4, 1.5)]
    [InlineData(5, 2.2)]
    [InlineData(99, 2.2)]
    public void EachBlurLevelHasItsBlockFactor(int level, double factor) => Assert.Equal(factor, BlurStrength.BlockFactor(level));

    [Fact]
    public void EachBlurLevelCoarsensTheMosaicAndTheMiddleKeepsTheOldStrength()
    {
        var capture = new SizeD(1920, 1080);
        Assert.Equal(AnnotationSizes.PixelBlockSize(capture), AnnotationSizes.PixelBlockSize(capture, BlurStrength.DefaultLevel));
        Assert.True(AnnotationSizes.PixelBlockSize(capture, 1) < AnnotationSizes.PixelBlockSize(capture, 2));
        Assert.True(AnnotationSizes.PixelBlockSize(capture, 2) < AnnotationSizes.PixelBlockSize(capture));
        Assert.True(AnnotationSizes.PixelBlockSize(capture) < AnnotationSizes.PixelBlockSize(capture, 4));
        Assert.True(AnnotationSizes.PixelBlockSize(capture, 4) < AnnotationSizes.PixelBlockSize(capture, 5));
        Assert.True(AnnotationSizes.PixelBlockSize(new SizeD(40, 40), 1) >= 2);
    }

    [Fact]
    public void PixelationBlocksScaleWithTheCaptureAndNeverGetTooFine()
    {
        Assert.Equal(65, AnnotationSizes.PixelBlockSize(new SizeD(5500, 3600)));
        Assert.Equal(10, AnnotationSizes.PixelBlockSize(new SizeD(200, 120)));
    }
}

public class ScreenshotCanvasAnnotationTests
{
    [Fact]
    public void DefaultsMatchTheSwiftInitialiser()
    {
        var a = new Annotation(ScreenshotTool.Rect);
        Assert.Equal(AnnotationColor.Red, a.Color);
        Assert.Equal(StrokeWidth.Medium, a.Stroke);
        Assert.Equal(19, a.TextSize);
        Assert.Equal(3, a.BlurLevel);
        Assert.Equal(BlurStyle.Pixelate, a.BlurStyle);
        Assert.False(a.BlurTextOnly);
        Assert.Equal(ArrowStyle.Filled, a.ArrowStyle);
        Assert.Equal(0ul, a.ScribbleSeed);
        Assert.Equal(0, a.Number);
        Assert.Empty(a.Points);
        Assert.Equal("", a.Text);
        Assert.Null(a.ImageAsset);
        Assert.Null(a.RenderReferenceSize);
    }

    [Fact]
    public void EachAnnotationHasItsOwnId() =>
        Assert.NotEqual(new Annotation(ScreenshotTool.Rect).Id, new Annotation(ScreenshotTool.Rect).Id);

    [Fact]
    public void ANewScribblyArrowGetsItsOwnSeedAndOtherArrowsKeepZero()
    {
        var seeds = Enumerable.Range(0, 8).Select(_ => new Annotation(ScreenshotTool.Arrow, arrowStyle: ArrowStyle.Scribbly).ScribbleSeed).ToList();
        Assert.True(seeds.Distinct().Count() > 1);
        Assert.Equal(0ul, new Annotation(ScreenshotTool.Arrow, arrowStyle: ArrowStyle.Open).ScribbleSeed);
        Assert.Equal(17ul, new Annotation(ScreenshotTool.Arrow, arrowStyle: ArrowStyle.Scribbly, scribbleSeed: 17).ScribbleSeed);
    }

    [Fact]
    public void ArrowAnnotationsRetainTheChosenStyleIndependentlyOfColourAndThickness()
    {
        var open = new Annotation(ScreenshotTool.Arrow, points: [new PointD(0, 0), new PointD(100, 100)], arrowStyle: ArrowStyle.Open);
        Assert.Equal(ArrowStyle.Open, open.ArrowStyle);
        Assert.Equal(AnnotationColor.Red, open.Color);
    }

    [Fact]
    public void GeometryEditsRetainTheImmutableImageData()
    {
        var asset = new ImageAsset(100, 50);
        var layer = new Annotation(ScreenshotTool.Image, new RectD(0, 10, 40, 20), imageAsset: asset);
        var moved = layer with { Rect = layer.Rect with { X = layer.Rect.X + 5 } };
        Assert.Same(layer.ImageAsset, moved.ImageAsset);
        Assert.Equal(5, moved.Rect.X);
    }

    [Fact]
    public void EqualityComparesEveryFieldAndThePointsByValue()
    {
        var id = Guid.NewGuid();
        var a = new Annotation(ScreenshotTool.Line, points: [new PointD(1, 2), new PointD(3, 4)], id: id);
        var b = new Annotation(ScreenshotTool.Line, points: [new PointD(1, 2), new PointD(3, 4)], id: id);
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Color = AnnotationColor.Blue });
        Assert.NotEqual(a, b with { Points = [new PointD(1, 2), new PointD(3, 5)] });
    }

    [Fact]
    public void ImagesCompareByReferenceNotByContent()
    {
        var id = Guid.NewGuid();
        var a = new Annotation(ScreenshotTool.Image, imageAsset: new ImageAsset(2, 2), id: id);
        var b = new Annotation(ScreenshotTool.Image, imageAsset: new ImageAsset(2, 2), id: id);
        Assert.NotEqual(a, b);
        Assert.Equal(a, a with { Rect = a.Rect });
    }

    [Fact]
    public void ASavedAnnotationComparesEqualWithoutHoldingTheImage()
    {
        var asset = new ImageAsset(2, 2);
        var layer = new Annotation(ScreenshotTool.Image, imageAsset: asset);
        Assert.Equal(new SavedAnnotation(layer), new SavedAnnotation(layer with { }));
        var replaced = layer with { ImageAsset = new ImageAsset(2, 2) };
        Assert.NotEqual(new SavedAnnotation(layer), new SavedAnnotation(replaced));
        Assert.NotEqual(new SavedAnnotation(layer), new SavedAnnotation(layer with { Text = "x" }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (SavedAnnotation Saved, WeakReference Asset) SaveWithAWeakImage()
    {
        var asset = new ImageAsset(2, 2);
        var saved = new SavedAnnotation(new Annotation(ScreenshotTool.Image, imageAsset: asset));
        return (saved, new WeakReference(asset));
    }

    [Fact]
    public void ASavedAnnotationDoesNotRetainTheImagePixels()
    {
        var (saved, weak) = SaveWithAWeakImage();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive);
        GC.KeepAlive(saved);
    }

    [Fact]
    public void ImageAssetsKeepTheirSizeAndByteCost()
    {
        var asset = new ImageAsset(10, 20);
        Assert.Equal(new SizeD(10, 20), asset.PixelSize);
        Assert.Equal(800, asset.ByteCount);
        Assert.Equal(5000, new ImageAsset(10, 20, 5000).ByteCount);
        Assert.NotEqual(asset.Id, new ImageAsset(10, 20).Id);
    }
}

public class ScreenshotCanvasCounterAndLayerTests
{
    [Fact]
    public void DeletingACounterRenumbersTheRestWithoutHoles()
    {
        var list = new List<Annotation>
        {
            new(ScreenshotTool.Counter, number: 1), new(ScreenshotTool.Arrow),
            new(ScreenshotTool.Counter, number: 2), new(ScreenshotTool.Counter, number: 3),
        };
        list.RemoveAt(0);
        var renumbered = AnnotationSupport.RenumberingCounters(list);
        Assert.Equal([1, 2], renumbered.Where(a => a.Tool == ScreenshotTool.Counter).Select(a => a.Number));
        Assert.Equal(3, renumbered.Count);
        Assert.Equal(ScreenshotTool.Arrow, renumbered[0].Tool);
    }

    [Fact]
    public void RenumberingNeverTouchesOtherMarks()
    {
        var arrow = new Annotation(ScreenshotTool.Arrow, number: 9);
        Assert.Equal(9, AnnotationSupport.RenumberingCounters([arrow])[0].Number);
    }

    [Fact]
    public void RenumberingFollowsTheDrawingOrder()
    {
        var a = new Annotation(ScreenshotTool.Counter, number: 5);
        var b = new Annotation(ScreenshotTool.Counter, number: 5);
        var result = AnnotationSupport.RenumberingCounters([b, a]);
        Assert.Equal([b.Id, a.Id], result.Select(x => x.Id));
        Assert.Equal([1, 2], result.Select(x => x.Number));
    }

    private static List<Annotation> Layers() =>
        [new(ScreenshotTool.Text), new(ScreenshotTool.Rect), new(ScreenshotTool.Arrow)];

    [Fact]
    public void SendingBackSwapsWithTheOneDrawnBefore()
    {
        var layered = Layers();
        var result = AnnotationSupport.Reordering(layered, layered[1].Id, LayerMove.Backward);
        Assert.Equal([layered[1].Id, layered[0].Id, layered[2].Id], result.Select(a => a.Id));
    }

    [Fact]
    public void BringingForwardSwapsWithTheOneDrawnAfter()
    {
        var layered = Layers();
        var result = AnnotationSupport.Reordering(layered, layered[1].Id, LayerMove.Forward);
        Assert.Equal([layered[0].Id, layered[2].Id, layered[1].Id], result.Select(a => a.Id));
    }

    [Fact]
    public void AnAnnotationAtEitherEndStaysPutInThatDirection()
    {
        var layered = Layers();
        Assert.Equal(layered.Select(a => a.Id), AnnotationSupport.Reordering(layered, layered[0].Id, LayerMove.Backward).Select(a => a.Id));
        Assert.Equal(layered.Select(a => a.Id), AnnotationSupport.Reordering(layered, layered[2].Id, LayerMove.Forward).Select(a => a.Id));
    }

    [Fact]
    public void AnUnknownAnnotationLeavesTheOrderAlone()
    {
        var layered = Layers();
        Assert.Equal(layered.Select(a => a.Id), AnnotationSupport.Reordering(layered, Guid.NewGuid(), LayerMove.Forward).Select(a => a.Id));
    }

    [Fact]
    public void EachDirectionIsOfferedOnlyWhileTheAnnotationCanStillTakeIt()
    {
        var layered = Layers();
        Assert.False(AnnotationSupport.CanReorder(layered, layered[0].Id, LayerMove.Backward));
        Assert.True(AnnotationSupport.CanReorder(layered, layered[0].Id, LayerMove.Forward));
        Assert.False(AnnotationSupport.CanReorder(layered, layered[2].Id, LayerMove.Forward));
        Assert.True(AnnotationSupport.CanReorder(layered, layered[2].Id, LayerMove.Backward));
        Assert.True(AnnotationSupport.CanReorder(layered, layered[1].Id, LayerMove.Forward));
        Assert.True(AnnotationSupport.CanReorder(layered, layered[1].Id, LayerMove.Backward));
    }

    [Fact]
    public void AnAnnotationThatIsNotThereCanNeverBeReordered()
    {
        var layered = Layers();
        Assert.False(AnnotationSupport.CanReorder(layered, Guid.NewGuid(), LayerMove.Forward));
        Assert.False(AnnotationSupport.CanReorder([], layered[0].Id, LayerMove.Backward));
    }

    [Fact]
    public void ReorderingDoesNotMutateTheInput()
    {
        var layered = Layers();
        var ids = layered.Select(a => a.Id).ToList();
        AnnotationSupport.Reordering(layered, layered[1].Id, LayerMove.Forward);
        Assert.Equal(ids, layered.Select(a => a.Id));
    }

    [Fact]
    public void CounterBadgesScaleWithTheCapture()
    {
        Assert.Equal(22, AnnotationGeometry.CounterDiameter(new SizeD(400, 300), 1));
        Assert.Equal(37.5, AnnotationGeometry.CounterDiameter(new SizeD(1200, 900), 1));
        Assert.Equal(75, AnnotationGeometry.CounterDiameter(new SizeD(1200, 900), 2));
        Assert.Equal(22, AnnotationGeometry.CounterDiameter(new SizeD(10, 10), 1));
    }
}

public class ScreenshotCanvasSelectionStyleTests
{
    [Fact]
    public void SelectingAThinArrowExposesItsOwnStroke()
    {
        var thick = new Annotation(ScreenshotTool.Arrow, stroke: StrokeWidth.Large);
        var thin = new Annotation(ScreenshotTool.Arrow, stroke: StrokeWidth.Small);
        Assert.Equal(StrokeWidth.Small, AnnotationSupport.SelectionStyleFor(thin).Stroke);
        Assert.NotEqual(AnnotationSupport.SelectionStyleFor(thin), AnnotationSupport.SelectionStyleFor(thick));
        Assert.Equal(ArrowStyle.Filled, AnnotationSupport.SelectionStyleFor(thin).ArrowStyle);
    }

    [Fact]
    public void SelectionSyncLeavesUnusedStickerAndPixelationControlsAlone()
    {
        var sticker = AnnotationSupport.SelectionStyleFor(new Annotation(ScreenshotTool.Sticker, color: AnnotationColor.Blue, stroke: StrokeWidth.Large));
        var pixelate = AnnotationSupport.SelectionStyleFor(new Annotation(ScreenshotTool.Pixelate, color: AnnotationColor.Green, stroke: StrokeWidth.Large));
        var highlight = AnnotationSupport.SelectionStyleFor(new Annotation(ScreenshotTool.Highlight, color: AnnotationColor.Yellow, stroke: StrokeWidth.Large));
        Assert.Equal(new SelectionStyle(null, null, null), sticker);
        Assert.Equal(new SelectionStyle(null, null, null, BlurLevel: BlurStrength.DefaultLevel, BlurStyle: BlurStyle.Pixelate, BlurTextOnly: false), pixelate);
        Assert.Equal(AnnotationColor.Yellow, highlight.Color);
        Assert.Null(highlight.Stroke);
        Assert.Null(highlight.ArrowStyle);
    }

    [Fact]
    public void PickingABlurAreaShowsItsOwnStyleStrengthAndTextOnlyChoice()
    {
        var erased = new Annotation(ScreenshotTool.Pixelate, blurLevel: 2, blurStyle: BlurStyle.Erase, blurTextOnly: true);
        Assert.Equal(new SelectionStyle(null, null, null, BlurLevel: 2, BlurStyle: BlurStyle.Erase, BlurTextOnly: true),
                     AnnotationSupport.SelectionStyleFor(erased));
    }

    [Fact]
    public void TextExposesItsOwnPointSizeNotTheShapeThickness()
    {
        var big = new Annotation(ScreenshotTool.Text, stroke: StrokeWidth.Small, textSize: 48);
        Assert.Equal(new SelectionStyle(AnnotationColor.Red, null, null, TextSize: 48), AnnotationSupport.SelectionStyleFor(big));
        Assert.Null(AnnotationSupport.SelectionStyleFor(new Annotation(ScreenshotTool.Arrow)).TextSize);
    }

    [Theory]
    [InlineData(ScreenshotTool.Line)]
    [InlineData(ScreenshotTool.Rect)]
    [InlineData(ScreenshotTool.Ellipse)]
    [InlineData(ScreenshotTool.Freehand)]
    public void ShapesExposeColourAndStrokeOnly(ScreenshotTool tool)
    {
        var style = AnnotationSupport.SelectionStyleFor(new Annotation(tool, color: AnnotationColor.Purple, stroke: StrokeWidth.Large));
        Assert.Equal(new SelectionStyle(AnnotationColor.Purple, StrokeWidth.Large, null), style);
    }

    [Theory]
    [InlineData(ScreenshotTool.Highlight)]
    [InlineData(ScreenshotTool.Counter)]
    [InlineData(ScreenshotTool.Redact)]
    public void AreaMarksExposeColourOnly(ScreenshotTool tool)
    {
        var style = AnnotationSupport.SelectionStyleFor(new Annotation(tool, color: AnnotationColor.Orange, stroke: StrokeWidth.Large));
        Assert.Equal(new SelectionStyle(AnnotationColor.Orange, null, null), style);
    }

    [Theory]
    [InlineData(ScreenshotTool.Select)]
    [InlineData(ScreenshotTool.Crop)]
    [InlineData(ScreenshotTool.Spotlight)]
    [InlineData(ScreenshotTool.ExpandCanvas)]
    [InlineData(ScreenshotTool.Image)]
    [InlineData(ScreenshotTool.Sticker)]
    public void MarksWithNoControlsExposeNothing(ScreenshotTool tool) =>
        Assert.Equal(new SelectionStyle(null, null, null), AnnotationSupport.SelectionStyleFor(new Annotation(tool, color: AnnotationColor.Blue)));

    [Fact]
    public void PixelatingMosaicLevelsKeepSamplesOnlyForTheLevelsInUse()
    {
        var light = new Annotation(ScreenshotTool.Pixelate, blurLevel: 1);
        var strong = new Annotation(ScreenshotTool.Pixelate, blurLevel: 5);
        var outlined = new Annotation(ScreenshotTool.Rect, blurLevel: 4);
        Assert.Equal(new HashSet<int> { 1, 5 }, AnnotationGeometry.MosaicLevels([light, strong, strong, outlined]));
        Assert.Empty(AnnotationGeometry.MosaicLevels([outlined]));
    }

    [Fact]
    public void EachBlurStyleKeepsSamplesOnlyForItsOwnLevelsAndErasingKeepsNone()
    {
        var light = new Annotation(ScreenshotTool.Pixelate, blurLevel: 1);
        var soft = new Annotation(ScreenshotTool.Pixelate, blurLevel: 4, blurStyle: BlurStyle.Blur);
        var erased = new Annotation(ScreenshotTool.Pixelate, blurLevel: 2, blurStyle: BlurStyle.Erase, blurTextOnly: true);
        Assert.Equal(new HashSet<int> { 1 }, AnnotationGeometry.MosaicLevels([light, soft, erased]));
        Assert.Equal(new HashSet<int> { 4 }, AnnotationGeometry.SoftBlurLevels([light, soft, erased]));
    }
}

public class ScreenshotCanvasGeometryTests
{
    private static PointD P(double x, double y) => new(x, y);
    private static RectD R(double x, double y, double w, double h) => new(x, y, w, h);

    // ---- Selection rectangles and crop snapping ------------------------------------------------------------------

    [Fact]
    public void ADragInAnyDirectionNormalizesToAPositiveRect() =>
        Assert.Equal(R(40, 80, 60, 120), AnnotationGeometry.SelectionRect(P(100, 80), P(40, 200)));

    [Fact]
    public void ShiftConstrainsTheSelectionToASquare()
    {
        var square = AnnotationGeometry.SelectionRect(P(10, 10), P(40, 90), square: true);
        Assert.Equal(square.Width, square.Height);
        Assert.Equal(80, square.Width);
    }

    [Fact]
    public void ASquareDragKeepsItsDirection() =>
        Assert.Equal(R(-70, -70, 80, 80), AnnotationGeometry.SelectionRect(P(10, 10), P(-40, -70), square: true));

    [Fact]
    public void OptionGrowsTheSelectionFromTheCenter() =>
        Assert.Equal(R(30, 40, 40, 20), AnnotationGeometry.SelectionRect(P(50, 50), P(70, 60), fromCenter: true));

    [Fact]
    public void ADragWithNoMovementIsAnEmptyRect() =>
        Assert.True(AnnotationGeometry.SelectionRect(P(5, 5), P(5, 5)).IsEmpty());

    private static readonly RectD CropBounds = R(0, 0, 800, 600);
    private static readonly RectD CropDraft = R(120, 90, 400, 300);

    [Fact]
    public void DraggingInsideTheInitialFullImageCropStartsANewSelection() =>
        Assert.True(AnnotationGeometry.StartsNewCropSelection(P(300, 250), CropBounds, CropBounds));

    [Fact]
    public void DraggingInsideAnAdjustedCropKeepsMovingIt() =>
        Assert.False(AnnotationGeometry.StartsNewCropSelection(P(300, 250), CropDraft, CropBounds));

    [Fact]
    public void DraggingElsewhereInTheImageReplacesAnAdjustedCrop() =>
        Assert.True(AnnotationGeometry.StartsNewCropSelection(P(700, 500), CropDraft, CropBounds));

    [Fact]
    public void ADragOutsideTheImageCannotStartACrop() =>
        Assert.False(AnnotationGeometry.StartsNewCropSelection(P(900, 700), CropDraft, CropBounds));

    [Fact]
    public void ACropDraftRoundsEachEdgeToTheNearestPixelBoundary()
    {
        var snapped = AnnotationGeometry.PixelSnappedCropRect(R(100.4, 60.4, 100, 100), CropBounds);
        Assert.Equal(R(100, 60, 100, 100), snapped);
        Assert.Equal(200, snapped.MaxX());
    }

    [Fact]
    public void SnappingADraftThatAlreadySitsOnPixelsChangesNothing()
    {
        var snapped = R(100, 60, 100, 100);
        Assert.Equal(snapped, AnnotationGeometry.PixelSnappedCropRect(snapped, CropBounds));
    }

    [Fact]
    public void ACropEdgePastTheHalfwayMarkRoundsToTheNextBoundary() =>
        Assert.Equal(R(100, 60, 101, 101), AnnotationGeometry.PixelSnappedCropRect(R(100.4, 60.4, 100.2, 100.2), CropBounds));

    [Fact]
    public void MovingACropDraftNeverChangesItsSize()
    {
        var snapped = R(100, 60, 100, 100);
        var moved = AnnotationGeometry.PixelSnappedCropRect(AnnotationGeometry.MovedRect(snapped, P(12.6, -4.3), CropBounds), CropBounds);
        Assert.Equal(snapped.Size(), moved.Size());
    }

    [Fact]
    public void ASnappedCropDraftStaysInsideTheImage() =>
        Assert.Equal(R(0, 0, 60, 70), AnnotationGeometry.PixelSnappedCropRect(R(-40, -30, 100, 100), CropBounds));

    [Fact]
    public void ADraftFullyOutsideTheImageSnapsToNothing() =>
        Assert.Equal(default, AnnotationGeometry.PixelSnappedCropRect(R(900, 900, 50, 50), CropBounds));

    [Fact]
    public void RoundingHalvesGoAwayFromZeroLikeSwift() =>
        Assert.Equal(R(1, 1, 2, 2), AnnotationGeometry.PixelSnappedCropRect(R(0.5, 0.5, 2, 2), CropBounds));

    [Fact]
    public void DraggingAHandleResizesTheRect() =>
        Assert.Equal(R(10, 10, 40, 50), AnnotationGeometry.ResizedRect(R(10, 10, 100, 100), SelectionHandle.BottomRight, P(50, 60)));

    [Fact]
    public void DraggingAHandleAcrossTheOppositeEdgeFlipsInsteadOfGoingNegative()
    {
        var crossed = AnnotationGeometry.ResizedRect(R(10, 10, 100, 100), SelectionHandle.Right, P(0, 0));
        Assert.Equal(10, crossed.Width);
        Assert.Equal(0, crossed.MinX());
    }

    [Theory]
    [InlineData(SelectionHandle.TopLeft, 20, 30, 20, 30, 90, 80)]
    [InlineData(SelectionHandle.Top, 20, 30, 10, 30, 100, 80)]
    [InlineData(SelectionHandle.TopRight, 20, 30, 10, 30, 10, 80)]
    [InlineData(SelectionHandle.Right, 20, 30, 10, 10, 10, 100)]
    [InlineData(SelectionHandle.Bottom, 20, 30, 10, 10, 100, 20)]
    [InlineData(SelectionHandle.BottomLeft, 20, 30, 20, 10, 90, 20)]
    [InlineData(SelectionHandle.Left, 20, 30, 20, 10, 90, 100)]
    public void EveryHandleMovesOnlyItsOwnEdges(SelectionHandle handle, double px, double py, double ex, double ey, double ew, double eh)
    {
        var result = AnnotationGeometry.ResizedRect(R(10, 10, 100, 100), handle, P(px, py));
        Assert.Equal(R(ex, ey, Math.Abs(ew), Math.Abs(eh)), result);
    }

    [Fact]
    public void DraggingInsideACropMovesItWithoutResizingPastTheImageEdges() =>
        Assert.Equal(R(40, 0, 80, 60), AnnotationGeometry.MovedRect(R(20, 30, 80, 60), P(50, -100), R(0, 0, 120, 100)));

    [Fact]
    public void MovingARectLargerThanTheBoundsClipsItInstead() =>
        Assert.Equal(R(0, 0, 120, 100), AnnotationGeometry.MovedRect(R(-10, -10, 200, 150), P(5, 5), R(0, 0, 120, 100)));

    [Fact]
    public void HandleHitTestingFindsTheCorner() =>
        Assert.Equal(SelectionHandle.TopLeft, AnnotationGeometry.HandleAt(P(10, 10), R(10, 10, 100, 100), 6));

    [Fact]
    public void HandleHitTestingMissesTheInteriorAndFindsAllEight()
    {
        var rect = R(10, 10, 100, 100);
        Assert.Null(AnnotationGeometry.HandleAt(P(60, 60), rect, 6));
        foreach (var handle in SelectionHandles.All) Assert.Equal(handle, AnnotationGeometry.HandleAt(handle.Position(rect), rect, 6));
    }

    [Fact]
    public void HandlePositionsSitOnTheEdgesOfATopLeftRect()
    {
        var rect = R(10, 20, 100, 60);
        Assert.Equal(P(10, 20), SelectionHandle.TopLeft.Position(rect));
        Assert.Equal(P(60, 20), SelectionHandle.Top.Position(rect));
        Assert.Equal(P(110, 20), SelectionHandle.TopRight.Position(rect));
        Assert.Equal(P(110, 50), SelectionHandle.Right.Position(rect));
        Assert.Equal(P(110, 80), SelectionHandle.BottomRight.Position(rect));
        Assert.Equal(P(60, 80), SelectionHandle.Bottom.Position(rect));
        Assert.Equal(P(10, 80), SelectionHandle.BottomLeft.Position(rect));
        Assert.Equal(P(10, 50), SelectionHandle.Left.Position(rect));
    }

    // ---- Stickers ------------------------------------------------------------------------------------------------

    [Fact]
    public void AStickerPlacedAtAnEdgeStaysFullyInsideTheImage() =>
        Assert.Equal(R(0, 0, 40, 40), AnnotationGeometry.StickerRect(P(5, 5), 40, R(0, 0, 100, 80)));

    [Fact]
    public void AStickerNeverExceedsTheImage() =>
        Assert.Equal(R(0, 35, 30, 30), AnnotationGeometry.StickerRect(P(50, 50), 500, R(0, 0, 30, 90)));

    [Fact]
    public void AStickerAtTheFarCornerIsPulledBackInside() =>
        Assert.Equal(R(60, 40, 40, 40), AnnotationGeometry.StickerRect(P(99, 79), 40, R(0, 0, 100, 80)));

    [Theory]
    [InlineData(1000, 800, 1, 128)]
    [InlineData(100, 100, 1, 45)]
    [InlineData(400, 300, 1, 52)]
    [InlineData(1000, 800, 2, 128)]
    [InlineData(4000, 3000, 2, 256)]
    [InlineData(1, 1, 1, 1)]
    public void StickerSideScalesWithTheCapture(double w, double h, double scale, double expected) =>
        Assert.Equal(expected, AnnotationGeometry.StickerSide(new SizeD(w, h), scale));

    // ---- Segment distance ----------------------------------------------------------------------------------------

    [Fact]
    public void SegmentDistanceMeasuresPerpendicularOffset() =>
        Assert.True(Math.Abs(AnnotationGeometry.Distance(P(50, 10), P(0, 0), P(100, 0)) - 10) < 0.001);

    [Fact]
    public void SegmentDistanceClampsToTheEndpoints()
    {
        Assert.Equal(30, AnnotationGeometry.Distance(P(-30, 0), P(0, 0), P(100, 0)));
        Assert.Equal(20, AnnotationGeometry.Distance(P(120, 0), P(0, 0), P(100, 0)));
    }

    [Fact]
    public void ADegenerateSegmentMeasuresToItsPoint() =>
        Assert.Equal(5, AnnotationGeometry.Distance(P(3, 4), P(0, 0), P(0, 0)));

    // ---- Arrows --------------------------------------------------------------------------------------------------

    [Fact]
    public void ArrowHeadsOpenBehindTheTip()
    {
        var head = AnnotationGeometry.ArrowHead(P(0, 0), P(100, 0), 4);
        Assert.True(head.Left.X < 100 && head.Right.X < 100);
        Assert.NotEqual(head.Left.Y, head.Right.Y);
    }

    [Fact]
    public void AShortArrowHeadNeverExtendsBehindItsTail()
    {
        var head = AnnotationGeometry.ArrowHead(P(0, 0), P(10, 0), 14);
        Assert.True(head.Left.X >= 0 && head.Right.X >= 0);
    }

    [Fact]
    public void TheArrowHeadGrowsWithTheStroke()
    {
        var thin = AnnotationGeometry.ArrowHead(P(0, 0), P(200, 0), 2);
        var thick = AnnotationGeometry.ArrowHead(P(0, 0), P(200, 0), 7);
        Assert.True(thick.Left.X < thin.Left.X);
    }

    [Fact]
    public void TheArrowTailStaysFilledWhereItsCapMeetsTheShaft()
    {
        var path = AnnotationGeometry.ArrowSilhouette(P(100, 20), P(100, 300), 40);
        Assert.True(AnnotationGeometry.PolygonContains(path, P(100, 30)));
    }

    [Fact]
    public void TheArrowStaysFilledWhereItsShaftMeetsTheHead()
    {
        var path = AnnotationGeometry.ArrowSilhouette(P(100, 20), P(100, 300), 40);
        Assert.True(AnnotationGeometry.PolygonContains(path, P(100, 190)));
    }

    [Fact]
    public void TheArrowSilhouetteIsHollowOutsideItsOutline()
    {
        var path = AnnotationGeometry.ArrowSilhouette(P(100, 20), P(100, 300), 40);
        Assert.False(AnnotationGeometry.PolygonContains(path, P(200, 100)));
        Assert.False(AnnotationGeometry.PolygonContains(path, P(100, 310)));
    }

    [Fact]
    public void TheRoundTailCapReachesBehindTheTail()
    {
        var path = AnnotationGeometry.ArrowSilhouette(P(100, 20), P(100, 300), 40);
        Assert.True(AnnotationGeometry.PolygonContains(path, P(100, 5)));
        Assert.True(path.Min(p => p.Y) >= 0 - 0.001 && path.Min(p => p.Y) <= 0.001);
    }

    [Fact]
    public void TheSilhouetteContourStartsAndEndsOnTheTailEdge()
    {
        var path = AnnotationGeometry.ArrowSilhouette(P(0, 0), P(100, 0), 10);
        Assert.Equal(P(0, 5).Y, path[0].Y, 6);
        Assert.Equal(path[0].X, path[^1].X, 6);
        Assert.Equal(path[0].Y, path[^1].Y, 6);
    }

    [Fact]
    public void ScribblyArrowsVaryBySeedButKeepOneStableDesignWhenRedrawn()
    {
        var stable = AnnotationGeometry.ScribblyArrow(P(0, 0), P(160, 80), 4, 17);
        var same = AnnotationGeometry.ScribblyArrow(P(0, 0), P(160, 80), 4, 17);
        var different = AnnotationGeometry.ScribblyArrow(P(0, 0), P(160, 80), 4, 18);
        Assert.Equal(stable, same);
        Assert.NotEqual(stable, different);
        Assert.True(stable.Shaft.Count > 2);
    }

    [Fact]
    public void AScribbleStartsAtTheTailAndItsWingsEndOnTheTip()
    {
        var geometry = AnnotationGeometry.ScribblyArrow(P(10, 20), P(170, 100), 4, 17);
        Assert.Equal(P(10, 20), geometry.Shaft[0]);
        Assert.Equal(P(170, 100), geometry.LeftWing[^1]);
        Assert.Equal(P(170, 100), geometry.RightWing[^1]);
        Assert.Equal(4, geometry.LeftWing.Count);
        Assert.Equal(4, geometry.RightWing.Count);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(17ul)]
    [InlineData(ulong.MaxValue)]
    public void AnySeedGivesAFiniteStableSketch(ulong seed)
    {
        var a = AnnotationGeometry.ScribblyArrow(P(0, 0), P(100, 40), 4, seed);
        var b = AnnotationGeometry.ScribblyArrow(P(0, 0), P(100, 40), 4, seed);
        Assert.Equal(a, b);
        Assert.All(a.Shaft, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    [Fact]
    public void AZeroSeedIsTheSameAsTheGoldenRatioSeed() =>
        Assert.Equal(AnnotationGeometry.ScribblyArrow(P(0, 0), P(100, 40), 4, 0),
                     AnnotationGeometry.ScribblyArrow(P(0, 0), P(100, 40), 4, 0x9E3779B97F4A7C15UL));

    [Fact]
    public void TheSeededGeneratorIsPinnedToItsDocumentedRecurrence()
    {
        // state = state * 2862933555777941757 + 3037000493 (wrapping), draw = state / 2^64 * 2 - 1. The shaft's first interior
        // point is the first two draws; recompute it here independently so a changed constant is noticed.
        ulong state = 17;
        double Next()
        {
            unchecked { state = state * 2862933555777941757UL + 3037000493UL; }
            return (double)state / (double)ulong.MaxValue * 2 - 1;
        }
        var tail = P(0, 0);
        var tip = P(160, 80);
        var geometry = AnnotationGeometry.ScribblyArrow(tail, tip, 4, 17);
        var head = AnnotationGeometry.ArrowHead(tail, tip, 4);
        var baseMid = P((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        double distance = Math.Sqrt(160.0 * 160 + 80 * 80);
        double angle = Math.Atan2(80, 160);
        var direction = P(Math.Cos(angle), Math.Sin(angle));
        var perpendicular = P(-direction.Y, direction.X);
        int segments = Math.Max(4, Math.Min(24, (int)Math.Ceiling(distance / Math.Max(10, 4 * 3.0))));
        double wobble = Math.Min(Math.Max(1, 4 * 0.35), distance * 0.025);
        double progress = 1.0 / segments;
        double envelope = Math.Sin(Math.PI * progress);
        double side = Next() * wobble * envelope;
        double forward = Next() * wobble * 0.28 * envelope;
        double lineX = baseMid.X - tail.X, lineY = baseMid.Y - tail.Y;
        var expected = P(tail.X + lineX * progress + perpendicular.X * side + direction.X * forward,
                         tail.Y + lineY * progress + perpendicular.Y * side + direction.Y * forward);
        Assert.Equal(expected.X, geometry.Shaft[1].X, 9);
        Assert.Equal(expected.Y, geometry.Shaft[1].Y, 9);
    }

    [Fact]
    public void TheSolidArrowIsAFilledSilhouetteAndEveryOtherStyleIsStrokedPaths()
    {
        Assert.Null(AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.Filled, 0));
        foreach (var style in Enum.GetValues<ArrowStyle>().Where(s => s != ArrowStyle.Filled))
        {
            var path = AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, style, 17);
            Assert.NotNull(path);
            Assert.True(AnnotationGeometry.BoundingBox(path!).Width >= 100, style.ToString());
        }
    }

    [Fact]
    public void StrokedArrowStylesHaveTheirOwnSubpaths()
    {
        Assert.Equal(2, AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.Open, 0)!.Count);
        Assert.Equal(3, AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.DoubleEnded, 0)!.Count);
        Assert.Equal(3, AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.Scribbly, 5)!.Count);
        var outline = AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.Outline, 0)!;
        Assert.Equal(2, outline.Count);
        Assert.True(outline[1].Closed);
        Assert.False(outline[0].Closed);
    }

    [Fact]
    public void ADoubleEndedArrowHasAHeadOnTheTailToo()
    {
        var path = AnnotationGeometry.ArrowStrokePath(P(0, 0), P(100, 0), 4, ArrowStyle.DoubleEnded, 0)!;
        Assert.Equal(P(0, 0), path[2].Points[1]);
        Assert.Equal(P(100, 0), path[1].Points[1]);
    }

    // ---- Drag tolerance ------------------------------------------------------------------------------------------

    private static EditorDrag Drag(PointD start) => EditorDrag.Started(start);

    [Fact]
    public void AStrokeEndingNearItsStartRemainsADragForThePen()
    {
        var drag = Drag(P(100, 100));
        drag.Update(P(150, 100));
        drag.Update(P(103, 102));
        Assert.False(drag.IsTap(ScreenshotTool.Freehand));
        foreach (var tool in ScreenshotTools.All.Where(t => t != ScreenshotTool.Freehand))
            Assert.True(drag.IsTap(tool), tool.ToString());
    }

    [Fact]
    public void AClosedPenStrokeRemainsADragButOtherToolsDiscardIt()
    {
        var drag = Drag(P(100, 100));
        drag.Update(P(150, 100));
        drag.Update(P(100, 100));
        Assert.False(drag.IsTap(ScreenshotTool.Freehand));
        foreach (var tool in ScreenshotTools.All.Where(t => t != ScreenshotTool.Freehand))
            Assert.True(drag.IsTap(tool), tool.ToString());
    }

    [Fact]
    public void ANewClickResetsPriorMovementAndToleratesSmallJitter()
    {
        var drag = Drag(P(100, 100));
        drag.Update(P(150, 100));
        drag.Begin(P(100, 100));
        foreach (var p in new[] { P(100, 100), P(103, 102), P(97, 98), P(100, 100) }) drag.Update(p);
        Assert.All(ScreenshotTools.All, t => Assert.True(drag.IsTap(t), t.ToString()));
    }

    [Fact]
    public void MovementAtTheSevenPointBoundaryIsADragForEveryTool()
    {
        var drag = Drag(P(100, 100));
        drag.Update(P(107, 100));
        Assert.All(ScreenshotTools.All, t => Assert.False(drag.IsTap(t), t.ToString()));
    }

    [Fact]
    public void MovementDeliveredOnlyAtReleaseStillCountsForEveryTool()
    {
        var drag = Drag(P(100, 100));
        drag.Update(P(100, 100));
        drag.Update(P(120, 100));
        Assert.All(ScreenshotTools.All, t => Assert.False(drag.IsTap(t), t.ToString()));
    }

    [Theory]
    [InlineData(0.125)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void ClosedPenStrokesSurviveSparseAndDenseSamples(double zoom)
    {
        PointD[] square = [P(100, 100), P(180, 100), P(180, 180), P(100, 180), P(100, 100)];
        var dense = new List<PointD>();
        for (int i = 0; i < square.Length - 1; i++)
            for (int step = 0; step <= 40; step++)
                dense.Add(P(square[i].X + (square[i + 1].X - square[i].X) * step / 40.0,
                            square[i].Y + (square[i + 1].Y - square[i].Y) * step / 40.0));
        foreach (var path in new[] { square.ToList(), dense })
        {
            var drag = Drag(P(path[0].X * zoom, path[0].Y * zoom));
            foreach (var p in path.Skip(1)) drag.Update(P(p.X * zoom, p.Y * zoom));
            Assert.False(drag.IsTap(ScreenshotTool.Freehand));
        }
    }

    // ---- Recognized words -----------------------------------------------------------------------------------------

    private static readonly RectD[] WordBoxes = [R(0, 0, 40, 10), R(50, 0, 40, 10), R(0, 20, 40, 10)];

    [Fact]
    public void ADragAcrossALineSelectsTheWordsItCrosses() =>
        Assert.Equal([0, 1], AnnotationGeometry.WordSelection(P(5, 5), P(60, 5), WordBoxes));

    [Fact]
    public void ADragAcrossLinesSelectsIntoTheNextLine() =>
        Assert.Equal([0, 2], AnnotationGeometry.WordSelection(P(5, 5), P(10, 25), WordBoxes));

    [Fact]
    public void AHairlineDragStillSelectsWhatItCrosses() =>
        Assert.Equal([0], AnnotationGeometry.WordSelection(P(20, 5), P(20, 5), WordBoxes));

    private static readonly RecognizedWord[] Words =
    [
        new("ola", WordBoxes[0], 0), new("mundo", WordBoxes[1], 0), new("linha", WordBoxes[2], 1),
    ];

    [Fact]
    public void SelectedWordsJoinInReadingOrderWithSpaces() =>
        Assert.Equal("ola mundo", AnnotationGeometry.JoinedWords(Words, [1, 0]));

    [Fact]
    public void LineChangesBecomeNewlines() =>
        Assert.Equal("ola\nlinha", AnnotationGeometry.JoinedWords(Words, [2, 0]));

    [Fact]
    public void OutOfRangeOrEmptySelectionsCopyNothing()
    {
        Assert.Equal("", AnnotationGeometry.JoinedWords(Words, [9]));
        Assert.Equal("", AnnotationGeometry.JoinedWords(Words, []));
        Assert.Equal("ola", AnnotationGeometry.JoinedWords(Words, [9, 0, -1]));
    }

    private static readonly RecognizedWord[] RunWords =
    [
        new("john", R(10, 10, 40, 20), 0), new("doe", R(56, 10, 30, 20), 0),
        new("total", R(300, 10, 50, 20), 0), new("next", R(10, 40, 40, 20), 1),
    ];

    private static void AssertRect(RectD expected, RectD actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) < 0.001 && Math.Abs(expected.Y - actual.Y) < 0.001
                    && Math.Abs(expected.Width - actual.Width) < 0.001 && Math.Abs(expected.Height - actual.Height) < 0.001,
                    $"{expected} vs {actual}");
    }

    [Fact]
    public void CloseWordsOnALineShareAPaddedRunFarOnesAndOtherLinesGetTheirOwn()
    {
        var runs = AnnotationGeometry.TextRuns(RunWords);
        Assert.Equal(3, runs.Count);
        AssertRect(R(-2, 4, 100, 32), runs[0]);
        AssertRect(R(288, 4, 74, 32), runs[1]);
        AssertRect(R(-2, 34, 64, 32), runs[2]);
    }

    [Fact]
    public void ZeroSizedWordsAreSkippedWhenBuildingRuns() =>
        Assert.Empty(AnnotationGeometry.TextRuns([new RecognizedWord("x", R(5, 5, 0, 10), 0)]));

    [Fact]
    public void ACropKeepsTheRunsStillOnItMovedAndAnUnreadCaptureStaysUnread()
    {
        var crop = R(20, 30, 200, 100);
        var carried = AnnotationGeometry.CroppedRuns([R(10, 20, 120, 20), R(400, 40, 50, 20)], crop);
        Assert.NotNull(carried);
        Assert.Single(carried!);
        AssertRect(R(-10, -10, 120, 20), carried[0]);
        Assert.Null(AnnotationGeometry.CroppedRuns(null, crop));
    }

    [Fact]
    public void ATextOnlyAreaCoversTheRunsItReachesAndAllOfItselfBeforeRecognition()
    {
        var runs = AnnotationGeometry.TextRuns(RunWords);
        var picked = AnnotationGeometry.BlurTextRuns(R(0, 0, 60, 30), runs);
        Assert.Single(picked);
        AssertRect(runs[0], picked[0]);
        Assert.Empty(AnnotationGeometry.BlurTextRuns(R(400, 0, 20, 20), runs));
        Assert.Equal([R(1, 2, 3, 4)], AnnotationGeometry.BlurTextRuns(R(1, 2, 3, 4), null));
    }
}

public class ScreenshotCanvasRectMathTests
{
    [Fact]
    public void ContainsIsHalfOpenLikeCGRect()
    {
        var r = new RectD(0, 0, 10, 10);
        Assert.True(r.ContainsPoint(new PointD(0, 0)));
        Assert.True(r.ContainsPoint(new PointD(9.99, 9.99)));
        Assert.False(r.ContainsPoint(new PointD(10, 5)));
        Assert.False(r.ContainsPoint(new PointD(5, 10)));
    }

    [Fact]
    public void AnEmptyRectContainsNothing() => Assert.False(new RectD(5, 5, 0, 0).ContainsPoint(new PointD(5, 5)));

    [Fact]
    public void RectsSharingOnlyAnEdgeDoNotIntersect()
    {
        Assert.False(new RectD(0, 0, 10, 10).Intersects(new RectD(10, 0, 10, 10)));
        Assert.True(new RectD(0, 0, 10, 10).Intersects(new RectD(9, 9, 10, 10)));
    }

    [Fact]
    public void IntersectionAndUnionAreTheObviousBoxes()
    {
        Assert.Equal(new RectD(5, 5, 5, 5), new RectD(0, 0, 10, 10).Intersection(new RectD(5, 5, 10, 10)));
        Assert.Equal(new RectD(0, 0, 15, 15), new RectD(0, 0, 10, 10).Union(new RectD(5, 5, 10, 10)));
    }

    [Fact]
    public void InsettingPastNothingComesBackEmpty()
    {
        Assert.True(new RectD(0, 0, 10, 10).InsetBy(6, 6).IsEmpty());
        Assert.Equal(new RectD(-5, -5, 20, 20), new RectD(0, 0, 10, 10).InsetBy(-5, -5));
    }

    [Fact]
    public void IntegralCoversTheRect() =>
        Assert.Equal(new RectD(1, 2, 4, 4), new RectD(1.4, 2.6, 3, 3).Integral());

    [Fact]
    public void StandardizedFlipsNegativeSizes() =>
        Assert.Equal(new RectD(5, 5, 10, 10), new RectD(15, 15, -10, -10).Standardized());

    [Fact]
    public void RoundedGoesAwayFromZero()
    {
        Assert.Equal(3, RectMath.Rounded(2.5));
        Assert.Equal(-3, RectMath.Rounded(-2.5));
        Assert.Equal(2, RectMath.Rounded(2.4));
    }
}
