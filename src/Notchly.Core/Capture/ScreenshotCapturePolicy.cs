// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ScreenshotCapturePolicy.swift (all of it) plus
// ScreenshotSupport.attachedCaptureCrop / AlphaCoverage.
//
// Window ids are uint (CGWindowID) and owner ids int (pid_t). On Windows an HWND fits in a uint for
// the purposes of this policy; the caller keeps the id-to-HWND map. Frames are window-manager
// rectangles in logical points with a top-left origin, which is already what upstream's
// "window-server points" are, so nothing is flipped in this file except compositeRect (see there).
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>Decides which of this process's windows a capture has to exclude, and which can be picked.</summary>
public static class ScreenshotCapturePolicy
{
    /// <summary>
    /// The app's own windows one capture has to keep out before the "hide own windows" preference
    /// narrows what is left. Workflow surfaces (selection overlays, countdown and scrolling HUDs,
    /// the quick preview) can never be the subject; content windows (editors, pins) obey the preference.
    /// Recording is exempt from that preference, so it passes <c>honoursVisibilityPreference: false</c>.
    /// </summary>
    public static HashSet<uint> ProtectedWindowIds(IEnumerable<uint> workflowWindowIds,
                                                   IEnumerable<uint> contentWindowIds,
                                                   bool honoursVisibilityPreference)
    {
        var protectedIds = new HashSet<uint>(workflowWindowIds);
        if (!honoursVisibilityPreference) protectedIds.UnionWith(contentWindowIds);
        return protectedIds;
    }

    /// <summary>
    /// <paramref name="islandWindowIds"/> are the island windows that show in captures. The island has
    /// its own preference for that, so "hide own windows" leaves them in, but a caller that protects
    /// them keeps them out.
    /// </summary>
    public static HashSet<uint> ExcludedWindowIds(bool hideOwnWindows,
                                                  IEnumerable<uint> ownWindowIds,
                                                  IEnumerable<uint> protectedWindowIds,
                                                  IEnumerable<uint>? islandWindowIds = null)
    {
        var own = new HashSet<uint>(ownWindowIds);
        var protectedIds = new HashSet<uint>(protectedWindowIds);
        var excluded = hideOwnWindows ? own : new HashSet<uint>(own.Where(protectedIds.Contains));
        var island = new HashSet<uint>(islandWindowIds ?? []);
        island.ExceptWith(protectedIds);
        excluded.ExceptWith(island);
        return excluded;
    }

    /// <summary>
    /// The island windows that show in captures. While the main island offers the capture controls or
    /// shows a capture just taken, it is the tool taking the picture and stays out of it even with the
    /// preference on. Copies on other displays only ever show activities.
    /// </summary>
    public static HashSet<uint> IslandCaptureWindowIds(IEnumerable<uint> islandWindowIds, uint? mainWindowId,
                                                       bool showsInCaptures, bool showsCaptureTool)
    {
        if (!showsInCaptures) return [];
        var ids = new HashSet<uint>(islandWindowIds);
        if (!showsCaptureTool || mainWindowId is not { } main) return ids;
        ids.Remove(main);
        return ids;
    }

    public static bool CanPickWindow(uint windowId, bool isOwnWindow, bool hideOwnWindows,
                                     IEnumerable<uint> protectedWindowIds) =>
        !isOwnWindow || (!hideOwnWindows && !protectedWindowIds.Contains(windowId));

    /// <summary>One on-screen window as the capture decision needs it.</summary>
    /// <param name="IsUntitled">The window list gives it no title. Decorations have none.</param>
    public readonly record struct CaptureWindow(uint Id, int OwnerPid, RectD Frame, bool IsUntitled = false);

    /// <summary>How far past the window it frames a decoration may reach on each side (inclusive).</summary>
    public const double DecorationMarginMin = 1, DecorationMarginMax = 32;

    /// <summary>
    /// Windows another process draws around a window, such as a focus border. Picking one captures
    /// only the painted frame, so a click there has to reach the window it surrounds. A decoration has
    /// no title, sits directly in front of or behind that window and reaches past it by the same small
    /// margin on every side.
    /// </summary>
    public static HashSet<uint> DecorationWindowIds(IReadOnlyList<CaptureWindow> frontToBack)
    {
        var decorations = new HashSet<uint>();
        for (var index = 0; index < frontToBack.Count; index++)
        {
            var window = frontToBack[index];
            if (!window.IsUntitled) continue;
            foreach (var neighbour in new[] { index - 1, index + 1 })
            {
                if (neighbour < 0 || neighbour >= frontToBack.Count) continue;
                if (Frames(window, frontToBack[neighbour])) { decorations.Add(window.Id); break; }
            }
        }
        return decorations;
    }

    private static bool Frames(CaptureWindow decoration, CaptureWindow window)
    {
        if (decoration.OwnerPid == window.OwnerPid) return false;
        var margins = new[]
        {
            window.Frame.X - decoration.Frame.X,
            window.Frame.Y - decoration.Frame.Y,
            decoration.Frame.Right - window.Frame.Right,
            decoration.Frame.Bottom - window.Frame.Bottom,
        };
        var smallest = margins.Min();
        var largest = margins.Max();
        // A point of slack absorbs rounding.
        return smallest >= DecorationMarginMin && smallest <= DecorationMarginMax
            && largest >= DecorationMarginMin && largest <= DecorationMarginMax
            && largest - smallest <= 1;
    }

    /// <summary>
    /// The windows a capture of one clicked window has to draw. The area is the clicked window's own,
    /// so the shot stays the one that was asked for. <c>WindowIds</c> is the clicked window first, then
    /// what sits on it, back to front.
    /// </summary>
    public sealed record AttachedCapturePlan(IReadOnlyList<uint> WindowIds, RectD Bounds)
    {
        public bool Equals(AttachedCapturePlan? other) =>
            other is not null && Bounds == other.Bounds && WindowIds.SequenceEqual(other.WindowIds);

        public override int GetHashCode() => HashCode.Combine(Bounds, WindowIds.Count);
    }

    /// <summary>
    /// A display crop is safe only when that display contains the whole window and no other display
    /// draws part of it. A shared edge contains no pixels. Returns the index of that display, or null.
    /// </summary>
    public static int? DisplayCoveringWindow<TDisplay>(RectD bounds, IReadOnlyList<TDisplay> displays,
                                                       Func<TDisplay, RectD> frame)
    {
        if (bounds.IsEmpty()) return null;
        var hits = new List<int>();
        for (var i = 0; i < displays.Count; i++)
            if (bounds.Intersection(frame(displays[i])) is { } overlap && !overlap.IsEmpty()) hits.Add(i);
        if (hits.Count != 1) return null;
        return frame(displays[hits[0]]).ContainsRect(bounds) ? hits[0] : null;
    }

    /// <summary>
    /// What a sheet, alert or modal dialog stacked on the clicked window adds to its capture. The
    /// relationship is not in the window list, so this first pass finds the shape it has there: same
    /// application, in front of the window, and lying entirely within it. Anything reaching past its
    /// edge is left to the ordinary capture. Null when nothing is attached.
    /// </summary>
    public static AttachedCapturePlan? AttachedCapturePlanFor(CaptureWindow target, IReadOnlyList<CaptureWindow> frontToBack)
    {
        if (!(target.Frame.Width > 0 && target.Frame.Height > 0)) return null;
        var position = -1;
        for (var i = 0; i < frontToBack.Count; i++)
            if (frontToBack[i].Id == target.Id) { position = i; break; }
        if (position < 0) return null;
        var attached = frontToBack.Take(position)
            .Where(c => c.OwnerPid == target.OwnerPid && target.Frame.ContainsRect(c.Frame))
            .ToList();
        if (attached.Count == 0) return null;
        // Back to front, so the clicked window is drawn first and what the app stacked on it lands on
        // top in the order it is shown.
        attached.Reverse();
        return new AttachedCapturePlan([target.Id, .. attached.Select(w => w.Id)], target.Frame);
    }

    /// <summary>
    /// Where one window of an attached plan lands on a canvas drawn at <paramref name="scale"/> pixels
    /// per point over the clicked window's <paramref name="bounds"/>.
    /// DEVIATION: upstream returns the canvas's bottom-left (Core Graphics bitmap) coordinates,
    /// <c>y = (bounds.maxY - frame.maxY) * scale</c>. Notchly bitmaps are top-down like everything
    /// else, so y is measured from the top: <c>(frame.minY - bounds.minY) * scale</c>.
    /// </summary>
    public static RectD CompositeRect(RectD frame, RectD bounds, double scale) =>
        new((frame.X - bounds.X) * scale, (frame.Y - bounds.Y) * scale, frame.Width * scale, frame.Height * scale);

    /// <summary>
    /// Whether a window buffer covers <paramref name="frame"/> whole at <paramref name="scale"/> pixels
    /// per point in both axes, within a pixel of rounding. A buffer clipped at a display edge would be
    /// stretched and shift the dialog on it; such a layer has to be recaptured or drop the composite.
    /// </summary>
    public static bool LayerCoversFrame(int imageWidth, int imageHeight, RectD frame, double scale)
    {
        if (!(scale > 0 && frame.Width > 0 && frame.Height > 0)) return false;
        return Math.Abs(imageWidth - frame.Width * scale) <= 1 && Math.Abs(imageHeight - frame.Height * scale) <= 1;
    }

    /// <summary>The display scale at which a buffer covers <paramref name="frame"/> whole, if any (finest first).</summary>
    public static double? LayerScale(int imageWidth, int imageHeight, RectD frame, IEnumerable<double> candidates)
    {
        foreach (var scale in candidates.OrderByDescending(s => s))
            if (LayerCoversFrame(imageWidth, imageHeight, frame, scale)) return scale;
        return null;
    }

    /// <summary>
    /// How the clicked window's layer of a composite is taken, which fixes the scale every layer, the
    /// canvas and the reported capture share.
    /// </summary>
    public readonly record struct CompositeTargetCapture(bool IsBuffer, double Scale)
    {
        /// <summary>Its window buffer already covers its frame at this scale.</summary>
        public static CompositeTargetCapture Buffer(double scale) => new(true, scale);

        /// <summary>Recapture it through its own filter at this scale.</summary>
        public static CompositeTargetCapture Recapture(double scale) => new(false, scale);
    }

    /// <summary>
    /// The window buffer sets the scale when it covers the frame at some display's scale. Otherwise the
    /// recapture takes the finest one, which need not be the clicked display's.
    /// </summary>
    public static CompositeTargetCapture? CompositeTarget((int Width, int Height)? buffer, RectD frame, IReadOnlyList<double> candidates)
    {
        var list = candidates.ToList();
        if (buffer is { } b && LayerScale(b.Width, b.Height, frame, list) is { } scale)
            return CompositeTargetCapture.Buffer(scale);
        return list.Count == 0 ? null : CompositeTargetCapture.Recapture(list.Max());
    }

    /// <summary>
    /// Narrows a geometric plan to the attached windows the accessibility layer named. A missing answer
    /// (null) leaves geometry alone; an answer with no matches leaves the single-window capture to answer.
    /// </summary>
    public static AttachedCapturePlan? ConfirmedAttachment(AttachedCapturePlan plan, ISet<uint>? confirmedIds)
    {
        if (confirmedIds is null) return plan;
        if (plan.WindowIds.Count == 0) return null;
        var attached = plan.WindowIds.Skip(1).Where(confirmedIds.Contains).ToList();
        if (attached.Count == 0) return null;
        return new AttachedCapturePlan([plan.WindowIds[0], .. attached], plan.Bounds);
    }

    /// <summary>A captured image's alpha, top row first.</summary>
    public sealed record AlphaCoverage(byte[] Alpha, int Width, int Height)
    {
        /// <summary>Total alpha inside <paramref name="rect"/>, in image pixels from the top left.</summary>
        public int Sum(RectD rect)
        {
            if (rect.Integral().Intersection(new RectD(0, 0, Width, Height)) is not { } area || area.IsEmpty()) return 0;
            var total = 0;
            for (var row = (int)area.Y; row < (int)area.Bottom; row++)
            {
                var start = row * Width;
                for (var column = (int)area.X; column < (int)area.Right; column++) total += Alpha[start + column];
            }
            return total;
        }
    }

    /// <summary>
    /// Where a display capture of only some windows put them. Some systems draw each window where it
    /// sits on the display; others pack the included windows into the image's top-left corner. The
    /// placement that holds more of them is the one the system used.
    /// </summary>
    public static RectD AttachedCaptureCrop(RectD placed, RectD packed, AlphaCoverage coverage) =>
        coverage.Sum(packed) > coverage.Sum(placed) ? packed : placed;
}
