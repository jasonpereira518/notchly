// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the selection geometry in upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift (selectionRect, handles, resizedRect,
// movedRect, pixelSnappedCropRect, isClick, startsNewCropSelection, PickableWindow / window(at:)),
// the pure pickableWindows filter of ScreenshotCaptureEngine, and the drag / Space-move / click /
// repeat-last-region / arrow-nudge logic of ScreenshotSelectionController (mouseDown, mouseDragged,
// mouseUp, repeatLastRegion, nudgePointer).
//
// All coordinates are logical points with a top-left origin, y down. Upstream's selection overlay is
// already a flipped (top-left) view, so selection and handle maths is unchanged; only the arrow nudge
// is flipped (see SelectionNudge).
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

public static class ScreenshotSelection
{
    public static readonly IReadOnlyList<SelectionHandle> Handles =
        [SelectionHandle.TopLeft, SelectionHandle.Top, SelectionHandle.TopRight, SelectionHandle.Right,
         SelectionHandle.BottomRight, SelectionHandle.Bottom, SelectionHandle.BottomLeft, SelectionHandle.Left];

    /// <summary>A press that never travelled beyond this is a click, which captures the window under the pointer.</summary>
    public const double ClickDragThreshold = 4;

    /// <summary>A region smaller than this on either side is dropped on release.</summary>
    public const double MinimumRegionSide = 2;

    /// <summary>Windows smaller than this on either side cannot be picked.</summary>
    public const double MinimumPickableWindowSide = 40;

    public static PointD HandlePosition(this SelectionHandle handle, RectD rect) => handle switch
    {
        SelectionHandle.TopLeft => new(rect.X, rect.Y),
        SelectionHandle.Top => new(rect.MidX, rect.Y),
        SelectionHandle.TopRight => new(rect.Right, rect.Y),
        SelectionHandle.Right => new(rect.Right, rect.MidY()),
        SelectionHandle.BottomRight => new(rect.Right, rect.Bottom),
        SelectionHandle.Bottom => new(rect.MidX, rect.Bottom),
        SelectionHandle.BottomLeft => new(rect.X, rect.Bottom),
        _ => new(rect.X, rect.MidY()),
    };

    /// <summary>The first handle (in the fixed order above) within <paramref name="tolerance"/> of the point.</summary>
    public static SelectionHandle? HandleAt(PointD point, RectD rect, double tolerance)
    {
        foreach (var handle in Handles)
        {
            var position = handle.HandlePosition(rect);
            if (Math.Abs(position.X - point.X) <= tolerance && Math.Abs(position.Y - point.Y) <= tolerance) return handle;
        }
        return null;
    }

    /// <summary>
    /// Rectangle between two drag points. <paramref name="square"/> constrains to the largest square
    /// that fits the drag; <paramref name="fromCenter"/> treats the origin as the centre (Alt held).
    /// </summary>
    public static RectD SelectionRect(PointD origin, PointD current, bool square = false, bool fromCenter = false)
    {
        var dx = current.X - origin.X;
        var dy = current.Y - origin.Y;
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        if (fromCenter)
            return new RectD(origin.X - Math.Abs(dx), origin.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2);
        return new RectD(Math.Min(origin.X, origin.X + dx), Math.Min(origin.Y, origin.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }

    /// <summary>
    /// A full-image crop cannot move, so an interior drag must start a new selection. Dragging outside
    /// an existing crop replaces it as well.
    /// </summary>
    public static bool StartsNewCropSelection(PointD point, RectD draft, RectD within) =>
        within.CgContainsPoint(point) && (draft.Standardized() == within.Standardized() || !draft.CgContainsPoint(point));

    public static bool IsClick(PointD origin, PointD end) =>
        Math.Abs(end.X - origin.X) < ClickDragThreshold && Math.Abs(end.Y - origin.Y) < ClickDragThreshold;

    /// <summary>
    /// The rectangle after dragging <paramref name="handle"/> to <paramref name="point"/>. Width and
    /// height stay non-negative by swapping edges when a drag crosses over.
    /// </summary>
    public static RectD ResizedRect(RectD rect, SelectionHandle handle, PointD point)
    {
        double minX = rect.X, minY = rect.Y, maxX = rect.Right, maxY = rect.Bottom;
        switch (handle)
        {
            case SelectionHandle.TopLeft: minX = point.X; minY = point.Y; break;
            case SelectionHandle.Top: minY = point.Y; break;
            case SelectionHandle.TopRight: maxX = point.X; minY = point.Y; break;
            case SelectionHandle.Right: maxX = point.X; break;
            case SelectionHandle.BottomRight: maxX = point.X; maxY = point.Y; break;
            case SelectionHandle.Bottom: maxY = point.Y; break;
            case SelectionHandle.BottomLeft: minX = point.X; maxY = point.Y; break;
            case SelectionHandle.Left: minX = point.X; break;
        }
        return new RectD(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Abs(maxX - minX), Math.Abs(maxY - minY));
    }

    /// <summary>
    /// Moves an existing selection without resizing it, stopping cleanly at the edges of
    /// <paramref name="bounds"/> instead of shrinking the rectangle through intersection.
    /// </summary>
    public static RectD MovedRect(RectD rect, PointD delta, RectD bounds)
    {
        if (!(rect.Width <= bounds.Width && rect.Height <= bounds.Height)) return rect.Clamp(bounds);
        var x = Math.Min(Math.Max(rect.X + delta.X, bounds.X), bounds.Right - rect.Width);
        var y = Math.Min(Math.Max(rect.Y + delta.Y, bounds.Y), bounds.Bottom - rect.Height);
        return new RectD(x, y, rect.Width, rect.Height);
    }

    /// <summary>
    /// The pixel rectangle a crop draft stands for. Rounding both edges to the nearest boundary leaves
    /// an already snapped rectangle the same size under a move, because the two edges carry the same
    /// fraction. Swift's <c>rounded()</c> is half away from zero.
    /// </summary>
    public static RectD PixelSnappedCropRect(RectD rect, RectD within)
    {
        var minX = Math.Round(rect.X, MidpointRounding.AwayFromZero);
        var minY = Math.Round(rect.Y, MidpointRounding.AwayFromZero);
        var maxX = Math.Round(rect.Right, MidpointRounding.AwayFromZero);
        var maxY = Math.Round(rect.Bottom, MidpointRounding.AwayFromZero);
        return new RectD(minX, minY, maxX - minX, maxY - minY).Clamp(within);
    }

    // ---- Window picking --------------------------------------------------------------------------

    /// <summary>A window the click can pick. <c>Frame</c> is in the overlay's own view coordinates.</summary>
    public readonly record struct PickableWindow(uint WindowId, RectD Frame);

    /// <summary>
    /// The first window whose frame contains the point; the list keeps the window manager's
    /// front-to-back order, so the visible window wins. Half-open containment, as CGRect.contains.
    /// </summary>
    public static PickableWindow? WindowAt(PointD point, IReadOnlyList<PickableWindow> windows)
    {
        foreach (var window in windows) if (window.Frame.CgContainsPoint(point)) return window;
        return null;
    }

    /// <summary>One entry of the system's on-screen window list, front to back.</summary>
    /// <param name="Layer">0 for an ordinary application window; menus, docks and overlays sit on other layers.</param>
    /// <param name="Alpha">1 when opaque; a window at or below 0.01 is invisible.</param>
    public readonly record struct WindowListEntry(uint Id, int OwnerPid, RectD Frame, int Layer = 0, double Alpha = 1,
                                                  bool IsUntitled = false);

    /// <summary>
    /// The windows a click can pick, front to back: ordinary layer-0 windows that are not another
    /// process's border decoration, not this app's own windows unless allowed, at least 40 points on
    /// each side and not transparent.
    /// </summary>
    public static IReadOnlyList<WindowListEntry> PickableWindows(IReadOnlyList<WindowListEntry> frontToBack, int ownPid,
                                                                 bool hideOwnWindows, IReadOnlyCollection<uint> protectedWindowIds)
    {
        var decorations = ScreenshotCapturePolicy.DecorationWindowIds(
            frontToBack.Select(e => new ScreenshotCapturePolicy.CaptureWindow(e.Id, e.OwnerPid, e.Frame, e.IsUntitled)).ToList());
        var result = new List<WindowListEntry>();
        foreach (var entry in frontToBack)
        {
            if (entry.Layer != 0) continue;
            if (decorations.Contains(entry.Id)) continue;
            if (!ScreenshotCapturePolicy.CanPickWindow(entry.Id, entry.OwnerPid == ownPid, hideOwnWindows, protectedWindowIds)) continue;
            if (entry.Frame.Width < MinimumPickableWindowSide || entry.Frame.Height < MinimumPickableWindowSide) continue;
            if (entry.Alpha <= 0.01) continue;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// The windows of one display as the overlay sees them: frames moved into the display's own view
    /// coordinates (global minus the display origin; both are top-left so no flip) and dropped when they
    /// do not reach the display.
    /// </summary>
    public static IReadOnlyList<PickableWindow> PickableWindowsOnDisplay(IEnumerable<WindowListEntry> windows, RectD screenFrame)
    {
        var view = new RectD(0, 0, screenFrame.Width, screenFrame.Height);
        var result = new List<PickableWindow>();
        foreach (var entry in windows)
        {
            var frame = new RectD(entry.Frame.X - screenFrame.X, entry.Frame.Y - screenFrame.Y, entry.Frame.Width, entry.Frame.Height);
            if (frame.CgIntersects(view)) result.Add(new PickableWindow(entry.Id, frame));
        }
        return result;
    }
}

/// <summary>What releasing the pointer did.</summary>
public enum SelectionReleaseKind
{
    /// <summary>No drag was in progress.</summary>
    None,
    /// <summary>Colour picking: sample the pixel at the pointer.</summary>
    Color,
    /// <summary>A click on a window: capture it (see <see cref="SelectionRelease.Window"/>).</summary>
    Window,
    /// <summary>A click that picked nothing (or window clicks are off); the selection is cleared.</summary>
    ClickIgnored,
    /// <summary>A drag too small to be a region; the selection is cleared.</summary>
    TooSmall,
    /// <summary>A region to capture (see <see cref="SelectionRelease.Region"/>).</summary>
    Region,
}

public readonly record struct SelectionRelease(SelectionReleaseKind Kind, RectD Region = default,
                                               ScreenshotSelection.PickableWindow? Window = null, PointD Point = default);

/// <summary>
/// The pointer side of one display's selection overlay: press, drag (with Shift square, Alt from the
/// centre and Space to move the rectangle under the pointer), release. Feed it view coordinates.
/// </summary>
public sealed class SelectionDragSession
{
    public RectD Selection { get; private set; } = CaptureGeometry.Zero;
    public PointD? DragOrigin { get; private set; }
    public PointD LastDragPoint { get; private set; }
    public bool IsDragging => DragOrigin is not null;

    /// <summary>Held while the person drags: pans the selection instead of resizing it.</summary>
    public bool SpaceIsDown { get; set; }

    public void Press(PointD point)
    {
        DragOrigin = point;
        LastDragPoint = point;
        Selection = CaptureGeometry.Zero;
    }

    /// <param name="bounds">The overlay view's bounds; the selection never leaves it.</param>
    public void Drag(PointD point, bool square, bool fromCenter, bool pickingColor, RectD bounds)
    {
        if (DragOrigin is not { } origin) return;
        if (pickingColor)
        {
            LastDragPoint = point;
            return;
        }
        if (SpaceIsDown && Selection.Width > 0)
        {
            // Space pans the selection instead of resizing it.
            var dx = point.X - LastDragPoint.X;
            var dy = point.Y - LastDragPoint.Y;
            Selection = Selection with { X = Selection.X + dx, Y = Selection.Y + dy };
            DragOrigin = new PointD(origin.X + dx, origin.Y + dy);
        }
        else
        {
            Selection = ScreenshotSelection.SelectionRect(origin, point, square, fromCenter);
        }
        Selection = Selection.Clamp(bounds);
        LastDragPoint = point;
    }

    public SelectionRelease Release(PointD point, bool pickingColor, bool acceptsWindowClick,
                                    IReadOnlyList<ScreenshotSelection.PickableWindow> windows)
    {
        if (DragOrigin is not { } origin) return new SelectionRelease(SelectionReleaseKind.None);
        DragOrigin = null;
        SpaceIsDown = false;

        if (pickingColor)
        {
            Selection = CaptureGeometry.Zero;
            return new SelectionRelease(SelectionReleaseKind.Color, Point: point);
        }
        if (ScreenshotSelection.IsClick(origin, point))
        {
            Selection = CaptureGeometry.Zero;
            if (acceptsWindowClick && ScreenshotSelection.WindowAt(point, windows) is { } target)
                return new SelectionRelease(SelectionReleaseKind.Window, Window: target, Point: point);
            return new SelectionRelease(SelectionReleaseKind.ClickIgnored, Point: point);
        }
        if (!(Selection.Width >= ScreenshotSelection.MinimumRegionSide && Selection.Height >= ScreenshotSelection.MinimumRegionSide))
        {
            Selection = CaptureGeometry.Zero;
            return new SelectionRelease(SelectionReleaseKind.TooSmall);
        }
        return new SelectionRelease(SelectionReleaseKind.Region, Region: Selection);
    }

    /// <summary>Escape or the end of the session.</summary>
    public void Reset()
    {
        Selection = CaptureGeometry.Zero;
        DragOrigin = null;
        SpaceIsDown = false;
    }
}

/// <summary>The last confirmed region, remembered for this app session so R can repeat it.</summary>
public sealed record LastCaptureRegion(string DisplayId, RectD ViewRect);

public sealed class LastRegionStore
{
    public LastCaptureRegion? Last { get; private set; }

    public void Remember(string displayId, RectD viewRect) => Last = new LastCaptureRegion(displayId, viewRect);

    /// <summary>The ghost drawn over the display that owns the stored region, when the preference is on.</summary>
    public RectD? GhostFor(string displayId, bool showLastRegion) =>
        showLastRegion && Last is { } last && last.DisplayId == displayId ? last.ViewRect : null;

    /// <summary>
    /// The region R would repeat: null when nothing is stored, when the display it belongs to is gone
    /// or when colours are being picked. The guide hint and the action read the same answer, so the bar
    /// cannot promise a key that does nothing.
    /// </summary>
    public LastCaptureRegion? RepeatTarget(IReadOnlyCollection<string> availableDisplayIds, bool isPickingColor) =>
        Last is { } last && ScreenshotSupport.OffersRepeatLastRegion(isPickingColor, availableDisplayIds.Contains(last.DisplayId)) ? last : null;
}

/// <summary>The loupe's arrow keys: move the pointer by whole device pixels.</summary>
public enum NudgeKey { Left, Right, Up, Down }

/// <summary>One display for pointer nudging: its frame in logical points and its scale (device pixels per point).</summary>
public readonly record struct NudgeDisplay(RectD Frame, double Scale);

public static class SelectionNudge
{
    /// <summary>
    /// The unit step of an arrow key in screen coordinates (y down).
    /// DEVIATION: upstream's pointer was in Cocoa coordinates where Up is +y; here Up is -y.
    /// </summary>
    public static PointD Direction(NudgeKey key) => key switch
    {
        NudgeKey.Left => new(-1, 0),
        NudgeKey.Right => new(1, 0),
        NudgeKey.Up => new(0, -1),
        _ => new(0, 1),
    };

    /// <summary>
    /// The pointer after an arrow press. It moves one device pixel (ten with Shift) of the display it
    /// is on, may cross onto a neighbouring display, and only stops at the current display's edge when
    /// no display contains the target. Containment is [minX, maxX) x [minY, maxY), as
    /// <see cref="CaptureGeometry.ContainsPoint"/>. Null when the pointer is on no display.
    /// </summary>
    public static PointD? Apply(PointD location, NudgeKey key, bool fast, IReadOnlyList<NudgeDisplay> displays)
    {
        var current = displays.Cast<NudgeDisplay?>().FirstOrDefault(d => d!.Value.Frame.CgContainsPoint(location));
        if (current is not { } display) return null;
        var direction = Direction(key);
        var delta = ScreenshotLoupe.Nudge(direction.X, direction.Y, fast, display.Scale);
        var target = new PointD(location.X + delta.X, location.Y + delta.Y);
        if (displays.Any(d => d.Frame.CgContainsPoint(target))) return target;
        var frame = display.Frame;
        var pixel = 1 / Math.Max(display.Scale, 1);
        return new PointD(Math.Min(Math.Max(target.X, frame.X), frame.Right - pixel),
                          Math.Min(Math.Max(target.Y, frame.Y), frame.Bottom - pixel));
    }
}
