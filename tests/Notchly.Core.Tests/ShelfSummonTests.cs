// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the constants and geometry asserted in upstream ShelfFeatureTests (dock, edge, card), plus tests of the shake rule that upstream
// leaves untested (ShelfService.swift handleDrag).
using Notchly.Core.Shelf;
using Notchly.Core.Windowing;

namespace Notchly.Core.Tests;

public class ShakeDetectorTests
{
    /// <summary>Samples at 10 ms spacing from a list of x positions.</summary>
    private static (bool Fired, double At) Run(ShakeDetector detector, IEnumerable<double> xs, double start = 10)
    {
        var t = start;
        var fired = false;
        foreach (var x in xs)
        {
            fired |= detector.Add(t, x);
            if (fired) return (true, t);
            t += 0.02;
        }
        return (false, t);
    }

    private static IEnumerable<double> Zigzag(double from, double step, int legs, int stepsPerLeg)
    {
        var x = from;
        var sign = 1;
        for (var leg = 0; leg < legs; leg++)
        {
            for (var i = 0; i < stepsPerLeg; i++) { x += sign * step; yield return x; }
            sign = -sign;
        }
    }

    [Fact]
    public void AFastBackAndForthIsAShake()
    {
        var (fired, _) = Run(new ShakeDetector(), Zigzag(500, 20, legs: 5, stepsPerLeg: 3));
        Assert.True(fired);
    }

    [Fact]
    public void ADragInOneDirectionIsNot() =>
        Assert.False(Run(new ShakeDetector(), Enumerable.Range(0, 20).Select(i => 100.0 + i * 30)).Fired);

    [Fact]
    public void StepsInsideTheDeadZoneNeverCountAsADirection() =>
        Assert.False(Run(new ShakeDetector(), Zigzag(500, 6, legs: 8, stepsPerLeg: 3)).Fired);

    [Fact]
    public void ShortWigglesDoNotTravelFarEnough() =>
        Assert.False(Run(new ShakeDetector(), Zigzag(500, 8, legs: 6, stepsPerLeg: 1)).Fired);

    [Fact]
    public void SlowShakingFallsOutOfTheHalfSecondWindow()
    {
        var detector = new ShakeDetector();
        var t = 0.0;
        var fired = false;
        foreach (var x in Zigzag(500, 40, legs: 6, stepsPerLeg: 1)) { fired |= detector.Add(t, x); t += 0.3; }
        Assert.False(fired);
    }

    [Fact]
    public void FewerThanFiveSamplesNeverFire()
    {
        var detector = new ShakeDetector();
        Assert.False(detector.Add(0, 0));
        Assert.False(detector.Add(0.01, 300));
        Assert.False(detector.Add(0.02, 0));
        Assert.False(detector.Add(0.03, 300));
    }

    [Fact]
    public void ASecondShakeWithinASecondOfASummonIsIgnored()
    {
        var detector = new ShakeDetector();
        var (fired, at) = Run(detector, Zigzag(500, 20, 5, 3));
        Assert.True(fired);
        detector.Summoned(at);
        Assert.False(Run(detector, Zigzag(500, 20, 5, 3), start: at + 0.1).Fired);
        Assert.True(Run(detector, Zigzag(500, 20, 5, 3), start: at + 1.5).Fired);
    }

    [Fact]
    public void ANotSummonedShakeStillCounts()
    {
        // The caller may decline (not a content drag); the cooldown starts only when it summons.
        var detector = new ShakeDetector();
        Assert.True(Run(detector, Zigzag(500, 20, 5, 3)).Fired);
        detector.Reset();
        Assert.True(Run(detector, Zigzag(500, 20, 5, 3), start: 11).Fired);
    }

    [Fact]
    public void AResetForgetsTheSamples()
    {
        var detector = new ShakeDetector();
        foreach (var x in Zigzag(500, 20, 2, 3)) detector.Add(1, x);
        detector.Reset();
        Assert.False(detector.Add(1.1, 0));
    }
}

public class ShelfSummonTests
{
    private static readonly WindowRect Screen = new(0, 0, 1920, 1080);
    private static readonly WindowRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void TheConstantsAreUpstreams()
    {
        Assert.Equal(304, ShelfSummonSupport.CardWidth);
        Assert.Equal(188, ShelfSummonSupport.TileAreaHeight);
        Assert.Equal(5, ShelfSummonSupport.AutoHideSeconds);
        Assert.Equal(0.15, ShelfSummonSupport.DockDwell);
        Assert.Equal(16, ShelfSummonSupport.DockTriggerMargin);
        Assert.Equal(32, ShelfSummonSupport.DockRetreatMargin);
        Assert.Equal(200, ShelfSummonSupport.EdgeTriggerDistance);
        Assert.Equal(330, ShelfSummonSupport.EdgeRetreatDistance);
        Assert.Equal(0.15, ShelfSummonSupport.EdgeDwell);
        Assert.Equal(78, ShelfLayout.TileWidth);
    }

    [Fact]
    public void TheTileLayoutMatchesUpstream()
    {
        Assert.Equal(3, ShelfLayout.ColumnCount(276));
        Assert.Equal(1, ShelfLayout.ColumnCount(40));
        Assert.Equal(new TileFrame(4, 4, 78, 88), ShelfLayout.GridTileFrame(0, 3));
        Assert.Equal(new TileFrame(180, 4, 78, 88), ShelfLayout.GridTileFrame(2, 3));
        Assert.Equal(new TileFrame(4, 102, 78, 88), ShelfLayout.GridTileFrame(3, 3));
        Assert.Equal(new TileFrame(4, 200, 78, 88), ShelfLayout.GridTileFrame(6, 3));
    }

    [Fact]
    public void TheCardSitsCentredBelowThePointer()
    {
        var (x, y) = ShelfSummonSupport.CardOrigin(960, 500, 304, 250, Work);
        Assert.Equal(960 - 152, x);
        Assert.Equal(516, y);
    }

    [Fact]
    public void TheCardStaysInsideTheWorkArea()
    {
        var (left, _) = ShelfSummonSupport.CardOrigin(10, 500, 304, 250, Work);
        Assert.Equal(8, left);
        var (right, _) = ShelfSummonSupport.CardOrigin(1915, 500, 304, 250, Work);
        Assert.Equal(1920 - 304 - 8, right);
        var (_, bottom) = ShelfSummonSupport.CardOrigin(960, 1030, 304, 250, Work);
        Assert.Equal(1040 - 250 - 8, bottom);
        var (_, top) = ShelfSummonSupport.CardOrigin(960, -100, 304, 250, Work);
        Assert.Equal(8, top);
    }

    [Fact]
    public void OnlyAnEmptyUnheldShelfHidesItself()
    {
        Assert.True(ShelfSummonSupport.ShouldAutoHide(true, false, false, false, false, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(false, false, false, false, false, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(true, true, false, false, false, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(true, false, true, false, false, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(true, false, false, true, false, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(true, false, false, false, true, false));
        Assert.False(ShelfSummonSupport.ShouldAutoHide(true, false, false, false, false, true));
    }

    [Fact]
    public void ATileDragStartsPastFourPixels()
    {
        Assert.False(ShelfSummonSupport.StartsDrag(4, -4));
        Assert.True(ShelfSummonSupport.StartsDrag(5, 0));
        Assert.True(ShelfSummonSupport.StartsDrag(0, -5));
    }

    [Fact]
    public void AWindowMoveIsNotAContentDrag()
    {
        Assert.True(ShelfSummonSupport.IsContentDrag(false, false, 8));
        Assert.False(ShelfSummonSupport.IsContentDrag(true, false, 300));
        Assert.False(ShelfSummonSupport.IsContentDrag(false, true, 300));
        Assert.False(ShelfSummonSupport.IsContentDrag(false, false, 7.9));
    }

    [Fact]
    public void ExclusionsBlockOnlyAutomaticSummoning()
    {
        Assert.False(ShelfSummonSupport.AutoOpenAllowed("Game.EXE", ["game.exe"]));
        Assert.True(ShelfSummonSupport.AutoOpenAllowed("notepad.exe", ["game.exe"]));
        Assert.True(ShelfSummonSupport.AutoOpenAllowed(null, ["game.exe"]));
    }

    [Fact]
    public void TheHotkeyAddsTheSelectionOnlyWhenThereIsSomethingNew()
    {
        string[] selected = [@"C:\a.txt", @"C:\b.txt"];
        Assert.Equal(ShelfShortcutAction.AddSelection, ShelfSummonSupport.ShortcutAction(true, true, selected, [@"C:\a.txt"]));
        Assert.Equal(ShelfShortcutAction.Toggle, ShelfSummonSupport.ShortcutAction(true, true, selected, [@"c:\A.TXT", @"C:\b.txt"]));
        Assert.Equal(ShelfShortcutAction.Toggle, ShelfSummonSupport.ShortcutAction(true, true, [], []));
        Assert.Equal(ShelfShortcutAction.Toggle, ShelfSummonSupport.ShortcutAction(false, true, selected, []));
        Assert.Equal(ShelfShortcutAction.Toggle, ShelfSummonSupport.ShortcutAction(true, false, selected, []));
        Assert.Equal([@"C:\b.txt"], ShelfSummonSupport.NewSelection(selected, [@"c:\a.txt"]));
    }

    [Fact]
    public void TheDockHasAPlacementAndTheIslandTakesTheTopCentre()
    {
        Assert.Equal(ShelfDockPlacement.TopCenter, ShelfSummonSupport.ParsePlacement("topCenter"));
        Assert.Equal(ShelfDockPlacement.Tray, ShelfSummonSupport.ParsePlacement("menuBar"));
        Assert.Equal(ShelfDockPlacement.Tray, ShelfSummonSupport.ParsePlacement(null));
        Assert.Equal(ShelfDockPlacement.Tray, ShelfSummonSupport.EffectivePlacement(ShelfDockPlacement.TopCenter, islandEnabled: true));
        Assert.Equal(ShelfDockPlacement.TopCenter, ShelfSummonSupport.EffectivePlacement(ShelfDockPlacement.TopCenter, islandEnabled: false));
    }

    [Fact]
    public void TheDockFramesSitWhereTheyShould()
    {
        Assert.Equal(new WindowRect(1920 - 12 - 180, 1040 - 12 - 40, 180, 40), ShelfSummonSupport.DockFrame(ShelfDockPlacement.Tray, Work));
        Assert.Equal(new WindowRect(870, 8, 180, 40), ShelfSummonSupport.DockFrame(ShelfDockPlacement.TopCenter, Work));
    }

    [Fact]
    public void TheDockZoneGrowsByItsMargin()
    {
        var frame = new WindowRect(100, 100, 180, 40);
        Assert.True(ShelfSummonSupport.InZone(frame, 84, 100, ShelfSummonSupport.DockTriggerMargin));
        Assert.False(ShelfSummonSupport.InZone(frame, 83, 100, ShelfSummonSupport.DockTriggerMargin));
        Assert.True(ShelfSummonSupport.InZone(frame, 83, 100, ShelfSummonSupport.DockRetreatMargin));
        Assert.False(ShelfSummonSupport.InZone(frame, 150, 173, ShelfSummonSupport.DockRetreatMargin));
    }

    [Fact]
    public void TheEdgePeekWatchesOnlyTheSideEdges()
    {
        Assert.Equal(ScreenSide.Left, ShelfSummonSupport.EdgeSide(150, 500, Screen, Work, []));
        Assert.Equal(ScreenSide.Right, ShelfSummonSupport.EdgeSide(1800, 500, Screen, Work, []));
        Assert.Equal(ScreenSide.None, ShelfSummonSupport.EdgeSide(960, 500, Screen, Work, []));
        Assert.Equal(ScreenSide.None, ShelfSummonSupport.EdgeSide(1000, 5, Screen with { Height = 10 }, Work, []));
        Assert.Equal(ScreenSide.None, ShelfSummonSupport.EdgeSide(150, 1200, Screen, Work, []));
    }

    [Fact]
    public void TheSeamBetweenScreensNeverTriggers()
    {
        var right = new WindowRect(1920, 0, 1920, 1080);
        Assert.Equal(ScreenSide.None, ShelfSummonSupport.EdgeSide(1800, 500, Screen, Work, [right]));
        Assert.Equal(ScreenSide.Left, ShelfSummonSupport.EdgeSide(100, 500, Screen, Work, [right]));
        // A screen beside it only at other heights does not block the edge here.
        var low = new WindowRect(1920, 2000, 1920, 1080);
        Assert.Equal(ScreenSide.Right, ShelfSummonSupport.EdgeSide(1800, 500, Screen, Work, [low]));
    }

    [Fact]
    public void ATaskbarAlongAnEdgeStopsThePeekThere()
    {
        var workWithLeftBar = new WindowRect(60, 0, 1860, 1080);
        Assert.Equal(ScreenSide.None, ShelfSummonSupport.EdgeSide(100, 500, Screen, workWithLeftBar, []));
        Assert.Equal(ScreenSide.Right, ShelfSummonSupport.EdgeSide(1800, 500, Screen, workWithLeftBar, []));
    }

    [Fact]
    public void ThePeekShowsAThirdAndASettledCardSitsInsideTheEdge()
    {
        var (lx, ly) = ShelfSummonSupport.PeekOrigin(ScreenSide.Left, Work, 304, 250);
        var (rx, ry) = ShelfSummonSupport.PeekOrigin(ScreenSide.Right, Work, 304, 250);
        Assert.Equal(-304 * 2 / 3.0, lx, 6);
        Assert.Equal(1920 - 304 / 3.0, rx, 6);
        Assert.Equal(395, ly);
        Assert.Equal(395, ry);
        var (sl, _) = ShelfSummonSupport.SettledOrigin(ScreenSide.Left, Work, 304, 250);
        var (sr, _) = ShelfSummonSupport.SettledOrigin(ScreenSide.Right, Work, 304, 250);
        Assert.Equal(8, sl);
        Assert.Equal(1920 - 304 - 8, sr);
    }

    [Fact]
    public void ADwellNeedsTheFullTimeInsideAndLeavingStartsOver()
    {
        var dwell = new ShelfSummonSupport.DwellTracker(0.15);
        Assert.False(dwell.Update(true, 10));
        Assert.False(dwell.Update(true, 10.149));
        Assert.True(dwell.Update(true, 10.15));
        Assert.False(dwell.Update(false, 10.2));
        Assert.False(dwell.Update(true, 10.3));
        Assert.True(dwell.Update(true, 10.5));
        dwell.Reset();
        Assert.False(dwell.Update(true, 11));
    }
}
