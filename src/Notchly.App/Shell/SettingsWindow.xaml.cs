// SPDX-License-Identifier: GPL-3.0-or-later
// The Settings window: a sidebar of pages, as upstream's NavigationSplitView
// (UI/Settings/SettingsDirectory.swift), restricted to what this build ports.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

public enum SettingsPage { General, Features, Island, Sound, About }

public partial class SettingsWindow : FluentWindow
{
    private sealed record NavItem(SettingsPage Page, string Title, string Glyph);

    private static readonly NavItem[] Items =
    [
        new(SettingsPage.General, "General", ""),
        new(SettingsPage.Features, "Features", ""),
        new(SettingsPage.Island, "Dynamic Island", ""),
        new(SettingsPage.Sound, "Sound", ""),
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
