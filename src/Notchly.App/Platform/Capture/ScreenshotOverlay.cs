// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the selection surface of upstream's ScreenshotSelectionController + ScreenCaptureService chooser. One topmost window
// per display; the screen is frozen into each (or left live when "freeze" is off or a delay is set). Drag an area, click a window,
// R repeats the last area, L shows the loupe, C copies the colour under the pointer, 1 to 5 switch tool, arrows nudge the pointer,
// Space moves the selection while dragging. The geometry (drag session, window pick, nudge, loupe zoom) is Core's.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Cursors = System.Windows.Input.Cursors;
using Path = System.Windows.Shapes.Path;

namespace Notchly.App.Platform.Capture;

/// <summary>What the overlay returned: the area in screen pixels, the frozen pixels when it was frozen, and the window if one was clicked.</summary>
public sealed record ScreenshotPick(Native.RECT Area, CapturedImage? Image, CapturableWindow? Window, string DisplayId, RectD ViewRect, ScreenCaptureTool Tool);

public sealed class ScreenshotOverlayOptions
{
    public IReadOnlyList<ScreenCaptureTool> Tools { get; init; } = [ScreenCaptureTool.Screenshot];
    public ScreenCaptureTool Tool { get; set; } = ScreenCaptureTool.Screenshot;
    public bool Frozen { get; init; } = true;
    public bool HighlightWindows { get; init; } = true;
    public bool ShowLastRegion { get; init; } = true;
    public bool LoupeStartsOn { get; init; }
    public bool RememberZoom { get; init; }
    public double DefaultZoom { get; init; } = 1;
    public double LastZoom { get; init; } = 1;
    public bool SteppedZoom { get; init; }
    public bool IncludeOwnWindows { get; init; }
    public LastRegionStore Regions { get; init; } = new();
}

public sealed class ScreenshotOverlay
{
    private readonly ScreenshotOverlayOptions _options;
    private readonly List<Surface> _surfaces = [];
    private readonly LoupeZoomState _zoom;
    private IReadOnlyList<CapturableWindow> _windows = [];
    private bool _done, _loupeOn;

    public ScreenshotOverlay(ScreenshotOverlayOptions options)
    {
        _options = options;
        _zoom = new LoupeZoomState(options.RememberZoom, options.DefaultZoom, options.LastZoom);
        _loupeOn = options.LoupeStartsOn && options.Frozen;
    }

    /// <summary>A region or window was chosen. Raised once, before <see cref="Closed"/>.</summary>
    public event Action<ScreenshotPick>? Picked;
    /// <summary>Text or colour was chosen in the strip: that tool takes over.</summary>
    public event Action<ScreenCaptureTool>? ToolChosen;
    public event Action<double>? ZoomChanged;
    public event Action? Closed;

    public bool Open()
    {
        if (_options.HighlightWindows || _options.Tool == ScreenCaptureTool.Screenshot) _windows = CapturableWindows.List(_options.IncludeOwnWindows);
        foreach (var display in Displays.All())
        {
            CapturedImage? image = null;
            if (_options.Frozen)
            {
                // Before any overlay exists, so the copy is the screen as it is (layered windows included).
                image = ScreenRegion.Read(display.Bounds, includeLayered: true);
                if (image is null) continue;
            }
            _surfaces.Add(new Surface(this, display, image));
        }
        if (_surfaces.Count == 0) return false;
        Native.GetCursorPos(out var p);
        var home = _surfaces.FirstOrDefault(s => s.Contains(p.X, p.Y)) ?? _surfaces[0];
        home.ShowChooser = true;
        foreach (var surface in _surfaces) { surface.RefreshChrome(); surface.Show(); }
        home.Focus();
        return true;
    }

    public void Cancel() => Finish(null, null);

    private void SelectTool(ScreenCaptureTool tool)
    {
        if (!_options.Tools.Contains(tool) || tool == _options.Tool) return;
        if (tool is ScreenCaptureTool.Screenshot or ScreenCaptureTool.Scrolling)
        {
            _options.Tool = tool;
            foreach (var surface in _surfaces) surface.RefreshChrome();
            return;
        }
        Finish(null, tool);
    }

    private void Finish(ScreenshotPick? pick, ScreenCaptureTool? chosen)
    {
        if (_done) return;
        _done = true;
        foreach (var surface in _surfaces) surface.Close();
        if (pick is not null) Picked?.Invoke(pick);
        else if (chosen is { } tool) ToolChosen?.Invoke(tool);
        Closed?.Invoke();
    }

    private void OnSurfaceDeactivated()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_done && !_surfaces.Any(s => s.IsActive)) Cancel();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private string Hint()
    {
        var repeat = _options.Regions.RepeatTarget(_surfaces.Select(s => s.DisplayId).ToList(), false) is not null ? "  R repeats the last area." : "";
        return _options.Tool == ScreenCaptureTool.Scrolling
            ? "Drag the area to scroll through. Esc cancels." + repeat
            : "Drag an area, or click a window. L loupe, C copies the colour, Esc cancels." + repeat;
    }

    private static string Label(ScreenCaptureTool tool) => tool switch
    {
        ScreenCaptureTool.Screenshot => "Screenshot", ScreenCaptureTool.Recording => "Record", ScreenCaptureTool.Text => "Copy text",
        ScreenCaptureTool.Color => "Color", _ => "Scrolling",
    };

    // MARK: One display

    private sealed class Surface
    {
        private readonly ScreenshotOverlay _owner;
        private readonly DisplayInfo _display;
        private readonly CapturedImage? _image;
        private readonly Window _window;
        private readonly Canvas _canvas = new() { Cursor = Cursors.Cross };
        private readonly Image _picture = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
        private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _selection = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly Rectangle _ghost = new() { Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)), StrokeThickness = 1, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly Rectangle _hover = new()
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0x8C, 0xFF)), StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0x8C, 0xFF)),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        private readonly Border _hint = new() { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 8, 16, 8), IsHitTestVisible = false };
        private readonly TextBlock _hintText = new() { FontSize = 12, Foreground = Brushes.White };
        private readonly Border _chooser = new() { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Visibility = Visibility.Collapsed };
        private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
        private readonly ShotLoupe _loupe = new() { Visibility = Visibility.Collapsed };
        private readonly SelectionDragSession _session = new();
        private IReadOnlyList<ScreenshotSelection.PickableWindow> _pickable = [];
        private IReadOnlyList<CapturableWindow> _pickableSource = [];

        public Surface(ScreenshotOverlay owner, DisplayInfo display, CapturedImage? image)
        {
            _owner = owner;
            _display = display;
            _image = image;
            if (image is not null)
            {
                _picture.Source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
                _canvas.Background = Brushes.Black;
                _canvas.Children.Add(_picture);
            }
            else _canvas.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_hover);
            _canvas.Children.Add(_ghost);
            _canvas.Children.Add(_selection);
            _hint.Child = _hintText;
            _canvas.Children.Add(_hint);
            _chooser.Child = _tabs;
            _canvas.Children.Add(_chooser);
            _canvas.Children.Add(_loupe);

            _window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = image is null, Background = image is null ? Brushes.Transparent : Brushes.Black,
                Topmost = true, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, ShowActivated = true, Title = "Screenshot",
                Content = _canvas, Left = -32000, Top = -32000, Width = 10, Height = 10,
            };
            _window.SourceInitialized += (_, _) =>
            {
                var b = display.Bounds;
                Native.SetWindowPos(new WindowInteropHelper(_window).Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0x0010);
                // The overlay is never part of a capture (the live case reads the screen after it is gone, but a recorder may not).
                Native.SetWindowDisplayAffinity(new WindowInteropHelper(_window).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            };
            _canvas.SizeChanged += (_, _) => Layout();
            _canvas.MouseLeftButtonDown += OnDown;
            _canvas.MouseMove += OnMove;
            _canvas.MouseLeftButtonUp += OnUp;
            _canvas.MouseWheel += OnWheel;
            _canvas.MouseRightButtonUp += (_, _) => owner.Cancel();
            _canvas.MouseLeave += (_, _) => { _loupe.Visibility = Visibility.Collapsed; _hover.Visibility = Visibility.Collapsed; };
            _window.PreviewKeyDown += OnKey;
            _window.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) _session.SpaceIsDown = false; };
            _window.Deactivated += (_, _) => { if (!_session.IsDragging) owner.OnSurfaceDeactivated(); };
        }

        public string DisplayId => _display.DeviceName;
        public bool IsActive => _window.IsActive;
        public bool ShowChooser { get; set; }
        public bool Contains(int x, int y) => x >= _display.Bounds.Left && x < _display.Bounds.Right && y >= _display.Bounds.Top && y < _display.Bounds.Bottom;
        public void Show() => _window.Show();
        public void Close() => _window.Close();
        public void Focus() { _window.Activate(); _window.Focus(); }

        private double Scale => _display.Bounds.Width / Math.Max(1, _canvas.ActualWidth);
        private RectD ViewBounds => new(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
        private CaptureDisplay AsCapture => new(DisplayId, ViewBounds, Scale);

        public void RefreshChrome()
        {
            _hintText.Text = _owner.Hint();
            _tabs.Children.Clear();
            foreach (var tool in _owner._options.Tools)
            {
                var active = tool == _owner._options.Tool;
                var tab = new Border
                {
                    Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand,
                    Background = active ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent,
                    Child = new TextBlock { Text = $"{tool.ShortcutKey()}  {Label(tool)}", FontSize = 12, Foreground = Brushes.White },
                };
                var chosen = tool;
                tab.MouseLeftButtonDown += (_, e) => { e.Handled = true; _owner.SelectTool(chosen); };
                _tabs.Children.Add(tab);
            }
            _chooser.Visibility = ShowChooser && _owner._options.Tools.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Layout();
        }

        private void Layout()
        {
            var width = _canvas.ActualWidth;
            var height = _canvas.ActualHeight;
            if (width <= 0) return;
            _picture.Width = width;
            _picture.Height = height;
            _dim.Data = new RectangleGeometry(new Rect(0, 0, width, height));
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _chooser.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_chooser, Math.Max(0, (width - _chooser.DesiredSize.Width) / 2));
            Canvas.SetTop(_chooser, 24);
            Canvas.SetLeft(_hint, Math.Max(0, (width - _hint.DesiredSize.Width) / 2));
            Canvas.SetTop(_hint, ShowChooser ? 24 + _chooser.DesiredSize.Height + 12 : 40);
            _pickable = ViewWindows();
            var ghost = _owner._options.Regions.GhostFor(DisplayId, _owner._options.ShowLastRegion);
            if (ghost is { } g)
            {
                Canvas.SetLeft(_ghost, g.X); Canvas.SetTop(_ghost, g.Y); _ghost.Width = g.Width; _ghost.Height = g.Height;
                _ghost.Visibility = Visibility.Visible;
            }
            if (!_window.IsActive && Contains(CursorX(), CursorY())) Focus();
        }

        private static int CursorX() => Native.GetCursorPos(out var p) ? p.X : 0;
        private static int CursorY() => Native.GetCursorPos(out var p) ? p.Y : 0;

        /// <summary>The pickable windows in this display's own view coordinates (DIPs), front to back.</summary>
        private IReadOnlyList<ScreenshotSelection.PickableWindow> ViewWindows()
        {
            if (!_owner._options.HighlightWindows && _owner._options.Tool != ScreenCaptureTool.Screenshot) return [];
            var entries = _owner._windows.Select((w, i) => new ScreenshotSelection.WindowListEntry(
                (uint)i, w.ProcessId, new RectD(w.Bounds.Left, w.Bounds.Top, w.Bounds.Width, w.Bounds.Height), IsUntitled: w.Title.Length == 0)).ToList();
            var allowed = ScreenshotSelection.PickableWindows(entries, Environment.ProcessId, true, []);
            _pickableSource = _owner._windows;
            var scale = Scale;
            return allowed.Select(e => new ScreenshotSelection.PickableWindow(e.Id, new RectD(
                (e.Frame.X - _display.Bounds.Left) / scale, (e.Frame.Y - _display.Bounds.Top) / scale, e.Frame.Width / scale, e.Frame.Height / scale)))
                .Where(w => w.Frame.CgIntersects(ViewBounds)).ToList();
        }

        // MARK: Pointer

        private static PointD ToPoint(Point p) => new(p.X, p.Y);

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _hover.Visibility = Visibility.Collapsed;
            _session.Press(ToPoint(e.GetPosition(_canvas)));
            _canvas.CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            var point = ToPoint(e.GetPosition(_canvas));
            if (_session.IsDragging)
            {
                var modifiers = Keyboard.Modifiers;
                _session.Drag(point, modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Alt), false, ViewBounds);
                ShowSelection(_session.Selection);
                return;
            }
            UpdateHover(point);
            UpdateLoupe(e.GetPosition(_canvas));
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_session.IsDragging) return;
            _canvas.ReleaseMouseCapture();
            var windowsAllowed = _owner._options.Tool == ScreenCaptureTool.Screenshot;
            var release = _session.Release(ToPoint(e.GetPosition(_canvas)), false, windowsAllowed, _pickable);
            _selection.Visibility = Visibility.Collapsed;
            _dim.Data = new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
            switch (release.Kind)
            {
                case SelectionReleaseKind.Region: PickRegion(release.Region); break;
                case SelectionReleaseKind.Window when release.Window is { } w: PickWindow(w); break;
            }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_owner._loupeOn) return;
            var stepped = ScreenshotLoupe.UsesSteppedZoom(_owner._options.SteppedZoom, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            _owner._zoom.Adjust(e.Delta / 120.0, stepped, false);
            _owner.ZoomChanged?.Invoke(_owner._zoom.Zoom);
            UpdateLoupe(e.GetPosition(_canvas));
            e.Handled = true;
        }

        private void ShowSelection(RectD rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) { _selection.Visibility = Visibility.Collapsed; return; }
            Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
            _selection.Width = rect.Width; _selection.Height = rect.Height;
            _selection.Visibility = Visibility.Visible;
            _ghost.Visibility = Visibility.Collapsed;
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight)),
                new RectangleGeometry(new Rect(rect.X, rect.Y, rect.Width, rect.Height)));
            _loupe.Visibility = Visibility.Collapsed;
        }

        private void UpdateHover(PointD point)
        {
            if (!_owner._options.HighlightWindows || _owner._options.Tool != ScreenCaptureTool.Screenshot) { _hover.Visibility = Visibility.Collapsed; return; }
            if (ScreenshotSelection.WindowAt(point, _pickable) is not { } window) { _hover.Visibility = Visibility.Collapsed; return; }
            var f = window.Frame;
            Canvas.SetLeft(_hover, f.X); Canvas.SetTop(_hover, f.Y); _hover.Width = f.Width; _hover.Height = f.Height;
            _hover.Visibility = Visibility.Visible;
        }

        // MARK: Results

        private void PickRegion(RectD view)
        {
            var pixels = ScreenshotCoordinates.SelectionPixelRect(view, AsCapture);
            if (pixels.Width < 1 || pixels.Height < 1) return;
            var b = _display.Bounds;
            var area = new Native.RECT
            {
                Left = b.Left + (int)pixels.X, Top = b.Top + (int)pixels.Y,
                Right = b.Left + (int)Math.Ceiling(pixels.Right), Bottom = b.Top + (int)Math.Ceiling(pixels.Bottom),
            };
            var image = _image?.Crop(new RectD((int)pixels.X, (int)pixels.Y, area.Right - area.Left, area.Bottom - area.Top));
            _owner._options.Regions.Remember(DisplayId, view);
            _owner.Finish(new ScreenshotPick(area, image, null, DisplayId, view, _owner._options.Tool), null);
        }

        private void PickWindow(ScreenshotSelection.PickableWindow target)
        {
            var window = _owner._windows.ElementAtOrDefault((int)target.WindowId);
            if (window is null) return;
            var area = window.Bounds;
            CapturedImage? image = null;
            if (_image is not null)
            {
                var b = _display.Bounds;
                var x = Math.Max(0, area.Left - b.Left);
                var y = Math.Max(0, area.Top - b.Top);
                var w = Math.Min(_image.Width - x, area.Right - b.Left - x);
                var h = Math.Min(_image.Height - y, area.Bottom - b.Top - y);
                if (w > 0 && h > 0) image = _image.Crop(new RectD(x, y, w, h));
            }
            _owner.Finish(new ScreenshotPick(area, image, window, DisplayId, target.Frame, ScreenCaptureTool.Screenshot), null);
        }

        // MARK: Loupe

        private void UpdateLoupe(Point point)
        {
            if (!_owner._loupeOn || _image is null) { _loupe.Visibility = Visibility.Collapsed; return; }
            var scale = Scale;
            var px = Math.Clamp((int)(point.X * scale), 0, _image.Width - 1);
            var py = Math.Clamp((int)(point.Y * scale), 0, _image.Height - 1);
            var side = Math.Max(3, (int)Math.Round(_owner._zoom.SampleSide) | 1);
            _loupe.Fill(_image, px, py, side, _owner._zoom.GridVisible);
            var at = _image.Offset + py * _image.Stride + px * 4;
            _loupe.SetBar($"#{_image.Pixels[at + 2]:X2}{_image.Pixels[at + 1]:X2}{_image.Pixels[at]:X2}", $"{_display.Bounds.Left + px}, {_display.Bounds.Top + py}");
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
            var modifiers = Keyboard.Modifiers;
            switch (key)
            {
                case Key.Escape: _owner.Cancel(); e.Handled = true; return;
                case Key.Space: _session.SpaceIsDown = true; e.Handled = true; return;
                case Key.L: _owner._loupeOn = _image is not null && !_owner._loupeOn; if (Native.GetCursorPos(out var lp)) UpdateLoupe(PointFromScreen(lp)); e.Handled = true; return;
                case Key.R: RepeatLast(); e.Handled = true; return;
                case Key.C when modifiers == ModifierKeys.None: CopyColor(); e.Handled = true; return;
                case Key.Left or Key.Right or Key.Up or Key.Down: Nudge(key, modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; return;
            }
            var digit = key is >= Key.D1 and <= Key.D5 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad5 ? key - Key.NumPad0 : 0;
            if (digit > 0 && ScreenCaptureTools.MatchingShortcut(digit.ToString()) is { } tool) { _owner.SelectTool(tool); e.Handled = true; }
        }

        private Point PointFromScreen(Native.POINT p) => new((p.X - _display.Bounds.Left) / Scale, (p.Y - _display.Bounds.Top) / Scale);

        private void RepeatLast()
        {
            var ids = _owner._surfaces.Select(s => s.DisplayId).ToList();
            if (_owner._options.Regions.RepeatTarget(ids, false) is not { } last) return;
            _owner._surfaces.First(s => s.DisplayId == last.DisplayId).PickRegion(last.ViewRect);
        }

        private void CopyColor()
        {
            if (_image is null || !Native.GetCursorPos(out var p)) return;
            var x = Math.Clamp(p.X - _display.Bounds.Left, 0, _image.Width - 1);
            var y = Math.Clamp(p.Y - _display.Bounds.Top, 0, _image.Height - 1);
            var at = _image.Offset + y * _image.Stride + x * 4;
            var hex = $"#{_image.Pixels[at + 2]:X2}{_image.Pixels[at + 1]:X2}{_image.Pixels[at]:X2}";
            if (Notchly.App.Tools.ClipboardWrite.TrySetText(hex)) _loupe.SetBar("✓ " + hex, "copied");
        }

        private void Nudge(Key key, bool fast)
        {
            if (!Native.GetCursorPos(out var p)) return;
            var displays = Displays.All().Select(d => new NudgeDisplay(new RectD(d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height), 1)).ToList();
            var nudge = key switch { Key.Left => NudgeKey.Left, Key.Right => NudgeKey.Right, Key.Up => NudgeKey.Up, _ => NudgeKey.Down };
            if (SelectionNudge.Apply(new PointD(p.X, p.Y), nudge, fast, displays) is not { } to) return;
            Native.SetCursorPos((int)Math.Round(to.X), (int)Math.Round(to.Y));
            UpdateLoupe(PointFromScreen(new Native.POINT { X = (int)Math.Round(to.X), Y = (int)Math.Round(to.Y) }));
        }
    }

    // MARK: The magnifier

    /// <summary>A magnified patch of the frozen screen, a ring on the middle pixel, and a bar with its colour and place.</summary>
    private sealed class ShotLoupe : Border
    {
        private const double Frame = ScreenshotLoupe.FrameSide;
        private readonly Image _picture = new() { Width = Frame, Height = Frame, Stretch = Stretch.Fill };
        private readonly Canvas _grid = new() { Width = Frame, Height = Frame, IsHitTestVisible = false };
        private readonly TextBlock _value = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        private readonly TextBlock _place = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)) };
        private WriteableBitmap? _patch;

        public ShotLoupe()
        {
            RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.NearestNeighbor);
            var frame = new Grid { Width = Frame, Height = Frame, Children = { _picture, _grid }, Clip = new RectangleGeometry(new Rect(0, 0, Frame, Frame), 9, 9) };
            var bar = new StackPanel { Margin = new Thickness(8, 6, 8, 6), Children = { _value, _place } };
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1E));
            CornerRadius = new CornerRadius(10);
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
            BorderThickness = new Thickness(1);
            IsHitTestVisible = false;
            Width = Frame + 2;
            Child = new StackPanel { Children = { frame, bar } };
        }

        public void Fill(CapturedImage image, int x, int y, int side, bool grid)
        {
            if (_patch is null || _patch.PixelWidth != side) { _patch = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null); _picture.Source = _patch; }
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
            _grid.Children.Clear();
            var cell = Frame / side;
            if (grid)
            {
                var line = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
                for (var i = 1; i < side; i++)
                {
                    _grid.Children.Add(new Rectangle { Width = Frame, Height = 0.5, Fill = line, Margin = new Thickness(0, i * cell, 0, 0) });
                    _grid.Children.Add(new Rectangle { Width = 0.5, Height = Frame, Fill = line, Margin = new Thickness(i * cell, 0, 0, 0) });
                }
            }
            var middle = half * cell;
            _grid.Children.Add(new Rectangle { Width = cell, Height = cell, Stroke = Brushes.White, StrokeThickness = 2, Margin = new Thickness(middle, middle, 0, 0) });
            _grid.Children.Add(new Rectangle { Width = Math.Max(1, cell - 2), Height = Math.Max(1, cell - 2), Stroke = Brushes.Black, StrokeThickness = 1, Margin = new Thickness(middle + 1, middle + 1, 0, 0) });
        }

        public void SetBar(string value, string place)
        {
            _value.Text = value;
            _place.Text = place;
        }
    }
}
