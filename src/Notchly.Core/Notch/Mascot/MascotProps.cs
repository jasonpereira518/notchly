// SPDX-License-Identifier: GPL-3.0-or-later
// Windows addition (upstream's companion has no props): the small shapes that float beside the companion as it
// reacts, such as hearts, sparkles and notes. Each prop's outline and its life are pure maths, so the frame loop only
// samples them and WPF only draws what it is given.
namespace Notchly.Core.Notch.Mascot;

public enum MascotPropKind
{
    Spark, Heart, Bang, Question, Zee, Ring, Note, Dots, Lines, Arcs, Band, Drop, Pause, Badge, Rays, Clock, Skip,
}

/// <summary>
/// One prop's life. <paramref name="From"/> and <paramref name="To"/> are offsets from the figure's middle in shares of
/// the companion's size, with x positive toward the side away from the camera and y down. It pops in, drifts, and
/// fades out over <paramref name="Duration"/> seconds from <paramref name="Begin"/>.
/// </summary>
/// <param name="Size">The prop's own size, as a share of the companion's.</param>
/// <param name="GrowTo">What its scale becomes by the end, as a multiple of its first size (a ring spreading).</param>
/// <param name="Spin">How far it turns over its life, in radians.</param>
public sealed record MascotProp(MascotPropKind Kind, double Begin, double Duration, PointD From, PointD To,
                                double Size = 0.4, double GrowTo = 1, double Spin = 0)
{
    /// <summary>Share of its life it spends popping in, and where it starts to fade.</summary>
    public const double PopShare = 0.18, FadeFrom = 0.65;

    public double End => Begin + Duration;

    /// <summary>How it stands at <paramref name="now"/>, or null before it begins and after it ends.</summary>
    /// <param name="size">The companion's size in points.</param>
    /// <param name="outward">-1 when the side away from the camera is the left, +1 when it is the right.</param>
    public MascotPropFrame? Sample(double now, double size, double outward)
    {
        if (Duration <= 0 || now < Begin || now >= End) return null;
        var share = (now - Begin) / Duration;
        var move = MascotTiming.EaseOut.Ease(share);
        var x = (From.X + (To.X - From.X) * move) * outward * size;
        var y = (From.Y + (To.Y - From.Y) * move) * size;
        double pop = 1, overshoot = 1;
        if (share < PopShare)
        {
            var into = share / PopShare;
            pop = MascotTiming.EaseOut.Ease(into);
            overshoot = 1 + 0.2 * Math.Sin(into * Math.PI);
        }
        var scale = Size * size * pop * overshoot * (1 + (GrowTo - 1) * share);
        var opacity = share < 0.1 ? share / 0.1 : share > FadeFrom ? (1 - share) / (1 - FadeFrom) : 1;
        return new MascotPropFrame(Kind, new PointD(x, y), scale, Math.Clamp(opacity, 0, 1), Spin * share);
    }
}

/// <summary>A prop at one moment: where (from the figure's middle, in points), how big, how opaque, and how turned.</summary>
public readonly record struct MascotPropFrame(MascotPropKind Kind, PointD Offset, double Size, double Opacity, double Rotation);

/// <summary>The props' outlines and colours.</summary>
public static class MascotPropShapes
{
    /// <summary>The outline in a unit square about (0, 0), y down. Scale it by the prop's size.</summary>
    public static MascotPath Unit(MascotPropKind kind) => kind switch
    {
        MascotPropKind.Spark => Polygon(Pt(0, -0.5), Pt(0.12, -0.12), Pt(0.5, 0), Pt(0.12, 0.12), Pt(0, 0.5), Pt(-0.12, 0.12),
                                        Pt(-0.5, 0), Pt(-0.12, -0.12)),
        MascotPropKind.Heart => Heart(),
        MascotPropKind.Bang => MascotPath.Combine(Stroke([Pt(0, -0.45), Pt(0, 0.12)], 0.2), Dot(Pt(0, 0.37), 0.11)),
        MascotPropKind.Question => MascotPath.Combine(Stroke(QuestionHook(), 0.16), Dot(Pt(0, 0.4), 0.1)),
        MascotPropKind.Zee => Stroke([Pt(-0.3, -0.3), Pt(0.3, -0.3), Pt(-0.3, 0.3), Pt(0.3, 0.3)], 0.16),
        MascotPropKind.Ring => Stroke(Circle(Pt(0, 0), 0.42, 28), 0.1, closed: true),
        MascotPropKind.Note => Note(),
        MascotPropKind.Dots => MascotPath.Combine(Dot(Pt(-0.32, 0), 0.09), Dot(Pt(0, 0), 0.09), Dot(Pt(0.32, 0), 0.09)),
        MascotPropKind.Lines => MascotPath.Combine(Stroke([Pt(-0.45, -0.22), Pt(-0.05, -0.22)], 0.09),
                                                    Stroke([Pt(-0.35, 0), Pt(0.05, 0)], 0.09),
                                                    Stroke([Pt(-0.45, 0.22), Pt(-0.05, 0.22)], 0.09)),
        MascotPropKind.Arcs => MascotPath.Combine(Stroke(Arc(Pt(-0.25, 0), 0.28, -50, 50), 0.09),
                                                   Stroke(Arc(Pt(-0.25, 0), 0.48, -50, 50), 0.09)),
        MascotPropKind.Band => MascotPath.Combine(Stroke(Arc(Pt(0, 0.12), 0.5, 200, 340, up: true), 0.1),
                                                   Dot(Pt(-0.49, 0.12), 0.1), Dot(Pt(0.49, 0.12), 0.1)),
        MascotPropKind.Drop => MascotPath.Combine(Dot(Pt(0, 0.15), 0.27), Polygon(Pt(0, -0.45), Pt(0.24, 0.05), Pt(-0.24, 0.05))),
        MascotPropKind.Pause => MascotPath.Combine(Stroke([Pt(-0.17, -0.3), Pt(-0.17, 0.3)], 0.17),
                                                    Stroke([Pt(0.17, -0.3), Pt(0.17, 0.3)], 0.17)),
        MascotPropKind.Badge => Dot(Pt(0, 0), 0.4),
        MascotPropKind.Rays => MascotPath.Combine(Stroke([Pt(0.05, -0.45), Pt(0.05, -0.2)], 0.09),
                                                   Stroke([Pt(0.3, -0.35), Pt(0.18, -0.15)], 0.09),
                                                   Stroke([Pt(0.45, -0.1), Pt(0.2, -0.05)], 0.09)),
        MascotPropKind.Clock => MascotPath.Combine(Stroke(Circle(Pt(0, 0), 0.4, 24), 0.1, closed: true),
                                                    Stroke([Pt(0, 0), Pt(0, -0.24)], 0.08), Stroke([Pt(0, 0), Pt(0.17, 0.08)], 0.08)),
        MascotPropKind.Skip => MascotPath.Combine(Polygon(Pt(-0.5, -0.28), Pt(-0.08, 0), Pt(-0.5, 0.28)),
                                                   Polygon(Pt(-0.08, -0.28), Pt(0.34, 0), Pt(-0.08, 0.28))),
        _ => MascotPath.Empty,
    };

    /// <summary>What a prop is drawn in: each reads on the island's black.</summary>
    public static MascotColor Color(MascotPropKind kind) => kind switch
    {
        MascotPropKind.Heart => new(1.00, 0.42, 0.56),
        MascotPropKind.Spark => new(1.00, 0.84, 0.32),
        MascotPropKind.Bang => new(1.00, 0.52, 0.30),
        MascotPropKind.Question => new(0.55, 0.74, 1.00),
        MascotPropKind.Zee => new(0.74, 0.68, 1.00),
        MascotPropKind.Ring => new(1.00, 1.00, 1.00, 0.85),
        MascotPropKind.Note => new(0.46, 0.92, 0.72),
        MascotPropKind.Dots => new(0.84, 0.86, 0.92),
        MascotPropKind.Lines => new(1.00, 1.00, 1.00, 0.85),
        MascotPropKind.Arcs => new(1.00, 0.72, 0.30),
        MascotPropKind.Band => new(0.96, 0.96, 1.00),
        MascotPropKind.Drop => new(0.56, 0.82, 1.00),
        MascotPropKind.Pause => new(0.84, 0.86, 0.92),
        MascotPropKind.Badge => new(1.00, 0.30, 0.30),
        MascotPropKind.Rays => new(1.00, 0.92, 0.50),
        MascotPropKind.Skip => new(0.46, 0.92, 0.72),
        _ => new(0.92, 0.92, 0.96),
    };

    // MARK: Pieces

    private static PointD Pt(double x, double y) => new(x, y);

    private static MascotPath Polygon(params PointD[] points) =>
        new(new MascotFigure(points[0], points.Skip(1).Select(p => (MascotSegment)new MascotLine(p)).ToArray()));

    private static MascotPath Dot(PointD center, double radius) =>
        MascotGeometry.Ellipse(new RectD(center.X - radius, center.Y - radius, radius * 2, radius * 2));

    private static MascotPath Heart()
    {
        // Two lobes and a point: the classic curve, resampled and smoothed like the heart eyes.
        var raw = new List<PointD>(64);
        for (var index = 0; index < 64; index++)
        {
            var t = (double)index / 64 * 2 * Math.PI;
            raw.Add(new PointD(16 * Math.Pow(Math.Sin(t), 3),
                               -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t))));
        }
        double minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y), middle = (minY + maxY) / 2;
        var points = raw.Select(p => new PointD(p.X / 32, (p.Y - middle) / (maxY - minY) * 0.9)).ToArray();
        return MascotGeometry.SmoothClosedPath(points);
    }

    private static MascotPath Note()
    {
        var head = Dot(Pt(0, 0), 1).Transformed(MascotMatrix.Scale(0.17, 0.12)
            .Then(MascotMatrix.Rotation(-0.35)).Then(MascotMatrix.Translation(-0.1, 0.3)));
        return MascotPath.Combine(head, Stroke([Pt(0.05, 0.27), Pt(0.05, -0.4)], 0.1),
                                  Stroke([Pt(0.05, -0.4), Pt(0.32, -0.22), Pt(0.32, -0.05)], 0.1));
    }

    private static List<PointD> QuestionHook()
    {
        var points = new List<PointD>();
        // From the hook's left, over the top and round to its right, then down to the stem.
        for (var degrees = 190; degrees >= -45; degrees -= 15)
        {
            var angle = degrees * Math.PI / 180;
            points.Add(Pt(0.25 * Math.Cos(angle), -0.2 - 0.25 * Math.Sin(angle)));
        }
        points.Add(Pt(0, 0.1));
        return points;
    }

    private static List<PointD> Circle(PointD center, double radius, int steps)
    {
        var points = new List<PointD>(steps);
        for (var index = 0; index < steps; index++)
        {
            var angle = (double)index / steps * 2 * Math.PI;
            points.Add(Pt(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>An arc about <paramref name="center"/>, in degrees from the right going counter-clockwise on screen
    /// (up). With <paramref name="up"/>, angles are measured up from the right (a band over the head).</summary>
    private static List<PointD> Arc(PointD center, double radius, double from, double to, bool up = false)
    {
        var points = new List<PointD>();
        const int steps = 12;
        for (var step = 0; step <= steps; step++)
        {
            var degrees = from + (to - from) * step / steps;
            var angle = degrees * Math.PI / 180;
            // y grows downward, so a positive angle with `up` rises.
            points.Add(Pt(center.X + radius * Math.Cos(angle), center.Y + radius * (up ? Math.Sin(angle) : -Math.Sin(angle))));
        }
        return points;
    }

    /// <summary>A line through the points, <paramref name="width"/> thick: a clockwise quad on each stretch and a dot at
    /// each joint, so the nonzero fill leaves no hole where they overlap.</summary>
    private static MascotPath Stroke(IReadOnlyList<PointD> points, double width, bool closed = false)
    {
        var figures = new List<MascotPath>();
        var half = width / 2;
        var count = closed ? points.Count : points.Count - 1;
        for (var index = 0; index < count; index++)
        {
            PointD a = points[index], b = points[(index + 1) % points.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            double nx = -dy / length * half, ny = dx / length * half;
            var quad = new[] { Pt(a.X + nx, a.Y + ny), Pt(b.X + nx, b.Y + ny), Pt(b.X - nx, b.Y - ny), Pt(a.X - nx, a.Y - ny) };
            // Clockwise on screen is the positive area the dots use.
            if (Area(quad) < 0) Array.Reverse(quad);
            figures.Add(Polygon(quad));
        }
        for (var index = 0; index < points.Count; index++) figures.Add(Dot(points[index], half));
        return MascotPath.Combine(figures.ToArray());
    }

    private static double Area(IReadOnlyList<PointD> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            PointD a = points[index], b = points[(index + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area;
    }
}
