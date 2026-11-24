// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the area picker upstream's Watch borrows from ScreenshotSelectionController (geometry mode). A
// full-screen, topmost, see-through overlay on one display with upstream's hint; a drag marks the area, Esc or a
// right-click cancels. It reads nothing itself: it only hands back the rectangle, in screen pixels.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;

namespace Notchly.App.Island.Watch;

public sealed class AreaPicker
{
    /// <summary>Upstream NotchWatchStrings.purpose.</summary>
    public const string Purpose = "Choose what to watch";
    private const string Hint = "Drag across the part of a window to watch. Press Esc to cancel.";

    private readonly DisplayInfo _display;
    private readonly Window _window;
    private readonly Canvas _canvas;
    private readonly Rectangle _selection;
    private Point? _start;
    private bool _done;

    public AreaPicker(DisplayInfo display)
    {
        _display = display;
        _selection = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 16, 10), IsHitTestVisible = false,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Purpose, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                                    HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = Hint, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                                    Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        _canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), Cursor = Cursors.Cross };
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(hint);
        _window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, ShowActivated = true, Title = Purpose, Content = _canvas,
            Left = -32000, Top = -32000, Width = 10, Height = 10,
        };
        _window.SourceInitialized += (_, _) =>
        {
            // Placed in pixels, so the overlay covers the display exactly whatever its scale.
            var handle = new WindowInteropHelper(_window).Handle;
            var b = _display.Bounds;
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0x0010 /* SWP_NOACTIVATE */);
        };
        _window.Loaded += (_, _) =>
        {
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, Math.Max(0, (_window.ActualWidth - hint.DesiredSize.Width) / 2));
            Canvas.SetTop(hint, 48);
            _window.Activate();
            _window.Focus();
        };
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        _canvas.MouseRightButtonUp += (_, _) => Finish(null);
        _window.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        _window.Deactivated += (_, _) => { if (_start is null) Finish(null); };
    }

    /// <summary>The chosen area in screen pixels, or null once cancelled. Raised once.</summary>
    public event Action<Native.RECT?>? Completed;

    public void Open() => _window.Show();

    public void Cancel() => Finish(null);

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        UpdateSelection(_start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not null) UpdateSelection(e.GetPosition(_canvas));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        _canvas.ReleaseMouseCapture();
        var end = e.GetPosition(_canvas);
        _start = null;
        var dpi = VisualTreeHelper.GetDpi(_window);
        var b = _display.Bounds;
        var left = (int)Math.Round(Math.Min(start.X, end.X) * dpi.DpiScaleX) + b.Left;
        var top = (int)Math.Round(Math.Min(start.Y, end.Y) * dpi.DpiScaleY) + b.Top;
        var right = (int)Math.Round(Math.Max(start.X, end.X) * dpi.DpiScaleX) + b.Left;
        var bottom = (int)Math.Round(Math.Max(start.Y, end.Y) * dpi.DpiScaleY) + b.Top;
        // A click without a drag is no area: keep choosing.
        if (right - left < 8 || bottom - top < 8) { _selection.Visibility = Visibility.Collapsed; return; }
        Finish(new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom });
    }

    private void UpdateSelection(Point to)
    {
        if (_start is not { } from) return;
        Canvas.SetLeft(_selection, Math.Min(from.X, to.X));
        Canvas.SetTop(_selection, Math.Min(from.Y, to.Y));
        _selection.Width = Math.Abs(to.X - from.X);
        _selection.Height = Math.Abs(to.Y - from.Y);
        _selection.Visibility = Visibility.Visible;
    }

    private void Finish(Native.RECT? area)
    {
        if (_done) return;
        _done = true;
        _window.Close();
        Completed?.Invoke(area);
    }
}
