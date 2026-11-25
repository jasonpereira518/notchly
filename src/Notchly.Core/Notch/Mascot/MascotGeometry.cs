// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchMascotSupport.swift (enum NotchMascotGeometry): the body
// outlines, the robot's parts, the shine, the figure's offset in its box and the eyes, each resampled to 24 points
// so any eye can turn into any other. Outlines are in a square of `size` points from its top left corner, y down.
namespace Notchly.Core.Notch.Mascot;

/// <summary>The robot's head, ears, antenna, bulb and face plate.</summary>
public sealed record MascotRobotParts(MascotPath Head, MascotPath Ears, MascotPath Antenna, MascotPath Bulb, MascotPath Visor)
{
    /// <summary>Everything but the visor, which sits on the head.</summary>
    public RectD FigureBounds => MascotPath.Combine(Head, Ears, Antenna, Bulb).Bounds;
}

public static class MascotGeometry
{
    /// <summary>Every eye is drawn with this many curves, so any eye can turn into any other one point at a time.</summary>
    public const int EyeCurveCount = 24;

    /// <summary>The share of the box an eye's own square takes, centred on the eye (upstream's eye layers).</summary>
    public const double EyeBoxShare = 0.4;

    /// <summary>Four cubic quarter arcs, as Core Graphics draws an ellipse.</summary>
    private const double Kappa = 0.5522847498307936;

    public static MascotPath Body(MascotShape shape, double size)
    {
        PointD P(double x, double y) => new(x * size, y * size);
        switch (shape)
        {
            case MascotShape.Egg:
                return new MascotPath(new MascotFigure(P(0.5, 0.10),
                [
                    new MascotCurve(P(0.71, 0.10), P(0.86, 0.36), P(0.86, 0.60)),
                    new MascotCurve(P(0.86, 0.80), P(0.70, 0.96), P(0.5, 0.96)),
                    new MascotCurve(P(0.30, 0.96), P(0.14, 0.80), P(0.14, 0.60)),
                    new MascotCurve(P(0.14, 0.36), P(0.29, 0.10), P(0.5, 0.10)),
                ]));
            case MascotShape.Squircle:
            {
                // A superellipse: flatter sides than a rounded rectangle, no corner seam.
                const int count = 72;
                const double exponent = 2 / 3.4;
                var points = new PointD[count];
                for (var index = 0; index < count; index++)
                {
                    var angle = (double)index / count * 2 * Math.PI;
                    double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                    var x = 0.5 + 0.43 * (cosine < 0 ? -1 : 1) * Math.Pow(Math.Abs(cosine), exponent);
                    var y = 0.55 + 0.40 * (sine < 0 ? -1 : 1) * Math.Pow(Math.Abs(sine), exponent);
                    points[index] = P(x, y);
                }
                return new MascotPath(new MascotFigure(points[0], points.Skip(1).Select(p => (MascotSegment)new MascotLine(p)).ToArray()));
            }
            case MascotShape.Pill:
                return RoundedRect(new RectD(0.05 * size, 0.28 * size, 0.90 * size, 0.60 * size), 0.30 * size);
            default:
                return Ellipse(new RectD(0.08 * size, 0.12 * size, 0.84 * size, 0.84 * size));
        }
    }

    public static MascotRobotParts Robot(double size)
    {
        RectD R(double x, double y, double width, double height) => new(x * size, y * size, width * size, height * size);
        MascotPath Rounded(RectD rect, double radius) => RoundedRect(rect, radius * size);
        return new MascotRobotParts(
            Head: Rounded(R(0.12, 0.26, 0.76, 0.68), 0.20),
            Ears: MascotPath.Combine(Rounded(R(0.05, 0.50, 0.08, 0.20), 0.035), Rounded(R(0.87, 0.50, 0.08, 0.20), 0.035)),
            Antenna: Rounded(R(0.475, 0.12, 0.05, 0.16), 0.025),
            Bulb: Ellipse(R(0.43, 0.04, 0.14, 0.14)),
            Visor: Rounded(R(0.20, 0.44, 0.60, 0.38), 0.15));
    }

    /// <summary>
    /// How far below the middle of its box the figure's own middle sits, as a share of the box. The box keeps room
    /// above a minimal body for the robot's antenna, so a body placed by its box would sit a little low wherever it
    /// is meant to be centred.
    /// </summary>
    public static double FigureOffset(MascotLook look)
    {
        var bounds = look.Style == MascotStyle.Robot ? Robot(1).FigureBounds : Body(look.Shape, 1).Bounds;
        return bounds.Y + bounds.Height / 2 - 0.5;
    }

    /// <summary>The highlight, an ellipse leaning with the body's curve.</summary>
    public static MascotPath Shine(MascotFaceLayout face, double size)
    {
        var rx = face.ShineRadii.Width * size;
        var ry = face.ShineRadii.Height * size;
        var place = MascotMatrix.Rotation(face.ShineAngle)
            .Then(MascotMatrix.Translation(face.ShineCenter.X * size, face.ShineCenter.Y * size));
        return Ellipse(new RectD(-rx, -ry, rx * 2, ry * 2)).Transformed(place);
    }

    /// <summary>
    /// An eye's outline around <paramref name="center"/>. <paramref name="size"/> is an open eye's width and height,
    /// and the right eye mirrors the left one.
    /// </summary>
    public static MascotPath Eye(MascotEye eye, SizeD size, PointD center, bool right) =>
        SmoothClosedPath(EyePoints(eye, size, right).Select(p => new PointD(p.X + center.X, p.Y + center.Y)).ToArray());

    /// <summary>The eye's outline as <see cref="EyeCurveCount"/> points around (0, 0), from the top of its middle
    /// line, clockwise on screen.</summary>
    public static PointD[] EyePoints(MascotEye eye, SizeD size, bool right)
    {
        double width = Math.Max(0.5, size.Width), height = Math.Max(0.5, size.Height);
        var outline = eye switch
        {
            MascotEye.Closed => Stadium(width * 1.5, width * 0.45, new PointD(0, height * 0.12)),
            MascotEye.Squint => Stadium(width, height * 0.55, new PointD(0, height * 0.08)),
            MascotEye.Wide => EllipsePoints(new SizeD(width * 0.8, width * 0.8), new PointD(0, -height * 0.02)),
            MascotEye.Happy => Arch(width * 2.1, height * 0.42, width * 0.55, -height * 0.18),
            MascotEye.Heart => Heart(width * 2.0, width * 1.8, new PointD(0, -height * 0.02)),
            MascotEye.Sleepy => Lid(width * 1.25, height * 0.02, height * 0.38),
            // The left eye's lid falls toward the nose, on its right.
            MascotEye.Determined => ClipBelow(Stadium(width, height, new PointD(0, 0)),
                                              new PointD(-width / 2, -height * 0.30), new PointD(width / 2, height * 0.02)),
            _ => Stadium(width, height, new PointD(0, 0)),
        };
        if (right) outline = outline.Select(p => new PointD(-p.X, p.Y)).ToList();
        return Resample(outline, EyeCurveCount);
    }

    /// <summary>One eye between two others, point by point: <paramref name="share"/> 0 is <paramref name="from"/>.
    /// Core Animation morphs a path the same way.</summary>
    public static PointD[] Morph(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, double share)
    {
        var count = Math.Min(from.Count, to.Count);
        var points = new PointD[count];
        for (var index = 0; index < count; index++)
            points[index] = new PointD(from[index].X + (to[index].X - from[index].X) * share,
                                       from[index].Y + (to[index].Y - from[index].Y) * share);
        return points;
    }

    // MARK: Primitive outlines

    public static MascotPath Ellipse(RectD rect)
    {
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        double rx = rect.Width / 2, ry = rect.Height / 2, kx = rx * Kappa, ky = ry * Kappa;
        return new MascotPath(new MascotFigure(new PointD(cx + rx, cy),
        [
            new MascotCurve(new(cx + rx, cy + ky), new(cx + kx, cy + ry), new(cx, cy + ry)),
            new MascotCurve(new(cx - kx, cy + ry), new(cx - rx, cy + ky), new(cx - rx, cy)),
            new MascotCurve(new(cx - rx, cy - ky), new(cx - kx, cy - ry), new(cx, cy - ry)),
            new MascotCurve(new(cx + kx, cy - ry), new(cx + rx, cy - ky), new(cx + rx, cy)),
        ]));
    }

    /// <summary>A rectangle with quarter-circle corners of <paramref name="radius"/>, kept within half its sides.</summary>
    public static MascotPath RoundedRect(RectD rect, double radius)
    {
        var r = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        double left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom, k = r * Kappa;
        return new MascotPath(new MascotFigure(new PointD(left + r, top),
        [
            new MascotLine(new(right - r, top)),
            new MascotCurve(new(right - r + k, top), new(right, top + r - k), new(right, top + r)),
            new MascotLine(new(right, bottom - r)),
            new MascotCurve(new(right, bottom - r + k), new(right - r + k, bottom), new(right - r, bottom)),
            new MascotLine(new(left + r, bottom)),
            new MascotCurve(new(left + r - k, bottom), new(left, bottom - r + k), new(left, bottom - r)),
            new MascotLine(new(left, top + r)),
            new MascotCurve(new(left, top + r - k), new(left + r - k, top), new(left + r, top)),
        ]));
    }

    // MARK: Eye outlines (dense, before resampling)

    private const int Dense = 96;

    /// <summary>A capsule standing on its round ends, or lying on them when wider.</summary>
    private static List<PointD> Stadium(double width, double height, PointD center)
    {
        var radius = Math.Min(width, height) / 2;
        return RoundedRectPoints(new RectD(center.X - width / 2, center.Y - height / 2, width, height), radius);
    }

    private static List<PointD> RoundedRectPoints(RectD rect, double radius)
    {
        var corners = new (PointD Center, double Start)[]
        {
            (new PointD(rect.Right - radius, rect.Y + radius), -Math.PI / 2),
            (new PointD(rect.Right - radius, rect.Bottom - radius), 0),
            (new PointD(rect.X + radius, rect.Bottom - radius), Math.PI / 2),
            (new PointD(rect.X + radius, rect.Y + radius), Math.PI),
        };
        var points = new List<PointD>();
        const int steps = Dense / 4;
        foreach (var (center, start) in corners)
            for (var step = 0; step <= steps; step++)
            {
                var angle = start + (double)step / steps * Math.PI / 2;
                points.Add(new PointD(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
        return points;
    }

    private static List<PointD> EllipsePoints(SizeD radii, PointD center)
    {
        var points = new List<PointD>(Dense);
        for (var index = 0; index < Dense; index++)
        {
            var angle = -Math.PI / 2 + (double)index / Dense * 2 * Math.PI;
            points.Add(new PointD(center.X + radii.Width * Math.Cos(angle), center.Y + radii.Height * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>Eyes shut with a smile: a band bent upward, round at both ends.</summary>
    private static List<PointD> Arch(double span, double rise, double thickness, double top)
    {
        var half = Math.Max(0.1, span / 2 - thickness / 2);
        var lift = Math.Max(0.1, rise);
        var radius = (half * half + lift * lift) / (2 * lift);
        var apex = top + thickness / 2;
        var center = new PointD(0, apex + radius);
        var reach = Math.Asin(Math.Min(1, half / radius));
        PointD OnArc(double angle, double r) => new(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
        const int steps = Dense / 3;
        var points = new List<PointD>();
        for (var step = 0; step <= steps; step++)
            points.Add(OnArc(-reach + (double)step / steps * reach * 2, radius + thickness / 2));
        // The right end's round cap, from the outer arc to the inner one.
        var rightEnd = OnArc(reach, radius);
        for (var step = 1; step < 12; step++)
        {
            var angle = reach + step / 12.0 * Math.PI;
            points.Add(new PointD(rightEnd.X + thickness / 2 * Math.Sin(angle), rightEnd.Y - thickness / 2 * Math.Cos(angle)));
        }
        for (var step = 0; step <= steps; step++)
            points.Add(OnArc(reach - (double)step / steps * reach * 2, radius - thickness / 2));
        var leftEnd = OnArc(-reach, radius);
        for (var step = 1; step < 12; step++)
        {
            var angle = -reach + Math.PI + step / 12.0 * Math.PI;
            points.Add(new PointD(leftEnd.X + thickness / 2 * Math.Sin(angle), leftEnd.Y - thickness / 2 * Math.Cos(angle)));
        }
        return points;
    }

    private static List<PointD> Heart(double width, double height, PointD center)
    {
        // The classic heart curve, 32 wide and about 29 tall, with y up.
        var raw = new List<PointD>(Dense);
        for (var index = 0; index < Dense; index++)
        {
            var t = (double)index / Dense * 2 * Math.PI;
            var x = 16 * Math.Pow(Math.Sin(t), 3);
            var y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            raw.Add(new PointD(x, y));
        }
        double minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y);
        var middle = (minY + maxY) / 2;
        return raw.Select(p => new PointD(center.X + p.X / 32 * width,
                                          center.Y - (p.Y - middle) / (maxY - minY) * height)).ToList();
    }

    /// <summary>A drowsy eye: the lid's flat edge on top, the eye's round bottom below.</summary>
    private static List<PointD> Lid(double width, double top, double depth)
    {
        var half = width / 2;
        var points = new List<PointD>();
        const int flat = Dense / 4;
        for (var step = 0; step <= flat; step++) points.Add(new PointD(-half + (double)step / flat * width, top));
        const int round = Dense - flat;
        for (var step = 1; step < round; step++)
        {
            var angle = (double)step / round * Math.PI;
            points.Add(new PointD(half * Math.Cos(angle), top + depth * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>The part of a convex outline below the line from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static List<PointD> ClipBelow(List<PointD> points, PointD from, PointD to)
    {
        // Positive below the line, with y growing downward.
        double Side(PointD p) => (to.X - from.X) * (p.Y - from.Y) - (to.Y - from.Y) * (p.X - from.X);
        var clipped = new List<PointD>();
        for (var index = 0; index < points.Count; index++)
        {
            PointD current = points[index], next = points[(index + 1) % points.Count];
            double a = Side(current), b = Side(next);
            if (a >= 0) clipped.Add(current);
            if ((a >= 0) != (b >= 0))
            {
                var share = a / (a - b);
                clipped.Add(new PointD(current.X + (next.X - current.X) * share, current.Y + (next.Y - current.Y) * share));
            }
        }
        return clipped.Count >= 3 ? clipped : points;
    }

    // MARK: Resampling

    /// <summary>
    /// <paramref name="count"/> points evenly spaced along the outline, starting where its top crosses the middle
    /// line and going clockwise on screen. Every eye then has its points in the same places, which is what lets one
    /// become another.
    /// </summary>
    public static PointD[] Resample(IReadOnlyList<PointD> outline, int count)
    {
        if (outline.Count < 3 || count < 3) return outline.ToArray();
        var points = outline.ToList();
        // Clockwise on screen, with y growing downward, is a positive area.
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            PointD a = points[index], b = points[(index + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area < 0) points.Reverse();
        // The start: the highest place where the outline crosses x = 0.
        (int Index, PointD Point)? start = null;
        for (var index = 0; index < points.Count; index++)
        {
            PointD a = points[index], b = points[(index + 1) % points.Count];
            if (!((a.X <= 0 && b.X > 0) || (a.X >= 0 && b.X < 0) || a.X == 0)) continue;
            var share = b.X == a.X ? 0 : -a.X / (b.X - a.X);
            var crossing = new PointD(0, a.Y + (b.Y - a.Y) * share);
            if (start is null || crossing.Y < start.Value.Point.Y) start = (index, crossing);
        }
        if (start is not { } found) return points.Take(count).ToArray();
        var walk = new List<PointD> { found.Point };
        for (var offset = 1; offset <= points.Count; offset++) walk.Add(points[(found.Index + offset) % points.Count]);
        walk.Add(found.Point);
        var lengths = new List<double> { 0 };
        for (var index = 1; index < walk.Count; index++)
            lengths.Add(lengths[index - 1] + Math.Sqrt(Math.Pow(walk[index].X - walk[index - 1].X, 2)
                                                       + Math.Pow(walk[index].Y - walk[index - 1].Y, 2)));
        var total = lengths[^1];
        if (total <= 0) return Enumerable.Repeat(found.Point, count).ToArray();
        var result = new PointD[count];
        var segment = 1;
        for (var step = 0; step < count; step++)
        {
            var target = total * step / count;
            while (segment < lengths.Count - 1 && lengths[segment] < target) segment++;
            var span = lengths[segment] - lengths[segment - 1];
            var share = span > 0 ? (target - lengths[segment - 1]) / span : 0;
            PointD a = walk[segment - 1], b = walk[segment];
            result[step] = new PointD(a.X + (b.X - a.X) * share, a.Y + (b.Y - a.Y) * share);
        }
        return result;
    }

    /// <summary>A closed curve through every point, one cubic per point, with tangents a sixth of the way to the
    /// neighbours (a closed Catmull-Rom spline).</summary>
    public static MascotPath SmoothClosedPath(IReadOnlyList<PointD> points)
    {
        var count = points.Count;
        if (count < 3) return MascotPath.Empty;
        var segments = new MascotSegment[count];
        for (var index = 0; index < count; index++)
        {
            var previous = points[(index - 1 + count) % count];
            var current = points[index];
            var next = points[(index + 1) % count];
            var after = points[(index + 2) % count];
            var first = new PointD(current.X + (next.X - previous.X) / 6, current.Y + (next.Y - previous.Y) / 6);
            var second = new PointD(next.X - (after.X - current.X) / 6, next.Y - (after.Y - current.Y) / 6);
            segments[index] = new MascotCurve(first, second, next);
        }
        return new MascotPath(new MascotFigure(points[0], segments));
    }
}
