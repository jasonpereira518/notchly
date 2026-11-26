// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the short-lived toast of upstream Services/QuickTools/QuickToolHUD.swift ("Text copied", a picked colour with its swatch):
// a small pill near the bottom of the display the pointer is on, that never takes the focus or the mouse, and fades after a moment.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;

namespace Notchly.App.Tools;

public sealed class QuickToolHud : Window
{
    private static QuickToolHud? _current;

    private readonly Border _swatch = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), BorderThickness = new Thickness(0.5) };
    private readonly TextBlock _text = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 40 };
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private IntPtr _handle;

    private QuickToolHud()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Left = -32000;
        Top = -32000;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _swatch.Margin = new Thickness(0, 0, 10, 0);
        _swatch.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_swatch);
        row.Children.Add(_text);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10), Child = row, Margin = new Thickness(8),
        };
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            animation.Completed += (_, _) => { if (ReferenceEquals(_current, this)) { _current = null; Close(); } };
            BeginAnimation(OpacityProperty, animation);
        };
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
        };
    }

    /// <summary>Shows a line, with a colour swatch when given, for a moment. A second one replaces the first.</summary>
    public static void Show(string text, Color? swatch = null)
    {
        _current?.Close();
        var hud = _current = new QuickToolHud();
        hud._text.Text = text;
        hud._swatch.Visibility = swatch is null ? Visibility.Collapsed : Visibility.Visible;
        if (swatch is { } color) hud._swatch.Background = new SolidColorBrush(color);
        hud.Show();
        hud.UpdateLayout();
        hud.Place();
        hud._fade.Start();
    }

    private void Place()
    {
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        if (display is null || _handle == IntPtr.Zero) return;
        var own = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * own.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * own.DpiScaleY);
        var work = display.WorkArea;
        var x = work.Left + (work.Width - width) / 2;
        var y = work.Bottom - height - (int)(72 * display.Scale);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001 | 0x0010 /* SWP_NOSIZE | SWP_NOACTIVATE */);
    }

    protected override void OnClosed(EventArgs e)
    {
        _fade.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }
}
