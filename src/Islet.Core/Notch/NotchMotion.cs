// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchSupport.swift (enum NotchMotion and
// struct NotchMotionTimeline): the island's analytic springs. Upstream bakes them into Core
// Animation keyframes; Islet samples them every rendered frame.
namespace Islet.Core.Notch;

public static class NotchMotion
{
    /// <summary>Departing content has faded out by then; the next content fades in after.</summary>
    public const double DepartureHidden = 0.2;

    /// <summary>Perceptual duration and bounce, as SwiftUI and Core Animation define them.</summary>
    public readonly record struct Spring(double Duration, double Bounce)
    {
        /// <summary>Progress from rest at 0 toward 1.</summary>
        public double Progress(double time)
        {
            if (time <= 0) return 0;
            var natural = 2 * Math.PI / Duration;
            var damping = 1 - Bounce;
            if (damping >= 1) return 1 - Math.Exp(-natural * time) * (1 + natural * time);
            var damped = natural * Math.Sqrt(1 - damping * damping);
            return 1 - Math.Exp(-damping * natural * time)
                * (Math.Cos(damped * time) + damping * natural / damped * Math.Sin(damped * time));
        }

        /// <summary>How far past the target the spring swings, as a share of its travel.</summary>
        public double Overshoot
        {
            get
            {
                if (Bounce <= 0) return 0;
                var damping = 1 - Bounce;
                return Math.Exp(-Math.PI * damping / Math.Sqrt(1 - damping * damping));
            }
        }

        /// <summary>This spring with only as much bounce as keeps the swing within `limit` points.</summary>
        public Spring Limited(double travel, double limit)
        {
            if (Bounce <= 0 || travel <= 0 || travel * Overshoot <= limit) return this;
            var share = Math.Log(Math.Max(limit, 0.01) / travel);
            return this with { Bounce = 1 + share / Math.Sqrt(Math.PI * Math.PI + share * share) };
        }
    }

    public static readonly Spring GrowingWidth = new(0.44, 0.25);
    public static readonly Spring GrowingHeight = new(0.38, 0.22);
    public static readonly Spring ShrinkingWidth = new(0.30, 0);
    public static readonly Spring ShrinkingHeight = new(0.26, 0);
    /// <summary>A strip that only fits a new reading eases to its width without a swing.</summary>
    public static readonly Spring SteadyWidth = new(0.5, 0);
    /// <summary>The farthest a side may pass its target.</summary>
    public const double OvershootLimit = 12;
    /// <summary>Sides closer than this to their targets read as settled.</summary>
    public const double SettledDistance = 0.5;

    public static Spring SpringFor(double from, double to, bool width, bool steady = false)
    {
        if (steady && width) return SteadyWidth;
        var spring = to > from ? (width ? GrowingWidth : GrowingHeight) : (width ? ShrinkingWidth : ShrinkingHeight);
        return spring.Limited(Math.Abs(to - from), OvershootLimit);
    }

    public static SizeD SizeAt(double time, SizeD from, SizeD to, bool steady = false)
    {
        double Side(double start, double end, bool width)
        {
            if (start == end) return end;
            var spring = SpringFor(start, end, width, steady);
            return Math.Max(0, start + (end - start) * spring.Progress(time));
        }
        return new SizeD(Side(from.Width, to.Width, true), Side(from.Height, to.Height, false));
    }

    /// <summary>When size stays within <see cref="SettledDistance"/> of its target for good.</summary>
    public static double SettlingTime(SizeD from, SizeD to, bool steady = false)
    {
        const double step = 1.0 / 240;
        var settled = step;
        for (var time = step; time < 2; time += step)
        {
            var size = SizeAt(time, from, to, steady);
            if (Math.Abs(size.Width - to.Width) > SettledDistance || Math.Abs(size.Height - to.Height) > SettledDistance)
                settled = time + step;
        }
        return settled;
    }

    /// <summary>The largest size a move from `from` to `to` passes through, overshoot included.</summary>
    public static SizeD Envelope(SizeD from, SizeD to)
    {
        double Side(double start, double end, bool width)
        {
            var swing = end > start ? SpringFor(start, end, width).Overshoot : 0;
            if (swing <= 0) return Math.Max(start, end);
            return Math.Ceiling(end + (end - start) * swing);
        }
        return new SizeD(Side(from.Width, to.Width, true), Side(from.Height, to.Height, false));
    }
}

/// <summary>
/// A resize in flight, each side a sum of the island's springs from rest: the move it began with
/// and one more from each moment that side's target moved on the way it was already going. A
/// motion turning back starts again from where it is drawn.
/// </summary>
public sealed class NotchMotionTimeline
{
    private readonly record struct Leg(double Begin, double From, double To);

    private readonly List<Leg> _width;
    private readonly List<Leg> _height;
    public double Begin { get; private set; }
    public bool Steady { get; }

    public NotchMotionTimeline(SizeD from, SizeD to, double begin, bool steady = false)
    {
        _width = [new Leg(begin, from.Width, to.Width)];
        _height = [new Leg(begin, from.Height, to.Height)];
        Begin = begin;
        Steady = steady;
    }

    public SizeD Target => new(_width[^1].To, _height[^1].To);

    public SizeD SizeAt(double time) => new(Value(_width, time, true), Value(_height, time, false));

    public bool IsSettled(double time)
    {
        // Every leg's spring is close enough to rest that no side moves visibly any more.
        var size = SizeAt(time);
        var target = Target;
        if (Math.Abs(size.Width - target.Width) > NotchMotion.SettledDistance
            || Math.Abs(size.Height - target.Height) > NotchMotion.SettledDistance) return false;
        var later = SizeAt(time + 0.05);
        return Math.Abs(later.Width - target.Width) <= NotchMotion.SettledDistance
            && Math.Abs(later.Height - target.Height) <= NotchMotion.SettledDistance
            && time - Begin > 0.05;
    }

    /// <summary>This motion headed for `size` from `time` on, or null when a side turns back.</summary>
    public NotchMotionTimeline? Continued(SizeD size, double time)
    {
        if (Steady) return null;
        var width = Continue(_width, size.Width, time);
        var height = Continue(_height, size.Height, time);
        if (width is null || height is null) return null;
        var next = new NotchMotionTimeline(width, height, time, Steady);
        return next;
    }

    private NotchMotionTimeline(List<Leg> width, List<Leg> height, double begin, bool steady)
    {
        _width = width;
        _height = height;
        Begin = begin;
        Steady = steady;
    }

    private double Value(List<Leg> legs, double time, bool width)
    {
        var first = legs[0];
        var last = legs[^1];
        var value = first.From;
        foreach (var leg in legs)
        {
            if (leg.From == leg.To) continue;
            var spring = NotchMotion.SpringFor(leg.From, leg.To, width, Steady);
            value += (leg.To - leg.From) * spring.Progress(time - leg.Begin);
        }
        // Swings that coincide still pass the target no further than one does.
        return Math.Max(0, Math.Min(value, Math.Max(first.From, last.To) + NotchMotion.OvershootLimit));
    }

    private static List<Leg>? Continue(List<Leg> legs, double end, double time)
    {
        var last = legs[^1];
        if (end == last.To) return [.. legs];
        var headingLeg = legs.FirstOrDefault(l => l.From != l.To);
        var heading = headingLeg == default ? 0 : headingLeg.To - headingLeg.From;
        if (heading != 0 && (end > last.To) != (heading > 0)) return null;
        return [.. legs, new Leg(time, last.To, end)];
    }
}
