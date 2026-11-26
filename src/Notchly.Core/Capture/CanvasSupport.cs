// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/QuickTools/ScreenshotCanvasSupport.swift (canvas expansion and image-layer maths), the pure
// sizing parts of ScreenshotImageImport.swift, and the pure layout of UI/Screenshot/ScreenshotCanvasExpansionPreview.swift.
// Pixel work (expandedImage, drawing the preview) is the WPF layer's job: it gets the validated rect from here.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>What the margin added by Expand canvas is filled with.</summary>
public readonly record struct CanvasFill(bool IsTransparent, double Red = 1, double Green = 1, double Blue = 1)
{
    public static readonly CanvasFill Transparent = new(true, 0, 0, 0);
    public static readonly CanvasFill White = new(false, 1, 1, 1);
    public static CanvasFill Color(double red, double green, double blue) => new(false, red, green, blue);
}

public static class CanvasSupport
{
    /// <summary>The most pixels a canvas may hold (upstream scrollingCaptureMaximumPixels, which bounds every editor canvas).</summary>
    public const long MaximumPixels = 60_000_000;

    /// <summary>
    /// How close to an expansion grip, in points on screen, a press picks it up. The grips sit centered on the capture's
    /// edge, so the editor takes presses this far outside the canvas too.
    /// </summary>
    public const double ExpansionGripReach = 10;

    public static bool ValidSize(SizeD size)
    {
        double limit = MaximumPixels;
        return double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && size.Width >= 1 && size.Height >= 1
            && size.Width == RectMath.Rounded(size.Width) && size.Height == RectMath.Rounded(size.Height)
            && size.Width <= limit && size.Height <= limit
            && size.Width <= limit / size.Height;
    }

    /// <summary>
    /// The canvas rectangle, in the old canvas's coordinates, after dragging a grip to a point. Never smaller than the old
    /// canvas (an inward drag cannot crop); null when the result is not a valid canvas.
    /// </summary>
    public static RectD? ExpandedRect(SizeD size, SelectionHandle handle, PointD point)
    {
        if (!ValidSize(size) || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        double x = 0, y = 0, right = size.Width, bottom = size.Height;
        double px = RectMath.Rounded(point.X), py = RectMath.Rounded(point.Y);
        switch (handle)
        {
            case SelectionHandle.TopLeft: x = Math.Min(0, px); y = Math.Min(0, py); break;
            case SelectionHandle.Top: y = Math.Min(0, py); break;
            case SelectionHandle.TopRight: right = Math.Max(right, px); y = Math.Min(0, py); break;
            case SelectionHandle.Right: right = Math.Max(right, px); break;
            case SelectionHandle.BottomRight: right = Math.Max(right, px); bottom = Math.Max(bottom, py); break;
            case SelectionHandle.Bottom: bottom = Math.Max(bottom, py); break;
            case SelectionHandle.BottomLeft: x = Math.Min(0, px); bottom = Math.Max(bottom, py); break;
            case SelectionHandle.Left: x = Math.Min(0, px); break;
        }
        var rect = new RectD(x, y, right - x, bottom - y);
        return ValidSize(rect.Size()) ? rect : null;
    }

    /// <summary>Whether a target rect (relative to the old canvas) is a legal expansion: valid size, whole origin, covers the old canvas.</summary>
    public static bool IsExpansion(RectD rect, SizeD oldSize) =>
        ValidSize(rect.Size()) && double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && rect.X == RectMath.Rounded(rect.X) && rect.Y == RectMath.Rounded(rect.Y)
        && rect.ContainsRect(RectMath.FromSize(oldSize));

    /// <summary>
    /// Where the old picture is drawn inside the expanded one, top-left origin: the old canvas's origin sits at the
    /// negative of the expansion rect's origin.
    /// </summary>
    public static RectD OldImagePlacement(RectD expansion, SizeD oldSize) =>
        new(-expansion.MinX(), -expansion.MinY(), oldSize.Width, oldSize.Height);

    /// <summary>
    /// Centered on whole pixels: a layer drawn off the pixel grid is resampled, and a pasted screenshot's text would
    /// export blurred. Larger images shrink to fit, smaller ones keep their size.
    /// </summary>
    public static RectD FittedImageRect(SizeD imageSize, SizeD canvas)
    {
        double width = imageSize.Width, height = imageSize.Height;
        double factor = Math.Min(1, Math.Min(canvas.Width / width, canvas.Height / height));
        double w = width * factor, h = height * factor;
        return new RectD(Math.Floor((canvas.Width - w) / 2), Math.Floor((canvas.Height - h) / 2), w, h);
    }

    public static SelectionHandle? ExpansionHandleAt(PointD point, RectD rect, double tolerance) => NearestHandle(point, rect, tolerance);

    /// <summary>Which of the eight grips of an image layer a press picks up; the interior is not a grip, so it moves instead.</summary>
    public static SelectionHandle? ImageHandleAt(PointD point, RectD rect, double tolerance) => NearestHandle(point, rect, tolerance);

    private static SelectionHandle? NearestHandle(PointD point, RectD rect, double tolerance)
    {
        double radius = Math.Min(tolerance, Math.Max(2, Math.Min(rect.Width, rect.Height) / 4));
        SelectionHandle? best = null;
        double bestDistance = double.MaxValue;
        foreach (var handle in SelectionHandles.All)
        {
            var position = handle.Position(rect);
            if (!(Math.Abs(position.X - point.X) <= radius && Math.Abs(position.Y - point.Y) <= radius)) continue;
            double distance = RectMath.Distance(position, point);
            // Strictly closer wins, so a tie keeps the first handle in order, as Swift's min(by:) does.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = handle;
            }
        }
        return best;
    }

    /// <summary>
    /// An image layer resized by a grip: edge grips stretch one axis and fix the opposite edge, corner grips keep the
    /// aspect ratio and fix the opposite corner. Never collapses below one point or inverts.
    /// </summary>
    public static RectD ResizedImageRect(RectD rect, SelectionHandle handle, PointD point)
    {
        if (!(rect.Width > 0) || !(rect.Height > 0)) return rect;
        switch (handle)
        {
            case SelectionHandle.Left:
                return new RectD(Math.Min(point.X, rect.MaxX() - 1), rect.MinY(), Math.Max(1, rect.MaxX() - point.X), rect.Height);
            case SelectionHandle.Right:
                return new RectD(rect.MinX(), rect.MinY(), Math.Max(1, point.X - rect.MinX()), rect.Height);
            case SelectionHandle.Top:
                return new RectD(rect.MinX(), Math.Min(point.Y, rect.MaxY() - 1), rect.Width, Math.Max(1, rect.MaxY() - point.Y));
            case SelectionHandle.Bottom:
                return new RectD(rect.MinX(), rect.MinY(), rect.Width, Math.Max(1, point.Y - rect.MinY()));
        }
        bool left = handle is SelectionHandle.TopLeft or SelectionHandle.BottomLeft;
        bool top = handle is SelectionHandle.TopLeft or SelectionHandle.TopRight;
        var anchor = new PointD(left ? rect.MaxX() : rect.MinX(), top ? rect.MaxY() : rect.MinY());
        double dx = (left ? -1 : 1) * (point.X - anchor.X);
        double dy = (top ? -1 : 1) * (point.Y - anchor.Y);
        double ratio = rect.Width / rect.Height;
        double width = Math.Max(1, Math.Max(dx, dy * ratio));
        double height = width / ratio;
        return new RectD(left ? anchor.X - width : anchor.X, top ? anchor.Y - height : anchor.Y, width, height);
    }
}

/// <summary>The pure sizing rules of ScreenshotImageImport: when a picture is too big to open as-is and what it is reduced to.</summary>
public static class ImageImportSizing
{
    /// <summary>The longest side an imported picture may keep without the person choosing to reduce it.</summary>
    public const int MaximumDimension = 6_000;

    public static bool RequiresSizeChoice(SizeD pixelSize) => Math.Max(pixelSize.Width, pixelSize.Height) > MaximumDimension;

    /// <summary>The longest side after reducing to <see cref="MaximumDimension"/> keeping the aspect ratio (at least 1 point per side, each rounded).</summary>
    public static int ReducedMaximumDimension(SizeD pixelSize)
    {
        double width = Math.Max(1, Math.Abs(pixelSize.Width)), height = Math.Max(1, Math.Abs(pixelSize.Height));
        double scale = Math.Min(1, (double)MaximumDimension / Math.Max(width, height));
        double w = Math.Max(1, RectMath.Rounded(width * scale)), h = Math.Max(1, RectMath.Rounded(height * scale));
        return (int)Math.Max(w, h);
    }

    /// <summary>EXIF orientations 5 to 8 turn the picture a quarter, so its upright size swaps width and height.</summary>
    public static SizeD OrientedPixelSize(int width, int height, int orientation) =>
        orientation is >= 5 and <= 8 ? new SizeD(height, width) : new SizeD(width, height);
}

/// <summary>
/// Where the Expand canvas preview draws, in its own space. Each grip is centered on the draft's edge, where a press picks
/// it up, so the preview reaches past the draft on every side; the room is whole 8 point checker tiles, so the draft keeps
/// the pixels it had and a transparent fill's checkerboard still continues the one the card shows.
/// </summary>
public sealed class ExpansionPreviewLayout
{
    public SizeD Size { get; }
    public RectD Draft { get; }
    public IReadOnlyList<RectD> Grips { get; }
    public double GripLineWidth { get; }

    /// <summary>The grips match the selection handles: a 3.5 pt ring drawn with a 1.5 pt line, scaled like the capture.</summary>
    public ExpansionPreviewLayout(SizeD draftSize, double displayScale)
    {
        double radius = 3.5 * displayScale;
        double lineWidth = 1.5 * displayScale;
        double outset = Math.Ceiling((radius + lineWidth) / 8) * 8;
        var frame = new RectD(outset, outset, draftSize.Width, draftSize.Height);
        Size = new SizeD(draftSize.Width + outset * 2, draftSize.Height + outset * 2);
        Draft = frame;
        GripLineWidth = lineWidth;
        Grips = SelectionHandles.All.Select(handle =>
        {
            var point = handle.Position(frame);
            return new RectD(point.X - radius, point.Y - radius, radius * 2, radius * 2);
        }).ToList();
    }

    /// <summary>
    /// The corners of the capture card that the expansion fill must round off: only the ones where the new margin meets
    /// the card, so a corner the draft still shares keeps the card's own rounding and nothing is filled before a drag.
    /// Edges closer than 0.001 are the same edge, apart only by rounding in the scaling.
    /// </summary>
    public static CardCornerRadii CardHoleCorners(RectD card, RectD draft, double cornerRadius)
    {
        static bool Moved(double a, double b) => Math.Abs(a - b) > 0.001;
        bool left = Moved(card.MinX(), draft.MinX()), top = Moved(card.MinY(), draft.MinY());
        bool right = Moved(card.MaxX(), draft.MaxX()), bottom = Moved(card.MaxY(), draft.MaxY());
        double radius = cornerRadius > 0 ? cornerRadius + 1 : 0;
        double Corner(bool meetsMargin) => meetsMargin ? radius : 0;
        return new CardCornerRadii(Corner(top || left), Corner(top || right), Corner(bottom || right), Corner(bottom || left));
    }
}

/// <summary>Corner radii of the card hole, clockwise from the top-left.</summary>
public readonly record struct CardCornerRadii(double TopLeft, double TopRight, double BottomRight, double BottomLeft);
