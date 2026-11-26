// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotScrollingCapture / ScreenshotAutomaticScroller host side: the floating Done and Cancel controls, a frame
// around the area, reading the area over and over, and (for automatic scrolling) posting wheel messages to the window under the area.
// What counts as a match, when to stop and what to keep is Core's ScrollingCaptureSession; this class owns the clock, the pixels and the keys.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Serilog;
using Button = System.Windows.Controls.Button;

namespace Notchly.App.Screenshots;

internal sealed class ScrollingCaptureController
{
    private const uint WmMouseWheel = 0x020A;
    private readonly Native.RECT _area;
    private readonly bool _auto;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cancel = new();
    private ControlsWindow? _controls;
    private GuideWindow? _guide;
    private volatile bool _finish;

    public ScrollingCaptureController(Native.RECT area, bool autoScroll)
    {
        _area = area;
        _auto = autoScroll;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Done was pressed (or the shortcut again): the capture ends with what it has.</summary>
    public void Finish() => _finish = true;

    public void Cancel() => _cancel.Cancel();

    /// <summary>Runs until the area is captured, stopped or cancelled. Returns null when cancelled or the area could not be read.</summary>
    public async Task<(CapturedImage Image, ScrollingResultKind Kind)?> RunAsync()
    {
        IsRunning = true;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await dispatcher.InvokeAsync(ShowChrome);
            // The keys' "pressed since last asked" bit may be stale from before the capture began.
            _ = EscapePressed();
            _ = EnterPressed();
            var session = new ScrollingCaptureSession(_auto);
            var first = ReadFrame();
            if (first is null || session.Begin(first, Now()) is not null) return null;
            var wheelTarget = new Native.POINT { X = (_area.Left + _area.Right) / 2, Y = (_area.Top + _area.Bottom) / 2 };
            ScrollingResult? result = null;
            while (result is null)
            {
                if (_cancel.IsCancellationRequested) return null;
                if (EscapePressed()) _cancel.Cancel();
                if (EnterPressed()) _finish = true;
                result = session.PreCheck(Now(), _finish);
                if (result is not null) break;
                if (!session.ReadyForFrame(Now())) { await Task.Delay(TimeSpan.FromSeconds(session.DelayUntilNextFrame(Now())), CancellationToken.None); continue; }
                var step = session.Ingest(ReadFrame(), Now(), targetCurrent: true, _finish, _cancel.Token);
                if (step.ProgressHeight is { } height) dispatcher.Invoke(() => _controls?.Progress(height));
                if (step.Result is not null) { result = step.Result; break; }
                if (step.Action == AutoScrollAction.Scroll)
                {
                    var delivered = PostWheel(wheelTarget, ScrollingCaptureSession.AutomaticScrollStep(_area.Height));
                    if (session.AfterScroll(delivered, _cancel.IsCancellationRequested || _finish) is { } ended) { result = ended; break; }
                }
                await Task.Delay(30, CancellationToken.None);
            }
            if (result.Kind == ScrollingResultKind.Cancelled || result.Image is null) return null;
            var image = result.Image;
            return (new CapturedImage(image.Pixels, image.Width, image.Height, image.Stride), result.Kind);
        }
        catch (Exception e)
        {
            Log.Warning(e, "The scrolling capture failed");
            return null;
        }
        finally
        {
            IsRunning = false;
            await dispatcher.InvokeAsync(() => { _controls?.Close(); _guide?.Close(); });
        }
    }

    private double Now() => _clock.Elapsed.TotalSeconds;

    private BgraFrame? ReadFrame()
    {
        var image = ScreenRegion.Read(_area, includeLayered: true);
        return image is null ? null : new BgraFrame(image.Width, image.Height, image.Stride, image.Pixels);
    }

    private static bool EscapePressed() => (Native.GetAsyncKeyState(0x1B) & 0x0001) != 0;
    private static bool EnterPressed() => (Native.GetAsyncKeyState(0x0D) & 0x0001) != 0;

    /// <summary>A wheel tick to the window under the area, without moving the pointer (the notch of 120 units is one step down).</summary>
    private static bool PostWheel(Native.POINT screenPoint, int step)
    {
        var window = Native.WindowFromPoint(screenPoint);
        if (window == IntPtr.Zero) return false;
        var delta = -Math.Max(120, step / 120 * 120);
        var wParam = new IntPtr(((delta & 0xFFFF) << 16));
        var lParam = new IntPtr(((screenPoint.Y & 0xFFFF) << 16) | (screenPoint.X & 0xFFFF));
        return Native.PostMessage(window, WmMouseWheel, wParam, lParam);
    }

    private void ShowChrome()
    {
        _guide = new GuideWindow(_area);
        _guide.Show();
        _controls = new ControlsWindow(_auto, Finish, Cancel);
        _controls.Show();
        _controls.Place(_area);
    }

    // MARK: Windows

    /// <summary>The Done and Cancel card. It never takes the focus (the page being captured must keep it) and is never part of the picture.</summary>
    private sealed class ControlsWindow : Window
    {
        private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };

        public ControlsWindow(bool auto, Action done, Action cancel)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -32000;
            Top = -32000;
            _text.Text = auto ? "Scrolling for you…" : "Scroll the area, then press Done";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _text } };
            var doneButton = new Button { Content = "Done", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 0) };
            doneButton.Click += (_, _) => done();
            var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5) };
            cancelButton.Click += (_, _) => cancel();
            row.Children.Add(doneButton);
            row.Children.Add(cancelButton);
            Content = new Border { Margin = new Thickness(8), Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)), Child = row };
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
                Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);
            };
        }

        public void Progress(int height) => _text.Text = $"{height:N0} px captured — Enter or Done to finish, Esc to cancel";

        public void Place(Native.RECT area)
        {
            UpdateLayout();
            var display = Displays.AtPoint((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2) ?? Displays.All().First();
            var dpi = VisualTreeHelper.GetDpi(this);
            var size = new SizeD(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
            var work = display.WorkArea;
            var frame = ScreenshotSupport.ScrollingControlsFrame(size, new RectD(work.Left, work.Top, work.Width, work.Height),
                new PointD((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2));
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, (int)frame.X, (int)frame.Y, 0, 0, 0x0001 | 0x0010);
        }
    }

    /// <summary>A thin frame round the area; click-through, so the pointer reaches what is being scrolled, and left out of every capture.</summary>
    private sealed class GuideWindow : Window
    {
        public GuideWindow(Native.RECT area)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Left = -32000;
            Top = -32000;
            Width = 100;
            Height = 100;
            Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x8C, 0xFF)), BorderThickness = new Thickness(2), IsHitTestVisible = false };
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
                Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);
                Native.SetWindowPos(handle, Native.HWND_TOPMOST, area.Left - 3, area.Top - 3, area.Width + 6, area.Height + 6, 0x0010);
            };
        }
    }
}
