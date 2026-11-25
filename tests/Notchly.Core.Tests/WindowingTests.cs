// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream's window layout, gesture, edge snap and switcher specs: WindowLayoutFeatureTests, WindowEdgeSnapRestoreTests,
// PointerInputFeatureTests (gestures) and SwitcherModelFeatureTests. Upstream's rectangles are in AppKit points (y up); these are
// the same cases in pixels with y down, on a 1440×900 display whose work area is 1440×860 (a 40 pixel taskbar at the bottom).
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;
using static Notchly.Core.Windowing.WindowLayoutAction;

namespace Notchly.Core.Tests;

public class WindowingTests
{
    private static readonly WindowRect Visible = new(0, 0, 1440, 860);
    private static readonly WindowRect Current = new(200, 200, 800, 500);

    private static WindowRect R(WindowLayoutAction action, double windowGap = 0, double screenGap = 0, double margin = 5) =>
        WindowLayoutSupport.Rect(action, Current, Visible, windowGap, screenGap, margin);

    // MARK: Geometry

    [Fact]
    public void HalvesAndCentreHalf()
    {
        Assert.Equal(new WindowRect(0, 0, 720, 860), R(LeftHalf));
        Assert.Equal(new WindowRect(720, 0, 720, 860), R(RightHalf));
        Assert.Equal(new WindowRect(0, 0, 1440, 430), R(TopHalf));
        Assert.Equal(new WindowRect(0, 430, 1440, 430), R(BottomHalf));
        Assert.Equal(new WindowRect(360, 0, 720, 860), R(CenterHalf));
    }

    [Fact]
    public void ThirdsAndTwoThirds()
    {
        Assert.Equal(new WindowRect(0, 0, 480, 860), R(LeftThird));
        Assert.Equal(new WindowRect(480, 0, 480, 860), R(CenterThird));
        Assert.Equal(new WindowRect(960, 0, 480, 860), R(RightThird));
        Assert.Equal(new WindowRect(0, 0, 960, 860), R(LeftTwoThirds));
        Assert.Equal(new WindowRect(480, 0, 960, 860), R(RightTwoThirds));
        Assert.Equal(new WindowRect(240, 0, 960, 860), R(CenterTwoThirds));
        // The rows round outwards, so three rows of 286.67 become 287, 288 and 287.
        Assert.Equal(new WindowRect(0, 0, 1440, 287), R(TopThird));
        Assert.Equal(new WindowRect(0, 286, 1440, 288), R(MiddleThird));
        Assert.Equal(new WindowRect(0, 573, 1440, 287), R(BottomThird));
    }

    [Fact]
    public void QuartersAndSixthsAndCorners()
    {
        Assert.Equal([0.0, 215, 430, 645], new[] { TopQuarter, UpperMiddleQuarter, LowerMiddleQuarter, BottomQuarter }.Select(a => R(a).Y));
        Assert.All(new[] { TopQuarter, UpperMiddleQuarter, LowerMiddleQuarter, BottomQuarter }, a => Assert.Equal(215, R(a).Height));
        Assert.Equal([0.0, 360, 720, 1080], new[] { LeftQuarter, LeftMiddleQuarter, RightMiddleQuarter, RightQuarter }.Select(a => R(a).X));
        Assert.Equal(new WindowRect(0, 0, 480, 430), R(TopLeftSixth));
        Assert.Equal(new WindowRect(960, 430, 480, 430), R(BottomRightSixth));
        Assert.Equal(new WindowRect(0, 0, 720, 430), R(TopLeft));
        Assert.Equal(new WindowRect(720, 430, 720, 430), R(BottomRight));
    }

    [Fact]
    public void MaximizeMarginAndCentre()
    {
        Assert.Equal(Visible, R(Maximize));
        Assert.Equal(new WindowRect(72, 43, 1296, 774), R(MarginMaximize));
        Assert.Equal(new WindowRect(144, 86, 1152, 688), R(MarginMaximize, margin: 10));
        Assert.Equal(Visible, R(MarginMaximize, margin: 0));
        Assert.Equal(new WindowRect(320, 180, 800, 500), R(Center));
        // Restore and the display moves keep the frame; the caller does the rest.
        Assert.Equal(Current, R(Restore));
        Assert.Equal(Current, R(NextDisplay));
        Assert.Equal(Current, R(FullScreen));
    }

    [Fact]
    public void ABigWindowIsKeptOnTheScreenWhenCentred()
    {
        Assert.Equal(new WindowRect(0, 0, 1440, 860), WindowLayoutSupport.Rect(Center, new WindowRect(10, 10, 3000, 2000), Visible));
    }

    [Fact]
    public void TheMarginIsSanitized()
    {
        Assert.Equal(0, WindowLayoutSupport.SanitizedMargin(-10));
        Assert.Equal(25, WindowLayoutSupport.SanitizedMargin(80));
        Assert.Equal(5, WindowLayoutSupport.SanitizedMargin(double.NaN));
        Assert.Equal(5, WindowLayoutSupport.SanitizedMargin(double.PositiveInfinity));
        Assert.Equal(12, WindowLayoutSupport.SanitizedMargin(12));
        // A portrait display at the top margin.
        Assert.Equal(new WindowRect(250, 400, 500, 800), WindowLayoutSupport.Rect(MarginMaximize, Current, new WindowRect(0, 0, 1000, 1600), marginPercent: 25));
    }

    [Fact]
    public void WindowGapsSplitTheSpaceBetweenNeighbours()
    {
        Assert.Equal(new WindowRect(0, 0, 712, 860), R(LeftHalf, windowGap: 16));
        Assert.Equal(new WindowRect(728, 0, 712, 860), R(RightHalf, windowGap: 16));
        Assert.Equal(new WindowRect(488, 0, 464, 860), R(CenterThird, windowGap: 16));
        Assert.Equal(new WindowRect(0, 0, 704, 414), R(TopLeft, windowGap: 32));
        // Maximize and the others are exempt.
        Assert.Equal(Visible, R(Maximize, windowGap: 32));
    }

    [Fact]
    public void ScreenGapsPullInTheFrame()
    {
        Assert.Equal(new WindowRect(32, 32, 688, 796), R(LeftHalf, screenGap: 32));
        Assert.Equal(new WindowRect(32, 32, 680, 796), R(LeftHalf, windowGap: 16, screenGap: 32));
        Assert.Equal(new WindowRect(32, 32, 1376, 796), R(Maximize, screenGap: 32));
        Assert.Equal(new WindowRect(10, 10, 80, 80), WindowLayoutSupport.ScreenGapFrame(new WindowRect(0, 0, 100, 100), 128));
        // Margin and centre ignore it.
        Assert.Equal(R(MarginMaximize), R(MarginMaximize, screenGap: 64));
        Assert.Equal([0, 8, 16, 32, 64, 128], WindowLayoutSupport.GapPresets);
        Assert.Equal(0, WindowLayoutSupport.SanitizedGap(7));
        Assert.Equal(64, WindowLayoutSupport.SanitizedGap(64));
    }

    [Fact]
    public void AnAppThatKeepsALargerSizeStaysAgainstItsEdge()
    {
        WindowRect Quarter(WindowLayoutAction a) => R(a);
        Assert.Equal(new WindowRect(0, 0, 600, 860), WindowLayoutSupport.Anchored(LeftQuarter, Quarter(LeftQuarter), Visible, 600, 860));
        Assert.Equal(840, WindowLayoutSupport.Anchored(RightQuarter, Quarter(RightQuarter), Visible, 600, 860).X);
        Assert.Equal(480, WindowLayoutSupport.Anchored(RightMiddleQuarter, Quarter(RightMiddleQuarter), Visible, 840, 860).X);
        Assert.Equal(new WindowRect(0, 0, 1440, 400), WindowLayoutSupport.Anchored(TopQuarter, Quarter(TopQuarter), Visible, 1440, 400));
        Assert.Equal(460, WindowLayoutSupport.Anchored(BottomQuarter, Quarter(BottomQuarter), Visible, 1440, 400).Y);
        Assert.Equal(230, WindowLayoutSupport.Anchored(MiddleThird, R(MiddleThird), Visible, 1440, 400).Y, 0);
    }

    [Fact]
    public void AnAppliedLayoutMustHaveLandedWhereAskedAndBeBigEnough()
    {
        var asked = R(LeftHalf);
        Assert.True(WindowLayoutSupport.Accepts(LeftHalf, asked, new WindowRect(0, 0, 720, 860)));
        Assert.True(WindowLayoutSupport.Accepts(LeftHalf, asked, new WindowRect(0, 0, 760, 860)));
        Assert.False(WindowLayoutSupport.Accepts(LeftHalf, asked, new WindowRect(800, 0, 600, 860)));
        Assert.False(WindowLayoutSupport.Accepts(LeftHalf, asked, new WindowRect(0, 0, 60, 860)));
    }

    [Fact]
    public void EveryActionHasAStableName()
    {
        foreach (var action in Enum.GetValues<WindowLayoutAction>())
            Assert.Equal(action, WindowLayoutSupport.ParseAction(WindowLayoutSupport.RawName(action)));
        Assert.Equal("leftHalf", WindowLayoutSupport.RawName(LeftHalf));
        Assert.Equal("marginMaximize", WindowLayoutSupport.RawName(MarginMaximize));
        Assert.Null(WindowLayoutSupport.ParseAction("nonsense"));
        Assert.Equal("windowLayoutShortcutLeftHalf", WindowLayoutSupport.ShortcutKey(LeftHalf));
    }

    [Fact]
    public void ShortcutDefaultsParseAndMatchUpstream()
    {
        Assert.Equal("control+alt:37", WindowLayoutSupport.DefaultShortcut(LeftHalf));
        Assert.Equal("control+alt:13", WindowLayoutSupport.DefaultShortcut(Maximize));
        Assert.Equal("control+alt+win:39", WindowLayoutSupport.DefaultShortcut(NextDisplay));
        Assert.Equal("none", WindowLayoutSupport.DefaultShortcut(CenterHalf));
        foreach (var action in Enum.GetValues<WindowLayoutAction>())
        {
            var text = WindowLayoutSupport.DefaultShortcut(action);
            if (text != "none") Assert.NotNull(Shortcut.Parse(text));
        }
        // The defaults are registered under each action's own key.
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.Equal("control+alt:37", store.GetString(WindowLayoutSupport.ShortcutKey(LeftHalf)));
        Assert.Equal("none", store.GetString(WindowLayoutSupport.ShortcutKey(CenterHalf)));
        Assert.False(store.GetBool(DefaultsKey.WindowLayoutShortcutsEnabled));
        Assert.Equal(5.0, store.GetDouble(DefaultsKey.WindowLayoutMarginPercent, -1));
    }

    // MARK: Pressing a direction again

    [Fact]
    public void RepeatedDirectionsGoOn()
    {
        // First press: the half.
        Assert.Equal(new WindowLayoutSupport.DirectionalStep(LeftHalf, null), WindowLayoutSupport.NextDirectional(DisplayDirection.Left, null, false, false, true));
        // Again: across to the next display when there is one, otherwise it stays.
        Assert.Equal(new WindowLayoutSupport.DirectionalStep(null, DisplayDirection.Left), WindowLayoutSupport.NextDirectional(DisplayDirection.Left, LeftHalf, true, false, true));
        Assert.Equal(new WindowLayoutSupport.DirectionalStep(LeftHalf, null), WindowLayoutSupport.NextDirectional(DisplayDirection.Left, LeftHalf, true, false, false));
        // Up twice with nothing above: maximize.
        Assert.Equal(new WindowLayoutSupport.DirectionalStep(Maximize, null), WindowLayoutSupport.NextDirectional(DisplayDirection.Up, TopHalf, true, false, false));
        // A press after the window was moved by hand starts again.
        Assert.Equal(new WindowLayoutSupport.DirectionalStep(RightHalf, null), WindowLayoutSupport.NextDirectional(DisplayDirection.Right, RightHalf, false, false, true));
        Assert.Equal(LeftHalf, WindowLayoutSupport.OppositeHalf(DisplayDirection.Right));
        Assert.Equal(TopHalf, WindowLayoutSupport.OppositeHalf(DisplayDirection.Down));
    }

    [Fact]
    public void SideKeysCanCycleThirds()
    {
        var sequence = new List<WindowLayoutAction?>();
        WindowLayoutAction? last = null;
        for (var i = 0; i < 4; i++)
        {
            var step = WindowLayoutSupport.NextDirectional(DisplayDirection.Left, last, last is not null, true, true);
            Assert.Null(step.CrossTo);
            last = step.Action;
            sequence.Add(last);
        }
        Assert.Equal([LeftHalf, LeftTwoThirds, LeftThird, LeftHalf], sequence);
        // Up and down still cross while the cycle is on.
        Assert.Equal(DisplayDirection.Down, WindowLayoutSupport.NextDirectional(DisplayDirection.Down, BottomHalf, true, true, true).CrossTo);
    }

    // MARK: Displays

    private static readonly WindowRect Main = new(0, 0, 1440, 900);
    private static readonly WindowRect West = new(-1200, -100, 1200, 1920);
    private static readonly WindowRect East = new(1440, 300, 2560, 1440);
    private static readonly WindowRect South = new(0, 900, 1440, 900);

    [Fact]
    public void NextAndPreviousDisplayFollowReadingOrder()
    {
        WindowRect[] displays = [Main, West, East];
        Assert.Equal(2, WindowLayoutSupport.AdjacentIndex(displays, 0, forward: true));
        Assert.Equal(1, WindowLayoutSupport.AdjacentIndex(displays, 0, forward: false));
        Assert.Equal(0, WindowLayoutSupport.AdjacentIndex(displays, 1, forward: true));
        Assert.Equal(0, WindowLayoutSupport.AdjacentIndex(displays, 2, forward: false));
        Assert.Null(WindowLayoutSupport.AdjacentIndex([Main], 0, forward: true));
    }

    [Fact]
    public void ANeighbourLiesBeyondTheEdge()
    {
        WindowRect[] displays = [Main, West, East, South];
        Assert.Equal(1, WindowLayoutSupport.NeighbourIndex(displays, 0, DisplayDirection.Left));
        Assert.Equal(2, WindowLayoutSupport.NeighbourIndex(displays, 0, DisplayDirection.Right));
        Assert.Equal(3, WindowLayoutSupport.NeighbourIndex(displays, 0, DisplayDirection.Down));
        Assert.Null(WindowLayoutSupport.NeighbourIndex(displays, 0, DisplayDirection.Up));
        Assert.Equal(0, WindowLayoutSupport.NeighbourIndex(displays, 3, DisplayDirection.Up));
        Assert.Null(WindowLayoutSupport.NeighbourIndex([Main], 0, DisplayDirection.Left));
    }

    [Fact]
    public void AWindowKeepsItsPlaceOnAnotherDisplay()
    {
        var source = Visible;
        var destination = new WindowRect(1440, 80, 1920, 1000);
        Assert.Equal(new WindowRect(1640, 280, 800, 500), WindowLayoutSupport.RectForDisplay(Current, source, destination));
        // A right half stays flush right, a left half flush left.
        Assert.Equal(new WindowRect(2640, 80, 720, 860), WindowLayoutSupport.RectForDisplay(R(RightHalf), source, destination));
        Assert.Equal(new WindowRect(1440, 80, 720, 860), WindowLayoutSupport.RectForDisplay(R(LeftHalf), source, destination));
        // One that filled the display fills the next; an oversized one is shrunk to it.
        Assert.Equal(destination, WindowLayoutSupport.RectForDisplay(Visible, source, destination));
        Assert.Equal(destination, WindowLayoutSupport.RectForDisplay(new WindowRect(0, 0, 1436, 856), source, destination));
        Assert.Equal(destination, WindowLayoutSupport.RectForDisplay(new WindowRect(100, 100, 2500, 1500), source, destination));
        // A short destination shrinks the height and keeps the window inside it.
        var short_ = new WindowRect(1440, 80, 1920, 400);
        Assert.Equal(new WindowRect(1640, 80, 800, 400), WindowLayoutSupport.RectForDisplay(Current, source, short_));
    }

    // MARK: History

    [Fact]
    public void HistoryKeepsTwentyFramesAndSkipsTheCurrentOne()
    {
        var history = new WindowLayoutSupport.History();
        for (var i = 0; i < 25; i++) history.Push("w", new WindowRect(i * 10, 0, 100, 100));
        Assert.Equal(20, history.Count("w"));
        var current = new WindowRect(240, 0, 100, 100);
        // The newest frame is where the window is now: Restore goes to the one before.
        Assert.Equal(new WindowRect(230, 0, 100, 100), history.PopPrevious("w", current));
        Assert.Equal(new WindowRect(220, 0, 100, 100), history.PopPrevious("w", new WindowRect(0, 0, 1, 1)));
        history.Push("w", new WindowRect(5, 5, 5, 5));
        history.DiscardLatest("w");
        Assert.Equal(new WindowRect(210, 0, 100, 100), history.PopPrevious("w", new WindowRect(0, 0, 1, 1)));
        Assert.Null(history.PopPrevious("none", current));
        history.RemoveStale(new HashSet<string> { "other" });
        Assert.Equal(0, history.Count("w"));
    }

    // MARK: Gesture

    [Fact]
    public void GestureModifiersFallBackToTheDefault()
    {
        Assert.Equal(["control", "win"], WindowGestureSupport.Modifiers(null));
        Assert.Equal(["alt"], WindowGestureSupport.Modifiers("alt+shift"));
        Assert.Equal(["control", "win"], WindowGestureSupport.Modifiers("shift"));
        Assert.Equal(["control", "win"], WindowGestureSupport.Modifiers("invalid"));
        Assert.Equal(["alt", "win"], WindowGestureSupport.Modifiers("command+option"));
        Assert.Equal("control+win", WindowGestureSupport.StorageValue(["win", "control"]));
        Assert.Equal(["control", "win", "shift"], WindowGestureSupport.ResizeChord(["control", "win"]));
    }

    [Fact]
    public void ADragIsAClickUntilItGoesPastTheSlop()
    {
        Assert.False(WindowGestureSupport.ExceedsSlop(6, 0));
        Assert.True(WindowGestureSupport.ExceedsSlop(6.1, 0));
        Assert.True(WindowGestureSupport.ExceedsSlop(5, 4));
        Assert.False(WindowGestureSupport.ExceedsSlop(5, 3));
    }

    [Fact]
    public void TheGestureStateMachine()
    {
        var (d, p) = WindowGestureSupport.Decide(WindowGestureSupport.Phase.Idle, WindowGestureSupport.Input.ButtonDownWithChord);
        Assert.Equal((WindowGestureSupport.Decision.Arm, WindowGestureSupport.Phase.Pending), (d, p));
        Assert.Equal(WindowGestureSupport.Decision.PassThrough, WindowGestureSupport.Decide(WindowGestureSupport.Phase.Idle, WindowGestureSupport.Input.ButtonDownOther).Decision);
        Assert.Equal((WindowGestureSupport.Decision.Hold, WindowGestureSupport.Phase.Pending), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Pending, WindowGestureSupport.Input.Drag));
        Assert.Equal((WindowGestureSupport.Decision.Promote, WindowGestureSupport.Phase.Active), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Pending, WindowGestureSupport.Input.DragPastSlop));
        Assert.Equal((WindowGestureSupport.Decision.ReplayThenPass, WindowGestureSupport.Phase.Idle), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Pending, WindowGestureSupport.Input.ButtonUp));
        Assert.Equal((WindowGestureSupport.Decision.RestartAsIdle, WindowGestureSupport.Phase.Idle), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Pending, WindowGestureSupport.Input.SameButtonDown));
        Assert.Equal((WindowGestureSupport.Decision.FlushThenPass, WindowGestureSupport.Phase.Idle), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Pending, WindowGestureSupport.Input.FeatureOff));
        Assert.Equal((WindowGestureSupport.Decision.ApplyMove, WindowGestureSupport.Phase.Active), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Active, WindowGestureSupport.Input.Drag));
        Assert.Equal((WindowGestureSupport.Decision.ApplyFinish, WindowGestureSupport.Phase.Idle), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Active, WindowGestureSupport.Input.ButtonUp));
        Assert.Equal((WindowGestureSupport.Decision.DropState, WindowGestureSupport.Phase.Idle), WindowGestureSupport.Decide(WindowGestureSupport.Phase.Active, WindowGestureSupport.Input.FeatureOff));
    }

    [Fact]
    public void MovingAndResizingFollowThePointer()
    {
        Assert.Equal(new PointD(145, 55), WindowGestureSupport.MovedOrigin(new PointD(100, 80), new PointD(300, 200), new PointD(345, 175)));
        var frame = new WindowRect(100, 80, 600, 420);
        Assert.Equal(WindowGestureSupport.Edges.Left | WindowGestureSupport.Edges.Top, WindowGestureSupport.RegionFor(frame, new PointD(110, 90)));
        Assert.Equal(WindowGestureSupport.Edges.Top, WindowGestureSupport.RegionFor(frame, new PointD(400, 90)));
        Assert.Equal(WindowGestureSupport.Edges.Right | WindowGestureSupport.Edges.Bottom, WindowGestureSupport.RegionFor(frame, new PointD(690, 490)));
        Assert.NotEqual(WindowGestureSupport.Edges.None, WindowGestureSupport.RegionFor(frame, new PointD(400, 290)));
        Assert.Equal(new WindowRect(150, 110, 550, 390), WindowGestureSupport.Resized(frame, WindowGestureSupport.Edges.Left | WindowGestureSupport.Edges.Top, new PointD(110, 90), new PointD(160, 120)));
        Assert.Equal(new WindowRect(100, 80, 670, 470), WindowGestureSupport.Resized(frame, WindowGestureSupport.Edges.Right | WindowGestureSupport.Edges.Bottom, new PointD(690, 490), new PointD(760, 540)));
        // Dragged far past the opposite edge, the size stops at 120×80.
        Assert.Equal(new WindowRect(580, 420, 120, 80), WindowGestureSupport.Resized(frame, WindowGestureSupport.Edges.Left | WindowGestureSupport.Edges.Top, new PointD(110, 90), new PointD(700, 520)));
    }

    [Fact]
    public void AnAppThatKeepsALargerSizeIsAnchoredBack()
    {
        var requested = new WindowRect(580, 420, 120, 80);
        Assert.Equal(new PointD(440, 320), WindowGestureSupport.Reanchored(WindowGestureSupport.Edges.Left | WindowGestureSupport.Edges.Top, requested, 260, 180));
        Assert.Equal(new PointD(440, 420), WindowGestureSupport.Reanchored(WindowGestureSupport.Edges.Left, requested, 260, 180));
        Assert.Null(WindowGestureSupport.Reanchored(WindowGestureSupport.Edges.Right | WindowGestureSupport.Edges.Bottom, requested, 260, 180));
    }

    [Fact]
    public void TheDirectionalRingHasADeadZoneAndEightSlices()
    {
        var o = new PointD(200, 200);
        Assert.Null(WindowGestureSupport.RingAction(o, new PointD(220, 200)));
        Assert.Equal(RightHalf, WindowGestureSupport.RingAction(o, new PointD(240, 200)));
        Assert.Equal(TopRight, WindowGestureSupport.RingAction(o, new PointD(240, 160)));
        Assert.Equal(TopHalf, WindowGestureSupport.RingAction(o, new PointD(200, 160)));
        Assert.Equal(TopLeft, WindowGestureSupport.RingAction(o, new PointD(160, 160)));
        Assert.Equal(LeftHalf, WindowGestureSupport.RingAction(o, new PointD(160, 200)));
        Assert.Equal(BottomLeft, WindowGestureSupport.RingAction(o, new PointD(160, 240)));
        Assert.Equal(BottomHalf, WindowGestureSupport.RingAction(o, new PointD(200, 240)));
        Assert.Equal(BottomRight, WindowGestureSupport.RingAction(o, new PointD(240, 240)));
    }

    // MARK: Edge snap

    private static readonly SnapScreen One = new(new WindowRect(0, 0, 1440, 900), new WindowRect(0, 0, 1440, 860));

    private static EdgeSnapZone? Zone(double x, double y, params SnapScreen[] screens) =>
        EdgeSnapSupport.ZoneAt(new PointD(x, y), screens.Length == 0 ? [One] : screens)?.Zone;

    [Fact]
    public void EdgeSnapFindsTheZoneUnderThePointer()
    {
        Assert.Equal(EdgeSnapZone.Top, Zone(720, 0));
        Assert.Equal(EdgeSnapZone.Top, Zone(720, 12));
        Assert.Null(Zone(720, 13));
        Assert.Equal(EdgeSnapZone.Left, Zone(0, 450));
        Assert.Equal(EdgeSnapZone.Right, Zone(1440, 450));
        Assert.Equal(EdgeSnapZone.Bottom, Zone(720, 900));
        Assert.Null(Zone(720, 450));
        Assert.Equal(EdgeSnapZone.TopLeft, Zone(0, 0));
        Assert.Equal(EdgeSnapZone.TopRight, Zone(1440, 0));
        Assert.Equal(EdgeSnapZone.BottomLeft, Zone(0, 900));
        Assert.Equal(EdgeSnapZone.BottomRight, Zone(1440, 900));
        // A corner reaches along the edge by about a fifth of it (96 to 180 pixels).
        Assert.Equal(EdgeSnapZone.TopLeft, Zone(150, 0));
        Assert.Equal(EdgeSnapZone.Top, Zone(260, 0));
        Assert.Null(EdgeSnapSupport.ZoneAt(new PointD(720, 0), [One], new HashSet<EdgeSnapZone> { EdgeSnapZone.Top }));
    }

    [Fact]
    public void EdgeSnapWorksOnADisplayLeftOfTheMainOne()
    {
        var west = new SnapScreen(new WindowRect(-1280, 0, 1280, 800), new WindowRect(-1280, 0, 1280, 775));
        var hit = EdgeSnapSupport.ZoneAt(new PointD(-1280, 400), [west]);
        Assert.Equal(EdgeSnapZone.Left, hit?.Zone);
        Assert.Equal(new WindowRect(-1280, 0, 640, 775), WindowLayoutSupport.Rect(LeftHalf, new WindowRect(0, 0, 10, 10), west.Visible));
    }

    [Fact]
    public void ASeamBetweenDisplaysNeedsASlowPointer()
    {
        var east = new SnapScreen(new WindowRect(1440, 0, 1440, 900), new WindowRect(1440, 0, 1440, 860));
        var hit = EdgeSnapSupport.ZoneAt(new PointD(1435, 450), [One, east]);
        Assert.Equal(EdgeSnapZone.Right, hit?.Zone);
        Assert.Equal(0, hit?.Screen);
        Assert.False(EdgeSnapSupport.AllowsSnap(edgeHasNeighbour: true, speedAcrossEdge: 1200));
        Assert.True(EdgeSnapSupport.AllowsSnap(true, 400));
        Assert.True(EdgeSnapSupport.AllowsSnap(true, 100));
        // A wall: any speed.
        Assert.True(EdgeSnapSupport.AllowsSnap(false, 5000));
    }

    [Fact]
    public void EdgesSplitIntoUpToFourParts()
    {
        Assert.Equal(WindowLayoutAction.Maximize, EdgeSnapSupport.DefaultAction(EdgeSnapZone.Top));
        Assert.Equal([LeftThird, CenterThird, RightThird], EdgeSnapSupport.DefaultSplit(EdgeSnapZone.Top, 3));
        Assert.Equal([TopHalf, BottomHalf], EdgeSnapSupport.DefaultSplit(EdgeSnapZone.Left, 2));
        Assert.Equal([TopQuarter, UpperMiddleQuarter, LowerMiddleQuarter, BottomQuarter], EdgeSnapSupport.DefaultSplit(EdgeSnapZone.Right, 4));
        Assert.Equal([TopLeft], EdgeSnapSupport.DefaultSplit(EdgeSnapZone.TopLeft, 3));
        Assert.Equal(0, EdgeSnapSupport.PartIndex(3, 0));
        Assert.Equal(1, EdgeSnapSupport.PartIndex(3, 0.5));
        Assert.Equal(2, EdgeSnapSupport.PartIndex(3, 1));
    }

    [Fact]
    public void ZoneChoicesAreStoredOnlyWhenTheyDiffer()
    {
        var actions = EdgeSnapSupport.ZoneActions.Parse("top=topHalf,right=rightThird,bogus=leftHalf,left=nonsense,bottom=leftHalf+leftHalf+leftHalf+leftHalf+leftHalf");
        Assert.Equal([TopHalf], actions.For(EdgeSnapZone.Top));
        Assert.Equal([RightThird], actions.For(EdgeSnapZone.Right));
        Assert.Equal([LeftHalf], actions.For(EdgeSnapZone.Left));
        Assert.Equal([BottomHalf], actions.For(EdgeSnapZone.Bottom));
        Assert.Equal("top=topHalf,right=rightThird", actions.Storage);
        Assert.Equal("top=leftThird+centerThird+rightThird", new EdgeSnapSupport.ZoneActions().WithPartCount(EdgeSnapZone.Top, 3).Storage);
        Assert.Equal("left=topHalf+bottomHalf", new EdgeSnapSupport.ZoneActions().WithPartCount(EdgeSnapZone.Left, 2).Storage);
        Assert.Equal("", new EdgeSnapSupport.ZoneActions().WithPartCount(EdgeSnapZone.Top, 3).WithPartCount(EdgeSnapZone.Top, 1).Storage);
        var thirds = new EdgeSnapSupport.ZoneActions().WithPartCount(EdgeSnapZone.Top, 3);
        Assert.Equal(CenterThird, EdgeSnapSupport.ActionFor(new SnapHit(EdgeSnapZone.Top, 0, 0.5), thirds));
        Assert.Equal(RightThird, EdgeSnapSupport.ActionFor(new SnapHit(EdgeSnapZone.Top, 0, 0.99), thirds));
        Assert.Equal(new WindowRect(0, 0, 1, 0.5), EdgeSnapSupport.PreviewFractions(TopHalf));
    }

    [Fact]
    public void ThePointerTrailTellsAFlickFromAStop()
    {
        var trail = new EdgeSnapSupport.PointerTrail();
        for (var i = 0; i <= 10; i++) trail.Add(i * 10, 0, i * 0.01);
        Assert.Equal(1000, trail.Velocity(0.1).X, 0);
        // No movement for a tenth of a second reads as stopped.
        Assert.Equal((0.0, 0.0), trail.Velocity(0.2));
        // A nudge of one pixel after rest is slow; a flick of thirty is fast.
        var nudge = new EdgeSnapSupport.PointerTrail();
        nudge.Add(100, 100, 5);
        nudge.Add(101, 100, 5.01);
        Assert.True(Math.Abs(nudge.Velocity(5.01).X) <= 400);
        var flick = new EdgeSnapSupport.PointerTrail();
        flick.Add(100, 100, 5);
        flick.Add(130, 100, 5.01);
        Assert.True(Math.Abs(flick.Velocity(5.01).X) > 400);
    }

    [Fact]
    public void ADragIsAMoveAResizeOrNeither()
    {
        var original = new WindowRect(100, 100, 800, 500);
        Assert.Equal(EdgeSnapSupport.DragKind.Moving, EdgeSnapSupport.Classify(original, new WindowRect(150, 130, 800, 500), 50, 30));
        Assert.Equal(EdgeSnapSupport.DragKind.Resizing, EdgeSnapSupport.Classify(original, new WindowRect(100, 100, 900, 500), 100, 0));
        Assert.Equal(EdgeSnapSupport.DragKind.Resizing, EdgeSnapSupport.Classify(original, new WindowRect(60, 100, 840, 500), -40, 0));
        // Both edges changed and the size too: not a resize by one handle.
        Assert.Equal(EdgeSnapSupport.DragKind.None, EdgeSnapSupport.Classify(original, new WindowRect(150, 150, 900, 550), 50, 50));
        Assert.Equal(EdgeSnapSupport.DragKind.None, EdgeSnapSupport.Classify(original, new WindowRect(101, 100, 800, 500), 1, 0));
        Assert.Equal(EdgeSnapSupport.DragKind.None, EdgeSnapSupport.Classify(original, new WindowRect(150, 100, 800, 500), -50, 0));
    }

    [Fact]
    public void ResizeHandlesAreAtCornersAndEdges()
    {
        var frame = new WindowRect(0, 0, 800, 600);
        Assert.True(EdgeSnapSupport.StartsAtResizeHandle(frame, new PointD(3, 300)));
        Assert.True(EdgeSnapSupport.StartsAtResizeHandle(frame, new PointD(10, 10)));
        Assert.False(EdgeSnapSupport.StartsAtResizeHandle(frame, new PointD(400, 300)));
        Assert.False(EdgeSnapSupport.StartsAtResizeHandle(frame, new PointD(10, 300)));
        Assert.False(EdgeSnapSupport.StartsAtResizeHandle(frame, new PointD(-5, 300)));
    }

    [Fact]
    public void ADraggedSnappedWindowGetsItsOldSizeBack()
    {
        var snapped = new WindowRect(0, 25, 1440, 875);
        var size = new SizeD(800, 500);
        Assert.True(EdgeSnapSupport.NeedsSizeRestore(snapped, size));
        Assert.False(EdgeSnapSupport.NeedsSizeRestore(snapped, new SizeD(1441, 876)));
        Assert.False(EdgeSnapSupport.NeedsSizeRestore(snapped, new SizeD(0, 500)));
        Assert.Equal(new WindowRect(400, 45, 800, 500), EdgeSnapSupport.RestoredFrame(snapped, size, new PointD(720, 40), new PointD(800, 60), null));
        Assert.Equal(30, EdgeSnapSupport.RestoredFrame(snapped, size, new PointD(0, 40), new PointD(30, 60), null).X);
        Assert.Equal(1400, EdgeSnapSupport.RestoredFrame(snapped, size, new PointD(1440, 40), new PointD(1400, 60), null).Right);
        // On a narrower display it stays inside, and never above the top.
        var small = new WindowRect(0, 0, 1000, 900);
        Assert.Equal(200, EdgeSnapSupport.RestoredFrame(snapped, size, new PointD(720, 40), new PointD(800, 60), small).X);
        Assert.Equal(0, EdgeSnapSupport.RestoredFrame(snapped, size, new PointD(720, 40), new PointD(800, 10), small).Y);
    }

    // MARK: Switcher

    [Fact]
    public void TheSelectionStartsAfterTheForegroundEntry()
    {
        Assert.Equal(1, SwitcherSupport.InitialSelection([101, 202, 303], true, 101, false));
        Assert.Equal(2, SwitcherSupport.InitialSelection([101, 202, 303], true, 101, true));
        Assert.Equal(0, SwitcherSupport.InitialSelection([101], true, 101, false));
        Assert.Equal(2, SwitcherSupport.InitialSelection([101, 101, 202], false, 101, false));
        Assert.Equal(0, SwitcherSupport.InitialSelection([], false, 101, false));
    }

    [Fact]
    public void TheCapKeepsOneEntryPerApp()
    {
        Assert.Equal([0, 1, 2], SwitcherSupport.VisibleSelectionIndices([7, 7, 9], 5));
        Assert.Equal([0, 1, 3, 5], SwitcherSupport.VisibleSelectionIndices([1, 2, 1, 3, 1, 4], 4));
        var many = Enumerable.Repeat(101, 18).Concat([202, 303, 404, 505]).ToList();
        var kept = SwitcherSupport.VisibleSelectionIndices(many, 6);
        Assert.Equal(6, kept.Count);
        Assert.Equal(5, kept.Select(i => many[i]).Distinct().Count());
        Assert.Equal([0, 1], kept.Where(i => many[i] == 101));
        Assert.Equal(kept.OrderBy(i => i), kept);
        Assert.Empty(SwitcherSupport.VisibleSelectionIndices([1, 2], 0));
    }

    [Fact]
    public void TheGridBalancesItsRows()
    {
        Assert.Equal(5, SwitcherSupport.GridColumnCount(10, 8));
        Assert.Equal(5, SwitcherSupport.GridColumnCount(9, 8));
        Assert.Equal(6, SwitcherSupport.GridColumnCount(17, 8));
        Assert.Equal(8, SwitcherSupport.GridColumnCount(8, 8));
        Assert.Equal(3, SwitcherSupport.GridColumnCount(3, 8));
        Assert.Equal(8, SwitcherSupport.GridColumnCount(16, 8));
        Assert.Null(SwitcherSupport.SanitizedMaxColumns(0));
        Assert.Null(SwitcherSupport.SanitizedMaxColumns(-3));
        Assert.Equal(2, SwitcherSupport.SanitizedMaxColumns(1));
        Assert.Equal(10, SwitcherSupport.SanitizedMaxColumns(11));
        Assert.Equal(10, SwitcherSupport.SanitizedMaxColumns(99));
        Assert.Equal(0, SwitcherSupport.SanitizedAppearanceDelay(-1));
        Assert.Equal(500, SwitcherSupport.SanitizedAppearanceDelay(9000));
        Assert.Equal(100, SwitcherSupport.SanitizedAppearanceDelay(100));
    }

    [Fact]
    public void RecentWindowsComeFirst()
    {
        Assert.Equal([7, 3, 5], SwitcherSupport.Promoting([3, 5, 7], 7, 3));
        Assert.Equal([3, 5, 9], SwitcherSupport.Promoting([5, 3, 9], null, 3));
        Assert.Equal([5, 3, 9], SwitcherSupport.Promoting([5, 3, 9], null, null));
        // Order: the history first, then by app recency, then front to back.
        (int Id, int Pid)[] windows = [(1, 10), (2, 20), (3, 30), (4, 20)];
        Assert.Equal([3, 1, 2, 4], SwitcherSupport.Order(windows, [3, 99], [10, 20]));
        Assert.Equal([2, 4, 1, 3], SwitcherSupport.Order(windows, [], [20, 10]));
        Assert.Equal([4, 1, 3], SwitcherSupport.Reconciled([4, 9, 1], [1, 3, 4]));
    }

    [Fact]
    public void MinimizedWindowsStayGoLastOrLeave()
    {
        var entries = new[]
        {
            new SwitcherEntry(1, 1, "a.exe", "A", "one"), new SwitcherEntry(2, 2, "b.exe", "B", "two", IsMinimized: true),
            new SwitcherEntry(3, 3, "c.exe", "C", "three"), new SwitcherEntry(4, 4, "d.exe", "D", "four", IsHiddenApp: true),
        };
        Assert.Equal([1, 2, 3, 4], SwitcherSupport.PlaceMinimized(entries, MinimizedPlacement.Normal, true).Select(e => e.Id));
        Assert.Equal([1, 3, 2, 4], SwitcherSupport.PlaceMinimized(entries, MinimizedPlacement.End, true).Select(e => e.Id));
        Assert.Equal([1, 3], SwitcherSupport.PlaceMinimized(entries, MinimizedPlacement.Hidden, true).Select(e => e.Id));
        Assert.Equal([1, 3, 4], SwitcherSupport.PlaceMinimized(entries, MinimizedPlacement.Hidden, false).Select(e => e.Id));
    }

    [Fact]
    public void AppRulesAndWindowlessApps()
    {
        var rules = new Dictionary<string, AppRule> { ["a.exe"] = AppRule.Hidden, ["b.exe"] = AppRule.WindowsOnly, ["c.exe"] = AppRule.ShowWithoutWindows };
        var entries = new[]
        {
            new SwitcherEntry(1, 1, "a.exe", "A", "x"), new SwitcherEntry(2, 2, "b.exe", "B", "", HasWindow: false),
            new SwitcherEntry(3, 3, "b.exe", "B", "y"), new SwitcherEntry(4, 4, "c.exe", "C", "", HasWindow: false),
        };
        Assert.Equal([3, 4], SwitcherSupport.ApplyAppRules(entries, rules).Select(e => e.Id));
        Assert.True(SwitcherSupport.ShowsWithoutWindow("explorer.exe", WindowlessMode.Explorer, rules));
        Assert.False(SwitcherSupport.ShowsWithoutWindow("other.exe", WindowlessMode.Explorer, rules));
        Assert.True(SwitcherSupport.ShowsWithoutWindow("other.exe", WindowlessMode.All, rules));
        Assert.True(SwitcherSupport.ShowsWithoutWindow("c.exe", WindowlessMode.Off, rules));
        Assert.False(SwitcherSupport.ShowsWithoutWindow("b.exe", WindowlessMode.All, rules));
    }

    [Fact]
    public void SearchIgnoresCaseAccentsAndOrder()
    {
        Assert.True(SwitcherSupport.Matches("chr goo", "Google", "Chrome"));
        Assert.True(SwitcherSupport.Matches("CAFE", "Café menu", "Edge"));
        Assert.True(SwitcherSupport.Matches("", "anything", "app"));
        Assert.False(SwitcherSupport.Matches("zzz", "Google", "Chrome"));
        Assert.False(SwitcherSupport.Matches("chrome zzz", "Google", "Chrome"));
    }

    [Fact]
    public void TheFirstLetterIsACommandWithAnEmptyQuery()
    {
        Assert.Equal(SwitcherLetter.CloseWindow, SwitcherSupport.LetterAction(0x57, false));
        Assert.Equal(SwitcherLetter.QuitApp, SwitcherSupport.LetterAction(0x51, false));
        Assert.Equal(SwitcherLetter.None, SwitcherSupport.LetterAction(0x53, false));
        Assert.Equal(SwitcherLetter.PinSearch, SwitcherSupport.LetterAction(0x53, true));
        Assert.Equal(SwitcherLetter.None, SwitcherSupport.LetterAction(0x41, true));
    }

    [Fact]
    public void ClosingAWindowKeepsTheSelectionInPlace()
    {
        var r1 = SwitcherSupport.CloseState([1, 2, 3], 1, 2);
        Assert.Equal([1, 3], r1.Ids);
        Assert.Equal(1, r1.Selected);
        Assert.False(r1.SessionEnds);
        var r2 = SwitcherSupport.CloseState([1, 2, 3], 2, 1);
        Assert.Equal(1, r2.Selected);
        var r3 = SwitcherSupport.CloseState([1], 0, 1);
        Assert.True(r3.SessionEnds);
        var r4 = SwitcherSupport.CloseState([1, 2, 3], 1, 9);
        Assert.Equal([1, 2, 3], r4.Ids);
        Assert.Equal(1, r4.Selected);
        Assert.False(r4.SessionEnds);
    }

    [Fact]
    public void ReleasingWhileWindowsCloseSkipsThem()
    {
        Assert.Equal(3, SwitcherSupport.CommitTarget([1, 2, 3], 1, new HashSet<int> { 2 }));
        Assert.Equal(1, SwitcherSupport.CommitTarget([1, 2, 3], 2, new HashSet<int> { 2, 3 }));
        Assert.Null(SwitcherSupport.CommitTarget([1, 2, 3], 0, new HashSet<int> { 1, 2, 3 }));
        Assert.Null(SwitcherSupport.CommitTarget([], 0, new HashSet<int>()));
        Assert.Equal(2, SwitcherSupport.CommitTarget([1, 2, 3], 1, new HashSet<int>()));
    }

    [Fact]
    public void TheWheelMovesOneEntryPerNotchAndATouchpadAddsUp()
    {
        var wheel = new SwitcherSupport.WheelNavigator();
        Assert.Equal(1, wheel.Add(120, 0));
        Assert.Equal(-1, wheel.Add(-120, 0.1));
        Assert.Equal(2, wheel.Add(240, 0.2));
        var pad = new SwitcherSupport.WheelNavigator();
        Assert.Equal(0, pad.Add(10, 10));
        Assert.Equal(0, pad.Add(10, 10.05));
        Assert.Equal(1, pad.Add(10, 10.1));
        // A pause forgets what was added.
        Assert.Equal(0, pad.Add(20, 11));
        Assert.Equal(0, pad.Add(9, 11.05));
    }

    [Fact]
    public void SwitcherDefaultsStartOffAndKeepUpstreamValues()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.False(store.GetBool(DefaultsKey.SwitcherEnabled));
        Assert.True(store.GetBool(DefaultsKey.SwitcherTakeOverSystemShortcuts));
        Assert.Equal(100, store.GetInt(DefaultsKey.SwitcherAppearanceDelay, -1));
        Assert.Equal("pointer", store.GetString(DefaultsKey.SwitcherScreenPlacement));
        Assert.Equal(0, store.GetInt(DefaultsKey.SwitcherMaxColumns, -1));
        Assert.NotNull(Shortcut.Parse(store.GetString(DefaultsKey.SwitcherShortcut)));
        Assert.NotNull(Shortcut.Parse(store.GetString(DefaultsKey.SwitcherWindowShortcut)));
        Assert.Equal("control+win", store.GetString(DefaultsKey.WindowGestureModifiers));
    }
}
