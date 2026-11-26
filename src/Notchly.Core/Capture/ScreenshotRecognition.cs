// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the text recognition helpers of upstream Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift
// (recognitionTiles, mergedRecognition, textRuns, croppedRuns, blurTextRuns, wordSelection, joinedWords). The recogniser itself
// (Windows.Media.Ocr in the app) is not here: words come in as plain records. Image pixels, TOP-LEFT origin, y down, which is the
// space upstream already keeps recognised words in.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>One word of recognised text, in image pixels.</summary>
/// <param name="Line">Line index, so copied selections keep their line breaks.</param>
public readonly record struct RecognizedWord(string Text, RectD Rect, int Line);

/// <summary>
/// A word one recognition band read. <see cref="Rect"/> is null when recognition gave the word no box of its own, and its line's
/// box then stands in for it wherever text must be covered.
/// </summary>
public readonly record struct BandWord(string Text, RectD? Rect, RectD LineBox, int Line);

/// <summary>A band of the capture recognition reads at once, and the rows [OwnedStart, OwnedEnd) it is responsible for.</summary>
public readonly record struct RecognitionTile(RectD Rect, double OwnedStart, double OwnedEnd)
{
    /// <summary>Half open, like the range upstream keeps.</summary>
    public bool OwnsRow(double row) => row >= OwnedStart && row < OwnedEnd;
}

/// <summary>What merging the bands gives: the words in reading order, and the runs a text-only area covers (null until every band was read).</summary>
public sealed record MergedRecognition(IReadOnlyList<RecognizedWord> Words, IReadOnlyList<RectD>? Runs);

/// <summary>The few rectangle operations the recognition code needs; CGRect's semantics, on <see cref="RectD"/>.</summary>
internal static class RecognitionRects
{
    public static RectD Union(RectD a, RectD b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new RectD(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    /// <summary>The overlap, possibly zero-sized when the rectangles only touch; null when they are apart.</summary>
    public static RectD? Intersection(RectD a, RectD b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right < x || bottom < y ? null : new RectD(x, y, right - x, bottom - y);
    }

    /// <summary>Overlap of positive area (touching edges do not count).</summary>
    public static bool Intersects(RectD a, RectD b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    public static RectD InsetBy(RectD r, double dx, double dy) =>
        new(r.X + dx, r.Y + dy, r.Width - dx * 2, r.Height - dy * 2);

    public static RectD OffsetBy(RectD r, double dx, double dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>Whether <paramref name="outer"/> fully contains <paramref name="inner"/> (shared edges count).</summary>
    public static bool ContainsRect(RectD outer, RectD inner) =>
        inner.X >= outer.X && inner.Right <= outer.Right && inner.Y >= outer.Y && inner.Bottom <= outer.Bottom;

    /// <summary>Half open point test, as CGRect.contains: the top and left edges are in, the bottom and right ones are out.</summary>
    public static bool ContainsPoint(RectD r, double x, double y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
}

public static class ScreenshotRecognition
{
    public const int MaximumTilePixels = 12_000_000;

    /// <summary>
    /// Bands of the capture that text recognition reads one at a time, so a tall capture never becomes one huge request. Neighbouring
    /// bands share some rows, because recognition misses a line cut by a band's edge, and each band owns the rows up to the middle
    /// of what it shares. <see cref="Merge"/> uses that seam to keep a word read twice once.
    /// </summary>
    public static IReadOnlyList<RecognitionTile> Tiles(int width, int height)
    {
        var tileHeight = Math.Min(height, Math.Max(512, Math.Min(4096, MaximumTilePixels / Math.Max(width, 1))));
        // Even, so the owned rows of two bands meet on the same row.
        var overlap = tileHeight < height ? Math.Min(256, tileHeight / 4) & ~1 : 0;
        var tiles = new List<RecognitionTile>();
        var tileY = 0;
        while (tileY < height)
        {
            var bandHeight = Math.Min(tileHeight, height - tileY);
            var isLast = tileY + bandHeight >= height;
            tiles.Add(new RecognitionTile(new RectD(0, tileY, width, bandHeight),
                tileY == 0 ? 0 : tileY + overlap / 2,
                isLast ? height : tileY + bandHeight - overlap / 2));
            if (isLast) break;
            tileY += bandHeight - overlap;
        }
        return tiles;
    }

    /// <summary>
    /// Joins what each band read into the capture's words and the runs text-only blur areas cover. <paramref name="reads"/> follows
    /// <paramref name="tiles"/>, and a null entry marks a band recognition could not read.
    /// Two bands read the rows they share and each places a word there a few pixels from where the other does, so neither guess
    /// alone can say which band keeps it. A word both bands read is kept once, from the band that owns the middle of the two
    /// guesses, and a word only one band read is always kept. Runs also take the second guess near a seam, so the text stays
    /// covered whichever guess was off. Runs stay null unless every band was read, so text-only areas keep covering all of themselves.
    /// Words come band by band, each in the band that owns its middle.
    /// </summary>
    public static MergedRecognition Merge(IReadOnlyList<IReadOnlyList<BandWord>?> reads, IReadOnlyList<RecognitionTile> tiles)
    {
        var count = Math.Min(reads.Count, tiles.Count);
        var bands = Enumerable.Range(0, count).Select(i => (Tile: tiles[i], Words: reads[i] ?? [])).ToList();
        var dropped = bands.Select(band => new bool[band.Words.Count]).ToList();
        for (var upper = 0; upper + 1 < bands.Count; upper++)
        {
            var lower = upper + 1;
            var sharedStart = bands[lower].Tile.Rect.Y;
            var sharedEnd = bands[upper].Tile.Rect.Bottom;
            var seam = bands[upper].Tile.OwnedEnd;
            List<(int Index, RectD Rect)> SharedWords(int band)
            {
                var found = new List<(int, RectD)>();
                for (var index = 0; index < bands[band].Words.Count; index++)
                    if (bands[band].Words[index].Rect is { } rect && rect.Bottom > sharedStart && rect.Y < sharedEnd)
                        found.Add((index, rect));
                return found;
            }
            var unpaired = SharedWords(lower);
            foreach (var (index, rect) in SharedWords(upper))
            {
                var text = bands[upper].Words[index].Text;
                // The other band's read of this word has the same text over at least half its height, or a box covering at least half
                // of the smaller one. The same word on the next line is not it, or it would take the only read of a line one band missed.
                var best = -1;
                var bestScore = double.NegativeInfinity;
                for (var candidate = 0; candidate < unpaired.Count; candidate++)
                {
                    var other = unpaired[candidate].Rect;
                    if (RecognitionRects.Intersection(rect, other) is not { } common || common.Width <= 0 || common.Height <= 0) continue;
                    var share = common.Width * common.Height / Math.Min(rect.Width * rect.Height, other.Width * other.Height);
                    var sameText = bands[lower].Words[unpaired[candidate].Index].Text == text
                                   && common.Height >= Math.Min(rect.Height, other.Height) / 2;
                    if (!sameText && share < 0.5) continue;
                    var score = share + (sameText ? 1 : 0);
                    if (score > bestScore) { best = candidate; bestScore = score; }
                }
                if (best < 0) continue;
                var match = unpaired[best];
                unpaired.RemoveAt(best);
                var middle = (MidY(rect) + MidY(match.Rect)) / 2;
                if (middle < seam) dropped[lower][match.Index] = true;
                else dropped[upper][index] = true;
            }
        }

        // Kept words follow the band that owns each, so a word kept from the other band of a seam still copies among the lines around it.
        var ownedWords = bands.Select(_ => new List<RecognizedWord>()).ToList();
        var covered = new List<RecognizedWord>();
        for (var index = 0; index < bands.Count; index++)
        {
            var band = bands[index];
            // Both guesses of a word near a seam become runs: each band's owned rows reach a quarter of the shared rows past the seam.
            var above = index > 0 ? (bands[index - 1].Tile.Rect.Bottom - band.Tile.Rect.Y) / 4 : 0;
            var below = index + 1 < bands.Count ? (band.Tile.Rect.Bottom - bands[index + 1].Tile.Rect.Y) / 4 : 0;
            var nearStart = band.Tile.OwnedStart - above;
            var nearEnd = band.Tile.OwnedEnd + below;
            for (var position = 0; position < band.Words.Count; position++)
            {
                var word = band.Words[position];
                if (word.Rect is not { } rect)
                {
                    covered.Add(new RecognizedWord(word.Text, word.LineBox, word.Line));
                    continue;
                }
                var kept = !dropped[index][position];
                var recognized = new RecognizedWord(word.Text, rect, word.Line);
                if (kept)
                {
                    var owner = bands.FindIndex(b => b.Tile.OwnsRow(MidY(rect)));
                    ownedWords[owner < 0 ? index : owner].Add(recognized);
                }
                if (kept || (MidY(rect) >= nearStart && MidY(rect) < nearEnd)) covered.Add(recognized);
            }
        }
        var everyBandRead = reads.Count == tiles.Count && reads.All(read => read is not null);
        return new MergedRecognition(ownedWords.SelectMany(words => words).ToList(), everyBandRead ? TextRuns(covered) : null);
    }

    private static double MidY(RectD rect) => rect.Y + rect.Height / 2;

    /// <summary>
    /// Recognised words joined into the runs a text-only blur area covers. Neighbouring words on a line share one run, so the spaces
    /// between them are covered too, and words far apart, like table columns, stay apart. Each run is padded past the antialiased
    /// edges of its glyphs.
    /// </summary>
    public static IReadOnlyList<RectD> TextRuns(IEnumerable<RecognizedWord> words)
    {
        var runs = new List<RectD>();
        (RectD Rect, int Line)? current = null;
        foreach (var word in words)
        {
            if (word.Rect.Width <= 0 || word.Rect.Height <= 0) continue;
            if (current is { } run && run.Line == word.Line)
            {
                var height = Math.Max(run.Rect.Height, word.Rect.Height);
                var gap = Math.Max(Math.Max(word.Rect.X - run.Rect.Right, run.Rect.X - word.Rect.Right), 0);
                var overlap = Math.Min(run.Rect.Bottom, word.Rect.Bottom) - Math.Max(run.Rect.Y, word.Rect.Y);
                if (gap <= height * 1.5 && overlap >= Math.Min(run.Rect.Height, word.Rect.Height) / 2)
                {
                    current = (RecognitionRects.Union(run.Rect, word.Rect), run.Line);
                    continue;
                }
            }
            if (current is { } finished) runs.Add(PaddedTextRun(finished.Rect));
            current = (word.Rect, word.Line);
        }
        if (current is { } last) runs.Add(PaddedTextRun(last.Rect));
        return runs;
    }

    /// <summary>
    /// In a tall band recognition can place a box a few pixels off, or stop it short of the glyphs, so the padding above and below is
    /// wider. A word read one letter short ends its box before that letter, so each end reaches about a glyph further.
    /// </summary>
    private static RectD PaddedTextRun(RectD rect) =>
        RecognitionRects.InsetBy(rect, -Math.Max(1, rect.Height * 0.6), -Math.Max(1, rect.Height * 0.3));

    /// <summary>
    /// Runs moved into a crop's coordinates, keeping those still on it; null when there were none to move (recognition had not read
    /// the capture).
    /// </summary>
    public static IReadOnlyList<RectD>? CroppedRuns(IEnumerable<RectD>? runs, RectD crop)
    {
        if (runs is null) return null;
        var bounds = new RectD(0, 0, crop.Width, crop.Height);
        return runs.Select(run => RecognitionRects.OffsetBy(run, -crop.X, -crop.Y)).Where(moved => RecognitionRects.Intersects(moved, bounds)).ToList();
    }

    /// <summary>
    /// The runs a text-only blur area covers: every run that reaches into it. Null runs mean recognition has not read the capture yet
    /// and the whole area stands in for them, so an early export never leaves text readable.
    /// </summary>
    public static IReadOnlyList<RectD> BlurTextRuns(RectD area, IEnumerable<RectD>? runs) =>
        runs is null ? [area] : runs.Where(run => RecognitionRects.Intersects(run, area)).ToList();

    /// <summary>The rectangle between two drag points, whichever way the drag went.</summary>
    public static RectD DragRect(PointD from, PointD to) =>
        new(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));

    /// <summary>Indexes of the words a selection drag touches, in reading order. A hairline drag still selects what it crosses.</summary>
    public static IReadOnlyList<int> WordSelection(PointD anchor, PointD current, IReadOnlyList<RectD> boxes)
    {
        var rect = RecognitionRects.InsetBy(DragRect(anchor, current), -1, -1);
        return Enumerable.Range(0, boxes.Count).Where(index => RecognitionRects.Intersects(boxes[index], rect)).ToList();
    }

    /// <summary>Joins selected words with spaces inside a line and newlines between lines, whatever order the indexes arrive in.</summary>
    public static string JoinedWords(IReadOnlyList<RecognizedWord> words, IEnumerable<int> selected)
    {
        var picked = selected.Order().Where(index => index >= 0 && index < words.Count).Select(index => words[index]).ToList();
        if (picked.Count == 0) return "";
        var lines = new List<string>();
        var currentLine = picked[0].Line;
        var currentWords = new List<string>();
        foreach (var word in picked)
        {
            if (word.Line != currentLine)
            {
                lines.Add(string.Join(' ', currentWords));
                currentWords = [];
                currentLine = word.Line;
            }
            currentWords.Add(word.Text);
        }
        lines.Add(string.Join(' ', currentWords));
        return string.Join('\n', lines);
    }
}
