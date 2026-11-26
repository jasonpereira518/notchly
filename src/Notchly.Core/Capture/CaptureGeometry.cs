// SPDX-License-Identifier: GPL-3.0-or-later
// CoreGraphics CGRect behaviour that the upstream screenshot logic leans on (intersection, integral,
// half-open containment), expressed on Notchly.Core.Notch.RectD. Coordinates are the Notchly
// convention everywhere: logical points, origin top-left, y down.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>A half-open integer range, as Swift's <c>lower..&lt;upper</c>.</summary>
public readonly record struct IntRange(int Lower, int Upper)
{
    public int Count => Math.Max(0, Upper - Lower);
    public bool IsEmpty => Upper <= Lower;
    public bool Contains(int value) => value >= Lower && value < Upper;
    public override string ToString() => $"{Lower}..<{Upper}";
}

public static class CaptureGeometry
{
    public static readonly RectD Zero = new(0, 0, 0, 0);

    /// <summary>
    /// CGRect.intersection: null (the null rectangle) when the two do not meet, a zero-size
    /// rectangle when they only share an edge.
    /// </summary>
    public static RectD? CgIntersection(this RectD a, RectD b)
    {
        a = a.Standardized();
        b = b.Standardized();
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        if (right < left || bottom < top) return null;
        return new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>CGRect.intersects: a real overlap, so rectangles that only share an edge do not.</summary>
    public static bool CgIntersects(this RectD a, RectD b) =>
        a.CgIntersection(b) is { } i && i.Width > 0 && i.Height > 0;

    /// <summary>The overlapping area, 0 when disjoint or touching.</summary>
    public static double OverlapArea(this RectD a, RectD b) =>
        a.CgIntersection(b) is { } i ? Math.Max(0, i.Width) * Math.Max(0, i.Height) : 0;

    /// <summary>CGRect.contains(rect): every edge of <paramref name="inner"/> lies within or on this one.</summary>
    public static bool CgContainsRect(this RectD outer, RectD inner)
    {
        outer = outer.Standardized();
        inner = inner.Standardized();
        return inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    }

    /// <summary>
    /// CGRect.contains(point): the top and left edges count, the bottom and right ones do not, so
    /// neighbouring windows never both claim one point.
    /// </summary>
    public static bool CgContainsPoint(this RectD rect, PointD point)
    {
        rect = rect.Standardized();
        return point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom;
    }

    /// <summary>The rectangle limited to <paramref name="bounds"/>; the zero rectangle when they do not meet.</summary>
    public static RectD Clamp(this RectD rect, RectD bounds) => rect.CgIntersection(bounds) ?? Zero;
}
