// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the recognition checks of upstream ScreenshotFeatureTests (~2330-2370 text runs and crops, ~2371-2400 bands, ~2745-2795
// the band merge, ~3238-3260 word selection). The editor-source grep checks are skipped.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class ScreenshotRecognitionTests
{
    private static void SameRect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    // MARK: - Text runs

    private static readonly RecognizedWord[] RunWords =
    [
        new("john", new RectD(10, 10, 40, 20), 0),
        new("doe", new RectD(56, 10, 30, 20), 0),
        new("total", new RectD(300, 10, 50, 20), 0),
        new("next", new RectD(10, 40, 40, 20), 1),
    ];

    [Fact]
    public void CloseWordsOnALineShareAPaddedRunFarOnesAndOtherLinesGetTheirOwn()
    {
        var runs = ScreenshotRecognition.TextRuns(RunWords);
        Assert.Equal(3, runs.Count);
        SameRect(new RectD(-2, 4, 100, 32), runs[0]);
        SameRect(new RectD(288, 4, 74, 32), runs[1]);
        SameRect(new RectD(-2, 34, 64, 32), runs[2]);
    }

    [Fact]
    public void NoWordsMakeNoRuns() => Assert.Empty(ScreenshotRecognition.TextRuns([]));

    [Fact]
    public void ABoxWithNoAreaMakesNoRun() =>
        Assert.Empty(ScreenshotRecognition.TextRuns([new RecognizedWord("x", new RectD(5, 5, 0, 10), 0), new RecognizedWord("y", new RectD(5, 5, 10, 0), 0)]));

    [Fact]
    public void ARunIsPaddedByHalfAGlyphAtEachEnd()
    {
        var run = ScreenshotRecognition.TextRuns([new RecognizedWord("a", new RectD(100, 100, 50, 10), 0)]).Single();
        SameRect(new RectD(94, 97, 62, 16), run);
    }

    [Fact]
    public void ATinyWordStillGetsAPixelOfPadding()
    {
        var run = ScreenshotRecognition.TextRuns([new RecognizedWord("a", new RectD(10, 10, 1, 1), 0)]).Single();
        SameRect(new RectD(9, 9, 3, 3), run);
    }

    [Fact]
    public void WordsOnTheSameLineIndexButFarApartVerticallyStaySeparate()
    {
        var runs = ScreenshotRecognition.TextRuns(
        [
            new RecognizedWord("a", new RectD(0, 0, 40, 20), 0),
            new RecognizedWord("b", new RectD(50, 100, 40, 20), 0),
        ]);
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void AGapOfOneAndAHalfWordHeightsStillJoins()
    {
        var joined = ScreenshotRecognition.TextRuns(
        [
            new RecognizedWord("a", new RectD(0, 0, 40, 20), 0),
            new RecognizedWord("b", new RectD(70, 0, 40, 20), 0),
        ]);
        var apart = ScreenshotRecognition.TextRuns(
        [
            new RecognizedWord("a", new RectD(0, 0, 40, 20), 0),
            new RecognizedWord("b", new RectD(71, 0, 40, 20), 0),
        ]);
        Assert.Single(joined);
        Assert.Equal(2, apart.Count);
    }

    [Fact]
    public void ADifferentLineNumberAlwaysStartsANewRun()
    {
        var runs = ScreenshotRecognition.TextRuns(
        [
            new RecognizedWord("a", new RectD(0, 0, 40, 20), 0),
            new RecognizedWord("b", new RectD(42, 0, 40, 20), 1),
        ]);
        Assert.Equal(2, runs.Count);
    }

    // MARK: - Crops and areas

    [Fact]
    public void ACropKeepsTheRunsStillOnItMovedAndAnUnreadCaptureStaysUnread()
    {
        var crop = new RectD(20, 30, 200, 100);
        var carried = ScreenshotRecognition.CroppedRuns([new RectD(10, 20, 120, 20), new RectD(400, 40, 50, 20)], crop)!;
        Assert.Single(carried);
        SameRect(new RectD(-10, -10, 120, 20), carried[0]);
        Assert.Null(ScreenshotRecognition.CroppedRuns(null, crop));
    }

    [Fact]
    public void ACropOfNoRunsIsAnEmptyList() =>
        Assert.Empty(ScreenshotRecognition.CroppedRuns([], new RectD(0, 0, 10, 10))!);

    [Fact]
    public void ARunThatOnlyTouchesTheCropIsDropped() =>
        Assert.Empty(ScreenshotRecognition.CroppedRuns([new RectD(100, 0, 10, 10)], new RectD(0, 0, 100, 100))!);

    [Fact]
    public void ATextOnlyAreaCoversTheRunsItReachesAndAllOfItselfBeforeRecognition()
    {
        var runs = ScreenshotRecognition.TextRuns(RunWords);
        var picked = ScreenshotRecognition.BlurTextRuns(new RectD(0, 0, 60, 30), runs);
        Assert.Single(picked);
        Assert.Equal(runs[0], picked[0]);
        Assert.Empty(ScreenshotRecognition.BlurTextRuns(new RectD(400, 0, 20, 20), runs));
        Assert.Equal([new RectD(1, 2, 3, 4)], ScreenshotRecognition.BlurTextRuns(new RectD(1, 2, 3, 4), null));
    }

    // MARK: - Bands

    [Theory]
    [InlineData(3456, 2234, 1)]
    [InlineData(5120, 2880, 2)]
    [InlineData(300, 200, 1)]
    public void ACommonCaptureReadsInTheExpectedNumberOfBands(int width, int height, int count) =>
        Assert.Equal(count, ScreenshotRecognition.Tiles(width, height).Count);

    [Theory]
    [InlineData(3456, 2234)]
    [InlineData(5120, 2880)]
    [InlineData(6016, 3384)]
    [InlineData(2000, 20_000)]
    [InlineData(12_032, 20_000)] // bands 997 rows tall, whose quarter is odd
    [InlineData(300, 200)]
    [InlineData(100, 100_000)]
    [InlineData(1, 5000)]
    public void BandsOverlapAndTheirOwnedRowsMeetExactly(int width, int height)
    {
        var bands = ScreenshotRecognition.Tiles(width, height);
        Assert.Equal(0, bands[0].OwnedStart);
        Assert.Equal(height, bands[^1].OwnedEnd);
        foreach (var (upper, lower) in bands.Zip(bands.Skip(1)))
        {
            // Owned rows meet without a gap, and each band reads well past them.
            Assert.Equal(upper.OwnedEnd, lower.OwnedStart);
            Assert.True(upper.Rect.Bottom - upper.OwnedEnd >= 64);
            Assert.True(lower.OwnedStart - lower.Rect.Y >= 64);
        }
        foreach (var band in bands)
        {
            Assert.Equal(width, band.Rect.Width);
            Assert.True(band.Rect.Y <= band.OwnedStart && band.OwnedEnd <= band.Rect.Bottom);
        }
    }

    [Fact]
    public void ABandNeverHoldsMoreThanTwelveMillionPixels()
    {
        foreach (var (width, height) in new[] { (12_032, 20_000), (6000, 30_000), (2000, 9000) })
            Assert.All(ScreenshotRecognition.Tiles(width, height), band => Assert.True(band.Rect.Width * band.Rect.Height <= 12_000_000 + width));
    }

    [Fact]
    public void ASingleBandOwnsTheWholePicture()
    {
        var only = ScreenshotRecognition.Tiles(300, 200).Single();
        Assert.Equal(new RectD(0, 0, 300, 200), only.Rect);
        Assert.Equal(0, only.OwnedStart);
        Assert.Equal(200, only.OwnedEnd);
    }

    [Fact]
    public void TheEveryRowIsOwnedByExactlyOneBand()
    {
        var bands = ScreenshotRecognition.Tiles(1200, 9000);
        foreach (var row in new[] { 0.0, 1, 3967, 3968, 3969, 7807, 7808, 8999 })
            Assert.Equal(1, bands.Count(band => band.OwnsRow(row)));
        Assert.Equal(0, bands.Count(band => band.OwnsRow(9000)));
    }

    // MARK: - Merging bands

    private static readonly IReadOnlyList<RecognitionTile> MergeTiles = ScreenshotRecognition.Tiles(1200, 9000);

    private static BandWord Read(string text, double x, double midY, int line, double width = 80, double height = 30, bool hasBox = true)
    {
        var rect = new RectD(x, midY - height / 2, width, height);
        return new BandWord(text, hasBox ? rect : null, new RectD(100, rect.Y, 600, height), line);
    }

    private static readonly BandWord[] UpperBand =
    [
        Read("alpha", 100, 1000, 0),
        Read("beta", 200, 1000, 0, hasBox: false),
        // Guessed just past the seam here, and just before it below.
        Read("lemon", 100, 3968, 1),
        Read("monkey", 190, 3968, 1),
        Read("bridge", 280, 3968, 1),
        // Guessed before the seam here, and past it below.
        Read("pencil", 700, 3965, 2),
        Read("river", 790, 3965, 2),
        // Read only here, in rows the next band owns.
        Read("orphan", 1000, 4000, 3),
        // A line just above another one the next band reads.
        Read("first", 500, 3880, 4),
        // Cut by this band's edge, so read short.
        Read("anch", 100, 4088, 5, width: 50, height: 16),
    ];

    private static readonly BandWord[] MiddleBand =
    [
        Read("lemon", 103, 3966.5, 10),
        Read("monkey", 193, 3966.5, 10),
        Read("bridge", 283, 3966.5, 10),
        Read("pencil", 698, 3972, 11),
        Read("river", 788, 3972, 11),
        // Read only here, in rows the band above owns.
        Read("stray", 1000, 3940, 12),
        Read("second", 500, 3905, 13),
        Read("anchor", 100, 4095, 14),
        Read("middle", 100, 6000, 15),
    ];

    private static readonly BandWord[] LowerBand = [Read("lone", 100, 7790, 20), Read("tail", 100, 8500, 21)];

    private static MergedRecognition MergeAll() =>
        ScreenshotRecognition.Merge([UpperBand, MiddleBand, LowerBand], MergeTiles);

    [Fact]
    public void TheSeamsSitWhereTheBandsPutThem()
    {
        Assert.Equal(3, MergeTiles.Count);
        Assert.Equal(3968, MergeTiles[0].OwnedEnd);
        Assert.Equal(7808, MergeTiles[1].OwnedEnd);
    }

    [Fact]
    public void EveryLineReadOnASeamIsKeptOnceWhicheverBandReadItAndWhereverEachGuessedIt()
    {
        var texts = MergeAll().Words.Select(w => w.Text).Order(StringComparer.Ordinal).ToArray();
        string[] every = ["alpha", "anchor", "bridge", "first", "lemon", "lone", "middle", "monkey", "orphan", "pencil", "river", "second", "stray", "tail"];
        Assert.Equal(every, texts);
    }

    [Fact]
    public void AWordBothBandsReadIsKeptFromTheBandThatOwnsTheMiddleOfTheTwoGuesses()
    {
        var words = MergeAll().Words;
        Assert.Equal(1, words.First(w => w.Text == "lemon").Line);
        Assert.Equal(11, words.First(w => w.Text == "pencil").Line);
        Assert.Equal(14, words.First(w => w.Text == "anchor").Line);
    }

    [Fact]
    public void MergedWordsComeBandByBandSoASelectionAcrossASeamKeepsItsLinesInOrder()
    {
        var owners = MergeAll().Words.Select(w => MergeTiles.ToList().FindIndex(t => t.OwnsRow(w.Rect.Y + w.Rect.Height / 2))).ToList();
        Assert.Equal(owners.Order().ToList(), owners);
    }

    [Fact]
    public void ALineOneBandMissedIsNotPairedWithTheSameWordOnTheNextLine()
    {
        // The same word in one column on two close lines, like a table. The band above reads both lines and the next band only the
        // lower one, a few pixels up, so the upper line has no other read to pair with.
        BandWord[] above =
        [
            Read("Yes", 1100, 4040, 6, 40, 22),
            Read("Yes", 1100, 4068, 7, 40, 22),
            Read("Yes", 400, 3990, 8, 40, 22),
            Read("Yes", 400, 4018, 9, 40, 22),
        ];
        BandWord[] below = [Read("Yes", 1100, 4060, 16, 40, 22), Read("Yes", 400, 4010, 17, 40, 22)];
        var merged = ScreenshotRecognition.Merge([above, below, []], MergeTiles);
        Assert.Equal([6, 8, 16, 17], merged.Words.Select(w => w.Line).Order().ToArray());
    }

    [Fact]
    public void TextOnlyRunsCoverBothGuessesOfASeamWordAndAWordWithoutABox()
    {
        var merged = MergeAll();
        string[] seamWords = ["lemon", "monkey", "bridge", "pencil", "river"];
        var guesses = UpperBand.Concat(MiddleBand).Where(w => seamWords.Contains(w.Text)).Select(w => w.Rect!.Value);
        var boxless = new RectD(100, 985, 600, 30);
        var mustCover = merged.Words.Select(w => w.Rect).Concat(guesses).Append(boxless).ToList();
        Assert.NotNull(merged.Runs);
        foreach (var rect in mustCover)
            Assert.Contains(merged.Runs!, run => run.X <= rect.X && run.Y <= rect.Y && run.Right >= rect.Right && run.Bottom >= rect.Bottom);
    }

    [Fact]
    public void AWordRecognitionGaveNoBoxIsCoveredButCannotBeSelected() =>
        Assert.DoesNotContain(MergeAll().Words, w => w.Text == "beta");

    [Fact]
    public void ABandRecognitionCouldNotReadKeepsTextOnlyAreasCoveringAllOfThemselves()
    {
        var failedMiddle = ScreenshotRecognition.Merge([UpperBand, null, LowerBand], MergeTiles);
        Assert.Null(failedMiddle.Runs);
        Assert.Contains(failedMiddle.Words, w => w.Text == "alpha");
        Assert.Contains(failedMiddle.Words, w => w.Text == "tail");
        Assert.Null(ScreenshotRecognition.Merge([UpperBand, MiddleBand], MergeTiles).Runs);
    }

    [Fact]
    public void AllBandsReadMeansRunsExist() => Assert.NotNull(MergeAll().Runs);

    [Fact]
    public void ASingleBandMergesToItsOwnWords()
    {
        var tiles = ScreenshotRecognition.Tiles(300, 200);
        BandWord[] read = [Read("hello", 10, 50, 0, 60, 20), Read("world", 80, 50, 0, 60, 20), Read("again", 10, 120, 1, 60, 20)];
        var merged = ScreenshotRecognition.Merge([read], tiles);
        Assert.Equal(["hello", "world", "again"], merged.Words.Select(w => w.Text).ToArray());
        Assert.Equal(2, merged.Runs!.Count);
    }

    [Fact]
    public void NothingReadMergesToNothing()
    {
        var merged = ScreenshotRecognition.Merge([[], [], []], MergeTiles);
        Assert.Empty(merged.Words);
        Assert.Empty(merged.Runs!);
    }

    [Fact]
    public void TheSameTextOnTheWrongLineDoesNotPairWithoutOverlap()
    {
        // Two reads of "x" in the shared rows with no overlap at all stay as two words.
        BandWord[] upper = [Read("x", 100, 4000, 1, 40, 20)];
        BandWord[] middle = [Read("x", 600, 4000, 2, 40, 20)];
        var merged = ScreenshotRecognition.Merge([upper, middle, []], MergeTiles);
        Assert.Equal(2, merged.Words.Count);
    }

    [Fact]
    public void ADifferentWordOverlappingByHalfTheSmallerBoxStillPairs()
    {
        // Boxes covering at least half of the smaller one are one word even when the text was read differently.
        BandWord[] upper = [Read("cat", 100, 4000, 1, 60, 20)];
        BandWord[] middle = [Read("cot", 110, 4004, 2, 60, 20)];
        var merged = ScreenshotRecognition.Merge([upper, middle, []], MergeTiles);
        Assert.Single(merged.Words);
    }

    [Fact]
    public void ADifferentWordOverlappingByLessThanHalfDoesNotPair()
    {
        BandWord[] upper = [Read("cat", 100, 4000, 1, 60, 20)];
        BandWord[] middle = [Read("cot", 150, 4015, 2, 60, 20)];
        var merged = ScreenshotRecognition.Merge([upper, middle, []], MergeTiles);
        Assert.Equal(2, merged.Words.Count);
    }

    [Fact]
    public void WordsFarFromTheSeamAreAlwaysKept()
    {
        BandWord[] upper = [Read("top", 10, 100, 0, 40, 20)];
        BandWord[] middle = [Read("mid", 10, 5000, 1, 40, 20)];
        BandWord[] lower = [Read("end", 10, 8800, 2, 40, 20)];
        var merged = ScreenshotRecognition.Merge([upper, middle, lower], MergeTiles);
        Assert.Equal(["top", "mid", "end"], merged.Words.Select(w => w.Text).ToArray());
    }

    // MARK: - Selection

    private static readonly RectD[] WordBoxes = [new(0, 0, 40, 10), new(50, 0, 40, 10), new(0, 20, 40, 10)];

    [Fact]
    public void ADragAcrossALineSelectsTheWordsItCrosses() =>
        Assert.Equal([0, 1], ScreenshotRecognition.WordSelection(new PointD(5, 5), new PointD(60, 5), WordBoxes));

    [Fact]
    public void ADragAcrossLinesSelectsIntoTheNextLine() =>
        Assert.Equal([0, 2], ScreenshotRecognition.WordSelection(new PointD(5, 5), new PointD(10, 25), WordBoxes));

    [Fact]
    public void ADragInAnyDirectionSelectsTheSame() =>
        Assert.Equal([0, 1], ScreenshotRecognition.WordSelection(new PointD(60, 5), new PointD(5, 5), WordBoxes));

    [Fact]
    public void ADragThroughNothingSelectsNothing() =>
        Assert.Empty(ScreenshotRecognition.WordSelection(new PointD(200, 200), new PointD(210, 210), WordBoxes));

    [Fact]
    public void AClickOnAWordSelectsIt() =>
        Assert.Equal([1], ScreenshotRecognition.WordSelection(new PointD(70, 5), new PointD(70, 5), WordBoxes));

    private static readonly RecognizedWord[] Words =
    [
        new("ola", WordBoxes[0], 0), new("mundo", WordBoxes[1], 0), new("linha", WordBoxes[2], 1),
    ];

    [Fact]
    public void SelectedWordsJoinInReadingOrderWithSpaces() =>
        Assert.Equal("ola mundo", ScreenshotRecognition.JoinedWords(Words, [1, 0]));

    [Fact]
    public void LineChangesBecomeNewlines() =>
        Assert.Equal("ola\nlinha", ScreenshotRecognition.JoinedWords(Words, [2, 0]));

    [Fact]
    public void OutOfRangeOrEmptySelectionsCopyNothing()
    {
        Assert.Equal("", ScreenshotRecognition.JoinedWords(Words, [9]));
        Assert.Equal("", ScreenshotRecognition.JoinedWords(Words, []));
        Assert.Equal("", ScreenshotRecognition.JoinedWords(Words, [-1]));
    }

    [Fact]
    public void AnOutOfRangeIndexBesideAGoodOneIsIgnored() =>
        Assert.Equal("mundo", ScreenshotRecognition.JoinedWords(Words, [1, 77]));

    [Fact]
    public void EveryWordSelectedCopiesTheWholeText() =>
        Assert.Equal("ola mundo\nlinha", ScreenshotRecognition.JoinedWords(Words, [0, 1, 2]));
}
