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
    private readonly ShelfTilesView _tiles;

    /// <summary>True while a tile is being dragged out, so the island ignores its own drop.</summary>
    public bool DraggingOut => _tiles.DraggingOut;

    public FilesPage(NotchWindow window, ShelfService shelf, SettingsStore settings, Action<bool> setModal, Action collapse)
    {
        _window = window;
        _shelf = shelf;
        _settings = settings;
        _setModal = setModal;
        _collapse = collapse;
        _tiles = new ShelfTilesView(shelf, settings, () => window.Handle, window, collapse);
        window.FilesClear.Click += (_, _) =>
        {
            if (_shelf.Store.Selection.Count > 0) _shelf.Remove(_shelf.Store.Selection.ToList());
            else _shelf.ClearAll();
        };
        window.FilesShare.Click += (_, _) => _tiles.Share(_shelf.Store.ActionTargets);
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
        _tiles.Render(_window.FilesTiles);
    }

    /// <summary>The page fills the preset's height; tiles flow in as many rows as fit.</summary>
    public void Layout(double height)
    {
        _window.FilesPage.Height = height;
        var rows = ShelfLayout.RowCount(Math.Max(0, height - 40));
        _window.FilesTiles.Height = rows * (ShelfLayout.TileHeight + ShelfLayout.Spacing);
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
