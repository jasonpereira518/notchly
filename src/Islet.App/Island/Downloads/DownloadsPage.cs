// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Downloads page: the Windows counterpart of upstream UI/Notch/NotchDownloadsView.swift.
// The watched folder heads the page with its menu; below, a card for each transfer still arriving
// (size and speed, since Windows browsers publish no percentage) and for the folder's newest files.
// Cards are kept by id and updated in place, so a progress tick never rebuilds the list.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Islet.App.Island.Files;
using Islet.App.Platform.Shell;
using Islet.Core.Downloads;
using Islet.Core.Settings;

namespace Islet.App.Island.Downloads;

public sealed class DownloadsPage
{
    private static readonly SolidColorBrush Card = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Track = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly SolidColorBrush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private readonly NotchWindow _window;
    private readonly DownloadService _downloads;
    private readonly SettingsStore _settings;
    private readonly Func<ShelfService?> _shelf;
    private readonly Action<bool> _setModal;
    private readonly Action _reopen;
    private readonly Dictionary<string, CardView> _cards = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = [];
    private bool _visible;

    private sealed class CardView
    {
        public required Border Root;
        public required TextBlock Icon, Name, Percent, Detail;
        public required ProgressBar Bar;
        public required StackPanel Actions;
        public required Button NameButton;
        public DownloadItem? Item;
    }

    public DownloadsPage(NotchWindow window, DownloadService downloads, SettingsStore settings, Func<ShelfService?> shelf,
                         Action<bool> setModal, Action reopen)
    {
        _window = window;
        _downloads = downloads;
        _settings = settings;
        _shelf = shelf;
        _setModal = setModal;
        _reopen = reopen;
        window.DownloadsFolder.Click += (_, _) => ShowFolderMenu();
        window.DownloadsChoose.Click += (_, _) => ChooseFolder();
        downloads.Changed += Render;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    /// <summary>The page renders only while it is on screen; showing it renders it at once.</summary>
    public bool Visible
    {
        get => _visible;
        set { _visible = value; if (value) Render(); }
    }

    /// <summary>The page fills the preset's height; the list scrolls inside it.</summary>
    public void Layout(double height) => _window.DownloadsPage.Height = height;

    public void Render()
    {
        if (!_visible) return;
        var w = _window;
        w.DownloadsFolderName.Text = _downloads.FolderName ?? "Downloads";
        w.DownloadsFolder.ToolTip = _downloads.Folder ?? DownloadService.ResolveFolder(_settings);
        var items = _downloads.Items;
        var unavailable = _downloads.FolderUnavailable;
        var empty = unavailable || items.Count == 0;
        w.DownloadsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        w.DownloadsScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        w.DownloadsEmptyGlyph.Text = unavailable ? "" : "";
        w.DownloadsEmptyGlyph.Foreground = unavailable ? Orange : Dim;
        w.DownloadsEmptyText.Text = unavailable ? "This folder is unavailable. Choose it again to restore access." : "No files in this folder";
        w.DownloadsEmptyText.Foreground = unavailable ? Orange : Dim;
        w.DownloadsChoose.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            _cards.Clear();
            _order = [];
            w.DownloadsList.Children.Clear();
            return;
        }

        var order = items.Select(i => i.Id).ToList();
        foreach (var gone in _cards.Keys.Except(order, StringComparer.OrdinalIgnoreCase).ToList()) _cards.Remove(gone);
        foreach (var item in items)
        {
            if (!_cards.TryGetValue(item.Id, out var card)) _cards[item.Id] = card = NewCard();
            if (card.Item != item) Update(card, item);
        }
        if (!order.SequenceEqual(_order, StringComparer.OrdinalIgnoreCase))
        {
            _order = order;
            w.DownloadsList.Children.Clear();
            foreach (var id in order) w.DownloadsList.Children.Add(_cards[id].Root);
        }
    }

    // MARK: Cards

    private CardView NewCard()
    {
        var icon = new TextBlock { FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        label.Children.Add(icon);
        label.Children.Add(name);
        var nameButton = new Button
        {
            Style = (Style)_window.Resources["PlainButton"], Content = label, MinHeight = 26,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var percent = new TextBlock { FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(percent, 1);
        Grid.SetColumn(actions, 2);
        top.Children.Add(nameButton);
        top.Children.Add(percent);
        top.Children.Add(actions);

        var bar = new ProgressBar
        {
            Height = 4, MinHeight = 4, Minimum = 0, Maximum = 1, Foreground = Brushes.White, Background = Track,
            BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
        };
        var detail = new TextBlock { FontSize = 10, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel { Children = { top, bar, detail } };
        var root = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 7, 10, 8),
            Margin = new Thickness(0, 0, 0, 8), Child = stack,
        };
        var view = new CardView { Root = root, Icon = icon, Name = name, Percent = percent, Detail = detail, Bar = bar, Actions = actions, NameButton = nameButton };
        nameButton.Click += (_, _) => { if (view.Item is { Completed: true } item) ShellActions.Open(item.Path); };
        return view;
    }

    private void Update(CardView card, DownloadItem item)
    {
        var wasCompleted = card.Item?.Completed;
        card.Item = item;
        card.Name.Text = item.Name;
        card.Root.ToolTip = item.Name;
        card.NameButton.IsHitTestVisible = item.Completed;
        card.NameButton.ToolTip = item.Completed ? "Open" : null;
        if (item.Completed)
        {
            card.Icon.Text = "";
            card.Icon.Foreground = Green;
            card.Percent.Visibility = Visibility.Collapsed;
            card.Bar.Visibility = Visibility.Collapsed;
            card.Detail.Text = item.ReceivedBytes is { } bytes ? $"Saved · {DownloadSupport.FormatBytes(bytes)}" : "Saved";
        }
        else
        {
            card.Icon.Text = "";
            card.Icon.Foreground = Brushes.White;
            card.Percent.Text = item.Fraction is { } fraction ? $"{Math.Round(fraction * 100):0}%" : "";
            card.Percent.Visibility = item.Fraction is null ? Visibility.Collapsed : Visibility.Visible;
            card.Bar.Visibility = item.Fraction is not null || item.Active ? Visibility.Visible : Visibility.Collapsed;
            card.Bar.IsIndeterminate = item.Fraction is null;
            if (item.Fraction is { } value) card.Bar.Value = value;
            card.Detail.Text = DownloadSupport.ProgressLine(item);
        }
        if (wasCompleted != item.Completed) BuildActions(card, item);
    }

    private void BuildActions(CardView card, DownloadItem item)
    {
        card.Actions.Children.Clear();
        if (!item.Completed) return;
        Button Action(string glyph, string tip, Action run)
        {
            var button = new Button { Style = (Style)_window.Resources["IconButton"], Content = glyph, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
            button.Click += (_, _) => { if (card.Item is { } current) run(); };
            card.Actions.Children.Add(button);
            return button;
        }
        Action("", "Show in Explorer", () => ShellActions.Reveal(card.Item!.Path));
        if (_shelf() is not null)
            Action("", "Add to Files", () => _shelf()?.AddFiles([card.Item!.Path]));
    }

    // MARK: Folder

    private void ShowFolderMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var entry = new MenuItem { Header = header };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        if (_downloads.Folder is { } folder && !_downloads.FolderUnavailable) Item("Open folder", () => ShellActions.Open(folder));
        Item("Choose folder…", ChooseFolder);
        if (!DownloadService.UsesDefaultFolder(_settings)) Item("Use Downloads folder", () => DownloadService.UseDownloadsFolder(_settings));
        menu.PlacementTarget = _window.DownloadsFolder;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The island stays open while the chooser is up and comes back to this page afterwards.</summary>
    private void ChooseFolder()
    {
        _setModal(true);
        try { DownloadService.ChooseFolder(_settings, _window); }
        finally { _setModal(false); }
        _reopen();
    }
}
