// SPDX-License-Identifier: GPL-3.0-or-later
// The Settings window: a sidebar of pages, as upstream's NavigationSplitView
// (UI/Settings/SettingsDirectory.swift), restricted to what this build ports.
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

public enum SettingsPage { General, Features, Island, Sound, Monitor, Energy, Windows, Tools, Processes, Mouse, Keyboard, About }

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
        if (Navigation.SelectedItem is not NavItem item) return;
        PageHost.Content = _pages.Build(item.Page);
        Scroller.ScrollToTop();
    }
}
