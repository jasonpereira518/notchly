// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchSupport.swift (struct NotchGeometry),
// specialised to a SIMULATED cutout: a Windows laptop has no camera housing for the OS to
// report. Upstream's physical-camera, menu-bar measurement and Lock Screen branches are left
// out. Coordinates are DIPs with a top-left origin; y grows downward.
namespace Notchly.Core.Notch;

public sealed record NotchGeometry
{
    /// <summary>The monitor the island sits on, in DIPs.</summary>
    public RectD Screen { get; }
    public NotchSize Layout { get; }
    public double CustomWidth { get; }
    public double CustomHeight { get; }
    /// <summary>Stands in for the menu bar height upstream sizes the simulated cutout from.</summary>
    public double BarHeight { get; }
    /// <summary>Null hangs the island from the top edge. A capsule floats this far inside its strip.</summary>
    public double? FloatingGap { get; }
    /// <summary>How far a fitted capsule sits below the top of the display.</summary>
    public double FloatingDrop { get; }
    /// <summary>Width of the simulated camera cutout the closed island is built around.</summary>
    public double CameraWidth { get; }
    /// <summary>Height of one closed strip.</summary>
    public double CameraHeight { get; }
    /// <summary>Free room on each side of the cutout for wings; null when unknown.</summary>
    public double? CompactSideRoom { get; init; }
    /// <summary>Windows-only: moves the cutout to sit under an off-centre webcam.</summary>
    public double CameraOffset { get; init; }
    /// <summary>How far the island's centre sits right of the camera's (a lopsided notice).</summary>
    public double SurfaceShift { get; init; }

    private readonly double _capsuleWidthFit;

    /// <summary>The floating controls' gutter beside the open island (NotchQuickAccessLayout.gutter).</summary>
    public const double QuickAccessGutter = 72;
    public const double OvershootRoom = 12;

    public NotchGeometry(RectD screen, double barHeight = 24, NotchSize layout = NotchSize.Spacious,
                         double customWidth = NotchSizes.DefaultWidth, double customHeight = NotchSizes.DefaultHeight,
                         NotchSilhouette silhouette = NotchSilhouette.Notch, NotchCapsuleFit capsuleFit = default)
    {
        Screen = screen;
        Layout = layout;
        CustomWidth = NotchSizes.Clamped(customWidth, NotchSizes.MinWidth, NotchSizes.MaxWidth, NotchSizes.DefaultWidth);
        CustomHeight = NotchSizes.Clamped(customHeight, NotchSizes.MinHeight, NotchSizes.MaxHeight, NotchSizes.DefaultHeight);
        BarHeight = double.IsFinite(barHeight) ? Math.Min(64, Math.Max(16, barHeight)) : 24;

        double? gap = silhouette == NotchSilhouette.Capsule ? NotchLayout.CapsuleGap(BarHeight) : null;
        FloatingGap = gap;
        var fit = gap is null ? NotchCapsuleFit.Zero : capsuleFit;
        FloatingDrop = fit.Drop;
        _capsuleWidthFit = fit.Width;

        double? fullStrip = gap is { } g1 ? Math.Max(BarHeight + fit.Height, g1 * 2 + 12) : null;
        var stripHeight = gap is { } g2 ? Math.Max(BarHeight + fit.Height, g2 * 2 + 12) : BarHeight;
        var profileHeight = gap is { } g3 && fullStrip is { } full ? full - (g3 - NotchLayout.CapsuleMargin) * 2 : BarHeight;
        // A simulated cutout is as wide as a MacBook's camera housing for this bar height.
        var simulated = 180 * profileHeight / 32;
        CameraWidth = Math.Min(gap is null ? simulated : Math.Round(simulated + fit.Width, MidpointRounding.AwayFromZero),
                               screen.Width * 0.7);
        CameraHeight = stripHeight;
        CompactSideRoom = Math.Max(0, Math.Floor((screen.Width - CameraWidth) / 2 - 12));
    }

    public bool Floats => FloatingGap is not null;
    public double StripHeight => CameraHeight;
    /// <summary>What a strip shows inside: all of it, or the capsule within its margins.</summary>
    public double StripBodyHeight => Math.Max(0, StripHeight - (FloatingGap ?? 0) * 2);

    // MARK: Closed sizes

    public double RestingWingWidth
    {
        get
        {
            var available = Math.Floor(Math.Min(44, Math.Max(0, CompactSideRoom ?? 0)));
            return available >= 44 ? available : 0;
        }
    }

    public SizeD Collapsed => new(Math.Min(Screen.Width - 24, CameraWidth + RestingWingWidth * 2), StripHeight);

    public SizeD RestingSize(bool showsContent)
    {
        if (showsContent) return Collapsed;
        if (!Floats) return new SizeD(CameraWidth, CameraHeight);
        // Bare, a capsule is shorter than an activity's wings, closer to the phone's proportions.
        var shoulders = NotchLayout.ShoulderFor(StripHeight) * 2;
        var capsule = Math.Max(NotchLayout.CapsuleRestingAspect * StripBodyHeight + _capsuleWidthFit, StripBodyHeight * 2);
        return new SizeD(Math.Min(CameraWidth, Math.Round(capsule + shoulders, MidpointRounding.AwayFromZero)), CameraHeight);
    }

    /// <summary>
    /// A closed strip with a wing on each side of the cutout, each `wing` wide, when the display
    /// leaves room for it (upstream compactTimer/Download/Agent/WatchGeometry + musicStrip).
    /// </summary>
    public SizeD StripWithWings(double wing, double minimumWing)
    {
        var room = CompactSideRoom ?? 0;
        if (!double.IsFinite(room) || room < minimumWing) return new SizeD(CameraWidth, StripHeight);
        var side = Math.Min(wing, room);
        return new SizeD(CameraWidth + side * 2, StripHeight);
    }

    /// <summary>The closed music strip: the cover in the left wing, the bars in the right one.</summary>
    public SizeD MusicStrip => StripWithWings(56, 44);

    /// <summary>A download: both wings as wide as the name needs, 56–160 (upstream compactDownloadGeometry).</summary>
    public SizeD DownloadStrip(double wing) => StripWithWings(wing, 44);
    public double MusicWingWidth => Math.Max(0, (MusicStrip.Width - CameraWidth) / 2);

    // MARK: Closed music strip contents (upstream compactMusicArtwork*/compactMusicBars* and
    // CapsuleLayout.artworkSide/artworkInset/barsHeight/endPadding): the cover sits at the
    // strip's left end and the bars at its right end, each an even gap from the outline.

    /// <summary>Gap between the capsule's end and its bars (upstream CapsuleLayout.endPadding).</summary>
    public const double CapsuleEndPadding = 8;

    /// <summary>The cover's side: the strip's height less an even gap above and below.</summary>
    public double MusicArtworkSide => Floats
        ? Math.Max(0, StripBodyHeight - 6)
        : Math.Max(0, Math.Min(26, StripHeight - NotchLayout.CompactEdgeGap * 2));

    /// <summary>A capsule's cover is round, concentric with the capsule's end.</summary>
    public double MusicArtworkRadius
    {
        get
        {
            var side = MusicArtworkSide;
            if (Floats || !MusicArtworkFills) return Floats ? side / 2 : side * 0.28;
            var concentric = NotchLayout.SurfaceRadius(MusicStrip.Height) - (StripHeight - side) / 2;
            return Math.Min(side / 2, Math.Max(side * 0.2, concentric));
        }
    }

    /// <summary>From the strip's left edge to the cover's left edge.</summary>
    public double MusicArtworkLeading => Floats
        ? NotchLayout.ShoulderFor(StripHeight) + (StripBodyHeight - MusicArtworkSide) / 2
        : CompactEdgeInset(MusicArtworkSide, MusicArtworkRadius,
                           MusicArtworkFills ? (StripHeight - MusicArtworkSide) / 2 : NotchLayout.CompactEdgeGap);

    public double MusicBarsHeight => Floats
        ? Math.Min(16, Math.Max(6, StripBodyHeight - 8))
        : Math.Min(16, Math.Max(6, StripHeight - NotchLayout.CompactEdgeGap * 2));

    /// <summary>From the bars' right edge to the strip's right edge.</summary>
    public double MusicBarsTrailing => Floats
        ? NotchLayout.ShoulderFor(StripHeight) + CapsuleEndPadding
        : CompactEdgeInset(MusicBarsHeight, NotchLayout.CompactMusicBarWidth / 2, NotchLayout.CompactEdgeGap);

    /// <summary>A cover that nearly fills the strip shares its corners' centre with the strip's.</summary>
    private bool MusicArtworkFills => StripHeight - MusicArtworkSide <= NotchLayout.CompactEdgeGap * 4;

    /// <summary>
    /// Upstream compactActivityEdgeInset: how far in from the strip's end a vertically centred
    /// box `boxHeight` tall, rounded by `radius`, keeps an even `gap` from the outline, whose
    /// lower half is mostly one long arc.
    /// </summary>
    private double CompactEdgeInset(double boxHeight, double radius, double gap)
    {
        var size = MusicStrip;
        var shoulder = NotchLayout.ShoulderFor(size.Height);
        var corner = Math.Min(NotchLayout.SurfaceRadius(size.Height), (size.Width - shoulder * 2) / 2);
        var flat = shoulder + gap;
        var below = (StripHeight - boxHeight) / 2;
        var reach = corner - radius - gap;
        var drop = corner - radius - below;
        if (reach <= 0 || drop <= 0) return Math.Max(0, flat);
        var span = reach > drop ? Math.Sqrt(reach * reach - drop * drop) : 0;
        return Math.Max(0, Math.Max(flat, shoulder + corner - radius - span));
    }

    /// <summary>Both timer wings take the width the wider reading needs, 44–64 points.</summary>
    public SizeD TimerStrip(double fittedWing) =>
        StripWithWings(Math.Min(64, Math.Max(44, double.IsFinite(fittedWing) ? Math.Ceiling(fittedWing) : 0)), 64);

    public static readonly (double Min, double Max) NoticeWingRange = (44, 120);

    public double NoticeWingWidth(double preferred) =>
        Math.Max(0, Math.Min(preferred, (Screen.Width - 24 - CameraWidth) / 2));

    /// <summary>A notice beside the camera, each side as wide as it asks.</summary>
    public SizeD NoticeSize(double leading, double trailing) =>
        new(CameraWidth + NoticeWingWidth(leading) + NoticeWingWidth(trailing), StripHeight);

    public SizeD Notice => NoticeSize(80, 80);

    /// <summary>How far a lopsided notice's centre sits from the camera's, toward its wider side.</summary>
    public double NoticeShift(double leading, double trailing) =>
        (NoticeWingWidth(trailing) - NoticeWingWidth(leading)) / 2;

    // MARK: Open sizes

    /// <summary>Below the cutout, or for a capsule the island's even inner margin.</summary>
    public double SafeContentTop => Floats ? NotchLayout.BottomInset : CameraHeight + 10;

    public SizeD Peek => new(Math.Min(Screen.Width - 24, Math.Max(CameraWidth + 110, 340)), SafeContentTop + 52);

    /// <summary>The closed island while files are dragged near it: room for "Drop files here".</summary>
    public SizeD DropPlaceholder => new(Math.Min(Screen.Width - 24, Math.Max(CameraWidth + 110, 340)), SafeContentTop + 66);

    /// <summary>A capsule's row keeps clear of its rounded top corners.</summary>
    public double HeaderTopInset => Floats ? NotchLayout.BottomInset - (NotchLayout.HeaderHeight - 28) / 2 : 0;
    public double HeaderRowHeight => NotchLayout.HeaderHeight;
    public double PageTopInset => HeaderTopInset + HeaderRowHeight + NotchLayout.Spacing;
    public double HeaderChromeHeight => PageTopInset - HeaderTopInset + NotchLayout.BottomInset;

    public double ExpandedWidth
    {
        get
        {
            var chosen = NotchLayout.PreferredWidth(Layout, CustomWidth);
            return Math.Min(Math.Max(chosen, CameraWidth + 36), Screen.Width - 24 - QuickAccessGutter * 2);
        }
    }

    public double ContentWidth => Math.Max(0, ExpandedWidth - NotchLayout.HorizontalInset * 2);

    /// <summary>Content height available before a page needs to scroll.</summary>
    public double ContentBudget => Layout switch
    {
        NotchSize.Compact => NotchLayout.CompactContentHeight,
        NotchSize.Spacious => NotchLayout.SpaciousContentHeight,
        _ => Math.Max(0, CustomHeight - HeaderTopInset - HeaderChromeHeight),
    };

    /// <summary>
    /// Lyrics and the queue open below the player. Presets grow the island for them; a custom
    /// height keeps them within its limit (upstream musicExtrasHeight).
    /// </summary>
    public double MusicExtrasHeight => Layout == NotchSize.Custom ? Math.Min(216, ContentBudget) : 216;

    /// <summary>
    /// The open island around a page `contentHeight` tall (the budget when null), plus
    /// `extraHeight` the page adds below itself past the preset's budget, as the player's lyrics do.
    /// </summary>
    public SizeD ExpandedSize(double? contentHeight = null, double extraHeight = 0)
    {
        var content = Math.Min(ContentBudget, Math.Max(0, contentHeight ?? ContentBudget)) + Math.Max(0, extraHeight);
        var height = HeaderTopInset + HeaderChromeHeight + content;
        if (Layout == NotchSize.Custom) height = Math.Min(height, CustomHeight);
        return new SizeD(ExpandedWidth, Math.Min(height, Screen.Height - 48));
    }

    public SizeD Expanded => ExpandedSize();

    /// <summary>The page area inside an island of `size`.</summary>
    public RectD ContentRect(SizeD size) => new(
        NotchLayout.HorizontalInset, PageTopInset,
        Math.Max(0, size.Width - NotchLayout.HorizontalInset * 2),
        Math.Max(0, size.Height - PageTopInset - NotchLayout.BottomInset));

    /// <summary>The top row inside an island of `size`.</summary>
    public RectD HeaderRect(SizeD size) => new(
        NotchLayout.HorizontalInset, HeaderTopInset,
        Math.Max(0, size.Width - NotchLayout.HorizontalInset * 2), HeaderRowHeight);

    // MARK: Hover

    /// <summary>The closed island grows slightly under the pointer (NotchHoverEmphasis).</summary>
    public SizeD HoverEmphasis(SizeD resting, double reach = 0)
    {
        var occupiedWing = Math.Max(0, (resting.Width - CameraWidth) / 2) + reach;
        var freeSide = Math.Max(0, (CompactSideRoom ?? 0) - occupiedWing);
        var growth = Math.Min(10, freeSide);
        return new SizeD(resting.Width + growth * 2, resting.Height + (Floats ? 0 : 5));
    }

    // MARK: Placement

    /// <summary>The window envelope that holds every size plus spring overshoot.</summary>
    public SizeD Envelope
    {
        get
        {
            var open = ExpandedSize(extraHeight: MusicExtrasHeight);
            var width = Math.Max(open.Width, Math.Max(Peek.Width, NoticeSize(120, 120).Width)) + OvershootRoom * 2;
            var height = Math.Max(open.Height, Math.Max(Peek.Height, DropPlaceholder.Height)) + OvershootRoom + FloatingDrop;
            return new SizeD(Math.Min(Screen.Width, Math.Ceiling(width)), Math.Min(Screen.Height, Math.Ceiling(height)));
        }
    }

    /// <summary>Where an island of `size` sits on the screen, top-anchored under the camera.</summary>
    public RectD Frame(SizeD size) => new(
        Screen.MidX + CameraOffset + SurfaceShift - size.Width / 2,
        Screen.Y + FloatingDrop,
        size.Width, size.Height);

    public bool Contains(PointD point, SizeD size) => Frame(size).Contains(point);
}
