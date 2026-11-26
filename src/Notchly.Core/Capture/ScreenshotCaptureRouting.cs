// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the capture-policy parts of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift (unified capture policy, delay and
// preview timing, guide visibility, countdown ring, scrolling controls frame), the routing decision in
// ScreenshotService.route / runDefaultAction, the shared countdown of ScreenCaptureService
// (performAfterDelay / cancelCountdownIfActive) and the dismissal timer of
// ScreenshotQuickPreviewController (scheduleAutoDismiss / hoverChanged).
// Nothing here reads settings: callers pass the stored values in.
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>Pure screenshot capture policy: what a mode photographs, what to show afterwards, for how long.</summary>
public static class ScreenshotSupport
{
    // ---- Unified capture policy ------------------------------------------------------------------

    /// <param name="KeepsContentWindowsOut">
    /// Whether editors and pinned captures stay out of the picture and the pickable windows. Recording
    /// keeps them out even while "hide own windows" is off, which that flag alone cannot tell apart.
    /// </param>
    public readonly record struct UnifiedCapturePolicy(bool Freeze, bool IncludePointer, bool HideOwnWindows,
                                                       bool KeepsContentWindowsOut, bool UsesGeometry)
    {
        /// <summary>
        /// Two tools can want the same photograph and still do different things with it, so only the
        /// fields that decide which pixels are taken, and which windows can be picked, force a new one.
        /// </summary>
        public bool SharesSource(UnifiedCapturePolicy other) =>
            Freeze == other.Freeze && IncludePointer == other.IncludePointer
            && HideOwnWindows == other.HideOwnWindows && KeepsContentWindowsOut == other.KeepsContentWindowsOut;
    }

    public static UnifiedCapturePolicy UnifiedCapturePolicyFor(ScreenCaptureTool tool, bool screenshotFreeze,
                                                               bool screenshotIncludePointer,
                                                               bool screenshotHideOwnWindows) =>
        new(Freeze: tool == ScreenCaptureTool.Scrolling ? false : tool == ScreenCaptureTool.Screenshot ? screenshotFreeze : true,
            IncludePointer: tool == ScreenCaptureTool.Screenshot && screenshotIncludePointer,
            HideOwnWindows: tool != ScreenCaptureTool.Recording && screenshotHideOwnWindows,
            KeepsContentWindowsOut: tool == ScreenCaptureTool.Recording || screenshotHideOwnWindows,
            UsesGeometry: tool is ScreenCaptureTool.Recording or ScreenCaptureTool.Scrolling);

    public static bool CaptureAvailabilityChanged(IReadOnlyList<ScreenCaptureTool> activeTools,
                                                  IReadOnlyList<ScreenCaptureTool> availableTools) =>
        !activeTools.SequenceEqual(availableTools);

    public static bool CaptureRouteIsAuthorized(ScreenCaptureTool selected, Func<Features.AppFeature, bool> isAvailable) =>
        isAvailable(selected.Feature());

    // ---- Selection surface -----------------------------------------------------------------------

    public static double SelectionDimAlpha(bool notchControls, bool isFrozen, bool isDragging)
    {
        if (notchControls) return isDragging ? 0.18 : 0;
        return isFrozen ? 0.22 : 0.18;
    }

    public static bool CaptureGuideIsVisible(bool pointerOnDisplay, bool selectionInProgress, bool capturePending) =>
        pointerOnDisplay && !selectionInProgress && !capturePending;

    public static bool FullScreenCaptureControlIsAvailable(ScreenCaptureTool? selectedTool, bool standaloneScreenshot,
                                                           bool requiresDraggedRegion, bool scrollingCaptureEnabled)
    {
        var isScreenshot = standaloneScreenshot || selectedTool == ScreenCaptureTool.Screenshot;
        return isScreenshot && !requiresDraggedRegion && !scrollingCaptureEnabled;
    }

    public static bool FullScreenCaptureControlIsVisible(bool isAvailable, bool pointerOnDisplay,
                                                         bool selectionInProgress, bool capturePending) =>
        isAvailable && pointerOnDisplay && !selectionInProgress && !capturePending;

    /// <summary>
    /// The full-screen button sits below the screen chrome (the taskbar or notch strip) and below any
    /// island capture controls.
    /// </summary>
    public static double FullScreenCaptureControlTopInset(double screenChromeHeight, double? notchControlsHeight)
    {
        var chrome = double.IsFinite(screenChromeHeight) ? Math.Max(0, screenChromeHeight) : 0;
        var controls = notchControlsHeight is { } h && double.IsFinite(h) ? Math.Max(0, h) : 0;
        return Math.Max(chrome, controls) + 12;
    }

    /// <summary>
    /// Whether the guide should advertise repeating the last capture region. The region belongs to one
    /// display, so the hint stays hidden until pressing the key would really capture something.
    /// </summary>
    public static bool OffersRepeatLastRegion(bool isPickingColor, bool storedRegionDisplayIsAvailable) =>
        !isPickingColor && storedRegionDisplayIsAvailable;

    /// <summary>
    /// Once a capture is on its way, or the session is over, every remaining pointer event has to do
    /// nothing (a drag made with three fingers delivers exactly those late events).
    /// </summary>
    public static bool SelectionAcceptsPointerInput(bool sessionIsOver, bool capturePending) =>
        !sessionIsOver && !capturePending;

    // ---- Delay and after-capture preview ---------------------------------------------------------

    /// <summary>Optional countdown before the capture starts, so menus and hover states can be staged first.</summary>
    public static readonly IReadOnlyList<int> AllowedDelays = [0, 3, 5, 10];

    public static int SanitizedDelay(int raw) => AllowedDelays.Contains(raw) ? raw : 0;

    /// <summary>Seconds the confirmation stays up; 0 keeps it until dismissed.</summary>
    public static readonly IReadOnlyList<int> ConfirmationPreviewDurations = [1, 2, 3, 5, 10, 0];
    public const int DefaultConfirmationPreviewDuration = 3;
    public const double RecoveryPreviewDismissInterval = 12;

    /// <summary>How the confirmation behaves: whether it takes focus, closes when collapsed, shows a dismiss button.</summary>
    public readonly record struct ConfirmationPreviewPresentationPolicy(bool TakesFocus, bool ClosesOnCollapse, bool ShowsDismissButton);

    public static int SanitizedConfirmationPreviewDuration(int raw) =>
        ConfirmationPreviewDurations.Contains(raw) ? raw : DefaultConfirmationPreviewDuration;

    /// <summary>Null (never dismisses on its own) for the persistent choice.</summary>
    public static double? ConfirmationPreviewDismissInterval(int raw)
    {
        var duration = SanitizedConfirmationPreviewDuration(raw);
        return duration == 0 ? null : duration;
    }

    /// <summary>A shared (uploaded) capture keeps its result up for 30 s, unless the base never dismisses.</summary>
    public static double? SharedPreviewDismissInterval(double? baseInterval) => baseInterval is null ? null : 30;

    public static ConfirmationPreviewPresentationPolicy ConfirmationPreviewPresentationPolicyFor(double? dismissInterval, bool prefersFocus)
    {
        var persistent = dismissInterval is null;
        return new(TakesFocus: !persistent && prefersFocus, ClosesOnCollapse: persistent, ShowsDismissButton: persistent);
    }

    public static bool AutomaticActionSucceeded(ScreenshotDefaultAction action, bool saved, bool copied) => action switch
    {
        ScreenshotDefaultAction.None or ScreenshotDefaultAction.Edit => false,
        ScreenshotDefaultAction.Save => saved,
        ScreenshotDefaultAction.SaveAndCopy => saved && copied,
        _ => copied,
    };

    public static bool ShouldShowQuickPreview(ScreenshotDefaultAction defaultAction, bool saved, bool copied, bool confirmationEnabled) =>
        defaultAction switch
        {
            ScreenshotDefaultAction.None => true,
            ScreenshotDefaultAction.Edit => false,
            _ => confirmationEnabled || !AutomaticActionSucceeded(defaultAction, saved, copied),
        };

    /// <summary>What a finished capture shows once its after-capture action ran. A null interval keeps the preview until dismissed.</summary>
    public readonly record struct QuickPreviewPresentation(bool IsShown, double? DismissInterval)
    {
        public static readonly QuickPreviewPresentation Hidden = new(false, null);
        public static QuickPreviewPresentation Shown(double? dismissInterval) => new(true, dismissInterval);
    }

    /// <summary>
    /// The whole decision <c>route</c> makes, from the stored settings: a successful action shows the
    /// confirmation for its chosen duration or nothing at all, while a failed or partial action, or no
    /// action, gets the recovery preview with its longer timer.
    /// </summary>
    /// <param name="storedDuration">The raw stored duration preference; anything that is not a number reads as the default.</param>
    public static QuickPreviewPresentation QuickPreviewPresentationFor(ScreenshotDefaultAction defaultAction, bool saved,
                                                                       bool copied, bool previewEnabled, object? storedDuration)
    {
        if (!ShouldShowQuickPreview(defaultAction, saved, copied, previewEnabled)) return QuickPreviewPresentation.Hidden;
        if (!AutomaticActionSucceeded(defaultAction, saved, copied))
            return QuickPreviewPresentation.Shown(RecoveryPreviewDismissInterval);
        // A stored value that is not a number reads as the default duration, not as 0, which would keep
        // every confirmation until dismissed.
        var duration = storedDuration switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            short s => s,
            byte b => b,
            double d when double.IsFinite(d) => (int)Math.Clamp(Math.Truncate(d), int.MinValue, int.MaxValue),
            float f when float.IsFinite(f) => (int)Math.Clamp(Math.Truncate(f), int.MinValue, int.MaxValue),
            _ => DefaultConfirmationPreviewDuration,
        };
        return QuickPreviewPresentation.Shown(ConfirmationPreviewDismissInterval(duration));
    }

    /// <summary>Captures go to the shelf on their own only while the shelf is installed and on.</summary>
    public static bool AddsCapturesToShelf(bool addToShelfPreference, bool shelfInstalled, bool shelfEnabled) =>
        addToShelfPreference && shelfInstalled && shelfEnabled;

    /// <summary>
    /// Remaining stroke for the one-second countdown ring. Time drives the value directly so a delayed
    /// frame catches up instead of restarting the animation.
    /// </summary>
    public static double CountdownRingProgress(double elapsed, double duration = 0.92)
    {
        if (!double.IsFinite(elapsed) || !double.IsFinite(duration) || duration <= 0) return 0;
        var fraction = Math.Min(Math.Max(elapsed / duration, 0), 1);
        return 1 - fraction;
    }

    // ---- Scrolling capture controls and limits ---------------------------------------------------

    /// <summary>
    /// The floating Done/Cancel controls: centred at the top of the visible frame, moving to the bottom
    /// only when they would sit under the point the automatic wheel scrolls.
    /// DEVIATION: upstream is y-up (<c>maxY - height - 24</c> is the top); here y is down, so the top is
    /// <c>visible.Y + 24</c> and the alternative is <c>visible.Bottom - height - 24</c>.
    /// </summary>
    public static RectD ScrollingControlsFrame(SizeD size, RectD visibleFrame, PointD? avoiding)
    {
        var frame = new RectD(visibleFrame.MidX - size.Width / 2, visibleFrame.Y + 24, size.Width, size.Height);
        if (avoiding is { } point && frame.ContainsPoint(point))
            frame = frame with { Y = visibleFrame.Bottom - size.Height - 24 };
        return frame;
    }

    /// <summary>A failed scroll target must never keep the capture alive forever or exhaust memory.</summary>
    public const double ScrollingCaptureMaximumDuration = 120;
    public const int ScrollingCaptureMaximumFrames = 512;
    public const int ScrollingCaptureMaximumRetainedPixels = 60_000_000;
    public const int ScrollingCaptureMaximumPixels = 60_000_000;

    // ---- Recent captures -------------------------------------------------------------------------

    public const int RecentCaptureLimit = 12;
    public const long RecentCaptureMaximumBytes = 256L * 1024 * 1024;
}

/// <summary>What the after-capture routing does with a finished capture, in order (ScreenshotService.route).</summary>
public sealed record ScreenshotRoutePlan(bool CopyToClipboardFirst, bool OpensEditor, bool Saves, bool CopiesAsAction)
{
    /// <summary>
    /// The clipboard copy happens first and independently, so it also reaches the captures that open
    /// straight in the editor. The editor decides what is kept, so Edit neither saves, copies as an
    /// action nor shelves the raw capture.
    /// </summary>
    public static ScreenshotRoutePlan For(bool copyToClipboardPreference, ScreenshotDefaultAction action) => new(
        CopyToClipboardFirst: copyToClipboardPreference,
        OpensEditor: action == ScreenshotDefaultAction.Edit,
        Saves: action is ScreenshotDefaultAction.Save or ScreenshotDefaultAction.SaveAndCopy,
        CopiesAsAction: action is ScreenshotDefaultAction.Copy or ScreenshotDefaultAction.SaveAndCopy);

    /// <summary>Only a capture that does not open in the editor is offered to the shelf.</summary>
    public bool OffersShelf => !OpensEditor;
}

/// <summary>
/// The countdown shared by the capture chooser and the direct full-screen shortcut. Pressing either
/// while the other is waiting cancels the pending action. The host calls <see cref="Tick"/> once a second.
/// </summary>
public sealed class CaptureCountdown
{
    private readonly Action<int> _showRemaining;
    private Action? _pending;

    public CaptureCountdown(Action<int> showRemaining) => _showRemaining = showRemaining;

    public int Remaining { get; private set; }
    public bool IsActive => _pending is not null;

    /// <summary>With no delay the action runs at once and nothing is scheduled. Returns whether a countdown started.</summary>
    public bool Start(int delay, Action action)
    {
        if (delay <= 0) { action(); return false; }
        Remaining = delay;
        _pending = action;
        _showRemaining(Remaining);
        return true;
    }

    /// <summary>The same press while a countdown runs cancels it instead of starting another.</summary>
    public bool StartOrCancel(int delay, Action action)
    {
        if (Cancel()) return false;
        return Start(delay, action);
    }

    /// <summary>One second passed. Shows the next number, or runs the action when it reaches zero.</summary>
    public void Tick()
    {
        if (_pending is not { } action) return;
        Remaining--;
        if (Remaining > 0) { _showRemaining(Remaining); return; }
        _pending = null;
        Remaining = 0;
        action();
    }

    /// <summary>True when a countdown was running.</summary>
    public bool Cancel()
    {
        if (_pending is null) return false;
        _pending = null;
        Remaining = 0;
        return true;
    }
}

/// <summary>
/// When an after-capture preview closes itself. Time is passed in by the caller (seconds on any
/// monotonic clock); no timers. Mirrors scheduleAutoDismiss / hoverChanged of the quick preview.
/// </summary>
public sealed class QuickPreviewDismissal
{
    private double? _deadline;

    /// <summary>Null keeps the preview until dismissed.</summary>
    public double? Duration { get; set; }
    public bool PointerInside { get; private set; }
    public bool SystemSharing { get; set; }
    public bool Sharing { get; set; }
    public bool DeletingShare { get; set; }
    public bool Closed { get; private set; }
    public double? Deadline => _deadline;

    public QuickPreviewDismissal(double? duration) => Duration = duration;

    /// <summary>Starts (or restarts) the countdown unless something holds the preview open.</summary>
    public void Schedule(double now)
    {
        if (Closed || PointerInside || SystemSharing || Sharing || DeletingShare || Duration is not { } duration) return;
        _deadline = now + duration;
    }

    /// <summary>The pointer entered or left the preview: entering cancels dismissal, leaving restarts it.</summary>
    public void HoverChanged(bool inside, double now)
    {
        PointerInside = inside;
        _deadline = null;
        if (!inside) Schedule(now);
    }

    /// <summary>Advances the clock; returns true once, when the preview should close.</summary>
    public bool Advance(double now)
    {
        if (Closed || _deadline is not { } deadline || now < deadline) return false;
        Close();
        return true;
    }

    public void Close()
    {
        Closed = true;
        _deadline = null;
    }
}
