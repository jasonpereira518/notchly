// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools and Settings → Mouse: the Windows counterparts of upstream's QuickToolsSettings.swift (quick toggles,
// quit apps) and MouseSettings.swift (focus follows mouse, pointer acceleration). Upstream's strings, with Windows'
// names for its controls.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Tools;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string TogglesNote = "Tiles in the panel for the things you reach for most. Turn a tile off to hide it, and use the arrows to put them in your order.";
    private const string QuitNote = "Quit apps lists every open app and asks each one to close, so unsaved work still gets asked about. Apps below are left alone by \"Quit all\".";
    private const string FocusNote = "Windows brings the window under the pointer forward after a pause. Notchly turns that on and puts your own setting back when you turn it off or quit.";
    private const string AccelerationNote = "Turns off \"Enhance pointer precision\" so the pointer moves the same distance whatever the speed. Notchly puts your own setting back when you turn it off or quit.";

    private FrameworkElement ToolsPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Tools"));

        page.Children.Add(Section("Quick toggles"));
        page.Children.Add(Caption(TogglesNote));
        if (!settings.IsAvailable(AppFeature.QuickToggles)) page.Children.Add(Caption("Install Quick toggles in Features to use them."));
        page.Children.Add(Toggle("Show them in the panel", null, DefaultsKey.PanelShowToggles, ""));
        var order = QuickToggleSupport.SanitizedOrder(settings.GetString(DefaultsKey.PanelToggleOrder)).ToList();
        for (var i = 0; i < order.Count; i++) page.Children.Add(ToggleRow(order, i));
        page.Children.Add(Card("Never eject", "Drive names or letters to leave out of \"Eject drives\", separated by commas.", "", ExcludedDrivesBox()));
        page.Children.Add(ActionButton("Reset tiles", () =>
        {
            settings.Set(DefaultsKey.PanelToggleOrder, "");
            foreach (var id in QuickToggleSupport.DefaultOrder) settings.Set(QuickToggleSupport.VisibilityKey(id), true);
            Refresh();
        }));

        page.Children.Add(Section("Quit apps"));
        page.Children.Add(Caption(QuitNote));
        if (!settings.IsAvailable(AppFeature.QuitApps)) page.Children.Add(Caption("Install Quit apps in Features to use it."));
        page.Children.Add(Toggle("Open it with a shortcut", null, DefaultsKey.QuitAppsShortcutEnabled, ""));
        page.Children.Add(Card("Quit all", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.QuitAppsShortcut)));
        page.Children.Add(Toggle("Open it with a second shortcut, resting on \"except the front app\"", null, DefaultsKey.QuitAppsExceptFrontShortcutEnabled, ""));
        page.Children.Add(Card("Quit all except the front app", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.QuitAppsExceptFrontShortcut)));
        page.Children.Add(Card("Kept apps", "Program names \"Quit all\" leaves open, separated by commas. Explorer is always kept.", "", KeptAppsBox()));
        AddCommandBarSection(page);
        AddScreenToolSections(page);
        return page;
    }

    private CardControl ToggleRow(List<string> order, int index)
    {
        var id = order[index];
        var info = QuickTogglesService.All.First(i => i.Id == id);
        var key = QuickToggleSupport.VisibilityKey(id);
        var toggle = new ToggleSwitch { IsChecked = app.Settings.GetBool(key), Margin = new Thickness(8, 0, 0, 0) };
        toggle.Click += (_, _) => app.Settings.Set(key, toggle.IsChecked == true);
        Button Arrow(string glyph, int to)
        {
            var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), FontSize = 12 }, Appearance = ControlAppearance.Transparent, Padding = new Thickness(8), IsEnabled = to >= 0 && to < order.Count };
            button.Click += (_, _) => Move(order, index, to);
            return button;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Arrow("", index - 1));
        row.Children.Add(Arrow("", index + 1));
        row.Children.Add(toggle);
        var gate = QuickToggleSupport.GatedBy(id);
        var note = gate is AppFeature.QuickToggles ? null : app.Settings.IsAvailable(gate) ? null : $"Needs {FeatureCatalog.Info(gate).Title} installed.";
        return Card(info.Title, note, info.Glyph, row);
    }

    private void Move(List<string> order, int from, int to)
    {
        if (to < 0 || to >= order.Count) return;
        (order[from], order[to]) = (order[to], order[from]);
        app.Settings.Set(DefaultsKey.PanelToggleOrder, string.Join(",", order));
        Refresh();
    }

    private TextBox ExcludedDrivesBox()
    {
        var box = new TextBox { PlaceholderText = "Backup, F:", MinWidth = 240, Text = string.Join(", ", app.Settings.GetList(DefaultsKey.DiskEjectExcludedVolumes)) };
        box.LostFocus += (_, _) => app.Settings.SetList(DefaultsKey.DiskEjectExcludedVolumes,
            box.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return box;
    }

    private TextBox KeptAppsBox()
    {
        var kept = QuitAppsSupport.SanitizedExceptions(app.Settings.GetList(DefaultsKey.QuitAppsKeptApps)).Skip(QuitAppsSupport.MandatoryExceptions.Length);
        var box = new TextBox { PlaceholderText = "code.exe, spotify.exe", MinWidth = 240, Text = string.Join(", ", kept) };
        box.LostFocus += (_, _) =>
        {
            var names = box.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe");
            app.Settings.SetList(DefaultsKey.QuitAppsKeptApps, QuitAppsSupport.SanitizedExceptions(names));
        };
        return box;
    }

    private FrameworkElement MousePage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Mouse"));
        page.Children.Add(Section("Focus follows mouse"));
        page.Children.Add(Caption(FocusNote));
        if (!settings.IsAvailable(AppFeature.FocusFollowsMouse)) page.Children.Add(Caption("Install Focus follows mouse in Features to use it."));
        page.Children.Add(Toggle("Bring the window under the pointer forward", null, DefaultsKey.FocusFollowsMouseEnabled, ""));
        page.Children.Add(Toggle("Raise it above other windows", "Off: it takes keyboard focus but stays where it is.", DefaultsKey.FocusFollowsMouseRaise, ""));
        page.Children.Add(Card("After a pause of", null, "",
            AgentSettings.NumberChoice(settings, DefaultsKey.FocusFollowsMouseDelayMilliseconds, [100, 250, 500, 1000, 2000, 3000], 250, v => $"{v:0} ms")));

        page.Children.Add(Section("Pointer acceleration"));
        page.Children.Add(Caption(AccelerationNote));
        if (!settings.IsAvailable(AppFeature.MouseAcceleration)) page.Children.Add(Caption("Install Pointer acceleration in Features to use it."));
        page.Children.Add(Toggle("Turn off pointer acceleration", null, DefaultsKey.MouseAccelerationDisabled, ""));
        AddMouseInputSections(page);
        return page;
    }

    /// <summary>Builds the current page again, so a reorder shows at once.</summary>
    private void Refresh() => app.RefreshSettingsPage();
}
