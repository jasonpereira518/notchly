// SPDX-License-Identifier: GPL-3.0-or-later
// The clipboard history list, shared by the island's Clipboard page and the quick window. Port of upstream UI/Notch/NotchClipboardView.swift
// and UI/MenuPanel/ClipboardQuickPanelView.swift: a column of cards (104 high, or 52 compact), newest first with pinned ones on top, a
// search box that appears when you type, a pinned-only filter, clear-recent, and per-card copy, pin, move and delete. A compact card
// opens to up to eight lines after a 0.7 s hover. Return pastes, Shift+Return only copies, Ctrl+1 to 9 pick by position, Alt+P pins.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;

namespace Notchly.App.Pasteboard;

public sealed record ClipboardPalette(Brush Text, Brush Dim, Brush Faint, Brush Card, Brush CardSelected, Brush Accent, Brush Hover)
{
    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>For the island, which is always dark.</summary>
    public static ClipboardPalette Dark { get; } = new(Frozen(0xFF, 0xFF, 0xFF, 0xFF), Frozen(0x99, 0xFF, 0xFF, 0xFF), Frozen(0x66, 0xFF, 0xFF, 0xFF),
        Frozen(0x12, 0xFF, 0xFF, 0xFF), Frozen(0x33, 0x4C, 0x8D, 0xFF), Frozen(0xFF, 0x4C, 0x8D, 0xFF), Frozen(0x22, 0xFF, 0xFF, 0xFF));

    public static ClipboardPalette Light { get; } = new(Frozen(0xFF, 0x1B, 0x1B, 0x1B), Frozen(0x99, 0x1B, 0x1B, 0x1B), Frozen(0x66, 0x1B, 0x1B, 0x1B),
        Frozen(0x0D, 0x00, 0x00, 0x00), Frozen(0x2E, 0x00, 0x67, 0xC0), Frozen(0xFF, 0x00, 0x67, 0xC0), Frozen(0x1A, 0x00, 0x00, 0x00));
}

public sealed class ClipboardView
{
    private const double ComfortableHeight = 104, CompactHeight = 52;
    private static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(700), KeyDwell = TimeSpan.FromMilliseconds(1400);

    private readonly ClipboardHistoryService _history;
    private readonly SettingsStore _settings;
    private readonly ClipboardPalette _palette;
    private readonly Grid _root = new();
    private readonly System.Windows.Controls.TextBox _search;
    private readonly Border _searchBox;
    private readonly StackPanel _cards = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly TextBlock _pinFilter;
    private readonly DispatcherTimer _dwell;
    private string _query = "";
    private bool _pinnedOnly, _visible;
    private Guid? _selected, _anchor, _open;
    private readonly HashSet<Guid> _marked = [];
    private IReadOnlyList<ClipboardEntry> _shown = [];
    private Guid? _hoverPending;

    /// <summary>The entries to paste (or copy only) were chosen.</summary>
    public event Action<IReadOnlyList<ClipboardEntry>, bool>? Chosen;
    /// <summary>The island asks for this to open the quick window.</summary>
    public event Action? OpenWindowRequested;

    public ClipboardView(ClipboardHistoryService history, SettingsStore settings, ClipboardPalette palette, bool offerWindow)
    {
        _history = history;
        _settings = settings;
        _palette = palette;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search = new System.Windows.Controls.TextBox
        {
            Style = null, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = palette.Text, CaretBrush = palette.Text,
            FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        _search.TextChanged += (_, _) => { _query = _search.Text; Render(); };
        var magnifier = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13, Foreground = palette.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(magnifier);
        Grid.SetColumn(_search, 1);
        searchRow.Children.Add(_search);
        _searchBox = new Border { Background = palette.Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0), Height = 34, Child = searchRow, Visibility = Visibility.Collapsed };
        var hint = new TextBlock { Text = "Type to search", FontSize = 11, Foreground = palette.Faint, VerticalAlignment = VerticalAlignment.Center, Name = "Hint" };
        var left = new Grid();
        left.Children.Add(hint);
        left.Children.Add(_searchBox);
        _searchHint = hint;
        header.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        _pinFilter = Glyph("", "Show pinned only", () => { _pinnedOnly = !_pinnedOnly; Render(); });
        actions.Children.Add(Wrap(_pinFilter));
        actions.Children.Add(Wrap(Glyph("", "Clear recent (pinned stay)", ClearRecent)));
        if (offerWindow) actions.Children.Add(Wrap(Glyph("", "Open in a window", () => OpenWindowRequested?.Invoke())));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        _root.Children.Add(header);

        _scroller = new ScrollViewer
        {
            Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        _empty = new TextBlock
        {
            Text = "Things you copy appear here", FontSize = 13, Foreground = palette.Dim, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        };
        var body = new Grid();
        body.Children.Add(_scroller);
        body.Children.Add(_empty);
        Grid.SetRow(body, 2);
        _root.Children.Add(body);

        _dwell = new DispatcherTimer(DispatcherPriority.Background) { Interval = HoverDwell };
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_hoverPending is { } id && Compact) { _open = id; Render(); }
        };
        history.Changed += () => { if (_visible) Render(); };
    }

    private readonly TextBlock _searchHint;
    private static FontFamily IconFont => (FontFamily)(Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons"));

    public FrameworkElement Root => _root;
    private bool Compact => _settings.GetString(DefaultsKey.NotchClipboardCardSize) == "compact";
    private double CardHeight => Compact ? CompactHeight : ComfortableHeight;

    /// <summary>The page is on screen: it draws, and a fresh look starts with nothing searched and the newest entry chosen.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) { ResetSearch(); _selected = null; _marked.Clear(); _open = null; Render(); }
            else _dwell.Stop();
        }
    }

    public void ResetSearch()
    {
        _query = "";
        _search.Text = "";
        _searchBox.Visibility = Visibility.Collapsed;
        _searchHint.Visibility = Visibility.Visible;
        _pinnedOnly = false;
    }

    // MARK: Drawing

    private TextBlock Glyph(string glyph, string tip, Action act)
    {
        var block = new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 13, Foreground = _palette.Dim, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand,
        };
        block.MouseLeftButtonUp += (_, e) => { act(); e.Handled = true; };
        return block;
    }

    private Border Wrap(TextBlock glyph)
    {
        var border = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Child = glyph, Margin = new Thickness(2, 0, 0, 0) };
        border.MouseEnter += (_, _) => border.Background = _palette.Hover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        glyph.IsHitTestVisible = true;
        return border;
    }

    private void ClearRecent()
    {
        _history.ClearRecent();
        _selected = null;
        _marked.Clear();
        Render();
    }

    private IReadOnlyList<ClipboardEntry> Filtered()
    {
        var entries = _history.Entries;
        if (_pinnedOnly) entries = entries.Where(e => e.IsPinned).ToList();
        return ClipboardHistorySupport.Search(entries, _query);
    }

    public void Render()
    {
        if (!_visible) return;
        _shown = Filtered();
        _pinFilter.Foreground = _pinnedOnly ? _palette.Accent : _palette.Dim;
        _cards.Children.Clear();
        _empty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = !_settings.GetBool(DefaultsKey.ClipboardHistoryEnabled) && _history.Entries.Count == 0
                ? "Clipboard history is off. Turn it on in Settings → Clipboard and files."
            : _history.Entries.Count == 0 ? "Things you copy appear here"
            : _pinnedOnly && _query.Length == 0 ? "Nothing is pinned" : "Nothing matches";
        _scroller.Visibility = _shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The newest unpinned entry is where the highlight starts, as upstream.
        if (_selected is not { } chosen || _shown.All(e => e.Id != chosen))
            _selected = (_shown.FirstOrDefault(e => !e.IsPinned) ?? _shown.FirstOrDefault())?.Id;
        _marked.IntersectWith(_shown.Select(e => e.Id));
        var position = 0;
        foreach (var entry in _shown) _cards.Children.Add(Card(entry, position++));
        if (_selected is { } id && _cards.Children.OfType<FrameworkElement>().FirstOrDefault(c => (Guid?)c.Tag == id) is { } card)
            card.Loaded += (_, _) => card.BringIntoView();
    }

    private FrameworkElement Card(ClipboardEntry entry, int position)
    {
        var opened = Compact && _open == entry.Id;
        var selected = _selected == entry.Id || _marked.Contains(entry.Id);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (!Compact || opened)
        {
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            if (_settings.GetBool(DefaultsKey.ClipboardHistoryShowSourceAppIcons) && SourceIcon(entry) is { } icon)
                meta.Children.Add(new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
            if (entry.IsPinned) meta.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = _palette.Accent, Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var source = entry.SourceApp is { Length: > 0 } app ? Path.GetFileNameWithoutExtension(app) + "  ·  " : "";
            meta.Children.Add(new TextBlock { Text = source + ClipboardHistorySupport.Age(entry.CopiedAt, DateTimeOffset.Now), FontSize = 10, Foreground = _palette.Faint, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(meta);
        }
        // A compact card has no header row, so a pinned one says so beside its text.
        if (Compact && !opened && entry.IsPinned)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = "\uE718", FontFamily = IconFont, FontSize = 10, Foreground = _palette.Accent, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(Body(entry, opened));
            content.Children.Add(line);
        }
        else content.Children.Add(Body(entry, opened));
        grid.Children.Add(content);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        buttons.Children.Add(Wrap(Glyph("", "Copy", () => Choose([entry], paste: false))));
        buttons.Children.Add(Wrap(Glyph(entry.IsPinned ? "" : "", entry.IsPinned ? "Unpin" : "Pin", () => { _history.TogglePin(entry.Id); })));
        buttons.Children.Add(Wrap(Glyph("", "Move up", () => _history.Move(entry.Id, -1))));
        buttons.Children.Add(Wrap(Glyph("", "Move down", () => _history.Move(entry.Id, 1))));
        buttons.Children.Add(Wrap(Glyph("", "Delete", () => _history.Delete([entry.Id]))));
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Background = selected ? _palette.CardSelected : _palette.Card, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 7, 8, 7),
            Margin = new Thickness(0, 0, 0, 8), Child = grid, Cursor = Cursors.Hand, Tag = entry.Id, ClipToBounds = true,
        };
        if (opened) card.MinHeight = CompactHeight; else card.Height = CardHeight;
        if (position < 9) card.ToolTip = $"Ctrl+{position + 1} pastes this";
        card.MouseEnter += (_, _) =>
        {
            buttons.Visibility = Visibility.Visible;
            _hoverPending = entry.Id;
            if (Compact && _open != entry.Id) { _dwell.Interval = HoverDwell; _dwell.Stop(); _dwell.Start(); }
        };
        card.MouseLeave += (_, _) =>
        {
            buttons.Visibility = Visibility.Hidden;
            if (_hoverPending == entry.Id) { _hoverPending = null; _dwell.Stop(); }
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideButtons(source, buttons)) return;
            Select(entry.Id, Keyboard.Modifiers);
            if (e.ClickCount >= 2) Choose([entry], paste: true);
        };
        return card;
    }

    private static bool InsideButtons(DependencyObject node, DependencyObject buttons)
    {
        for (var current = node; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, buttons)) return true;
        return false;
    }

    private ImageSource? SourceIcon(ClipboardEntry entry) => entry.SourcePath is { Length: > 0 } path ? AppIcons.ForImagePath(path) : null;

    private FrameworkElement Body(ClipboardEntry entry, bool opened)
    {
        switch (entry.Kind)
        {
            case ClipboardKind.Image:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var thumb = new Image { Height = Compact ? 28 : 56, MaxWidth = 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelHeight = 120;
                    bitmap.UriSource = new Uri(_history.ImagePath(entry));
                    bitmap.EndInit();
                    bitmap.Freeze();
                    thumb.Source = bitmap;
                }
                catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException) { /* a missing file shows no picture */ }
                row.Children.Add(thumb);
                row.Children.Add(new TextBlock
                {
                    Text = entry.ImageWidth is { } w && entry.ImageHeight is { } h ? $"{w} × {h}" : "Image", FontSize = 11, Foreground = _palette.Dim,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                });
                return row;
            }
            case ClipboardKind.Files:
            {
                var names = entry.FilePaths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p).ToList();
                var panel = new StackPanel();
                var first = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new Image { Width = Compact ? 20 : 28, Height = Compact ? 20 : 28, Margin = new Thickness(0, 0, 8, 0) };
                _ = ShellThumbnails.GetAsync(entry.FilePaths[0], 48).ContinueWith(t => { if (t.Result is { } image) icon.Dispatcher.BeginInvoke(() => icon.Source = image); }, TaskScheduler.Default);
                first.Children.Add(icon);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Lines(names[0] + (names.Count > 1 ? $"  +{names.Count - 1}" : ""), 12, 1, _palette.Text, FontWeights.Medium));
                if (!Compact || opened) text.Children.Add(Lines(string.Join(", ", names.Skip(1).Take(3)), 11, opened ? 4 : 1, _palette.Dim, FontWeights.Normal));
                first.Children.Add(text);
                panel.Children.Add(first);
                return panel;
            }
            default:
                return Lines(entry.Text.TrimStart(), 12, opened ? 8 : Compact ? 1 : 3, _palette.Text, FontWeights.Normal, ClipboardHistorySupport.RowPreview(entry.Text.TrimStart()));
        }
    }

    private static TextBlock Lines(string text, double size, int lines, Brush brush, FontWeight weight, string? shown = null)
    {
        var height = Math.Ceiling(size * 1.34);
        return new TextBlock
        {
            Text = (shown ?? text).ReplaceLineEndings(lines == 1 ? " " : "\n"), FontSize = size, FontWeight = weight, Foreground = brush,
            TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = height, MaxHeight = height * lines + 0.5,
        };
    }

    // MARK: Choosing

    private void Select(Guid id, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) && _anchor is { } anchor)
        {
            var from = _shown.ToList().FindIndex(e => e.Id == anchor);
            var to = _shown.ToList().FindIndex(e => e.Id == id);
            if (from >= 0 && to >= 0)
            {
                _marked.Clear();
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) _marked.Add(_shown[i].Id);
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_marked.Remove(id)) _marked.Add(id);
            _anchor = id;
        }
        else { _marked.Clear(); _anchor = id; }
        _selected = id;
        Render();
    }

    /// <summary>What Return acts on: the marked cards in list order, or the highlighted one.</summary>
    private IReadOnlyList<ClipboardEntry> Targets()
    {
        if (_marked.Count > 0) return _shown.Where(e => _marked.Contains(e.Id)).ToList();
        return _shown.Where(e => e.Id == _selected).ToList();
    }

    private void Choose(IReadOnlyList<ClipboardEntry> entries, bool paste)
    {
        if (entries.Count > 0) Chosen?.Invoke(entries, paste);
    }

    private void Step(int direction)
    {
        if (_shown.Count == 0) return;
        var at = _shown.ToList().FindIndex(e => e.Id == _selected);
        var next = Math.Clamp(at < 0 ? 0 : at + direction, 0, _shown.Count - 1);
        _marked.Clear();
        _selected = _shown[next].Id;
        _anchor = _selected;
        // A compact card opens when the highlight rests on it, longer than a hover.
        _open = null;
        _hoverPending = _selected;
        if (Compact) { _dwell.Stop(); _dwell.Interval = KeyDwell; _dwell.Start(); }
        Render();
    }

    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => null,
    };

    /// <summary>A key pressed while the list is on screen. True when it was used. Esc is the host's unless a search is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!_visible) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        switch (key)
        {
            case Key.Escape when _searchBox.Visibility == Visibility.Visible || _query.Length > 0:
                ResetSearch();
                Render();
                return true;
            case Key.Down or Key.Up:
                Step(key == Key.Down ? 1 : -1);
                return true;
            case Key.N when ctrl: Step(1); return true;
            case Key.P when ctrl: Step(-1); return true;
            case Key.Enter or Key.Return:
                Choose(Targets(), paste: !modifiers.HasFlag(ModifierKeys.Shift));
                return true;
            case Key.P when modifiers.HasFlag(ModifierKeys.Alt):
                foreach (var entry in Targets()) _history.TogglePin(entry.Id);
                return true;
            case Key.Delete:
                _history.Delete(Targets().Select(t => t.Id));
                return true;
        }
        if (ctrl && DigitOf(key) is { } digit && ClipboardHistorySupport.PositionForDigit(digit, _shown.Count) is { } at)
        {
            Choose([_shown[at]], paste: !modifiers.HasFlag(ModifierKeys.Shift));
            return true;
        }
        return false;
    }

    /// <summary>Typing with nothing else focused opens the search box with what was typed.</summary>
    public bool HandleText(string text)
    {
        if (!_visible || _search.IsKeyboardFocused || string.IsNullOrEmpty(text) || text.Any(char.IsControl)) return false;
        _searchBox.Visibility = Visibility.Visible;
        _searchHint.Visibility = Visibility.Collapsed;
        _search.Text += text;
        _search.Focus();
        _search.CaretIndex = _search.Text.Length;
        return true;
    }
}
