// SPDX-License-Identifier: GPL-3.0-or-later
// The shelf's tiles, shared by the island's Files page and the floating Shelf card: the Windows counterpart of upstream
// UI/Shelf/ShelfTilesView.swift (tiles 78×88, selection, piles, pins, menu, drag out, share). It fills a panel with one tile per
// visible item and acts on the shelf service; the host decides what a finished drag does to its window.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Shelf;

namespace Notchly.App.Island.Files;

public sealed class ShelfTilesView(ShelfService shelf, SettingsStore settings, Func<IntPtr> owner, UIElement positionRoot, Action afterDrop)
{
    private static readonly SolidColorBrush Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush SelectedBack = Frozen(Color.FromArgb(0x29, 0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush Well = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private readonly ShelfService _shelf = shelf;
    private readonly SettingsStore _settings = settings;
    private readonly Func<IntPtr> _owner = owner;
    private readonly UIElement _positionRoot = positionRoot;
    private readonly Action _afterDrop = afterDrop;
    private Point _pressPoint;
    private ShelfItem? _pressItem;
    private bool _dragStarted;

    /// <summary>True while a tile is being dragged out, so the host ignores its own drop.</summary>
    public bool DraggingOut { get; private set; }

    /// <summary>True while a menu is open or a tile is being dragged: the host keeps itself open.</summary>
    public bool Interacting { get; private set; }

    public event Action? InteractionChanged;

    private void SetInteracting(bool value)
    {
        if (Interacting == value) return;
        Interacting = value;
        InteractionChanged?.Invoke();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Fills the panel with a tile for each visible item.</summary>
    public void Render(Panel panel)
    {
        panel.Children.Clear();
        foreach (var item in _shelf.Store.Visible) panel.Children.Add(Tile(item));
    }

    public void Share(IReadOnlyList<ShelfItem> targets)
    {
        var leaves = ShelfStore.LeavesOf(targets);
        var files = leaves.Where(l => l.Kind == ShelfKind.File && l.Path is not null).Select(l => l.Path!).ToList();
        var texts = leaves.Where(l => l.Kind == ShelfKind.Text).Select(l => l.Text!).ToList();
        var link = leaves.FirstOrDefault(l => l.Kind == ShelfKind.Link)?.Url;
        ShellActions.Share(_owner(), files, texts.Count > 0 ? string.Join(Environment.NewLine, texts) : null,
                           link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri : null);
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
            _pressPoint = e.GetPosition(_positionRoot);
            _pressItem = item;
            _dragStarted = false;
            if (e.ClickCount == 2 && item.Kind == ShelfKind.Batch) { _shelf.ToggleExpanded(item.Id); _pressItem = null; }
            e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (_pressItem != item || e.LeftButton != MouseButtonState.Pressed || _dragStarted) return;
            var delta = e.GetPosition(_positionRoot) - _pressPoint;
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
        SetInteracting(true);
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(tile, _shelf.DragData(dragged), effects); }
        finally { DraggingOut = false; SetInteracting(false); }
        var accepted = result != DragDropEffects.None;
        _shelf.CompleteDrag(dragged, accepted, removeAfterDrop);
        if (ShelfInteraction.ShouldCloseAfterDrag(accepted, dragged.Count, _settings.GetBool(DefaultsKey.ShelfCloseAfterDrop), pinned: false))
            _afterDrop();
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
        menu.Opened += (_, _) => SetInteracting(true);
        menu.Closed += (_, _) => SetInteracting(false);
        menu.IsOpen = true;
    }
}
