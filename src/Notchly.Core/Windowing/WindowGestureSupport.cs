// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/WindowLayout/WindowGestureSupport.swift: the modifier-drag gesture (move and resize
// with nine regions), the directional pointer ring and the edge-snap zones with their splits, seam rule and pointer
// speed, kept apart from the hooks so tests can pin them down. Windows differences: the default chord is Ctrl+Win (upstream's
// Control+Command), and coordinates are pixels with y pointing down, the same convention upstream's accessibility frames use.
using System.Globalization;
using Notchly.Core.Notch;

namespace Notchly.Core.Windowing;

public static class WindowGestureSupport
{
    // MARK: The chord

    public const string DefaultModifiers = "control+win";
    public const double DragSlop = 6;
    public static readonly SizeD MinimumSize = new(120, 80);

    private static readonly string[] Primary = ["control", "alt", "win"];

    /// <summary>
    /// The modifiers of the gesture from their storage text (control, alt, win, in any order, with upstream's option and command
    /// understood). Shift alone, nothing or unknown words fall back to the default.
    /// </summary>
    public static IReadOnlyList<string> Modifiers(string? stored)
    {
        var parts = (stored ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant() switch { "option" => "alt", "command" => "win", var other => other }).Distinct().ToList();
        if (parts.Count == 0 || parts.Any(p => !Primary.Contains(p) && p != "shift") || !parts.Any(Primary.Contains)) return DefaultModifiers.Split('+');
        parts = parts.Where(Primary.Contains).ToList();
        return Primary.Where(parts.Contains).ToList();
    }

    public static string StorageValue(IEnumerable<string> modifiers) => string.Join("+", Primary.Where(modifiers.Contains));

    /// <summary>Resize is the chord plus Shift.</summary>
    public static IReadOnlyList<string> ResizeChord(IReadOnlyList<string> move) => [.. move, "shift"];

    // MARK: The state machine

    public enum Phase { Idle, Pending, Active }
    public enum Input { ButtonDownWithChord, ButtonDownOther, Drag, DragPastSlop, ButtonUp, SameButtonDown, FeatureOff }
    public enum Decision { PassThrough, Arm, Hold, Promote, ReplayThenPass, ApplyMove, ApplyFinish, FlushThenPass, DropState, RestartAsIdle }

    /// <summary>
    /// What to do with the next mouse event. A press with the chord is held back until it clearly is a drag (a click
    /// is replayed untouched); once it is one, moves apply until the button goes up.
    /// </summary>
    public static (Decision Decision, Phase Next) Decide(Phase phase, Input input) => (phase, input) switch
    {
        (Phase.Idle, Input.ButtonDownWithChord) => (Decision.Arm, Phase.Pending),
        (Phase.Idle, _) => (Decision.PassThrough, Phase.Idle),
        (Phase.Pending, Input.DragPastSlop) => (Decision.Promote, Phase.Active),
        (Phase.Pending, Input.Drag) => (Decision.Hold, Phase.Pending),
        (Phase.Pending, Input.ButtonUp) => (Decision.ReplayThenPass, Phase.Idle),
        (Phase.Pending, Input.SameButtonDown) => (Decision.RestartAsIdle, Phase.Idle),
        (Phase.Pending, _) => (Decision.FlushThenPass, Phase.Idle),
        (Phase.Active, Input.Drag or Input.DragPastSlop) => (Decision.ApplyMove, Phase.Active),
        (Phase.Active, Input.ButtonUp) => (Decision.ApplyFinish, Phase.Idle),
        (Phase.Active, _) => (Decision.DropState, Phase.Idle),
        _ => (Decision.DropState, Phase.Idle),
    };

    /// <summary>Moved further than the slop from the press (exactly the slop still counts as a click).</summary>
    public static bool ExceedsSlop(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy) > DragSlop;

    // MARK: Move and resize

    public static PointD MovedOrigin(PointD origin, PointD pointerStart, PointD pointerNow) =>
        new(origin.X + pointerNow.X - pointerStart.X, origin.Y + pointerNow.Y - pointerStart.Y);

    [Flags] public enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>The window is cut into thirds each way; the corners and edges take the matching sides, the middle the nearest edge.</summary>
    public static Edges RegionFor(WindowRect frame, PointD point)
    {
        var lx = point.X - frame.X;
        var ly = point.Y - frame.Y;
        var edges = Edges.None;
        if (lx < frame.Width / 3) edges |= Edges.Left;
        else if (lx > frame.Width * 2 / 3) edges |= Edges.Right;
        if (ly < frame.Height / 3) edges |= Edges.Top;
        else if (ly > frame.Height * 2 / 3) edges |= Edges.Bottom;
        if (edges != Edges.None) return edges;
        // The middle cell: whichever side is nearest.
        var distances = new (double D, Edges E)[] { (lx, Edges.Left), (frame.Width - lx, Edges.Right), (ly, Edges.Top), (frame.Height - ly, Edges.Bottom) };
        return distances.OrderBy(d => d.D).First().E;
    }

    /// <summary>The frame after dragging the given edges: the opposite edges stay put and the size never drops below 120×80.</summary>
    public static WindowRect Resized(WindowRect frame, Edges edges, PointD pointerStart, PointD pointerNow)
    {
        var dx = pointerNow.X - pointerStart.X;
        var dy = pointerNow.Y - pointerStart.Y;
        double left = frame.X, right = frame.Right, top = frame.Y, bottom = frame.Bottom;
        if (edges.HasFlag(Edges.Left)) left = Math.Min(left + dx, right - MinimumSize.Width);
        if (edges.HasFlag(Edges.Right)) right = Math.Max(right + dx, left + MinimumSize.Width);
        if (edges.HasFlag(Edges.Top)) top = Math.Min(top + dy, bottom - MinimumSize.Height);
        if (edges.HasFlag(Edges.Bottom)) bottom = Math.Max(bottom + dy, top + MinimumSize.Height);
        return new WindowRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// When an app keeps a larger size than asked for, a window resized from its left or top edge is moved back so the
    /// opposite edge stays where it was. Right and bottom edges need no move.
    /// </summary>
    public static PointD? Reanchored(Edges edges, WindowRect requested, double acceptedWidth, double acceptedHeight)
    {
        if (!edges.HasFlag(Edges.Left) && !edges.HasFlag(Edges.Top)) return null;
        return new PointD(edges.HasFlag(Edges.Left) ? requested.Right - acceptedWidth : requested.X,
                          edges.HasFlag(Edges.Top) ? requested.Bottom - acceptedHeight : requested.Y);
    }

    // MARK: The directional ring

    public const double RingDeadZone = 28;

    /// <summary>
    /// Holding the trigger and moving the pointer picks a layout by direction: eight 45° slices, none inside a dead zone
    /// of 28 pixels. The vertical axis points down here, so up on the screen is a negative y.
    /// </summary>
    public static WindowLayoutAction? RingAction(PointD origin, PointD pointer)
    {
        var dx = pointer.X - origin.X;
        var dy = origin.Y - pointer.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < RingDeadZone) return null;
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (angle < 0) angle += 360;
        return angle switch
        {
            >= 337.5 or < 22.5 => WindowLayoutAction.RightHalf,
            < 67.5 => WindowLayoutAction.TopRight,
            < 112.5 => WindowLayoutAction.TopHalf,
            < 157.5 => WindowLayoutAction.TopLeft,
            < 202.5 => WindowLayoutAction.LeftHalf,
            < 247.5 => WindowLayoutAction.BottomLeft,
            < 292.5 => WindowLayoutAction.BottomHalf,
            _ => WindowLayoutAction.BottomRight,
        };
    }
}

public enum EdgeSnapZone { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

/// <summary>A display as the edge snap sees it: the whole screen and the part windows may use.</summary>
public readonly record struct SnapScreen(WindowRect Frame, WindowRect Visible);

public readonly record struct SnapHit(EdgeSnapZone Zone, int Screen, double FractionAlongEdge);

public static class EdgeSnapSupport
{
    public const double Activation = 12;
    public const double SeamSpeedLimit = 400;
    public const int MaxParts = 4;

    public static WindowLayoutAction DefaultAction(EdgeSnapZone zone) => zone switch
    {
        EdgeSnapZone.TopLeft => WindowLayoutAction.TopLeft,
        EdgeSnapZone.Top => WindowLayoutAction.Maximize,
        EdgeSnapZone.TopRight => WindowLayoutAction.TopRight,
        EdgeSnapZone.Left => WindowLayoutAction.LeftHalf,
        EdgeSnapZone.Right => WindowLayoutAction.RightHalf,
        EdgeSnapZone.BottomLeft => WindowLayoutAction.BottomLeft,
        EdgeSnapZone.Bottom => WindowLayoutAction.BottomHalf,
        _ => WindowLayoutAction.BottomRight,
    };

    // MARK: Hit testing

    private static double DistanceTo(WindowRect frame, PointD p)
    {
        var dx = Math.Max(Math.Max(frame.X - p.X, 0), p.X - frame.Right);
        var dy = Math.Max(Math.Max(frame.Y - p.Y, 0), p.Y - frame.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The zone under the pointer, if any: within 12 pixels of an edge of a display (the top band reaches from just under the
    /// work area's top edge up past the screen), top and bottom before left and right, corners where the edge ends.
    /// </summary>
    public static SnapHit? ZoneAt(PointD pointer, IReadOnlyList<SnapScreen> screens, IReadOnlySet<EdgeSnapZone>? disabled = null)
    {
        var order = screens.Select((s, i) => (s, i)).OrderBy(p => DistanceTo(p.s.Frame, pointer)).ThenBy(p => p.s.Frame.Contains(pointer) ? 0 : 1).ThenBy(p => p.i);
        foreach (var (screen, index) in order)
        {
            if (DistanceTo(screen.Frame, pointer) > Activation) continue;
            var f = screen.Frame;
            var cornerW = Math.Clamp(f.Width * 0.18, 96, 180);
            var cornerH = Math.Clamp(f.Height * 0.18, 80, 160);
            EdgeSnapZone? zone = null;
            var along = 0.0;
            var inX = pointer.X >= f.X - 1 && pointer.X <= f.Right + 1;
            var inY = pointer.Y >= f.Y - 1 && pointer.Y <= f.Bottom + 1;
            if (inX && pointer.Y <= screen.Visible.Y + Activation)
            {
                zone = pointer.X <= f.X + cornerW ? EdgeSnapZone.TopLeft : pointer.X >= f.Right - cornerW ? EdgeSnapZone.TopRight : EdgeSnapZone.Top;
                along = (pointer.X - f.X) / f.Width;
            }
            else if (inX && pointer.Y >= f.Bottom - Activation)
            {
                zone = pointer.X <= f.X + cornerW ? EdgeSnapZone.BottomLeft : pointer.X >= f.Right - cornerW ? EdgeSnapZone.BottomRight : EdgeSnapZone.Bottom;
                along = (pointer.X - f.X) / f.Width;
            }
            else if (inY && pointer.X <= f.X + Activation)
            {
                zone = pointer.Y <= f.Y + cornerH ? EdgeSnapZone.TopLeft : pointer.Y >= f.Bottom - cornerH ? EdgeSnapZone.BottomLeft : EdgeSnapZone.Left;
                along = (pointer.Y - f.Y) / f.Height;
            }
            else if (inY && pointer.X >= f.Right - Activation)
            {
                zone = pointer.Y <= f.Y + cornerH ? EdgeSnapZone.TopRight : pointer.Y >= f.Bottom - cornerH ? EdgeSnapZone.BottomRight : EdgeSnapZone.Right;
                along = (pointer.Y - f.Y) / f.Height;
            }
            if (zone is { } z && disabled?.Contains(z) != true) return new SnapHit(z, index, Math.Clamp(along, 0, 1));
            if (zone is not null) return null;
        }
        return null;
    }

    /// <summary>
    /// An edge shared with another display is a seam the pointer may be passing through: faster than 400 pixels a second
    /// across it and there is no target. An edge with nothing beyond it (a wall) snaps at any speed.
    /// </summary>
    public static bool AllowsSnap(bool edgeHasNeighbour, double speedAcrossEdge) => !edgeHasNeighbour || speedAcrossEdge <= SeamSpeedLimit;

    // MARK: Splitting an edge

    private static bool IsHorizontalEdge(EdgeSnapZone zone) => zone is EdgeSnapZone.Top or EdgeSnapZone.Bottom;
    private static bool IsSplittable(EdgeSnapZone zone) => zone is EdgeSnapZone.Top or EdgeSnapZone.Bottom or EdgeSnapZone.Left or EdgeSnapZone.Right;

    /// <summary>The layouts an edge offers when it is split into 1 to 4 parts: columns along a horizontal edge, rows along a vertical one.</summary>
    public static IReadOnlyList<WindowLayoutAction> DefaultSplit(EdgeSnapZone zone, int parts)
    {
        if (!IsSplittable(zone) || parts <= 1) return [DefaultAction(zone)];
        if (IsHorizontalEdge(zone))
            return Math.Min(parts, MaxParts) switch
            {
                2 => [WindowLayoutAction.LeftHalf, WindowLayoutAction.RightHalf],
                3 => [WindowLayoutAction.LeftThird, WindowLayoutAction.CenterThird, WindowLayoutAction.RightThird],
                _ => [WindowLayoutAction.LeftQuarter, WindowLayoutAction.LeftMiddleQuarter, WindowLayoutAction.RightMiddleQuarter, WindowLayoutAction.RightQuarter],
            };
        return Math.Min(parts, MaxParts) switch
        {
            2 => [WindowLayoutAction.TopHalf, WindowLayoutAction.BottomHalf],
            3 => [WindowLayoutAction.TopThird, WindowLayoutAction.MiddleThird, WindowLayoutAction.BottomThird],
            _ => [WindowLayoutAction.TopQuarter, WindowLayoutAction.UpperMiddleQuarter, WindowLayoutAction.LowerMiddleQuarter, WindowLayoutAction.BottomQuarter],
        };
    }

    /// <summary>The part of an edge a pointer is over, from the fraction along it (the corners are one part).</summary>
    public static int PartIndex(int parts, double fraction) => Math.Clamp((int)Math.Floor(Math.Clamp(fraction, 0, 1) * parts), 0, parts - 1);

    /// <summary>The saved choices, "zone=action+action,...": only zones that differ from their default are kept.</summary>
    public sealed class ZoneActions
    {
        private readonly Dictionary<EdgeSnapZone, List<WindowLayoutAction>> _custom = [];

        public static ZoneActions Parse(string? stored)
        {
            var result = new ZoneActions();
            foreach (var entry in (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pair = entry.Split('=', 2);
                if (pair.Length != 2 || !Enum.TryParse<EdgeSnapZone>(pair[0], ignoreCase: true, out var zone)) continue;
                var actions = pair[1].Split('+', StringSplitOptions.RemoveEmptyEntries).Select(WindowLayoutSupport.ParseAction).ToList();
                if (actions.Count is 0 or > MaxParts || actions.Any(a => a is null) || actions.Count > 1 && !IsSplittable(zone)) continue;
                result._custom[zone] = actions.Select(a => a!.Value).ToList();
            }
            return result;
        }

        public IReadOnlyList<WindowLayoutAction> For(EdgeSnapZone zone) => _custom.TryGetValue(zone, out var actions) ? actions : [DefaultAction(zone)];

        public ZoneActions With(EdgeSnapZone zone, IReadOnlyList<WindowLayoutAction> actions)
        {
            var copy = new ZoneActions();
            foreach (var pair in _custom) copy._custom[pair.Key] = pair.Value;
            if (actions.Count == 1 && actions[0] == DefaultAction(zone)) copy._custom.Remove(zone);
            else copy._custom[zone] = [.. actions];
            return copy;
        }

        public ZoneActions WithPartCount(EdgeSnapZone zone, int parts) => With(zone, DefaultSplit(zone, parts));

        public string Storage => string.Join(",", _custom.OrderBy(p => p.Key)
            .Select(p => p.Key.ToString().ToLowerInvariant()[..1] + p.Key.ToString()[1..] + "=" + string.Join("+", p.Value.Select(WindowLayoutSupport.RawName))));
    }

    /// <summary>The layout for a hit: the zone's saved split, the part the pointer is over.</summary>
    public static WindowLayoutAction ActionFor(SnapHit hit, ZoneActions actions)
    {
        var parts = actions.For(hit.Zone);
        return parts[PartIndex(parts.Count, hit.FractionAlongEdge)];
    }

    /// <summary>The preview of an action as fractions of the visible frame, so the overlay can draw it on any display.</summary>
    public static WindowRect PreviewFractions(WindowLayoutAction action) => WindowLayoutSupport.Proportions(action, new WindowRect(0, 0, 1, 1));

    // MARK: Pointer speed

    /// <summary>The pointer's recent path, to tell a flick across a seam from a deliberate stop at the edge.</summary>
    public sealed class PointerTrail
    {
        public const double Window = 0.05;
        public const double SpanFloor = 0.02;
        public const double StopAfter = 0.1;
        public const int Capacity = 512;

        private readonly List<(double X, double Y, double T)> _samples = [];

        public void Add(double x, double y, double time)
        {
            // After a pause the path starts again, as if it began a moment earlier, so a flick out of rest reads fast.
            if (_samples.Count > 0 && time - _samples[^1].T >= StopAfter) _samples.Clear();
            if (_samples.Count == 0) _samples.Add((x, y, time - SpanFloor));
            _samples.Add((x, y, time));
            if (_samples.Count > Capacity) _samples.RemoveAt(0);
        }

        public void Clear() => _samples.Clear();

        /// <summary>Pixels per second over the last 0.05 s, zero when nothing moved for 0.1 s. Returned as (along x, along y).</summary>
        public (double X, double Y) Velocity(double now)
        {
            if (_samples.Count < 2 || now - _samples[^1].T >= StopAfter) return (0, 0);
            var last = _samples[^1];
            var from = _samples.LastOrDefault(s => last.T - s.T >= Window);
            if (from == default) from = _samples[0];
            var span = Math.Max(last.T - from.T, SpanFloor);
            return ((last.X - from.X) / span, (last.Y - from.Y) / span);
        }
    }

    // MARK: Drags

    public enum DragKind { None, Moving, Resizing }

    /// <summary>
    /// A frame that changed size while keeping both a vertical and a horizontal edge is a resize; one that kept its size and
    /// moved roughly with the pointer (cosine ≥ 0.6, travelling between 0.2 and 1.8 times as far) is a move.
    /// </summary>
    public static DragKind Classify(WindowRect original, WindowRect now, double pointerDx, double pointerDy)
    {
        const double sizeTolerance = 2, moveThreshold = 2;
        if (Math.Abs(now.Width - original.Width) > sizeTolerance || Math.Abs(now.Height - original.Height) > sizeTolerance)
        {
            var sharesVertical = Math.Abs(now.X - original.X) <= sizeTolerance || Math.Abs(now.Right - original.Right) <= sizeTolerance;
            var sharesHorizontal = Math.Abs(now.Y - original.Y) <= sizeTolerance || Math.Abs(now.Bottom - original.Bottom) <= sizeTolerance;
            return sharesVertical && sharesHorizontal ? DragKind.Resizing : DragKind.None;
        }
        var dx = now.X - original.X;
        var dy = now.Y - original.Y;
        var windowDistance = Math.Sqrt(dx * dx + dy * dy);
        var pointerDistance = Math.Sqrt(pointerDx * pointerDx + pointerDy * pointerDy);
        if (windowDistance <= moveThreshold || pointerDistance <= 0) return DragKind.None;
        var cosine = (dx * pointerDx + dy * pointerDy) / (windowDistance * pointerDistance);
        var ratio = windowDistance / pointerDistance;
        return cosine >= 0.6 && ratio is >= 0.2 and <= 1.8 ? DragKind.Moving : DragKind.None;
    }

    /// <summary>A grab within 12 pixels of a corner or 5 of an edge starts a resize, not a move.</summary>
    public static bool StartsAtResizeHandle(WindowRect frame, PointD point)
    {
        var nearLeft = point.X - frame.X;
        var nearRight = frame.Right - point.X;
        var nearTop = point.Y - frame.Y;
        var nearBottom = frame.Bottom - point.Y;
        var horizontalEdge = Math.Min(nearLeft, nearRight);
        var verticalEdge = Math.Min(nearTop, nearBottom);
        if (horizontalEdge < 0 || verticalEdge < 0) return false;
        return horizontalEdge <= 12 && verticalEdge <= 12 || horizontalEdge <= 5 || verticalEdge <= 5;
    }

    // MARK: Restoring on drag

    /// <summary>Dragging a snapped window away gives it its old size back, if it differs from the snapped size by more than 2 pixels.</summary>
    public static bool NeedsSizeRestore(WindowRect snapped, SizeD restoreSize) =>
        restoreSize.Width > 0 && restoreSize.Height > 0
        && (Math.Abs(restoreSize.Width - snapped.Width) > 2 || Math.Abs(restoreSize.Height - snapped.Height) > 2);

    /// <summary>
    /// The frame the restored window takes: the pointer keeps its fractional place along the title bar and its depth below
    /// the top, then the frame is kept on the pointer's display and never above its top.
    /// </summary>
    public static WindowRect RestoredFrame(WindowRect snapped, SizeD restoreSize, PointD pointerStart, PointD pointerNow, WindowRect? screen)
    {
        var fraction = snapped.Width <= 0 ? 0.5 : Math.Clamp((pointerStart.X - snapped.X) / snapped.Width, 0, 1);
        var depth = pointerStart.Y - snapped.Y;
        var x = pointerNow.X - fraction * restoreSize.Width;
        var y = pointerNow.Y - depth;
        if (screen is { } s)
        {
            x = Math.Clamp(x, s.X, Math.Max(s.X, s.Right - restoreSize.Width));
            y = Math.Max(y, s.Y);
        }
        return new WindowRect(x, y, restoreSize.Width, restoreSize.Height);
    }
}
