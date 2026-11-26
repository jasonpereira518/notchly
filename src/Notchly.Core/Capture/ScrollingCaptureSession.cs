// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure parts of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotScrollingCapture.swift (the capture loop's state and
// decisions, contentFrame, sample, copiedStrip, stitch, AutoScrollState) and
// ScreenshotAutomaticScroller.swift (the wheel step size).
//
// Upstream's loop is an async function that reads a frame, decides, sleeps and repeats. Here the same
// decisions are a synchronous state machine: the host (a WPF capture service) owns the clock, the
// screen reads, the scroll injection and the cancellation, and calls
//   PreCheck(now, finishRequested)  -> ends the session on Done / the time and frame limits
//   ReadyForFrame(now)              -> true once the 0.09 s sampling interval has passed
//   Ingest(frame, now, ...)         -> matches the frame against the last accepted one
// in the same order as the loop. No timers, no bitmap classes: frames are raw BGRA bytes.
namespace Notchly.Core.Capture;

/// <summary>
/// A captured view as raw bytes: BGRA, 8 bits per channel, <see cref="Stride"/> bytes per row, rows
/// top to bottom. Colour is premultiplied when <see cref="HasAlpha"/> is true.
/// </summary>
public sealed class BgraFrame
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    /// <summary>
    /// False when the capture API leaves the alpha byte meaningless (GDI and DXGI often write 0 or 255
    /// regardless). Only a frame that really carries alpha has its transparent edge rows trimmed.
    /// </summary>
    public bool HasAlpha { get; }

    public BgraFrame(int width, int height, int stride, byte[] pixels, bool hasAlpha = false)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < stride * height)
            throw new ArgumentException("Frame dimensions do not match its pixel buffer.");
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        HasAlpha = hasAlpha;
    }

    public static BgraFrame FromGray(int width, int height, byte[] gray)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = gray[i];
            pixels[i * 4 + 1] = gray[i];
            pixels[i * 4 + 2] = gray[i];
            pixels[i * 4 + 3] = 255;
        }
        return new BgraFrame(width, height, width * 4, pixels);
    }

    public byte AlphaAt(int x, int y) => Pixels[y * Stride + x * 4 + 3];

    /// <summary>
    /// A tightly packed copy of part of this frame. A copy, not a window, so the retained-pixel guard
    /// is truthful. Null when the range is empty or outside the frame.
    /// </summary>
    public BgraFrame? CopyStrip(IntRange columns, int topCrop, int bottomCrop)
    {
        var height = Height - topCrop - bottomCrop;
        if (!(Width > 0 && height > 0 && topCrop >= 0 && bottomCrop >= 0
              && columns.Lower >= 0 && columns.Upper <= Width && !columns.IsEmpty)) return null;
        var rowBytes = columns.Count * 4;
        var pixels = new byte[rowBytes * height];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, (topCrop + row) * Stride + columns.Lower * 4, pixels, row * rowBytes, rowBytes);
        return new BgraFrame(columns.Count, height, rowBytes, pixels, HasAlpha);
    }

    /// <summary>
    /// A screen capture can pad a region with a transparent edge row, which would leave a dark seam in
    /// each appended strip. Removes only fully transparent outer rows and columns so overlap and strip
    /// coordinates describe the same actual screen pixels. Null when nothing is drawn.
    /// </summary>
    public BgraFrame? ContentFrame()
    {
        if (!HasAlpha) return this;
        bool RowEmpty(int y, int left, int right)
        {
            for (var x = left; x < right; x++) if (AlphaAt(x, y) != 0) return false;
            return true;
        }
        bool ColumnEmpty(int x, int top, int bottom)
        {
            for (var y = top; y < bottom; y++) if (AlphaAt(x, y) != 0) return false;
            return true;
        }
        int topEdge = 0, bottomEdge = Height, left = 0, right = Width;
        while (topEdge < bottomEdge && RowEmpty(topEdge, 0, Width)) topEdge++;
        while (bottomEdge > topEdge && RowEmpty(bottomEdge - 1, 0, Width)) bottomEdge--;
        if (topEdge >= bottomEdge) return null;
        while (left < right && ColumnEmpty(left, topEdge, bottomEdge)) left++;
        while (right > left && ColumnEmpty(right - 1, topEdge, bottomEdge)) right--;
        if (left >= right) return null;
        if (topEdge == 0 && bottomEdge == Height && left == 0 && right == Width) return this;
        return CopyStrip(new IntRange(left, right), topEdge, Height - bottomEdge);
    }

    /// <summary>
    /// The grayscale view the matcher works on. Native vertical rows keep exact offsets; the width is
    /// capped at 256 (averaging horizontally) so a wide terminal keeps enough detail to tell digits
    /// apart while the matcher picks a few informative columns from it.
    /// </summary>
    public ScrollingSample? ToSample(int maxWidth = 256)
    {
        var width = Math.Min(maxWidth, Width);
        if (width <= 0 || Height <= 0) return null;
        var pixels = new byte[width * Height];
        for (var y = 0; y < Height; y++)
        {
            var rowStart = y * Stride;
            for (var x = 0; x < width; x++)
            {
                var from = x * Width / width;
                var to = Math.Max(from + 1, (x + 1) * Width / width);
                var sum = 0;
                for (var sx = from; sx < to; sx++)
                {
                    var p = rowStart + sx * 4;
                    // Rec. 601 luma (the weights DeviceGray uses), integer, exact for greys.
                    sum += (Pixels[p + 2] * 77 + Pixels[p + 1] * 150 + Pixels[p] * 29 + 128) >> 8;
                }
                pixels[y * width + x] = (byte)(sum / (to - from));
            }
        }
        return new ScrollingSample(width, Height, pixels);
    }

    /// <summary>Stacks strips top to bottom at the narrowest strip's width.</summary>
    public static BgraFrame? Stitch(IReadOnlyList<BgraFrame> slices)
    {
        if (slices.Count == 0) return null;
        if (slices.Count == 1) return slices[0];
        var width = slices.Min(s => s.Width);
        var height = slices.Sum(s => (long)s.Height);
        if (height > int.MaxValue / (width * 4L)) return null;
        var rowBytes = width * 4;
        var pixels = new byte[rowBytes * (int)height];
        var destinationRow = 0;
        foreach (var slice in slices)
        {
            for (var row = 0; row < slice.Height; row++)
                Buffer.BlockCopy(slice.Pixels, row * slice.Stride, pixels, (destinationRow + row) * rowBytes, rowBytes);
            destinationRow += slice.Height;
        }
        return new BgraFrame(width, (int)height, rowBytes, pixels, slices.Any(s => s.HasAlpha));
    }
}

public enum ScrollingResultKind
{
    /// <summary>Every scrolled view was joined with a confident overlap.</summary>
    Success,
    /// <summary>Some view could not be matched; the image holds what was joined before it.</summary>
    Partial,
    /// <summary>A safeguard (time, frame count or pixel budget) stopped the capture; the image keeps what it has.</summary>
    Limited,
    Cancelled,
    Failed,
}

public sealed record ScrollingResult(ScrollingResultKind Kind, BgraFrame? Image)
{
    public static readonly ScrollingResult Cancelled = new(ScrollingResultKind.Cancelled, null);
    public static readonly ScrollingResult Failed = new(ScrollingResultKind.Failed, null);
}

public enum AutoScrollAction { Wait, Scroll, Finish, Partial }

/// <summary>
/// The capture loop drives scrolling only after observing its previous attempt, so no independent
/// timer can keep scrolling after cancellation. Wait 0.55 s after a scroll (1.5 s if the page never
/// settles), stop after three attempts that added no height, and give up with a partial image when
/// the last frame could not be matched.
/// </summary>
public sealed class AutoScrollState
{
    public const double MinimumWait = 0.55, SettleTimeout = 1.5;
    public const int AttemptsWithoutProgress = 3;

    private double? _waitingSince;
    private double? _attemptedAt;
    private int _heightBeforeAttempt;
    private int _attemptsWithoutProgress;

    public AutoScrollAction Next(double now, int height, bool settled, bool unmatched = false)
    {
        _waitingSince ??= now;
        if (_attemptedAt is { } attemptedAt)
        {
            var elapsed = now - attemptedAt;
            if (!(elapsed >= MinimumWait && (settled || elapsed >= SettleTimeout))) return AutoScrollAction.Wait;
            // Another step could move the last good frame entirely out of view.
            if (unmatched) return AutoScrollAction.Partial;
            _attemptsWithoutProgress = height > _heightBeforeAttempt ? 0 : _attemptsWithoutProgress + 1;
            if (_attemptsWithoutProgress >= AttemptsWithoutProgress) return AutoScrollAction.Finish;
        }
        else if (!settled && now - (_waitingSince ?? now) < SettleTimeout)
        {
            return AutoScrollAction.Wait;
        }
        _attemptedAt = now;
        _heightBeforeAttempt = height;
        return AutoScrollAction.Scroll;
    }
}

/// <summary>What one frame did to a <see cref="ScrollingCaptureSession"/>.</summary>
/// <param name="Result">Non-null when the session is over.</param>
/// <param name="Action">What the automatic scroller should do next (only when auto-scroll is on and Done was not requested).</param>
/// <param name="ProgressHeight">Non-null when the stitched height grew: report it to the HUD.</param>
public readonly record struct ScrollingStep(ScrollingResult? Result, AutoScrollAction? Action, int? ProgressHeight);

/// <summary>
/// One scrolling screenshot: the accepted strips, the matcher state and the stop rules. See the file
/// header for how the host drives it.
/// </summary>
public sealed class ScrollingCaptureSession
{
    public const double SampleInterval = 0.09;
    public const double SettleInterval = 0.22;
    public const double FinishGraceInterval = 0.85;

    /// <summary>
    /// Wheel travel of one automatic step in points: 15 percent of the region's height, 1 to 160
    /// (truncated, as Int32(...) is). The host converts it to wheel delta units and sends it downward.
    /// </summary>
    public static int AutomaticScrollStep(double regionHeight) => (int)Math.Min(160, Math.Max(1, regionHeight * 0.15));

    private readonly bool _autoScroll;
    private readonly AutoScrollState _automatic = new();
    private readonly List<BgraFrame> _slices = [];
    private BgraFrame? _first;
    private ScrollingSample _previousSample = new(0, 0, []);
    private ScrollingSample _lastObservedSample = new(0, 0, []);
    private int _retainedPixels;
    private IntRange? _contentSampleColumns;
    private IntRange? _contentPixelColumns;
    private int _fixedBottomPixels;
    private BgraFrame? _footerSlice;
    private bool _hasUnmatchedContent;
    private double _startedAt;
    private double _lastChangedAt;
    private double _lastCaptureAt;
    private bool _scrollPending;
    private double? _finishRequestedAt;
    private bool _over;

    public ScrollingCaptureSession(bool autoScroll = false) => _autoScroll = autoScroll;

    /// <summary>Height of the stitched image so far.</summary>
    public int TotalHeight { get; private set; }

    public int SliceCount => _slices.Count;

    /// <summary>The first view. Null result: carry on and report <see cref="TotalHeight"/>; Failed: the frame was unusable.</summary>
    public ScrollingResult? Begin(BgraFrame firstFrame, double now)
    {
        if (firstFrame.ContentFrame() is not { } first || first.ToSample() is not { } sample) return ScrollingResult.Failed;
        _first = first;
        _slices.Clear();
        _slices.Add(first);
        _previousSample = sample;
        _lastObservedSample = sample;
        TotalHeight = first.Height;
        _retainedPixels = first.Width * first.Height;
        _startedAt = now;
        _lastChangedAt = now;
        _lastCaptureAt = now;
        return null;
    }

    /// <summary>
    /// Runs at the top of every loop turn. Handles Done (one last frame is still taken, bounded by a
    /// grace period) and the duration and frame-count safeguards. Non-null when the session is over.
    /// </summary>
    public ScrollingResult? PreCheck(double now, bool finishRequested)
    {
        if (_over) return ScrollingResult.Cancelled;
        if (finishRequested && _finishRequestedAt is null)
        {
            _finishRequestedAt = now;
            // Take one final frame even if no wheel event was delivered.
            _scrollPending = true;
        }
        if (_finishRequestedAt is { } requestedAt && (!_scrollPending || now - requestedAt >= FinishGraceInterval))
            return Complete(_hasUnmatchedContent ? ScrollingResultKind.Partial : ScrollingResultKind.Success);
        if (now - _startedAt >= ScreenshotSupport.ScrollingCaptureMaximumDuration
            || _slices.Count >= ScreenshotSupport.ScrollingCaptureMaximumFrames)
            return Complete(ScrollingResultKind.Limited);
        return null;
    }

    /// <summary>Whether enough time passed since the last read; otherwise wait <see cref="DelayUntilNextFrame"/>.</summary>
    public bool ReadyForFrame(double now) => now - _lastCaptureAt >= SampleInterval;

    public double DelayUntilNextFrame(double now) => Math.Max(0, SampleInterval - (now - _lastCaptureAt));

    /// <summary>
    /// Takes one more view. <paramref name="frame"/> null (the read failed) or
    /// <paramref name="targetCurrent"/> false (the scroll target was covered or moved) ends the session
    /// with what is already joined, as a partial image.
    /// </summary>
    public ScrollingStep Ingest(BgraFrame? frame, double now, bool targetCurrent = true, bool finishRequested = false,
                                CancellationToken cancellation = default)
    {
        if (_over || _first is null) return new ScrollingStep(ScrollingResult.Failed, null, null);
        if (frame is null || !targetCurrent || frame.ContentFrame() is not { } current || current.ToSample() is not { } currentSample)
            return new ScrollingStep(Complete(ScrollingResultKind.Partial), null, null);
        if (cancellation.IsCancellationRequested) return new ScrollingStep(Cancel(), null, null);

        _lastCaptureAt = now;
        var frameIsStable = ScrollingStitch.SamplesAreStable(_lastObservedSample, currentSample, _contentSampleColumns);
        _lastObservedSample = currentSample;
        if (!frameIsStable) _lastChangedAt = now;
        var transition = ScrollingStitch.Transition(_previousSample, currentSample, _contentSampleColumns, cancellation);
        int? progress = null;

        switch (transition.Kind)
        {
            case ScrollingTransitionKind.End:
                _hasUnmatchedContent = false;
                break;

            case ScrollingTransitionKind.Advanced when transition.Direction == ScrollingDirection.Forward:
            {
                var sampleOverlap = transition.Overlap;
                var overlap = (int)Math.Round((double)sampleOverlap / currentSample.Height * current.Height, MidpointRounding.AwayFromZero);
                if (!(overlap > 0 && overlap < current.Height)) return new ScrollingStep(ScrollingResult.Failed, null, null);
                var establishingContent = _contentSampleColumns is null;
                if (establishingContent)
                {
                    // Matching may use only the narrow text at one edge of a terminal. It is evidence of
                    // movement, not the bounds of the selected image; keep every selected column.
                    var pixelColumns = new IntRange(0, current.Width);
                    var fixedBottomRows = ScrollingStitch.FixedBottomRows(_previousSample, currentSample, sampleOverlap, transition.ContentColumns);
                    _fixedBottomPixels = (int)Math.Round((double)fixedBottomRows / currentSample.Height * current.Height, MidpointRounding.AwayFromZero);
                    if (ScrollingStitch.NewContentRows(current.Height, overlap, _fixedBottomPixels) is not { } newRows
                        || _first.CopyStrip(pixelColumns, 0, _fixedBottomPixels) is not { } croppedFirst)
                        return new ScrollingStep(ScrollingResult.Failed, null, null);
                    _contentSampleColumns = transition.ContentColumns;
                    _contentPixelColumns = pixelColumns;
                    _slices[0] = croppedFirst;
                    _retainedPixels = croppedFirst.Width * croppedFirst.Height;

                    if (_fixedBottomPixels > 0)
                    {
                        if (current.CopyStrip(pixelColumns, newRows.Upper, 0) is not { } footer)
                            return new ScrollingStep(ScrollingResult.Failed, null, null);
                        _footerSlice = footer;
                        _retainedPixels += footer.Width * footer.Height;
                    }
                }
                if (_contentPixelColumns is not { } columns) return new ScrollingStep(ScrollingResult.Failed, null, null);
                if (ScrollingStitch.NewContentRows(current.Height, overlap, _fixedBottomPixels) is not { } rows)
                    return new ScrollingStep(ScrollingResult.Failed, null, null);
                var stripHeight = rows.Count;
                var nextHeight = TotalHeight + stripHeight;
                if (columns.IsEmpty || nextHeight > ScreenshotSupport.ScrollingCaptureMaximumPixels / columns.Count)
                    return new ScrollingStep(Complete(ScrollingResultKind.Limited), null, null);
                if (current.CopyStrip(columns, rows.Lower, current.Height - rows.Upper) is not { } strip)
                    return new ScrollingStep(ScrollingResult.Failed, null, null);
                var stripPixels = strip.Width * strip.Height;
                if (stripPixels > ScreenshotSupport.ScrollingCaptureMaximumRetainedPixels
                    || _retainedPixels > ScreenshotSupport.ScrollingCaptureMaximumRetainedPixels - stripPixels)
                    return new ScrollingStep(Complete(ScrollingResultKind.Limited), null, null);
                if (_fixedBottomPixels > 0 && !establishingContent)
                {
                    if (current.CopyStrip(columns, current.Height - _fixedBottomPixels, 0) is not { } footer)
                        return new ScrollingStep(ScrollingResult.Failed, null, null);
                    _footerSlice = footer;
                }
                // Keep only new pixels. Retaining every full frame made a common Retina selection hit
                // the memory guard after about eleven scrolls.
                _slices.Add(strip);
                TotalHeight = nextHeight;
                _retainedPixels += stripPixels;
                _previousSample = currentSample;
                _hasUnmatchedContent = false;
                progress = TotalHeight;
                break;
            }

            case ScrollingTransitionKind.Advanced:
                // Scrolling back never duplicates pixels already kept. A later forward movement can
                // continue from the furthest accepted frame without inventing a seam.
                _hasUnmatchedContent = _autoScroll;
                break;

            default:
                _hasUnmatchedContent = true;
                break;
        }

        _scrollPending = !frameIsStable || _lastCaptureAt - _lastChangedAt < SettleInterval;
        AutoScrollAction? action = null;
        if (_autoScroll && !finishRequested)
        {
            switch (_automatic.Next(_lastCaptureAt, TotalHeight, !_scrollPending, _hasUnmatchedContent))
            {
                case AutoScrollAction.Finish:
                    return new ScrollingStep(Complete(ScrollingResultKind.Success), null, progress);
                case AutoScrollAction.Partial:
                    return new ScrollingStep(Complete(ScrollingResultKind.Partial), null, progress);
                case var other:
                    action = other;
                    break;
            }
        }
        return new ScrollingStep(null, action, progress);
    }

    /// <summary>
    /// After the host acts on <see cref="AutoScrollAction.Scroll"/>: when the wheel event could not be
    /// delivered the capture ends partially. Done or Cancel requested meanwhile counts as delivered, so
    /// the loop's own checks end the session.
    /// </summary>
    public ScrollingResult? AfterScroll(bool delivered, bool cancelledOrFinishRequested = false) =>
        delivered || cancelledOrFinishRequested ? null : Complete(ScrollingResultKind.Partial);

    /// <summary>Stitches what was kept into the result of the given kind.</summary>
    public ScrollingResult Complete(ScrollingResultKind kind)
    {
        if (_over) return ScrollingResult.Cancelled;
        _over = true;
        var slices = _footerSlice is { } footer ? [.. _slices, footer] : _slices.ToList();
        return BgraFrame.Stitch(slices) is { } image ? new ScrollingResult(kind, image) : ScrollingResult.Failed;
    }

    public ScrollingResult Cancel()
    {
        _over = true;
        return ScrollingResult.Cancelled;
    }
}
