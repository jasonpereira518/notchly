// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/Shelf/ShelfView.swift and the panel code of ShelfService.swift: the floating Shelf card. A dark card, 304 wide,
// with the shelf's tiles in an area 188 high, a header (count, pin, close) and a bottom bar (share, clear). It floats over everything
// without taking the focus, is itself a drop target, and hides after five seconds of being empty and left alone. It is placed in
// physical pixels, below the pointer for a shake or the hotkey, or a third of it in from the edge for the edge peek.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notchly.App.Island.Files;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Settings;
using Notchly.Core.Shelf;
using Notchly.Core.Windowing;

namespace Notchly.App.ShelfCard;

internal sealed class ShelfCardWindow : Window
{
    private static readonly SolidColorBrush Back = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush Edge = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush TargetedBack = Frozen(Color.FromArgb(0x26, 0x4C, 0x8D, 0xFF));

    private readonly ShelfService _shelf;
    private readonly SettingsStore _settings;
    private readonly ShelfTilesView _tiles;
    private readonly Border _card;
    private readonly WrapPanel _tilePanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
    private readonly ScrollViewer _scroller;
    private readonly Grid _empty;
    private readonly Rectangle _emptyOutline;
    private readonly TextBlock _count, _pinGlyph, _hint;
    private readonly Border _clear, _share;
    private readonly TextBlock _clearGlyph;
    private readonly DispatcherTimer _idle;
    private IntPtr _handle;
    private bool _pinned, _targeted, _edgePeek, _shown;
    private double _idleSeconds;

    /// <summary>Something was dropped on the card (a peeking card settles fully on).</summary>
    public event Action? Dropped;
    /// <summary>A drag came over the card.</summary>
    public event Action? DragEntered;
    /// <summary>The card went away (closed, or hid itself).</summary>
    public event Action? Hidden;

    public bool IsShown => _shown;
    public bool IsEdgePeek => _edgePeek;
    public bool IsPinned => _pinned;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)(Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons"));

    public ShelfCardWindow(ShelfService shelf, SettingsStore settings)
    {
        _shelf = shelf;
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = ShelfSummonSupport.CardWidth;
        Left = -32000;
        Top = -32000;
        AllowDrop = true;
        Focusable = false;
        Opacity = 0;

        var root = new Grid { Margin = new Thickness(ShelfSummonSupport.CardPadding) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ShelfSummonSupport.TileAreaHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: the title, how many are on it, and the pin and close buttons.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Shelf", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _count = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { Background = Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Height = 16, Margin = new Thickness(8, 0, 0, 0), Child = _count };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _pinGlyph = Glyph("", "Keep the shelf open");
        var pin = Wrap(_pinGlyph, () => { _pinned = !_pinned; _pinGlyph.Foreground = _pinned ? Accent : Dim; ResetIdle(); });
        var close = Wrap(Glyph("", "Close"), () => CloseCard());
        buttons.Children.Add(pin);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 3);
        header.Children.Add(buttons);
        root.Children.Add(header);

        // The tiles, or the empty drop target.
        _scroller = new ScrollViewer { Content = _tilePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Grid.SetRow(_scroller, 2);
        root.Children.Add(_scroller);
        _emptyOutline = new Rectangle { RadiusX = 16, RadiusY = 16, Stroke = Edge, StrokeThickness = 0.75, StrokeDashArray = [4, 5] };
        _empty = new Grid { Visibility = Visibility.Collapsed };
        _empty.Children.Add(_emptyOutline);
        var emptyText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        emptyText.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 22, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center });
        emptyText.Children.Add(new TextBlock { Text = "Drop files here", FontSize = 13, Foreground = Dim, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(emptyText);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);

        // The bottom bar: a hint, share and clear.
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint = new TextBlock { FontSize = 10, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        bar.Children.Add(_hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _share = Wrap(Glyph("", "Share", Brushes.White), () => _tiles!.Share(_shelf.Store.ActionTargets));
        _clearGlyph = Glyph("", "Clear all", Brushes.White);
        _clear = Wrap(_clearGlyph, () =>
        {
            if (_shelf.Store.Selection.Count > 0) _shelf.Remove(_shelf.Store.Selection.ToList());
            else _shelf.ClearAll();
        });
        actions.Children.Add(_share);
        actions.Children.Add(_clear);
        Grid.SetColumn(actions, 1);
        bar.Children.Add(actions);
        Grid.SetRow(bar, 4);
        root.Children.Add(bar);

        _card = new Border { Background = Back, BorderBrush = Edge, BorderThickness = new Thickness(0.75), CornerRadius = new CornerRadius(ShelfSummonSupport.CardCornerRadius), Child = root };
        Content = _card;
        _tiles = new ShelfTilesView(shelf, settings, () => _handle, this, () => { if (ShelfInteraction.ShouldCloseAfterDrag(true, 1, _settings.GetBool(DefaultsKey.ShelfCloseAfterDrop), _pinned)) Fade(false); });
        _tiles.InteractionChanged += ResetIdle;
        shelf.Changed += () => { Render(); ResetIdle(); };

        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => SetTargeted(false);
        Drop += OnDrop;
        MouseMove += (_, _) => ResetIdle();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            // A tool window that never takes the focus.
            Native.AddExStyle(_handle, 0x80 | 0x08000000, 0x40000);
        };
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnIdleTick(), Dispatcher) { IsEnabled = false };
        Render();
    }

    private static TextBlock Glyph(string glyph, string tip, Brush? foreground = null) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = 13, Foreground = foreground ?? Dim, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static Border Wrap(TextBlock glyph, Action click)
    {
        var border = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Child = glyph, Cursor = Cursors.Hand };
        border.MouseEnter += (_, _) => border.Background = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return border;
    }

    // MARK: Contents

    private void Render()
    {
        var store = _shelf.Store;
        var empty = store.Items.Count == 0;
        _tiles.Render(_tilePanel);
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _count.Text = store.Items.Sum(i => Math.Max(1, i.LeafCount)).ToString();
        _count.Parent.SetValue(UIElement.VisibilityProperty, empty ? Visibility.Collapsed : Visibility.Visible);
        var selected = store.Selection.Count;
        _hint.Text = selected > 0 ? $"{selected} selected" : empty ? "" : "Drag out to use";
        _clearGlyph.ToolTip = selected > 0 ? "Remove selected" : "Clear all";
        foreach (var button in new[] { _clear, _share })
        {
            button.IsHitTestVisible = !empty;
            button.Opacity = empty ? 0.35 : 1;
        }
    }

    // MARK: Dropping

    private void OnDrag(object sender, DragEventArgs e)
    {
        DragEntered?.Invoke();
        if (_tiles.DraggingOut || !_shelf.CanAccept(e.Data)) { e.Effects = DragDropEffects.None; SetTargeted(false); }
        else { e.Effects = DragDropEffects.Copy; SetTargeted(true); }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetTargeted(false);
        e.Handled = true;
        if (_tiles.DraggingOut || !_shelf.CanAccept(e.Data)) return;
        _shelf.AddDrop(e.Data);
        Dropped?.Invoke();
        ResetIdle();
    }

    private void SetTargeted(bool value)
    {
        if (_targeted == value) return;
        _targeted = value;
        _emptyOutline.Stroke = value ? Accent : Edge;
        _card.Background = value ? TargetedBack : Back;
        ResetIdle();
    }

    // MARK: Showing and hiding

    /// <summary>The card's size in physical pixels at a display's scale.</summary>
    private (double Width, double Height) PhysicalSize(double scale)
    {
        if (_card.DesiredSize.Height <= 0 || double.IsNaN(_card.DesiredSize.Height))
        {
            Measure(new Size(ShelfSummonSupport.CardWidth, double.PositiveInfinity));
        }
        var height = DesiredSize.Height > 0 ? DesiredSize.Height : ShelfSummonSupport.TileAreaHeight + 70;
        return (ShelfSummonSupport.CardWidth * scale, height * scale);
    }

    private void Place(double x, double y)
    {
        if (_handle == IntPtr.Zero) _handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0001 | 0x0010 | 0x0040);
    }

    /// <summary>Opens below the pointer, kept inside the display's work area.</summary>
    public void ShowAtPointer(DisplayInfo display, int pointerX, int pointerY)
    {
        _edgePeek = false;
        Render();
        var (width, height) = PhysicalSize(display.Scale);
        var work = new WindowRect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
        var (x, y) = ShelfSummonSupport.CardOrigin(pointerX, pointerY, width, height, work);
        Reveal(x, y);
    }

    /// <summary>Peeks in a third of the way from a side edge; a drop slides it fully on.</summary>
    public void ShowPeek(ScreenSide side, DisplayInfo display)
    {
        _edgePeek = true;
        _peekSide = side;
        _peekDisplay = display;
        Render();
        var (width, height) = PhysicalSize(display.Scale);
        var work = new WindowRect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
        var (x, y) = ShelfSummonSupport.PeekOrigin(side, work, width, height);
        Reveal(x, y);
    }

    private ScreenSide _peekSide;
    private DisplayInfo? _peekDisplay;

    /// <summary>The peeking card slides fully onto the screen.</summary>
    public void Settle()
    {
        if (!_edgePeek || _peekDisplay is not { } display) return;
        _edgePeek = false;
        var (width, height) = PhysicalSize(display.Scale);
        var work = new WindowRect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
        var (x, y) = ShelfSummonSupport.SettledOrigin(_peekSide, work, width, height);
        Place(x, y);
        ResetIdle();
    }

    private void Reveal(double x, double y)
    {
        Place(x, y);
        if (!IsVisible) Show();
        Place(x, y);
        _shown = true;
        ResetIdle();
        _idle.Start();
        Fade(true);
    }

    private void Fade(bool visible)
    {
        if (!visible) _shown = false;
        var animation = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromSeconds(visible ? 0.12 : ShelfSummonSupport.FadeSeconds));
        if (!visible)
            animation.Completed += (_, _) =>
            {
                if (_shown) return;
                _idle.Stop();
                _edgePeek = false;
                Hide();
                SetWindowPos(_handle, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010);
                Hidden?.Invoke();
            };
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>The close button: clears the shelf first when it is set to.</summary>
    public void CloseCard()
    {
        if (_settings.GetBool(DefaultsKey.ShelfClearOnClose)) _shelf.ClearAll();
        Fade(false);
    }

    /// <summary>Hides without clearing anything (the hotkey, a drag that ended elsewhere).</summary>
    public void HideCard()
    {
        if (_shown) Fade(false);
    }

    // MARK: Idle

    private void ResetIdle() => _idleSeconds = 0;

    private void OnIdleTick()
    {
        if (!_shown || _handle == IntPtr.Zero) return;
        GetWindowRect(_handle, out var rect);
        Native.GetCursorPos(out var pointer);
        var inside = pointer.X >= rect.Left && pointer.X <= rect.Right && pointer.Y >= rect.Top && pointer.Y <= rect.Bottom;
        var hold = !ShelfSummonSupport.ShouldAutoHide(_shelf.Store.Items.Count == 0, _pinned, inside, _targeted, _tiles.Interacting, _edgePeek);
        if (hold) { _idleSeconds = 0; return; }
        _idleSeconds++;
        if (_idleSeconds >= ShelfSummonSupport.AutoHideSeconds) HideCard();
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);
}
