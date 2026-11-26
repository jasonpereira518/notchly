// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QRResultController.swift: what a scanned code holds, with Copy and (for a
// web link only) Open link. Nothing is copied by itself. Esc or a click elsewhere closes it.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Tools;

namespace Notchly.App.Tools;

internal sealed class QrResultWindow : Window
{
    public const string TitleText = "QR code", CopyText = "Copy", OpenText = "Open link", CopiedText = "QR code copied";

    public QrResultWindow(string payload)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 336;
        Left = -32000;
        Top = -32000;
        Title = TitleText;

        var text = new TextBox
        {
            Text = payload, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, FontSize = 13, Padding = new Thickness(0),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ChipButton(CopyText, () =>
        {
            if (ClipboardWrite.TrySetText(payload)) QuickToolHud.Show(CopiedText);
            Close();
        }));
        if (ScreenTextSupport.OpenableUrl(payload) is { } url)
            buttons.Children.Add(ChipButton(OpenText, () =>
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                Close();
            }, primary: true));
        Content = new Border
        {
            Margin = new Thickness(8), CornerRadius = new CornerRadius(16), Padding = new Thickness(16),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, Opacity = 0.4, ShadowDepth = 4 },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = TitleText, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 8) },
                    text, buttons,
                },
            },
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        Loaded += (_, _) => { PlaceBelowPointer(); Activate(); };
    }

    private static Border ChipButton(string label, Action action, bool primary = false)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(primary ? Color.FromRgb(0x2F, 0x6B, 0xEB) : Color.FromArgb(0x33, 255, 255, 255)),
            Child = new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White },
        };
        chip.MouseLeftButtonUp += (_, _) => action();
        return chip;
    }

    /// <summary>Below the pointer, kept inside the display's work area.</summary>
    private void PlaceBelowPointer()
    {
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y);
        if (display is null) return;
        var handle = new WindowInteropHelper(this).Handle;
        var own = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * own.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * own.DpiScaleY);
        var work = display.WorkArea;
        var x = Math.Clamp(p.X - width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        var y = Math.Clamp(p.Y + 20, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */);
    }
}
