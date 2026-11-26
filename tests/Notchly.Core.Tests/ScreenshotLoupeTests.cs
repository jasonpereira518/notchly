// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the capture and crop loupe contracts in upstream Tests/ScreenshotFeatureTests.swift
// (lines ~790-810 and ~3480-3694: captureLoupe* and cropLoupeSampleRect), plus the coordinate
// conversions at ~1606-1623 that the loupe reads its pixel from.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class LoupeSampleTests
{
    [Fact]
    public void TheCropLoupeCentersItsSourcePixelsAroundAnInnerGrip() =>
        Assert.Equal(new RectD(44, 34, 13, 13), ScreenshotLoupe.CropSampleRect(new PointD(50, 40), new SizeD(100, 80)));

    [Fact]
    public void TheCropLoupeKeepsAFullSampleAtTheBottomRightEdge() =>
        Assert.Equal(new RectD(87, 67, 13, 13), ScreenshotLoupe.CropSampleRect(new PointD(100, 80), new SizeD(100, 80)));

    [Fact]
    public void TheCropLoupeSafelyShrinksOnlyForImagesSmallerThanItsSample() =>
        Assert.Equal(new RectD(0, 0, 8, 5), ScreenshotLoupe.CropSampleRect(new PointD(0, 0), new SizeD(8, 5)));

    [Fact]
    public void TheSampleSlidesInwardAtEveryEdge()
    {
        var image = new SizeD(100, 80);
        Assert.Equal(new RectD(0, 0, 13, 13), ScreenshotLoupe.CropSampleRect(new PointD(0, 0), image));
        Assert.Equal(new RectD(87, 0, 13, 13), ScreenshotLoupe.CropSampleRect(new PointD(100, 0), image));
        Assert.Equal(new RectD(0, 67, 13, 13), ScreenshotLoupe.CropSampleRect(new PointD(0, 80), image));
    }

    [Fact]
    public void TheCropLoupeCentersOnTheEdgeItMarksOnceTheDraftSitsOnPixels()
    {
        // An even side centres a whole-coordinate edge; the draft is snapped so its edges are whole.
        var snapped = ScreenshotSelection.PixelSnappedCropRect(new RectD(100.4, 60.4, 100, 100), new RectD(0, 0, 800, 600));
        var edge = SelectionHandle.Left.HandlePosition(snapped);
        var sample = ScreenshotLoupe.CropSampleRect(edge, new SizeD(800, 600), 14, centredOnPixel: false);
        Assert.Equal(edge.X, sample.MidX);
        Assert.Equal(edge.Y, sample.MidY());
    }

    [Fact]
    public void AnEvenSampleSideAloneDoesNotCentreAFractionalCropEdge()
    {
        var loose = new RectD(100.4, 60.4, 100, 100);
        var edge = SelectionHandle.Left.HandlePosition(loose);
        var sample = ScreenshotLoupe.CropSampleRect(edge, new SizeD(800, 600), 14, centredOnPixel: false);
        Assert.NotEqual(edge.X, sample.MidX);
    }

    [Fact]
    public void TheCropLoupeCentresOnTheEdgeNotOnAPixelCell()
    {
        var sample = ScreenshotLoupe.CropSampleRect(new PointD(100, 60), new SizeD(400, 300), 14, centredOnPixel: false);
        Assert.Equal(new RectD(93, 53, 14, 14), sample);
        Assert.Equal(100, sample.MidX);
        Assert.Equal(60, sample.MidY());
    }

    [Fact]
    public void AnOddSideGrowsToTheNextEvenOneWhenTheLoupeCentresOnAnEdge() =>
        Assert.Equal(14, ScreenshotLoupe.CropSampleRect(new PointD(100, 60), new SizeD(400, 300), 13, centredOnPixel: false).Width);

    [Fact]
    public void AnEvenSideShrinksToTheNextOddOneWhenCentringOnAPixel() =>
        Assert.Equal(13, ScreenshotLoupe.CropSampleRect(new PointD(100, 60), new SizeD(400, 300), 14).Width);

    [Fact]
    public void ADegenerateImageStillGetsAOnePixelSample() =>
        Assert.Equal(new RectD(0, 0, 1, 1), ScreenshotLoupe.CropSampleRect(new PointD(5, 5), new SizeD(0, 0)));

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(13.0 / 3.0)]
    public void TheLoupeMarksThePixelInTheMiddleOfItsFrameAcrossTheZoomRange(double zoom)
    {
        // An even sample side has no middle cell, which read as an off-centre reticle.
        var side = ScreenshotLoupe.SampleSide(zoom);
        var pointer = new PointD(80.4, 50.7);
        var sample = ScreenshotLoupe.CropSampleRect(pointer, new SizeD(160, 100), side);
        var frame = new RectD(0, 0, 70, 70);
        var marked = ScreenshotLoupe.TargetPixelRect(pointer, sample, frame);
        Assert.Equal(frame.MidX, marked.MidX, 9);
        Assert.Equal(frame.MidY(), marked.MidY(), 9);
    }

    [Fact]
    public void TheLoupeHighlightsOneWholeSourcePixelNotTheSubPixelPosition() =>
        Assert.Equal(new RectD(35, 35, 5, 5),
            ScreenshotLoupe.TargetPixelRect(new PointD(50.5, 40.2), new RectD(43, 33, 14, 14), new RectD(0, 0, 70, 70)));

    [Fact]
    public void APointerOnTheFarDisplayEdgeHighlightsTheLastPixelThePickerCanRead() =>
        Assert.Equal(new RectD(65, 65, 5, 5),
            ScreenshotLoupe.TargetPixelRect(new PointD(100, 80), new RectD(86, 66, 14, 14), new RectD(0, 0, 70, 70)));

    [Fact]
    public void AnEmptySampleLeavesTheHighlightHarmless() =>
        Assert.Equal(new RectD(0, 0, 70, 70),
            ScreenshotLoupe.TargetPixelRect(new PointD(0, 0), CaptureGeometry.Zero, new RectD(0, 0, 70, 70)));

    [Fact]
    public void TheMarkedPixelIsTheOneTheColorPickerCopiesAtEveryZoomAndPosition()
    {
        // Sweep the whole overlay at every zoom: the highlight and the copy come from two independent
        // expressions that must never drift apart by one pixel.
        var imageSize = new SizeD(160, 100);
        var viewSize = new SizeD(320, 200);
        var frame = new RectD(20, 30, 70, 70);
        foreach (var zoom in new[] { ScreenshotLoupe.MinZoom, 1, ScreenshotLoupe.MaxZoom })
            for (var stepX = 0; stepX <= 128; stepX++)
                for (var stepY = 0; stepY <= 80; stepY++)
                {
                    var pixelPoint = ScreenshotCoordinates.ImagePixelPoint(new PointD(stepX * 2.5, stepY * 2.5), viewSize, imageSize);
                    var source = ScreenshotLoupe.CropSampleRect(pixelPoint, imageSize, ScreenshotLoupe.SampleSide(zoom));
                    var highlight = ScreenshotLoupe.TargetPixelRect(pixelPoint, source, frame);
                    var cellWidth = frame.Width / source.Width;
                    var cellHeight = frame.Height / source.Height;
                    var markedX = source.X + Math.Round((highlight.X - frame.X) / cellWidth, MidpointRounding.AwayFromZero);
                    var markedY = source.Y + Math.Round((highlight.Y - frame.Y) / cellHeight, MidpointRounding.AwayFromZero);
                    // Copied verbatim from the colour confirmation.
                    var copiedX = Math.Min(Math.Max((int)Math.Floor(pixelPoint.X), 0), (int)imageSize.Width - 1);
                    var copiedY = Math.Min(Math.Max((int)Math.Floor(pixelPoint.Y), 0), (int)imageSize.Height - 1);
                    Assert.True(markedX == copiedX && markedY == copiedY,
                        $"at {pixelPoint} zoom {zoom} the loupe marks ({markedX}, {markedY}) but the picker copies ({copiedX}, {copiedY})");
                    var inner = highlight.InsetBy(0, 0);
                    Assert.True(inner.X >= frame.X - 0.001 && inner.Y >= frame.Y - 0.001
                        && inner.Right <= frame.Right + 0.001 && inner.Bottom <= frame.Bottom + 0.001,
                        $"at {pixelPoint} zoom {zoom} the highlight leaves the loupe");
                }
    }
}

public class LoupeZoomTests
{
    [Fact]
    public void TheLimitsAreDerivedFromTheSampleSides()
    {
        Assert.Equal(13, ScreenshotLoupe.BaseSampleSide);
        Assert.Equal(3, ScreenshotLoupe.MinSampleSide);
        Assert.Equal(0.5, ScreenshotLoupe.MinZoom);
        Assert.Equal(ScreenshotLoupe.BaseSampleSide / ScreenshotLoupe.MinSampleSide, ScreenshotLoupe.MaxZoom, 12);
        Assert.Equal(132, ScreenshotLoupe.FrameSide);
        Assert.Equal(new[] { 0.5, 1, 2, 4 }, ScreenshotLoupe.DefaultZooms);
    }

    [Fact]
    public void ScrollingUpZoomsTheLoupeIn() => Assert.Equal(1.15, ScreenshotLoupe.Zoom(1, 1), 12);

    [Fact]
    public void ScrollingDownZoomsItOutByTheSameFactor() => Assert.Equal(1 / 1.15, ScreenshotLoupe.Zoom(1, -1), 12);

    [Fact]
    public void ZoomStaysAboveItsMinimumAndBelowItsMaximum()
    {
        Assert.Equal(0.5, ScreenshotLoupe.Zoom(0.5, -1), 12);
        Assert.Equal(ScreenshotLoupe.MaxZoom, ScreenshotLoupe.Zoom(10, 1), 12);
    }

    [Fact]
    public void ZoomReachesTheThreePixelSampleWithoutADeadRangeAboveIt() =>
        Assert.Equal(ScreenshotLoupe.MaxZoom, ScreenshotLoupe.Zoom(4, 1), 12);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseWheelDeltaLeavesTheZoomAlone(double delta)
    {
        Assert.Equal(2, ScreenshotLoupe.Zoom(2, delta));
        Assert.Equal(2, ScreenshotLoupe.SteppedZoom(2, delta));
        Assert.Equal(2, ScreenshotLoupe.FastZoom(2, delta, false));
    }

    [Fact]
    public void ALeftoverOutOfRangeZoomIsClampedEvenWithoutAWheelDelta()
    {
        Assert.Equal(0.5, ScreenshotLoupe.Zoom(0.1, 0));
        Assert.Equal(ScreenshotLoupe.MaxZoom, ScreenshotLoupe.SteppedZoom(50, 0));
    }

    [Fact]
    public void TheLoupeStartsAtItsChosenDefaultZoom() =>
        Assert.Equal(2, ScreenshotLoupe.InitialZoom(rememberLast: false, defaultZoom: 2, lastZoom: 4));

    [Fact]
    public void TheLoupeCanRestoreItsLastZoom() =>
        Assert.Equal(4, ScreenshotLoupe.InitialZoom(rememberLast: true, defaultZoom: 2, lastZoom: 4));

    [Fact]
    public void AnInvalidSavedZoomFallsBackSafely()
    {
        Assert.Equal(1, ScreenshotLoupe.InitialZoom(false, double.NaN, 4));
        Assert.Equal(1, ScreenshotLoupe.InitialZoom(true, 2, double.PositiveInfinity));
    }

    [Fact]
    public void ASavedZoomOutsideTheRangeIsClamped()
    {
        Assert.Equal(0.5, ScreenshotLoupe.InitialZoom(true, 2, 0.01));
        Assert.Equal(ScreenshotLoupe.MaxZoom, ScreenshotLoupe.InitialZoom(false, 99, 4), 12);
    }

    [Fact]
    public void FractionalWheelNotchesDoNotDisappear() =>
        Assert.Equal(0.25, ScreenshotLoupe.WheelDelta(0, 0, 0.25));

    [Fact]
    public void OrdinaryLineBasedNotchesRemainAvailable() =>
        Assert.Equal(-1, ScreenshotLoupe.WheelDelta(0, -1, 0));

    [Fact]
    public void ThePlainScrollingDeltaIsTheLastResort()
    {
        Assert.Equal(3, ScreenshotLoupe.WheelDelta(3, 0, 0));
        Assert.Equal(0, ScreenshotLoupe.WheelDelta(double.NaN, 0, 0));
        Assert.Equal(7, ScreenshotLoupe.WheelDelta(1, 2, 7));
        Assert.Equal(2, ScreenshotLoupe.WheelDelta(1, 2, double.NaN));
    }

    [Fact]
    public void OptionTemporarilySwapsTheChosenWheelMode()
    {
        Assert.True(ScreenshotLoupe.UsesSteppedZoom(steppedByDefault: true, optionPressed: false));
        Assert.False(ScreenshotLoupe.UsesSteppedZoom(true, true));
        Assert.True(ScreenshotLoupe.UsesSteppedZoom(false, true));
        Assert.False(ScreenshotLoupe.UsesSteppedZoom(false, false));
    }

    [Fact]
    public void EverySteppedNotchChangesOneVisibleLevelAcrossTheWholeRange()
    {
        var zoom = ScreenshotLoupe.MinZoom;
        var sides = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            zoom = ScreenshotLoupe.SteppedZoom(zoom, 0.25);
            sides.Add(ScreenshotLoupe.SampleSide(zoom));
        }
        Assert.Equal(new double[] { 25, 23, 21, 19, 17, 15, 13, 11, 9, 7, 5, 3 }, sides);
    }

    [Fact]
    public void AllSteppedLevelsAreReversibleWithoutDeadNotches()
    {
        var zoom = ScreenshotLoupe.MinZoom;
        for (var i = 0; i < 12; i++) zoom = ScreenshotLoupe.SteppedZoom(zoom, 0.25);
        for (var i = 0; i < 12; i++) zoom = ScreenshotLoupe.SteppedZoom(zoom, -0.25);
        Assert.Equal(ScreenshotLoupe.MinZoom, zoom, 12);
    }

    [Fact]
    public void SteppingStopsAtEitherEnd()
    {
        Assert.Equal(ScreenshotLoupe.MinZoom, ScreenshotLoupe.SteppedZoom(ScreenshotLoupe.MinZoom, -1), 12);
        Assert.Equal(ScreenshotLoupe.MaxZoom, ScreenshotLoupe.SteppedZoom(ScreenshotLoupe.MaxZoom, 1), 12);
    }

    [Fact]
    public void FastZoomOnAPlainWheelCrossesTheRangeInAFewNotches()
    {
        var zoom = ScreenshotLoupe.MinZoom;
        var notches = 0;
        while (zoom < ScreenshotLoupe.MaxZoom && notches < 20)
        {
            zoom = ScreenshotLoupe.FastZoom(zoom, 1, isContinuous: false);
            notches++;
        }
        Assert.True(notches <= 6, $"{notches} notches");
    }

    [Fact]
    public void FastZoomKeepsItsPerPacketFactorForASmoothedWheel() =>
        Assert.Equal(1.15, ScreenshotLoupe.FastZoom(1, 1, isContinuous: true), 12);

    [Fact]
    public void FastZoomOnAPlainWheelZoomsBackOutAtTheSamePace() =>
        Assert.Equal(4 / (1.15 * 1.15 * 1.15), ScreenshotLoupe.FastZoom(4, -1, isContinuous: false), 12);

    [Fact]
    public void APlainNotchCarryingThreeLinesLandsWhereAOneLineNotchDoes() =>
        Assert.Equal(ScreenshotLoupe.FastZoom(1, 1, false), ScreenshotLoupe.FastZoom(1, 3, false), 12);

    [Fact]
    public void FastZoomPreservesThePacketByPacketBehaviour()
    {
        double zoom = 1;
        for (var i = 0; i < 6; i++) zoom = ScreenshotLoupe.Zoom(zoom, 20);
        Assert.True(zoom > 2);
    }

    [Theory]
    [InlineData(2.0, 7)]
    [InlineData(0.5, 27)]
    [InlineData(1.0, 13)]
    [InlineData(4.0, 3)]
    [InlineData(0.1, 27)]
    [InlineData(100.0, 3)]
    public void SampleSideFollowsTheZoom(double zoom, double side) =>
        Assert.Equal(side, ScreenshotLoupe.SampleSide(zoom));

    [Fact]
    public void MaximumZoomKeepsAThreePixelSample() =>
        Assert.Equal(3, ScreenshotLoupe.SampleSide(ScreenshotLoupe.MaxZoom));

    [Fact]
    public void EverySampleSideIsOddSoAnExactCentrePixelExists()
    {
        foreach (var zoom in new[] { 0.5, 1, 2, 4, ScreenshotLoupe.MaxZoom })
            Assert.Equal(1, ScreenshotLoupe.SampleSide(zoom) % 2);
        for (var zoom = 0.5; zoom <= ScreenshotLoupe.MaxZoom; zoom += 0.01)
            Assert.Equal(1, ScreenshotLoupe.SampleSide(zoom) % 2);
    }

    [Theory]
    [InlineData(132, 13, true)]
    [InlineData(132, 27, false)]
    [InlineData(132, 0, false)]
    [InlineData(132, 22, true)]
    [InlineData(132, 23, false)]
    [InlineData(60, 10, true)]
    public void ThePixelGridAppearsOnlyWhenCellsAreReadable(double frame, double sample, bool visible) =>
        Assert.Equal(visible, ScreenshotLoupe.GridVisible(frame, sample));

    [Fact]
    public void TheGridIsHiddenAtTheWidestZoomAndShownFromTheDefaultOnward()
    {
        Assert.False(new LoupeZoomState(false, 0.5, 1).GridVisible);
        Assert.True(new LoupeZoomState(false, 1, 1).GridVisible);
        Assert.True(new LoupeZoomState(false, 4, 1).GridVisible);
    }
}

public class LoupeNudgeTests
{
    [Fact]
    public void AnArrowNudgeMovesExactlyOneDevicePixelOnAHighDpiDisplay() =>
        Assert.Equal(new PointD(0.5, 0), ScreenshotLoupe.Nudge(1, 0, fast: false, scale: 2));

    [Fact]
    public void AShiftedNudgeCoversTenDevicePixels() =>
        Assert.Equal(new PointD(0, -5), ScreenshotLoupe.Nudge(0, -1, fast: true, scale: 2));

    [Fact]
    public void AMissingDisplayScaleFallsBackToWholePoints()
    {
        Assert.Equal(new PointD(-1, 1), ScreenshotLoupe.Nudge(-1, 1, fast: false, scale: 0));
        Assert.Equal(new PointD(10, 0), ScreenshotLoupe.Nudge(1, 0, fast: true, scale: 0.5));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 0.8)]
    [InlineData(1.5, 2.0 / 3.0)]
    [InlineData(2.0, 0.5)]
    [InlineData(3.0, 1.0 / 3.0)]
    public void OnePressIsOneDevicePixelAtEveryWindowsScale(double scale, double points) =>
        Assert.Equal(points, ScreenshotLoupe.Nudge(1, 0, false, scale).X, 12);
}

public class LoupeStateTests
{
    [Fact]
    public void ASessionStartsFromTheSettingsAndRemembersEveryChange()
    {
        var state = new LoupeZoomState(rememberLast: true, defaultZoom: 1, lastZoom: 2);
        Assert.Equal(2, state.Zoom);
        Assert.Equal(7, state.SampleSide);
        var stepped = state.Adjust(1, stepped: true, isContinuous: false);
        Assert.Equal(stepped, state.Zoom);
        Assert.Equal(5, state.SampleSide);
        var fast = state.Adjust(-1, stepped: false, isContinuous: true);
        Assert.Equal(stepped / 1.15, fast, 12);
    }

    [Fact]
    public void ASteppedSessionWalksTheGridLevelsOneAtATime()
    {
        var state = new LoupeZoomState(false, 1, 1);
        var sides = new List<double> { state.SampleSide };
        for (var i = 0; i < 6; i++) { state.Adjust(1, true, false); sides.Add(state.SampleSide); }
        Assert.Equal(new double[] { 13, 11, 9, 7, 5, 3, 3 }, sides);
    }
}

public class CoordinateConversionTests
{
    [Fact]
    public void BottomUpAndTopDownRectanglesConvertThroughTheContainerHeight()
    {
        // Upstream's window-server to Cocoa numbers; kept for bottom-up bitmaps and APIs.
        var flipped = ScreenshotCoordinates.FlipVertically(new RectD(10, 30, 200, 100), 900);
        Assert.Equal(new RectD(10, 770, 200, 100), flipped);
        Assert.Equal(new RectD(10, 30, 200, 100), ScreenshotCoordinates.FlipVertically(flipped, 900));
    }

    [Fact]
    public void AGlobalRectangleRoundTripsThroughADisplaysOverlay()
    {
        var screen = new RectD(1920, -200, 1600, 900);
        var global = new RectD(2000, -100, 200, 100);
        var view = ScreenshotCoordinates.GlobalToView(global, screen);
        Assert.Equal(new RectD(80, 100, 200, 100), view);
        Assert.Equal(global, ScreenshotCoordinates.ViewToGlobal(view, screen));
        Assert.Equal(new PointD(80, 100), ScreenshotCoordinates.GlobalToView(new PointD(2000, -100), screen));
        Assert.Equal(new PointD(2000, -100), ScreenshotCoordinates.ViewToGlobal(new PointD(80, 100), screen));
    }

    [Fact]
    public void ViewPointsScaleToImagePixels() =>
        Assert.Equal(new RectD(20, 40, 60, 80),
            ScreenshotCoordinates.ImagePixelRect(new RectD(10, 20, 30, 40), new SizeD(100, 100), new SizeD(200, 200)));

    [Fact]
    public void PixelRectsClampToTheImage() =>
        Assert.Equal(new RectD(0, 0, 200, 200),
            ScreenshotCoordinates.ImagePixelRect(new RectD(-20, -20, 500, 500), new SizeD(100, 100), new SizeD(200, 200)));

    [Fact]
    public void AnEmptyViewMapsToNothing() =>
        Assert.Equal(CaptureGeometry.Zero,
            ScreenshotCoordinates.ImagePixelRect(new RectD(1, 1, 5, 5), new SizeD(0, 100), new SizeD(200, 200)));

    [Fact]
    public void ThePixelRectRoundsOutwardToWholePixelsAtAFractionalScale()
    {
        // 125 percent: a 100.4 x 50.2 point selection at (10.2, 20.6) needs whole device pixels around it.
        var rect = ScreenshotCoordinates.ImagePixelRect(new RectD(10.2, 20.6, 100.4, 50.2), new SizeD(800, 600), new SizeD(1000, 750));
        Assert.Equal(new RectD(12, 25, 127, 64), rect);
        Assert.True(rect.X <= 10.2 * 1.25 && rect.Right >= (10.2 + 100.4) * 1.25);
    }

    [Fact]
    public void ThePickerMapsItsPointerToTheMatchingSourcePixel()
    {
        Assert.Equal(new PointD(100, 50),
            ScreenshotCoordinates.ImagePixelPoint(new PointD(50, 25), new SizeD(100, 50), new SizeD(200, 100)));
        // The pointer is not rounded: the loupe follows it without jumping by whole selections.
        Assert.Equal(new PointD(0.5, 0.25),
            ScreenshotCoordinates.ImagePixelPoint(new PointD(0.25, 0.125), new SizeD(100, 50), new SizeD(200, 100)));
    }

    [Fact]
    public void ThePickerClampsSourcePointsAtDisplayEdges()
    {
        Assert.Equal(new PointD(0, 100),
            ScreenshotCoordinates.ImagePixelPoint(new PointD(-10, 90), new SizeD(100, 50), new SizeD(200, 100)));
        Assert.Equal(new PointD(0, 0),
            ScreenshotCoordinates.ImagePixelPoint(new PointD(5, 5), new SizeD(0, 0), new SizeD(200, 100)));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void LogicalAndPhysicalConvertBothWaysAtEveryDisplayScale(double scale)
    {
        var logical = new RectD(10, 20, 300, 200);
        var physical = ScreenshotCoordinates.LogicalToPhysical(logical, scale);
        Assert.Equal(new RectD(10 * scale, 20 * scale, 300 * scale, 200 * scale), physical);
        var back = ScreenshotCoordinates.PhysicalToLogical(physical, scale);
        Assert.Equal(logical.X, back.X, 9);
        Assert.Equal(logical.Width, back.Width, 9);
        var point = ScreenshotCoordinates.PhysicalToLogical(ScreenshotCoordinates.LogicalToPhysical(new PointD(7, 9), scale), scale);
        Assert.Equal(7, point.X, 9);
        Assert.Equal(9, point.Y, 9);
    }

    [Fact]
    public void ANonPositiveScaleLeavesPhysicalValuesAlone() =>
        Assert.Equal(new RectD(1, 2, 3, 4), ScreenshotCoordinates.PhysicalToLogical(new RectD(1, 2, 3, 4), 0));

    [Fact]
    public void ASelectionBecomesTheDevicePixelRectOfItsDisplay()
    {
        var display = new CaptureDisplay("d", new RectD(0, 0, 1536, 864), 1.25);
        Assert.Equal(new SizeD(1920, 1080), display.PixelSize);
        Assert.Equal(new RectD(125, 250, 625, 375), ScreenshotCoordinates.SelectionPixelRect(new RectD(100, 200, 500, 300), display));
        Assert.Equal(new RectD(1800, 1000, 120, 80), ScreenshotCoordinates.SelectionPixelRect(new RectD(1440, 800, 500, 500), display));
    }

    [Fact]
    public void ThePointerBelongsToTheDisplayWhoseFrameHoldsIt()
    {
        var left = new CaptureDisplay("left", new RectD(-1920, 0, 1920, 1080), 1);
        var main = new CaptureDisplay("main", new RectD(0, 0, 1536, 864), 1.25);
        Assert.Equal("left", ScreenshotCoordinates.DisplayContaining(new PointD(-1, 10), [left, main])!.Value.Id);
        Assert.Equal("main", ScreenshotCoordinates.DisplayContaining(new PointD(0, 10), [left, main])!.Value.Id);
        Assert.Null(ScreenshotCoordinates.DisplayContaining(new PointD(3000, 10), [left, main]));
    }

    [Fact]
    public void AWindowsDevicePixelSizeRoundsToWholePixels()
    {
        Assert.Equal((1000, 750), ScreenshotCoordinates.PixelSizeFor(new RectD(0, 0, 800, 600), 1.25));
        Assert.Equal((151, 76), ScreenshotCoordinates.PixelSizeFor(new RectD(0, 0, 100.4, 50.4), 1.5));
    }
}
