// SPDX-License-Identifier: GPL-3.0-or-later
// The Settings window: a sidebar of pages, as upstream's NavigationSplitView
// (UI/Settings/SettingsDirectory.swift), restricted to what this build ports.
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Islet.App.Shell;

public enum SettingsPage { General, Features, Island, About }

public partial class SettingsWindow : FluentWindow
{
    private sealed record NavItem(SettingsPage Page, string Title, string Glyph);

    private static readonly NavItem[] Items =
    [
        new(SettingsPage.General, "General", ""),
        new(SettingsPage.Features, "Features", ""),
        new(SettingsPage.Island, "Dynamic Island", ""),
        new(SettingsPage.About, "About", ""),
    ];

    private readonly SettingsPages _pages;

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _pages = new SettingsPages(app, new SettingsProxy(app.Settings, Dispatcher));
        Navigation.ItemsSource = Items;
        SystemThemeWatcher.Watch(this);
    }

    public void Navigate(SettingsPage page) => Navigation.SelectedItem = Items.First(i => i.Page == page);

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not NavItem item) return;
        PageHost.Content = _pages.Build(item.Page);
        Scroller.ScrollToTop();
    }
}
