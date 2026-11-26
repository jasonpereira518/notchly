// SPDX-License-Identifier: GPL-3.0-or-later
// The switcher's panel: the Windows counterpart of upstream's UI/Switcher/SwitcherView.swift. A grid of cards, each with the
// app's icon, the window's title and a live preview drawn by DWM; or, in icon-row mode, a row of icons with the chosen window's name
// beneath. It takes no focus (the keys arrive through the hook), sits above everything, and answers the mouse: a click
// switches to that window, a middle click closes it, and moving over a card selects it. Previews of minimized windows are
// replaced by the app's icon: DWM has no live picture of a window that is not on screen.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Notchly.App.Platform.Windowing;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Switcher;

public sealed record SwitcherCard(SwitchWindow Window, ImageSource? Icon);

public sealed class SwitcherOverlay : Window
{
    private const int GwlExStyle = -20;
    private const long ExToolWindow = 0x80, ExNoActivate = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly Brush Panel = new SolidColorBrush(Color.FromArgb(0xFB, 0x1E, 0x1E, 0x20));
    private static readonly Brush CardFill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardChosen = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xA3, 0xFF));

    private readonly Grid _root = new();
    private readonly TextBlock _query = new() { FontSize = 15, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 10), Visibility = Visibility.Collapsed };
    private readonly UniformGrid _grid = new();
    private readonly TextBlock _caption = new() { FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _hints = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<(Border Card, FrameworkElement? Preview, DwmThumbnail? Thumbnail)> _cards = [];
    private IntPtr _handle;
    private IReadOnlyList<SwitcherCard> _items = [];
    private bool _iconRow;
    private double _cardWidth, _cardHeight;
    private Rect _target;

    public event Action<int>? CardClicked, CardClosed, CardHovered;

    public SwitcherOverlay()
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
        var stack = new StackPanel();
        stack.Children.Add(_query);
        stack.Children.Add(_grid);
        stack.Children.Add(_caption);
        stack.Children.Add(_hints);
        _root.Children.Add(new Border { Background = Panel, CornerRadius = new CornerRadius(20), Padding = new Thickness(16), Child = stack, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) });
        Content = _root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(style | ExToolWindow | ExNoActivate));
        };
        ContentRendered += (_, _) => PlaceAndPreview();
    }

    /// <summary>Shows the cards on a display (physical pixels); the first call also shows the window.</summary>
    public void ShowCards(IReadOnlyList<SwitcherCard> cards, int selected, int columns, bool iconRow, string size, RECT display, bool hints)
    {
        _items = cards;
        _iconRow = iconRow;
        var scale = size switch { "small" => 0.78, "large" => 1.22, _ => 1.0 };
        _cardWidth = 288 * scale;
        _cardHeight = 214 * scale;
        _target = new Rect(display.Left, display.Top, display.Width, display.Height);
        _hints.Visibility = hints ? Visibility.Visible : Visibility.Collapsed;
        _hints.Text = "Tab or arrows to move  ·  Esc to cancel  ·  W close window  ·  Q quit app  ·  type to search";
        Build(columns, selected);
        if (!IsVisible) Show();
        else Dispatcher.BeginInvoke(PlaceAndPreview, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void SetQuery(string query)
    {
        _query.Text = "Search: " + query;
        _query.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Select(int index)
    {
        for (var i = 0; i < _cards.Count; i++) Highlight(_cards[i].Card, i == index);
        if (_iconRow)
        {
            _caption.Text = index >= 0 && index < _items.Count ? _items[index].Window.Title : "";
            _caption.Visibility = Visibility.Visible;
        }
    }

    private void Build(int columns, int selected)
    {
        foreach (var c in _cards) c.Thumbnail?.Dispose();
        _cards.Clear();
        _grid.Children.Clear();
        _grid.Columns = Math.Max(1, columns);
        _caption.Visibility = _iconRow ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = _items[i];
            var (card, preview) = _iconRow ? IconTile(item) : Card(item);
            card.MouseLeftButtonUp += (_, _) => CardClicked?.Invoke(index);
            card.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CardClosed?.Invoke(index); };
            card.MouseEnter += (_, _) => CardHovered?.Invoke(index);
            _grid.Children.Add(card);
            _cards.Add((card, preview, null));
        }
        Select(selected);
    }

    private (Border, FrameworkElement?) Card(SwitcherCard item)
    {
        var grid = new Grid { Width = _cardWidth - 20, Height = _cardHeight - 20 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(31) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Image { Source = item.Icon, Width = 20, Height = 20, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = item.Window.Title, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        grid.Children.Add(header);
        var preview = new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)), CornerRadius = new CornerRadius(6) };
        if (item.Window.IsMinimized || item.Window.OnOtherDesktop)
        {
            var holder = new Grid();
            holder.Children.Add(new Image { Source = item.Icon, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            holder.Children.Add(new TextBlock
            {
                Text = item.Window.IsMinimized ? "Minimized" : "Other desktop", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6),
            });
            preview.Child = holder;
        }
        Grid.SetRow(preview, 1);
        grid.Children.Add(preview);
        var card = new Border { Child = grid, Margin = new Thickness(5), Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Background = CardFill, BorderBrush = Brushes.Transparent };
        return (card, item.Window.IsMinimized || item.Window.OnOtherDesktop ? null : preview);
    }

    private (Border, FrameworkElement?) IconTile(SwitcherCard item)
    {
        var tile = new Border
        {
            Width = 86, Height = 86, Margin = new Thickness(4), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), Background = CardFill, BorderBrush = Brushes.Transparent,
            Child = new Image { Source = item.Icon, Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = item.Window.IsMinimized ? 0.6 : 1 },
        };
        return (tile, null);
    }

    private static void Highlight(Border card, bool chosen)
    {
        card.BorderBrush = chosen ? Accent : Brushes.Transparent;
        card.Background = chosen ? CardChosen : CardFill;
    }

    /// <summary>Puts the panel in the middle of the display and lays the previews into their cards.</summary>
    private void PlaceAndPreview()
    {
        if (_handle == IntPtr.Zero) return;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var x = (int)(_target.Left + (_target.Width - width) / 2);
        var y = (int)(_target.Top + (_target.Height - height) / 2);
        SetWindowPos(_handle, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0040);
        for (var i = 0; i < _cards.Count && i < _items.Count; i++)
        {
            var (card, preview, old) = _cards[i];
            old?.Dispose();
            DwmThumbnail? thumb = null;
            if (preview is not null && preview.ActualWidth > 0)
            {
                thumb = DwmThumbnail.Register(_handle, _items[i].Window.Window);
                var bounds = preview.TransformToAncestor(this).TransformBounds(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
                thumb?.Show((int)(bounds.X * dpi.DpiScaleX), (int)(bounds.Y * dpi.DpiScaleY), (int)(bounds.Width * dpi.DpiScaleX), (int)(bounds.Height * dpi.DpiScaleY));
            }
            _cards[i] = (card, preview, thumb);
        }
    }

    public void Dismiss()
    {
        foreach (var c in _cards) c.Thumbnail?.Dispose();
        _cards.Clear();
        try { Close(); } catch (InvalidOperationException) { /* already closed */ }
    }
}
