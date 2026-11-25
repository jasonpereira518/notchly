// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/WindowLayout/WindowLayoutSupport.swift: the layout actions, the geometry that turns
// one into a rectangle (gaps, margins, anchoring), the rules for moving between displays and the per-window history that
// Restore uses. Windows differences: coordinates are pixels with y pointing down (upstream's AppKit points up), so the
// top and bottom rows swap; and the shortcuts use Ctrl+Alt with Win in place of upstream's Command.
using System.Globalization;

namespace Notchly.Core.Windowing;

/// <summary>A rectangle in pixels, y pointing down.</summary>
public readonly record struct WindowRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(Notchly.Core.Notch.PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    /// <summary>The smallest whole-pixel rectangle that contains this one (origin rounded down, far edge up).</summary>
    public WindowRect Integral()
    {
        var x = Math.Floor(X);
        var y = Math.Floor(Y);
        return new WindowRect(x, y, Math.Ceiling(Right) - x, Math.Ceiling(Bottom) - y);
    }

    public WindowRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public double OverlapArea(WindowRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public bool NearlyEquals(WindowRect other, double tolerance) =>
        Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance
        && Math.Abs(Width - other.Width) <= tolerance && Math.Abs(Height - other.Height) <= tolerance;
}

public enum WindowLayoutAction
{
    LeftHalf, RightHalf, TopHalf, BottomHalf, CenterHalf,
    LeftThird, CenterThird, RightThird, LeftTwoThirds, RightTwoThirds, CenterTwoThirds,
    TopThird, MiddleThird, BottomThird, TopTwoThirds, BottomTwoThirds,
    TopQuarter, UpperMiddleQuarter, LowerMiddleQuarter, BottomQuarter,
    LeftQuarter, LeftMiddleQuarter, RightMiddleQuarter, RightQuarter,
    TopLeftSixth, TopCenterSixth, TopRightSixth, BottomLeftSixth, BottomCenterSixth, BottomRightSixth,
    TopLeft, TopRight, BottomLeft, BottomRight,
    Maximize, MarginMaximize, FullScreen, Center, PreviousDisplay, NextDisplay, Restore,
}

public enum DisplayDirection { Left, Right, Up, Down }

public static class WindowLayoutSupport
{
    public const double FrameTolerance = 8;
    public const int HistoryLimit = 20;
    public const double MinimumSpan = 80;
    public const double DefaultMarginPercent = 5;
    public static readonly int[] GapPresets = [0, 8, 16, 32, 64, 128];

    /// <summary>The name an action is stored under (upstream's raw value): "leftHalf".</summary>
    public static string RawName(WindowLayoutAction action) => char.ToLowerInvariant(action.ToString()[0]) + action.ToString()[1..];

    public static WindowLayoutAction? ParseAction(string? raw) =>
        Enum.GetValues<WindowLayoutAction>().Cast<WindowLayoutAction?>().FirstOrDefault(a => string.Equals(RawName(a!.Value), raw, StringComparison.Ordinal));

    /// <summary>The key a per-action shortcut is stored under.</summary>
    public static string ShortcutKey(WindowLayoutAction action) => "windowLayoutShortcut" + action;

    // MARK: Shortcut defaults (Ctrl+Alt, upstream's Control+Option)

    private const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;

    /// <summary>The default shortcut of an action in storage form, or "none" for the ones that ship unbound.</summary>
    public static string DefaultShortcut(WindowLayoutAction action) => action switch
    {
        WindowLayoutAction.LeftHalf => Chord(Left),
        WindowLayoutAction.RightHalf => Chord(Right),
        WindowLayoutAction.TopHalf => Chord(Up),
        WindowLayoutAction.BottomHalf => Chord(Down),
        WindowLayoutAction.TopLeft => Chord('U'),
        WindowLayoutAction.TopRight => Chord('I'),
        WindowLayoutAction.BottomLeft => Chord('J'),
        WindowLayoutAction.BottomRight => Chord('K'),
        WindowLayoutAction.Maximize => Chord(0x0D),
        WindowLayoutAction.Center => Chord('C'),
        WindowLayoutAction.Restore => Chord('R'),
        WindowLayoutAction.LeftThird => Chord('D'),
        WindowLayoutAction.CenterThird => Chord('F'),
        WindowLayoutAction.RightThird => Chord('G'),
        WindowLayoutAction.LeftTwoThirds => Chord('E'),
        WindowLayoutAction.RightTwoThirds => Chord('T'),
        WindowLayoutAction.NextDisplay => "control+alt+win:" + Right.ToString(CultureInfo.InvariantCulture),
        _ => "none",
    };

    private static string Chord(int vk) => "control+alt:" + vk.ToString(CultureInfo.InvariantCulture);

    // MARK: Sanitizers

    /// <summary>The margin for "maximize with margin": 0…25 percent; anything unreadable is the default.</summary>
    public static double SanitizedMargin(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0, 25) : DefaultMarginPercent;

    /// <summary>A gap is one of the presets; anything else is none.</summary>
    public static int SanitizedGap(int points) => GapPresets.Contains(points) ? points : 0;

    // MARK: Geometry

    /// <summary>The screen's frame pulled in by the screen gap, never leaving less than 80 pixels on an axis.</summary>
    public static WindowRect ScreenGapFrame(WindowRect visible, double screenGap)
    {
        if (screenGap <= 0) return visible;
        var dx = Math.Min(screenGap, Math.Max(0, (visible.Width - MinimumSpan) / 2));
        var dy = Math.Min(screenGap, Math.Max(0, (visible.Height - MinimumSpan) / 2));
        return visible.Inset(dx, dy);
    }

    private static bool IgnoresScreenGap(WindowLayoutAction a) =>
        a is WindowLayoutAction.MarginMaximize or WindowLayoutAction.Center or WindowLayoutAction.Restore or WindowLayoutAction.FullScreen
        or WindowLayoutAction.PreviousDisplay or WindowLayoutAction.NextDisplay;

    private static bool IgnoresWindowGap(WindowLayoutAction a) => IgnoresScreenGap(a) || a == WindowLayoutAction.Maximize;

    /// <summary>
    /// The rectangle an action asks for inside a display's visible frame. The screen gap is taken off the frame first, then
    /// half the window gap off every edge that is not on the frame, so two neighbours end up one gap apart.
    /// </summary>
    public static WindowRect Rect(WindowLayoutAction action, WindowRect current, WindowRect visible,
                                  double windowGap = 0, double screenGap = 0, double marginPercent = DefaultMarginPercent)
    {
        switch (action)
        {
            case WindowLayoutAction.MarginMaximize:
            {
                var f = SanitizedMargin(marginPercent) / 100;
                return visible.Inset(visible.Width * f, visible.Height * f).Integral();
            }
            case WindowLayoutAction.Center:
            {
                var width = Math.Min(current.Width, visible.Width);
                var height = Math.Min(current.Height, visible.Height);
                return new WindowRect(visible.X + (visible.Width - width) / 2, visible.Y + (visible.Height - height) / 2, width, height).Integral();
            }
            case WindowLayoutAction.Restore or WindowLayoutAction.FullScreen or WindowLayoutAction.PreviousDisplay or WindowLayoutAction.NextDisplay:
                return current;
        }
        var frame = IgnoresScreenGap(action) ? visible : ScreenGapFrame(visible, screenGap);
        var rect = Fraction(action, frame);
        if (windowGap > 0 && !IgnoresWindowGap(action)) rect = ShaveForWindowGap(rect, frame, windowGap);
        return rect.Integral();
    }

    /// <summary>The rectangle an action asks for inside a frame, with no gaps and no rounding (for previews and fractions).</summary>
    public static WindowRect Proportions(WindowLayoutAction action, WindowRect frame) => Fraction(action, frame);

    private static WindowRect Fraction(WindowLayoutAction action, WindowRect f)
    {
        var w = f.Width;
        var h = f.Height;
        double x(double fraction) => f.X + w * fraction;
        double y(double fraction) => f.Y + h * fraction;
        return action switch
        {
            WindowLayoutAction.Maximize => f,
            WindowLayoutAction.LeftHalf => new(f.X, f.Y, w / 2, h),
            WindowLayoutAction.RightHalf => new(x(0.5), f.Y, w / 2, h),
            WindowLayoutAction.TopHalf => new(f.X, f.Y, w, h / 2),
            WindowLayoutAction.BottomHalf => new(f.X, y(0.5), w, h / 2),
            WindowLayoutAction.CenterHalf => new(f.MidX - w / 4, f.Y, w / 2, h),
            WindowLayoutAction.LeftThird => new(f.X, f.Y, w / 3, h),
            WindowLayoutAction.CenterThird => new(x(1.0 / 3), f.Y, w / 3, h),
            WindowLayoutAction.RightThird => new(f.Right - w / 3, f.Y, w / 3, h),
            WindowLayoutAction.LeftTwoThirds => new(f.X, f.Y, w * 2 / 3, h),
            WindowLayoutAction.RightTwoThirds => new(f.Right - w * 2 / 3, f.Y, w * 2 / 3, h),
            WindowLayoutAction.CenterTwoThirds => new(f.MidX - w / 3, f.Y, w * 2 / 3, h),
            WindowLayoutAction.TopThird => new(f.X, f.Y, w, h / 3),
            WindowLayoutAction.MiddleThird => new(f.X, y(1.0 / 3), w, h / 3),
            WindowLayoutAction.BottomThird => new(f.X, f.Bottom - h / 3, w, h / 3),
            WindowLayoutAction.TopTwoThirds => new(f.X, f.Y, w, h * 2 / 3),
            WindowLayoutAction.BottomTwoThirds => new(f.X, f.Bottom - h * 2 / 3, w, h * 2 / 3),
            WindowLayoutAction.TopQuarter => new(f.X, f.Y, w, h / 4),
            WindowLayoutAction.UpperMiddleQuarter => new(f.X, y(0.25), w, h / 4),
            WindowLayoutAction.LowerMiddleQuarter => new(f.X, y(0.5), w, h / 4),
            WindowLayoutAction.BottomQuarter => new(f.X, f.Bottom - h / 4, w, h / 4),
            WindowLayoutAction.LeftQuarter => new(f.X, f.Y, w / 4, h),
            WindowLayoutAction.LeftMiddleQuarter => new(x(0.25), f.Y, w / 4, h),
            WindowLayoutAction.RightMiddleQuarter => new(x(0.5), f.Y, w / 4, h),
            WindowLayoutAction.RightQuarter => new(f.Right - w / 4, f.Y, w / 4, h),
            WindowLayoutAction.TopLeftSixth => new(f.X, f.Y, w / 3, h / 2),
            WindowLayoutAction.TopCenterSixth => new(x(1.0 / 3), f.Y, w / 3, h / 2),
            WindowLayoutAction.TopRightSixth => new(f.Right - w / 3, f.Y, w / 3, h / 2),
            WindowLayoutAction.BottomLeftSixth => new(f.X, y(0.5), w / 3, h / 2),
            WindowLayoutAction.BottomCenterSixth => new(x(1.0 / 3), y(0.5), w / 3, h / 2),
            WindowLayoutAction.BottomRightSixth => new(f.Right - w / 3, y(0.5), w / 3, h / 2),
            WindowLayoutAction.TopLeft => new(f.X, f.Y, w / 2, h / 2),
            WindowLayoutAction.TopRight => new(x(0.5), f.Y, w / 2, h / 2),
            WindowLayoutAction.BottomLeft => new(f.X, y(0.5), w / 2, h / 2),
            WindowLayoutAction.BottomRight => new(x(0.5), y(0.5), w / 2, h / 2),
            _ => f,
        };
    }

    /// <summary>Half a gap off every side that is not flush with the frame (more than a pixel away), never below one pixel.</summary>
    private static WindowRect ShaveForWindowGap(WindowRect rect, WindowRect frame, double gap)
    {
        var half = gap / 2;
        var left = Math.Abs(rect.X - frame.X) > 1 ? half : 0;
        var top = Math.Abs(rect.Y - frame.Y) > 1 ? half : 0;
        var right = Math.Abs(rect.Right - frame.Right) > 1 ? half : 0;
        var bottom = Math.Abs(rect.Bottom - frame.Bottom) > 1 ? half : 0;
        return new WindowRect(rect.X + left, rect.Y + top, Math.Max(1, rect.Width - left - right), Math.Max(1, rect.Height - top - bottom));
    }

    // MARK: Apps that refuse a size

    /// <summary>
    /// When an app keeps a larger size than asked for, the window stays against the edge the action was meant for:
    /// left and top anchors keep their origin, right and bottom anchors keep their far edge, and a strip in the middle is
    /// centred on its own centre.
    /// </summary>
    public static WindowRect Anchored(WindowLayoutAction action, WindowRect requested, WindowRect visible, double actualWidth, double actualHeight)
    {
        var x = AnchorX(action) switch
        {
            Anchor.Start => requested.X,
            Anchor.End => requested.Right - actualWidth,
            _ => requested.MidX - actualWidth / 2,
        };
        var y = AnchorY(action) switch
        {
            Anchor.Start => requested.Y,
            Anchor.End => requested.Bottom - actualHeight,
            _ => requested.MidY - actualHeight / 2,
        };
        return new WindowRect(x, y, actualWidth, actualHeight);
    }

    private enum Anchor { Start, Middle, End }

    private static Anchor AnchorX(WindowLayoutAction a) => a switch
    {
        WindowLayoutAction.RightHalf or WindowLayoutAction.RightThird or WindowLayoutAction.RightTwoThirds or WindowLayoutAction.RightQuarter
            or WindowLayoutAction.TopRight or WindowLayoutAction.BottomRight or WindowLayoutAction.TopRightSixth or WindowLayoutAction.BottomRightSixth => Anchor.End,
        WindowLayoutAction.CenterHalf or WindowLayoutAction.CenterThird or WindowLayoutAction.CenterTwoThirds or WindowLayoutAction.LeftMiddleQuarter
            or WindowLayoutAction.RightMiddleQuarter or WindowLayoutAction.TopCenterSixth or WindowLayoutAction.BottomCenterSixth => Anchor.Middle,
        _ => Anchor.Start,
    };

    private static Anchor AnchorY(WindowLayoutAction a) => a switch
    {
        WindowLayoutAction.BottomHalf or WindowLayoutAction.BottomThird or WindowLayoutAction.BottomTwoThirds or WindowLayoutAction.BottomQuarter
            or WindowLayoutAction.BottomLeft or WindowLayoutAction.BottomRight or WindowLayoutAction.BottomLeftSixth or WindowLayoutAction.BottomCenterSixth
            or WindowLayoutAction.BottomRightSixth => Anchor.End,
        WindowLayoutAction.MiddleThird or WindowLayoutAction.UpperMiddleQuarter or WindowLayoutAction.LowerMiddleQuarter => Anchor.Middle,
        _ => Anchor.Start,
    };

    /// <summary>The window ended up where the action wanted it: more than 80 pixels each way and mostly over the requested rectangle.</summary>
    public static bool Accepts(WindowLayoutAction action, WindowRect requested, WindowRect actual)
    {
        if (actual.Width <= MinimumSpan || actual.Height <= MinimumSpan) return false;
        var needed = action is WindowLayoutAction.PreviousDisplay or WindowLayoutAction.NextDisplay ? 0.72 : 0.45;
        var smaller = Math.Min(requested.Width * requested.Height, actual.Width * actual.Height);
        return smaller > 0 && requested.OverlapArea(actual) / smaller > needed;
    }

    // MARK: Pressing a direction again

    /// <summary>The next step of the directional keys: the half in that direction, then the next display, or thirds in a cycle.</summary>
    public readonly record struct DirectionalStep(WindowLayoutAction? Action, DisplayDirection? CrossTo);

    /// <summary>
    /// Pressing the same direction twice goes on: left and right cross to the next display (landing on the opposite half),
    /// or with the thirds cycle on, go half, two thirds, third and back; up goes to a maximized window; down to the display below.
    /// </summary>
    public static DirectionalStep NextDirectional(DisplayDirection key, WindowLayoutAction? last, bool lastStillApplies, bool sideRepeatCyclesThirds, bool hasNeighbour)
    {
        var half = key switch
        {
            DisplayDirection.Left => WindowLayoutAction.LeftHalf,
            DisplayDirection.Right => WindowLayoutAction.RightHalf,
            DisplayDirection.Up => WindowLayoutAction.TopHalf,
            _ => WindowLayoutAction.BottomHalf,
        };
        var sideKey = key is DisplayDirection.Left or DisplayDirection.Right;
        if (sideKey && sideRepeatCyclesThirds)
        {
            var twoThirds = key == DisplayDirection.Left ? WindowLayoutAction.LeftTwoThirds : WindowLayoutAction.RightTwoThirds;
            var third = key == DisplayDirection.Left ? WindowLayoutAction.LeftThird : WindowLayoutAction.RightThird;
            if (!lastStillApplies) return new(half, null);
            return last == half ? new(twoThirds, null) : last == twoThirds ? new(third, null) : new(half, null);
        }
        if (!lastStillApplies || last != half) return new(half, null);
        if (key == DisplayDirection.Up && !hasNeighbour) return new(WindowLayoutAction.Maximize, null);
        return hasNeighbour ? new(null, key) : new(half, null);
    }

    /// <summary>The half a window lands on when it crosses to another display in a direction.</summary>
    public static WindowLayoutAction OppositeHalf(DisplayDirection crossed) => crossed switch
    {
        DisplayDirection.Left => WindowLayoutAction.RightHalf,
        DisplayDirection.Right => WindowLayoutAction.LeftHalf,
        DisplayDirection.Up => WindowLayoutAction.BottomHalf,
        _ => WindowLayoutAction.TopHalf,
    };

    // MARK: Displays

    /// <summary>
    /// The display next to this one in a direction: left and right need a strictly lower or higher left edge, up and down a
    /// display wholly beyond the edge. Nearest edge wins, then the closest centre on the other axis, then the lowest index.
    /// </summary>
    public static int? NeighbourIndex(IReadOnlyList<WindowRect> displays, int current, DisplayDirection direction)
    {
        if (current < 0 || current >= displays.Count) return null;
        var here = displays[current];
        var best = (Index: -1, Edge: double.MaxValue, Center: double.MaxValue);
        for (var i = 0; i < displays.Count; i++)
        {
            if (i == current) continue;
            var d = displays[i];
            double edge, center;
            switch (direction)
            {
                case DisplayDirection.Left when d.X < here.X: edge = here.X - d.Right; center = Math.Abs(d.MidY - here.MidY); break;
                case DisplayDirection.Right when d.X > here.X: edge = d.X - here.Right; center = Math.Abs(d.MidY - here.MidY); break;
                case DisplayDirection.Up when d.Bottom <= here.Y + 0.5: edge = here.Y - d.Bottom; center = Math.Abs(d.MidX - here.MidX); break;
                case DisplayDirection.Down when d.Y >= here.Bottom - 0.5: edge = d.Y - here.Bottom; center = Math.Abs(d.MidX - here.MidX); break;
                default: continue;
            }
            edge = Math.Abs(edge);
            if (edge < best.Edge || edge == best.Edge && center < best.Center) best = (i, edge, center);
        }
        return best.Index >= 0 ? best.Index : null;
    }

    /// <summary>The next or previous display in reading order (left edge, then top, then size), wrapping; none with one display.</summary>
    public static int? AdjacentIndex(IReadOnlyList<WindowRect> displays, int current, bool forward)
    {
        if (displays.Count < 2) return null;
        var order = displays.Select((d, i) => (d, i)).OrderBy(p => p.d.X).ThenBy(p => p.d.Y).ThenBy(p => p.d.Width).ThenBy(p => p.d.Height).ThenBy(p => p.i).Select(p => p.i).ToList();
        var at = order.IndexOf(current);
        if (at < 0) return null;
        return order[(at + (forward ? 1 : order.Count - 1)) % order.Count];
    }

    /// <summary>
    /// Where a window goes on another display: one that filled its display fills the new one; any other keeps its size (shrunk
    /// to fit) and its distance from the top and from the left edge, or from the right edge when it was flush right.
    /// </summary>
    public static WindowRect RectForDisplay(WindowRect window, WindowRect source, WindowRect destination)
    {
        var fillsSource = window.Width >= source.Width - FrameTolerance && window.Height >= source.Height - FrameTolerance;
        var oversized = window.Width > destination.Width && window.Height > destination.Height;
        if (fillsSource || oversized) return destination;
        var width = Math.Min(window.Width, destination.Width);
        var height = Math.Min(window.Height, destination.Height);
        var leftInset = window.X - source.X;
        var rightInset = source.Right - window.Right;
        var x = rightInset <= FrameTolerance && rightInset < leftInset
            ? destination.Right - width - Math.Max(0, rightInset)
            : destination.X + Math.Max(0, leftInset);
        var y = destination.Y + Math.Max(0, window.Y - source.Y);
        x = Math.Clamp(x, destination.X, Math.Max(destination.X, destination.Right - width));
        y = Math.Clamp(y, destination.Y, Math.Max(destination.Y, destination.Bottom - height));
        return new WindowRect(x, y, width, height).Integral();
    }

    // MARK: History

    /// <summary>The frames a window had before layouts moved it, most recent last, for Restore.</summary>
    public sealed class History
    {
        private readonly Dictionary<string, List<WindowRect>> _frames = [];

        public void Push(string window, WindowRect frame)
        {
            if (!_frames.TryGetValue(window, out var list)) _frames[window] = list = [];
            if (list.Count > 0 && list[^1] == frame) return;
            list.Add(frame);
            if (list.Count > HistoryLimit) list.RemoveAt(0);
        }

        /// <summary>The latest frame that is not (within a pixel) where the window is now, removing it.</summary>
        public WindowRect? PopPrevious(string window, WindowRect current)
        {
            if (!_frames.TryGetValue(window, out var list)) return null;
            while (list.Count > 0)
            {
                var frame = list[^1];
                list.RemoveAt(list.Count - 1);
                if (!frame.NearlyEquals(current, 1)) return frame;
            }
            return null;
        }

        /// <summary>A layout that failed to apply forgets the frame it saved.</summary>
        public void DiscardLatest(string window)
        {
            if (_frames.TryGetValue(window, out var list) && list.Count > 0) list.RemoveAt(list.Count - 1);
        }

        public int Count(string window) => _frames.TryGetValue(window, out var list) ? list.Count : 0;

        /// <summary>Forgets windows that are gone.</summary>
        public void RemoveStale(IReadOnlySet<string> live)
        {
            foreach (var key in _frames.Keys.Where(k => !live.Contains(k)).ToList()) _frames.Remove(key);
        }
    }
}
