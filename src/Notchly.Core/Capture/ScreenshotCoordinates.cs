// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the "Coordinate conversions" section of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift, adapted to Windows.
//
// Upstream converts between three systems: window-server points (top-left), Cocoa global points
// (bottom-left) and flipped overlay views (top-left). Notchly has only top-left systems, so:
//   * global logical points -> a display's overlay view is a plain translation (GlobalToView/ViewToGlobal);
//   * logical points <-> physical pixels is a per-display scale (WPF device-independent units to
//     device pixels: 1.0, 1.25, 1.5, 2.0 ...);
//   * the bottom-left flips (FlipVertically) are kept, with their upstream numbers, for APIs and
//     bitmaps that really are bottom-up.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>One monitor: its frame in logical points (global, top-left origin) and its scale in device pixels per point.</summary>
public readonly record struct CaptureDisplay(string Id, RectD Frame, double Scale)
{
    /// <summary>The frame in device pixels (the capture surface's size).</summary>
    public SizeD PixelSize => new(Frame.Width * Scale, Frame.Height * Scale);
}

public static class ScreenshotCoordinates
{
    /// <summary>
    /// Upstream cocoaRect(fromWindowServer:): a rectangle measured from the top of a container
    /// <paramref name="containerHeight"/> tall, re-measured from the bottom. The same call undoes
    /// itself, which is also what flippedViewRect / cocoaRect(fromFlippedView:) amount to.
    /// </summary>
    public static RectD FlipVertically(RectD rect, double containerHeight) =>
        new(rect.X, containerHeight - rect.Bottom, rect.Width, rect.Height);

    /// <summary>A global logical rectangle into the coordinates of the overlay that covers <paramref name="screenFrame"/>.</summary>
    public static RectD GlobalToView(RectD globalRect, RectD screenFrame) =>
        new(globalRect.X - screenFrame.X, globalRect.Y - screenFrame.Y, globalRect.Width, globalRect.Height);

    /// <summary>A rectangle in a display's overlay back into global logical points (anchors controls beside the captured area).</summary>
    public static RectD ViewToGlobal(RectD viewRect, RectD screenFrame) =>
        new(screenFrame.X + viewRect.X, screenFrame.Y + viewRect.Y, viewRect.Width, viewRect.Height);

    public static PointD GlobalToView(PointD global, RectD screenFrame) => new(global.X - screenFrame.X, global.Y - screenFrame.Y);

    public static PointD ViewToGlobal(PointD view, RectD screenFrame) => new(screenFrame.X + view.X, screenFrame.Y + view.Y);

    /// <summary>
    /// A rectangle in a view of <paramref name="viewSize"/> points mapped onto an image of
    /// <paramref name="imageSize"/> pixels covering the same area, rounded outward to whole pixels and
    /// clamped to the image. With a fractional display scale this is where the physical capture rectangle comes from.
    /// </summary>
    public static RectD ImagePixelRect(RectD rect, SizeD viewSize, SizeD imageSize)
    {
        if (!(viewSize.Width > 0 && viewSize.Height > 0)) return CaptureGeometry.Zero;
        var scaleX = imageSize.Width / viewSize.Width;
        var scaleY = imageSize.Height / viewSize.Height;
        var scaled = new RectD(rect.X * scaleX, rect.Y * scaleY, rect.Width * scaleX, rect.Height * scaleY).Integral();
        return scaled.Clamp(new RectD(0, 0, imageSize.Width, imageSize.Height));
    }

    /// <summary>
    /// A point in the overlay mapped to the source image. Kept apart from rectangle rounding so a pixel
    /// loupe follows the pointer without jumping by whole selections.
    /// </summary>
    public static PointD ImagePixelPoint(PointD point, SizeD viewSize, SizeD imageSize)
    {
        if (!(viewSize.Width > 0 && viewSize.Height > 0)) return new PointD(0, 0);
        return new PointD(Math.Min(Math.Max(point.X / viewSize.Width * imageSize.Width, 0), imageSize.Width),
                          Math.Min(Math.Max(point.Y / viewSize.Height * imageSize.Height, 0), imageSize.Height));
    }

    /// <summary>Logical points to device pixels at a display scale (no rounding).</summary>
    public static RectD LogicalToPhysical(RectD logical, double scale) =>
        new(logical.X * scale, logical.Y * scale, logical.Width * scale, logical.Height * scale);

    /// <summary>Device pixels back to logical points at a display scale.</summary>
    public static RectD PhysicalToLogical(RectD physical, double scale) =>
        scale > 0 ? new RectD(physical.X / scale, physical.Y / scale, physical.Width / scale, physical.Height / scale) : physical;

    public static PointD LogicalToPhysical(PointD logical, double scale) => new(logical.X * scale, logical.Y * scale);

    public static PointD PhysicalToLogical(PointD physical, double scale) =>
        scale > 0 ? new PointD(physical.X / scale, physical.Y / scale) : physical;

    /// <summary>
    /// The device-pixel rectangle of a selection made in a display's overlay: the view rectangle scaled
    /// by the display's scale, rounded outward and clamped to the display's pixel size.
    /// </summary>
    public static RectD SelectionPixelRect(RectD viewRect, CaptureDisplay display) =>
        ImagePixelRect(viewRect, new SizeD(display.Frame.Width, display.Frame.Height), display.PixelSize);

    /// <summary>
    /// The display whose frame holds the point (half-open), as the overlay that owns the pointer.
    /// Null when the point is on no display.
    /// </summary>
    public static CaptureDisplay? DisplayContaining(PointD globalPoint, IEnumerable<CaptureDisplay> displays)
    {
        foreach (var display in displays) if (display.Frame.CgContainsPoint(globalPoint)) return display;
        return null;
    }

    /// <summary>
    /// Whether a stretched-over-two-scales layer is safe: the device-pixel size a window of
    /// <paramref name="frame"/> should have at <paramref name="scale"/>, rounded to whole pixels.
    /// </summary>
    public static (int Width, int Height) PixelSizeFor(RectD frame, double scale) =>
        ((int)Math.Round(frame.Width * scale, MidpointRounding.AwayFromZero),
         (int)Math.Round(frame.Height * scale, MidpointRounding.AwayFromZero));
}
