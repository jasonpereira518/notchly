// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Files page: the Windows counterpart of upstream UI/Notch/NotchFilesView.swift and
// UI/Shelf/ShelfTilesView.swift (sideways tiles 78×88, selection, piles, pins, menu, drag out).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Shelf;

namespace Notchly.App.Island.Files;

public sealed class FilesPage
{
    private static readonly SolidColorBrush Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush SelectedBack = Frozen(Color.FromArgb(0x29, 0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush Well = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private readonly NotchWindow _window;
    private readonly ShelfService _shelf;
    private readonly SettingsStore _settings;
    private readonly Action<bool> _setModal;
    private readonly Action _collapse;
    private Point _pressPoint;
    private ShelfItem? _pressItem;
    private bool _dragStarted;

    /// <summary>True while a tile is being dragged out, so the island ignores its own drop.</summary>
    public bool DraggingOut { get; private set; }

    public FilesPage(NotchWindow window, ShelfService shelf, SettingsStore settings, Action<bool> setModal, Action collapse)
    {
        _window = window;
        _shelf = shelf;
        _settings = settings;
        _setModal = setModal;
        _collapse = collapse;
        window.FilesClear.Click += (_, _) =>
        {
            if (_shelf.Store.Selection.Count > 0) _shelf.Remove(_shelf.Store.Selection.ToList());
            else _shelf.ClearAll();
        };
        window.FilesShare.Click += (_, _) => Share(_shelf.Store.ActionTargets);
        window.FilesZip.Click += async (_, _) => await CreateZipAsync();
        // The vertical wheel scrolls the sideways strip.
        window.FilesScroller.PreviewMouseWheel += (_, e) =>
        {
            window.FilesScroller.ScrollToHorizontalOffset(window.FilesScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        shelf.Changed += Render;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Render()
    {
        var store = _shelf.Store;
        var selected = store.Selection.Count;
        _window.FilesHint.Text = selected > 0 ? $"{selected} selected" : "Click to select. Drag out to use or right-click for more actions.";
        _window.FilesClear.Content = selected > 0 ? "Remove selected" : "Clear all";
        var empty = store.Items.Count == 0;
        _window.FilesEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _window.FilesScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _window.FilesZip.IsEnabled = _window.FilesShare.IsEnabled = _window.FilesClear.IsEnabled = !empty;
        _window.FilesTiles.Children.Clear();
        foreach (var item in store.Visible) _window.FilesTiles.Children.Add(Tile(item));
    }

    /// <summary>The page fills the preset's height; tiles flow in as many rows as fit.</summary>
    public void Layout(double height)
    {
        _window.FilesPage.Height = height;
        var rows = ShelfLayout.RowCount(Math.Max(0, height - 40));
        _window.FilesTiles.Height = rows * (ShelfLayout.TileHeight + ShelfLayout.Spacing);
    }

    // MARK: Tiles

    private FrameworkElement Tile(ShelfItem item)
    {
        var store = _shelf.Store;
        var selected = store.Selection.Contains(item.Id);
        var pinned = store.Pinned.Contains(item.Id);
        var tile = new Border
        {
            Width = ShelfLayout.TileWidth, Height = ShelfLayout.TileHeight, CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, ShelfLayout.Spacing, ShelfLayout.Spacing),
            Background = selected ? SelectedBack : Brushes.Transparent,
            BorderBrush = selected ? Accent : Brushes.Transparent, BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand, Tag = item,
            ToolTip = Tooltip(item),
        };
        var grid = new Grid();
        tile.Child = grid;

        var well = new Border
        {
            Width = 64, Height = 50, CornerRadius = new CornerRadius(6), Background = Well,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0),
        };
        var face = item.Kind == ShelfKind.Batch ? item.Leaves().First() : item;
        var glyph = new TextBlock
        {
            Text = face.Kind switch { ShelfKind.Text => "", ShelfKind.Link => "", _ => "" },
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 20, Foreground = Dim,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4) };
        well.Child = new Grid { Children = { glyph, image } };
        if (face.Kind == ShelfKind.File && face.Path is { } path) _ = LoadThumbnail(image, glyph, path);
        grid.Children.Add(well);

        grid.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 28, Margin = new Thickness(4, 58, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        if (item.Kind == ShelfKind.Batch)
        {
            grid.Children.Add(new Border
            {
                Background = Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0, 5, 0), Height = 16,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 0, 0, 0),
                Child = new TextBlock { Text = item.LeafCount.ToString(), FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = store.Expanded.Contains(item.Id) ? "Double-click to collapse" : "Double-click to expand",
            });
        }
        if (selected)
        {
            grid.Children.Add(Badge("", Accent, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(2, 0, 0, 2)));
        }
        // A pin badge that becomes a remove button on hover; unpinned tiles show the button on hover only.
        var corner = Badge(pinned ? "" : "", Frozen(Color.FromArgb(0xCC, 0x30, 0x30, 0x30)),
                           HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 0, 0, 0));
        corner.Visibility = pinned ? Visibility.Visible : Visibility.Hidden;
        corner.Cursor = Cursors.Arrow;
        corner.MouseLeftButtonUp += (_, e) => { e.Handled = true; _shelf.Remove([item.Id]); };
        tile.MouseEnter += (_, _) => { corner.Visibility = Visibility.Visible; ((TextBlock)corner.Child).Text = ""; };
        tile.MouseLeave += (_, _) =>
        {
            ((TextBlock)corner.Child).Text = pinned ? "" : "";
            corner.Visibility = pinned ? Visibility.Visible : Visibility.Hidden;
        };
        grid.Children.Add(corner);

        tile.MouseLeftButtonDown += (_, e) =>
        {
            _pressPoint = e.GetPosition(_window);
            _pressItem = item;
            _dragStarted = false;
            if (e.ClickCount == 2 && item.Kind == ShelfKind.Batch) { _shelf.ToggleExpanded(item.Id); _pressItem = null; }
            e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (_pressItem != item || e.LeftButton != MouseButtonState.Pressed || _dragStarted) return;
            var delta = e.GetPosition(_window) - _pressPoint;
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
            _dragStarted = true;
            DragOut(tile, item);
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressItem != item || _dragStarted) return;
            _pressItem = null;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _shelf.ExtendSelection(item.Id);
            else _shelf.ToggleSelection(item.Id);
            e.Handled = true;
        };
        tile.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(tile, item); };
        return tile;
    }

    private static Border Badge(string glyph, Brush background, HorizontalAlignment h, VerticalAlignment v, Thickness margin) => new()
    {
        Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = background, HorizontalAlignment = h, VerticalAlignment = v,
        Margin = margin,
        Child = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static async Task LoadThumbnail(Image image, TextBlock glyph, string path)
    {
        var source = await ShellThumbnails.GetAsync(path, 96);
        if (source is null) return;
        image.Source = source;
        glyph.Visibility = Visibility.Collapsed;
    }

    private static string Tooltip(ShelfItem item) => item.Kind switch
    {
        ShelfKind.File => ShelfTooltip.ForFile(item.Title, item.Path is { } p ? ShellActions.TypeName(p) : null),
        ShelfKind.Text => ShelfTooltip.ForText(item.Text ?? ""),
        ShelfKind.Link => ShelfTooltip.ForLink(item.Url ?? ""),
        _ => ShelfTooltip.ForPile(item.Leaves()),
    };

    // MARK: Drag out

    private void DragOut(FrameworkElement tile, ShelfItem item)
    {
        var store = _shelf.Store;
        // Dragging a selected tile drags the whole selection.
        IReadOnlyList<ShelfItem> dragged = store.Selection.Contains(item.Id) ? store.ActionTargets : [item];
        var removeAfterDrop = _settings.GetBool(DefaultsKey.ShelfRemoveAfterDrop);
        var protectedIds = store.ProtectedIds;
        var includesPinned = dragged.Any(d => protectedIds.Contains(d.Id));
        var effects = ShelfInteraction.OffersMoveOutside(removeAfterDrop, includesPinned)
            ? DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link
            : DragDropEffects.Copy | DragDropEffects.Link;
        DraggingOut = true;
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(tile, _shelf.DragData(dragged), effects); }
        finally { DraggingOut = false; }
        var accepted = result != DragDropEffects.None;
        _shelf.CompleteDrag(dragged, accepted, removeAfterDrop);
        if (ShelfInteraction.ShouldCloseAfterDrag(accepted, dragged.Count, _settings.GetBool(DefaultsKey.ShelfCloseAfterDrop), pinned: false))
            _collapse();
    }

    // MARK: Actions

    private void ShowMenu(FrameworkElement tile, ShelfItem item)
    {
        var store = _shelf.Store;
        var targets = store.Selection.Contains(item.Id) ? store.ActionTargets : [item];
        var leaves = ShelfStore.LeavesOf(targets);
        var files = leaves.Where(l => l.Kind == ShelfKind.File && l.Path is not null).Select(l => l.Path!).ToList();
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
            return entry;
        }
        var pinned = store.Pinned.Contains(item.Id);
        Item(pinned ? "Unpin" : "Pin", () => { foreach (var t in targets) _shelf.TogglePin(t.Id); });
        menu.Items.Add(new Separator());
        Item("Open", () =>
        {
            foreach (var leaf in leaves)
            {
                if (leaf.Kind == ShelfKind.File && leaf.Path is { } p) ShellActions.Open(p);
                else if (leaf.Kind == ShelfKind.Link && leaf.Url is { } u) ShellActions.Open(u);
            }
        }, leaves.Any(l => l.Kind is ShelfKind.File or ShelfKind.Link));
        Item("Open with…", () => ShellActions.OpenWith(files[0]), files.Count == 1);
        Item("Share", () => Share(targets));
        Item("Show in Explorer", () => ShellActions.Reveal(files[0]), files.Count > 0);
        menu.Items.Add(new Separator());
        Item("Remove", () => _shelf.Remove(targets.Select(t => t.Id).ToList()));
        menu.PlacementTarget = tile;
        menu.IsOpen = true;
    }

    private void Share(IReadOnlyList<ShelfItem> targets)
    {
        var leaves = ShelfStore.LeavesOf(targets);
        var files = leaves.Where(l => l.Kind == ShelfKind.File && l.Path is not null).Select(l => l.Path!).ToList();
        var texts = leaves.Where(l => l.Kind == ShelfKind.Text).Select(l => l.Text!).ToList();
        var link = leaves.FirstOrDefault(l => l.Kind == ShelfKind.Link)?.Url;
        ShellActions.Share(_window.Handle, files, texts.Count > 0 ? string.Join(Environment.NewLine, texts) : null,
                           link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri : null);
    }

    private async Task CreateZipAsync()
    {
        _setModal(true);
        try
        {
            if (await _shelf.CreateZipsAsync(_shelf.Store.ActionTargets, _window) is { } problem) _window.FilesHint.Text = problem;
        }
        finally { _setModal(false); }
    }
}
