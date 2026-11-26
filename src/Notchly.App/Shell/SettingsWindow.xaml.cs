// SPDX-License-Identifier: GPL-3.0-or-later
// The Settings window: a sidebar of pages, as upstream's NavigationSplitView
// (UI/Settings/SettingsDirectory.swift), restricted to what this build ports.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

public enum SettingsPage { General, Features, Island, Sound, Monitor, Energy, Windows, Tools, Processes, Mouse, Keyboard, Clipboard, About }

public partial class SettingsWindow : FluentWindow
{
    private sealed record NavItem(SettingsPage Page, string Title, string Glyph);

    private static readonly NavItem[] Items =
    [
        new(SettingsPage.General, "General", ""),
        new(SettingsPage.Features, "Features", ""),
        new(SettingsPage.Island, "Dynamic Island", ""),
        new(SettingsPage.Sound, "Sound", ""),
        new(SettingsPage.Monitor, "System monitor", "\uE9D9"),
        new(SettingsPage.Energy, "Energy", "\uE945"),
        new(SettingsPage.Windows, "Windows", "\uE7C4"),
        new(SettingsPage.Tools, "Tools", "\uE90F"),
        new(SettingsPage.Processes, "Processes and ports", "\uE9D9"),
        new(SettingsPage.Mouse, "Mouse", "\uE962"),
        new(SettingsPage.Keyboard, "Keyboard", "\uE765"),
        new(SettingsPage.Clipboard, "Clipboard and files", "\uE77F"),
        new(SettingsPage.About, "About", ""),
    ];

    private readonly SettingsPages _pages;
    private readonly SettingsSearch _search;

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _pages = new SettingsPages(app, new SettingsProxy(app.Settings, Dispatcher));
        _search = new SettingsSearch(_pages, page => Items.First(i => i.Page == page).Title, OpenFromSearch);
        Navigation.ItemsSource = Items;
        SystemThemeWatcher.Watch(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        };
    }

    /// <summary>Builds the page again where it stands, so a change to its own rows shows at once.</summary>
    public void Rebuild()
    {
        if (Navigation.SelectedItem is not NavItem item) return;
        var offset = Scroller.VerticalOffset;
        PageHost.Content = _pages.Build(item.Page);
        Scroller.UpdateLayout();
        Scroller.ScrollToVerticalOffset(offset);
    }

    public void Navigate(SettingsPage page) => Navigation.SelectedItem = Items.First(i => i.Page == page);

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        // Picking a page ends a search; clearing the box shows the page.
        if (SearchBox.Text.Length > 0) { SearchBox.Clear(); return; }
        ShowSelectedPage();
    }

    /// <summary>Clicking the page that is already selected leaves a search too (no selection change reports it).</summary>
    private void OnNavigationClicked(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.Text.Length > 0 && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Navigation, source) is ListBoxItem)
            SearchBox.Clear();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_search.Show(SearchBox.Text))
        {
            PageHost.Content = _search.Results;
            Scroller.ScrollToTop();
        }
        else
        {
            ShowSelectedPage();
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    /// <summary>A page heading in the results: open that page and leave the search.</summary>
    private void OpenFromSearch(SettingsPage page)
    {
        Navigate(page);
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
    }

    private void ShowSelectedPage()
    {
        if (Navigation.SelectedItem is not NavItem item) return;
        PageHost.Content = _pages.Build(item.Page);
        Scroller.ScrollToTop();
    }
}
