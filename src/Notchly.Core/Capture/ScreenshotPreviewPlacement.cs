// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the "Quick preview placement" and "Editor layout" sections of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift.
//
// DEVIATION (axis): upstream works in Cocoa points, origin bottom-left, y up. Notchly uses a
// top-left origin with y down, so every "bottom" is a larger y. The corner and automatic placement
// below are the mirror images of upstream's; the numbers in the tests are mirrored accordingly.
// DEVIATION (overflow): a preview taller than the usable area is top-aligned (its top edge stays
// visible); upstream's mirror image would have been bottom-aligned.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>One display for placement: its frame and its work area (the frame minus the taskbar), both logical points, top-left origin.</summary>
public readonly record struct PlacementScreen(RectD Frame, RectD VisibleFrame);

public static class ScreenshotPreviewPlacement
{
    public const double AutomaticInset = 10, CornerInset = 16, AnchorGap = 14;

    /// <summary>
    /// Picks the display containing most of the capture. The pointer breaks an exact tie and is also
    /// the fallback when a display was disconnected or rearranged before the preview appears. A pointer
    /// on a display's top row belongs to that display (half-open containment).
    /// </summary>
    public static RectD VisibleFrame(RectD anchor, PointD pointer, IReadOnlyList<PlacementScreen> screens, RectD fallback)
    {
        PlacementScreen? selected = null;
        var selectedArea = 0.0;
        foreach (var screen in screens)
        {
            var area = anchor.OverlapArea(screen.Frame);
            var winsTie = area == selectedArea
                && area > 0
                && screen.Frame.CgContainsPoint(pointer)
                && !(selected is { } current && current.Frame.CgContainsPoint(pointer));
            if (area > selectedArea || winsTie)
            {
                selected = screen;
                selectedArea = area;
            }
        }
        if (selected is { } chosen) return chosen.VisibleFrame;
        foreach (var screen in screens) if (screen.Frame.CgContainsPoint(pointer)) return screen.VisibleFrame;
        return fallback;
    }

    /// <summary>
    /// Places the capture preview beside the selection in automatic mode, or in the chosen display
    /// corner, and clamps it to the visible frame.
    /// </summary>
    public static RectD Frame(SizeD size, RectD anchor, PointD pointer, RectD visibleFrame,
                              QuickPreviewPosition position = QuickPreviewPosition.Automatic)
    {
        var inset = position == QuickPreviewPosition.Automatic ? AutomaticInset : CornerInset;
        var usable = visibleFrame.InsetBy(inset, inset);

        if (position != QuickPreviewPosition.Automatic)
        {
            var right = Math.Max(usable.X, usable.Right - size.Width);
            var top = usable.Y;
            var bottom = Math.Max(usable.Y, usable.Bottom - size.Height);
            return position switch
            {
                QuickPreviewPosition.TopLeft => new RectD(usable.X, top, size.Width, size.Height),
                QuickPreviewPosition.TopRight => new RectD(right, top, size.Width, size.Height),
                QuickPreviewPosition.BottomLeft => new RectD(usable.X, bottom, size.Width, size.Height),
                _ => new RectD(right, bottom, size.Width, size.Height),
            };
        }

        var x = anchor.Right + AnchorGap;
        if (x + size.Width > usable.Right) x = anchor.X - size.Width - AnchorGap;
        if (x < usable.X || x + size.Width > usable.Right) x = pointer.X + AnchorGap;

        var y = anchor.MidY() - size.Height / 2;
        if (y < usable.Y || y + size.Height > usable.Bottom)
        {
            // Prefer the room below the selection, then above it, then centre on the pointer.
            var below = anchor.Bottom + AnchorGap;
            var above = anchor.Y - size.Height - AnchorGap;
            if (below + size.Height <= usable.Bottom) y = below;
            else if (above >= usable.Y) y = above;
            else y = pointer.Y - size.Height / 2;
        }

        x = Math.Min(Math.Max(x, usable.X), Math.Max(usable.X, usable.Right - size.Width));
        y = Math.Min(Math.Max(y, usable.Y), Math.Max(usable.Y, usable.Bottom - size.Height));
        return new RectD(x, y, size.Width, size.Height);
    }

    // ---- Editor layout ---------------------------------------------------------------------------

    /// <summary>A fresh editor should be large enough for its controls and canvas, while fitting compact or rotated displays.</summary>
    public static SizeD EditorMinimumContentSize(SizeD visibleSize) => new(
        Math.Min(visibleSize.Width, Math.Min(980, Math.Max(760, visibleSize.Width * 0.76))),
        Math.Min(visibleSize.Height, Math.Min(680, Math.Max(560, visibleSize.Height * 0.72))));

    /// <summary>
    /// Initial content size for an editor window. Large captures fit inside the display and small
    /// captures still open on a comfortable canvas.
    /// </summary>
    public static SizeD EditorContentSize(SizeD imagePointSize, SizeD visibleSize)
    {
        var chrome = new SizeD(96, 140);
        var minimum = EditorMinimumContentSize(visibleSize);
        var maximum = new SizeD(Math.Max(minimum.Width, visibleSize.Width * 0.90),
                                Math.Max(minimum.Height, visibleSize.Height * 0.88));
        var fit = Math.Min(1, Math.Min((maximum.Width - chrome.Width) / Math.Max(imagePointSize.Width, 1),
                                       (maximum.Height - chrome.Height) / Math.Max(imagePointSize.Height, 1)));
        var preferred = new SizeD(imagePointSize.Width * fit + chrome.Width, imagePointSize.Height * fit + chrome.Height);
        return new SizeD(Math.Min(maximum.Width, Math.Max(minimum.Width, preferred.Width)),
                         Math.Min(maximum.Height, Math.Max(minimum.Height, preferred.Height)));
    }

    /// <summary>The footer under the canvas wraps to a second row when a tool's controls do not fit, so its height changes with the tool.</summary>
    public const double EditorWrappedFooterHeight = 76;

    /// <summary>
    /// The height the tool rail plans for: its column, less what a wrapped footer would take from it,
    /// so picking a tool never reflows the rail under the pointer.
    /// </summary>
    public static double ToolRailHeight(double column, double footer) => Math.Min(column, column + footer - EditorWrappedFooterHeight);

    /// <summary>
    /// A command another process posts as a mouse drag ends (select-to-copy utilities) must not copy and
    /// close the editor under a stroke that has not landed yet. A pressed copy always copies.
    /// </summary>
    public const double EditorPostedCopyGrace = 0.6;

    public static bool EditorIgnoresPostedCopy(long sourceProcessId, long ownProcessId, double now, double lastPointerActivity) =>
        sourceProcessId != 0 && sourceProcessId != ownProcessId && now - lastPointerActivity < EditorPostedCopyGrace;

    /// <summary>
    /// Key events normally carry the editor's window handle, but a menu accelerator can arrive without
    /// one. Then the key window still owns it; an event explicitly owned by another window never does.
    /// </summary>
    public static bool EditorOwnsKeyEvent(long eventWindow, long editorWindow, bool editorIsKey)
    {
        if (editorWindow == 0) return false;
        if (eventWindow == editorWindow) return true;
        return eventWindow == 0 && editorIsKey;
    }
}

/// <summary>
/// How the editor's tool rail fits its height. Regular rows when every tool fits, compact rows when
/// only those fit, and past that the last tools move behind an overflow button.
/// </summary>
public readonly struct ToolRailLayout
{
    /// <summary>The rail's padding and the divider above the gear.</summary>
    public const double ChromeHeight = 15;

    public double RowHeight { get; }
    public double Spacing { get; }

    /// <summary>Tools shown as buttons. The ones after them are in the overflow menu.</summary>
    public int VisibleTools { get; }

    public ToolRailLayout(int toolCount, double height)
    {
        var rowHeight = 29.0;
        var spacing = 2.0;
        var visible = toolCount;
        // Every tool and the gear.
        if (!(ChromeHeight + (toolCount + 1) * (rowHeight + spacing) <= height))
        {
            // Compact rows still hold all of them in the shortest editor.
            rowHeight = 23;
            spacing = 1;
            var rows = (int)Math.Floor((height - ChromeHeight) / (rowHeight + spacing));
            // The overflow button and the gear take the last two rows.
            if (rows < toolCount + 1) visible = Math.Max(0, rows - 2);
        }
        RowHeight = rowHeight;
        Spacing = spacing;
        VisibleTools = visible;
    }
}
