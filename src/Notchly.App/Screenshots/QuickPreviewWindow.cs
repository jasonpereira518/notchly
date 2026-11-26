// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotQuickPreviewController: the small card that shows a finished capture beside the selection (or in a
// corner), with Edit, Pin, Copy, Save and close, dismissing itself after the chosen time unless the pointer is on it. It never takes
// the focus unless the setting says so. Timing is Core's QuickPreviewDismissal; placement is ScreenshotPreviewPlacement (physical pixels).
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Cursors = System.Windows.Input.Cursors;

namespace Notchly.App.Screenshots;

public sealed record QuickPreviewActions(Action Edit, Action Pin, Action Copy, Action Save, Action Discard, Action Share, Action? Text);

public sealed class QuickPreviewWindow : Window
{
    private const double ThumbWidth = 240, ThumbHeight = 150;
    private readonly QuickPreviewDismissal _dismissal;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), Margin = new Thickness(2, 6, 0, 0) };
    private readonly bool _takesFocus;
    private readonly string? _dragFile;
    private Point _dragStart;

    public QuickPreviewWindow(CapturedImage image, double? dismissInterval, bool takesFocus, string? status, string? dragFile, QuickPreviewActions actions)
    {
        _dismissal = new QuickPreviewDismissal(dismissInterval);
        _takesFocus = takesFocus;
        _dragFile = dragFile;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = takesFocus;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = takesFocus;
        Left = -32000;
        Top = -32000;
        Title = "Screenshot preview";

        var scale = Math.Min(1, Math.Min(ThumbWidth / image.Width, ThumbHeight / image.Height));
        var picture = new System.Windows.Controls.Image
        {
            Source = ImageCodec.ToBitmap(image), Width = Math.Max(24, image.Width * scale), Height = Math.Max(24, image.Height * scale),
            Stretch = Stretch.Fill, Cursor = Cursors.Hand,
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = picture, HorizontalAlignment = HorizontalAlignment.Center,
        };
        picture.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); if (e.ClickCount == 2) { actions.Edit(); Close(); } };
        picture.MouseMove += OnPictureMove;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        void Add(string label, Action action, bool closes = true)
        {
            var text = new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.White };
            var button = new Border { Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Child = text, Background = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)) };
            button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(0x48, 255, 255, 255));
            button.MouseLeave += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
            button.MouseLeftButtonUp += (_, _) => { action(); if (closes) Close(); };
            buttons.Children.Add(button);
        }
        Add("Edit", actions.Edit);
        Add("Pin", actions.Pin);
        Add("Copy", actions.Copy);
        Add("Save", actions.Save);
        if (actions.Text is { } text) Add("Text", text);
        Add("Share", actions.Share, closes: false);
        Add("✕", actions.Discard);

        var column = new StackPanel { Children = { frame, buttons } };
        if (!string.IsNullOrEmpty(status)) { _status.Text = status; column.Children.Add(_status); }
        Content = new Border
        {
            Margin = new Thickness(14), Padding = new Thickness(10), CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 }, Child = column,
        };
        MouseEnter += (_, _) => _dismissal.HoverChanged(true, _clock.Elapsed.TotalSeconds);
        MouseLeave += (_, _) => _dismissal.HoverChanged(false, _clock.Elapsed.TotalSeconds);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { actions.Discard(); Close(); } };
        _timer.Tick += (_, _) => { if (_dismissal.Advance(_clock.Elapsed.TotalSeconds)) Close(); };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Places the card (physical pixels, from the selection) and shows it.</summary>
    public void ShowNear(Native.RECT area, QuickPreviewPosition position)
    {
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (!_takesFocus) Native.AddExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        Show();
        UpdateLayout();
        Native.GetCursorPos(out var pointer);
        var screens = Displays.All().Select(d => new PlacementScreen(Px(d.Bounds), Px(d.WorkArea))).ToList();
        var anchor = Px(area);
        var visible = ScreenshotPreviewPlacement.VisibleFrame(anchor, new PointD(pointer.X, pointer.Y), screens, screens.FirstOrDefault().VisibleFrame);
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new SizeD(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
        var frame = ScreenshotPreviewPlacement.Frame(size, anchor, new PointD(pointer.X, pointer.Y), visible, position);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, (int)frame.X, (int)frame.Y, 0, 0, 0x0001 | 0x0010);
        _dismissal.Schedule(_clock.Elapsed.TotalSeconds);
        _timer.Start();
    }

    private static RectD Px(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    private void OnPictureMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragFile is null || !System.IO.File.Exists(_dragFile)) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new DataObject(DataFormats.FileDrop, new[] { _dragFile });
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }
}
