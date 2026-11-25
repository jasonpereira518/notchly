// SPDX-License-Identifier: GPL-3.0-or-later
// The panel's Quick toggles: the Windows counterpart of upstream's UI/MenuPanel/QuickTogglesSection.swift. A grid of
// tiles for the rows the user kept, in their order; a tile with a readable state (dark mode, hidden files, the radios)
// is lit while it is on, a running one dims, and a failure shows for a moment.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Notchly.App.Tools;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed class PanelTools(StackPanel host, SettingsStore settings)
{
    private QuickTogglesService? _service;

    private static System.Windows.Media.FontFamily IconFont => (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"];

    public void Show(QuickTogglesService? service)
    {
        Hide();
        _service = service;
        if (service is null || !settings.GetBool(DefaultsKey.PanelShowToggles)) { host.Children.Clear(); return; }
        service.Changed += Rebuild;
        service.Refresh();
        Rebuild();
    }

    public void Hide()
    {
        if (_service is not null) _service.Changed -= Rebuild;
        _service = null;
    }

    private void Rebuild()
    {
        host.Children.Clear();
        if (_service is not { } service) return;
        var rows = QuickToggleSupport.VisibleRows(settings).Where(service.IsSupported).ToList();
        if (rows.Count == 0) return;
        host.Children.Add(new TextBlock { Text = "Quick toggles", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var id in rows)
        {
            var info = QuickTogglesService.All.First(i => i.Id == id);
            grid.Children.Add(Tile(service, info));
        }
        host.Children.Add(grid);
    }

    private static FrameworkElement Tile(QuickTogglesService service, ToggleInfo info)
    {
        var state = service.StateOf(info.Id);
        var on = service.IsOn(info.Id) == true;
        var subtitle = state switch
        {
            QuickToggleSupport.RunState.Failed => "Failed",
            QuickToggleSupport.RunState.NeedsPermission => "Needs permission",
            _ => info.Id == "refreshRate" ? service.RefreshText : "",
        };
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = info.Glyph, FontFamily = IconFont, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = info.Title, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 10, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
        });
        var tile = new Button
        {
            Content = content, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(4, 10, 4, 8), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 66,
            Appearance = state == QuickToggleSupport.RunState.Failed ? ControlAppearance.Danger : on ? ControlAppearance.Primary : ControlAppearance.Secondary,
            Opacity = state == QuickToggleSupport.RunState.Running ? 0.5 : 1,
            ToolTip = info.Title,
        };
        tile.Click += (_, _) => service.Run(info.Id);
        return tile;
    }
}
