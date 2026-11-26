// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the surface upstream's ScreenCaptureService / ScreenshotSelectionController gives the colour picker and the screen text
// tool. Every display is frozen into a picture of itself, so what is picked is what was seen, and one topmost window per display shows
// it. Region mode dims the picture and cuts out a dragged rectangle; pixel mode shows a magnifier with the colour under the pointer.
// Built to be reused by screenshots (M10). It reads nothing but the screens; it hands back a rectangle and its pixels, or a colour.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Notch;
using Serilog;

namespace Notchly.App.Platform.Capture;

public enum CaptureMode { Region, Pixel }

/// <summary>A pixel of a frozen display: where it is on the screen and its colour (sRGB, as the display holds it).</summary>
public readonly record struct PixelSample(int ScreenX, int ScreenY, byte Red, byte Green, byte Blue);

/// <summary>A part of one display, in screen pixels, with its pixels.</summary>
public sealed record RegionCapture(Native.RECT Area, CapturedImage Image);

public sealed class CaptureOverlay
{
    private const int LoupeSide = 13;               // pixels sampled across the magnifier
    private const double LoupeSize = 132;           // DIPs, upstream's 132 pt frame
    private const double Cell = LoupeSize / LoupeSide;

    private readonly CaptureMode _mode;
    private readonly string _purpose, _hint;
    private readonly List<Surface> _surfaces = [];
    private bool _done;

    public CaptureOverlay(CaptureMode mode, string purpose, string hint)
    {
        _mode = mode;
        _purpose = purpose;
        _hint = hint;
    }

    /// <summary>Region mode: the chosen part, or null once cancelled. Raised once, just before <see cref="Closed"/>.</summary>
    public event Action<RegionCapture?>? RegionCompleted;

    /// <summary>Pixel mode: a colour was picked. <c>final</c> is a click (the overlay closes); false is the C key (it stays).</summary>
    public event Action<PixelSample, bool>? PixelPicked;

    /// <summary>The overlay is gone, by a pick, a cancel or <see cref="Cancel"/>. Raised once.</summary>
    public event Action? Closed;

    /// <summary>What the loupe's bar shows for a pixel (the value in the copy format); plain hex when unset.</summary>
    public Func<PixelSample, string>? Describe { get; set; }

    /// <summary>Freezes every display and shows the overlay. False when no display could be read.</summary>
    public bool Open()
    {
        foreach (var display in Displays.All())
        {
            // Before any overlay exists, so the copy shows the screen as it is (layered windows such as the island included).
            if (ScreenRegion.Read(display.Bounds, includeLayered: true) is not { } image) continue;
            _surfaces.Add(new Surface(this, display, image));
        }
        if (_surfaces.Count == 0) return false;
        Native.GetCursorPos(out var p);
        var home = _surfaces.FirstOrDefault(s => s.Contains(p.X, p.Y)) ?? _surfaces[0];
        foreach (var surface in _surfaces) surface.Show();
        home.Focus();
        return true;
    }

    public void Cancel() => Finish(null, null);

    /// <summary>The value shows as copied (a tick) in the loupe's bar.</summary>
    public void ShowCopied(string value)
    {
        foreach (var surface in _surfaces) surface.ShowCopied(value);
    }

    private void Finish(RegionCapture? region, PixelSample? pixel)
    {
        if (_done) return;
        _done = true;
        foreach (var surface in _surfaces) surface.Close();
        if (_mode == CaptureMode.Region) RegionCompleted?.Invoke(region);
        else if (pixel is { } sample) PixelPicked?.Invoke(sample, true);
        Closed?.Invoke();
    }

    private void OnSurfaceDeactivated()
    {
        // Clicking another display's surface deactivates this one a moment before activating that one.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_done && !_surfaces.Any(s => s.IsActive)) Cancel();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private string Text(PixelSample sample) =>
        Describe?.Invoke(sample) ?? $"#{sample.Red:X2}{sample.Green:X2}{sample.Blue:X2}";

    // MARK: One display

    private sealed class Surface
    {
        private readonly CaptureOverlay _owner;
        private readonly DisplayInfo _display;
        private readonly CapturedImage _image;
        private readonly Window _window;
        private readonly Canvas _canvas = new() { Cursor = Cursors.Cross, Background = Brushes.Black };
        private readonly Image _picture = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
        private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _selection = new()
        {
            Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        private readonly Border _hint = new();
        private readonly Loupe? _loupe;
        private Point? _start;
        private bool _pressed;

        public Surface(CaptureOverlay owner, DisplayInfo display, CapturedImage image)
        {
            _owner = owner;
            _display = display;
            _image = image;
            _picture.Source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
            _canvas.Children.Add(_picture);
            if (owner._mode == CaptureMode.Region)
            {
                _canvas.Children.Add(_dim);
                _canvas.Children.Add(_selection);
            }
            else
            {
                _loupe = new Loupe { Visibility = Visibility.Collapsed };
                _canvas.Children.Add(_loupe);
            }
            _hint.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0));
            _hint.CornerRadius = new CornerRadius(12);
            _hint.Padding = new Thickness(16, 10, 16, 10);
            _hint.IsHitTestVisible = false;
            _hint.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = owner._purpose, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = owner._hint, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                                    Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                },
            };
            _canvas.Children.Add(_hint);

            _window = new Window
            {
                WindowStyle = WindowStyle.None, Background = Brushes.Black, Topmost = true, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
                ShowActivated = true, Title = owner._purpose, Content = _canvas, Left = -32000, Top = -32000, Width = 10, Height = 10,
            };
            _window.SourceInitialized += (_, _) =>
            {
                // Placed in pixels, so the overlay covers its display exactly whatever its scale.
                var b = display.Bounds;
                Native.SetWindowPos(new WindowInteropHelper(_window).Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0x0010 /* SWP_NOACTIVATE */);
            };
            _canvas.SizeChanged += (_, _) => Layout();
            _canvas.MouseLeftButtonDown += (_, e) => { _pressed = true; OnDown(e); };
            _canvas.MouseMove += (_, e) => OnMove(e);
            _canvas.MouseLeftButtonUp += (_, e) => { if (_pressed) OnUp(e); _pressed = false; };
            _canvas.MouseRightButtonUp += (_, _) => owner.Cancel();
            _canvas.MouseLeave += (_, _) => { if (_loupe is not null && _start is null) _loupe.Visibility = Visibility.Collapsed; };
            _window.PreviewKeyDown += OnKey;
            _window.Deactivated += (_, _) => { if (_start is null) owner.OnSurfaceDeactivated(); };
        }

        public bool IsActive => _window.IsActive;

        public bool Contains(int x, int y) =>
            x >= _display.Bounds.Left && x < _display.Bounds.Right && y >= _display.Bounds.Top && y < _display.Bounds.Bottom;

        public void Show() => _window.Show();

        public void Focus()
        {
            _window.Activate();
            _window.Focus();
        }

        public void Close() => _window.Close();

        public void ShowCopied(string value) => _loupe?.SetBar(null, "✓ " + value, "");

        private void Layout()
        {
            var width = _canvas.ActualWidth;
            var height = _canvas.ActualHeight;
            _picture.Width = width;
            _picture.Height = height;
            _dim.Data = new RectangleGeometry(new Rect(0, 0, width, height));
            Canvas.SetLeft(_hint, Math.Max(0, (width - _hint.ActualWidth) / 2));
            Canvas.SetTop(_hint, 48);
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_hint, Math.Max(0, (width - _hint.DesiredSize.Width) / 2));
            if (!_window.IsActive && Contains(CursorX(), CursorY())) Focus();
        }

        private static int CursorX() => Native.GetCursorPos(out var p) ? p.X : 0;
        private static int CursorY() => Native.GetCursorPos(out var p) ? p.Y : 0;

        private double Scale => VisualTreeHelper.GetDpi(_window).DpiScaleX;

        /// <summary>The pixel of this display under a point of the canvas, clamped inside it.</summary>
        private (int X, int Y) PixelAt(Point point)
        {
            var dpi = VisualTreeHelper.GetDpi(_window);
            return (Math.Clamp((int)Math.Floor(point.X * dpi.DpiScaleX), 0, _image.Width - 1),
                    Math.Clamp((int)Math.Floor(point.Y * dpi.DpiScaleY), 0, _image.Height - 1));
        }

        private PixelSample Sample(int x, int y)
        {
            var at = _image.Offset + y * _image.Stride + x * 4;
            return new PixelSample(_display.Bounds.Left + x, _display.Bounds.Top + y, _image.Pixels[at + 2], _image.Pixels[at + 1], _image.Pixels[at]);
        }

        // MARK: Pointer

        private void OnDown(MouseButtonEventArgs e)
        {
            if (_owner._mode != CaptureMode.Region) return;
            _start = e.GetPosition(_canvas);
            _canvas.CaptureMouse();
            UpdateSelection(_start.Value);
        }

        private void OnMove(MouseEventArgs e)
        {
            var point = e.GetPosition(_canvas);
            if (_owner._mode == CaptureMode.Region)
            {
                if (_start is not null) UpdateSelection(point);
            }
            else UpdateLoupe(point);
        }

        private void OnUp(MouseButtonEventArgs e)
        {
            var end = e.GetPosition(_canvas);
            if (_owner._mode == CaptureMode.Pixel)
            {
                var (x, y) = PixelAt(end);
                _owner.Finish(null, Sample(x, y));
                return;
            }
            if (_start is not { } start) return;
            _canvas.ReleaseMouseCapture();
            _start = null;
            var dpi = VisualTreeHelper.GetDpi(_window);
            var left = (int)Math.Round(Math.Min(start.X, end.X) * dpi.DpiScaleX);
            var top = (int)Math.Round(Math.Min(start.Y, end.Y) * dpi.DpiScaleY);
            var right = (int)Math.Round(Math.Max(start.X, end.X) * dpi.DpiScaleX);
            var bottom = (int)Math.Round(Math.Max(start.Y, end.Y) * dpi.DpiScaleY);
            // A click without a drag is no area: keep choosing.
            if (right - left < 8 || bottom - top < 8) { _selection.Visibility = Visibility.Collapsed; Layout(); return; }
            left = Math.Clamp(left, 0, _image.Width - 1);
            top = Math.Clamp(top, 0, _image.Height - 1);
            right = Math.Clamp(right, left + 1, _image.Width);
            bottom = Math.Clamp(bottom, top + 1, _image.Height);
            var b = _display.Bounds;
            var area = new Native.RECT { Left = b.Left + left, Top = b.Top + top, Right = b.Left + right, Bottom = b.Top + bottom };
            _owner.Finish(new RegionCapture(area, _image.Crop(new RectD(left, top, right - left, bottom - top))), null);
        }

        private void UpdateSelection(Point to)
        {
            if (_start is not { } from) return;
            var rect = new Rect(from, to);
            Canvas.SetLeft(_selection, rect.X);
            Canvas.SetTop(_selection, rect.Y);
            _selection.Width = rect.Width;
            _selection.Height = rect.Height;
            _selection.Visibility = Visibility.Visible;
            // The picture stays bright inside the rectangle: the dim is the screen minus the selection.
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight)), new RectangleGeometry(rect));
        }

        // MARK: Loupe

        private Point? _last;

        private void UpdateLoupe(Point point)
        {
            if (_loupe is null) return;
            _last = point;
            var (px, py) = PixelAt(point);
            var sample = Sample(px, py);
            _loupe.Fill(_image, px, py, LoupeSide);
            _loupe.SetBar(Color.FromRgb(sample.Red, sample.Green, sample.Blue), _owner.Text(sample), $"{sample.ScreenX}, {sample.ScreenY}");
            _loupe.Visibility = Visibility.Visible;
            _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _loupe.DesiredSize;
            var x = point.X + 24;
            var y = point.Y + 24;
            if (x + size.Width > _canvas.ActualWidth - 8) x = point.X - 24 - size.Width;
            if (y + size.Height > _canvas.ActualHeight - 8) y = point.Y - 24 - size.Height;
            Canvas.SetLeft(_loupe, Math.Max(0, x));
            Canvas.SetTop(_loupe, Math.Max(0, y));
        }

        // MARK: Keys

        private void OnKey(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { _owner.Cancel(); e.Handled = true; return; }
            if (_owner._mode != CaptureMode.Pixel) return;
            if (key == Key.C && Keyboard.Modifiers == ModifierKeys.None)
            {
                Native.GetCursorPos(out var p);
                var (x, y) = (Math.Clamp(p.X - _display.Bounds.Left, 0, _image.Width - 1), Math.Clamp(p.Y - _display.Bounds.Top, 0, _image.Height - 1));
                var sample = Sample(x, y);
                _owner.PixelPicked?.Invoke(sample, false);
                e.Handled = true;
                return;
            }
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var (dx, dy) = key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0) };
            if (dx == 0 && dy == 0) return;
            // One device pixel at a time, so a pixel between two others can be reached.
            Native.GetCursorPos(out var at);
            var b = _display.Bounds;
            var nx = Math.Clamp(at.X + dx, b.Left, b.Right - 1);
            var ny = Math.Clamp(at.Y + dy, b.Top, b.Bottom - 1);
            Native.SetCursorPos(nx, ny);
            var scale = Scale;
            UpdateLoupe(new Point((nx - b.Left + 0.5) / scale, (ny - b.Top + 0.5) / scale));
            e.Handled = true;
        }
    }

    // MARK: The magnifier

    /// <summary>A 13 by 13 pixel patch magnified, with a grid, a ring on the middle pixel, and a bar with the colour, its value and place.</summary>
    private sealed class Loupe : Border
    {
        private readonly WriteableBitmap _patch = new(LoupeSide, LoupeSide, 96, 96, PixelFormats.Bgra32, null);
        private readonly Border _swatch = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), BorderThickness = new Thickness(0.5) };
        private readonly TextBlock _value = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _place = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };

        public Loupe()
        {
            var picture = new Image { Source = _patch, Width = LoupeSize, Height = LoupeSize, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
            var grid = new Canvas { Width = LoupeSize, Height = LoupeSize, IsHitTestVisible = false };
            var line = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            for (var i = 1; i < LoupeSide; i++)
            {
                grid.Children.Add(new Rectangle { Width = LoupeSize, Height = 0.5, Fill = line, Margin = new Thickness(0, i * Cell, 0, 0) });
                grid.Children.Add(new Rectangle { Width = 0.5, Height = LoupeSize, Fill = line, Margin = new Thickness(i * Cell, 0, 0, 0) });
            }
            var middle = LoupeSide / 2 * Cell;
            grid.Children.Add(new Rectangle { Width = Cell, Height = Cell, Stroke = Brushes.White, StrokeThickness = 2, Margin = new Thickness(middle, middle, 0, 0) });
            grid.Children.Add(new Rectangle { Width = Cell - 2, Height = Cell - 2, Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(middle + 1, middle + 1, 0, 0) });
            var frame = new Grid { Width = LoupeSize, Height = LoupeSize, Children = { picture, grid } };
            var bar = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.RowDefinitions.Add(new RowDefinition());
            bar.RowDefinitions.Add(new RowDefinition());
            _swatch.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRowSpan(_swatch, 2);
            _value.Margin = new Thickness(8, 0, 0, 0);
            _place.Margin = new Thickness(8, 1, 0, 0);
            Grid.SetColumn(_value, 1);
            Grid.SetColumn(_place, 1);
            Grid.SetRow(_place, 1);
            bar.Children.Add(_swatch);
            bar.Children.Add(_value);
            bar.Children.Add(_place);
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1E));
            CornerRadius = new CornerRadius(10);
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
            BorderThickness = new Thickness(1);
            IsHitTestVisible = false;
            Width = LoupeSize + 2;
            frame.Clip = new RectangleGeometry(new Rect(0, 0, LoupeSize, LoupeSize), 9, 9);
            Child = new StackPanel { Children = { frame, bar } };
        }

        /// <summary>The 13 by 13 pixels around (x, y); what lies off the display is shown black.</summary>
        public void Fill(CapturedImage image, int x, int y, int side)
        {
            var half = side / 2;
            var pixels = new byte[side * side * 4];
            for (var row = 0; row < side; row++)
                for (var col = 0; col < side; col++)
                {
                    int sx = x - half + col, sy = y - half + row;
                    var to = (row * side + col) * 4;
                    if (sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height) { pixels[to + 3] = 255; continue; }
                    Buffer.BlockCopy(image.Pixels, image.Offset + sy * image.Stride + sx * 4, pixels, to, 4);
                }
            _patch.WritePixels(new Int32Rect(0, 0, side, side), pixels, side * 4, 0);
        }

        public void SetBar(Color? color, string value, string place)
        {
            if (color is { } c) _swatch.Background = new SolidColorBrush(c);
            _value.Text = value;
            _place.Text = place;
        }
    }
}
