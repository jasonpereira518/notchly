// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's recent-captures panel (RecentCaptureService's window): the last captures as thumbnails; a click opens one in the
// editor, the context menu copies, pins, shows it in its folder or removes it. The list is Core's RecentCaptureStore through CaptureHistory.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Shell;
using Notchly.App.Tools;
using Notchly.Core.Capture;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Screenshots;

internal sealed class HistoryWindow : FluentWindow
{
    private static HistoryWindow? _current;
    private readonly ScreenshotService _service;
    private readonly WrapPanel _grid = new() { Margin = new Thickness(4) };
    private readonly TextBlock _empty = new() { Text = "No screenshots yet", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0), Opacity = 0.7 };
    private readonly Action _changed;

    public static void Show(ScreenshotService service)
    {
        if (_current is { } open) { open.Activate(); return; }
        _current = new HistoryWindow(service);
        _current.Closed += (_, _) => _current = null;
        ((Window)_current).Show();
    }

    private HistoryWindow(ScreenshotService service)
    {
        _service = service;
        Title = "Recent screenshots";
        Width = 760;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var clear = new Button { Content = "Clear all", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        clear.Click += (_, _) => service.History.Clear();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = "Recent screenshots", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(title);
        var root = new DockPanel { Margin = new Thickness(16, 40, 16, 16) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { _grid, _empty } } });
        Content = root;
        _changed = () => Dispatcher.BeginInvoke(Rebuild);
        service.History.Changed += _changed;
        Closed += (_, _) => service.History.Changed -= _changed;
        Rebuild();
    }

    private void Rebuild()
    {
        _grid.Children.Clear();
        var entries = _service.History.Entries();
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries) _grid.Children.Add(Tile(entry));
    }

    private FrameworkElement Tile(RecentCaptureEntry entry)
    {
        var picture = new System.Windows.Controls.Image { Width = 200, Height = 125, Stretch = Stretch.Uniform };
        if (_service.History.ThumbnailPathOf(entry) is { } thumb && ImageCodec.DecodeFile(thumb) is { } image) picture.Source = ImageCodec.ToBitmap(image);
        var caption = new TextBlock { Text = entry.CreatedAt.LocalDateTime.ToString("g"), FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var tile = new Border
        {
            Margin = new Thickness(6), Padding = new Thickness(8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), Child = new StackPanel { Children = { picture, caption } },
        };
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        tile.MouseLeave += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255));
        tile.MouseLeftButtonUp += (_, _) => Open(entry);
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action) { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); return item; }
        menu.Items.Add(Item("Edit", () => Open(entry)));
        menu.Items.Add(Item("Copy", () => WithImage(entry, image => _service.CopyToClipboard(image, entry.Scale ?? 1))));
        menu.Items.Add(Item("Pin", () => WithImage(entry, image => _service.Pin(image, entry.Scale ?? 1, AreaOf(entry, image)))));
        menu.Items.Add(Item("Show in folder", () => { if (_service.History.PathOf(entry) is { } path) ShellActions.Reveal(path); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () => _service.History.Remove(entry.Id)));
        tile.ContextMenu = menu;
        return tile;
    }

    private void Open(RecentCaptureEntry entry) => WithImage(entry, image => _service.OpenEditor(image, entry.Scale ?? 1));

    private void WithImage(RecentCaptureEntry entry, Action<CapturedImage> action)
    {
        if (_service.History.PathOf(entry) is { } path && ImageCodec.DecodeFile(path) is { } image) action(image);
        else QuickToolHud.Show("That screenshot is gone");
    }

    private static Native.RECT AreaOf(RecentCaptureEntry entry, CapturedImage image) => new()
    {
        Left = (int)(entry.AnchorX ?? 100), Top = (int)(entry.AnchorY ?? 100),
        Right = (int)((entry.AnchorX ?? 100) + (entry.AnchorWidth ?? image.Width)), Bottom = (int)((entry.AnchorY ?? 100) + (entry.AnchorHeight ?? image.Height)),
    };
}
