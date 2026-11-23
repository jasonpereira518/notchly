// SPDX-License-Identifier: GPL-3.0-or-later
// Contracts from upstream Tests/NotchCapsuleTests.swift and NotchActivityTests.swift, plus the
// numbers the port plan promises for the OMEN 16 (1707x960 DIP).
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class NotchGeometryTests
{
    private static readonly RectD Omen = new(0, 0, 1707, 960);

    private static NotchGeometry Capsule(double bar) => new(Omen, barHeight: bar, silhouette: NotchSilhouette.Capsule);

    [Fact]
    public void SimulatedNotchIs135By24AndCentredUnderTheWebcam()
    {
        var geometry = new NotchGeometry(Omen);
        Assert.Equal(135, geometry.CameraWidth, 6);
        Assert.Equal(24, geometry.CameraHeight);
        Assert.False(geometry.Floats);
        var frame = geometry.Frame(geometry.RestingSize(showsContent: false));
        Assert.Equal(786, frame.X, 6);
        Assert.Equal(0, frame.Y);
    }

    [Fact]
    public void CameraOffsetMovesTheIsland()
    {
        var geometry = new NotchGeometry(Omen) { CameraOffset = 20 };
        Assert.Equal(806, geometry.Frame(new SizeD(135, 24)).X, 6);
    }

    [Fact]
    public void CapsuleLevelNoticeIsOneRowAsWideAsItsContent()
    {
        // Upstream CapsuleLayout.surface: the row plus an end padding and the capsule's side at each end.
        var geometry = new NotchGeometry(Omen, silhouette: NotchSilhouette.Capsule) { CompactSideRoom = 500 };
        var content = NotchGeometry.CapsuleLevelContent(30);
        Assert.Equal(16 + 6 + 56 + 6 + 30, content, 6);
        var surface = geometry.CapsuleSurface(content, NotchGeometry.CapsuleMaximumNotice);
        var expected = Math.Ceiling(content + 8 * 2 + geometry.CapsuleSide * 2);
        Assert.Equal(Math.Max(expected, geometry.RestingSize(showsContent: false).Width), surface.Width, 6);
        Assert.Equal(geometry.StripHeight, surface.Height, 6);
        Assert.Equal(geometry.CapsuleSide + 8, geometry.CapsuleRowLeading, 6);
    }

    [Fact]
    public void CapsuleSurfaceStaysWithinItsLimits()
    {
        var geometry = new NotchGeometry(Omen, silhouette: NotchSilhouette.Capsule) { CompactSideRoom = 500 };
        var resting = geometry.RestingSize(showsContent: false).Width;
        Assert.Equal(resting, geometry.CapsuleSurface(0, NotchGeometry.CapsuleMaximumNotice).Width, 6);
        Assert.Equal(Math.Floor(NotchGeometry.CapsuleMaximumNotice + geometry.CapsuleSide * 2),
                     geometry.CapsuleSurface(10_000, NotchGeometry.CapsuleMaximumNotice).Width, 6);
        var cramped = new NotchGeometry(Omen, silhouette: NotchSilhouette.Capsule) { CompactSideRoom = 10 };
        Assert.True(cramped.CapsuleSurface(10_000, NotchGeometry.CapsuleMaximumNotice).Width <= Math.Max(resting, cramped.CapsuleAvailableWidth));
    }

    [Fact]
    public void CapsuleMusicStripPutsCoverAndBarsAtItsEnds()
    {
        // Upstream CapsuleLayout: a round cover concentric with the capsule's end, bars 8 points
        // from the other end, both measured past the capsule's side within its surface.
        var geometry = new NotchGeometry(Omen, silhouette: NotchSilhouette.Capsule) { CompactSideRoom = 500 };
        var side = NotchLayout.ShoulderFor(geometry.StripHeight);
        Assert.Equal(geometry.StripBodyHeight - 6, geometry.MusicArtworkSide, 6);
        Assert.Equal(geometry.MusicArtworkSide / 2, geometry.MusicArtworkRadius, 6);
        Assert.Equal(side + 3, geometry.MusicArtworkLeading, 6);
        Assert.Equal(side + 8, geometry.MusicBarsTrailing, 6);
        Assert.True(geometry.MusicArtworkLeading + geometry.MusicArtworkSide <= geometry.MusicWingWidth);
    }

    [Fact]
    public void NotchMusicStripKeepsCoverAndBarsInsideTheCurvedEnds()
    {
        var geometry = new NotchGeometry(Omen) { CompactSideRoom = 500 };
        var shoulder = NotchLayout.ShoulderFor(geometry.StripHeight);
        Assert.Equal(Math.Min(26, geometry.StripHeight - NotchLayout.CompactEdgeGap * 2), geometry.MusicArtworkSide, 6);
        Assert.True(geometry.MusicArtworkLeading >= shoulder + NotchLayout.CompactEdgeGap - 1e-9);
        Assert.True(geometry.MusicBarsTrailing >= shoulder + NotchLayout.CompactEdgeGap - 1e-9);
        Assert.True(geometry.MusicArtworkLeading + geometry.MusicArtworkSide <= geometry.MusicWingWidth);
    }

    [Fact]
    public void StandardBarDrawsTheSame20PointCapsule()
    {
        var standard = Capsule(24);
        var hiddenBar = Capsule(22);
        Assert.Equal(2, standard.FloatingGap);
        Assert.Equal(20, standard.StripBodyHeight);
        Assert.Equal(1, hiddenBar.FloatingGap);
        Assert.Equal(20, hiddenBar.StripBodyHeight);
        Assert.Equal(standard.CameraWidth, hiddenBar.CameraWidth);
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(21, 0)]
    [InlineData(37, 2)]
    [InlineData(double.NaN, 2)]
    public void CapsuleGapStaysInsideShortBars(double bar, double gap) => Assert.Equal(gap, Capsule(bar).FloatingGap);

    [Fact]
    public void BareCapsuleIsShortOnWholePoints()
    {
        var standard = Capsule(24);
        var resting = standard.RestingSize(showsContent: false);
        Assert.True(resting.Width < standard.CameraWidth);
        Assert.Equal(Math.Round(resting.Width), resting.Width);
        var body = NotchLayout.CapsuleBody(new RectD(0, 0, resting.Width, resting.Height), standard.FloatingGap!.Value);
        Assert.InRange(Math.Abs(body.Width - NotchLayout.CapsuleRestingAspect * 20), 0, 1);
    }

    [Fact]
    public void CapsuleStretchesAlongTheTopOnHover()
    {
        var geometry = Capsule(24);
        var resting = geometry.RestingSize(false);
        var hover = geometry.HoverEmphasis(resting);
        Assert.Equal(resting.Height, hover.Height);
        Assert.Equal(resting.Width + 20, hover.Width);
    }

    [Fact]
    public void HangingNotchGrowsBelowOnHover()
    {
        var geometry = new NotchGeometry(Omen);
        var resting = geometry.RestingSize(false);
        Assert.Equal(resting.Height + 5, geometry.HoverEmphasis(resting).Height);
    }

    [Fact]
    public void StripsKeepTheCameraBetweenTheirWings()
    {
        var geometry = new NotchGeometry(Omen);
        Assert.Equal(new SizeD(135 + 112, 24), geometry.MusicStrip);
        Assert.Equal(56, geometry.MusicWingWidth);
        Assert.Equal(new SizeD(135 + 160, 24), geometry.Notice);
        Assert.Equal(new SizeD(135 + 128, 24), geometry.TimerStrip(70));
        Assert.Equal(new SizeD(135 + 88, 24), geometry.TimerStrip(10));
    }

    [Fact]
    public void LopsidedNoticeShiftsTowardItsWiderSide()
    {
        var geometry = new NotchGeometry(Omen);
        Assert.Equal(20, geometry.NoticeShift(40, 80));
        Assert.Equal(new SizeD(135 + 120, 24), geometry.NoticeSize(40, 80));
    }

    [Theory]
    [InlineData(NotchSize.Spacious, 560, 64 + 264)]
    [InlineData(NotchSize.Compact, 480, 64 + 180)]
    public void PresetsKeepUpstreamOpenSizes(NotchSize layout, double width, double height)
    {
        var geometry = new NotchGeometry(Omen, layout: layout);
        Assert.Equal(new SizeD(width, height), geometry.Expanded);
    }

    [Fact]
    public void LyricsGrowAPresetButStayWithinACustomHeight()
    {
        var spacious = new NotchGeometry(Omen);
        Assert.Equal(64 + 120 + 216, spacious.ExpandedSize(120, spacious.MusicExtrasHeight).Height);
        Assert.True(spacious.Envelope.Height >= spacious.ExpandedSize(extraHeight: spacious.MusicExtrasHeight).Height + NotchMotion.OvershootLimit);
        var custom = new NotchGeometry(Omen, layout: NotchSize.Custom, customHeight: 300);
        Assert.Equal(300, custom.ExpandedSize(120, custom.MusicExtrasHeight).Height);
    }

    [Fact]
    public void DropPlaceholderMatchesUpstreamAndFitsTheEnvelope()
    {
        var geometry = new NotchGeometry(Omen);
        Assert.Equal(new SizeD(340, 34 + 66), geometry.DropPlaceholder);
        var small = new NotchGeometry(new RectD(0, 0, 640, 360), layout: NotchSize.Custom, customHeight: 260);
        Assert.True(small.Envelope.Height >= small.DropPlaceholder.Height);
    }

    [Fact]
    public void CustomSizeIsClampedAndHonoured()
    {
        var geometry = new NotchGeometry(Omen, layout: NotchSize.Custom, customWidth: 9999, customHeight: 300);
        Assert.Equal(600, geometry.Expanded.Width);
        Assert.Equal(300, geometry.Expanded.Height);
    }

    [Fact]
    public void SmallScreenKeepsTheIslandOnScreen()
    {
        var geometry = new NotchGeometry(new RectD(0, 0, 640, 360));
        Assert.True(geometry.Expanded.Width <= 640 - 24);
        Assert.True(geometry.Expanded.Height <= 360 - 48);
        Assert.True(geometry.Envelope.Width <= 640 && geometry.Envelope.Height <= 360);
    }

    [Fact]
    public void EnvelopeHoldsEveryOpenSizeWithOvershoot()
    {
        var geometry = new NotchGeometry(Omen);
        Assert.True(geometry.Envelope.Width >= geometry.Expanded.Width + 2 * NotchMotion.OvershootLimit);
        Assert.True(geometry.Envelope.Height >= geometry.Expanded.Height + NotchMotion.OvershootLimit);
    }

    [Fact]
    public void HangingOutlineFillsItsRectAndCapsuleKeepsItsMargins()
    {
        var rect = new SizeD(247, 24);
        Assert.Equal(new RectD(0, 0, 247, 24), NotchLayout.IslandPath(rect, null).Bounds);
        var capsule = NotchLayout.IslandPath(rect, 2).Bounds;
        Assert.Equal(NotchLayout.CapsuleBody(new RectD(0, 0, 247, 24), 2), capsule);
    }

    [Theory]
    [InlineData(24, 4.56, 8.16)]
    [InlineData(370, 14, 28)]
    public void ShoulderAndRadiusFollowHeight(double height, double shoulder, double radius)
    {
        Assert.Equal(shoulder, NotchLayout.ShoulderFor(height), 6);
        Assert.Equal(radius, NotchLayout.SurfaceRadius(height), 6);
    }
}
