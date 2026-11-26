// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the geometry in upstream Services/QuickTools/ScreenshotSupport.swift: selection handles, rect resizing and
// moving, crop snapping, sticker and counter sizing, arrow heads, arrow silhouettes and strokes, the seeded scribbly
// arrow, segment distance, EditorDrag, and the recognized-word helpers the editor uses (word selection, text runs,
// cropped runs). Everything is in image points, top-left origin, y down (see RectMath).
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>The eight grips of a selected rectangle, in upstream's CaseIterable order.</summary>
public enum SelectionHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

public static class SelectionHandles
{
    public static IReadOnlyList<SelectionHandle> All { get; } = Enum.GetValues<SelectionHandle>();

    public static PointD Position(this SelectionHandle handle, RectD rect) => handle switch
    {
        SelectionHandle.TopLeft => new(rect.MinX(), rect.MinY()),
        SelectionHandle.Top => new(rect.MidX, rect.MinY()),
        SelectionHandle.TopRight => new(rect.MaxX(), rect.MinY()),
        SelectionHandle.Right => new(rect.MaxX(), rect.MidY()),
        SelectionHandle.BottomRight => new(rect.MaxX(), rect.MaxY()),
        SelectionHandle.Bottom => new(rect.MidX, rect.MaxY()),
        SelectionHandle.BottomLeft => new(rect.MinX(), rect.MaxY()),
        _ => new(rect.MinX(), rect.MidY()),
    };
}

/// <summary>One word of recognized text, in image-pixel coordinates.</summary>
public sealed record RecognizedWord(string Text, RectD Rect, int Line);

/// <summary>Seeded wobble for one scribbly arrow: shaft and both wings of the head as polylines.</summary>
public sealed class ScribblyArrowGeometry : IEquatable<ScribblyArrowGeometry>
{
    public IReadOnlyList<PointD> Shaft { get; }
    public IReadOnlyList<PointD> LeftWing { get; }
    public IReadOnlyList<PointD> RightWing { get; }

    public ScribblyArrowGeometry(IReadOnlyList<PointD> shaft, IReadOnlyList<PointD> leftWing, IReadOnlyList<PointD> rightWing)
    {
        Shaft = shaft;
        LeftWing = leftWing;
        RightWing = rightWing;
    }

    public bool Equals(ScribblyArrowGeometry? other) =>
        other is not null && Shaft.SequenceEqual(other.Shaft) && LeftWing.SequenceEqual(other.LeftWing)
        && RightWing.SequenceEqual(other.RightWing);

    public override bool Equals(object? obj) => obj is ScribblyArrowGeometry other && Equals(other);
    public override int GetHashCode() => Shaft.Count;
}

/// <summary>One run of connected points of a stroked arrow; a closed run joins its last point back to its first.</summary>
public sealed record ArrowSubpath(IReadOnlyList<PointD> Points, bool Closed = false);

public struct EditorDrag
{
    private PointD start;
    private bool stayedNearStart;
    private bool endsNearStart;

    /// <summary>
    /// Movement in view points, so clicks keep the same tolerance at every zoom. Only the pen keeps its path when it
    /// returns to the start; other tools still discard a shape whose endpoints form a click.
    /// </summary>
    public void Begin(PointD point)
    {
        start = point;
        stayedNearStart = true;
        endsNearStart = true;
    }

    public void Update(PointD point)
    {
        endsNearStart = RectMath.Distance(point, start) < 7;
        stayedNearStart = stayedNearStart && endsNearStart;
    }

    public readonly bool IsTap(ScreenshotTool tool) => tool == ScreenshotTool.Freehand ? stayedNearStart : endsNearStart;

    /// <summary>A default-constructed drag has not begun: treat it like a fresh press at the origin.</summary>
    public static EditorDrag Started(PointD point)
    {
        var drag = new EditorDrag();
        drag.Begin(point);
        return drag;
    }
}

public static class AnnotationGeometry
{
    // ---- Selection handles --------------------------------------------------------------------------------------

    public static SelectionHandle? HandleAt(PointD point, RectD rect, double tolerance)
    {
        foreach (var handle in SelectionHandles.All)
        {
            var position = handle.Position(rect);
            if (Math.Abs(position.X - point.X) <= tolerance && Math.Abs(position.Y - point.Y) <= tolerance) return handle;
        }
        return null;
    }

    /// <summary>The rectangle after dragging a handle to a point; edges swap when the drag crosses over, so sizes stay non-negative.</summary>
    public static RectD ResizedRect(RectD rect, SelectionHandle handle, PointD point)
    {
        double minX = rect.MinX(), minY = rect.MinY(), maxX = rect.MaxX(), maxY = rect.MaxY();
        switch (handle)
        {
            case SelectionHandle.TopLeft: minX = point.X; minY = point.Y; break;
            case SelectionHandle.Top: minY = point.Y; break;
            case SelectionHandle.TopRight: maxX = point.X; minY = point.Y; break;
            case SelectionHandle.Right: maxX = point.X; break;
            case SelectionHandle.BottomRight: maxX = point.X; maxY = point.Y; break;
            case SelectionHandle.Bottom: maxY = point.Y; break;
            case SelectionHandle.BottomLeft: minX = point.X; maxY = point.Y; break;
            case SelectionHandle.Left: minX = point.X; break;
        }
        return new RectD(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Abs(maxX - minX), Math.Abs(maxY - minY));
    }

    /// <summary>Moves a rect without resizing it, stopping cleanly at the bounds instead of shrinking it through intersection.</summary>
    public static RectD MovedRect(RectD rect, PointD delta, RectD bounds)
    {
        if (!(rect.Width <= bounds.Width) || !(rect.Height <= bounds.Height))
            return rect.Intersection(bounds) ?? default;
        double x = Math.Min(Math.Max(rect.MinX() + delta.X, bounds.MinX()), bounds.MaxX() - rect.Width);
        double y = Math.Min(Math.Max(rect.MinY() + delta.Y, bounds.MinY()), bounds.MaxY() - rect.Height);
        return new RectD(x, y, rect.Width, rect.Height);
    }

    /// <summary>
    /// The pixel rectangle a crop draft stands for. A crop cuts on pixel boundaries, so the chrome, the loupe cross and the
    /// crop itself all read the draft through here. Both edges round to the nearest boundary, which leaves an already
    /// snapped rectangle the same size under a move.
    /// </summary>
    public static RectD PixelSnappedCropRect(RectD rect, RectD bounds)
    {
        double minX = RectMath.Rounded(rect.MinX()), minY = RectMath.Rounded(rect.MinY());
        double maxX = RectMath.Rounded(rect.MaxX()), maxY = RectMath.Rounded(rect.MaxY());
        return Clamp(RectMath.FromEdges(minX, minY, maxX, maxY), bounds);
    }

    public static RectD Clamp(RectD rect, RectD bounds) => rect.Intersection(bounds) ?? default;

    /// <summary>
    /// Rectangle between two drag points. <paramref name="square"/> constrains to the largest square that fits the drag;
    /// <paramref name="fromCenter"/> treats the origin as the center (Alt held).
    /// </summary>
    public static RectD SelectionRect(PointD origin, PointD current, bool square = false, bool fromCenter = false)
    {
        double dx = current.X - origin.X, dy = current.Y - origin.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        if (fromCenter)
            return new RectD(origin.X - Math.Abs(dx), origin.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2);
        return new RectD(Math.Min(origin.X, origin.X + dx), Math.Min(origin.Y, origin.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }

    /// <summary>A full-image crop cannot move, so an interior drag must start a new selection; dragging outside an existing crop replaces it too.</summary>
    public static bool StartsNewCropSelection(PointD point, RectD draft, RectD bounds) =>
        bounds.ContainsPoint(point) && (draft.Standardized() == bounds.Standardized() || !draft.ContainsPoint(point));

    // ---- Sticker and counter sizing -----------------------------------------------------------------------------

    public static double StickerSide(SizeD imageSize, double scale)
    {
        double shortSide = Math.Min(imageSize.Width, imageSize.Height);
        double minimum = Math.Min(52 * scale, shortSide * 0.45);
        return Math.Max(1, Math.Max(minimum, Math.Min(shortSide * 0.16, 128 * scale)));
    }

    public static RectD StickerRect(PointD center, double side, RectD bounds)
    {
        double fitted = Math.Min(Math.Max(1, side), Math.Min(bounds.Width, bounds.Height));
        var rect = new RectD(center.X - fitted / 2, center.Y - fitted / 2, fitted, fitted);
        return MovedRect(rect, new PointD(0, 0), bounds);
    }

    /// <summary>Counter badge diameter for an image, scaling with capture resolution so badges stay readable.</summary>
    public static double CounterDiameter(SizeD imageSize, double scale) =>
        Math.Max(22, Math.Min(imageSize.Width, imageSize.Height) / 24) * scale;

    // ---- Arrows -------------------------------------------------------------------------------------------------

    /// <summary>Arrow head as two wing points for a line ending at the tip. The head grows with the stroke so thick arrows stay proportionate.</summary>
    public static (PointD Left, PointD Right) ArrowHead(PointD tail, PointD tip, double strokeWidth)
    {
        double angle = Math.Atan2(tip.Y - tail.Y, tip.X - tail.X);
        double distance = Math.Sqrt((tip.X - tail.X) * (tip.X - tail.X) + (tip.Y - tail.Y) * (tip.Y - tail.Y));
        double preferred = Math.Max(10, strokeWidth * 3.4);
        double proportional = Math.Max(distance * 0.72, Math.Min(strokeWidth * 1.2, distance));
        double length = Math.Min(preferred, proportional);
        const double spread = Math.PI / 7;
        var left = new PointD(tip.X - length * Math.Cos(angle - spread), tip.Y - length * Math.Sin(angle - spread));
        var right = new PointD(tip.X - length * Math.Cos(angle + spread), tip.Y - length * Math.Sin(angle + spread));
        return (left, right);
    }

    private const int ArcSegments = 16;

    /// <summary>
    /// One continuous outline for the complete solid arrow: shaft, round tail and head on the same contour, so no
    /// winding-rule cutouts appear where independently filled shapes would overlap. The tail's round cap is sampled
    /// into <see cref="ArcSegments"/> straight segments; fill it with the non-zero winding rule.
    /// </summary>
    public static IReadOnlyList<PointD> ArrowSilhouette(PointD tail, PointD tip, double strokeWidth)
    {
        double angle = Math.Atan2(tip.Y - tail.Y, tip.X - tail.X);
        var head = ArrowHead(tail, tip, strokeWidth);
        var baseMid = new PointD((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        double half = strokeWidth / 2;
        var perpendicular = new PointD(-Math.Sin(angle) * half, Math.Cos(angle) * half);

        var points = new List<PointD>
        {
            new(tail.X + perpendicular.X, tail.Y + perpendicular.Y),
            new(baseMid.X + perpendicular.X, baseMid.Y + perpendicular.Y),
            head.Left,
            tip,
            head.Right,
            new(baseMid.X - perpendicular.X, baseMid.Y - perpendicular.Y),
            new(tail.X - perpendicular.X, tail.Y - perpendicular.Y),
        };
        // The cap runs from the far side of the tail round its back (away from the tip) to the near side.
        for (int i = 1; i <= ArcSegments; i++)
        {
            double theta = angle - Math.PI / 2 - Math.PI * i / ArcSegments;
            points.Add(new PointD(tail.X + half * Math.Cos(theta), tail.Y + half * Math.Sin(theta)));
        }
        return points;
    }

    /// <summary>
    /// Shaft and heads of a stroked arrow style as subpaths, so a single stroke draws the whole arrow and casts one
    /// shadow. The solid style is a filled silhouette rather than a stroke and answers null.
    /// </summary>
    public static IReadOnlyList<ArrowSubpath>? ArrowStrokePath(PointD tail, PointD tip, double strokeWidth, ArrowStyle style, ulong seed)
    {
        var head = ArrowHead(tail, tip, strokeWidth);
        switch (style)
        {
            case ArrowStyle.Filled:
                return null;
            case ArrowStyle.Outline:
                return
                [
                    new ArrowSubpath([tail, new PointD((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2)]),
                    new ArrowSubpath([head.Left, tip, head.Right], Closed: true),
                ];
            case ArrowStyle.Open:
                return [new ArrowSubpath([tail, tip]), new ArrowSubpath([head.Left, tip, head.Right])];
            case ArrowStyle.DoubleEnded:
                var tailHead = ArrowHead(tip, tail, strokeWidth);
                return
                [
                    new ArrowSubpath([tail, tip]),
                    new ArrowSubpath([head.Left, tip, head.Right]),
                    new ArrowSubpath([tailHead.Left, tail, tailHead.Right]),
                ];
            default:
                var geometry = ScribblyArrow(tail, tip, strokeWidth, seed);
                return [new ArrowSubpath(geometry.Shaft), new ArrowSubpath(geometry.LeftWing), new ArrowSubpath(geometry.RightWing)];
        }
    }

    /// <summary>
    /// A lightly hand-drawn arrow made from stable, seeded wobble. The seed belongs to the annotation so a redraw or
    /// export keeps the same sketch. The generator is a 64-bit linear congruential one
    /// (state = state * 2862933555777941757 + 3037000493, wrapping; a zero seed becomes 0x9E3779B97F4A7C15) and each
    /// draw is state / 2^64 scaled to -1...1: tiny, deterministic and identical on every platform, so a saved seed
    /// redraws the same arrow anywhere. Do not change the constants or the order of draws (side offset, then forward
    /// offset, shaft first, then left wing, then right wing) without bumping every saved seed.
    /// </summary>
    public static ScribblyArrowGeometry ScribblyArrow(PointD tail, PointD tip, double strokeWidth, ulong seed)
    {
        double dx = tip.X - tail.X, dy = tip.Y - tail.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dy, dx);
        var direction = new PointD(Math.Cos(angle), Math.Sin(angle));
        var perpendicular = new PointD(-direction.Y, direction.X);
        var head = ArrowHead(tail, tip, strokeWidth);
        var baseMid = new PointD((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        var randomizer = new ScribbleRandomizer(seed);
        int shaftSegments = Math.Max(4, Math.Min(24, (int)Math.Ceiling(distance / Math.Max(10, strokeWidth * 3))));
        double shaftWobble = Math.Min(Math.Max(1, strokeWidth * 0.35), distance * 0.025);
        var shaft = RoughPath(tail, baseMid, shaftSegments, direction, perpendicular, shaftWobble, ref randomizer);
        double wingWobble = Math.Min(Math.Max(0.8, strokeWidth * 0.22), distance * 0.035);
        var leftWing = RoughPath(head.Left, tip, 3, null, null, wingWobble, ref randomizer);
        var rightWing = RoughPath(head.Right, tip, 3, null, null, wingWobble, ref randomizer);
        return new ScribblyArrowGeometry(shaft, leftWing, rightWing);
    }

    private struct ScribbleRandomizer
    {
        private ulong state;

        public ScribbleRandomizer(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public double SignedUnit()
        {
            unchecked { state = state * 2862933555777941757UL + 3037000493UL; }
            double normalized = (double)state / (double)ulong.MaxValue;
            return normalized * 2 - 1;
        }
    }

    private static List<PointD> RoughPath(PointD start, PointD end, int segments, PointD? direction, PointD? perpendicular,
                                          double wobble, ref ScribbleRandomizer randomizer)
    {
        double lineX = end.X - start.X, lineY = end.Y - start.Y;
        double length = Math.Sqrt(lineX * lineX + lineY * lineY);
        var pathDirection = direction ?? new PointD(lineX / Math.Max(length, 0.001), lineY / Math.Max(length, 0.001));
        var pathPerpendicular = perpendicular ?? new PointD(-pathDirection.Y, pathDirection.X);
        int count = Math.Max(1, segments);
        var result = new List<PointD>(count + 1);
        for (int index = 0; index <= count; index++)
        {
            double progress = (double)index / count;
            if (index == 0 || index == count)
            {
                result.Add(new PointD(start.X + lineX * progress, start.Y + lineY * progress));
                continue;
            }
            double envelope = Math.Sin(Math.PI * progress);
            double sideOffset = randomizer.SignedUnit() * wobble * envelope;
            double forwardOffset = randomizer.SignedUnit() * wobble * 0.28 * envelope;
            result.Add(new PointD(
                start.X + lineX * progress + pathPerpendicular.X * sideOffset + pathDirection.X * forwardOffset,
                start.Y + lineY * progress + pathPerpendicular.Y * sideOffset + pathDirection.Y * forwardOffset));
        }
        return result;
    }

    /// <summary>The non-zero winding rule for a closed polygon, for hit-testing a silhouette.</summary>
    public static bool PolygonContains(IReadOnlyList<PointD> polygon, PointD point)
    {
        int winding = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            double cross = (b.X - a.X) * (point.Y - a.Y) - (point.X - a.X) * (b.Y - a.Y);
            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && cross > 0) winding++;
            }
            else if (b.Y <= point.Y && cross < 0)
            {
                winding--;
            }
        }
        return winding != 0;
    }

    public static RectD BoundingBox(IEnumerable<ArrowSubpath> subpaths)
    {
        var points = subpaths.SelectMany(s => s.Points).ToList();
        return points.Count == 0
            ? default
            : RectMath.FromEdges(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>Distance from a point to a segment, for hit-testing lines and arrows.</summary>
    public static double Distance(PointD point, PointD start, PointD end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (!(lengthSquared > 0)) return RectMath.Distance(point, start);
        double t = Math.Max(0, Math.Min(1, ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared));
        return RectMath.Distance(point, new PointD(start.X + t * dx, start.Y + t * dy));
    }

    // ---- Recognized text ----------------------------------------------------------------------------------------

    /// <summary>Words a selection drag touches, in reading order. A hairline drag still selects what it crosses.</summary>
    public static List<int> WordSelection(PointD anchor, PointD current, IReadOnlyList<RectD> boxes)
    {
        var rect = SelectionRect(anchor, current).InsetBy(-1, -1);
        var result = new List<int>();
        for (int i = 0; i < boxes.Count; i++) if (boxes[i].Intersects(rect)) result.Add(i);
        return result;
    }

    /// <summary>Joins selected words with spaces inside a line and newlines between lines, whatever order the indexes arrive in.</summary>
    public static string JoinedWords(IReadOnlyList<RecognizedWord> words, IEnumerable<int> selected)
    {
        var picked = selected.OrderBy(i => i).Where(i => i >= 0 && i < words.Count).Select(i => words[i]).ToList();
        if (picked.Count == 0) return "";
        var lines = new List<string>();
        int currentLine = picked[0].Line;
        var currentWords = new List<string>();
        foreach (var word in picked)
        {
            if (word.Line != currentLine)
            {
                lines.Add(string.Join(" ", currentWords));
                currentWords = [];
                currentLine = word.Line;
            }
            currentWords.Add(word.Text);
        }
        lines.Add(string.Join(" ", currentWords));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Recognized words joined into the runs a text only blur area covers. Neighboring words on a line share one run, so
    /// the spaces between them are covered too, and words far apart, like table columns, stay apart. Each run is padded
    /// past the antialiased edges of its glyphs.
    /// </summary>
    public static List<RectD> TextRuns(IEnumerable<RecognizedWord> words)
    {
        var runs = new List<RectD>();
        (RectD Rect, int Line)? current = null;
        foreach (var word in words)
        {
            if (!(word.Rect.Width > 0) || !(word.Rect.Height > 0)) continue;
            if (current is { } run && run.Line == word.Line)
            {
                double height = Math.Max(run.Rect.Height, word.Rect.Height);
                double gap = Math.Max(Math.Max(word.Rect.MinX() - run.Rect.MaxX(), run.Rect.MinX() - word.Rect.MaxX()), 0);
                double overlap = Math.Min(run.Rect.MaxY(), word.Rect.MaxY()) - Math.Max(run.Rect.MinY(), word.Rect.MinY());
                if (gap <= height * 1.5 && overlap >= Math.Min(run.Rect.Height, word.Rect.Height) / 2)
                {
                    current = (run.Rect.Union(word.Rect), run.Line);
                    continue;
                }
            }
            if (current is { } finished) runs.Add(PaddedTextRun(finished.Rect));
            current = (word.Rect, word.Line);
        }
        if (current is { } last) runs.Add(PaddedTextRun(last.Rect));
        return runs;
    }

    private static RectD PaddedTextRun(RectD rect) =>
        // In a tall band recognition can place a box a few pixels off, or stop it short of the glyphs, so the padding
        // above and below is wider; each end reaches about a glyph further.
        rect.InsetBy(-Math.Max(1, rect.Height * 0.6), -Math.Max(1, rect.Height * 0.3));

    /// <summary>Runs moved into a crop's coordinates, keeping those still on it, or null when there were none to move.</summary>
    public static List<RectD>? CroppedRuns(IReadOnlyList<RectD>? runs, RectD crop)
    {
        if (runs is null) return null;
        var bounds = new RectD(0, 0, crop.Width, crop.Height);
        return runs.Select(r => r.OffsetBy(-crop.MinX(), -crop.MinY())).Where(r => r.Intersects(bounds)).ToList();
    }

    /// <summary>
    /// The runs a text only blur area covers: every run that reaches into it. Null runs mean recognition has not read the
    /// capture yet, and the whole area stands in for them, so an early export never leaves text readable.
    /// </summary>
    public static List<RectD> BlurTextRuns(RectD rect, IReadOnlyList<RectD>? runs) =>
        runs is null ? [rect] : runs.Where(r => r.Intersects(rect)).ToList();

    /// <summary>The blur levels the pixelate marks of one style use; only those samples are kept.</summary>
    public static HashSet<int> BlurLevels(IEnumerable<Annotation> annotations, BlurStyle style) =>
        annotations.Where(a => a.Tool == ScreenshotTool.Pixelate && a.BlurStyle == style).Select(a => a.BlurLevel).ToHashSet();

    public static HashSet<int> MosaicLevels(IEnumerable<Annotation> annotations) => BlurLevels(annotations, BlurStyle.Pixelate);
    public static HashSet<int> SoftBlurLevels(IEnumerable<Annotation> annotations) => BlurLevels(annotations, BlurStyle.Blur);
}
