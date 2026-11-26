// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the quick-preview, after-capture routing and capture-mode policy contracts in upstream
// Tests/ScreenshotFeatureTests.swift (lines ~830-1330, 1642-1700), Tests/ScreenshotCountdownTests.swift,
// Tests/ScreenshotPreviewHoverTests.swift (the dismissal timing), Tests/ScreenshotAutoCopyTests.swift,
// Tests/ScreenshotAutoShelfTests.swift (the routing policy) and Tests/ScreenshotSelectionRefreshTests.swift
// (the policy parts). Placement numbers are mirrored from upstream's y-up screens to top-left, y-down.
using Notchly.Core.Capture;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class QuickPreviewPlacementTests
{
    private static readonly SizeD Size = new(310, 210);

    [Fact]
    public void TheConfirmationSitsInsetInTheBottomRightCorner() =>
        // Upstream (y-up): (1114, 16). Mirrored for a 900 pt tall display: y = 900 - 16 - 210 = 674.
        Assert.Equal(new RectD(1114, 674, 310, 210),
            ScreenshotPreviewPlacement.Frame(Size, CaptureGeometry.Zero, new PointD(0, 0), new RectD(0, 0, 1440, 900), QuickPreviewPosition.BottomRight));

    [Fact]
    public void EveryConfiguredCornerRespectsTheDisplayOriginAndInset()
    {
        var screen = new RectD(-1920, 50, 1440, 900);
        RectD At(QuickPreviewPosition p) => ScreenshotPreviewPlacement.Frame(Size, CaptureGeometry.Zero, new PointD(0, 0), screen, p);
        Assert.Equal(new RectD(-1904, 66, 310, 210), At(QuickPreviewPosition.TopLeft));
        Assert.Equal(new RectD(-806, 66, 310, 210), At(QuickPreviewPosition.TopRight));
        Assert.Equal(new RectD(-1904, 724, 310, 210), At(QuickPreviewPosition.BottomLeft));
        Assert.Equal(new RectD(-806, 724, 310, 210), At(QuickPreviewPosition.BottomRight));
    }

    [Fact]
    public void ACornerPreviewTallerThanTheDisplayStaysTopAligned()
    {
        var frame = ScreenshotPreviewPlacement.Frame(new SizeD(310, 2000), CaptureGeometry.Zero, new PointD(0, 0),
            new RectD(0, 0, 1440, 900), QuickPreviewPosition.BottomRight);
        Assert.Equal(16, frame.Y);
    }

    private static readonly RectD Visible = new(0, 0, 1920, 1080); // usable: (10, 10, 1900, 1060)

    private static RectD Auto(SizeD size, RectD anchor, PointD pointer) =>
        ScreenshotPreviewPlacement.Frame(size, anchor, pointer, Visible);

    [Fact]
    public void AutomaticPlacementSitsRightOfTheSelectionCenteredOnIt() =>
        Assert.Equal(new RectD(914, 350, 300, 200), Auto(new SizeD(300, 200), new RectD(500, 300, 400, 300), new PointD(0, 0)));

    [Fact]
    public void WhenThereIsNoRoomOnTheRightItGoesLeft() =>
        Assert.Equal(new RectD(1186, 350, 300, 200), Auto(new SizeD(300, 200), new RectD(1500, 300, 400, 300), new PointD(0, 0)));

    [Fact]
    public void WithNoRoomOnEitherSideItFollowsThePointer() =>
        Assert.Equal(new RectD(814, 350, 300, 200), Auto(new SizeD(300, 200), new RectD(0, 300, 1920, 300), new PointD(800, 400)));

    [Fact]
    public void NearTheBottomItMovesAboveTheSelection() =>
        Assert.Equal(new RectD(514, 686, 300, 200), Auto(new SizeD(300, 200), new RectD(100, 900, 400, 200), new PointD(0, 0)));

    [Fact]
    public void NearTheTopItMovesBelowTheSelection()
    {
        // Centred would poke above the usable area, so it goes below the selection.
        var frame = Auto(new SizeD(300, 200), new RectD(100, 0, 400, 100), new PointD(0, 0));
        Assert.Equal(new RectD(514, 114, 300, 200), frame);
    }

    [Fact]
    public void WithNoRoomAboveOrBelowItCentersOnThePointerAndClamps() =>
        Assert.Equal(new RectD(514, 10, 300, 1056), Auto(new SizeD(300, 1056), new RectD(100, 900, 400, 200), new PointD(0, 500)));

    [Fact]
    public void TheQuickPreviewStaysFullyInsideTheVisibleDisplay()
    {
        var visible = new RectD(0, 0, 1470, 956);
        var frame = ScreenshotPreviewPlacement.Frame(new SizeD(286, 210), new RectD(1100, 556, 300, 300), new PointD(1300, 736), visible);
        Assert.True(new RectD(10, 10, 1450, 936).CgContainsRect(frame));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1900, 1050)]
    [InlineData(-300, -300)]
    [InlineData(5000, 5000)]
    public void AutomaticPlacementNeverLeavesTheUsableArea(double ax, double ay)
    {
        var frame = Auto(new SizeD(310, 210), new RectD(ax, ay, 200, 120), new PointD(ax + 50, ay + 40));
        Assert.True(new RectD(10, 10, 1900, 1060).CgContainsRect(frame), frame.ToString());
    }

    // The three-display layout of upstream's visible-frame tests, mirrored to a top-left origin with the
    // primary display's top at y = 0. Taskbar at the bottom of the main display, menu strip on the others.
    private static readonly PlacementScreen[] Three =
    [
        new(new RectD(-1920, -180, 1920, 1080), new RectD(-1920, -155, 1920, 1055)),
        new(new RectD(0, 0, 1440, 900), new RectD(0, 0, 1440, 860)),
        new(new RectD(1440, -424, 1280, 1024), new RectD(1440, -399, 1280, 999)),
    ];

    [Fact]
    public void ACaptureUsesTheVisibleFrameOfItsDisplayInAThreeDisplayLayout() =>
        Assert.Equal(Three[0].VisibleFrame,
            ScreenshotPreviewPlacement.VisibleFrame(new RectD(-900, 300, 500, 400), new PointD(-500, 600), Three, CaptureGeometry.Zero));

    [Fact]
    public void ACaptureCrossingDisplaysUsesTheScreenHoldingMostOfIt() =>
        Assert.Equal(Three[2].VisibleFrame,
            ScreenshotPreviewPlacement.VisibleFrame(new RectD(1300, -100, 500, 500), new PointD(1700, 250), Three, CaptureGeometry.Zero));

    [Fact]
    public void ADisconnectedCaptureDisplayFallsBackToThePointerDisplay() =>
        Assert.Equal(Three[1].VisibleFrame,
            ScreenshotPreviewPlacement.VisibleFrame(new RectD(5000, -5300, 400, 300), new PointD(700, 400), Three, CaptureGeometry.Zero));

    [Fact]
    public void TheDisplayWhoseTopRowHoldsThePointerWins() =>
        // Upstream put the pointer at frame.maxY (y-up top row); top-down it is frame.Y.
        Assert.Equal(Three[2].VisibleFrame,
            ScreenshotPreviewPlacement.VisibleFrame(new RectD(5000, -5300, 400, 300), new PointD(2000, -424), Three, CaptureGeometry.Zero));

    [Fact]
    public void WithNoDisplayUnderTheAnchorOrPointerTheFallbackIsUsed()
    {
        var fallback = new RectD(7, 7, 7, 7);
        Assert.Equal(fallback, ScreenshotPreviewPlacement.VisibleFrame(new RectD(9000, 9000, 10, 10), new PointD(9000, 9000), Three, fallback));
        Assert.Equal(fallback, ScreenshotPreviewPlacement.VisibleFrame(new RectD(9000, 9000, 10, 10), new PointD(9000, 9000), [], fallback));
    }

    [Fact]
    public void AnEvenSplitAcrossStackedDisplaysGoesToTheLowerOneWhenItsTopRowHoldsThePointer()
    {
        var upper = new PlacementScreen(new RectD(0, -900, 1440, 900), new RectD(0, -875, 1440, 875));
        var lower = new PlacementScreen(new RectD(0, 0, 1440, 900), new RectD(0, 25, 1440, 875));
        foreach (var screens in new[] { new[] { upper, lower }, new[] { lower, upper } })
            Assert.Equal(lower.VisibleFrame,
                ScreenshotPreviewPlacement.VisibleFrame(new RectD(100, -100, 400, 200), new PointD(300, 0), screens, CaptureGeometry.Zero));
    }

    [Fact]
    public void AnEvenSplitWithThePointerOnTheUpperDisplayGoesToTheUpperOne()
    {
        var upper = new PlacementScreen(new RectD(0, -900, 1440, 900), new RectD(0, -875, 1440, 875));
        var lower = new PlacementScreen(new RectD(0, 0, 1440, 900), new RectD(0, 25, 1440, 875));
        foreach (var screens in new[] { new[] { upper, lower }, new[] { lower, upper } })
            Assert.Equal(upper.VisibleFrame,
                ScreenshotPreviewPlacement.VisibleFrame(new RectD(100, -100, 400, 200), new PointD(300, -50), screens, CaptureGeometry.Zero));
    }

    [Fact]
    public void PreviewPositionsKeepStableStorageValuesAndRejectUnknownOnes()
    {
        Assert.Equal(new[] { "", "topLeft", "topRight", "bottomLeft", "bottomRight" },
            QuickPreviewPositions.All.Select(p => p.Raw()));
        Assert.Null(QuickPreviewPositions.FromRaw("bogus"));
        Assert.Equal(QuickPreviewPosition.TopRight, QuickPreviewPositions.FromRaw("topRight"));
    }

    [Fact]
    public void TheRegisteredWordAutomaticAndAnUnknownValueBothReadAsAutomatic()
    {
        Assert.Equal(QuickPreviewPosition.Automatic, QuickPreviewPositions.FromRaw("automatic"));
        Assert.Equal(QuickPreviewPosition.Automatic, QuickPreviewPositions.Current("bogus"));
        Assert.Equal(QuickPreviewPosition.Automatic, QuickPreviewPositions.Current(null));
        Assert.Equal(QuickPreviewPosition.BottomLeft, QuickPreviewPositions.Current("bottomLeft"));
    }
}

public class EditorLayoutTests
{
    [Fact]
    public void TheEditorOpensOnAComfortableCanvas() =>
        Assert.Equal(new SizeD(980, 680), ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(1470, 956)));

    [Fact]
    public void TheEditorMinimumNeverExceedsACompactDisplay() =>
        Assert.Equal(new SizeD(700, 500), ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(700, 500)));

    [Fact]
    public void AMidSizedDisplayScalesTheMinimum()
    {
        var size = ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(1000, 700));
        Assert.Equal(760, size.Width);
        Assert.Equal(560, size.Height);
    }

    [Fact]
    public void ASmallScreenshotStillGetsTheFullEditingCanvas()
    {
        var visible = new SizeD(1470, 956);
        Assert.Equal(ScreenshotPreviewPlacement.EditorMinimumContentSize(visible),
            ScreenshotPreviewPlacement.EditorContentSize(new SizeD(180, 100), visible));
    }

    [Fact]
    public void ALargeScreenshotEditorStaysInsideTheVisibleDisplay()
    {
        var size = ScreenshotPreviewPlacement.EditorContentSize(new SizeD(2200, 1400), new SizeD(1470, 956));
        Assert.True(size.Width <= 1470 * 0.90);
        Assert.True(size.Height <= 956 * 0.88);
        Assert.Equal(956 * 0.88, size.Height, 9);
    }

    [Fact]
    public void AMediumScreenshotAddsChromeAroundItsOwnSize()
    {
        var size = ScreenshotPreviewPlacement.EditorContentSize(new SizeD(900, 500), new SizeD(1470, 956));
        Assert.Equal(900 + 96, size.Width);
        Assert.Equal(680, size.Height); // 640 preferred, lifted to the minimum canvas
    }

    private const int RailTools = 15; // upstream's editorTools: every tool except the content-only image layer

    private static bool Fits(ToolRailLayout layout, int toolCount, double height)
    {
        var rows = layout.VisibleTools + (layout.VisibleTools < toolCount ? 2 : 1);
        return ToolRailLayout.ChromeHeight + rows * (layout.RowHeight + layout.Spacing) <= height;
    }

    [Fact]
    public void AToolThatWrapsTheFooterNeverReflowsTheToolRail()
    {
        var smallest = ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(1280, 775)).Height;
        var rails = new double[] { 40, ScreenshotPreviewPlacement.EditorWrappedFooterHeight }
            .Select(footer => ScreenshotPreviewPlacement.ToolRailHeight(smallest - 70 - footer, footer)).ToArray();
        Assert.Equal(rails[0], rails[1]);
    }

    [Fact]
    public void TheSmallestEditorShowsEveryToolAndTheGearInCompactRows()
    {
        var smallest = ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(1280, 775)).Height;
        var height = ScreenshotPreviewPlacement.ToolRailHeight(smallest - 70 - 40, 40);
        var layout = new ToolRailLayout(RailTools, height);
        Assert.Equal(RailTools, layout.VisibleTools);
        Assert.True(layout.RowHeight < 29);
        Assert.True(Fits(layout, RailTools, height));
    }

    [Fact]
    public void AComfortableEditorKeepsRegularToolRows()
    {
        var editor = ScreenshotPreviewPlacement.EditorMinimumContentSize(new SizeD(1470, 956));
        var layout = new ToolRailLayout(RailTools, ScreenshotPreviewPlacement.ToolRailHeight(editor.Height - 70 - 40, 40));
        Assert.Equal(RailTools, layout.VisibleTools);
        Assert.Equal(29, layout.RowHeight);
    }

    [Fact]
    public void AShorterRailMovesItsLastToolsBehindTheOverflowButton()
    {
        var layout = new ToolRailLayout(RailTools, 300);
        Assert.InRange(layout.VisibleTools, 1, RailTools - 1);
    }

    [Fact]
    public void TheToolRailItsOverflowButtonAndTheGearFitEveryHeight()
    {
        for (var height = 63; height <= 800; height++)
            Assert.True(Fits(new ToolRailLayout(RailTools, height), RailTools, height), $"height {height}");
    }

    [Fact]
    public void ATinyRailShowsNoToolsButStillTheOverflowAndTheGear() =>
        Assert.Equal(0, new ToolRailLayout(RailTools, 40).VisibleTools);

    [Fact]
    public void TheEditorOwnsKeyEventsCarryingItsWindowNumber() =>
        Assert.True(ScreenshotPreviewPlacement.EditorOwnsKeyEvent(42, 42, true));

    [Fact]
    public void TheEditorOwnsWindowlessMenuKeyEquivalentsWhileKey() =>
        Assert.True(ScreenshotPreviewPlacement.EditorOwnsKeyEvent(0, 42, true));

    [Fact]
    public void AnInactiveEditorIgnoresWindowlessKeyEvents() =>
        Assert.False(ScreenshotPreviewPlacement.EditorOwnsKeyEvent(0, 42, false));

    [Fact]
    public void TheEditorIgnoresEventsExplicitlyOwnedByAnotherWindow() =>
        Assert.False(ScreenshotPreviewPlacement.EditorOwnsKeyEvent(7, 42, true));

    [Fact]
    public void AnEditorWithoutAWindowOwnsNothing() =>
        Assert.False(ScreenshotPreviewPlacement.EditorOwnsKeyEvent(0, 0, true));

    [Fact]
    public void ACopyAnotherAppPostsAsADragEndsDoesNotCopyAndCloseTheEditor() =>
        Assert.True(ScreenshotPreviewPlacement.EditorIgnoresPostedCopy(4242, 77, 100.3, 100));

    [Fact]
    public void APressedCopyRightAfterDrawingStillCopies() =>
        Assert.False(ScreenshotPreviewPlacement.EditorIgnoresPostedCopy(0, 77, 100.1, 100));

    [Fact]
    public void APostedCopyWellAfterTheLastStrokeStillCopies() =>
        Assert.False(ScreenshotPreviewPlacement.EditorIgnoresPostedCopy(4242, 77, 101, 100));

    [Fact]
    public void APostedCopyBeforeAnyCanvasGestureStillCopies() =>
        Assert.False(ScreenshotPreviewPlacement.EditorIgnoresPostedCopy(4242, 77, 100, double.NegativeInfinity));

    [Fact]
    public void ACopyFromTheAppItselfIsNeverIgnored() =>
        Assert.False(ScreenshotPreviewPlacement.EditorIgnoresPostedCopy(77, 77, 100.1, 100));
}

public class QuickPreviewTimingTests
{
    [Fact]
    public void ConfirmationDurationsSupportShortDefaultPersistentAndShareResults()
    {
        Assert.Contains(1, ScreenshotSupport.ConfirmationPreviewDurations);
        Assert.Contains(ScreenshotSupport.DefaultConfirmationPreviewDuration, ScreenshotSupport.ConfirmationPreviewDurations);
        Assert.Contains(0, ScreenshotSupport.ConfirmationPreviewDurations);
        Assert.Equal(2, ScreenshotSupport.SanitizedConfirmationPreviewDuration(2));
        Assert.Equal(3, ScreenshotSupport.SanitizedConfirmationPreviewDuration(99));
        Assert.Equal(2.0, ScreenshotSupport.ConfirmationPreviewDismissInterval(2));
        Assert.Null(ScreenshotSupport.ConfirmationPreviewDismissInterval(0));
        Assert.Equal(30.0, ScreenshotSupport.SharedPreviewDismissInterval(3));
        Assert.Null(ScreenshotSupport.SharedPreviewDismissInterval(null));
    }

    [Fact]
    public void TheOfferedDurationsAreInTheOrderTheSettingsMenuShowsThem() =>
        Assert.Equal(new[] { 1, 2, 3, 5, 10, 0 }, ScreenshotSupport.ConfirmationPreviewDurations);

    [Fact]
    public void TheRecoveryPreviewKeepsAMuchLongerTimer() =>
        Assert.Equal(12, ScreenshotSupport.RecoveryPreviewDismissInterval);

    [Fact]
    public void ANegativeOrUnknownDurationReadsAsTheDefault()
    {
        Assert.Equal(3, ScreenshotSupport.SanitizedConfirmationPreviewDuration(-1));
        Assert.Equal(3.0, ScreenshotSupport.ConfirmationPreviewDismissInterval(4));
    }

    [Fact]
    public void PresentationKeepsTimedFocusBehaviourWhilePersistentConfirmationsStayDismissibleWithoutFocus()
    {
        var focusedTimed = ScreenshotSupport.ConfirmationPreviewPresentationPolicyFor(3, prefersFocus: true);
        var quietTimed = ScreenshotSupport.ConfirmationPreviewPresentationPolicyFor(3, prefersFocus: false);
        var persistent = ScreenshotSupport.ConfirmationPreviewPresentationPolicyFor(null, prefersFocus: true);
        Assert.True(focusedTimed.TakesFocus);
        Assert.False(focusedTimed.ClosesOnCollapse);
        Assert.False(focusedTimed.ShowsDismissButton);
        Assert.False(quietTimed.TakesFocus);
        Assert.False(quietTimed.ClosesOnCollapse);
        Assert.False(quietTimed.ShowsDismissButton);
        Assert.False(persistent.TakesFocus);
        Assert.True(persistent.ClosesOnCollapse);
        Assert.True(persistent.ShowsDismissButton);
    }

    [Theory]
    [InlineData(ScreenshotDefaultAction.None, false, false, false, true)]
    [InlineData(ScreenshotDefaultAction.Edit, false, false, true, false)]
    [InlineData(ScreenshotDefaultAction.Copy, false, true, true, true)]
    [InlineData(ScreenshotDefaultAction.Copy, false, true, false, false)]
    [InlineData(ScreenshotDefaultAction.Copy, false, false, false, true)]
    [InlineData(ScreenshotDefaultAction.Save, true, false, false, false)]
    [InlineData(ScreenshotDefaultAction.Save, false, false, false, true)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, false, false, true)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, true, true, true)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, true, false, false)]
    [InlineData(ScreenshotDefaultAction.None, true, true, true, true)]
    [InlineData(ScreenshotDefaultAction.Edit, true, true, false, false)]
    public void AutomaticActionsHonorConfirmationPreferencesWhileFailedOrPartialOnesStillExposeRecovery(
        ScreenshotDefaultAction action, bool saved, bool copied, bool confirmationEnabled, bool shown) =>
        Assert.Equal(shown, ScreenshotSupport.ShouldShowQuickPreview(action, saved, copied, confirmationEnabled));

    [Theory]
    [InlineData(ScreenshotDefaultAction.None, true, true, false)]
    [InlineData(ScreenshotDefaultAction.Edit, true, true, false)]
    [InlineData(ScreenshotDefaultAction.Save, true, false, true)]
    [InlineData(ScreenshotDefaultAction.Save, false, true, false)]
    [InlineData(ScreenshotDefaultAction.Copy, false, true, true)]
    [InlineData(ScreenshotDefaultAction.Copy, true, false, false)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, true, true)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, false, false)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, false, true, false)]
    public void AnAutomaticActionSucceedsOnlyWhenEveryPartOfItDid(ScreenshotDefaultAction action, bool saved, bool copied, bool ok) =>
        Assert.Equal(ok, ScreenshotSupport.AutomaticActionSucceeded(action, saved, copied));

    private static ScreenshotSupport.QuickPreviewPresentation Route(ScreenshotDefaultAction action, bool enabled, object? duration,
                                                                    bool saved = false, bool copied = false) =>
        ScreenshotSupport.QuickPreviewPresentationFor(action, saved, copied, enabled, duration);

    private static readonly ScreenshotSupport.QuickPreviewPresentation Recovery =
        ScreenshotSupport.QuickPreviewPresentation.Shown(ScreenshotSupport.RecoveryPreviewDismissInterval);

    [Fact]
    public void ASuccessfulActionConfirmsForTheChosenDurationAndAskingEachTimeKeepsTheLongerTimer()
    {
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(10), Route(ScreenshotDefaultAction.Save, true, 10, saved: true));
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(10), Route(ScreenshotDefaultAction.Copy, true, 10, copied: true));
        Assert.Equal(Recovery, Route(ScreenshotDefaultAction.None, true, 10));
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Hidden, Route(ScreenshotDefaultAction.Edit, true, 10));
    }

    [Fact]
    public void UntilDismissedKeepsASuccessfulConfirmationWithNoTimer() =>
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(null),
            Route(ScreenshotDefaultAction.SaveAndCopy, true, 0, saved: true, copied: true));

    [Fact]
    public void WithConfirmationsOffASuccessShowsNothingAndAFailureStillShowsRecovery()
    {
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Hidden, Route(ScreenshotDefaultAction.Save, false, 3, saved: true));
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Hidden,
            Route(ScreenshotDefaultAction.SaveAndCopy, false, 3, saved: true, copied: true));
        Assert.Equal(Recovery, Route(ScreenshotDefaultAction.Save, false, 3));
        Assert.Equal(Recovery, Route(ScreenshotDefaultAction.SaveAndCopy, false, 3, saved: true));
        Assert.Equal(Recovery, Route(ScreenshotDefaultAction.Copy, false, 3));
    }

    [Fact]
    public void AStoredDurationThatIsNotANumberFallsBackToTheDefaultInsteadOfStayingUntilDismissed() =>
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(ScreenshotSupport.DefaultConfirmationPreviewDuration),
            Route(ScreenshotDefaultAction.Save, true, "soon", saved: true));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void StoredDurationsOfAnyIntegerWidthAreRead(int seconds)
    {
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(seconds), Route(ScreenshotDefaultAction.Save, true, seconds, saved: true));
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(seconds), Route(ScreenshotDefaultAction.Save, true, (long)seconds, saved: true));
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(seconds), Route(ScreenshotDefaultAction.Save, true, (double)seconds, saved: true));
    }

    [Fact]
    public void ADurationThatIsNotOneOfTheOfferedOnesReadsAsTheDefault() =>
        Assert.Equal(ScreenshotSupport.QuickPreviewPresentation.Shown(3),
            Route(ScreenshotDefaultAction.Save, true, 99, saved: true));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void CapturesGoToTheShelfOnlyWhileTheShelfIsInstalledAndOn(bool preference, bool installed, bool on, bool adds) =>
        Assert.Equal(adds, ScreenshotSupport.AddsCapturesToShelf(preference, installed, on));
}

public class DefaultActionTests
{
    [Fact]
    public void AfterCaptureActionsDecodeFromTheirStoredRawValues()
    {
        Assert.Equal(ScreenshotDefaultAction.None, ScreenshotDefaultActions.FromRaw(""));
        Assert.Equal(ScreenshotDefaultAction.SaveAndCopy, ScreenshotDefaultActions.FromRaw("saveAndCopy"));
        Assert.Null(ScreenshotDefaultActions.FromRaw("bogus"));
        Assert.Equal(ScreenshotDefaultAction.None, ScreenshotDefaultActions.Current("bogus"));
        Assert.Equal(ScreenshotDefaultAction.None, ScreenshotDefaultActions.Current(null));
    }

    [Fact]
    public void EveryActionRoundTripsThroughItsRawValue() =>
        Assert.All(ScreenshotDefaultActions.All, a => Assert.Equal(a, ScreenshotDefaultActions.FromRaw(a.Raw())));

    [Fact]
    public void OnlyCopyAndSaveAndCopyPutTheCaptureOnTheClipboard() =>
        Assert.Equal(new[] { ScreenshotDefaultAction.SaveAndCopy, ScreenshotDefaultAction.Copy },
            ScreenshotDefaultActions.All.Where(a => a.CopiesToClipboard()));

    [Fact]
    public void TurningAutomaticCopyOffDropsOnlyTheCopyHalf()
    {
        Assert.Equal(new[]
        {
            ScreenshotDefaultAction.None, ScreenshotDefaultAction.Save, ScreenshotDefaultAction.Save,
            ScreenshotDefaultAction.None, ScreenshotDefaultAction.Edit,
        }, ScreenshotDefaultActions.All.Select(a => a.WithoutCopy()));
        Assert.All(ScreenshotDefaultActions.All, a => Assert.False(a.WithoutCopy().CopiesToClipboard()));
    }

    [Fact]
    public void TheRegisteredDefaultIsAskEachTime() =>
        Assert.Equal(ScreenshotDefaultAction.None,
            ScreenshotDefaultActions.Current(Defaults.Registered.First(p => p.Key == DefaultsKey.ScreenshotDefaultAction).Value as string));

    [Fact]
    public void EditOpensTheEditorWithoutRunningAnyActionOrShelvingTheRawCapture()
    {
        var plan = ScreenshotRoutePlan.For(copyToClipboardPreference: false, ScreenshotDefaultAction.Edit);
        Assert.True(plan.OpensEditor);
        Assert.False(plan.Saves);
        Assert.False(plan.CopiesAsAction);
        Assert.False(plan.OffersShelf);
    }

    [Fact]
    public void TheClipboardCopyHappensFirstAndAlsoReachesTheEditor()
    {
        var plan = ScreenshotRoutePlan.For(copyToClipboardPreference: true, ScreenshotDefaultAction.Edit);
        Assert.True(plan.CopyToClipboardFirst);
        Assert.True(plan.OpensEditor);
    }

    [Theory]
    [InlineData(ScreenshotDefaultAction.None, false, false)]
    [InlineData(ScreenshotDefaultAction.Save, true, false)]
    [InlineData(ScreenshotDefaultAction.SaveAndCopy, true, true)]
    [InlineData(ScreenshotDefaultAction.Copy, false, true)]
    public void EachActionRunsExactlyItsOwnSteps(ScreenshotDefaultAction action, bool saves, bool copies)
    {
        var plan = ScreenshotRoutePlan.For(false, action);
        Assert.Equal(saves, plan.Saves);
        Assert.Equal(copies, plan.CopiesAsAction);
        Assert.False(plan.OpensEditor);
        Assert.True(plan.OffersShelf);
    }
}

public class CaptureModeTests
{
    [Fact]
    public void NumberKeysSelectTheSameCaptureModeShownInTheChooser()
    {
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, ScreenCaptureTools.All.Select(t => t.ShortcutKey()));
        Assert.Equal(ScreenCaptureTool.Screenshot, ScreenCaptureTools.MatchingShortcut("1"));
        Assert.Equal(ScreenCaptureTool.Recording, ScreenCaptureTools.MatchingShortcut("2"));
        Assert.Equal(ScreenCaptureTool.Text, ScreenCaptureTools.MatchingShortcut("3"));
        Assert.Equal(ScreenCaptureTool.Color, ScreenCaptureTools.MatchingShortcut("4"));
        Assert.Equal(ScreenCaptureTool.Scrolling, ScreenCaptureTools.MatchingShortcut("5"));
        Assert.Null(ScreenCaptureTools.MatchingShortcut("6"));
        Assert.Null(ScreenCaptureTools.MatchingShortcut(null));
    }

    [Fact]
    public void TheChooserHidesEveryUninstalledMode()
    {
        HashSet<AppFeature> recordingOnly = [AppFeature.ScreenRecorder];
        Assert.Equal(new[] { ScreenCaptureTool.Recording }, ScreenCaptureTools.Available(recordingOnly.Contains));
    }

    [Fact]
    public void TheChooserKeepsAStableOrderForEveryInstalledMode()
    {
        HashSet<AppFeature> all = [AppFeature.Screenshot, AppFeature.ScreenRecorder, AppFeature.ScreenOCR, AppFeature.ColorPicker];
        Assert.Equal(new[]
        {
            ScreenCaptureTool.Screenshot, ScreenCaptureTool.Recording, ScreenCaptureTool.Text,
            ScreenCaptureTool.Color, ScreenCaptureTool.Scrolling,
        }, ScreenCaptureTools.Available(all.Contains));
    }

    [Fact]
    public void ScrollingCaptureIsInstalledAndRemovedWithTheScreenshotFeature()
    {
        Assert.Equal(new[] { ScreenCaptureTool.Screenshot, ScreenCaptureTool.Scrolling },
            ScreenCaptureTools.Available(f => f == AppFeature.Screenshot));
        Assert.Equal(AppFeature.Screenshot, ScreenCaptureTool.Scrolling.Feature());
    }

    [Fact]
    public void EachToolMapsToItsOwnFeature()
    {
        Assert.Equal(AppFeature.ScreenRecorder, ScreenCaptureTool.Recording.Feature());
        Assert.Equal(AppFeature.ScreenOCR, ScreenCaptureTool.Text.Feature());
        Assert.Equal(AppFeature.ColorPicker, ScreenCaptureTool.Color.Feature());
    }

    [Fact]
    public void OnlyTheRecorderCapturesAudio() =>
        Assert.Equal(new[] { ScreenCaptureTool.Recording }, ScreenCaptureTools.All.Where(t => t.CapturesAudio()));

    [Fact]
    public void CaptureMenuAndShortcutKeysAreDistinctPerTool()
    {
        Assert.Equal(5, ScreenCaptureTools.All.Select(t => t.ShowCaptureMenuOnShortcutKey()).Distinct().Count());
        Assert.Equal(5, ScreenCaptureTools.All.Select(t => t.ShortcutEnabledKey()).Distinct().Count());
        Assert.Equal(DefaultsKey.ScreenshotScrollingShortcutEnabled, ScreenCaptureTool.Scrolling.ShortcutEnabledKey());
        Assert.Equal(DefaultsKey.ScreenshotShortcutEnabled, ScreenCaptureTool.Screenshot.ShortcutEnabledKey());
    }

    [Fact]
    public void CaptureMenuPreferencesDefaultOnForTheToolsWithRegisteredKeys()
    {
        var registered = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        foreach (var tool in ScreenCaptureTools.All.Where(t => t != ScreenCaptureTool.Recording))
            Assert.Equal(true, registered[tool.ShowCaptureMenuOnShortcutKey()]);
    }

    [Fact]
    public void AnExistingInstallKeepsTheCaptureMenuForEveryTool() =>
        Assert.All(ScreenCaptureTools.All, tool => Assert.True(tool.ShowsCaptureMenu(true, _ => null)));

    [Fact]
    public void HidingTheMenuForOneToolDoesNotChangeTheOthers()
    {
        foreach (var hidden in ScreenCaptureTools.All)
        {
            bool? Read(string key) => key == hidden.ShowCaptureMenuOnShortcutKey() ? false : null;
            foreach (var tool in ScreenCaptureTools.All)
            {
                Assert.Equal(tool != hidden, tool.ShowsCaptureMenu(true, Read));
                // Buttons still open the menu even when a shortcut hides it.
                Assert.True(tool.ShowsCaptureMenu(false, Read));
                Assert.Equal(tool == hidden && tool != ScreenCaptureTool.Recording, tool.OpensDuringRecording(true, Read));
                // Buttons open the menu, so they never run over a recording.
                Assert.False(tool.OpensDuringRecording(false, Read));
            }
        }
    }

    [Fact]
    public void TurningTheSettingBackOnBlocksTheShortcutDuringARecordingAgain()
    {
        bool? On(string _) => true;
        Assert.All(ScreenCaptureTools.All, t => Assert.False(t.OpensDuringRecording(true, On)));
    }

    [Fact]
    public void CaptureCountdownsEndWheneverInstalledModesChange()
    {
        Assert.False(ScreenshotSupport.CaptureAvailabilityChanged(
            [ScreenCaptureTool.Screenshot, ScreenCaptureTool.Recording], [ScreenCaptureTool.Screenshot, ScreenCaptureTool.Recording]));
        Assert.True(ScreenshotSupport.CaptureAvailabilityChanged(
            [ScreenCaptureTool.Screenshot, ScreenCaptureTool.Recording], [ScreenCaptureTool.Recording]));
        Assert.True(ScreenshotSupport.CaptureAvailabilityChanged(
            [ScreenCaptureTool.Screenshot], [ScreenCaptureTool.Screenshot, ScreenCaptureTool.Text]));
    }

    [Fact]
    public void AResultIsRoutedOnlyWhileItsSelectedFeatureRemainsInstalled()
    {
        Assert.True(ScreenshotSupport.CaptureRouteIsAuthorized(ScreenCaptureTool.Screenshot, f => f == AppFeature.Screenshot));
        Assert.False(ScreenshotSupport.CaptureRouteIsAuthorized(ScreenCaptureTool.Screenshot, _ => false));
    }
}

public class UnifiedCapturePolicyTests
{
    private static ScreenshotSupport.UnifiedCapturePolicy Policy(ScreenCaptureTool tool, bool freeze, bool pointer, bool hide) =>
        ScreenshotSupport.UnifiedCapturePolicyFor(tool, freeze, pointer, hide);

    [Fact]
    public void ScrollingCaptureSelectsALiveRegionWithoutRepeatedPointerMarks() =>
        Assert.Equal(new ScreenshotSupport.UnifiedCapturePolicy(false, false, false, false, true),
            Policy(ScreenCaptureTool.Scrolling, freeze: true, pointer: true, hide: false));

    [Fact]
    public void SwitchingCaptureModeRebuildsTheFrozenFrameAndWindowPolicy()
    {
        Assert.Equal(new ScreenshotSupport.UnifiedCapturePolicy(false, true, true, true, false),
            Policy(ScreenCaptureTool.Screenshot, false, true, true));
        Assert.Equal(new ScreenshotSupport.UnifiedCapturePolicy(true, false, false, true, true),
            Policy(ScreenCaptureTool.Recording, false, true, true));
        Assert.Equal(new ScreenshotSupport.UnifiedCapturePolicy(true, false, true, true, false),
            Policy(ScreenCaptureTool.Text, false, true, true));
    }

    [Fact]
    public void OnlyFreezePointerAndWindowPolicyDecideWhetherAModeNeedsItsOwnPhotograph()
    {
        var text = Policy(ScreenCaptureTool.Text, false, true, true);
        Assert.True(text.SharesSource(Policy(ScreenCaptureTool.Color, false, true, true)));
        Assert.False(text.SharesSource(Policy(ScreenCaptureTool.Recording, false, true, true)));
        Assert.False(text.SharesSource(Policy(ScreenCaptureTool.Screenshot, false, true, true)));
    }

    private static Dictionary<ScreenCaptureTool, ScreenshotSupport.UnifiedCapturePolicy> Shown() =>
        ScreenCaptureTools.All.ToDictionary(t => t, t => Policy(t, freeze: true, pointer: false, hide: false));

    [Fact]
    public void OnlyRecordingKeepsEditorsAndPinsOutWhileOwnWindowsAreShown()
    {
        var shown = Shown();
        Assert.True(shown[ScreenCaptureTool.Recording].KeepsContentWindowsOut);
        Assert.False(shown[ScreenCaptureTool.Screenshot].KeepsContentWindowsOut);
        Assert.False(shown[ScreenCaptureTool.Text].KeepsContentWindowsOut);
        Assert.False(shown[ScreenCaptureTool.Color].KeepsContentWindowsOut);
    }

    [Theory]
    [InlineData(ScreenCaptureTool.Screenshot)]
    [InlineData(ScreenCaptureTool.Text)]
    [InlineData(ScreenCaptureTool.Color)]
    public void SwitchingBetweenRecordingAndOtherToolsRefreshesThePictureBothWays(ScreenCaptureTool other)
    {
        var shown = Shown();
        Assert.False(shown[ScreenCaptureTool.Recording].SharesSource(shown[other]));
        Assert.False(shown[other].SharesSource(shown[ScreenCaptureTool.Recording]));
    }

    [Theory]
    [InlineData(ScreenCaptureTool.Text)]
    [InlineData(ScreenCaptureTool.Color)]
    public void ToolsThatShowTheSameWindowsKeepThePictureTheyAlreadyHave(ScreenCaptureTool other) =>
        Assert.True(Shown()[ScreenCaptureTool.Screenshot].SharesSource(Shown()[other]));

    [Fact]
    public void HidingOwnWindowsKeepsEditorsAndPinsOutOfEveryTool() =>
        Assert.All(ScreenCaptureTools.All, t => Assert.True(Policy(t, true, false, true).KeepsContentWindowsOut));

    [Theory]
    [InlineData(ScreenCaptureTool.Screenshot, false)]
    [InlineData(ScreenCaptureTool.Recording, true)]
    [InlineData(ScreenCaptureTool.Text, false)]
    [InlineData(ScreenCaptureTool.Color, false)]
    [InlineData(ScreenCaptureTool.Scrolling, true)]
    public void OnlyRecordingAndScrollingSelectARegionByGeometry(ScreenCaptureTool tool, bool geometry) =>
        Assert.Equal(geometry, Policy(tool, true, false, false).UsesGeometry);

    [Theory]
    [InlineData(true, true, false, 0.22)]
    [InlineData(false, false, false, 0.18)]
    public void TheSelectionDimDependsOnFreezing(bool frozen, bool _, bool dragging, double alpha) =>
        Assert.Equal(alpha, ScreenshotSupport.SelectionDimAlpha(false, frozen, dragging));

    [Fact]
    public void TheIslandControlsOnlyDimWhileDragging()
    {
        Assert.Equal(0.18, ScreenshotSupport.SelectionDimAlpha(true, true, true));
        Assert.Equal(0, ScreenshotSupport.SelectionDimAlpha(true, true, false));
    }

    [Fact]
    public void TheChooserDisappearsForTheWholeDragAndWhileCapturePends()
    {
        Assert.True(ScreenshotSupport.CaptureGuideIsVisible(true, false, false));
        Assert.False(ScreenshotSupport.CaptureGuideIsVisible(true, true, false));
        Assert.False(ScreenshotSupport.CaptureGuideIsVisible(true, false, true));
        Assert.False(ScreenshotSupport.CaptureGuideIsVisible(false, false, false));
    }

    [Fact]
    public void StandaloneScreenshotSelectionOffersTheFullScreenAction() =>
        Assert.True(ScreenshotSupport.FullScreenCaptureControlIsAvailable(null, true, false, false));

    [Theory]
    [InlineData(ScreenCaptureTool.Screenshot, true)]
    [InlineData(ScreenCaptureTool.Recording, false)]
    [InlineData(ScreenCaptureTool.Text, false)]
    [InlineData(ScreenCaptureTool.Color, false)]
    public void TheUnifiedChooserOffersFullScreenOnlyForScreenshots(ScreenCaptureTool tool, bool offered) =>
        Assert.Equal(offered, ScreenshotSupport.FullScreenCaptureControlIsAvailable(tool, false, false, false));

    [Fact]
    public void RegionOnlyAndScrollingModesDoNotOfferAConflictingFullScreenAction()
    {
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsAvailable(ScreenCaptureTool.Screenshot, false, true, false));
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsAvailable(ScreenCaptureTool.Screenshot, false, false, true));
    }

    [Fact]
    public void TheFullScreenActionStaysOnThePointerDisplayAndDisappearsOnceSelectionOrCaptureStarts()
    {
        Assert.True(ScreenshotSupport.FullScreenCaptureControlIsVisible(true, true, false, false));
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsVisible(true, true, true, false));
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsVisible(true, true, false, true));
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsVisible(true, false, false, false));
        Assert.False(ScreenshotSupport.FullScreenCaptureControlIsVisible(false, true, false, false));
    }

    [Fact]
    public void TheFullScreenActionSitsBelowScreenChromeAndAnyActiveIslandCaptureControls()
    {
        Assert.Equal(44, ScreenshotSupport.FullScreenCaptureControlTopInset(32, null));
        Assert.Equal(180, ScreenshotSupport.FullScreenCaptureControlTopInset(32, 168));
    }

    [Theory]
    [InlineData(double.NaN, null, 12)]
    [InlineData(-5.0, -9.0, 12)]
    [InlineData(0.0, double.PositiveInfinity, 12)]
    public void NonsenseChromeHeightsFallBackToTheBaseInset(double chrome, double? controls, double inset) =>
        Assert.Equal(inset, ScreenshotSupport.FullScreenCaptureControlTopInset(chrome, controls));

    [Fact]
    public void ALiveSelectionAnswersThePointerAndAFinishedOneIgnoresTheTailOfAGesture()
    {
        Assert.True(ScreenshotSupport.SelectionAcceptsPointerInput(false, false));
        Assert.False(ScreenshotSupport.SelectionAcceptsPointerInput(true, false));
        Assert.False(ScreenshotSupport.SelectionAcceptsPointerInput(false, true));
        Assert.False(ScreenshotSupport.SelectionAcceptsPointerInput(true, true));
    }

    [Fact]
    public void TheCountdownRingDrainsWithTime()
    {
        Assert.Equal(1, ScreenshotSupport.CountdownRingProgress(0));
        Assert.Equal(0.5, ScreenshotSupport.CountdownRingProgress(0.46), 9);
        Assert.Equal(0, ScreenshotSupport.CountdownRingProgress(0.92));
        Assert.Equal(0, ScreenshotSupport.CountdownRingProgress(5));
        Assert.Equal(1, ScreenshotSupport.CountdownRingProgress(-1));
    }

    [Theory]
    [InlineData(double.NaN, 0.92)]
    [InlineData(0.1, 0.0)]
    [InlineData(0.1, -1.0)]
    [InlineData(double.PositiveInfinity, 0.92)]
    public void ABrokenRingClockShowsAnEmptyRing(double elapsed, double duration) =>
        Assert.Equal(0, ScreenshotSupport.CountdownRingProgress(elapsed, duration));

    [Fact]
    public void TheScrollingControlsMoveOnlyWhenTheyWouldInterceptTheAutomaticWheel()
    {
        var screen = new RectD(-800, 40, 800, 600);
        var size = new SizeD(320, 70);
        var usual = ScreenshotSupport.ScrollingControlsFrame(size, screen, null);
        Assert.Equal(new RectD(-560, 64, 320, 70), usual);
        var under = new PointD(usual.MidX, usual.MidY());
        var moved = ScreenshotSupport.ScrollingControlsFrame(size, screen, under);
        Assert.True(screen.CgContainsRect(moved));
        Assert.False(moved.CgContainsPoint(under));
        Assert.Equal(new RectD(-560, 546, 320, 70), moved);
        Assert.Equal(usual, ScreenshotSupport.ScrollingControlsFrame(size, screen, new PointD(0, 0)));
    }

    [Fact]
    public void RecentCaptureLimitsAreUpstreams()
    {
        Assert.Equal(12, ScreenshotSupport.RecentCaptureLimit);
        Assert.Equal(256L * 1024 * 1024, ScreenshotSupport.RecentCaptureMaximumBytes);
    }
}

public class CaptureCountdownTests
{
    [Fact]
    public void ACountdownShowsEachSecondThenRunsTheActionOnce()
    {
        var ticks = new List<int>();
        var runs = 0;
        var countdown = new CaptureCountdown(ticks.Add);
        Assert.True(countdown.Start(3, () => runs++));
        Assert.Equal(new[] { 3 }, ticks);
        Assert.True(countdown.IsActive);
        countdown.Tick();
        countdown.Tick();
        Assert.Equal(new[] { 3, 2, 1 }, ticks);
        Assert.Equal(0, runs);
        countdown.Tick();
        Assert.Equal(1, runs);
        Assert.False(countdown.IsActive);
        countdown.Tick();
        Assert.Equal(1, runs);
        Assert.Equal(new[] { 3, 2, 1 }, ticks);
    }

    [Fact]
    public void NoDelayRunsTheActionAtOnce()
    {
        var runs = 0;
        var countdown = new CaptureCountdown(_ => Assert.Fail("no countdown expected"));
        Assert.False(countdown.Start(0, () => runs++));
        Assert.Equal(1, runs);
        Assert.False(countdown.IsActive);
    }

    [Fact]
    public void PressingAgainWhileCountingCancelsInsteadOfStartingAnother()
    {
        var ticks = new List<int>();
        var runs = 0;
        var countdown = new CaptureCountdown(ticks.Add);
        Assert.True(countdown.StartOrCancel(5, () => runs++));
        Assert.False(countdown.StartOrCancel(5, () => runs++));
        Assert.False(countdown.IsActive);
        for (var i = 0; i < 6; i++) countdown.Tick();
        Assert.Equal(0, runs);
        Assert.Equal(new[] { 5 }, ticks);
    }

    [Fact]
    public void FullScreenAndScrollingShareOneCountdownAndCancelEachOther()
    {
        // Upstream: captureFullScreen then captureScrolling cancels the first; the second completes alone.
        var ticks = new List<int>();
        var ran = new List<string>();
        var countdown = new CaptureCountdown(ticks.Add);
        countdown.StartOrCancel(3, () => ran.Add("full"));
        countdown.StartOrCancel(3, () => ran.Add("scrolling")); // cancels
        for (var i = 0; i < 4; i++) countdown.Tick();
        Assert.Empty(ran);
        countdown.StartOrCancel(3, () => ran.Add("scrolling"));
        for (var i = 0; i < 3; i++) countdown.Tick();
        Assert.Equal(new[] { "scrolling" }, ran);
        Assert.Equal(new[] { 3, 3, 2, 1 }, ticks);
        countdown.StartOrCancel(3, () => ran.Add("full"));
        for (var i = 0; i < 3; i++) countdown.Tick();
        Assert.Equal(new[] { "scrolling", "full" }, ran);
    }

    [Fact]
    public void CancelReportsWhetherACountdownWasRunning()
    {
        var countdown = new CaptureCountdown(_ => { });
        Assert.False(countdown.Cancel());
        countdown.Start(3, () => { });
        Assert.True(countdown.Cancel());
        Assert.Equal(0, countdown.Remaining);
    }

    [Fact]
    public void TheDelayComesFromTheSanitizedPreference()
    {
        var delays = new[] { 0, 3, 5, 10, 7, -1 }.Select(ScreenshotSupport.SanitizedDelay);
        Assert.Equal(new[] { 0, 3, 5, 10, 0, 0 }, delays);
    }
}

public class QuickPreviewDismissalTests
{
    [Theory]
    [InlineData(3.0)]
    [InlineData(12.0)]
    public void EnteringThePreviewCancelsDismissalAndLeavingRestartsIt(double duration)
    {
        var preview = new QuickPreviewDismissal(duration);
        preview.Schedule(0);
        preview.HoverChanged(true, 1);
        Assert.False(preview.Advance(duration + 1));
        Assert.Null(preview.Deadline);

        // Leaving keeps the configured delay.
        preview.HoverChanged(false, 10);
        Assert.False(preview.Advance(10 + duration - 0.5));
        // Returning before the deadline cancels it again.
        preview.HoverChanged(true, 10 + duration - 0.4);
        Assert.False(preview.Advance(10 + duration * 3));
        preview.HoverChanged(false, 50);
        Assert.True(preview.Advance(50 + duration));
        Assert.True(preview.Closed);
    }

    [Fact]
    public void AnUnusedPreviewDismissesAfterItsDuration()
    {
        var preview = new QuickPreviewDismissal(3);
        preview.Schedule(100);
        Assert.False(preview.Advance(102.9));
        Assert.True(preview.Advance(103));
        Assert.False(preview.Advance(200));
    }

    [Fact]
    public void ADismissalIsReportedOnlyOnce()
    {
        var preview = new QuickPreviewDismissal(1);
        preview.Schedule(0);
        Assert.True(preview.Advance(2));
        Assert.False(preview.Advance(3));
    }

    [Fact]
    public void ARepeatedScheduleRestartsTheDelay()
    {
        var preview = new QuickPreviewDismissal(3);
        preview.Schedule(0);
        preview.Schedule(2);
        Assert.False(preview.Advance(4));
        Assert.True(preview.Advance(5));
    }

    [Fact]
    public void AnOpenShareSheetKeepsThePreviewFromDismissing()
    {
        var preview = new QuickPreviewDismissal(3) { SystemSharing = true };
        preview.HoverChanged(true, 0);
        preview.HoverChanged(false, 1);
        Assert.Null(preview.Deadline);
        Assert.False(preview.Advance(100));
        // A cancelled share sheet resumes the dismissal delay.
        preview.SystemSharing = false;
        preview.Schedule(100);
        Assert.True(preview.Advance(103));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AShareInProgressOrBeingDeletedHoldsThePreviewOpen(bool sharing, bool deleting)
    {
        var preview = new QuickPreviewDismissal(3) { Sharing = sharing, DeletingShare = deleting };
        preview.Schedule(0);
        Assert.Null(preview.Deadline);
        Assert.False(preview.Advance(60));
    }

    [Fact]
    public void APersistentConfirmationNeverSchedulesAutomaticDismissal()
    {
        var preview = new QuickPreviewDismissal(null);
        preview.Schedule(0);
        Assert.Null(preview.Deadline);
        Assert.False(preview.Advance(60));
        preview.HoverChanged(false, 61);
        Assert.Null(preview.Deadline);
    }

    [Fact]
    public void AClosedPreviewNeverSchedulesAgain()
    {
        var preview = new QuickPreviewDismissal(3);
        preview.Close();
        preview.Schedule(0);
        Assert.Null(preview.Deadline);
        Assert.False(preview.Advance(10));
    }

    [Fact]
    public void ASharedResultKeepsItsLongerTimer()
    {
        var preview = new QuickPreviewDismissal(3);
        preview.Duration = ScreenshotSupport.SharedPreviewDismissInterval(preview.Duration);
        preview.Schedule(0);
        Assert.False(preview.Advance(29));
        Assert.True(preview.Advance(30));
    }
}
