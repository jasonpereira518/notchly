// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the scrolling-capture contracts in upstream Tests/ScreenshotFeatureTests.swift (lines
// ~428-665: delay, safeguards, scrollingTransition, fixed rows) and Tests/ScreenshotScrollingCaptureTests.swift
// (the capture loop with an isolated image source, here driven through ScrollingCaptureSession with a
// virtual clock instead of an async loop). The terminal document is drawn from a 5x7 digit font
// instead of AppKit text.
using Notchly.Core.Capture;

namespace Notchly.Core.Tests;

internal static class ScrollFixtures
{
    public static byte[] Pixels(int width, int height, int offset)
    {
        var pixels = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var contentRow = row + offset;
            for (var column = 0; column < width; column++)
                pixels[row * width + column] = (byte)((contentRow * 17 + column * 31 + (contentRow / 7) * 13) % 251);
        }
        return pixels;
    }

    public static ScrollingSample Sample(int offset, int width = 16, int height = 120) => new(width, height, Pixels(width, height, offset));

    public static ScrollingSample LowContrast(int offset, bool noisy = false)
    {
        var sample = Sample(offset);
        var pixels = sample.Pixels.Select(p => (byte)(p / 16)).ToArray();
        if (noisy) for (var i = 0; i < pixels.Length; i += 17) pixels[i]++;
        return new ScrollingSample(sample.Width, sample.Height, pixels);
    }

    public static ScrollingSample WithFixedEdges(int offset)
    {
        var sample = Sample(offset);
        var pixels = (byte[])sample.Pixels.Clone();
        for (var row = 0; row < 12; row++)
            for (var column = 0; column < sample.Width; column++)
            {
                pixels[row * sample.Width + column] = (byte)(row * 5 + column);
                var bottomRow = sample.Height - row - 1;
                pixels[bottomRow * sample.Width + column] = (byte)(row * 3 + column);
            }
        return new ScrollingSample(sample.Width, sample.Height, pixels);
    }

    public static ScrollingSample WithFixedBottom(int offset, int fixedBottomRows = 18)
    {
        var sample = Sample(offset);
        var pixels = (byte[])sample.Pixels.Clone();
        for (var row = sample.Height - fixedBottomRows; row < sample.Height; row++)
            for (var column = 0; column < sample.Width; column++)
                pixels[row * sample.Width + column] = (byte)((row * 29 + column * 11 + 41) % 251);
        return new ScrollingSample(sample.Width, sample.Height, pixels);
    }

    public static ScrollingSample WithFixedColumns(int offset, bool changesAfterScroll = false)
    {
        const int width = 24, height = 120;
        var pixels = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                if (column < 6 || column >= 18)
                    pixels[row * width + column] = (byte)((row * 19 + column * 23 + (row / 5) * 11) % 251);
                else if (changesAfterScroll && row is >= 30 and < 46)
                    pixels[row * width + column] = (byte)((row * 61 + column * 7 + 37) % 251);
                else
                {
                    var contentRow = row + offset;
                    pixels[row * width + column] = (byte)((contentRow * 17 + column * 31 + (contentRow / 7) * 13) % 251);
                }
            }
        return new ScrollingSample(width, height, pixels);
    }

    // ---- Frames for the capture loop ---------------------------------------------------------------

    public static byte[] FrameGray(int offset, int width = 32, int height = 120, int seed = 0, int? contentWidth = null, bool sparse = false)
    {
        var pixels = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                if (column >= (contentWidth ?? width)) continue;
                var r = row + offset;
                if (seed == 0)
                {
                    pixels[row * width + column] = (byte)((r * 17 + column * 31 + (r / 7) * 13) % 251);
                }
                else
                {
                    unchecked
                    {
                        var value = (uint)(r * width + column + seed);
                        value = (value ^ (value >> 16)) * 0x45d9f3bu;
                        value = (value ^ (value >> 16)) * 0x45d9f3bu;
                        pixels[row * width + column] = (byte)(value ^ (value >> 16));
                    }
                }
                if (sparse) pixels[row * width + column] = r % 24 < 8 ? (byte)(215 + pixels[row * width + column] % 40) : (byte)255;
            }
        return pixels;
    }

    public static BgraFrame Frame(int offset, int width = 32, int height = 120, int seed = 0, int? contentWidth = null, bool sparse = false) =>
        BgraFrame.FromGray(width, height, FrameGray(offset, width, height, seed, contentWidth, sparse));

    /// <summary>The document does not move. Only an animated banner changes rows.</summary>
    public static BgraFrame StationaryPage(int animationOffset, bool blankBackground = false, IntRange? bannerRows = null)
    {
        const int width = 32, height = 120;
        var banner = bannerRows ?? new IntRange(30, 66);
        var bytes = blankBackground ? Enumerable.Repeat((byte)255, width * height).ToArray() : FrameGray(0, seed: 91);
        var animated = FrameGray(animationOffset, seed: 91);
        for (var row = banner.Lower; row < banner.Upper; row++) Array.Copy(animated, row * width, bytes, row * width, width);
        return BgraFrame.FromGray(width, height, bytes);
    }

    /// <summary>A transparent bottom row and right column, as a padded region capture.</summary>
    public static BgraFrame PaddedFrame(int offset)
    {
        var gray = FrameGray(offset);
        const int width = 32, height = 120, paddedWidth = 33, paddedHeight = 121;
        var pixels = new byte[paddedWidth * paddedHeight * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * paddedWidth + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = gray[y * width + x];
                pixels[at + 3] = 255;
            }
        return new BgraFrame(paddedWidth, paddedHeight, paddedWidth * 4, pixels, hasAlpha: true);
    }

    public static BgraFrame SparsePage(int offset, int height = 120)
    {
        const int width = 32;
        var bytes = Enumerable.Repeat((byte)255, width * height).ToArray();
        var lines = new[] { 26, 49, 74, 97, 117, 141 };
        for (var row = 0; row < height; row++)
        {
            var documentRow = row < 20 ? row : row + offset;
            if (!(row < 20 || lines.Contains(documentRow))) continue;
            for (var column = 0; column < width; column++) bytes[row * width + column] = (byte)((documentRow * 17 + column * 31) % 181);
        }
        return BgraFrame.FromGray(width, height, bytes);
    }

    public static BgraFrame Crop(BgraFrame frame, int y, int height) =>
        frame.CopyStrip(new IntRange(0, frame.Width), y, frame.Height - y - height)!;

    public static byte[] Bytes(BgraFrame frame)
    {
        var rowBytes = frame.Width * 4;
        var bytes = new byte[rowBytes * frame.Height];
        for (var y = 0; y < frame.Height; y++) Buffer.BlockCopy(frame.Pixels, y * frame.Stride, bytes, y * rowBytes, rowBytes);
        return bytes;
    }

    // 5x7 digit glyphs, one bit per column, top row first.
    private static readonly string[][] Digits =
    [
        ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    ];

    /// <summary>Sparse, repeated lines of line numbers, 28 points apart, white on black.</summary>
    public static BgraFrame TerminalDocument(int lines = 150, int width = 1800)
    {
        var height = lines * 28;
        var gray = new byte[width * height];
        for (var line = 1; line <= lines; line++)
        {
            var text = line.ToString();
            var top = (line - 1) * 28 + 4;
            for (var c = 0; c < text.Length; c++)
            {
                var glyph = Digits[text[c] - '0'];
                for (var gy = 0; gy < 7; gy++)
                    for (var gx = 0; gx < 5; gx++)
                    {
                        if (glyph[gy][gx] != '1') continue;
                        for (var dy = 0; dy < 3; dy++)
                            for (var dx = 0; dx < 3; dx++)
                                gray[(top + gy * 3 + dy) * width + 12 + c * 18 + gx * 3 + dx] = 255;
                    }
            }
        }
        return BgraFrame.FromGray(width, height, gray);
    }
}

/// <summary>Runs ScrollingCaptureSession the way the capture loop does, with a virtual clock and a fake image source.</summary>
internal sealed class ScrollingHarness
{
    public List<BgraFrame?> Images { get; }
    public bool Automatic { get; }
    public bool AnimateBeforeFirstScroll { get; set; }
    public bool TargetIsCurrent { get; set; } = true;
    public bool FinishRequested { get; set; }
    public int Scrolls { get; private set; }
    public int Calls { get; private set; }
    public Action<int>? OnRead { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public List<int> Heights { get; } = [];
    public double Now { get; private set; }
    private int? _automaticIndex;

    public ScrollingHarness(IEnumerable<BgraFrame?> images, bool automatic = false)
    {
        Images = images.ToList();
        Automatic = automatic;
        _automaticIndex = automatic ? 0 : null;
    }

    private BgraFrame? Read()
    {
        Calls++;
        OnRead?.Invoke(Calls);
        if (AnimateBeforeFirstScroll && Scrolls == 0) return ScrollFixtures.StationaryPage(Calls % 2 == 0 ? 0 : 12);
        if (_automaticIndex is { } index) return Images[index];
        var i = Math.Min(Calls - 1, Images.Count - 1);
        if (Calls >= Images.Count) FinishRequested = true;
        return Images[i];
    }

    private bool Scroll()
    {
        Scrolls++;
        _automaticIndex = Math.Min((_automaticIndex ?? 0) + 1, Images.Count - 1);
        return true;
    }

    public ScrollingResult Run()
    {
        var session = new ScrollingCaptureSession(Automatic);
        var firstFrame = Read();
        if (firstFrame is null) return Cancellation.IsCancellationRequested ? ScrollingResult.Cancelled : ScrollingResult.Failed;
        if (session.Begin(firstFrame, Now) is { } failed) return failed;
        Heights.Add(session.TotalHeight);
        for (var guard = 0; guard < 20_000; guard++)
        {
            if (Cancellation.IsCancellationRequested) return session.Cancel();
            if (session.PreCheck(Now, FinishRequested) is { } done) return done;
            if (!session.ReadyForFrame(Now)) { Now += session.DelayUntilNextFrame(Now) + 1e-6; continue; }
            var frame = Read();
            var step = session.Ingest(frame, Now, TargetIsCurrent, FinishRequested, Cancellation.Token);
            if (step.ProgressHeight is { } height) Heights.Add(height);
            if (step.Result is { } result) return result;
            if (step.Action == AutoScrollAction.Scroll)
            {
                var delivered = Cancellation.IsCancellationRequested || FinishRequested || Scroll();
                if (session.AfterScroll(delivered) is { } partial) return partial;
            }
        }
        throw new InvalidOperationException("scrolling capture did not finish");
    }
}

public class ScrollingSafeguardTests
{
    [Fact]
    public void DelayOnlyAcceptsTheOfferedSteps()
    {
        Assert.Equal(5, ScreenshotSupport.SanitizedDelay(5));
        Assert.Equal(0, ScreenshotSupport.SanitizedDelay(7));
        Assert.Equal(0, ScreenshotSupport.SanitizedDelay(-3));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(1, 0)]
    [InlineData(11, 0)]
    [InlineData(int.MaxValue, 0)]
    public void EveryOfferedDelayIsKeptAndEverythingElseIsZero(int raw, int expected) =>
        Assert.Equal(expected, ScreenshotSupport.SanitizedDelay(raw));

    [Fact]
    public void ManualScrollingHasExplicitLoopAndMemorySafeguards()
    {
        Assert.Equal(120, ScreenshotSupport.ScrollingCaptureMaximumDuration);
        Assert.Equal(512, ScreenshotSupport.ScrollingCaptureMaximumFrames);
        Assert.Equal(60_000_000, ScreenshotSupport.ScrollingCaptureMaximumRetainedPixels);
        Assert.Equal(60_000_000, ScreenshotSupport.ScrollingCaptureMaximumPixels);
    }

    [Fact]
    public void ASampleIsValidOnlyWhenItsBufferMatchesItsSize()
    {
        Assert.True(ScrollFixtures.Sample(0).IsValid);
        Assert.False(new ScrollingSample(0, 5, []).IsValid);
        Assert.False(new ScrollingSample(4, 4, new byte[15]).IsValid);
    }

    [Fact]
    public void MismatchedOrTooShortSamplesAreNeverMatched()
    {
        var a = ScrollFixtures.Sample(0);
        Assert.Equal(ScrollingTransition.Unmatched, ScrollingStitch.Transition(a, ScrollFixtures.Sample(0, width: 32)));
        Assert.Equal(ScrollingTransition.Unmatched, ScrollingStitch.Transition(a, ScrollFixtures.Sample(0, height: 130)));
        Assert.Equal(ScrollingTransition.Unmatched,
            ScrollingStitch.Transition(ScrollFixtures.Sample(0, height: 23), ScrollFixtures.Sample(5, height: 23)));
        Assert.Equal(ScrollingTransition.Unmatched, ScrollingStitch.Transition(a, ScrollFixtures.Sample(3), new IntRange(0, 40)));
        Assert.Equal(ScrollingTransition.Unmatched, ScrollingStitch.Transition(a, ScrollFixtures.Sample(3), new IntRange(4, 4)));
        Assert.False(ScrollingStitch.SamplesAreStable(a, ScrollFixtures.Sample(0, width: 32)));
    }
}

public class ScrollingTransitionTests
{
    private static ScrollingTransition Forward(int overlap, int columns = 16) =>
        ScrollingTransition.Advanced(overlap, ScrollingDirection.Forward, new IntRange(0, columns));

    private static ScrollingTransition Backward(int overlap, int columns = 16) =>
        ScrollingTransition.Advanced(overlap, ScrollingDirection.Backward, new IntRange(0, columns));

    [Fact]
    public void ExactSharedRowsAreFoundDeterministically() =>
        Assert.Equal(Forward(44), ScrollingStitch.Transition(ScrollFixtures.Sample(0), ScrollFixtures.Sample(76)));

    [Fact]
    public void BackwardMovementIsIdentifiedWithoutDuplicatingIt() =>
        Assert.Equal(Backward(44), ScrollingStitch.Transition(ScrollFixtures.Sample(76), ScrollFixtures.Sample(0)));

    [Fact]
    public void AnUnchangedCaptureMarksTheRealEndOfScrolling()
    {
        var sample = ScrollFixtures.Sample(76);
        Assert.Equal(ScrollingTransition.End, ScrollingStitch.Transition(sample, sample));
    }

    [Fact]
    public void SettlingRecognizesTwoUnchangedFrames()
    {
        var sample = ScrollFixtures.Sample(76);
        Assert.True(ScrollingStitch.SamplesAreStable(sample, sample));
        Assert.False(ScrollingStitch.SamplesAreStable(ScrollFixtures.Sample(0), ScrollFixtures.Sample(76)));
    }

    [Fact]
    public void APreciseLowContrastOverlapBeatsLongerButOnlySimilarRows() =>
        Assert.Equal(Forward(78), ScrollingStitch.Transition(ScrollFixtures.LowContrast(0), ScrollFixtures.LowContrast(42)));

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(42, false)]
    [InlineData(42, true)]
    public void LowContrastOverlapsSurviveSimilarRowsInBothDirections(int offset, bool noisy)
    {
        var start = ScrollFixtures.LowContrast(0);
        var next = ScrollFixtures.LowContrast(offset, noisy);
        Assert.Equal(Forward(120 - offset), ScrollingStitch.Transition(start, next));
        Assert.Equal(Backward(120 - offset), ScrollingStitch.Transition(next, start));
    }

    [Fact]
    public void AShortFinalScrollIsKeptInsteadOfFailingTheWholeCapture() =>
        Assert.Equal(Forward(112), ScrollingStitch.Transition(ScrollFixtures.Sample(0), ScrollFixtures.Sample(8)));

    [Fact]
    public void FixedPageEdgesDoNotHideTheSharedScrollingContent() =>
        Assert.Equal(Forward(70), ScrollingStitch.Transition(ScrollFixtures.WithFixedEdges(0), ScrollFixtures.WithFixedEdges(50)));

    [Fact]
    public void AFixedFooterDoesNotHideTheMovingPageOverlap() =>
        Assert.Equal(Forward(78), ScrollingStitch.Transition(ScrollFixtures.WithFixedBottom(0), ScrollFixtures.WithFixedBottom(42)));

    [Fact]
    public void AFixedFooterAtTheBottomEdgeIsIdentified() =>
        Assert.Equal(18, ScrollingStitch.FixedBottomRows(ScrollFixtures.WithFixedBottom(0), ScrollFixtures.WithFixedBottom(42), 78, new IntRange(0, 16)));

    [Fact]
    public void OrdinaryMovingContentIsNeverMistakenForAFixedFooter() =>
        Assert.Equal(0, ScrollingStitch.FixedBottomRows(ScrollFixtures.Sample(0), ScrollFixtures.Sample(42), 78, new IntRange(0, 16)));

    [Theory]
    [InlineData(0, 120)]
    [InlineData(120, 120)]
    [InlineData(-1, 120)]
    public void FixedRowsNeedAnOverlapInsideTheView(int overlap, int _)
    {
        var start = ScrollFixtures.WithFixedBottom(0);
        var next = ScrollFixtures.WithFixedBottom(42);
        Assert.Equal(0, ScrollingStitch.FixedBottomRows(start, next, overlap, new IntRange(0, 16)));
    }

    [Fact]
    public void FixedRowsNeverFillTheWholeOverlap()
    {
        // A footer as tall as the overlap would leave nothing to stitch on.
        var start = ScrollFixtures.WithFixedBottom(0, 40);
        var next = ScrollFixtures.WithFixedBottom(42, 40);
        Assert.Equal(0, ScrollingStitch.FixedBottomRows(start, next, 40, new IntRange(0, 16)));
    }

    [Fact]
    public void NewPixelsMoveAboveTheFixedFooterInsteadOfCopyingIt()
    {
        Assert.Equal(new IntRange(60, 102), ScrollingStitch.NewContentRows(120, 78, 18));
        Assert.Equal(new IntRange(78, 120), ScrollingStitch.NewContentRows(120, 78, 0));
    }

    [Theory]
    [InlineData(0, 78, 0)]
    [InlineData(120, 0, 0)]
    [InlineData(120, 120, 0)]
    [InlineData(120, 78, -1)]
    [InlineData(120, 78, 78)]
    [InlineData(120, 78, 90)]
    public void NewContentRowsRejectImpossibleGeometry(int height, int overlap, int fixedRows) =>
        Assert.Null(ScrollingStitch.NewContentRows(height, overlap, fixedRows));

    [Theory]
    [InlineData(8)]
    [InlineData(76)]
    [InlineData(96)]
    [InlineData(104)]
    public void MatchingHandlesAnAdvanceOfEveryDistance(int offset)
    {
        var start = ScrollFixtures.Sample(0);
        Assert.Equal(Forward(120 - offset), ScrollingStitch.Transition(start, ScrollFixtures.Sample(offset)));
        Assert.Equal(Backward(120 - offset), ScrollingStitch.Transition(ScrollFixtures.Sample(offset), start));
    }

    [Fact]
    public void AFullWidthSampleAtARealisticRetinaHeightMatches() =>
        Assert.Equal(Forward(342, 32),
            ScrollingStitch.Transition(ScrollFixtures.Sample(0, 32, 900), ScrollFixtures.Sample(558, 32, 900)));

    [Fact]
    public void MovingContentIsIsolatedFromFixedSideColumns() =>
        Assert.Equal(ScrollingTransition.Advanced(78, ScrollingDirection.Forward, new IntRange(6, 18)),
            ScrollingStitch.Transition(ScrollFixtures.WithFixedColumns(0), ScrollFixtures.WithFixedColumns(42)));

    [Fact]
    public void MatchingSurvivesAChangedBlockInsideMovingContent() =>
        Assert.Equal(ScrollingTransition.Advanced(78, ScrollingDirection.Forward, new IntRange(6, 18)),
            ScrollingStitch.Transition(ScrollFixtures.WithFixedColumns(0), ScrollFixtures.WithFixedColumns(42, changesAfterScroll: true)));

    [Fact]
    public void MovingSampleColumnsMapBackToTheExactCapturedPixels() =>
        Assert.Equal(new IntRange(480, 1_440), ScrollingStitch.PixelRange(new IntRange(6, 18), 24, 1_920));

    [Theory]
    [InlineData(0, 0, 24, 100)]
    [InlineData(6, 18, 0, 100)]
    [InlineData(6, 18, 24, 0)]
    [InlineData(-1, 4, 24, 100)]
    [InlineData(6, 30, 24, 100)]
    [InlineData(6, 6, 24, 100)]
    public void PixelRangeRejectsEmptyOrOutOfBoundsColumns(int lower, int upper, int sampleWidth, int imageWidth) =>
        Assert.Null(ScrollingStitch.PixelRange(new IntRange(lower, upper), sampleWidth, imageWidth));

    [Fact]
    public void PixelRangeRoundsOutwardToWholePixels() =>
        Assert.Equal(new IntRange(0, 4), ScrollingStitch.PixelRange(new IntRange(0, 1), 3, 10));

    [Fact]
    public void AmbiguousContentFailsInsteadOfInventingASeam()
    {
        var unmatched = new byte[16 * 120];
        for (var i = 0; i < unmatched.Length; i++) unmatched[i] = (byte)((i * 47 + i / 11) % 253);
        Assert.Equal(ScrollingTransition.Unmatched,
            ScrollingStitch.Transition(ScrollFixtures.Sample(0), new ScrollingSample(16, 120, unmatched)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquallyPreciseRepeatedRowsCannotSelectAnArbitraryScrollOffset(bool noisy)
    {
        static ScrollingSample Repeated(int offset, bool noisy)
        {
            var pixels = new byte[16 * 120];
            for (var i = 0; i < pixels.Length; i++)
            {
                var row = (i / 16 + offset) % 24;
                var noise = noisy && i % 17 == 0 ? 1 : 0;
                pixels[i] = (byte)(row * 9 + noise);
            }
            return new ScrollingSample(16, 120, pixels);
        }
        Assert.Equal(ScrollingTransition.Unmatched, ScrollingStitch.Transition(Repeated(0, false), Repeated(8, noisy)));
    }

    [Fact]
    public void AWideSampleKeepsNativeDetailWhereItChanges()
    {
        // 256 columns are searched through 32 representative ones; the moving columns map back out.
        var transition = ScrollingStitch.Transition(ScrollFixtures.Sample(0, 256, 300), ScrollFixtures.Sample(83, 256, 300));
        Assert.Equal(ScrollingTransitionKind.Advanced, transition.Kind);
        Assert.Equal(300 - 83, transition.Overlap);
        Assert.Equal(ScrollingDirection.Forward, transition.Direction);
    }

    [Fact]
    public void RetinaHeightMatchingIsExactAndDoesNotStall()
    {
        var start = ScrollFixtures.Sample(0, 256, 1340);
        var next = ScrollFixtures.Sample(83, 256, 1340);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var transition = ScrollingStitch.Transition(start, next);
        watch.Stop();
        Assert.Equal(ScrollingTransition.Advanced(1257, ScrollingDirection.Forward, new IntRange(0, 256)), transition);
        // Generous: the original unoptimised nested loops took seconds.
        Assert.True(watch.Elapsed.TotalSeconds < 5, $"matching took {watch.Elapsed.TotalSeconds}s");
    }

    [Fact]
    public void MatchingStopsWhenItsTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(ScrollingTransition.Unmatched,
            ScrollingStitch.Transition(ScrollFixtures.Sample(0), ScrollFixtures.Sample(40), null, cts.Token));
    }
}

public class AutoScrollStateTests
{
    [Fact]
    public void ObservesEachScrollAndWaitsForOverlappingFrames()
    {
        var automatic = new AutoScrollState();
        Assert.Equal(AutoScrollAction.Wait, automatic.Next(0, 120, settled: false));
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(0.25, 120, settled: true));
        Assert.Equal(AutoScrollAction.Wait, automatic.Next(0.4, 120, settled: true));
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(0.9, 162, settled: true));
        // Three attempts without new content stop it.
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(1.5, 162, settled: true));
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(2.1, 162, settled: true));
        Assert.Equal(AutoScrollAction.Finish, automatic.Next(2.7, 162, settled: true));
    }

    [Fact]
    public void AnAnimatedElementCannotKeepScrollingAliveIndefinitely()
    {
        var animatedBottom = new AutoScrollState();
        Assert.Equal(AutoScrollAction.Scroll, animatedBottom.Next(0, 120, settled: true));
        Assert.Equal(AutoScrollAction.Wait, animatedBottom.Next(1, 120, settled: false));
        Assert.Equal(AutoScrollAction.Scroll, animatedBottom.Next(1.6, 120, settled: false));
        Assert.Equal(AutoScrollAction.Scroll, animatedBottom.Next(3.2, 120, settled: false));
        Assert.Equal(AutoScrollAction.Finish, animatedBottom.Next(4.8, 120, settled: false));
    }

    [Fact]
    public void ScrollingStartsEvenWhenThePageNeverBecomesStill()
    {
        var animatedStart = new AutoScrollState();
        Assert.Equal(AutoScrollAction.Wait, animatedStart.Next(0, 120, settled: false));
        Assert.Equal(AutoScrollAction.Wait, animatedStart.Next(1, 120, settled: false));
        Assert.Equal(AutoScrollAction.Scroll, animatedStart.Next(1.6, 120, settled: false));
    }

    [Fact]
    public void AnUnmatchedFrameEndsPartiallyBecauseAnotherStepCouldLoseTheLastGoodFrame()
    {
        var automatic = new AutoScrollState();
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(0, 120, settled: true));
        Assert.Equal(AutoScrollAction.Partial, automatic.Next(0.6, 120, settled: true, unmatched: true));
    }

    [Fact]
    public void AnUnmatchedFrameIsIgnoredBeforeTheMinimumWait()
    {
        var automatic = new AutoScrollState();
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(0, 120, settled: true));
        Assert.Equal(AutoScrollAction.Wait, automatic.Next(0.3, 120, settled: true, unmatched: true));
    }

    [Fact]
    public void ProgressResetsTheCountOfAttemptsWithoutIt()
    {
        var automatic = new AutoScrollState();
        automatic.Next(0, 100, settled: true);
        automatic.Next(0.6, 100, settled: true);          // attempt 1 without progress, scrolls
        automatic.Next(1.2, 100, settled: true);          // attempt 2, scrolls
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(1.8, 140, settled: true)); // progress: reset
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(2.4, 140, settled: true));
        Assert.Equal(AutoScrollAction.Scroll, automatic.Next(3.0, 140, settled: true));
        Assert.Equal(AutoScrollAction.Finish, automatic.Next(3.6, 140, settled: true));
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(0, 1)]
    [InlineData(100, 15)]
    [InlineData(400, 60)]
    [InlineData(1_080, 160)]
    [InlineData(5_000, 160)]
    public void TheWheelStepIsAFifthTeenthOfTheRegionBetweenOneAndOneSixty(double height, int expected) =>
        Assert.Equal(expected, ScrollingCaptureSession.AutomaticScrollStep(height));
}

public class ScrollingCaptureSessionTests
{
    private static BgraFrame F(int offset, int width = 32, int height = 120, int seed = 0, int? contentWidth = null, bool sparse = false) =>
        ScrollFixtures.Frame(offset, width, height, seed, contentWidth, sparse);

    private static void AssertSamePixels(BgraFrame expected, BgraFrame? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Width, actual!.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.True(ScrollFixtures.Bytes(expected).AsSpan().SequenceEqual(ScrollFixtures.Bytes(actual)), "stitched pixels differ");
    }

    private static ScrollingResult Capture(params BgraFrame?[] frames) => new ScrollingHarness(frames).Run();

    [Fact]
    public void TallRetinaBitmapStitchingNeverCreatesTransparentSeamRows()
    {
        var heights = new[] { 1522, 160, 240, 320, 720, 400, 80, 1120, 400, 400, 78 };
        var slices = heights.Select(h =>
        {
            var pixels = new byte[2876 * h * 4];
            Array.Fill(pixels, (byte)255);
            return new BgraFrame(2876, h, 2876 * 4, pixels, hasAlpha: true);
        }).ToList();
        var joined = BgraFrame.Stitch(slices);
        Assert.NotNull(joined);
        Assert.Equal(5440, joined!.Height);
        Assert.All(Enumerable.Range(0, joined.Height), y => Assert.Equal(255, joined.AlphaAt(0, y)));
    }

    [Fact]
    public void AStationaryPageWithAnAnimatedBannerNeverAddsEmptyRows()
    {
        var result = Capture(ScrollFixtures.StationaryPage(0), ScrollFixtures.StationaryPage(12),
                             ScrollFixtures.StationaryPage(24), ScrollFixtures.StationaryPage(36));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void DoneIncludesTheLastFrameWithoutAWheelEventAndKeepsGeometry()
    {
        var result = Capture(F(0), F(42));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(162, result.Image!.Height);
        AssertSamePixels(F(0, height: 162), result.Image);
    }

    [Fact]
    public void ANarrowMovingColumnNeverCropsTheSelectedBlankSideArea()
    {
        var result = Capture(F(0, contentWidth: 8), F(42, contentWidth: 8));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(F(0, height: 162, contentWidth: 8), result.Image);
    }

    [Fact]
    public void TransparentCaptureEdgePaddingNeverBecomesASeamOrLosesRows()
    {
        var result = Capture(ScrollFixtures.PaddedFrame(0), ScrollFixtures.PaddedFrame(42), ScrollFixtures.PaddedFrame(72));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(F(0, height: 192), result.Image);
    }

    [Fact]
    public void ALongCaptureKeepsEveryRowAfterANearPageAdvance()
    {
        var result = Capture(new[] { 0, 42, 84, 180, 276, 372, 468, 564, 660 }.Select(o => F(o, seed: 91)).ToArray());
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(F(0, height: 780, seed: 91), result.Image);
    }

    [Fact]
    public void WhiteBandsOnALongPageNeverTurnForwardScrollingIntoBacktracking()
    {
        var result = Capture(new[] { 0, 8, 94, 180, 266, 352, 438, 524, 610 }.Select(o => F(o, seed: 91, sparse: true)).ToArray());
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(F(0, height: 730, seed: 91, sparse: true), result.Image);
    }

    [Fact]
    public void DoneWithoutScrollingReturnsTheInitialCapture()
    {
        var result = Capture(F(0));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void UnmatchedScrollingPreservesTheFirstFrame()
    {
        var result = Capture(F(0), F(0, seed: 91));
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void UnrelatedSparsePagesCannotBeStitchedJustOnWhiteSpace()
    {
        var result = Capture(F(0, seed: 91, sparse: true), F(104, seed: 731, sparse: true));
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void ALaterMismatchPreservesAllSuccessfullyStitchedContent()
    {
        var result = Capture(F(0), F(42), F(0, seed: 91));
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        AssertSamePixels(F(0, height: 162), result.Image);
    }

    [Fact]
    public void ALaterCaptureFailureKeepsTheExistingPixels()
    {
        var result = Capture(F(0), null);
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void BacktrackingAndResumingNeverDuplicatesCapturedContent()
    {
        var result = Capture(F(0), F(42), F(12), F(72));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(F(0, height: 192), result.Image);
    }

    [Fact]
    public void FailureBeforeAnyFrameIsReportedAsFailed() =>
        Assert.Equal(ScrollingResultKind.Failed, Capture(new BgraFrame?[] { null }).Kind);

    [Fact]
    public void ACancelledSessionDeliversNoImage()
    {
        var harness = new ScrollingHarness([F(0)]);
        harness.Cancellation.Cancel();
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Cancelled, result.Kind);
        Assert.Null(result.Image);
    }

    [Fact]
    public void TerminalOutputKeepsEveryLineWithoutGapsDuplicatesOrShifts()
    {
        var terminal = ScrollFixtures.TerminalDocument();
        var frames = Enumerable.Range(0, 9).Select(i => ScrollFixtures.Crop(terminal, i * 15 * 28, 30 * 28)).ToArray();
        var result = Capture(frames);
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(terminal.Width, result.Image!.Width);
        Assert.Equal(terminal.Height, result.Image.Height);
        AssertSamePixels(terminal, result.Image);
    }

    [Fact]
    public void TerminalBacktrackingAndPartialTextRowsKeepExactAlignment()
    {
        var terminal = ScrollFixtures.TerminalDocument();
        var frames = new[] { 0, 211, 87, 587, 1_000, 1_400 }.Select(o => ScrollFixtures.Crop(terminal, o, 30 * 28)).ToArray();
        var result = Capture(frames);
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        AssertSamePixels(ScrollFixtures.Crop(terminal, 0, 2_240), result.Image);
    }

    // ---- Automatic scrolling ------------------------------------------------------------------------

    [Fact]
    public void AutomaticCaptureStopsAtTheBottomWithoutPressingDone()
    {
        var harness = new ScrollingHarness([F(0), F(42), F(72)], automatic: true);
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(192, result.Image!.Height);
        Assert.Equal(new[] { 120, 162, 192 }, harness.Heights);
        Assert.Equal(5, harness.Scrolls);
        Assert.False(harness.FinishRequested);
    }

    [Fact]
    public void AutomaticCaptureAlreadyAtTheBottomDoesNotInflateTheImage()
    {
        var harness = new ScrollingHarness(new[] { 0, 12, 24, 36 }.Select(o => (BgraFrame?)ScrollFixtures.StationaryPage(o)), automatic: true);
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(120, result.Image!.Height);
        Assert.Equal(new[] { 120 }, harness.Heights);
        Assert.Equal(3, harness.Scrolls);
    }

    [Fact]
    public void AnAnimatedStartingViewportStillScrollsAndFinishes()
    {
        var harness = new ScrollingHarness([ScrollFixtures.StationaryPage(0)], automatic: true) { AnimateBeforeFirstScroll = true };
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(120, result.Image!.Height);
        Assert.Equal(3, harness.Scrolls);
    }

    [Fact]
    public void DoneDuringFrameAcquisitionPreventsTheNextWheelEvent()
    {
        var harness = new ScrollingHarness([F(0)], automatic: true);
        harness.OnRead = call => { if (call == 2) harness.FinishRequested = true; };
        harness.Run();
        Assert.Equal(0, harness.Scrolls);
    }

    [Fact]
    public void CancelDuringFrameAcquisitionSendsNoFurtherWheelEvent()
    {
        var harness = new ScrollingHarness([F(0)], automatic: true);
        harness.OnRead = call => { if (call == 2) harness.Cancellation.Cancel(); };
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Cancelled, result.Kind);
        Assert.Equal(0, harness.Scrolls);
    }

    [Fact]
    public void ATargetCoveredDuringFrameAcquisitionNeverContributesPixelsOrAnotherEvent()
    {
        var harness = new ScrollingHarness([F(0), F(42)], automatic: true);
        harness.OnRead = _ => { if (harness.Scrolls == 1) harness.TargetIsCurrent = false; };
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        Assert.Equal(1, harness.Scrolls);
        AssertSamePixels(F(0), result.Image);
    }

    [Fact]
    public void AnAmbiguousMovingBannerOnWhiteCannotFabricateHeightOrSkipAnotherViewport()
    {
        var banner = new IntRange(6, 42);
        var harness = new ScrollingHarness(
            new[] { 0, 6, 12 }.Select(o => (BgraFrame?)ScrollFixtures.StationaryPage(o, blankBackground: true, bannerRows: banner)), automatic: true);
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Partial, result.Kind);
        Assert.Equal(120, result.Image!.Height);
        Assert.Equal(1, harness.Scrolls);
    }

    [Fact]
    public void AFixedHeaderCannotFinishAMovingSparsePageEarlyOrHideAnyDocumentRow()
    {
        var harness = new ScrollingHarness(new[] { 0, 12, 24, 36 }.Select(o => (BgraFrame?)ScrollFixtures.SparsePage(o)), automatic: true);
        var result = harness.Run();
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        Assert.Equal(new[] { 120, 132, 144, 156 }, harness.Heights);
        AssertSamePixels(ScrollFixtures.SparsePage(0, 156), result.Image);
    }

    [Fact]
    public void ALostWheelEventEndsThePageWithWhatWasJoined()
    {
        var session = new ScrollingCaptureSession(autoScroll: true);
        Assert.Null(session.Begin(F(0), 0));
        var result = session.AfterScroll(delivered: false);
        Assert.NotNull(result);
        Assert.Equal(ScrollingResultKind.Partial, result!.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void ADeliveredWheelEventOrAPendingDoneLetsTheLoopContinue()
    {
        var session = new ScrollingCaptureSession(autoScroll: true);
        Assert.Null(session.AfterScroll(delivered: true));
        Assert.Null(session.AfterScroll(delivered: false, cancelledOrFinishRequested: true));
    }

    // ---- Safeguards and bookkeeping -------------------------------------------------------------------

    [Fact]
    public void TheDurationSafeguardKeepsWhatWasCapturedAsLimited()
    {
        var session = new ScrollingCaptureSession();
        session.Begin(F(0), 0);
        Assert.Null(session.PreCheck(119.9, finishRequested: false));
        var result = session.PreCheck(120, finishRequested: false);
        Assert.NotNull(result);
        Assert.Equal(ScrollingResultKind.Limited, result!.Kind);
        Assert.Equal(120, result.Image!.Height);
    }

    [Fact]
    public void TheFrameCountSafeguardStopsAtFiveHundredAndTwelveStrips()
    {
        // Each step advances 20 rows of a 40-row view: 512 strips are reached long before the pixel cap.
        var session = new ScrollingCaptureSession();
        Assert.Null(session.Begin(F(0, height: 40, seed: 91), 0));
        var now = 0.0;
        ScrollingResult? limited = null;
        for (var step = 1; step < 700 && limited is null; step++)
        {
            now += 0.1;
            limited = session.PreCheck(now, false);
            if (limited is not null) break;
            var s = session.Ingest(F(step * 20, height: 40, seed: 91), now);
            limited = s.Result;
        }
        Assert.NotNull(limited);
        Assert.Equal(ScrollingResultKind.Limited, limited!.Kind);
        Assert.Equal(40 + 511 * 20, limited.Image!.Height);
    }

    [Fact]
    public void TheSamplingIntervalSpacesFrameReads()
    {
        var session = new ScrollingCaptureSession();
        session.Begin(F(0), 10);
        Assert.False(session.ReadyForFrame(10.05));
        Assert.True(session.ReadyForFrame(10.1));
        Assert.Equal(0.04, session.DelayUntilNextFrame(10.05), 6);
        Assert.Equal(0, session.DelayUntilNextFrame(11));
    }

    [Fact]
    public void AnUnusableFirstFrameFails()
    {
        var empty = new BgraFrame(4, 4, 16, new byte[64], hasAlpha: true);
        Assert.Equal(ScrollingResultKind.Failed, new ScrollingCaptureSession().Begin(empty, 0)!.Kind);
    }

    [Fact]
    public void TheProgressHeightGrowsOnlyOnForwardAdvances()
    {
        var session = new ScrollingCaptureSession();
        session.Begin(F(0), 0);
        Assert.Equal(120, session.TotalHeight);
        var step = session.Ingest(F(42), 0.1);
        Assert.Equal(162, step.ProgressHeight);
        Assert.Equal(162, session.TotalHeight);
        var back = session.Ingest(F(12), 0.2);
        Assert.Null(back.ProgressHeight);
        Assert.Equal(162, session.TotalHeight);
        Assert.Equal(2, session.SliceCount);
    }

    [Fact]
    public void AFixedFooterIsAppendedOnceAtTheEndInsteadOfRepeatedInEveryStrip()
    {
        BgraFrame Page(int offset)
        {
            var gray = ScrollFixtures.FrameGray(offset, 32, 120, seed: 91);
            for (var row = 102; row < 120; row++)
                for (var column = 0; column < 32; column++) gray[row * 32 + column] = (byte)((row * 29 + column * 11 + 41) % 251);
            return BgraFrame.FromGray(32, 120, gray);
        }
        var result = Capture(Page(0), Page(42), Page(84));
        Assert.Equal(ScrollingResultKind.Success, result.Kind);
        // 120 rows + 42 + 42 new rows; the 18 footer rows end the image exactly once.
        Assert.Equal(204, result.Image!.Height);
        var expectedGray = ScrollFixtures.FrameGray(0, 32, 204, seed: 91);
        for (var row = 186; row < 204; row++)
            for (var column = 0; column < 32; column++) expectedGray[row * 32 + column] = (byte)(((row - 84) * 29 + column * 11 + 41) % 251);
        AssertSamePixels(BgraFrame.FromGray(32, 204, expectedGray), result.Image);
    }

    [Fact]
    public void ContentFrameTrimsOnlyFullyTransparentOuterRowsAndColumns()
    {
        var padded = ScrollFixtures.PaddedFrame(0);
        var trimmed = padded.ContentFrame();
        Assert.Equal(32, trimmed!.Width);
        Assert.Equal(120, trimmed.Height);
        // An opaque-by-contract frame is returned untouched.
        var opaque = F(0);
        Assert.Same(opaque, opaque.ContentFrame());
        // A frame with no alpha anywhere has nothing to show.
        Assert.Null(new BgraFrame(8, 8, 32, new byte[256], hasAlpha: true).ContentFrame());
    }

    [Fact]
    public void ToSampleKeepsNativeRowsAndCapsTheWidth()
    {
        var wide = F(0, width: 600, height: 50);
        var sample = wide.ToSample()!;
        Assert.Equal(256, sample.Width);
        Assert.Equal(50, sample.Height);
        Assert.True(sample.IsValid);
        var narrow = F(0, width: 32, height: 50).ToSample()!;
        Assert.Equal(32, narrow.Width);
        Assert.True(narrow.Pixels.AsSpan().SequenceEqual(ScrollFixtures.FrameGray(0, 32, 50)));
    }

    [Fact]
    public void StitchJoinsStripsAtTheNarrowestWidthInOrder()
    {
        var a = BgraFrame.FromGray(4, 2, [1, 1, 1, 1, 2, 2, 2, 2]);
        var b = BgraFrame.FromGray(3, 1, [9, 9, 9]);
        var joined = BgraFrame.Stitch([a, b])!;
        Assert.Equal(3, joined.Width);
        Assert.Equal(3, joined.Height);
        Assert.Equal(1, joined.Pixels[0]);
        Assert.Equal(2, joined.Pixels[joined.Stride]);
        Assert.Equal(9, joined.Pixels[joined.Stride * 2]);
        Assert.Null(BgraFrame.Stitch([]));
    }

    [Fact]
    public void ACopiedStripOwnsItsPixels()
    {
        var frame = F(0, width: 8, height: 8);
        var strip = frame.CopyStrip(new IntRange(2, 6), 1, 3)!;
        Assert.Equal(4, strip.Width);
        Assert.Equal(4, strip.Height);
        Assert.Equal(frame.Pixels[1 * frame.Stride + 2 * 4], strip.Pixels[0]);
        Assert.Null(frame.CopyStrip(new IntRange(2, 6), 5, 5));
        Assert.Null(frame.CopyStrip(new IntRange(2, 20), 0, 0));
    }
}
