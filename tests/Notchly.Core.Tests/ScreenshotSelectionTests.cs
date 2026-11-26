// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the selection, window-picking, coordinate and capture-window-policy contracts in upstream
// Tests/ScreenshotFeatureTests.swift (lines ~125-405, 677-745, 1606-1623, 1742, 4284-4330),
// Tests/ScreenshotAttachedCaptureTests.swift (the geometry), Tests/ScreenshotSelectionRefreshTests.swift
// and Tests/ScreenshotCountdownTests.swift (the pure parts). Upstream's y-up numbers are mirrored to the
// top-left origin where a test depends on the axis; those cases say so.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class SelectionRectTests
{
    [Fact]
    public void ADragInAnyDirectionNormalizesToAPositiveRect() =>
        Assert.Equal(new RectD(40, 80, 60, 120), ScreenshotSelection.SelectionRect(new PointD(100, 80), new PointD(40, 200)));

    [Theory]
    [InlineData(10, 10, 40, 90, 10, 10, 80, 80)]
    [InlineData(100, 100, 60, 90, 60, 60, 40, 40)]
    [InlineData(50, 50, 10, 90, 10, 50, 40, 40)]
    [InlineData(50, 50, 60, 20, 50, 20, 30, 30)]
    public void ShiftConstrainsTheSelectionToTheLargestSquare(double ox, double oy, double cx, double cy,
                                                              double x, double y, double w, double h)
    {
        var rect = ScreenshotSelection.SelectionRect(new PointD(ox, oy), new PointD(cx, cy), square: true);
        Assert.Equal(new RectD(x, y, w, h), rect);
        Assert.Equal(rect.Width, rect.Height);
    }

    [Fact]
    public void OptionGrowsTheSelectionFromTheCenter() =>
        Assert.Equal(new RectD(30, 40, 40, 20),
            ScreenshotSelection.SelectionRect(new PointD(50, 50), new PointD(70, 60), fromCenter: true));

    [Fact]
    public void SquareAndCenterCombine() =>
        Assert.Equal(new RectD(20, 20, 60, 60),
            ScreenshotSelection.SelectionRect(new PointD(50, 50), new PointD(60, 80), square: true, fromCenter: true));

    [Fact]
    public void ADragThatNeverMovedIsAnEmptyRect() =>
        Assert.Equal(new RectD(5, 5, 0, 0), ScreenshotSelection.SelectionRect(new PointD(5, 5), new PointD(5, 5)));

    [Fact]
    public void ATinyDragIsAClickAndARealDragIsNot()
    {
        Assert.True(ScreenshotSelection.IsClick(new PointD(5, 5), new PointD(7, 8)));
        Assert.False(ScreenshotSelection.IsClick(new PointD(0, 0), new PointD(12, 0)));
    }

    [Theory]
    [InlineData(3.9, 0, true)]
    [InlineData(4, 0, false)]
    [InlineData(0, -4, false)]
    [InlineData(-3.5, 3.5, true)]
    public void TheClickThresholdIsFourPoints(double dx, double dy, bool click) =>
        Assert.Equal(click, ScreenshotSelection.IsClick(new PointD(10, 10), new PointD(10 + dx, 10 + dy)));

    [Fact]
    public void ClampingKeepsTheSelectionInsideTheDisplay()
    {
        var bounds = new RectD(0, 0, 800, 600);
        Assert.Equal(new RectD(700, 500, 100, 100), new RectD(700, 500, 300, 300).Clamp(bounds));
        Assert.Equal(new RectD(0, 0, 50, 50), new RectD(-50, -50, 100, 100).Clamp(bounds));
        Assert.Equal(CaptureGeometry.Zero, new RectD(900, 900, 50, 50).Clamp(bounds));
    }
}

public class CropSelectionTests
{
    private static readonly RectD Bounds = new(0, 0, 800, 600);
    private static readonly RectD Draft = new(120, 90, 400, 300);

    [Fact]
    public void DraggingInsideTheInitialFullImageCropStartsANewSelection() =>
        Assert.True(ScreenshotSelection.StartsNewCropSelection(new PointD(300, 250), Bounds, Bounds));

    [Fact]
    public void DraggingInsideAnAdjustedCropKeepsMovingIt() =>
        Assert.False(ScreenshotSelection.StartsNewCropSelection(new PointD(300, 250), Draft, Bounds));

    [Fact]
    public void DraggingElsewhereInTheImageReplacesAnAdjustedCrop() =>
        Assert.True(ScreenshotSelection.StartsNewCropSelection(new PointD(700, 500), Draft, Bounds));

    [Fact]
    public void ADragOutsideTheImageCannotStartACrop() =>
        Assert.False(ScreenshotSelection.StartsNewCropSelection(new PointD(900, 700), Draft, Bounds));

    [Fact]
    public void ACropDraftRoundsEachEdgeToTheNearestPixelBoundary()
    {
        var snapped = ScreenshotSelection.PixelSnappedCropRect(new RectD(100.4, 60.4, 100, 100), Bounds);
        Assert.Equal(new RectD(100, 60, 100, 100), snapped);
        // Not CGRect.integral: the right edge does not grow outward.
        Assert.Equal(200, snapped.Right);
    }

    [Fact]
    public void SnappingADraftThatAlreadySitsOnPixelsChangesNothing()
    {
        var snapped = new RectD(100, 60, 100, 100);
        Assert.Equal(snapped, ScreenshotSelection.PixelSnappedCropRect(snapped, Bounds));
    }

    [Fact]
    public void ACropEdgePastTheHalfwayMarkRoundsToTheNextBoundary() =>
        Assert.Equal(new RectD(100, 60, 101, 101),
            ScreenshotSelection.PixelSnappedCropRect(new RectD(100.4, 60.4, 100.2, 100.2), Bounds));

    [Fact]
    public void HalfPixelsRoundAwayFromZero() =>
        Assert.Equal(new RectD(11, 3, 4, 3),
            ScreenshotSelection.PixelSnappedCropRect(new RectD(10.5, 2.5, 4.0, 3.0), Bounds));

    [Fact]
    public void MovingACropDraftNeverChangesItsSize()
    {
        var snapped = new RectD(100, 60, 100, 100);
        var moved = ScreenshotSelection.PixelSnappedCropRect(
            ScreenshotSelection.MovedRect(snapped, new PointD(12.6, -4.3), Bounds), Bounds);
        Assert.Equal(snapped.Width, moved.Width);
        Assert.Equal(snapped.Height, moved.Height);
    }

    [Fact]
    public void ASnappedCropDraftStaysInsideTheImage() =>
        Assert.Equal(new RectD(0, 0, 60, 70),
            ScreenshotSelection.PixelSnappedCropRect(new RectD(-40, -30, 100, 100), Bounds));

    [Fact]
    public void MovingStopsAtTheImageEdgesInsteadOfShrinking()
    {
        var rect = new RectD(700, 500, 80, 60);
        Assert.Equal(new RectD(720, 540, 80, 60), ScreenshotSelection.MovedRect(rect, new PointD(500, 500), Bounds));
        Assert.Equal(new RectD(0, 0, 80, 60), ScreenshotSelection.MovedRect(rect, new PointD(-2000, -2000), Bounds));
        Assert.Equal(new RectD(710, 505, 80, 60), ScreenshotSelection.MovedRect(rect, new PointD(10, 5), Bounds));
    }

    [Fact]
    public void ARectLargerThanTheBoundsIsIntersectedInsteadOfMoved() =>
        Assert.Equal(new RectD(0, 0, 800, 600), ScreenshotSelection.MovedRect(new RectD(-10, -10, 900, 700), new PointD(30, 30), Bounds));
}

public class SelectionHandleTests
{
    private static readonly RectD Rect = new(100, 50, 200, 100);

    [Theory]
    [InlineData(SelectionHandle.TopLeft, 100, 50)]
    [InlineData(SelectionHandle.Top, 200, 50)]
    [InlineData(SelectionHandle.TopRight, 300, 50)]
    [InlineData(SelectionHandle.Right, 300, 100)]
    [InlineData(SelectionHandle.BottomRight, 300, 150)]
    [InlineData(SelectionHandle.Bottom, 200, 150)]
    [InlineData(SelectionHandle.BottomLeft, 100, 150)]
    [InlineData(SelectionHandle.Left, 100, 100)]
    public void EachHandleSitsOnItsEdgeOrCorner(SelectionHandle handle, double x, double y) =>
        Assert.Equal(new PointD(x, y), handle.HandlePosition(Rect));

    [Fact]
    public void ThereAreEightHandlesInAFixedOrder() =>
        Assert.Equal(8, ScreenshotSelection.Handles.Count);

    [Theory]
    [InlineData(101, 52, SelectionHandle.TopLeft)]
    [InlineData(199, 48, SelectionHandle.Top)]
    [InlineData(302, 149, SelectionHandle.BottomRight)]
    [InlineData(98, 101, SelectionHandle.Left)]
    public void APointWithinToleranceHitsTheNearestHandle(double x, double y, SelectionHandle expected) =>
        Assert.Equal(expected, ScreenshotSelection.HandleAt(new PointD(x, y), Rect, 4));

    [Fact]
    public void APointAwayFromEveryHandleHitsNone() =>
        Assert.Null(ScreenshotSelection.HandleAt(new PointD(200, 100), Rect, 4));

    [Fact]
    public void WhenHandlesOverlapTheFirstOneWins() =>
        Assert.Equal(SelectionHandle.TopLeft, ScreenshotSelection.HandleAt(new PointD(1, 1), new RectD(0, 0, 2, 2), 3));

    [Theory]
    [InlineData(SelectionHandle.Right, 350, 999, 100, 50, 250, 100)]
    [InlineData(SelectionHandle.Bottom, 0, 200, 100, 50, 200, 150)]
    [InlineData(SelectionHandle.TopLeft, 120, 70, 120, 70, 180, 80)]
    [InlineData(SelectionHandle.Left, 50, 0, 50, 50, 250, 100)]
    [InlineData(SelectionHandle.Top, 0, 20, 100, 20, 200, 130)]
    [InlineData(SelectionHandle.BottomLeft, 150, 120, 150, 50, 150, 70)]
    [InlineData(SelectionHandle.TopRight, 250, 30, 100, 30, 150, 120)]
    [InlineData(SelectionHandle.BottomRight, 260, 90, 100, 50, 160, 40)]
    public void ResizingMovesOnlyTheDraggedEdges(SelectionHandle handle, double px, double py,
                                                 double x, double y, double w, double h) =>
        Assert.Equal(new RectD(x, y, w, h), ScreenshotSelection.ResizedRect(Rect, handle, new PointD(px, py)));

    [Fact]
    public void DraggingAHandleAcrossTheOppositeEdgeSwapsEdgesInsteadOfGoingNegative()
    {
        var crossed = ScreenshotSelection.ResizedRect(Rect, SelectionHandle.Right, new PointD(40, 0));
        Assert.Equal(new RectD(40, 50, 60, 100), crossed);
        var flipped = ScreenshotSelection.ResizedRect(Rect, SelectionHandle.Bottom, new PointD(0, 20));
        Assert.Equal(new RectD(100, 20, 200, 30), flipped);
    }
}

public class WindowPickingTests
{
    private static readonly IReadOnlyList<ScreenshotSelection.PickableWindow> Windows =
    [
        new(1, new RectD(0, 0, 50, 50)),
        new(2, new RectD(0, 0, 400, 400)),
    ];

    [Fact]
    public void TheFrontmostWindowWinsAClick() =>
        Assert.Equal(1u, ScreenshotSelection.WindowAt(new PointD(10, 10), Windows)?.WindowId);

    [Fact]
    public void ClicksOutsideEveryWindowPickNothing()
    {
        Assert.Equal(2u, ScreenshotSelection.WindowAt(new PointD(300, 300), Windows)?.WindowId);
        Assert.Null(ScreenshotSelection.WindowAt(new PointD(900, 900), Windows));
    }

    [Fact]
    public void AWindowsFarEdgesBelongToItsNeighbour()
    {
        // Half-open: x == 50 is outside window 1 and inside window 2.
        Assert.Equal(2u, ScreenshotSelection.WindowAt(new PointD(50, 10), Windows)?.WindowId);
        Assert.Equal(1u, ScreenshotSelection.WindowAt(new PointD(0, 0), Windows)?.WindowId);
        Assert.Null(ScreenshotSelection.WindowAt(new PointD(400, 400), Windows));
    }

    [Fact]
    public void AnEmptyListPicksNothing() =>
        Assert.Null(ScreenshotSelection.WindowAt(new PointD(1, 1), []));

    private static ScreenshotSelection.WindowListEntry Entry(uint id, int pid, RectD frame, int layer = 0, double alpha = 1, bool untitled = false) =>
        new(id, pid, frame, layer, alpha, untitled);

    [Fact]
    public void OnlyOrdinaryWindowsOfTheRightSizeAndOpacityAreListed()
    {
        var list = new[]
        {
            Entry(1, 100, new RectD(0, 0, 500, 400)),
            Entry(2, 100, new RectD(0, 0, 39, 400)),               // too narrow
            Entry(3, 100, new RectD(0, 0, 400, 39.9)),             // too short
            Entry(4, 100, new RectD(0, 0, 40, 40)),                // exactly the minimum
            Entry(5, 100, new RectD(0, 0, 500, 400), layer: 25),   // a menu or overlay layer
            Entry(6, 100, new RectD(0, 0, 500, 400), alpha: 0.01), // invisible
            Entry(7, 100, new RectD(0, 0, 500, 400), alpha: 0.5),
        };
        var picked = ScreenshotSelection.PickableWindows(list, ownPid: 1, hideOwnWindows: true, []).Select(e => e.Id);
        Assert.Equal(new uint[] { 1, 4, 7 }, picked);
    }

    [Fact]
    public void OwnWindowsFollowTheVisibilityPreferenceAndProtection()
    {
        var list = new[]
        {
            Entry(10, 1, new RectD(0, 0, 500, 400)),
            Entry(11, 1, new RectD(0, 0, 500, 400)),
            Entry(12, 2, new RectD(0, 0, 500, 400)),
        };
        Assert.Equal(new uint[] { 12 }, ScreenshotSelection.PickableWindows(list, 1, hideOwnWindows: true, [11]).Select(e => e.Id));
        Assert.Equal(new uint[] { 10, 12 }, ScreenshotSelection.PickableWindows(list, 1, hideOwnWindows: false, [11]).Select(e => e.Id));
    }

    [Fact]
    public void ABorderDrawnAroundAWindowIsDroppedSoTheClickReachesTheWindow()
    {
        var framed = Entry(81, 500, new RectD(100, 100, 800, 600));
        var border = Entry(80, 700, framed.Frame.InsetBy(-12, -12), untitled: true);
        Assert.Equal(new uint[] { 81 }, ScreenshotSelection.PickableWindows([border, framed], 1, true, []).Select(e => e.Id));
        Assert.Equal(new uint[] { 81 }, ScreenshotSelection.PickableWindows([framed, border], 1, true, []).Select(e => e.Id));
    }

    [Fact]
    public void WindowsAreMovedIntoTheirDisplaysViewAndDroppedWhenTheyMissIt()
    {
        var display = new RectD(1920, 0, 1280, 720);
        var list = new[]
        {
            Entry(1, 5, new RectD(2000, 100, 400, 300)),     // wholly on the display
            Entry(2, 5, new RectD(100, 100, 400, 300)),      // on the display to the left
            Entry(3, 5, new RectD(1700, 600, 400, 300)),     // straddles the display's left edge
            Entry(4, 5, new RectD(1520, 0, 400, 300)),       // touches the edge only
        };
        var view = ScreenshotSelection.PickableWindowsOnDisplay(list, display);
        Assert.Equal(new uint[] { 1, 3 }, view.Select(w => w.WindowId));
        Assert.Equal(new RectD(80, 100, 400, 300), view[0].Frame);
        Assert.Equal(new RectD(-220, 600, 400, 300), view[1].Frame);
    }
}

public class SelectionDragSessionTests
{
    private static readonly RectD Bounds = new(0, 0, 1000, 700);
    private static readonly IReadOnlyList<ScreenshotSelection.PickableWindow> Windows = [new(7, new RectD(100, 100, 300, 200))];

    [Fact]
    public void ADragDrawsTheRectBetweenPressAndPointer()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(300, 250), square: false, fromCenter: false, pickingColor: false, Bounds);
        Assert.Equal(new RectD(100, 100, 200, 150), session.Selection);
        Assert.True(session.IsDragging);
    }

    [Fact]
    public void TheSelectionNeverLeavesTheDisplay()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(900, 600));
        session.Drag(new PointD(1500, 900), false, false, false, Bounds);
        Assert.Equal(new RectD(900, 600, 100, 100), session.Selection);
    }

    [Fact]
    public void ReleasingAfterARealDragYieldsTheRegion()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(300, 250), false, false, false, Bounds);
        var release = session.Release(new PointD(300, 250), false, true, Windows);
        Assert.Equal(SelectionReleaseKind.Region, release.Kind);
        Assert.Equal(new RectD(100, 100, 200, 150), release.Region);
        Assert.False(session.IsDragging);
    }

    [Fact]
    public void SpaceMovesTheSelectionUnderThePointerWithoutResizingIt()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(300, 250), false, false, false, Bounds);
        session.SpaceIsDown = true;
        session.Drag(new PointD(340, 230), false, false, false, Bounds);
        Assert.Equal(new RectD(140, 80, 200, 150), session.Selection);
        // The anchor moved with it, so letting go of Space resumes resizing from the new corner.
        session.SpaceIsDown = false;
        session.Drag(new PointD(400, 300), false, false, false, Bounds);
        Assert.Equal(new RectD(140, 80, 260, 220), session.Selection);
    }

    [Fact]
    public void SpaceBeforeAnythingIsDrawnDoesNothingYet()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.SpaceIsDown = true;
        session.Drag(new PointD(150, 150), false, false, false, Bounds);
        Assert.Equal(new RectD(100, 100, 50, 50), session.Selection);
    }

    [Fact]
    public void ShiftAndAltShapeTheDrag()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(500, 300));
        session.Drag(new PointD(540, 380), square: true, fromCenter: true, pickingColor: false, Bounds);
        Assert.Equal(new RectD(420, 220, 160, 160), session.Selection);
    }

    [Fact]
    public void AClickOnAWindowPicksIt()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(200, 200));
        var release = session.Release(new PointD(201, 201), false, true, Windows);
        Assert.Equal(SelectionReleaseKind.Window, release.Kind);
        Assert.Equal(7u, release.Window!.Value.WindowId);
        Assert.Equal(CaptureGeometry.Zero, session.Selection);
    }

    [Fact]
    public void AClickIsIgnoredWhenWindowClicksAreOffOrNothingIsThere()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(200, 200));
        Assert.Equal(SelectionReleaseKind.ClickIgnored, session.Release(new PointD(200, 200), false, false, Windows).Kind);
        session.Press(new PointD(800, 600));
        Assert.Equal(SelectionReleaseKind.ClickIgnored, session.Release(new PointD(800, 600), false, true, Windows).Kind);
    }

    [Fact]
    public void ADragSmallerThanTwoPointsIsDropped()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(105, 101), false, false, false, Bounds);
        var release = session.Release(new PointD(105, 101), false, true, Windows);
        Assert.Equal(SelectionReleaseKind.TooSmall, release.Kind);
        Assert.Equal(CaptureGeometry.Zero, session.Selection);
    }

    [Fact]
    public void PickingAColourSamplesThePointerInsteadOfDrawingARegion()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(300, 250), false, false, pickingColor: true, Bounds);
        Assert.Equal(CaptureGeometry.Zero, session.Selection);
        var release = session.Release(new PointD(300, 250), pickingColor: true, true, Windows);
        Assert.Equal(SelectionReleaseKind.Color, release.Kind);
        Assert.Equal(new PointD(300, 250), release.Point);
    }

    [Fact]
    public void ALateReleaseWithoutAPressDoesNothing()
    {
        var session = new SelectionDragSession();
        Assert.Equal(SelectionReleaseKind.None, session.Release(new PointD(1, 1), false, true, Windows).Kind);
        session.Drag(new PointD(50, 50), false, false, false, Bounds);
        Assert.Equal(CaptureGeometry.Zero, session.Selection);
    }

    [Fact]
    public void ReleaseClearsSpaceSoTheNextDragResizes()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.SpaceIsDown = true;
        session.Release(new PointD(100, 100), false, false, []);
        Assert.False(session.SpaceIsDown);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var session = new SelectionDragSession();
        session.Press(new PointD(100, 100));
        session.Drag(new PointD(300, 250), false, false, false, Bounds);
        session.Reset();
        Assert.False(session.IsDragging);
        Assert.Equal(CaptureGeometry.Zero, session.Selection);
    }
}

public class LastRegionTests
{
    [Fact]
    public void NothingIsRepeatableBeforeTheFirstCapture()
    {
        var store = new LastRegionStore();
        Assert.Null(store.RepeatTarget(["display-1"], false));
        Assert.Null(store.GhostFor("display-1", showLastRegion: true));
    }

    [Fact]
    public void TheRegionRepeatsOnlyWhileItsDisplayIsStillThere()
    {
        var store = new LastRegionStore();
        store.Remember("display-2", new RectD(10, 20, 300, 200));
        Assert.Equal(new LastCaptureRegion("display-2", new RectD(10, 20, 300, 200)), store.RepeatTarget(["display-1", "display-2"], false));
        Assert.Null(store.RepeatTarget(["display-1"], false));
    }

    [Fact]
    public void ThePickerHasNoRegionToRepeat()
    {
        var store = new LastRegionStore();
        store.Remember("d", new RectD(0, 0, 10, 10));
        Assert.Null(store.RepeatTarget(["d"], isPickingColor: true));
    }

    [Fact]
    public void TheGhostShowsOnlyOnTheOwningDisplayAndOnlyWhenEnabled()
    {
        var store = new LastRegionStore();
        store.Remember("d1", new RectD(1, 2, 3, 4));
        Assert.Equal(new RectD(1, 2, 3, 4), store.GhostFor("d1", true));
        Assert.Null(store.GhostFor("d2", true));
        Assert.Null(store.GhostFor("d1", false));
    }

    [Fact]
    public void ANewRegionReplacesTheOldOne()
    {
        var store = new LastRegionStore();
        store.Remember("d1", new RectD(1, 2, 3, 4));
        store.Remember("d2", new RectD(5, 6, 7, 8));
        Assert.Equal("d2", store.Last!.DisplayId);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void TheGuideOffersRepeatOnlyWhenPressingTheKeyWouldCapture(bool picking, bool displayThere, bool offered) =>
        Assert.Equal(offered, ScreenshotSupport.OffersRepeatLastRegion(picking, displayThere));
}

public class SelectionNudgeTests
{
    private static readonly NudgeDisplay Left = new(new RectD(-1920, 0, 1920, 1080), 1);
    private static readonly NudgeDisplay Main = new(new RectD(0, 0, 1536, 864), 1.25);
    private static readonly NudgeDisplay Retina = new(new RectD(0, 0, 1440, 900), 2);

    [Fact]
    public void AnArrowMovesExactlyOneDevicePixelOnAScaledDisplay()
    {
        var moved = SelectionNudge.Apply(new PointD(100, 100), NudgeKey.Right, false, [Retina])!.Value;
        Assert.Equal(new PointD(100.5, 100), moved);
        var main = SelectionNudge.Apply(new PointD(100, 100), NudgeKey.Down, false, [Main])!.Value;
        Assert.Equal(100 + 1 / 1.25, main.Y, 10);
    }

    [Fact]
    public void ShiftCoversTenDevicePixels()
    {
        var moved = SelectionNudge.Apply(new PointD(100, 100), NudgeKey.Up, true, [Retina])!.Value;
        Assert.Equal(new PointD(100, 95), moved);
    }

    [Fact]
    public void UpIsTowardTheTopOfTheScreen()
    {
        Assert.Equal(new PointD(0, -1), SelectionNudge.Direction(NudgeKey.Up));
        Assert.Equal(new PointD(0, 1), SelectionNudge.Direction(NudgeKey.Down));
        Assert.Equal(new PointD(-1, 0), SelectionNudge.Direction(NudgeKey.Left));
        Assert.Equal(new PointD(1, 0), SelectionNudge.Direction(NudgeKey.Right));
    }

    [Fact]
    public void ThePointerMayCrossOntoANeighbouringDisplay()
    {
        var moved = SelectionNudge.Apply(new PointD(0.5, 100), NudgeKey.Left, true, [Left, Retina])!.Value;
        // The pointer is on the 2x display; ten device pixels is five points, landing on the left display.
        Assert.Equal(new PointD(-4.5, 100), moved);
    }

    [Fact]
    public void WhenNoDisplayHoldsTheTargetThePointerStopsAtTheEdge()
    {
        var atRight = SelectionNudge.Apply(new PointD(1439.75, 100), NudgeKey.Right, true, [Retina])!.Value;
        Assert.Equal(1440 - 0.5, atRight.X);
        var atTop = SelectionNudge.Apply(new PointD(100, 2), NudgeKey.Up, true, [Retina])!.Value;
        Assert.Equal(0, atTop.Y);
        var atBottom = SelectionNudge.Apply(new PointD(100, 899.9), NudgeKey.Down, true, [Retina])!.Value;
        Assert.Equal(900 - 0.5, atBottom.Y);
        var atLeft = SelectionNudge.Apply(new PointD(1, 100), NudgeKey.Left, true, [Retina])!.Value;
        Assert.Equal(0, atLeft.X);
    }

    [Fact]
    public void APointerOnNoDisplayCannotBeNudged() =>
        Assert.Null(SelectionNudge.Apply(new PointD(5000, 5000), NudgeKey.Left, false, [Retina]));
}

public class CaptureWindowPolicyTests
{
    private static readonly HashSet<uint> Own = [11, 12, 13];
    private static readonly HashSet<uint> Protected = [12, 99];

    private static void AssertSet(IEnumerable<uint> expected, IEnumerable<uint> actual) =>
        Assert.True(new HashSet<uint>(expected).SetEquals(actual), $"expected {{{string.Join(",", expected)}}} but was {{{string.Join(",", actual)}}}");

    [Fact]
    public void AScreenshotProtectsOnlyTheSurfacesTakingIt() =>
        AssertSet([12], ScreenshotCapturePolicy.ProtectedWindowIds([12], [11, 13], honoursVisibilityPreference: true));

    [Fact]
    public void ARecordingIsExemptFromThePreferenceAndProtectsBothKinds() =>
        AssertSet([11, 12, 13], ScreenshotCapturePolicy.ProtectedWindowIds([12], [11, 13], honoursVisibilityPreference: false));

    [Fact]
    public void ShowingOwnWindowsLeavesAnEditorAndAPinInTheCapture()
    {
        var protectedIds = ScreenshotCapturePolicy.ProtectedWindowIds([12], [11, 13], true);
        AssertSet([12], ScreenshotCapturePolicy.ExcludedWindowIds(false, Own, protectedIds));
    }

    [Fact]
    public void APinnedCaptureCanBePickedWhileOwnWindowsAreShown()
    {
        var protectedIds = ScreenshotCapturePolicy.ProtectedWindowIds([12], [11, 13], true);
        Assert.True(ScreenshotCapturePolicy.CanPickWindow(13, true, false, protectedIds));
    }

    [Fact]
    public void HidingOwnWindowsExcludesEveryOne() =>
        AssertSet(Own, ScreenshotCapturePolicy.ExcludedWindowIds(true, Own, Protected));

    [Fact]
    public void ProtectedWindowsStayExcludedWhileOwnWindowsAreVisible() =>
        AssertSet([12], ScreenshotCapturePolicy.ExcludedWindowIds(false, Own, Protected));

    [Fact]
    public void StaleProtectedIdsCannotAffectAnotherApp() =>
        Assert.DoesNotContain(99u, ScreenshotCapturePolicy.ExcludedWindowIds(false, Own, Protected));

    [Fact]
    public void AnExternalWindowCanAlwaysBePicked() =>
        Assert.True(ScreenshotCapturePolicy.CanPickWindow(7, false, true, Protected));

    [Fact]
    public void AnOwnWindowCannotBePickedWhileHidingThem() =>
        Assert.False(ScreenshotCapturePolicy.CanPickWindow(11, true, true, Protected));

    [Fact]
    public void AnOrdinaryOwnWindowCanBePickedWhenVisible() =>
        Assert.True(ScreenshotCapturePolicy.CanPickWindow(11, true, false, Protected));

    [Fact]
    public void ItsOwnProtectedCaptureUiCannotBePicked() =>
        Assert.False(ScreenshotCapturePolicy.CanPickWindow(12, true, false, Protected));

    // The island follows "show in screenshots" in the app's own captures too, except while it is the
    // tool taking the picture.
    private const uint Island = 20, IslandCopy = 21, Editor = 11, Overlay = 12;
    private static readonly HashSet<uint> IslandWindows = [Island, IslandCopy];
    private static readonly HashSet<uint> OwnWithIsland = [Editor, Overlay, Island, IslandCopy];

    private static HashSet<uint> Shown(bool preference, bool tool) =>
        ScreenshotCapturePolicy.IslandCaptureWindowIds(IslandWindows, Island, preference, tool);

    [Fact]
    public void TheIslandAtRestIsInThePictureWhenItShowsInCaptures() => AssertSet(IslandWindows, Shown(true, false));

    [Fact]
    public void TheCaptureControlsStayOutButCopiesOnOtherDisplaysStayIn() => AssertSet([IslandCopy], Shown(true, true));

    [Fact]
    public void TheIslandStaysOutWhenItDoesNotShowInCaptures()
    {
        Assert.Empty(Shown(false, false));
        Assert.Empty(Shown(false, true));
    }

    [Fact]
    public void WithoutAMainIslandWindowEveryCopyStaysIn() =>
        AssertSet(IslandWindows, ScreenshotCapturePolicy.IslandCaptureWindowIds(IslandWindows, null, true, true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HidingOwnWindowsLeavesTheShownIslandIn(bool captureTool)
    {
        var visible = Shown(true, captureTool);
        var protectedIds = new HashSet<uint>(IslandWindows.Except(visible)) { Overlay };
        var hidden = ScreenshotCapturePolicy.ExcludedWindowIds(true, OwnWithIsland, protectedIds, visible);
        AssertSet(OwnWithIsland.Except(visible), hidden);
        var kept = ScreenshotCapturePolicy.ExcludedWindowIds(false, OwnWithIsland, protectedIds, visible);
        AssertSet(protectedIds, kept);
    }

    [Fact]
    public void ACallerThatProtectsTheIslandKeepsItOutEvenWhenItShowsInCaptures() =>
        AssertSet(OwnWithIsland, ScreenshotCapturePolicy.ExcludedWindowIds(
            true, OwnWithIsland, IslandWindows.Union([Overlay]), Shown(true, false)));
}

public class DecorationWindowTests
{
    private static readonly ScreenshotCapturePolicy.CaptureWindow Framed = new(81, 500, new RectD(100, 100, 800, 600));
    private static readonly ScreenshotCapturePolicy.CaptureWindow Decoration = new(80, 700, Framed.Frame.InsetBy(-12, -12), true);

    private static void AssertSet(IEnumerable<uint> expected, IEnumerable<uint> actual) =>
        Assert.True(new HashSet<uint>(expected).SetEquals(actual));

    [Fact]
    public void ABorderDrawnByAnotherProcessIsNeverThePickedWindow()
    {
        AssertSet([80], ScreenshotCapturePolicy.DecorationWindowIds([Decoration, Framed]));
        AssertSet([80], ScreenshotCapturePolicy.DecorationWindowIds([Framed, Decoration]));
    }

    [Fact]
    public void ABorderOnlyFramesTheWindowRightNextToItInTheStack()
    {
        var between = new ScreenshotCapturePolicy.CaptureWindow(90, 800, new RectD(0, 0, 300, 300));
        Assert.Empty(ScreenshotCapturePolicy.DecorationWindowIds([Decoration, between, Framed]));
    }

    public static IEnumerable<object[]> OrdinaryWindows()
    {
        yield return [new ScreenshotCapturePolicy.CaptureWindow(82, 600, Framed.Frame.InsetBy(-16, -16))];                    // titled
        yield return [new ScreenshotCapturePolicy.CaptureWindow(83, 600, Framed.Frame, true)];                                 // equal frame
        yield return [new ScreenshotCapturePolicy.CaptureWindow(84, 600, new RectD(90, 80, 830, 640), true)];                  // uneven margins
        yield return [new ScreenshotCapturePolicy.CaptureWindow(85, 600, Framed.Frame.InsetBy(-40, -40), true)];               // too far
        yield return [new ScreenshotCapturePolicy.CaptureWindow(86, 500, Framed.Frame.InsetBy(-12, -12), true)];               // same app
        yield return [new ScreenshotCapturePolicy.CaptureWindow(87, 600, Framed.Frame.InsetBy(-0.5, -0.5), true)];             // under the minimum margin
    }

    [Theory]
    [MemberData(nameof(OrdinaryWindows))]
    public void ATitledEqualUnevenDistantOrSameAppWindowStaysPickable(ScreenshotCapturePolicy.CaptureWindow window)
    {
        Assert.Empty(ScreenshotCapturePolicy.DecorationWindowIds([window, Framed]));
        Assert.Empty(ScreenshotCapturePolicy.DecorationWindowIds([Framed, window]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(32)]
    public void AnyMarginFromOneToThirtyTwoPointsCountsAsADecoration(double margin)
    {
        var border = new ScreenshotCapturePolicy.CaptureWindow(80, 700, Framed.Frame.InsetBy(-margin, -margin), true);
        AssertSet([80], ScreenshotCapturePolicy.DecorationWindowIds([border, Framed]));
    }

    [Fact]
    public void AMarginPastThirtyTwoPointsDoesNot()
    {
        var border = new ScreenshotCapturePolicy.CaptureWindow(80, 700, Framed.Frame.InsetBy(-33, -33), true);
        Assert.Empty(ScreenshotCapturePolicy.DecorationWindowIds([border, Framed]));
    }
}

public class AttachedCaptureTests
{
    private static readonly ScreenshotCapturePolicy.CaptureWindow Captured = new(1, 500, new RectD(100, 100, 800, 600));
    private static readonly ScreenshotCapturePolicy.CaptureWindow Sheet = new(2, 500, new RectD(300, 100, 400, 300));

    private static ScreenshotCapturePolicy.AttachedCapturePlan Plan(params uint[] ids) => new(ids, Captured.Frame);

    [Fact]
    public void ASheetOnTheClickedWindowJoinsItsCaptureWithTheWindowDrawnFirst() =>
        Assert.Equal(Plan(1, 2), ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [Sheet, Captured]));

    [Fact]
    public void AWindowBehindTheClickedOneIsNotStackedOnIt() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [Captured, Sheet]));

    [Fact]
    public void AnotherApplicationsWindowOverTheClickedOneStaysOutOfTheShot() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured,
            [new(3, 900, new RectD(300, 100, 400, 300)), Captured]));

    [Fact]
    public void ASameAppWindowReachingPastTheClickedWindowIsNotDrawnIntoItsShot() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured,
            [new(4, 500, new RectD(700, 300, 500, 400)), Captured]));

    [Fact]
    public void AWindowOfTheSameAppThatDoesNotTouchTheClickedOneIsNotAttached() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured,
            [new(5, 500, new RectD(2_000, 100, 200, 200)), Captured]));

    [Fact]
    public void AWindowWithNothingStackedOnItKeepsTheSingleWindowCapture() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [Captured]));

    [Fact]
    public void ASheetTheFullWidthOfItsWindowStillJoins()
    {
        var fullWidth = new ScreenshotCapturePolicy.CaptureWindow(6, 500, new RectD(100, 100, 800, 200));
        Assert.Equal([1u, 6u], ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [fullWidth, Captured])!.WindowIds);
    }

    [Fact]
    public void WhatIsStackedOnTheWindowIsDrawnBackToFront()
    {
        var fullWidth = new ScreenshotCapturePolicy.CaptureWindow(6, 500, new RectD(100, 100, 800, 200));
        Assert.Equal([1u, 6u, 2u], ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [Sheet, fullWidth, Captured])!.WindowIds);
    }

    [Fact]
    public void ATargetMissingFromTheListDoesNotTreatEverythingAsStackedOnIt() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(Captured, [Sheet]));

    [Fact]
    public void AnEmptyTargetFrameHasNothingAttached() =>
        Assert.Null(ScreenshotCapturePolicy.AttachedCapturePlanFor(
            Captured with { Frame = new RectD(100, 100, 0, 600) }, [Sheet, Captured with { Frame = new RectD(100, 100, 0, 600) }]));

    [Fact]
    public void TheDisplayCropDeclinesAWindowStraddlingTwoDisplays()
    {
        var left = new RectD(-1200, 0, 1200, 900);
        var right = new RectD(0, 0, 1600, 1000);
        RectD[] displays = [left, right];
        int? Find(RectD window) => ScreenshotCapturePolicy.DisplayCoveringWindow(window, displays, d => d);
        Assert.Null(Find(new RectD(-40, 40, 100, 80)));
        Assert.Null(Find(new RectD(-1210, 40, 100, 80)));
        Assert.Equal(0, Find(new RectD(-100, 40, 100, 80)));
        Assert.Equal(1, Find(right));
        Assert.Null(Find(new RectD(1650, 30, 90, 70)));
        Assert.Null(Find(new RectD(10, 10, 0, 50)));
    }

    [Fact]
    public void AccessibilityConfirmationKeepsItsMatchingAttachmentInCaptureOrder()
    {
        var geometric = Plan(1, 6, 2);
        Assert.Equal(geometric, ScreenshotCapturePolicy.ConfirmedAttachment(geometric, null));
        Assert.Equal(Plan(1, 2), ScreenshotCapturePolicy.ConfirmedAttachment(geometric, new HashSet<uint> { 2 }));
        Assert.Equal(Plan(1, 6, 2), ScreenshotCapturePolicy.ConfirmedAttachment(geometric, new HashSet<uint> { 2, 6, 77 }));
        Assert.Null(ScreenshotCapturePolicy.ConfirmedAttachment(geometric, new HashSet<uint>()));
        Assert.Null(ScreenshotCapturePolicy.ConfirmedAttachment(geometric, new HashSet<uint> { 1 }));
    }

    // ---- Window packing and composite geometry ---------------------------------------------------

    private static ScreenshotCapturePolicy.AlphaCoverage Coverage(PointD at)
    {
        var alpha = new byte[40 * 20];
        for (var row = (int)at.Y; row < (int)at.Y + 6; row++)
            for (var column = (int)at.X; column < (int)at.X + 8; column++) alpha[row * 40 + column] = 255;
        return new ScreenshotCapturePolicy.AlphaCoverage(alpha, 40, 20);
    }

    private static readonly RectD Placed = new(20, 10, 8, 6);
    private static readonly RectD Packed = new(0, 0, 8, 6);

    [Fact]
    public void AWindowDrawnWhereItSitsOnTheDisplayIsCroppedThere() =>
        Assert.Equal(Placed, ScreenshotCapturePolicy.AttachedCaptureCrop(Placed, Packed, Coverage(new PointD(20, 10))));

    [Fact]
    public void WindowsPackedIntoTheCornerAreCroppedWholeNotAsASlice() =>
        Assert.Equal(Packed, ScreenshotCapturePolicy.AttachedCaptureCrop(Placed, Packed, Coverage(new PointD(0, 0))));

    [Fact]
    public void AnOverlappingPlacementStillFollowsWhereTheWindowWasDrawn()
    {
        var overlapping = new RectD(4, 2, 8, 6);
        Assert.Equal(overlapping, ScreenshotCapturePolicy.AttachedCaptureCrop(overlapping, Packed, Coverage(new PointD(4, 2))));
    }

    [Fact]
    public void CoverageOutsideTheImageOrEmptyCountsAsNothing()
    {
        var coverage = Coverage(new PointD(0, 0));
        Assert.Equal(0, coverage.Sum(new RectD(100, 100, 5, 5)));
        Assert.Equal(0, coverage.Sum(new RectD(2, 2, 0, 4)));
        Assert.Equal(8 * 6 * 255, coverage.Sum(new RectD(-5, -5, 50, 50)));
    }

    [Fact]
    public void ASheetOnAWindowSpanningTwoDisplaysLandsAtItsPlaceUnderTheTitleBar()
    {
        var parent = new RectD(1200, 100, 800, 600);
        Assert.Equal(new RectD(0, 0, 1600, 1200), ScreenshotCapturePolicy.CompositeRect(parent, parent, 2));
        // Mirrored: upstream's bottom-left canvas gave y = 744; from the top it is 56.
        Assert.Equal(new RectD(400, 56, 800, 400),
            ScreenshotCapturePolicy.CompositeRect(new RectD(1400, 128, 400, 200), parent, 2));
    }

    private static readonly RectD LayerFrame = new(1200, 100, 800, 600);

    [Fact]
    public void AWholeWindowBufferCoversItsFrameAllowingAPixelOfRounding()
    {
        Assert.True(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1200, LayerFrame, 2));
        Assert.True(ScreenshotCapturePolicy.LayerCoversFrame(1601, 1199, LayerFrame, 2));
    }

    [Fact]
    public void ABufferAFewPercentShortIsDroppedInsteadOfStretched()
    {
        Assert.False(ScreenshotCapturePolicy.LayerCoversFrame(1552, 1200, LayerFrame, 2));
        Assert.False(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1164, LayerFrame, 2));
        Assert.False(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1200, LayerFrame, 0));
        Assert.False(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1200, new RectD(0, 0, 0, 10), 2));
    }

    [Fact]
    public void TheClickedWindowsBufferSetsOneDisplayScaleForEveryLayer()
    {
        Assert.Equal(1.0, ScreenshotCapturePolicy.LayerScale(800, 600, LayerFrame, [1, 2]));
        Assert.Equal(2.0, ScreenshotCapturePolicy.LayerScale(1600, 1200, LayerFrame, [1, 2]));
        Assert.Null(ScreenshotCapturePolicy.LayerScale(1552, 1200, LayerFrame, [1, 2]));
    }

    [Fact]
    public void AWholeBufferOnMixedDisplaysReportsTheScaleItsPixelsWereDrawnAt()
    {
        Assert.Equal(ScreenshotCapturePolicy.CompositeTargetCapture.Buffer(1),
            ScreenshotCapturePolicy.CompositeTarget((800, 600), LayerFrame, [1, 2]));
        Assert.Equal(ScreenshotCapturePolicy.CompositeTargetCapture.Buffer(2),
            ScreenshotCapturePolicy.CompositeTarget((1600, 1200), LayerFrame, [1, 2]));
    }

    [Fact]
    public void AClippedOrMissingBufferOnMixedDisplaysIsRecapturedAtTheFinestScale()
    {
        var clipped = ScreenshotCapturePolicy.CompositeTarget((776, 600), LayerFrame, [1, 2]);
        Assert.Equal(ScreenshotCapturePolicy.CompositeTargetCapture.Recapture(2), clipped);
        Assert.Equal(ScreenshotCapturePolicy.CompositeTargetCapture.Recapture(2),
            ScreenshotCapturePolicy.CompositeTarget(null, LayerFrame, [1, 2]));
        // The recaptured canvas reports 2x, matching its pixels, not a clicked 1x display.
        var canvas = ScreenshotCapturePolicy.CompositeRect(LayerFrame, LayerFrame, clipped!.Value.Scale);
        Assert.Equal(new SizeD(1600, 1200), new SizeD(canvas.Width, canvas.Height));
        Assert.True(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1200, LayerFrame, clipped.Value.Scale));
        Assert.False(ScreenshotCapturePolicy.LayerCoversFrame(1600, 1200, LayerFrame, 1));
    }

    [Fact]
    public void WithNoDisplayScaleKnownThereIsNoCompositeToReport() =>
        Assert.Null(ScreenshotCapturePolicy.CompositeTarget((1600, 1200), LayerFrame, []));
}
