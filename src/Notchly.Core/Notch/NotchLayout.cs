// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchSupport.swift (enum NotchLayout: the
// measurements and outlines) and UI/Notch/NotchView.swift (struct NotchShape.path).
namespace Notchly.Core.Notch;

/// <summary>A platform-neutral outline: the app turns it into a WPF StreamGeometry.</summary>
public sealed class NotchPath
{
    public abstract record Segment;
    public sealed record MoveTo(PointD Point) : Segment;
    public sealed record LineTo(PointD Point) : Segment;
    public sealed record CubicTo(PointD Control1, PointD Control2, PointD Point) : Segment;

    private readonly List<Segment> _segments = [];
    public IReadOnlyList<Segment> Segments => _segments;

    public void Move(double x, double y) => _segments.Add(new MoveTo(new PointD(x, y)));
    public void Line(double x, double y) => _segments.Add(new LineTo(new PointD(x, y)));
    public void Curve(double c1x, double c1y, double c2x, double c2y, double x, double y) =>
        _segments.Add(new CubicTo(new PointD(c1x, c1y), new PointD(c2x, c2y), new PointD(x, y)));

    /// <summary>Bounds of every point, control points included (they stay inside for these shapes).</summary>
    public RectD Bounds
    {
        get
        {
            var points = _segments.SelectMany(s => s switch
            {
                MoveTo m => new[] { m.Point },
                LineTo l => [l.Point],
                CubicTo c => [c.Control1, c.Control2, c.Point],
                _ => [],
            }).ToList();
            if (points.Count == 0) return default;
            double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
            return new RectD(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
        }
    }
}

public static class NotchLayout
{
    public const double Shoulder = 14;
    public const double HorizontalInset = 28;
    public const double HeaderHeight = 36;
    public const double NavigationHeight = 36;
    public const double Spacing = 12;
    public const double BottomInset = 16;
    public static double ChromeHeight => HeaderHeight + Spacing + BottomInset;
    public const double CompactContentHeight = 180;
    public const double SpaciousContentHeight = 264;
    public const double PageContentHeight = 320;
    public const double RowSpacing = 10;
    public const double CompactEdgeGap = 5;
    public const int CompactMusicBarCount = 7;
    public const double CompactMusicBarWidth = 1.8;
    public static double CompactMusicBarsWidth =>
        CompactMusicBarWidth * (CompactMusicBarCount + (CompactMusicBarCount - 1) * 0.85);
    public const double MusicArtworkSize = 56;
    public const double CapsuleMargin = 2;
    public const double CapsuleRestingAspect = 5;
    public const double OutlineWidth = 2;
    private const double Tangent = 0.55228475;

    /// <summary>Corners of a surface of this height.</summary>
    public static double SurfaceRadius(double height) => Math.Min(28, height * 0.34);

    /// <summary>The concave flare where the island meets the top edge.</summary>
    public static double ShoulderFor(double height) => Math.Min(Shoulder, height * 0.19);

    /// <summary>A shorter bar leaves less, so the capsule is 20 points tall on a standard 24-point bar.</summary>
    public static double CapsuleGap(double barHeight) =>
        double.IsFinite(barHeight) ? Math.Min(CapsuleMargin, Math.Max(0, Math.Floor((barHeight - 20) / 2))) : 0;

    /// <summary>A closed capsule is round at its ends; an open one keeps the island's corners.</summary>
    public static double CapsuleRadius(double height) => Math.Min(Math.Max(0, height) / 2, 28);

    public static double PreferredWidth(NotchSize layout, double custom) => layout switch
    {
        NotchSize.Compact => 480,
        NotchSize.Spacious => 560,
        _ => custom,
    };

    /// <summary>The capsule body: `gap` inside its surface's top and bottom, between the shoulders.</summary>
    public static RectD CapsuleBody(RectD rect, double gap)
    {
        double width = Math.Max(0, rect.Width), height = Math.Max(0, rect.Height);
        var side = Math.Min(ShoulderFor(height), width / 2);
        var inset = Math.Min(Math.Max(0, gap), height / 2);
        return new RectD(rect.X + side, rect.Y + inset, width - side * 2, height - inset * 2);
    }

    public static NotchPath CapsulePath(RectD rect, double gap)
    {
        var body = CapsuleBody(rect, gap);
        var corner = Math.Min(CapsuleRadius(body.Height), body.Width / 2);
        var handle = corner * (1 - Tangent);
        double left = body.X, right = body.Right, top = body.Y, bottom = body.Bottom;
        var path = new NotchPath();
        path.Move(left + corner, top);
        path.Line(right - corner, top);
        path.Curve(right - handle, top, right, top + handle, right, top + corner);
        path.Line(right, bottom - corner);
        path.Curve(right, bottom - handle, right - handle, bottom, right - corner, bottom);
        path.Line(left + corner, bottom);
        path.Curve(left + handle, bottom, left, bottom - handle, left, bottom - corner);
        path.Line(left, top + corner);
        path.Curve(left, top + handle, left + handle, top, left + corner, top);
        return path;
    }

    /// <summary>
    /// The island's outline in a rect of `size`: a cutout hanging from the top edge with concave
    /// shoulders, or a floating capsule when `floatingGap` is set. Port of NotchShape.path(in:).
    /// </summary>
    public static NotchPath IslandPath(SizeD size, double? floatingGap)
    {
        var rect = new RectD(0, 0, Math.Max(0, size.Width), Math.Max(0, size.Height));
        if (floatingGap is { } gap) return CapsulePath(rect, gap);
        var radius = SurfaceRadius(rect.Height);
        var shoulder = ShoulderFor(rect.Height);
        var bottom = Math.Max(0, Math.Min(radius, Math.Min(rect.Height / 2, (rect.Width - shoulder * 2) / 2)));
        double w = rect.Width, h = rect.Height;
        var path = new NotchPath();
        path.Move(0, 0);
        path.Line(w, 0);
        path.Curve(w - shoulder * Tangent, 0, w - shoulder, shoulder * (1 - Tangent), w - shoulder, shoulder);
        path.Line(w - shoulder, h - bottom);
        path.Curve(w - shoulder, h - bottom * (1 - Tangent), w - shoulder - bottom * (1 - Tangent), h, w - shoulder - bottom, h);
        path.Line(shoulder + bottom, h);
        path.Curve(shoulder + bottom * (1 - Tangent), h, shoulder, h - bottom * (1 - Tangent), shoulder, h - bottom);
        path.Line(shoulder, shoulder);
        path.Curve(shoulder, shoulder * (1 - Tangent), shoulder * Tangent, 0, 0, 0);
        return path;
    }
}
