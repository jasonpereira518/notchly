// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/ScreenshotCanvasSupportTests.swift and the pure parts of ScreenshotImageLayerTests.swift and
// ScreenshotImageImportTests.swift: canvas expansion maths, expansion and image grips, image layer resizing and fitting,
// import sizing and the expansion preview layout. Pixel-level checks (expandedImage, rendering) belong to the WPF layer.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class ScreenshotCanvasSupportTests
{
    private static PointD P(double x, double y) => new(x, y);
    private static RectD R(double x, double y, double w, double h) => new(x, y, w, h);
    private static readonly SizeD Small = new(40, 40);
    private static readonly RectD SmallBounds = R(0, 0, 40, 40);

    // ---- Expansion grips -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SelectionHandle.TopLeft)]
    [InlineData(SelectionHandle.Top)]
    [InlineData(SelectionHandle.TopRight)]
    [InlineData(SelectionHandle.Right)]
    [InlineData(SelectionHandle.BottomRight)]
    [InlineData(SelectionHandle.Bottom)]
    [InlineData(SelectionHandle.BottomLeft)]
    [InlineData(SelectionHandle.Left)]
    public void ASmallRetinaCanvasPicksTheIntendedExpansionHandle(SelectionHandle handle) =>
        Assert.Equal(handle, CanvasSupport.ExpansionHandleAt(handle.Position(SmallBounds), SmallBounds, 28));

    [Fact]
    public void TheInsetBottomGripDoesNotResolveToTheRightEdge() =>
        Assert.Equal(SelectionHandle.Bottom, CanvasSupport.ExpansionHandleAt(P(20, 36), SmallBounds, 28));

    [Fact]
    public void TheCanvasInteriorDoesNotStartExpansion() =>
        Assert.Null(CanvasSupport.ExpansionHandleAt(P(20, 20), SmallBounds, 28));

    [Fact]
    public void AZoomedOutCanvasAcceptsTheDisplayedInsetGrip() =>
        Assert.Equal(SelectionHandle.TopLeft,
                     CanvasSupport.ExpansionHandleAt(P(40, 40), R(0, 0, 4000, 4000), 10 / 0.1));

    [Fact]
    public void APressOnAGripsOuterHalfPastTheCapturesEdgePicksItUp()
    {
        var bounds = RectMath.FromSize(new SizeD(40, 40));
        Assert.Equal(SelectionHandle.TopLeft, CanvasSupport.ExpansionHandleAt(P(-8, -8), bounds, CanvasSupport.ExpansionGripReach));
        Assert.Equal(SelectionHandle.Right, CanvasSupport.ExpansionHandleAt(P(48, 20), bounds, CanvasSupport.ExpansionGripReach));
    }

    [Fact]
    public void APressBeyondTheGripReachPicksNothing() =>
        Assert.Null(CanvasSupport.ExpansionHandleAt(P(-30, -30), SmallBounds, CanvasSupport.ExpansionGripReach));

    [Fact]
    public void AnEquallyNearPairKeepsTheFirstHandleInOrder()
    {
        // On a 4 pt square every grip lies within the radius of the centre; the radius is a quarter of the side (min 2).
        var tiny = R(0, 0, 8, 8);
        Assert.Equal(SelectionHandle.TopLeft, CanvasSupport.ExpansionHandleAt(P(0, 0), tiny, 28));
    }

    [Fact]
    public void ExpansionGripReachIsTenPoints() => Assert.Equal(10, CanvasSupport.ExpansionGripReach);

    // ---- Expanded rect ---------------------------------------------------------------------------------------------

    [Fact]
    public void BottomExpansionPreservesWidthAndAddsTwentyRows() =>
        Assert.Equal(R(0, 0, 40, 60), CanvasSupport.ExpandedRect(Small, SelectionHandle.Bottom, P(20, 60)));

    [Fact]
    public void TopLeftExpansionRetainsTheOldOriginInDraftSpace() =>
        Assert.Equal(R(-10, -20, 50, 60), CanvasSupport.ExpandedRect(Small, SelectionHandle.TopLeft, P(-10, -20)));

    [Fact]
    public void AnInwardDragCannotCrop() =>
        Assert.Equal(SmallBounds, CanvasSupport.ExpandedRect(Small, SelectionHandle.Bottom, P(20, 10)));

    [Theory]
    [InlineData(SelectionHandle.Top, 20, -5, 0, -5, 40, 45)]
    [InlineData(SelectionHandle.Left, -8, 20, -8, 0, 48, 40)]
    [InlineData(SelectionHandle.Right, 55, 20, 0, 0, 55, 40)]
    [InlineData(SelectionHandle.TopRight, 55, -5, 0, -5, 55, 45)]
    [InlineData(SelectionHandle.BottomRight, 55, 70, 0, 0, 55, 70)]
    [InlineData(SelectionHandle.BottomLeft, -8, 70, -8, 0, 48, 70)]
    public void EachGripGrowsOnlyItsOwnEdges(SelectionHandle handle, double px, double py, double ex, double ey, double ew, double eh) =>
        Assert.Equal(R(ex, ey, ew, eh), CanvasSupport.ExpandedRect(Small, handle, P(px, py)));

    [Fact]
    public void ExpansionRoundsFractionalDragsToWholePixels() =>
        Assert.Equal(R(0, 0, 40, 61), CanvasSupport.ExpandedRect(Small, SelectionHandle.Bottom, P(20, 60.5)));

    [Fact]
    public void ExpansionRejectsNonFinitePointsAndInvalidSizes()
    {
        Assert.Null(CanvasSupport.ExpandedRect(Small, SelectionHandle.Bottom, P(0, double.NaN)));
        Assert.Null(CanvasSupport.ExpandedRect(Small, SelectionHandle.Right, P(double.PositiveInfinity, 0)));
        Assert.Null(CanvasSupport.ExpandedRect(new SizeD(0, 10), SelectionHandle.Bottom, P(0, 20)));
        Assert.Null(CanvasSupport.ExpandedRect(Small, SelectionHandle.Bottom, P(0, 1e9)));
    }

    // ---- Valid size -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheCanvasRejectsPixelOverflowBeforeAllocation() =>
        Assert.False(CanvasSupport.ValidSize(new SizeD(60_000_001, 1)));

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(40, 40, true)]
    [InlineData(7745, 7745, true)]
    [InlineData(7746, 7746, false)]
    [InlineData(60_000_000, 1, true)]
    [InlineData(30_000_000, 2, true)]
    [InlineData(30_000_001, 2, false)]
    [InlineData(0, 10, false)]
    [InlineData(10, 0.5, false)]
    [InlineData(10.5, 10, false)]
    [InlineData(-5, 10, false)]
    public void ValidCanvasSizesAreWholePositiveAndWithinTheBudget(double w, double h, bool valid) =>
        Assert.Equal(valid, CanvasSupport.ValidSize(new SizeD(w, h)));

    [Fact]
    public void NonFiniteSizesAreInvalid()
    {
        Assert.False(CanvasSupport.ValidSize(new SizeD(double.NaN, 10)));
        Assert.False(CanvasSupport.ValidSize(new SizeD(10, double.PositiveInfinity)));
    }

    [Fact]
    public void TheCanvasBudgetIsSixtyMillionPixels() => Assert.Equal(60_000_000, CanvasSupport.MaximumPixels);

    // ---- Expansion legality and placement --------------------------------------------------------------------------

    [Fact]
    public void AnExpansionMustCoverTheOldCanvasOnWholePixels()
    {
        Assert.True(CanvasSupport.IsExpansion(R(-10, -20, 50, 60), Small));
        Assert.True(CanvasSupport.IsExpansion(R(0, 0, 60, 40), Small));
        Assert.False(CanvasSupport.IsExpansion(R(0, 0, 1, 1), Small));
        Assert.False(CanvasSupport.IsExpansion(R(0.5, 0, 60, 40), Small));
        Assert.False(CanvasSupport.IsExpansion(R(5, 0, 60, 40), Small));
        Assert.False(CanvasSupport.IsExpansion(R(0, 0, 0, 0), Small));
    }

    [Fact]
    public void TheOldPictureSitsAtTheNegativeOfTheExpansionOrigin() =>
        Assert.Equal(R(10, 20, 40, 40), CanvasSupport.OldImagePlacement(R(-10, -20, 50, 60), Small));

    [Fact]
    public void FillsDescribeTransparentWhiteAndCustomColours()
    {
        Assert.True(CanvasFill.Transparent.IsTransparent);
        Assert.False(CanvasFill.White.IsTransparent);
        Assert.Equal(CanvasFill.White, CanvasFill.Color(1, 1, 1));
        Assert.NotEqual(CanvasFill.White, CanvasFill.Color(0, 0, 1));
    }

    // ---- Image layer fitting and resizing ---------------------------------------------------------------------------

    [Fact]
    public void ALargeImageFitsTheCanvasWithoutStretching() =>
        Assert.Equal(R(0, 10, 40, 20), CanvasSupport.FittedImageRect(new SizeD(100, 50), new SizeD(40, 40)));

    [Fact]
    public void ASmallImageKeepsItsSizeAndIsCentredOnWholePixels() =>
        Assert.Equal(R(4, 10, 31, 20), CanvasSupport.FittedImageRect(new SizeD(31, 20), new SizeD(40, 40)));

    [Fact]
    public void AnImageTheSizeOfTheCanvasFillsIt() =>
        Assert.Equal(R(0, 0, 40, 40), CanvasSupport.FittedImageRect(new SizeD(40, 40), new SizeD(40, 40)));

    [Fact]
    public void ATallImageFitsByHeight() =>
        Assert.Equal(R(15, 0, 10, 40), CanvasSupport.FittedImageRect(new SizeD(25, 100), new SizeD(40, 40)));

    private static readonly RectD ImageOrigin = R(10, 20, 40, 20);

    [Fact]
    public void ImageResizeFixesTheOppositeCornerAndAspectRatio() =>
        Assert.Equal(R(10, 20, 80, 40), CanvasSupport.ResizedImageRect(ImageOrigin, SelectionHandle.BottomRight, P(90, 60)));

    [Fact]
    public void ImageInteriorDragsMoveInsteadOfResize() =>
        Assert.Null(CanvasSupport.ImageHandleAt(P(30, 30), ImageOrigin, 12));

    [Theory]
    [InlineData(SelectionHandle.TopLeft)]
    [InlineData(SelectionHandle.Top)]
    [InlineData(SelectionHandle.TopRight)]
    [InlineData(SelectionHandle.Right)]
    [InlineData(SelectionHandle.BottomRight)]
    [InlineData(SelectionHandle.Bottom)]
    [InlineData(SelectionHandle.BottomLeft)]
    [InlineData(SelectionHandle.Left)]
    public void AllEightImageGripsCanBeSelectedWithoutOverlappingHits(SelectionHandle handle) =>
        Assert.Equal(handle, CanvasSupport.ImageHandleAt(handle.Position(ImageOrigin), ImageOrigin, 12));

    [Theory]
    [InlineData(SelectionHandle.Left, -10, 999, -10, 20, 60, 20)]
    [InlineData(SelectionHandle.Right, 90, -999, 10, 20, 80, 20)]
    [InlineData(SelectionHandle.Top, -999, 10, 10, 10, 40, 30)]
    [InlineData(SelectionHandle.Bottom, 999, 60, 10, 20, 40, 40)]
    public void AnImageEdgeGripStretchesOnlyItsAxisAndFixesTheOppositeEdge(
        SelectionHandle handle, double px, double py, double ex, double ey, double ew, double eh) =>
        Assert.Equal(R(ex, ey, ew, eh), CanvasSupport.ResizedImageRect(ImageOrigin, handle, P(px, py)));

    [Theory]
    [InlineData(SelectionHandle.TopLeft, -30, 0, -30, 0, 80, 40)]
    [InlineData(SelectionHandle.TopRight, 90, 0, 10, 0, 80, 40)]
    [InlineData(SelectionHandle.BottomLeft, -30, 60, -30, 20, 80, 40)]
    public void AnImageCornerGripKeepsTheAspectRatioAndTheOppositeCorner(
        SelectionHandle handle, double px, double py, double ex, double ey, double ew, double eh) =>
        Assert.Equal(R(ex, ey, ew, eh), CanvasSupport.ResizedImageRect(ImageOrigin, handle, P(px, py)));

    [Theory]
    [InlineData(SelectionHandle.Left)]
    [InlineData(SelectionHandle.Right)]
    [InlineData(SelectionHandle.Top)]
    [InlineData(SelectionHandle.Bottom)]
    public void AnImageEdgeGripCannotCollapseOrInvertTheImage(SelectionHandle handle)
    {
        var point = handle is SelectionHandle.Left or SelectionHandle.Top ? P(90, 60) : P(-10, 0);
        var rect = CanvasSupport.ResizedImageRect(ImageOrigin, handle, point);
        Assert.True(rect.Width >= 1 && rect.Height >= 1);
    }

    [Theory]
    [InlineData(SelectionHandle.TopLeft)]
    [InlineData(SelectionHandle.TopRight)]
    [InlineData(SelectionHandle.BottomLeft)]
    [InlineData(SelectionHandle.BottomRight)]
    public void AnImageCornerGripNeverShrinksBelowOnePoint(SelectionHandle handle)
    {
        var rect = CanvasSupport.ResizedImageRect(ImageOrigin, handle, handle.Position(ImageOrigin).Offset(500, 500));
        Assert.True(rect.Width >= 1 && rect.Height > 0);
    }

    [Fact]
    public void ResizingARectWithNoAreaChangesNothing() =>
        Assert.Equal(R(5, 5, 0, 10), CanvasSupport.ResizedImageRect(R(5, 5, 0, 10), SelectionHandle.Right, P(50, 50)));

    [Fact]
    public void ASmallImageUsesAtMostAQuarterOfItsSideAsTheGripRadius()
    {
        // A 8x8 layer: radius = min(12, max(2, 8/4)) = 2, so a press 3 points from a corner picks nothing.
        var tiny = R(0, 0, 8, 8);
        Assert.Null(CanvasSupport.ImageHandleAt(P(3, 3), tiny, 12));
        Assert.Equal(SelectionHandle.TopLeft, CanvasSupport.ImageHandleAt(P(1, 1), tiny, 12));
    }

    // ---- Image import sizing -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(6000, 4000, false)]
    [InlineData(6001, 10, true)]
    [InlineData(32, 6001, true)]
    [InlineData(40, 20, false)]
    public void OnlyPicturesPastSixThousandPixelsNeedASizeChoice(double w, double h, bool needs) =>
        Assert.Equal(needs, ImageImportSizing.RequiresSizeChoice(new SizeD(w, h)));

    [Theory]
    [InlineData(6001, 10)]
    [InlineData(7000, 3000)]
    [InlineData(12000, 12000)]
    [InlineData(32, 6001)]
    [InlineData(100, 100)]
    public void ReducedPicturesNeverExceedTheLimit(double w, double h)
    {
        int reduced = ImageImportSizing.ReducedMaximumDimension(new SizeD(w, h));
        Assert.True(reduced <= 6000);
        if (Math.Max(w, h) > 6000) Assert.Equal(6000, reduced);
        else Assert.Equal((int)Math.Max(w, h), reduced);
    }

    [Theory]
    [InlineData(1, 40, 20, 40, 20)]
    [InlineData(4, 40, 20, 40, 20)]
    [InlineData(5, 40, 20, 20, 40)]
    [InlineData(6, 40, 20, 20, 40)]
    [InlineData(8, 40, 20, 20, 40)]
    public void AQuarterTurnOrientationSwapsTheUprightSize(int orientation, int w, int h, double ew, double eh) =>
        Assert.Equal(new SizeD(ew, eh), ImageImportSizing.OrientedPixelSize(w, h, orientation));

    [Fact]
    public void TheImportLimitIsSixThousand() => Assert.Equal(6000, ImageImportSizing.MaximumDimension);

    // ---- Expansion preview layout ------------------------------------------------------------------------------------

    public static IEnumerable<object[]> PreviewCases() =>
    [
        [680.0, 460.0, 2.0],
        [600.0, 400.0, 1.0],
        [9.0, 6.0, 0.37],
    ];

    [Theory]
    [MemberData(nameof(PreviewCases))]
    public void EveryExpansionGripShowsWholeInsideThePreviewRingLineIncluded(double w, double h, double scale)
    {
        var layout = new ExpansionPreviewLayout(new SizeD(w, h), scale);
        var bounds = RectMath.FromSize(layout.Size);
        double half = layout.GripLineWidth / 2;
        Assert.Equal(new SizeD(w, h), layout.Draft.Size());
        Assert.Equal(8, layout.Grips.Count);
        Assert.All(layout.Grips, g => Assert.True(bounds.ContainsRect(g.InsetBy(-half, -half))));
    }

    [Theory]
    [MemberData(nameof(PreviewCases))]
    public void ThePreviewsExtraRoomIsWholeCheckerTiles(double w, double h, double scale)
    {
        var layout = new ExpansionPreviewLayout(new SizeD(w, h), scale);
        Assert.True(layout.Draft.MinX() > 0);
        Assert.Equal(0, layout.Draft.MinX() % 8);
        Assert.Equal(layout.Draft.MinX(), layout.Draft.MinY());
    }

    [Theory]
    [MemberData(nameof(PreviewCases))]
    public void EachGripIsCentredOnTheDraftEdgeAPressPicksUp(double w, double h, double scale)
    {
        var layout = new ExpansionPreviewLayout(new SizeD(w, h), scale);
        for (int i = 0; i < SelectionHandles.All.Count; i++)
        {
            var point = SelectionHandles.All[i].Position(layout.Draft);
            Assert.True(Math.Abs(layout.Grips[i].MidX - point.X) < 0.001);
            Assert.True(Math.Abs(layout.Grips[i].MidY() - point.Y) < 0.001);
        }
    }

    [Fact]
    public void TheGripRingScalesWithTheDisplayScale()
    {
        var one = new ExpansionPreviewLayout(new SizeD(100, 100), 1);
        var two = new ExpansionPreviewLayout(new SizeD(100, 100), 2);
        Assert.Equal(1.5, one.GripLineWidth);
        Assert.Equal(3, two.GripLineWidth);
        Assert.Equal(7, one.Grips[0].Width);
        Assert.Equal(14, two.Grips[0].Width);
    }

    private static readonly RectD Card = R(10, 10, 100, 60);

    [Fact]
    public void BeforeADragTheExpansionFillLeavesTheWholeCardOpen()
    {
        var radii = ExpansionPreviewLayout.CardHoleCorners(Card, Card, 4);
        Assert.Equal(new CardCornerRadii(0, 0, 0, 0), radii);
    }

    [Fact]
    public void TheFillRoundsOnlyTheCornersTheNewMarginMeets()
    {
        var wider = R(10, 10, 140, 60);
        var radii = ExpansionPreviewLayout.CardHoleCorners(Card, wider, 4);
        Assert.Equal(new CardCornerRadii(0, 5, 5, 0), radii);
    }

    [Fact]
    public void AMarginOnTheLeftRoundsTheLeftCorners()
    {
        var draft = R(0, 10, 110, 60);
        Assert.Equal(new CardCornerRadii(5, 0, 0, 5), ExpansionPreviewLayout.CardHoleCorners(Card, draft, 4));
    }

    [Fact]
    public void ASquareCardNeedsNoRounding() =>
        Assert.Equal(new CardCornerRadii(0, 0, 0, 0), ExpansionPreviewLayout.CardHoleCorners(Card, R(10, 10, 140, 60), 0));

    [Fact]
    public void AnEdgeTheExpansionDidNotMoveKeepsItsCornersOpenDespiteRoundingInTheScaledRects()
    {
        // A 7 px left expansion of a 601 px capture at 60%: the right edges then differ only by rounding.
        var layout = new ExpansionPreviewLayout(new SizeD(608 * 0.6, 400 * 0.6), 1.2);
        var shifted = new RectD(layout.Draft.MinX() - -7 * 0.6, layout.Draft.MinY() - 0 * 0.6, 601 * 0.6, 400 * 0.6);
        var radii = ExpansionPreviewLayout.CardHoleCorners(shifted, layout.Draft, 4);
        Assert.Equal(new CardCornerRadii(5, 0, 0, 5), radii);
    }
}
