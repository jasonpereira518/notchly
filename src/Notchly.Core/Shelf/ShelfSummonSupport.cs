// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure parts of upstream Sources/Vorssaint/Services/Shelf/ShelfSupport.swift and the summoning code of ShelfService.swift:
// where the floating card goes, when it hides itself, the docked drop zone and the screen-edge peek, and what counts as a drag worth
// reacting to. Geometry is in physical pixels with y pointing down. Windows differences: the "menu bar" dock is the corner above the
// tray, and a drag has no pasteboard change count, so a content drag is judged by where it started and how it moved.
using Notchly.Core.Windowing;

namespace Notchly.Core.Shelf;

public enum ShelfDockPlacement { Tray, TopCenter }
public enum ScreenSide { None, Left, Right }
public enum ShelfShortcutAction { Toggle, AddSelection }

public static class ShelfSummonSupport
{
    // MARK: The card

    public const double CardWidth = 304, CardPadding = 14, CardCornerRadius = 18, TileAreaHeight = 188;
    public const double ScreenMargin = 8, PointerGap = 16, DragStartDistance = 4;
    public const double AutoHideSeconds = 5, FadeSeconds = 0.22;

    /// <summary>The top-left corner of the card: centred on the pointer, a little below it, and kept inside the work area.</summary>
    public static (double X, double Y) CardOrigin(double pointerX, double pointerY, double width, double height, WindowRect workArea)
    {
        var x = pointerX - width / 2;
        var y = pointerY + PointerGap;
        x = Math.Clamp(x, workArea.X + ScreenMargin, Math.Max(workArea.X + ScreenMargin, workArea.Right - width - ScreenMargin));
        y = Math.Clamp(y, workArea.Y + ScreenMargin, Math.Max(workArea.Y + ScreenMargin, workArea.Bottom - height - ScreenMargin));
        return (x, y);
    }

    /// <summary>
    /// Only an empty shelf hides itself, and only when nothing holds it open: pinned, the pointer inside, a drop targeted, a menu or
    /// drag in progress, or an edge peek.
    /// </summary>
    public static bool ShouldAutoHide(bool isEmpty, bool pinned, bool pointerInside, bool dropTargeted, bool interacting, bool edgePeek) =>
        isEmpty && !pinned && !pointerInside && !dropTargeted && !interacting && !edgePeek;

    /// <summary>A tile moves from the card to a drag after more than a few pixels.</summary>
    public static bool StartsDrag(double dx, double dy) => Math.Abs(dx) > DragStartDistance || Math.Abs(dy) > DragStartDistance;

    // MARK: Which drags

    public const double ContentDragMinMove = 8;

    /// <summary>
    /// Whether a press-and-move is dragging content: it began on the body of a window (not a title bar or a border), the window is not
    /// being moved or sized, and the pointer has moved a little.
    /// </summary>
    public static bool IsContentDrag(bool startedOnCaptionOrBorder, bool windowMoveOrSize, double movedDistance) =>
        !startedOnCaptionOrBorder && !windowMoveOrSize && movedDistance >= ContentDragMinMove;

    /// <summary>Automatic summoning (shake, dock, edge) skips programs on the exclusion list; the hotkey and the button never do.</summary>
    public static bool AutoOpenAllowed(string? sourceExe, IEnumerable<string> exclusions) => !ClipboardIgnoredLike(sourceExe, exclusions);

    private static bool ClipboardIgnoredLike(string? exe, IEnumerable<string> list) =>
        !string.IsNullOrEmpty(exe) && list.Any(item => string.Equals(item.Trim(), exe, StringComparison.OrdinalIgnoreCase));

    // MARK: The hotkey

    /// <summary>The hotkey adds the selected Explorer files when that option is on and some are not shelved yet; otherwise it toggles the card.</summary>
    public static ShelfShortcutAction ShortcutAction(bool addsSelection, bool explorerInFront, IReadOnlyList<string> selection, IReadOnlyCollection<string> shelved) =>
        addsSelection && explorerInFront && NewSelection(selection, shelved).Count > 0 ? ShelfShortcutAction.AddSelection : ShelfShortcutAction.Toggle;

    public static IReadOnlyList<string> NewSelection(IReadOnlyList<string> selection, IReadOnlyCollection<string> shelved) =>
        selection.Where(p => !shelved.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

    // MARK: The docked drop zone

    public const double DockDwell = 0.15, DockTriggerMargin = 16, DockRetreatMargin = 32;
    public const double DockWidth = 180, DockHeight = 40, DockEdgeGap = 8, DockCornerGap = 12;
    public const double DragEndGrace = 0.15, CaughtTick = 0.9;

    public static ShelfDockPlacement ParsePlacement(string? stored) => stored == "topCenter" ? ShelfDockPlacement.TopCenter : ShelfDockPlacement.Tray;

    /// <summary>With the island on, the top centre belongs to it: the dock goes back to the tray corner.</summary>
    public static ShelfDockPlacement EffectivePlacement(ShelfDockPlacement placement, bool islandEnabled) =>
        islandEnabled ? ShelfDockPlacement.Tray : placement;

    /// <summary>Where the pill sits: above the tray at the bottom right of the work area, or under the top edge at its centre. The scale is the display's.</summary>
    public static WindowRect DockFrame(ShelfDockPlacement placement, WindowRect workArea, double scale = 1)
    {
        var width = DockWidth * scale;
        var height = DockHeight * scale;
        return placement == ShelfDockPlacement.TopCenter
            ? new WindowRect(workArea.X + (workArea.Width - width) / 2, workArea.Y + DockEdgeGap * scale, width, height)
            : new WindowRect(workArea.Right - DockCornerGap * scale - width, workArea.Bottom - DockCornerGap * scale - height, width, height);
    }

    /// <summary>Whether the pointer is in the frame grown by a margin (the dwell starts inside the trigger margin; it ends past the retreat margin).</summary>
    public static bool InZone(WindowRect frame, double x, double y, double margin) =>
        x >= frame.X - margin && x <= frame.Right + margin && y >= frame.Y - margin && y <= frame.Bottom + margin;

    // MARK: The screen-edge peek

    public const double EdgeTriggerDistance = 200, EdgeRetreatDistance = 330, EdgeDwell = 0.15;

    /// <summary>
    /// Which side edge the pointer is near. Only the left and right edges count; an edge that touches another screen (the seam between
    /// two) or has the taskbar along it (the work area is narrower than the screen on that side) never does.
    /// </summary>
    public static ScreenSide EdgeSide(double x, double y, WindowRect bounds, WindowRect workArea, IReadOnlyList<WindowRect> otherScreens, double distance = EdgeTriggerDistance)
    {
        if (y < bounds.Y || y > bounds.Bottom) return ScreenSide.None;
        bool touchesAnother(bool left) => otherScreens.Any(o => y >= o.Y && y <= o.Bottom
            && (left ? Math.Abs(o.Right - bounds.X) <= 1 : Math.Abs(o.X - bounds.Right) <= 1));
        if (x >= bounds.X && x - bounds.X <= distance && workArea.X <= bounds.X && !touchesAnother(true)) return ScreenSide.Left;
        if (x <= bounds.Right && bounds.Right - x <= distance && workArea.Right >= bounds.Right && !touchesAnother(false)) return ScreenSide.Right;
        return ScreenSide.None;
    }

    /// <summary>The card as it peeks in: a third of its width inside the edge, centred vertically.</summary>
    public static (double X, double Y) PeekOrigin(ScreenSide side, WindowRect workArea, double width, double height)
    {
        var y = workArea.Y + (workArea.Height - height) / 2;
        return side == ScreenSide.Left ? (workArea.X - width * 2 / 3, y) : (workArea.Right - width / 3, y);
    }

    /// <summary>The card slid fully on, after a drop onto the peek.</summary>
    public static (double X, double Y) SettledOrigin(ScreenSide side, WindowRect workArea, double width, double height)
    {
        var y = workArea.Y + (workArea.Height - height) / 2;
        return side == ScreenSide.Left ? (workArea.X + ScreenMargin, y) : (workArea.Right - width - ScreenMargin, y);
    }

    // MARK: Dwell

    /// <summary>True once the pointer has stayed in a zone for the dwell; leaving the zone starts it over.</summary>
    public sealed class DwellTracker(double dwellSeconds)
    {
        private double? _enteredAt;

        public void Reset() => _enteredAt = null;

        public bool Update(bool inside, double now)
        {
            if (!inside) { _enteredAt = null; return false; }
            _enteredAt ??= now;
            return now - _enteredAt.Value >= dwellSeconds;
        }
    }
}
