// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-specific: the plain path and affine matrix types the companion's outlines are given in, standing in for
// upstream's CGPath and CGAffineTransform/CATransform3D in NotchMascotSupport.swift and NotchMascotView.swift.
// A WPF PathGeometry and MatrixTransform can be built from them one to one.
namespace Notchly.Core.Notch.Mascot;

/// <summary>One piece of a figure, ending at <see cref="To"/>.</summary>
public abstract record MascotSegment(PointD To);

public sealed record MascotLine(PointD To) : MascotSegment(To);

/// <summary>A cubic Bézier from the previous point.</summary>
public sealed record MascotCurve(PointD Control1, PointD Control2, PointD To) : MascotSegment(To);

/// <summary>A run of segments from <see cref="Start"/>, closed back to it.</summary>
public sealed record MascotFigure(PointD Start, IReadOnlyList<MascotSegment> Segments, bool Closed = true);

/// <summary>An outline of one or more closed figures, filled with the nonzero rule as Core Graphics fills.</summary>
public sealed class MascotPath
{
    public IReadOnlyList<MascotFigure> Figures { get; }

    public MascotPath(IReadOnlyList<MascotFigure> figures) => Figures = figures;

    public MascotPath(params MascotFigure[] figures) => Figures = figures;

    public static readonly MascotPath Empty = new(Array.Empty<MascotFigure>());

    /// <summary>The figures of several paths as one, as a CGMutablePath that adds them.</summary>
    public static MascotPath Combine(params MascotPath[] paths) => new(paths.SelectMany(p => p.Figures).ToArray());

    /// <summary>The tight box around the outline (CGPath.boundingBoxOfPath), curve extremes included.</summary>
    public RectD Bounds
    {
        get
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            void Add(PointD p)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            foreach (var figure in Figures)
            {
                var current = figure.Start;
                Add(current);
                foreach (var segment in figure.Segments)
                {
                    if (segment is MascotCurve curve)
                        foreach (var t in Extremes(current, curve))
                            Add(Point(current, curve, t));
                    Add(segment.To);
                    current = segment.To;
                }
            }
            return double.IsFinite(minX) ? new RectD(minX, minY, maxX - minX, maxY - minY) : new RectD(0, 0, 0, 0);
        }
    }

    /// <summary>Whether the point is inside, by the nonzero winding rule.</summary>
    public bool Contains(PointD point)
    {
        var winding = 0;
        foreach (var polygon in Flattened())
        {
            for (var index = 0; index < polygon.Count; index++)
            {
                var a = polygon[index];
                var b = polygon[(index + 1) % polygon.Count];
                if (a.Y <= point.Y)
                {
                    if (b.Y > point.Y && Cross(a, b, point) > 0) winding++;
                }
                else if (b.Y <= point.Y && Cross(a, b, point) < 0) winding--;
            }
        }
        return winding != 0;
    }

    /// <summary>The same outline with every point moved by <paramref name="matrix"/>; exact for Béziers.</summary>
    public MascotPath Transformed(MascotMatrix matrix) => new(Figures.Select(figure => new MascotFigure(
        matrix.Transform(figure.Start),
        figure.Segments.Select(segment => segment switch
        {
            MascotCurve c => (MascotSegment)new MascotCurve(matrix.Transform(c.Control1), matrix.Transform(c.Control2),
                                                            matrix.Transform(c.To)),
            _ => new MascotLine(matrix.Transform(segment.To)),
        }).ToArray(),
        figure.Closed)).ToArray());

    /// <summary>Each figure as a polygon, curves cut in 24 steps.</summary>
    public IReadOnlyList<IReadOnlyList<PointD>> Flattened(int steps = 24)
    {
        var polygons = new List<IReadOnlyList<PointD>>();
        foreach (var figure in Figures)
        {
            var points = new List<PointD> { figure.Start };
            var current = figure.Start;
            foreach (var segment in figure.Segments)
            {
                if (segment is MascotCurve curve)
                    for (var step = 1; step <= steps; step++) points.Add(Point(current, curve, (double)step / steps));
                else points.Add(segment.To);
                current = segment.To;
            }
            polygons.Add(points);
        }
        return polygons;
    }

    private static double Cross(PointD a, PointD b, PointD p) => (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);

    private static PointD Point(PointD p0, MascotCurve curve, double t)
    {
        var u = 1 - t;
        double Axis(double a, double b, double c, double d) => u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        return new PointD(Axis(p0.X, curve.Control1.X, curve.Control2.X, curve.To.X),
                          Axis(p0.Y, curve.Control1.Y, curve.Control2.Y, curve.To.Y));
    }

    /// <summary>Where the curve turns back along x or y, within it.</summary>
    private static IEnumerable<double> Extremes(PointD p0, MascotCurve curve)
    {
        foreach (var (a, b, c, d) in new[]
                 {
                     (p0.X, curve.Control1.X, curve.Control2.X, curve.To.X),
                     (p0.Y, curve.Control1.Y, curve.Control2.Y, curve.To.Y),
                 })
        {
            // The derivative's coefficients: 3[(−a+3b−3c+d)t² + 2(a−2b+c)t + (b−a)].
            var qa = -a + 3 * b - 3 * c + d;
            var qb = 2 * (a - 2 * b + c);
            var qc = b - a;
            if (Math.Abs(qa) < 1e-12)
            {
                if (Math.Abs(qb) > 1e-12)
                {
                    var t = -qc / qb;
                    if (t is > 0 and < 1) yield return t;
                }
                continue;
            }
            var disc = qb * qb - 4 * qa * qc;
            if (disc < 0) continue;
            var root = Math.Sqrt(disc);
            foreach (var t in new[] { (-qb + root) / (2 * qa), (-qb - root) / (2 * qa) })
                if (t is > 0 and < 1) yield return t;
        }
    }
}

/// <summary>
/// A 2D affine transform in WPF's convention: x' = x·M11 + y·M21 + OffsetX, y' = x·M12 + y·M22 + OffsetY, with y
/// growing downward. <c>a.Then(b)</c> applies a, then b.
/// </summary>
public readonly record struct MascotMatrix(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static readonly MascotMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public static MascotMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static MascotMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>Clockwise on screen for a positive angle, as WPF's RotateTransform and upstream's flipped layers.</summary>
    public static MascotMatrix Rotation(double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary><paramref name="matrix"/> applied about <paramref name="center"/>.</summary>
    public static MascotMatrix About(PointD center, MascotMatrix matrix) =>
        Translation(-center.X, -center.Y).Then(matrix).Then(Translation(center.X, center.Y));

    /// <summary>This transform, then <paramref name="next"/>.</summary>
    public MascotMatrix Then(MascotMatrix next) => new(
        M11 * next.M11 + M12 * next.M21, M11 * next.M12 + M12 * next.M22,
        M21 * next.M11 + M22 * next.M21, M21 * next.M12 + M22 * next.M22,
        OffsetX * next.M11 + OffsetY * next.M21 + next.OffsetX, OffsetX * next.M12 + OffsetY * next.M22 + next.OffsetY);

    public PointD Transform(PointD point) =>
        new(point.X * M11 + point.Y * M21 + OffsetX, point.X * M12 + point.Y * M22 + OffsetY);
}
