// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the floating panel of upstream's QuickLauncherService: 420 wide, three columns, a header with the customize switch, placed
// near the upper third of the display the pointer is on. It takes the keys (Core's model decides what each does), and closes on an outside
// click or another app coming forward unless a panel is hosted or tiles are being arranged.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Launcher;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Launcher;

internal sealed class QuickLauncherWindow : FluentWindow
{
    private readonly QuickLauncherService _service;
    private readonly QuickLauncherView _view;
    private readonly Border _customize;
    private bool _closing;

    public QuickLauncherWindow(QuickLauncherService service)
    {
        _service = service;
        Title = "Quick panel";
        Width = QuickLauncherLayout.PanelWidth;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;
        Left = -32000;
        Top = -32000;

        _view = new QuickLauncherView(service, rail: false);
        var title = new TextBlock { Text = "Quick panel", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var glyph = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _customize = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand, ToolTip = "Customize", Child = glyph, Background = Brushes.Transparent };
        _customize.MouseLeftButtonUp += (_, _) => { service.Model.IsEditing = !service.Model.IsEditing; service.Model.EditingOptionsItem = null; };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_customize, Dock.Right);
        header.Children.Add(_customize);
        header.Children.Add(title);
        var root = new StackPanel { Margin = new Thickness(QuickLauncherLayout.Padding, 38, QuickLauncherLayout.Padding, QuickLauncherLayout.Padding) };
        root.Children.Add(header);
        root.Children.Add(_view.Root);
        Content = root;

        service.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickLauncherModel.IsEditing)) _customize.Background = service.Model.IsEditing ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : Brushes.Transparent;
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_closing && IsVisible && service.Model.DismissesOnOutsideInteraction) Close(); };
        Closing += (_, _) => _closing = true;
        SourceInitialized += (_, _) => Place();
    }

    public void Open()
    {
        _view.Rebuild();
        Show();
        Activate();
    }

    /// <summary>Centred across the display with the pointer, near its upper third (upstream's Spotlight-style 62% height).</summary>
    private void Place()
    {
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        if (display is null) return;
        var work = display.WorkArea;
        var x = work.Left + (work.Width - (int)Math.Round(Width * display.Scale)) / 2;
        var y = work.Top + (int)(work.Height * 0.2);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001 | 0x0010);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = QuickLauncherModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= QuickLauncherModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= QuickLauncherModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= QuickLauncherModifiers.Windows;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= QuickLauncherModifiers.Shift;
        var composing = e.Key == Key.ImeProcessed;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (_service.Model.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKeys.FromVirtualKey(vk), modifiers, composing), _view.Flow)) e.Handled = true;
    }
}
