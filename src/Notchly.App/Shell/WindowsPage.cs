// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Windows: the Windows counterpart of upstream's SwitcherSettings.swift and WindowLayoutSettings.swift. The switcher's
// shortcuts and looks, then the layout shortcuts, gaps, directional keys, edge snap and drag gesture. Everything is off until you
// turn it on: these features change what Alt+Tab and the pointer do.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Layout;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Windowing;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string SwitcherNote = "Alt+Tab opens a panel of your windows with live previews. Hold Alt and press Tab to move, Shift to go back, type to search, W closes " +
        "a window and Q quits its app. It cannot reach windows of apps running as administrator unless Notchly does too, and it leaves Alt+Tab alone in full-screen games.";
    private const string LayoutNote = "Place the window in front by shortcut, by dragging with a modifier held, or by dragging a title bar to a screen edge. " +
        "Windows' own snapping also reacts to screen edges: turn it off in Settings → System → Multitasking before using edge snap here.";

    private FrameworkElement WindowsPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Windows"));

        // The switcher
        page.Children.Add(Section("App switcher"));
        page.Children.Add(Caption(SwitcherNote));
        if (!settings.IsAvailable(AppFeature.Switcher)) page.Children.Add(Caption("Install the App switcher in Features to use it."));
        page.Children.Add(Toggle("Turn on the switcher", null, DefaultsKey.SwitcherEnabled, ""));
        page.Children.Add(Toggle("Replace Windows' Alt+Tab", "Off: Alt+Tab stays Windows'; use your own shortcut below.", DefaultsKey.SwitcherTakeOverSystemShortcuts, ""));
        page.Children.Add(Card("All apps", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.SwitcherShortcut)));
        page.Children.Add(Card("This app's windows", "Cycle the windows of the app in front.", "", new ShortcutRecorder(settings, DefaultsKey.SwitcherWindowShortcut)));
        page.Children.Add(Toggle("A shortcut for every window", null, DefaultsKey.SwitcherAllWindowsShortcutEnabled, ""));
        page.Children.Add(Card("Every window", null, "", new ShortcutRecorder(settings, DefaultsKey.SwitcherAllWindowsShortcut)));
        page.Children.Add(Choice("Preview size", null, DefaultsKey.SwitcherPreviewSize, [("small", "Small"), ("normal", "Normal"), ("large", "Large")], ""));
        page.Children.Add(Toggle("Icons only", "A row of app icons instead of window previews.", DefaultsKey.SwitcherIconRowMode, ""));
        page.Children.Add(Toggle("One entry per app", null, DefaultsKey.SwitcherMergeTabs, ""));
        page.Children.Add(Choice("Minimized windows", null, DefaultsKey.SwitcherMinimizedPlacement, [("normal", "Where they were"), ("end", "At the end"), ("hidden", "Hide them")], ""));
        page.Children.Add(Toggle("Windows of this desktop only", null, DefaultsKey.SwitcherCurrentSpaceOnly, ""));
        page.Children.Add(Toggle("Windows of this display only", null, DefaultsKey.SwitcherCurrentDisplayOnly, ""));
        page.Children.Add(Choice("Show it on", null, DefaultsKey.SwitcherScreenPlacement, [("pointer", "The display with the pointer"), ("menuBar", "The main display"), ("activeWindow", "The display of the front window")], ""));
        page.Children.Add(Card("Appears after", "A quick flick of Alt+Tab switches without showing the panel.", "",
            AgentSettings.NumberChoice(settings, DefaultsKey.SwitcherAppearanceDelay, [0, 50, 100, 200, 300, 500], 100, v => $"{v:0} ms")));
        page.Children.Add(Card("Most columns", null, "",
            AgentSettings.NumberChoice(settings, DefaultsKey.SwitcherMaxColumns, [0, 2, 3, 4, 5, 6, 8, 10], 0, v => v == 0 ? "Automatic" : $"{v:0}")));
        page.Children.Add(Toggle("Ignore the pointer's hover", "Only keys and clicks choose a window.", DefaultsKey.SwitcherIgnoreHoverSelection, ""));
        page.Children.Add(Toggle("Keep the search open with S", "Press S first to keep the panel up after you let go of Alt.", DefaultsKey.SwitcherSearchPinEnabled, ""));
        page.Children.Add(Toggle("Show the key hints", null, DefaultsKey.SwitcherShowShortcutHints, ""));
        page.Children.Add(Card("App rules", "Programs to treat specially, like \"steam.exe=hidden;chrome.exe=windowsOnly\".", "", RulesBox()));

        // Window layout
        page.Children.Add(Section("Window layout"));
        page.Children.Add(Caption(LayoutNote));
        if (!settings.IsAvailable(AppFeature.WindowLayout)) page.Children.Add(Caption("Install Window layout in Features to use it."));
        page.Children.Add(Toggle("Layout shortcuts", "Ctrl+Alt with the arrows, U I J K, Return, C and R by default.", DefaultsKey.WindowLayoutShortcutsEnabled, ""));
        if (app.Features.Get<WindowLayoutService>(AppFeature.WindowLayout) is { TakenShortcuts.Count: > 0 } layout)
            page.Children.Add(Caption("Another program already uses the shortcut for: " + string.Join(", ", layout.TakenShortcuts) + "."));
        page.Children.Add(LayoutShortcuts());
        page.Children.Add(Toggle("Directional keys", "Hold the chord and move the pointer toward the layout you want.", DefaultsKey.WindowDirectionalEnabled, ""));
        page.Children.Add(Card("Chord", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.WindowDirectionalShortcut)));
        page.Children.Add(Toggle("Left and right cycle the width", "Pressing the same side again goes half, two thirds, third.", DefaultsKey.WindowLayoutSideRepeatCyclesThirds, ""));
        page.Children.Add(Toggle("Move the pointer between displays", null, DefaultsKey.PointerDisplayEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.PointerDisplayShortcut)));
        page.Children.Add(Toggle("Drag with a modifier", "Hold the chord and drag a window to move it; add Shift to resize it from the nearest edge.", DefaultsKey.WindowGestureEnabled, ""));
        page.Children.Add(Choice("Chord", null, DefaultsKey.WindowGestureModifiers, [("control+win", "Ctrl + Win"), ("control+alt", "Ctrl + Alt"), ("alt+win", "Alt + Win"), ("control+alt+win", "Ctrl + Alt + Win")], ""));
        page.Children.Add(Toggle("Bring the window forward", null, DefaultsKey.WindowGestureRaiseWindow, ""));
        page.Children.Add(Toggle("Snap at the screen's edge", "Drag a title bar to an edge or corner to place the window there.", DefaultsKey.WindowEdgeSnapEnabled, ""));
        page.Children.Add(Toggle("Give the old size back", "A snapped window goes back to its own size when you drag it away.", DefaultsKey.WindowEdgeSnapRestoreOnDrag, ""));
        foreach (var zone in new[] { EdgeSnapZone.Top, EdgeSnapZone.Bottom, EdgeSnapZone.Left, EdgeSnapZone.Right }) page.Children.Add(ZoneSplitCard(zone));
        page.Children.Add(Card("Gap between windows", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.WindowLayoutWindowGap, [0, 8, 16, 32, 64, 128], 0, v => $"{v:0} px")));
        page.Children.Add(Card("Gap at the screen's edge", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.WindowLayoutScreenGap, [0, 8, 16, 32, 64, 128], 0, v => $"{v:0} px")));
        page.Children.Add(Card("Margin for \"Maximize with margin\"", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.WindowLayoutMarginPercent, [0, 2, 5, 10, 15, 20, 25], 5, v => $"{v:0}%")));
        page.Children.Add(Card("Leave these apps alone", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.WindowLayoutIgnoredApps, "game.exe, vm.exe")));
        return page;
    }

    private TextBox RulesBox()
    {
        var box = new TextBox { PlaceholderText = "steam.exe=hidden", MinWidth = 260, Text = app.Settings.GetString(DefaultsKey.SwitcherAppRules) };
        box.LostFocus += (_, _) => app.Settings.Set(DefaultsKey.SwitcherAppRules, box.Text.Trim());
        return box;
    }

    private TextBox ProgramListBox(string key, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, MinWidth = 260, Text = string.Join(", ", app.Settings.GetList(key)) };
        box.LostFocus += (_, _) =>
        {
            var names = box.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe");
            app.Settings.SetList(key, names);
        };
        return box;
    }

    private static readonly (string Group, WindowLayoutAction[] Actions)[] LayoutGroups =
    [
        ("Halves", [WindowLayoutAction.LeftHalf, WindowLayoutAction.RightHalf, WindowLayoutAction.TopHalf, WindowLayoutAction.BottomHalf, WindowLayoutAction.CenterHalf]),
        ("Corners", [WindowLayoutAction.TopLeft, WindowLayoutAction.TopRight, WindowLayoutAction.BottomLeft, WindowLayoutAction.BottomRight]),
        ("Thirds", [WindowLayoutAction.LeftThird, WindowLayoutAction.CenterThird, WindowLayoutAction.RightThird, WindowLayoutAction.LeftTwoThirds, WindowLayoutAction.CenterTwoThirds, WindowLayoutAction.RightTwoThirds,
                    WindowLayoutAction.TopThird, WindowLayoutAction.MiddleThird, WindowLayoutAction.BottomThird, WindowLayoutAction.TopTwoThirds, WindowLayoutAction.BottomTwoThirds]),
        ("Quarters", [WindowLayoutAction.LeftQuarter, WindowLayoutAction.LeftMiddleQuarter, WindowLayoutAction.RightMiddleQuarter, WindowLayoutAction.RightQuarter,
                      WindowLayoutAction.TopQuarter, WindowLayoutAction.UpperMiddleQuarter, WindowLayoutAction.LowerMiddleQuarter, WindowLayoutAction.BottomQuarter]),
        ("Sixths", [WindowLayoutAction.TopLeftSixth, WindowLayoutAction.TopCenterSixth, WindowLayoutAction.TopRightSixth, WindowLayoutAction.BottomLeftSixth, WindowLayoutAction.BottomCenterSixth, WindowLayoutAction.BottomRightSixth]),
        ("Other", [WindowLayoutAction.Maximize, WindowLayoutAction.MarginMaximize, WindowLayoutAction.Center, WindowLayoutAction.FullScreen, WindowLayoutAction.Restore, WindowLayoutAction.PreviousDisplay, WindowLayoutAction.NextDisplay]),
    ];

    private static string Spaced(WindowLayoutAction action)
    {
        var raw = action.ToString();
        var text = string.Concat(raw.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return text;
    }

    private Expander LayoutShortcuts()
    {
        var stack = new StackPanel();
        foreach (var (group, actions) in LayoutGroups)
        {
            stack.Children.Add(new TextBlock { Text = group, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var action in actions) stack.Children.Add(Card(Spaced(action), null, "", new ShortcutRecorder(app.Settings, WindowLayoutSupport.ShortcutKey(action))));
        }
        return new Expander { Header = "Every layout's shortcut", Content = stack, Margin = new Thickness(0, 4, 0, 8) };
    }

    private CardControl ZoneSplitCard(EdgeSnapZone zone)
    {
        var settings = app.Settings;
        var current = EdgeSnapSupport.ZoneActions.Parse(settings.GetString(DefaultsKey.WindowEdgeSnapZoneActions)).For(zone).Count;
        var combo = new ComboBox { MinWidth = 180 };
        for (var parts = 1; parts <= EdgeSnapSupport.MaxParts; parts++) combo.Items.Add(new ComboBoxItem { Content = parts == 1 ? "One layout" : $"Split in {parts}", Tag = parts, IsSelected = parts == current });
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: int parts }) return;
            var actions = EdgeSnapSupport.ZoneActions.Parse(settings.GetString(DefaultsKey.WindowEdgeSnapZoneActions)).WithPartCount(zone, parts);
            settings.Set(DefaultsKey.WindowEdgeSnapZoneActions, actions.Storage);
        };
        return Card($"{zone} edge", $"Snapping here: {string.Join(", ", EdgeSnapSupport.ZoneActions.Parse(settings.GetString(DefaultsKey.WindowEdgeSnapZoneActions)).For(zone).Select(Spaced))}.", "", combo);
    }
}
