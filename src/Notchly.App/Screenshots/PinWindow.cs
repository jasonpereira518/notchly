// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotPinController: a capture kept on screen in a floating, always-on-top window. Drag to move, the wheel
// changes the opacity, the right-click menu copies, saves, turns click-through on and closes; Esc closes. While click-through is on,
// holding Ctrl makes the window catch the pointer again so it can be reached.
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
using Cursors = System.Windows.Input.Cursors;

namespace Notchly.App.Screenshots;

public sealed class PinWindow : Window
{
    private readonly DispatcherTimer _ctrl = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _clickThrough;
    private IntPtr _handle;

    public PinWindow(CapturedImage image, double scale, Action<CapturedImage> copy, Action<CapturedImage> save)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        Title = "Pinned screenshot";

        var picture = new System.Windows.Controls.Image
        {
            Source = ImageCodec.ToBitmap(image), Stretch = Stretch.Fill,
            Width = Math.Max(32, image.Width / Math.Max(1, scale)), Height = Math.Max(32, image.Height / Math.Max(1, scale)),
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        Content = new Border
        {
            Margin = new Thickness(10), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 }, Child = picture, Cursor = Cursors.SizeAll,
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MouseWheel += (_, e) => Opacity = Math.Clamp(Opacity + e.Delta / 120.0 * 0.1, 0.2, 1);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action) { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); return item; }
        menu.Items.Add(Item("Copy", () => copy(image)));
        menu.Items.Add(Item("Save", () => save(image)));
        var through = new MenuItem { Header = "Click through (hold Ctrl to reach it)", IsCheckable = true };
        through.Click += (_, _) => SetClickThrough(through.IsChecked);
        menu.Items.Add(through);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", Close));
        ContextMenu = menu;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(_handle, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        _ctrl.Tick += (_, _) =>
        {
            if (!_clickThrough) return;
            var held = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            if (held) Native.AddExStyle(_handle, 0, Native.WS_EX_TRANSPARENT);
            else Native.AddExStyle(_handle, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
        };
        Closed += (_, _) => _ctrl.Stop();
    }

    /// <summary>Opens the pin over the area it was taken from (physical pixels).</summary>
    public void ShowAt(Native.RECT area)
    {
        Show();
        // The pin sits where the capture was taken, one pixel for one pixel, so it covers what it shows.
        var dpi = VisualTreeHelper.GetDpi(this);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, area.Left - (int)(10 * dpi.DpiScaleX), area.Top - (int)(10 * dpi.DpiScaleY), 0, 0, 0x0001 | 0x0010);
        Activate();
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        if (on) { Native.AddExStyle(_handle, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED); _ctrl.Start(); }
        else { Native.AddExStyle(_handle, 0, Native.WS_EX_TRANSPARENT); _ctrl.Stop(); }
    }
}
