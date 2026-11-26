// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the redaction maths of upstream Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift (pixelBlockSize, BlurStrength)
// and ScreenshotRenderer.swift (pixelatedImage, softBlurredImage, erasePatch and the drawBlurArea clipping). Upstream draws these
// through CoreGraphics and CoreImage; here they work on a raw pixel buffer so the exporter needs no platform code and the results
// can be checked against hand-computed buffers.
//
// Buffers are 8 bit BGRA, premultiplied alpha, top row first (what WPF's Pbgra32 gives), with a row stride in bytes. Rectangles are
// image pixels with a top-left origin. Every operation REPLACES what is under the area, like upstream's copy blend mode, so a
// translucent capture never shows its text through the fill.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>A fill the erase tool made for one area, ready to be drawn or compared: BGRA, top row first, no padding.</summary>
public sealed record ErasePatch(int X, int Y, int Width, int Height, byte[] Bgra)
{
    public byte this[int x, int y, int channel] => Bgra[((y - Y) * Width + (x - X)) * 4 + channel];
}

public static class PixelOps
{
    // MARK: - Blur strength

    public const int MinimumLevel = 1, MaximumLevel = 5, DefaultLevel = 3;

    /// <summary>A stored or passed level kept within 1...5.</summary>
    public static int SanitizedLevel(int level) => Math.Min(Math.Max(level, MinimumLevel), MaximumLevel);

    /// <summary>What the level does to the mosaic block, relative to level 3: 0.4, 0.65, 1, 1.5 and 2.2.</summary>
    public static double BlockFactor(int level) => SanitizedLevel(level) switch
    {
        1 => 0.4,
        2 => 0.65,
        4 => 1.5,
        5 => 2.2,
        _ => 1,
    };

    /// <summary>
    /// Where a new capture's pixelate tool starts: the remembered level, but never a light one. Levels 1 and 2 make blocks smaller
    /// than a line of text, which can stay readable, so they are picked area by area instead of carried into the next redaction.
    /// </summary>
    public static int StartingLevel(int remembered) => Math.Max(SanitizedLevel(remembered), DefaultLevel);

    /// <summary>
    /// Pixelation block size in image pixels, scaled to the capture so small crops and full screens redact equally well. From the
    /// default level up the mosaic carries no legible detail.
    /// </summary>
    public static int BlockSize(SizeD imageSize, int level = DefaultLevel)
    {
        var baseSize = Math.Max(10, (int)(Math.Min(imageSize.Width, imageSize.Height) / 55));
        return Math.Max(2, (int)Math.Round(baseSize * BlockFactor(level), MidpointRounding.AwayFromZero));
    }

    // MARK: - Areas

    private readonly record struct Box(int X0, int Y0, int X1, int Y1)
    {
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    }

    private static void Check(byte[] bgra, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (height - 1) * stride + width * 4)
            throw new ArgumentException("The pixel buffer does not match its width, height and stride.");
    }

    /// <summary>The whole pixels a rectangle touches, kept on the image.</summary>
    private static Box Whole(RectD rect, int width, int height)
    {
        var left = Math.Min(rect.X, rect.Right);
        var right = Math.Max(rect.X, rect.Right);
        var top = Math.Min(rect.Y, rect.Bottom);
        var bottom = Math.Max(rect.Y, rect.Bottom);
        return new Box(
            (int)Math.Clamp(Math.Floor(left), 0, width), (int)Math.Clamp(Math.Floor(top), 0, height),
            (int)Math.Clamp(Math.Ceiling(right), 0, width), (int)Math.Clamp(Math.Ceiling(bottom), 0, height));
    }

    /// <summary>
    /// The rectangles an area acts on: all of it, or with text-only just each recognised run that reaches into it, cut to the area.
    /// Before recognition has read the capture (null runs) the whole area stands in, so nothing is left readable.
    /// </summary>
    private static List<RectD> ActiveRuns(RectD area, bool textOnly, IReadOnlyList<RectD>? textRuns)
    {
        var standard = Standardized(area);
        if (!textOnly) return [standard];
        return [.. ScreenshotRecognition.BlurTextRuns(standard, textRuns)
            .Select(run => RecognitionRects.Intersection(run, standard)).Where(run => run is not null).Select(run => run!.Value)];
    }

    private static RectD Standardized(RectD r) =>
        new(Math.Min(r.X, r.Right), Math.Min(r.Y, r.Bottom), Math.Abs(r.Width), Math.Abs(r.Height));

    // MARK: - Pixelate

    /// <summary>
    /// Covers the area with a mosaic. The grid is that of the whole picture, <c>width / block</c> by <c>height / block</c> cells, so
    /// neighbouring areas line up and a text-only area cuts the same blocks a whole one would. Each block takes the average of
    /// its pixels (upstream shrinks the picture and stretches it back without smoothing). <paramref name="noise"/> shifts every
    /// block by up to 9 levels, so the same text never samples to the same values twice; null leaves the blocks exact.
    /// </summary>
    /// <param name="blockSize">Overrides the block size, which otherwise follows <see cref="BlockSize"/> from the level.</param>
    public static void Pixelate(byte[] bgra, int width, int height, int stride, RectD area, int level = DefaultLevel,
        bool textOnly = false, IReadOnlyList<RectD>? textRuns = null, int? blockSize = null, Random? noise = null,
        SizeD? referenceSize = null)
    {
        Check(bgra, width, height, stride);
        var block = Math.Max(1, blockSize ?? BlockSize(referenceSize ?? new SizeD(width, height), level));
        var cellsX = Math.Max(1, width / block);
        var cellsY = Math.Max(1, height / block);
        var boxes = ActiveRuns(area, textOnly, textRuns).Select(run => Whole(run, width, height)).Where(b => !b.IsEmpty).ToList();
        if (boxes.Count == 0) return;
        // Cells are cut at the ceiling of their share of the picture, so each pixel falls in exactly one.
        int CellStart(int cell, int cells, int length) => (int)(((long)cell * length + cells - 1) / cells);
        int CellOf(int pixel, int cells, int length) => (int)Math.Min(cells - 1, (long)pixel * cells / length);
        var firstCellX = boxes.Min(b => CellOf(b.X0, cellsX, width));
        var lastCellX = boxes.Max(b => CellOf(b.X1 - 1, cellsX, width));
        var firstCellY = boxes.Min(b => CellOf(b.Y0, cellsY, height));
        var lastCellY = boxes.Max(b => CellOf(b.Y1 - 1, cellsY, height));
        var spanX = lastCellX - firstCellX + 1;
        var colors = new byte[spanX * (lastCellY - firstCellY + 1) * 4];
        for (var cy = firstCellY; cy <= lastCellY; cy++)
        {
            for (var cx = firstCellX; cx <= lastCellX; cx++)
            {
                long b = 0, g = 0, r = 0, a = 0, count = 0;
                var x0 = CellStart(cx, cellsX, width);
                var x1 = cx == cellsX - 1 ? width : CellStart(cx + 1, cellsX, width);
                var y0 = CellStart(cy, cellsY, height);
                var y1 = cy == cellsY - 1 ? height : CellStart(cy + 1, cellsY, height);
                for (var y = y0; y < y1; y++)
                {
                    var offset = y * stride + x0 * 4;
                    for (var x = x0; x < x1; x++, offset += 4)
                    {
                        b += bgra[offset]; g += bgra[offset + 1]; r += bgra[offset + 2]; a += bgra[offset + 3];
                        count++;
                    }
                }
                var at = ((cy - firstCellY) * spanX + (cx - firstCellX)) * 4;
                if (count == 0) continue;
                var alpha = (int)Math.Round((double)a / count, MidpointRounding.AwayFromZero);
                var shift = noise is null ? 0 : noise.Next(0, 19) - 9;
                colors[at] = Level((int)Math.Round((double)b / count, MidpointRounding.AwayFromZero) + shift, alpha);
                colors[at + 1] = Level((int)Math.Round((double)g / count, MidpointRounding.AwayFromZero) + shift, alpha);
                colors[at + 2] = Level((int)Math.Round((double)r / count, MidpointRounding.AwayFromZero) + shift, alpha);
                colors[at + 3] = (byte)alpha;
            }
        }
        foreach (var box in boxes)
        {
            for (var y = box.Y0; y < box.Y1; y++)
            {
                var rowCells = (CellOf(y, cellsY, height) - firstCellY) * spanX;
                for (var x = box.X0; x < box.X1; x++)
                {
                    var at = (rowCells + CellOf(x, cellsX, width) - firstCellX) * 4;
                    var offset = y * stride + x * 4;
                    bgra[offset] = colors[at];
                    bgra[offset + 1] = colors[at + 1];
                    bgra[offset + 2] = colors[at + 2];
                    bgra[offset + 3] = colors[at + 3];
                }
            }
        }
    }

    /// <summary>A colour channel in 0...alpha: premultiplied pixels never hold a channel above their alpha.</summary>
    private static byte Level(int value, int alpha) => (byte)Math.Max(0, Math.Min(Math.Min(255, alpha), value));

    // MARK: - Soft blur

    /// <summary>
    /// Blurs the area softly: a Gaussian of about 0.8 blocks (upstream's sample is blurred across about a block), approximated by three
    /// box passes. Edge pixels are extended, so the picture's border never darkens. Pixels outside the area are read but not changed.
    /// </summary>
    /// <param name="sigma">Overrides the Gaussian's standard deviation in pixels, which otherwise is 0.8 of the block size.</param>
    public static void SoftBlur(byte[] bgra, int width, int height, int stride, RectD area, int level = DefaultLevel,
        bool textOnly = false, IReadOnlyList<RectD>? textRuns = null, double? sigma = null, SizeD? referenceSize = null)
    {
        Check(bgra, width, height, stride);
        var deviation = sigma ?? 0.8 * BlockSize(referenceSize ?? new SizeD(width, height), level);
        if (!(deviation > 0) || !double.IsFinite(deviation)) return;
        var boxes = ActiveRuns(area, textOnly, textRuns).Select(run => Whole(run, width, height)).Where(b => !b.IsEmpty).ToList();
        if (boxes.Count == 0) return;
        // Work on the covered pixels plus the reach of the three passes, so the blur sees what lies just outside the area.
        var margin = (int)Math.Ceiling(deviation * 3) + 2;
        var region = new Box(Math.Max(0, boxes.Min(b => b.X0) - margin), Math.Max(0, boxes.Min(b => b.Y0) - margin),
                             Math.Min(width, boxes.Max(b => b.X1) + margin), Math.Min(height, boxes.Max(b => b.Y1) + margin));
        var rw = region.Width;
        var rh = region.Height;
        var planes = new float[4][];
        for (var channel = 0; channel < 4; channel++)
        {
            var plane = planes[channel] = new float[rw * rh];
            for (var y = 0; y < rh; y++)
            {
                var offset = (region.Y0 + y) * stride + region.X0 * 4 + channel;
                for (var x = 0; x < rw; x++, offset += 4) plane[y * rw + x] = bgra[offset];
            }
            foreach (var size in BoxSizes(deviation))
            {
                BoxBlur(plane, rw, rh, (size - 1) / 2, horizontal: true);
                BoxBlur(plane, rw, rh, (size - 1) / 2, horizontal: false);
            }
        }
        foreach (var box in boxes)
        {
            for (var y = box.Y0; y < box.Y1; y++)
            {
                for (var x = box.X0; x < box.X1; x++)
                {
                    var at = (y - region.Y0) * rw + (x - region.X0);
                    var offset = y * stride + x * 4;
                    var alpha = (int)Math.Round(planes[3][at], MidpointRounding.AwayFromZero);
                    bgra[offset + 3] = (byte)Math.Clamp(alpha, 0, 255);
                    for (var channel = 0; channel < 3; channel++)
                        bgra[offset + channel] = Level((int)Math.Round(planes[channel][at], MidpointRounding.AwayFromZero), alpha);
                }
            }
        }
    }

    /// <summary>The three odd box widths whose passes add up to a Gaussian of the given deviation.</summary>
    internal static int[] BoxSizes(double sigma)
    {
        const int passes = 3;
        var ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        lower = Math.Max(1, lower);
        var upper = lower + 2;
        var wide = Math.Round((12 * sigma * sigma - passes * lower * lower - 4 * passes * lower - 3 * passes) / (-4.0 * lower - 4),
            MidpointRounding.AwayFromZero);
        return Enumerable.Range(0, passes).Select(i => i < wide ? lower : upper).ToArray();
    }

    /// <summary>One box blur pass along rows or columns, with the edge pixel extended past the ends.</summary>
    private static void BoxBlur(float[] plane, int width, int height, int radius, bool horizontal)
    {
        if (radius <= 0) return;
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var step = horizontal ? 1 : width;
        var lineStep = horizontal ? width : 1;
        var window = radius * 2 + 1;
        var line = new float[length];
        for (var l = 0; l < lines; l++)
        {
            var origin = l * lineStep;
            for (var i = 0; i < length; i++) line[i] = plane[origin + i * step];
            double sum = 0;
            for (var k = -radius; k <= radius; k++) sum += line[Math.Clamp(k, 0, length - 1)];
            for (var i = 0; i < length; i++)
            {
                plane[origin + i * step] = (float)(sum / window);
                sum += line[Math.Min(length - 1, i + radius + 1)] - line[Math.Max(0, i - radius)];
            }
        }
    }

    // MARK: - Erase

    private enum Edge { Top, Bottom, Left, Right }

    /// <summary>
    /// Paints the area over with a smooth fill made of the pixels just around it (see <see cref="BuildErasePatch"/>). With text-only each
    /// recognised run is filled on its own, reading around the whole run and skipping the other runs of text, so a line is covered
    /// by the flat background around it and not by its neighbour's glyphs.
    /// </summary>
    public static void Erase(byte[] bgra, int width, int height, int stride, RectD area,
        bool textOnly = false, IReadOnlyList<RectD>? textRuns = null)
    {
        Check(bgra, width, height, stride);
        var standard = Standardized(area);
        var runs = textOnly ? ScreenshotRecognition.BlurTextRuns(standard, textRuns) : [standard];
        var skipped = textOnly ? textRuns ?? [] : [];
        // Every fill is read from the picture as it was, so one run's fill never learns from another's.
        var jobs = new List<(ErasePatch Patch, Box Write)>();
        foreach (var run in runs)
        {
            if (BuildErasePatch(bgra, width, height, stride, run, skipped) is not { } patch) continue;
            var write = RecognitionRects.Intersection(run, standard) is { } common ? Whole(common, width, height) : default;
            if (!write.IsEmpty) jobs.Add((patch, write));
        }
        foreach (var (patch, write) in jobs)
        {
            for (var y = Math.Max(write.Y0, patch.Y); y < Math.Min(write.Y1, patch.Y + patch.Height); y++)
            {
                for (var x = Math.Max(write.X0, patch.X); x < Math.Min(write.X1, patch.X + patch.Width); x++)
                {
                    Array.Copy(patch.Bgra, ((y - patch.Y) * patch.Width + (x - patch.X)) * 4, bgra, y * stride + x * 4, 4);
                }
            }
        }
    }

    /// <summary>
    /// A smooth fill for <paramref name="region"/> made of the pixels just around it. Each side is read as medians along a thin strip
    /// past that edge, which ignores the odd glyph touching it, and every point blends the sides by how close it is to each. A flat
    /// background comes back exactly, and so does a straight gradient. Sides past the edge of the picture are left out, and an area
    /// as large as the picture reads its own border instead. Pixels inside <paramref name="skipping"/> (the other runs of recognised
    /// text) are not background and are skipped while anything else is left to read. Null when there is nothing to read.
    /// </summary>
    public static ErasePatch? BuildErasePatch(byte[] bgra, int width, int height, int stride, RectD region,
        IReadOnlyList<RectD>? skipping = null)
    {
        Check(bgra, width, height, stride);
        var area = Whole(region, width, height);
        if (area.Width < 1 || area.Height < 1) return null;
        var band = Math.Max(2, Math.Min(8, (int)Math.Round(Math.Min(area.Width, area.Height) * 0.12, MidpointRounding.AwayFromZero)));
        var sides = new Dictionary<Edge, EraseSide>();
        foreach (var (outside, skipped) in new (bool, IReadOnlyList<RectD>)[]
                 { (true, skipping ?? []), (true, []), (false, []) })
        {
            if (sides.Count > 0) break;
            foreach (var edge in Enum.GetValues<Edge>())
                if (EraseSide.Create(edge, area, band, outside, skipped, bgra, width, height, stride) is { } side) sides[edge] = side;
        }
        if (sides.Count == 0) return null;

        var w = area.Width;
        var h = area.Height;
        // 1 / distance blends each pair of opposite sides linearly. A missing side weighs nothing.
        float[] Weights(Edge edge, Func<int, float> distance, int count)
        {
            var result = new float[count];
            if (sides.TryGetValue(edge, out var side))
                for (var i = 0; i < count; i++) result[i] = 1f / Math.Max(0.5f, distance(i) + side.Offset);
            return result;
        }
        float[] Colors(Edge edge, int count, float length) =>
            sides.TryGetValue(edge, out var side) ? side.ColorsAt(count, length) : new float[count * 4];
        var top = Colors(Edge.Top, w, w);
        var bottom = Colors(Edge.Bottom, w, w);
        var left = Colors(Edge.Left, h, h);
        var right = Colors(Edge.Right, h, h);
        var topWeights = Weights(Edge.Top, row => row + 0.5f, h);
        var bottomWeights = Weights(Edge.Bottom, row => h - (row + 0.5f), h);
        var leftWeights = Weights(Edge.Left, column => column + 0.5f, w);
        var rightWeights = Weights(Edge.Right, column => w - (column + 0.5f), w);

        var bytes = new byte[w * h * 4];
        Span<float> color = stackalloc float[4];
        for (var row = 0; row < h; row++)
        {
            for (var column = 0; column < w; column++)
            {
                var sum = topWeights[row] + bottomWeights[row] + leftWeights[column] + rightWeights[column];
                for (var channel = 0; channel < 4; channel++)
                    color[channel] = (topWeights[row] * top[column * 4 + channel]
                                      + bottomWeights[row] * bottom[column * 4 + channel]
                                      + leftWeights[column] * left[row * 4 + channel]
                                      + rightWeights[column] * right[row * 4 + channel]) / sum;
                var offset = (row * w + column) * 4;
                var alpha = (int)Math.Min(255f, Math.Max(0f, MathF.Round(color[3], MidpointRounding.AwayFromZero)));
                bytes[offset + 3] = (byte)alpha;
                for (var channel = 0; channel < 3; channel++)
                    bytes[offset + channel] = (byte)Math.Min(alpha, Math.Max(0f, MathF.Round(color[channel], MidpointRounding.AwayFromZero)));
            }
        }
        return new ErasePatch(area.X0, area.Y0, w, h, bytes);
    }

    /// <summary>
    /// One side of an area being erased: the medians of a thin strip of pixels along that edge, BGRA in order, and how far the
    /// strip's middle sits from the edge.
    /// </summary>
    private sealed class EraseSide
    {
        private readonly float[] _medians;
        private readonly int _count;
        public float Offset { get; }

        private EraseSide(float[] medians, int count, float offset)
        {
            _medians = medians;
            _count = count;
            Offset = offset;
        }

        /// <summary>The strip's colour across from each pixel centre, between the two nearest medians, BGRA in order.</summary>
        public float[] ColorsAt(int pixels, float length)
        {
            var colors = new float[pixels * 4];
            for (var index = 0; index < pixels; index++)
            {
                var position = index + 0.5f;
                var place = Math.Min(Math.Max(position / length * _count - 0.5f, 0), _count - 1);
                var lower = (int)place;
                var fraction = place - lower;
                var first = lower * 4;
                var second = Math.Min(lower + 1, _count - 1) * 4;
                for (var channel = 0; channel < 4; channel++)
                    colors[index * 4 + channel] = _medians[first + channel] + (_medians[second + channel] - _medians[first + channel]) * fraction;
            }
            return colors;
        }

        /// <summary>About one median per few strip widths: enough to follow a gradient, long enough for a stray glyph to stay a minority.</summary>
        internal static int SegmentCount(int length, int band) => Math.Max(1, Math.Min(48, (int)(length / (double)Math.Max(12, band * 4))));

        public static EraseSide? Create(Edge edge, Box area, int band, bool outside, IReadOnlyList<RectD> text,
            byte[] bgra, int width, int height, int stride)
        {
            var thickness = outside ? band : Math.Min(band, Math.Min(area.Width, area.Height));
            var strip = (edge, outside) switch
            {
                (Edge.Top, true) => new Box(area.X0, area.Y0 - thickness, area.X1, area.Y0),
                (Edge.Bottom, true) => new Box(area.X0, area.Y1, area.X1, area.Y1 + thickness),
                (Edge.Left, true) => new Box(area.X0 - thickness, area.Y0, area.X0, area.Y1),
                (Edge.Right, true) => new Box(area.X1, area.Y0, area.X1 + thickness, area.Y1),
                (Edge.Top, false) => new Box(area.X0, area.Y0, area.X1, area.Y0 + thickness),
                (Edge.Bottom, false) => new Box(area.X0, area.Y1 - thickness, area.X1, area.Y1),
                (Edge.Left, false) => new Box(area.X0, area.Y0, area.X0 + thickness, area.Y1),
                _ => new Box(area.X1 - thickness, area.Y0, area.X1, area.Y1),
            };
            var visible = new Box(Math.Max(0, strip.X0), Math.Max(0, strip.Y0), Math.Min(width, strip.X1), Math.Min(height, strip.Y1));
            if (visible.Width < 1 || visible.Height < 1) return null;
            var alongX = edge is Edge.Top or Edge.Bottom;
            var stripWidth = visible.Width;
            var length = alongX ? stripWidth : visible.Height;
            var depth = alongX ? visible.Height : stripWidth;
            var count = SegmentCount(length, band);
            var visibleRect = new RectD(visible.X0, visible.Y0, visible.Width, visible.Height);
            var skipped = text.Where(run => RecognitionRects.Intersects(run, visibleRect)).ToList();
            // Up to 32 pixels per segment, spread along it and across the strip, are plenty for a median.
            const int limit = 32;
            var medians = new float[count * 4];
            var found = new bool[count];
            var samples = new byte[4 * limit];
            for (var segment = 0; segment < count; segment++)
            {
                var start = segment * length / count;
                var span = Math.Max(1, (segment + 1) * length / count - start);
                var attempts = Math.Min(limit, span * depth);
                var taken = 0;
                var flat = true;
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    var along = start + attempt * span / attempts;
                    var across = attempt % depth;
                    var column = alongX ? along : across;
                    var row = alongX ? across : along;
                    if (skipped.Count > 0)
                    {
                        var x = visible.X0 + column + 0.5;
                        var y = visible.Y0 + row + 0.5;
                        if (skipped.Any(run => RecognitionRects.ContainsPoint(run, x, y))) continue;
                    }
                    var pixel = (visible.Y0 + row) * stride + (visible.X0 + column) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = bgra[pixel + channel];
                        samples[channel * limit + taken] = value;
                        if (value != samples[channel * limit]) flat = false;
                    }
                    taken++;
                }
                if (taken == 0) continue;
                found[segment] = true;
                for (var channel = 0; channel < 4; channel++)
                {
                    var first = channel * limit;
                    if (flat) medians[segment * 4 + channel] = samples[first];
                    else
                    {
                        var sorted = samples.AsSpan(first, taken).ToArray();
                        Array.Sort(sorted);
                        medians[segment * 4 + channel] = sorted[taken / 2];
                    }
                }
            }
            if (!found.Contains(true)) return null;
            // A segment covered by text takes the median of the nearest one that was read.
            for (var segment = 0; segment < count; segment++)
            {
                if (found[segment]) continue;
                var nearest = segment;
                var distance = int.MaxValue;
                for (var other = 0; other < count; other++)
                    if (found[other] && Math.Abs(other - segment) < distance) { nearest = other; distance = Math.Abs(other - segment); }
                for (var channel = 0; channel < 4; channel++) medians[segment * 4 + channel] = medians[nearest * 4 + channel];
            }
            // Inside strips stand on the edge itself, outside ones half their thickness past it.
            return new EraseSide(medians, count, outside ? depth / 2f : 0);
        }
    }
}
