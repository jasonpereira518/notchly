// SPDX-License-Identifier: GPL-3.0-or-later
// CGRect-style helpers for the screenshot editor model, over the shared RectD/PointD/SizeD.
//
// Upstream's editor geometry is in image points with a TOP-LEFT origin and y growing downward (its SwiftUI canvas is
// flipped before the model sees a point), which is exactly Notchly's convention. No y flipping is needed anywhere in
// the Capture namespace; "top" means the smaller y, "bottom" the larger.
//
// The helpers keep CGRect's semantics where tests depend on them: Contains is half-open (a point on the right or
// bottom edge is outside), an empty rect contains nothing, and a zero-area overlap is not an intersection.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

public static class RectMath
{
    public static double MinX(this RectD r) => r.X;
    public static double MinY(this RectD r) => r.Y;
    public static double MaxX(this RectD r) => r.X + r.Width;
    public static double MaxY(this RectD r) => r.Y + r.Height;
    public static double MidY(this RectD r) => r.Y + r.Height / 2;
    public static PointD Origin(this RectD r) => new(r.X, r.Y);
    public static SizeD Size(this RectD r) => new(r.Width, r.Height);

    public static RectD FromEdges(double minX, double minY, double maxX, double maxY) =>
        new(minX, minY, maxX - minX, maxY - minY);

    public static RectD FromSize(SizeD size) => new(0, 0, size.Width, size.Height);

    /// <summary>CGRect.isEmpty: no area.</summary>
    public static bool IsEmpty(this RectD r) => !(r.Width > 0) || !(r.Height > 0);

    public static RectD Standardized(this RectD r) =>
        new(Math.Min(r.X, r.X + r.Width), Math.Min(r.Y, r.Y + r.Height), Math.Abs(r.Width), Math.Abs(r.Height));

    /// <summary>CGRect.contains(point): half-open, so the right and bottom edges are outside.</summary>
    public static bool ContainsPoint(this RectD r, PointD p) =>
        !r.IsEmpty() && p.X >= r.X && p.X < r.MaxX() && p.Y >= r.Y && p.Y < r.MaxY();

    /// <summary>CGRect.contains(rect).</summary>
    public static bool ContainsRect(this RectD r, RectD other) =>
        !r.IsEmpty() && other.X >= r.X && other.MaxX() <= r.MaxX() && other.Y >= r.Y && other.MaxY() <= r.MaxY();

    /// <summary>CGRect.intersection; null when the rects share no area (CGRectNull).</summary>
    public static RectD? Intersection(this RectD a, RectD b)
    {
        double minX = Math.Max(a.X, b.X), minY = Math.Max(a.Y, b.Y);
        double maxX = Math.Min(a.MaxX(), b.MaxX()), maxY = Math.Min(a.MaxY(), b.MaxY());
        if (!(maxX > minX) || !(maxY > minY)) return null;
        return FromEdges(minX, minY, maxX, maxY);
    }

    public static bool Intersects(this RectD a, RectD b) => a.Intersection(b) is not null;

    public static RectD Union(this RectD a, RectD b) =>
        FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.MaxX(), b.MaxX()), Math.Max(a.MaxY(), b.MaxY()));

    public static RectD OffsetBy(this RectD r, double dx, double dy) => r with { X = r.X + dx, Y = r.Y + dy };

    /// <summary>CGRect.insetBy; a negative inset grows the rect, and an over-inset one comes back empty.</summary>
    public static RectD InsetBy(this RectD r, double dx, double dy) =>
        new(r.X + dx, r.Y + dy, r.Width - 2 * dx, r.Height - 2 * dy);

    /// <summary>CGRect.integral: the smallest whole-pixel rect that covers this one.</summary>
    public static RectD Integral(this RectD r)
    {
        double minX = Math.Floor(r.X), minY = Math.Floor(r.Y);
        return FromEdges(minX, minY, Math.Ceiling(r.MaxX()), Math.Ceiling(r.MaxY()));
    }

    public static PointD Offset(this PointD p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    public static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Swift's FloatingPoint.rounded(): to nearest, ties away from zero (not C#'s banker's default).</summary>
    public static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
