// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the capture and crop loupe maths in upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift (captureLoupe*, cropLoupeSampleRect)
// and ScreenshotSelectionController.adjustLoupeZoom / steppedLoupeNeedsRawWheel.
// Image coordinates are pixels from the top-left, as upstream's already are.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

public static class ScreenshotLoupe
{
    /// <summary>A stable square of source pixels for the loupe: odd, 13 at zoom 1, never below 3.</summary>
    public const double BaseSampleSide = 13;
    public const double MinSampleSide = 3;
    public const double MinZoom = 0.5;
    public const double MaxZoom = BaseSampleSide / MinSampleSide;
    public static readonly IReadOnlyList<double> DefaultZooms = [0.5, 1, 2, 4];

    /// <summary>The magnifier square on screen, in points. Big enough that each sampled pixel is a readable grid cell.</summary>
    public const double FrameSide = 132;

    /// <summary>Zoom factor of one wheel packet in fast mode.</summary>
    public const double FastZoomFactor = 1.15;

    private static double ClampZoom(double zoom) => Math.Min(Math.Max(zoom, MinZoom), MaxZoom);

    /// <summary>The zoom a session starts at: the remembered one or the default, clamped; 1 when it is not a number.</summary>
    public static double InitialZoom(bool rememberLast, double defaultZoom, double lastZoom)
    {
        var requested = rememberLast ? lastZoom : defaultZoom;
        return double.IsFinite(requested) ? ClampZoom(requested) : 1;
    }

    /// <summary>Option temporarily swaps the chosen wheel mode.</summary>
    public static bool UsesSteppedZoom(bool steppedByDefault, bool optionPressed) => steppedByDefault != optionPressed;

    /// <summary>
    /// A few high-resolution mouse drivers put sub-notch movement only in the fixed-point wheel field,
    /// which a rounded delta would lose. Fixed-point first, then whole lines, then the plain delta.
    /// On Windows pass WHEEL_DELTA / 120 as the fixed-point value and 0 for the line count.
    /// </summary>
    public static double WheelDelta(double scrollingDelta, long lineDelta, double fixedPointDelta)
    {
        if (double.IsFinite(fixedPointDelta) && fixedPointDelta != 0) return fixedPointDelta;
        if (lineDelta != 0) return lineDelta;
        return double.IsFinite(scrollingDelta) ? scrollingDelta : 0;
    }

    /// <summary>
    /// Fast mode on a plain wheel: one discrete event per notch zooms by three packet factors, so fast
    /// mode does not crawl at stepped pace. A smoothed wheel (continuous) keeps one factor per packet.
    /// </summary>
    public static double FastZoom(double zoom, double scrollDelta, bool isContinuous)
    {
        if (isContinuous || !double.IsFinite(scrollDelta) || scrollDelta == 0) return Zoom(zoom, scrollDelta);
        var result = zoom;
        for (var i = 0; i < 3; i++) result = Zoom(result, scrollDelta);
        return result;
    }

    /// <summary>One fast-mode step per event: 15 percent per packet, clamped.</summary>
    public static double Zoom(double zoom, double scrollDelta)
    {
        if (!double.IsFinite(scrollDelta) || scrollDelta == 0) return ClampZoom(zoom);
        var factor = scrollDelta > 0 ? FastZoomFactor : 1 / FastZoomFactor;
        return ClampZoom(zoom * factor);
    }

    /// <summary>
    /// Step mode advances by exactly one drawable pixel-grid level (two source pixels per side),
    /// which a fixed percentage cannot promise at either end of the range.
    /// </summary>
    public static double SteppedZoom(double zoom, double scrollDelta)
    {
        var clamped = ClampZoom(zoom);
        if (!double.IsFinite(scrollDelta) || scrollDelta == 0) return clamped;

        var currentSide = SampleSide(clamped);
        var widestSide = SampleSide(MinZoom);
        var targetSide = scrollDelta > 0
            ? Math.Max(MinSampleSide, currentSide - 2)
            : Math.Min(widestSide, currentSide + 2);
        if (targetSide == currentSide) return clamped;

        // The widest level lies just below the numeric minimum when expressed as base / side, so it
        // deliberately resolves to the public boundary.
        if (targetSide == widestSide) return MinZoom;
        return ClampZoom(BaseSampleSide / targetSide);
    }

    /// <summary>
    /// Sampled source pixels per side. Always an odd whole number, never below three, so the pixel
    /// under the pointer is a real middle cell the grid can outline.
    /// </summary>
    public static double SampleSide(double zoom)
    {
        var raw = BaseSampleSide / ClampZoom(zoom);
        var odd = 2 * Math.Floor(raw / 2) + 1;
        return Math.Max(MinSampleSide, odd);
    }

    /// <summary>The pixel grid appears only once a cell is at least 6 points, so lines separate pixels instead of shading the image.</summary>
    public static bool GridVisible(double frameSide, double sampleSide) => sampleSide > 0 && frameSide / sampleSide >= 6;

    /// <summary>
    /// Arrow keys move the pointer by whole device pixels of the display it is on, in points, so one
    /// press lands on the neighbouring pixel at any scale. Shift covers ten. <paramref name="scale"/>
    /// below 1 (or missing) falls back to whole points.
    /// </summary>
    public static PointD Nudge(double dx, double dy, bool fast, double scale)
    {
        var step = (fast ? 10 : 1) / Math.Max(scale, 1);
        return new PointD(dx * step, dy * step);
    }

    /// <summary>
    /// The square of source pixels a loupe magnifies. The capture loupe reads a colour, so it centres
    /// on a whole pixel (<paramref name="centredOnPixel"/>): the side is forced odd and centred on the
    /// floored coordinate. The editor's crop loupe marks an edge between pixels, so it passes false and
    /// gets an even side centred on a whole coordinate. Near an image edge the sample slides inward
    /// instead of shrinking.
    /// </summary>
    public static RectD CropSampleRect(PointD around, SizeD imageSize, double sideLength = 13, bool centredOnPixel = true)
    {
        var imageWidth = Math.Max(1, Math.Floor(imageSize.Width));
        var imageHeight = Math.Max(1, Math.Floor(imageSize.Height));
        var side = Math.Max(1, Math.Floor(sideLength));
        var isEven = side % 2 == 0;
        if (centredOnPixel && isEven) side = Math.Max(1, side - 1);
        else if (!centredOnPixel && !isEven) side += 1;
        var width = Math.Min(side, imageWidth);
        var height = Math.Min(side, imageHeight);
        var x = centredOnPixel ? Math.Floor(around.X) - Math.Floor(width / 2) : Math.Floor(around.X - width / 2);
        var y = centredOnPixel ? Math.Floor(around.Y) - Math.Floor(height / 2) : Math.Floor(around.Y - height / 2);
        return new RectD(Math.Min(Math.Max(x, 0), imageWidth - width), Math.Min(Math.Max(y, 0), imageHeight - height), width, height);
    }

    /// <summary>
    /// Where the one source pixel under <paramref name="around"/> lands inside a loupe frame. It floors
    /// the coordinate exactly like the colour read does, so the highlight can never mark a neighbour of
    /// the pixel that gets copied.
    /// </summary>
    public static RectD TargetPixelRect(PointD around, RectD source, RectD frame)
    {
        if (!(source.Width >= 1 && source.Height >= 1)) return frame;
        var column = Math.Min(Math.Max(Math.Floor(around.X), source.X), source.Right - 1);
        var row = Math.Min(Math.Max(Math.Floor(around.Y), source.Y), source.Bottom - 1);
        var width = frame.Width / source.Width;
        var height = frame.Height / source.Height;
        return new RectD(frame.X + (column - source.X) * width, frame.Y + (row - source.Y) * height, width, height);
    }
}

/// <summary>The loupe's per-session zoom: starts from the settings and is remembered as it changes.</summary>
public sealed class LoupeZoomState
{
    public double Zoom { get; private set; }

    public LoupeZoomState(bool rememberLast, double defaultZoom, double lastZoom) =>
        Zoom = ScreenshotLoupe.InitialZoom(rememberLast, defaultZoom, lastZoom);

    /// <summary>
    /// Applies one wheel event (adjustLoupeZoom). Returns the new zoom, which the caller stores as
    /// screenshotLoupeLastZoom.
    /// </summary>
    public double Adjust(double scrollDelta, bool stepped, bool isContinuous) =>
        Zoom = stepped
            ? ScreenshotLoupe.SteppedZoom(Zoom, scrollDelta)
            : ScreenshotLoupe.FastZoom(Zoom, scrollDelta, isContinuous);

    public double SampleSide => ScreenshotLoupe.SampleSide(Zoom);
    public bool GridVisible => ScreenshotLoupe.GridVisible(ScreenshotLoupe.FrameSide, SampleSide);
}
