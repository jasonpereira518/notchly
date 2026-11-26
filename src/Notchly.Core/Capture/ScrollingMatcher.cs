// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the "Scrolling capture" matching section of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift: ScrollingSample, ScrollingDirection,
// ScrollingTransition, scrollingSamplesAreStable, scrollingTransition (+ scrollingMotion,
// scrollingCandidate, scrollingMatch, scrollingColumnTiles, scrollingDifference),
// scrollingFixedBottomRows, scrollingNewContentRows and scrollingPixelRange.
//
// The algorithm is unchanged. The one implementation difference is that upstream computed the
// per-offset pixel differences with Accelerate (vDSP_vsub / vDSP_vabs / vDSP_desamp) on floats; here
// they are plain integer sums, which give identical results because every value involved is a whole
// number far below 2^24.
namespace Notchly.Core.Capture;

/// <summary>A small grayscale picture of one scrolled view, one byte per pixel, rows top to bottom.</summary>
public sealed class ScrollingSample
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public ScrollingSample(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public bool IsValid => Width > 0 && Height > 0 && Pixels.Length == Width * Height;
}

public enum ScrollingDirection { Forward, Backward }

public enum ScrollingTransitionKind { End, Advanced, Unmatched }

/// <summary>How the current view relates to the previous one.</summary>
/// <param name="Overlap">Advanced only: rows the two views share.</param>
/// <param name="ContentColumns">Advanced only: the sample columns that moved.</param>
public readonly record struct ScrollingTransition(ScrollingTransitionKind Kind, int Overlap = 0,
                                                  ScrollingDirection Direction = ScrollingDirection.Forward,
                                                  IntRange ContentColumns = default)
{
    public static readonly ScrollingTransition End = new(ScrollingTransitionKind.End);
    public static readonly ScrollingTransition Unmatched = new(ScrollingTransitionKind.Unmatched);

    public static ScrollingTransition Advanced(int overlap, ScrollingDirection direction, IntRange contentColumns) =>
        new(ScrollingTransitionKind.Advanced, overlap, direction, contentColumns);

    public override string ToString() => Kind == ScrollingTransitionKind.Advanced
        ? $"advanced(overlap: {Overlap}, {Direction}, {ContentColumns})" : Kind.ToString();
}

public static class ScrollingStitch
{
    private const double MovementThreshold = 1.5;

    /// <summary>
    /// Two views are the same when no column tile differs by more than 1.5 grey levels on average.
    /// Empty space must not dilute movement in a narrow column of text, hence the tiles.
    /// </summary>
    public static bool SamplesAreStable(ScrollingSample previous, ScrollingSample current, IntRange? contentColumns = null)
    {
        if (!previous.IsValid || !current.IsValid || previous.Width != current.Width || previous.Height != current.Height) return false;
        var columns = contentColumns ?? new IntRange(0, previous.Width);
        if (columns.Lower < 0 || columns.Upper > previous.Width || columns.IsEmpty) return false;
        foreach (var tile in ColumnTiles(columns, previous.Width))
            if (Difference(previous, current, tile) > MovementThreshold) return false;
        return true;
    }

    /// <summary>
    /// Finds how many rows two successive views share. Pure: captures only provide small grayscale
    /// samples. The search is O(advance range x rows x columns), bounded to 32 columns.
    /// </summary>
    public static ScrollingTransition Transition(ScrollingSample previous, ScrollingSample current,
                                                 IntRange? contentColumns = null, CancellationToken cancellation = default)
    {
        if (!previous.IsValid || !current.IsValid || previous.Width != current.Width
            || previous.Height != current.Height || previous.Height < 24)
            return ScrollingTransition.Unmatched;

        var columns = contentColumns ?? new IntRange(0, previous.Width);
        if (columns.Lower < 0 || columns.Upper > previous.Width || columns.IsEmpty) return ScrollingTransition.Unmatched;

        if (SamplesAreStable(previous, current, columns)) return ScrollingTransition.End;

        var height = previous.Height;
        // The final scroll at the bottom of a page is often only a few rows, so any advance above 1%
        // (at least two rows) is a candidate.
        var minimumAdvance = Math.Max(2, (int)Math.Round(height * 0.01, MidpointRounding.AwayFromZero));
        var maximumAdvance = Math.Min(height - 8, (int)Math.Round(height * 0.88, MidpointRounding.AwayFromZero));
        if (minimumAdvance > maximumAdvance) return ScrollingTransition.Unmatched;

        var tiles = ColumnTiles(columns, previous.Width);
        if (previous.Width > 32) return TransitionOnWideSample(previous, current, columns, tiles, cancellation);

        var movingTiles = tiles.Where(t => Difference(previous, current, t) > MovementThreshold).ToList();
        if (movingTiles.Count == 0) return ScrollingTransition.Unmatched;

        var differences = new int[previous.Pixels.Length];
        var matches = new List<Match>();
        for (var advance = minimumAdvance; advance <= maximumAdvance; advance++)
        {
            if (cancellation.IsCancellationRequested) return ScrollingTransition.Unmatched;
            for (var pass = 0; pass < 2; pass++)
            {
                var reversed = pass == 1;
                var count = (height - advance) * previous.Width;
                // Forward: previous row r + advance equals current row r. Backward: current row
                // r + advance equals previous row r.
                var lhs = reversed ? 0 : advance * previous.Width;
                var rhs = reversed ? advance * previous.Width : 0;
                var prevPixels = previous.Pixels;
                var curPixels = current.Pixels;
                for (var i = 0; i < count; i++)
                {
                    var d = prevPixels[lhs + i] - curPixels[rhs + i];
                    differences[i] = d < 0 ? -d : d;
                }
                if (BestCandidate(previous, advance, movingTiles, differences) is not { } c) continue;
                matches.Add(new Match(advance, reversed, c.ContentColumns, c.SupportingTiles, c.LongestRun, c.MatchingRows, c.Difference));
            }
        }
        if (matches.Count == 0) return ScrollingTransition.Unmatched;
        // A stable sort keeps the earlier (smaller advance) candidate on exact ties.
        var ordered = matches.OrderBy(m => m, Comparer<Match>.Create(CompareMatches)).ToList();

        var best = ordered[0];
        var requiredRun = Math.Max(8, Math.Min(28, height / 12));
        if (best.LongestRun < requiredRun) return ScrollingTransition.Unmatched;

        // Repeated content can match equally well over different lengths. Require comparable support so
        // a short blank band does not veto a longer match, but reject any equally precise, substantial
        // rival. The margin shrinks for precise matches: a low-contrast approximation must not veto an
        // exact overlap.
        var ambiguityTolerance = Math.Min(0.75, best.Difference);
        for (var i = 1; i < ordered.Count; i++)
        {
            var rival = ordered[i];
            if ((rival.Reversed != best.Reversed || Math.Abs(rival.Advance - best.Advance) > 2)
                && rival.ContentColumns.Count >= best.ContentColumns.Count - 1
                && rival.SupportingTiles >= best.SupportingTiles - 1
                && rival.LongestRun >= Math.Max(requiredRun, best.LongestRun / 2)
                && rival.MatchingRows >= Math.Max(requiredRun, best.MatchingRows / 2)
                && rival.Difference <= best.Difference + ambiguityTolerance)
                return ScrollingTransition.Unmatched;
        }
        return best.Reversed
            ? Motion(current, previous, best.Advance, best.ContentColumns, ScrollingDirection.Backward)
            : Motion(previous, current, best.Advance, best.ContentColumns, ScrollingDirection.Forward);
    }

    private static int CompareMatches(Match a, Match b)
    {
        if (a.ContentColumns.Count != b.ContentColumns.Count) return b.ContentColumns.Count.CompareTo(a.ContentColumns.Count);
        if (a.SupportingTiles != b.SupportingTiles) return b.SupportingTiles.CompareTo(a.SupportingTiles);
        // Similar digits can pass the row tolerance at a wrong offset. Prefer the closer pixel match
        // before rewarding a longer overlap, which otherwise biases sparse text toward a shorter scroll.
        if (a.Difference != b.Difference) return a.Difference.CompareTo(b.Difference);
        if (a.LongestRun != b.LongestRun) return b.LongestRun.CompareTo(a.LongestRun);
        return b.MatchingRows.CompareTo(a.MatchingRows);
    }

    /// <summary>
    /// Keep native detail where pixels actually change, instead of shrinking a wide terminal until
    /// different digits look alike: the offset search runs on at most 32 representative columns.
    /// </summary>
    private static ScrollingTransition TransitionOnWideSample(ScrollingSample previous, ScrollingSample current,
                                                              IntRange columns, List<IntRange> tiles,
                                                              CancellationToken cancellation)
    {
        var selected = new List<(int Column, IntRange Bounds)>();
        if (columns.Count <= 32)
        {
            for (var c = columns.Lower; c < columns.Upper; c++) selected.Add((c, new IntRange(c, c + 1)));
        }
        else
        {
            var perTile = Math.Max(1, 32 / tiles.Count);
            foreach (var tile in tiles)
            {
                var ranked = new List<(int Column, double Difference)>();
                for (var c = tile.Lower; c < tile.Upper; c++)
                    ranked.Add((c, Difference(previous, current, new IntRange(c, c + 1))));
                var chosen = ranked
                    .OrderByDescending(r => r.Difference).ThenBy(r => r.Column)
                    .Take(perTile).Select(r => r.Column).OrderBy(c => c);
                foreach (var column in chosen) selected.Add((column, tile));
            }
        }

        ScrollingSample Compact(ScrollingSample sample)
        {
            var pixels = new byte[selected.Count * sample.Height];
            var at = 0;
            for (var row = 0; row < sample.Height; row++)
            {
                var start = row * sample.Width;
                foreach (var selection in selected) pixels[at++] = sample.Pixels[start + selection.Column];
            }
            return new ScrollingSample(selected.Count, sample.Height, pixels);
        }

        var transition = Transition(Compact(previous), Compact(current), null, cancellation);
        if (transition.Kind == ScrollingTransitionKind.Advanced)
        {
            var lower = selected[transition.ContentColumns.Lower].Bounds.Lower;
            var upper = selected[transition.ContentColumns.Upper - 1].Bounds.Upper;
            return ScrollingTransition.Advanced(transition.Overlap, transition.Direction, new IntRange(lower, upper));
        }
        return transition;
    }

    private static ScrollingTransition Motion(ScrollingSample previous, ScrollingSample current, int advance,
                                              IntRange columns, ScrollingDirection direction)
    {
        var overlap = previous.Height - advance;
        int firstChange = current.Height, lastChange = -1;
        int firstMoving = current.Height, lastMoving = -1, moving = 0;
        var stationaryRows = new HashSet<int>();
        var stationaryCount = 0;
        var unexplainedChange = false;
        for (var row = 0; row < current.Height; row++)
        {
            int aligned = 0, shifted = 0;
            for (var column = columns.Lower; column < columns.Upper; column++)
            {
                var index = row * current.Width + column;
                var pixel = current.Pixels[index];
                aligned += Math.Abs(previous.Pixels[index] - pixel);
                if (row < overlap) shifted += Math.Abs(previous.Pixels[index + advance * previous.Width] - pixel);
            }
            var alignedError = (double)aligned / columns.Count;
            if (alignedError > MovementThreshold)
            {
                firstChange = Math.Min(firstChange, row);
                lastChange = row;
            }
            if (row >= overlap) continue;
            var shiftedError = (double)shifted / columns.Count;
            if (shiftedError <= 8 && alignedError > shiftedError + MovementThreshold)
            {
                firstMoving = Math.Min(firstMoving, row);
                lastMoving = row;
                moving++;
            }
            else if (alignedError <= MovementThreshold && shiftedError > alignedError + MovementThreshold)
            {
                stationaryRows.Add(row);
                stationaryCount++;
            }
            else if (alignedError > MovementThreshold && shiftedError > 8)
            {
                unexplainedChange = true;
            }
        }
        if (moving == 0) return ScrollingTransition.Unmatched;
        if (lastChange - firstChange + 1 < current.Height / 2)
        {
            // A moving banner between stationary document features is not a wheel advance. On a blank
            // background we cannot identify the document, but a clipped animation cannot explain every
            // changed row at the proposed offset. A sparse line sliding intact can.
            if (stationaryCount > moving && stationaryRows.Contains(firstChange - 1) && stationaryRows.Contains(lastChange + 1))
                return ScrollingTransition.End;
            if (unexplainedChange || lastChange >= overlap) return ScrollingTransition.Unmatched;
        }
        // Fixed headers and footers are outside the moving content. Their texture must never outvote a
        // few widely spaced lines of real text.
        var inMovingRegion = 0;
        foreach (var row in stationaryRows) if (row >= firstMoving && row <= lastMoving) inMovingRegion++;
        if (inMovingRegion > moving) return ScrollingTransition.Unmatched;
        return ScrollingTransition.Advanced(overlap, direction, columns);
    }

    /// <summary>
    /// A fixed footer stays at the same viewport rows while the page behind it advances. Keeping that
    /// suffix in every new strip would repeat it throughout the final image, so it is identified
    /// separately from the moving overlap. Returns the footer's height in sample rows, 0 if none.
    /// </summary>
    public static int FixedBottomRows(ScrollingSample previous, ScrollingSample current, int overlap, IntRange contentColumns)
    {
        if (!previous.IsValid || !current.IsValid || previous.Width != current.Width || previous.Height != current.Height
            || overlap <= 0 || overlap >= previous.Height
            || contentColumns.Lower < 0 || contentColumns.Upper > previous.Width || contentColumns.IsEmpty)
            return 0;

        var rows = 0;
        for (var row = previous.Height - 1; row >= 0; row--)
        {
            var start = row * previous.Width;
            var difference = 0;
            for (var column = contentColumns.Lower; column < contentColumns.Upper; column++)
                difference += Math.Abs(previous.Pixels[start + column] - current.Pixels[start + column]);
            var average = (double)difference / contentColumns.Count;
            if (average > 2) break;
            rows++;
        }

        var minimumRows = Math.Max(4, Math.Min(12, previous.Height / 100));
        return rows >= minimumRows && rows < overlap ? rows : 0;
    }

    /// <summary>
    /// Rows newly revealed by a forward scroll. A fixed footer shifts this range upward by its own
    /// height; its pixels are appended once at the end.
    /// </summary>
    public static IntRange? NewContentRows(int imageHeight, int overlap, int fixedBottomRows)
    {
        if (!(imageHeight > 0 && overlap > 0 && overlap < imageHeight && fixedBottomRows >= 0 && fixedBottomRows < overlap)) return null;
        return new IntRange(overlap - fixedBottomRows, imageHeight - fixedBottomRows);
    }

    /// <summary>Maps the moving sample columns back to the exact captured pixel columns.</summary>
    public static IntRange? PixelRange(IntRange sampleColumns, int sampleWidth, int imageWidth)
    {
        if (!(sampleWidth > 0 && imageWidth > 0 && sampleColumns.Lower >= 0 && sampleColumns.Upper <= sampleWidth && !sampleColumns.IsEmpty))
            return null;
        var lower = sampleColumns.Lower * imageWidth / sampleWidth;
        var upper = (sampleColumns.Upper * imageWidth + sampleWidth - 1) / sampleWidth;
        if (!(lower >= 0 && upper <= imageWidth && lower < upper)) return null;
        return new IntRange(lower, upper);
    }

    // ---- Internals -------------------------------------------------------------------------------

    private readonly record struct Match(int Advance, bool Reversed, IntRange ContentColumns, int SupportingTiles,
                                         int LongestRun, int MatchingRows, double Difference);

    private readonly record struct Candidate(IntRange ContentColumns, int SupportingTiles, int LongestRun,
                                             int MatchingRows, double Difference);

    private readonly record struct RowMatch(int LongestRun, int MatchingRows, int ComparedRows, double Difference);

    /// <summary>Mean absolute difference over the columns, ignoring the top and bottom 1/24 of the rows.</summary>
    private static double Difference(ScrollingSample lhs, ScrollingSample rhs, IntRange columns)
    {
        if (columns.Lower < 0 || columns.Upper > lhs.Width || columns.IsEmpty) return double.PositiveInfinity;
        var topInset = Math.Max(0, lhs.Height / 24);
        long difference = 0;
        var count = 0L;
        for (var row = topInset; row < lhs.Height - topInset; row++)
        {
            var start = row * lhs.Width;
            for (var column = columns.Lower; column < columns.Upper; column++)
            {
                difference += Math.Abs(lhs.Pixels[start + column] - rhs.Pixels[start + column]);
                count++;
            }
        }
        return count > 0 ? (double)difference / count : double.PositiveInfinity;
    }

    private static List<IntRange> ColumnTiles(IntRange columns, int sampleWidth)
    {
        var tileWidth = Math.Max(2, sampleWidth / 8);
        var tiles = new List<IntRange>();
        var lower = columns.Lower;
        while (lower < columns.Upper)
        {
            var upper = Math.Min(columns.Upper, lower + tileWidth);
            if (upper - lower >= 2 || tiles.Count == 0) tiles.Add(new IntRange(lower, upper));
            lower = upper;
        }
        return tiles;
    }

    private static Candidate? BestCandidate(ScrollingSample previous, int advance, List<IntRange> tiles, int[] differences)
    {
        var requiredRun = Math.Max(8, Math.Min(28, previous.Height / 12));
        var matches = new List<(IntRange Tile, RowMatch? Match)>(tiles.Count);
        foreach (var tile in tiles)
        {
            var match = RowMatching(previous, advance, tile, differences);
            if (match is { } m && m.LongestRun >= requiredRun && m.MatchingRows >= Math.Max(requiredRun, m.ComparedRows / 3))
                matches.Add((tile, m));
            else
                matches.Add((tile, null));
        }

        var minimumTiles = matches.Count >= 3 ? 2 : 1;
        var runs = new List<List<IntRange>>();
        var run = new List<IntRange>();
        var skippedOneTile = false;
        foreach (var (tile, match) in matches)
        {
            if (match is not null)
            {
                run.Add(tile);
            }
            else if (run.Count > 0 && !skippedOneTile)
            {
                skippedOneTile = true;
            }
            else
            {
                if (run.Count > 0) runs.Add(run);
                run = [];
                skippedOneTile = false;
            }
        }
        if (run.Count > 0) runs.Add(run);

        Candidate? best = null;
        foreach (var supported in runs)
        {
            if (supported.Count < minimumTiles) continue;
            var contentColumns = new IntRange(supported[0].Lower, supported[^1].Upper);
            if (RowMatching(previous, advance, contentColumns, differences) is not { } combined) continue;
            if (!(combined.LongestRun >= requiredRun && combined.MatchingRows >= Math.Max(requiredRun, combined.ComparedRows / 3))) continue;
            var candidate = new Candidate(contentColumns, supported.Count, combined.LongestRun, combined.MatchingRows, combined.Difference);
            // Wider, then more matching rows, then the smaller difference; the first of equals wins.
            if (best is not { } current || IsBetter(candidate, current)) best = candidate;
        }
        return best;
    }

    private static bool IsBetter(Candidate a, Candidate b)
    {
        if (a.ContentColumns.Count != b.ContentColumns.Count) return a.ContentColumns.Count > b.ContentColumns.Count;
        if (a.MatchingRows != b.MatchingRows) return a.MatchingRows > b.MatchingRows;
        return a.Difference < b.Difference;
    }

    private static RowMatch? RowMatching(ScrollingSample previous, int advance, IntRange columns, int[] differences)
    {
        var width = previous.Width;
        // Trim the shared rows, not the whole viewport. A viewport-sized inset can erase a valid
        // smaller overlap and strand a long capture on its last accepted frame.
        var edgeInset = Math.Max(2, (previous.Height - advance) / 10);
        var lastRow = previous.Height - advance - edgeInset;
        if (!(lastRow > edgeInset && columns.Lower >= 0 && columns.Upper <= width && !columns.IsEmpty)) return null;

        var rowCount = lastRow - edgeInset;
        var threshold = columns.Count * 8;
        int longestRun = 0, run = 0, matchingRows = 0;
        long totalDifference = 0;
        for (var r = 0; r < rowCount; r++)
        {
            var rowSum = 0;
            var at = (edgeInset + r) * width + columns.Lower;
            for (var c = 0; c < columns.Count; c++) rowSum += differences[at + c];
            if (rowSum <= threshold)
            {
                totalDifference += rowSum;
                run++;
                matchingRows++;
                if (run > longestRun) longestRun = run;
            }
            else
            {
                // Bound the influence of a changing block without treating its rows as matches or
                // discarding their error entirely.
                totalDifference += threshold;
                run = 0;
            }
        }
        return new RowMatch(longestRun, matchingRows, rowCount, (double)totalDifference / ((double)rowCount * columns.Count));
    }
}
