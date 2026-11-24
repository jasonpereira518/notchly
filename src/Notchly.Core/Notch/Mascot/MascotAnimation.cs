// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-specific: the Core Animation semantics upstream's NotchMascotView.swift leans on (CAMediaTimingFunction,
// CAKeyframeAnimation key times and timing functions, CASpringAnimation, additive and replacing animations stacked
// by key on a layer's property) as pure maths, so the companion's every frame can be computed and tested here and
// WPF only draws it.
namespace Notchly.Core.Notch.Mascot;

/// <summary>A cubic Bézier timing curve from (0, 0) to (1, 1), as CAMediaTimingFunction.</summary>
public readonly record struct MascotTiming(double X1, double Y1, double X2, double Y2)
{
    public static readonly MascotTiming Linear = new(0, 0, 1, 1);
    public static readonly MascotTiming EaseIn = new(0.42, 0, 1, 1);
    public static readonly MascotTiming EaseOut = new(0, 0, 0.58, 1);
    public static readonly MascotTiming EaseInEaseOut = new(0.42, 0, 0.58, 1);
    /// <summary>Quick out, gentle in: eyes settling on what they look at, and morphs between faces.</summary>
    public static readonly MascotTiming Settle = new(0.2, 0.8, 0.3, 1);
    /// <summary>The stage growing or shrinking as it moves between the island and a notice.</summary>
    public static readonly MascotTiming Grow = new(0.22, 1, 0.36, 1);

    /// <summary>The curve's height where it has come <paramref name="time"/> of the way across.</summary>
    public double Ease(double time)
    {
        if (time <= 0) return 0;
        if (time >= 1) return 1;
        if (this == Linear) return time;
        // Solve x(u) = time by Newton's method, falling back on bisection.
        var u = time;
        for (var i = 0; i < 8; i++)
        {
            var x = Axis(u, X1, X2) - time;
            if (Math.Abs(x) < 1e-7) return Axis(u, Y1, Y2);
            var slope = Slope(u, X1, X2);
            if (Math.Abs(slope) < 1e-6) break;
            u -= x / slope;
        }
        double low = 0, high = 1;
        u = time;
        for (var i = 0; i < 40; i++)
        {
            var x = Axis(u, X1, X2);
            if (Math.Abs(x - time) < 1e-7) break;
            if (x < time) low = u; else high = u;
            u = (low + high) / 2;
        }
        return Axis(u, Y1, Y2);
    }

    private static double Axis(double u, double p1, double p2) =>
        3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u;

    private static double Slope(double u, double p1, double p2) =>
        3 * (1 - u) * (1 - u) * p1 + 6 * (1 - u) * u * (p2 - p1) + 3 * u * u * (1 - p2);
}

/// <summary>A damped spring of unit mass from rest, as CASpringAnimation with no initial velocity.</summary>
public readonly record struct MascotSpring(double Stiffness, double Damping)
{
    /// <summary>How far from the start toward the end it is after <paramref name="time"/> seconds; it may overshoot 1.</summary>
    public double Progress(double time)
    {
        if (time <= 0) return 0;
        var omega = Math.Sqrt(Stiffness);
        var zeta = Damping / (2 * omega);
        if (zeta < 1)
        {
            var damped = omega * Math.Sqrt(1 - zeta * zeta);
            return 1 - Math.Exp(-zeta * omega * time) * (Math.Cos(damped * time) + zeta * omega / damped * Math.Sin(damped * time));
        }
        // Critically damped or more: no overshoot.
        return 1 - Math.Exp(-omega * time) * (1 + omega * time);
    }

    /// <summary>When it is within a thousandth of its end for good: its envelope's decay to that.</summary>
    public double SettlingDuration
    {
        get
        {
            var omega = Math.Sqrt(Stiffness);
            var zeta = Damping / (2 * omega);
            if (zeta >= 1) return 10 / omega;
            var damped = omega * Math.Sqrt(1 - zeta * zeta);
            var amplitude = Math.Sqrt(1 + Math.Pow(zeta * omega / damped, 2));
            return Math.Log(amplitude / 0.001) / (zeta * omega);
        }
    }
}

/// <summary>Key-time lookup, as CAKeyframeAnimation interpolates.</summary>
public static class MascotKeyframes
{
    /// <summary>The segment <paramref name="share"/> falls in, from key <c>Index</c> to the next, and how far along
    /// it. Keys that share a time are crossed in no time. Past the last key it is <c>(last, 0)</c>.</summary>
    public static (int Index, double Fraction) Locate(double share, IReadOnlyList<double> keyTimes)
    {
        var last = keyTimes.Count - 1;
        if (last <= 0 || share <= keyTimes[0]) return (0, 0);
        if (share >= keyTimes[last]) return (last, 0);
        for (var index = 0; index < last; index++)
        {
            double from = keyTimes[index], to = keyTimes[index + 1];
            if (share >= from && share < to) return (index, (share - from) / (to - from));
        }
        return (last, 0);
    }

    /// <summary>A keyframed value at <paramref name="share"/> of its run, eased by segment and, before that, as a
    /// whole. Without key times the keys are evenly spaced. Discrete holds each key until the next.</summary>
    public static double Value(double share, IReadOnlyList<double> values, IReadOnlyList<double>? keyTimes = null,
                               IReadOnlyList<MascotTiming>? timings = null, MascotTiming? overall = null,
                               bool discrete = false)
    {
        var (index, fraction) = Segment(share, values.Count, keyTimes, timings, overall, discrete);
        return index + 1 < values.Count ? values[index] + (values[index + 1] - values[index]) * fraction : values[index];
    }

    /// <summary>The segment and eased fraction, for keys of any kind.</summary>
    public static (int Index, double Fraction) Segment(double share, int count, IReadOnlyList<double>? keyTimes,
                                                       IReadOnlyList<MascotTiming>? timings, MascotTiming? overall,
                                                       bool discrete)
    {
        if (count <= 1) return (0, 0);
        var t = overall?.Ease(Math.Clamp(share, 0, 1)) ?? Math.Clamp(share, 0, 1);
        var times = keyTimes ?? Enumerable.Range(0, count).Select(i => (double)i / (count - 1)).ToArray();
        var (index, fraction) = Locate(t, times);
        if (index >= count - 1) return (count - 1, 0);
        if (discrete) return (index, 0);
        if (timings is not null && index < timings.Count) fraction = timings[index].Ease(fraction);
        return (index, fraction);
    }
}

/// <summary>
/// One animation on one property, as a CAAnimation added to a layer: it starts at <see cref="Begin"/> on the
/// companion's clock and runs <see cref="Duration"/> seconds. <see cref="Apply"/> takes the value below it (the
/// model value with every earlier animation applied) and its share of the way, and gives the new value: replacing
/// it, adding to it, or setting part of it.
/// </summary>
internal sealed class MascotAnimation<T>
{
    public required double Begin { get; init; }
    public required double Duration { get; init; }
    public required Func<T, double, T> Apply { get; init; }
    /// <summary>Holds its last value after it ends (fillMode forwards).</summary>
    public bool FillsForwards { get; init; }
    /// <summary>Holds its first value before it begins (fillMode backwards).</summary>
    public bool FillsBackwards { get; init; }
    /// <summary>Taken off as it ends, as by default.</summary>
    public bool RemovedOnCompletion { get; init; } = true;

    public double End => Begin + Duration;

    public bool IsOver(double now) => now >= End;

    /// <summary>Its share of the way at <paramref name="now"/>, or null where it shows nothing.</summary>
    public double? Share(double now)
    {
        if (now < Begin) return FillsBackwards ? 0 : null;
        if (Duration <= 0 || now >= End) return FillsForwards && !RemovedOnCompletion ? 1 : null;
        return (now - Begin) / Duration;
    }
}

/// <summary>A layer's animatable property: its model value and the animations on it, in the order added, each
/// under a key. Adding under a key already in use replaces that animation, as Core Animation does.</summary>
internal sealed class MascotChannel<T>
{
    private readonly List<(string Key, MascotAnimation<T> Animation)> _animations = [];

    public MascotChannel(T model) => Model = model;

    public T Model { get; set; }

    public void Add(string key, MascotAnimation<T> animation)
    {
        Remove(key);
        _animations.Add((key, animation));
    }

    public void Remove(string key) => _animations.RemoveAll(entry => entry.Key == key);

    public void RemoveAll() => _animations.Clear();

    /// <summary>The animation under <paramref name="key"/>, unless it ended and was taken off.</summary>
    public MascotAnimation<T>? Get(string key, double now)
    {
        foreach (var (k, animation) in _animations)
            if (k == key) return animation.IsOver(now) && animation.RemovedOnCompletion ? null : animation;
        return null;
    }

    /// <summary>Takes off what has ended, as Core Animation does after the frame it ends on.</summary>
    public void Prune(double now) =>
        _animations.RemoveAll(entry => entry.Animation.RemovedOnCompletion && entry.Animation.IsOver(now));

    /// <summary>What shows at <paramref name="now"/>: the presentation value.</summary>
    public T Value(double now)
    {
        var value = Model;
        foreach (var (_, animation) in _animations)
            if (animation.Share(now) is { } share)
                value = animation.Apply(value, share);
        return value;
    }
}
