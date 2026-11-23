// SPDX-License-Identifier: GPL-3.0-or-later
// The flyout above the notification area: the Windows counterpart of upstream's menu-bar popover
// (UI/MenuPanel). Sections appear as their features are ported; for now, the island's controls.
using System.Windows;
using Notchly.App.Island;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

public partial class PanelWindow : FluentWindow
{
    private readonly App _app;
    private bool _updating;

    public PanelWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        SystemThemeWatcher.Watch(this);
    }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Refresh();
        Show();
        PlaceAboveTray();
        Activate();
    }

    private void Refresh()
    {
        _updating = true;
        var installed = _app.Settings.IsAvailable(AppFeature.Notch);
        IslandSection.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        IslandToggle.IsChecked = _app.Settings.GetBool(DefaultsKey.NotchEnabled);
        IslandStatus.Text = _app.Features.Get<IslandService>(AppFeature.Notch)?.StatusText ?? "Install it from Features.";
        var planned = FeatureCatalog.All.Count(i => i.Status is WindowsStatus.Planned or WindowsStatus.NativeToggle);
        ComingSoon.Text = $"Mixer, system monitor, switcher, clipboard and {planned - 4} more features are on the way. See Features for the plan.";
        _updating = false;
    }

    /// <summary>Bottom-right of the work area, above the notification area.</summary>
    private void PlaceAboveTray()
    {
        UpdateLayout();
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 12;
        Top = work.Bottom - ActualHeight - 12;
    }

    private void OnIslandToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.Settings.Set(DefaultsKey.NotchEnabled, IslandToggle.IsChecked == true);
        Dispatcher.BeginInvoke(Refresh);
    }

    private void OnOpenIsland(object sender, RoutedEventArgs e)
    {
        Hide();
        _app.Features.Get<IslandService>(AppFeature.Notch)?.Open(takeFocus: true);
    }

    private void OnQuickTimer(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var minutes))
            _app.Features.Get<IslandService>(AppFeature.Notch)?.StartTimer(minutes);
        Hide();
    }

    private void OnSettings(object sender, RoutedEventArgs e) { Hide(); _app.ShowSettings(SettingsPage.General); }
    private void OnFeatures(object sender, RoutedEventArgs e) { Hide(); _app.ShowSettings(SettingsPage.Features); }
    private void OnQuit(object sender, RoutedEventArgs e) => _app.Quit();
}
