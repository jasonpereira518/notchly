// SPDX-License-Identifier: GPL-3.0-or-later
// The Calendar section of the island's settings: the calendars added on this PC (name, colour, shown,
// remove) and the window that adds one by its private iCal link or an .ics file. Upstream lists the
// Mac's EventKit calendars instead (NotchCalendarSelection.swift).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Notchly.App.Island.Calendar;
using Notchly.Core.Calendar;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal static class CalendarSettings
{
    /// <summary>The list of calendars, rebuilt whenever one is added, renamed, recoloured or removed.</summary>
    public static FrameworkElement CalendarList(SettingsStore settings)
    {
        var list = new StackPanel();
        void Build()
        {
            list.Children.Clear();
            var excluded = settings.GetList(DefaultsKey.NotchCalendarExcluded).ToHashSet(StringComparer.Ordinal);
            foreach (var feed in CalendarFeedStore.Load()) list.Children.Add(Row(feed, settings, excluded));
            var add = new Button { Content = "Add calendar…", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 4, 0, 8) };
            add.Click += (_, _) => new AddCalendarWindow { Owner = Window.GetWindow(list) }.ShowDialog();
            list.Children.Add(add);
        }
        Build();
        Action changed = () => list.Dispatcher.BeginInvoke(Build);
        CalendarFeedStore.Changed += changed;
        list.Unloaded += (_, _) => CalendarFeedStore.Changed -= changed;
        list.Loaded += (_, _) => { CalendarFeedStore.Changed -= changed; CalendarFeedStore.Changed += changed; };
        return list;
    }

    private static FrameworkElement Row(CalendarFeed feed, SettingsStore settings, HashSet<string> excluded)
    {
        var swatch = new Button
        {
            Width = 28, Height = 28, Padding = new Thickness(0), ToolTip = "Change colour",
            Content = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(feed.Color.R, feed.Color.G, feed.Color.B)) },
        };
        swatch.Click += (_, _) =>
        {
            var palette = CalendarColor.Palette;
            var index = palette.ToList().IndexOf(feed.Color);
            CalendarFeedStore.SetColor(feed.Id, palette[(index + 1) % palette.Count]);
        };
        var name = new TextBox { Text = feed.Name, MinWidth = 180, Margin = new Thickness(8, 0, 8, 0), ToolTip = "Rename" };
        name.LostFocus += (_, _) => { if (name.Text.Trim() != feed.Name) CalendarFeedStore.Rename(feed.Id, name.Text); };
        var where = new TextBlock
        {
            Text = feed.IsFile ? System.IO.Path.GetFileName(feed.Source) : new Uri(feed.Source).Host,
            Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150,
        };
        var shown = new ToggleSwitch { IsChecked = !excluded.Contains(feed.Id), ToolTip = "Show on the island", Margin = new Thickness(8, 0, 8, 0) };
        shown.Click += (_, _) =>
        {
            var set = settings.GetList(DefaultsKey.NotchCalendarExcluded).ToHashSet(StringComparer.Ordinal);
            if (shown.IsChecked == true) set.Remove(feed.Id); else set.Add(feed.Id);
            settings.SetList(DefaultsKey.NotchCalendarExcluded, set.Order(StringComparer.Ordinal));
        };
        var remove = new Button { Content = "Remove", Appearance = ControlAppearance.Secondary };
        remove.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show(Window.GetWindow(remove)!, $"Remove “{feed.Name}” from Notchly? The calendar itself isn't changed.",
                    "Remove calendar", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK)
                CalendarFeedStore.Remove(feed.Id);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var child in new UIElement[] { swatch, name, where, shown, remove }) row.Children.Add(child);
        return row;
    }

    /// <summary>The minutes before a start the countdown appears, upstream's presets.</summary>
    public static ComboBox LeadChoice(SettingsStore settings)
    {
        var combo = new ComboBox { MinWidth = 140 };
        foreach (var minutes in CalendarSupport.CountdownLeadPresets)
            combo.Items.Add(new ComboBoxItem { Content = minutes < 60 ? $"{minutes} minutes" : minutes == 60 ? "1 hour" : $"{minutes / 60} hours", Tag = minutes });
        var current = CalendarSupport.CountdownLeadMinutes(settings.GetInt(DefaultsKey.NotchCalendarCountdownLead, CalendarSupport.DefaultCountdownLeadMinutes));
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current) ?? combo.Items[2];
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: int minutes }) settings.Set(DefaultsKey.NotchCalendarCountdownLead, minutes); };
        return combo;
    }
}

/// <summary>Adds a calendar by its private iCal link or an .ics file, checking it reads before keeping it.</summary>
internal sealed class AddCalendarWindow : FluentWindow
{
    private readonly TextBox _link = new() { PlaceholderText = "https://… or webcal://…", MinWidth = 420 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _add = new() { Content = "Add", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };

    public AddCalendarWindow()
    {
        Title = "Add calendar";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        stack.Children.Add(new TitleBar { Title = "Add calendar", ShowMaximize = false, ShowMinimize = false, Margin = new Thickness(-20, -16, -20, 8) });
        stack.Children.Add(new TextBlock { Text = "Paste the calendar's private iCal link", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(_link);
        stack.Children.Add(_status);
        foreach (var (where, how) in new[]
        {
            ("Google Calendar", "Settings → your calendar → Integrate calendar → “Secret address in iCal format”."),
            ("Outlook or Microsoft 365", "Settings → Calendar → Shared calendars → Publish a calendar → the ICS link. Some schools switch publishing off."),
            ("Canvas", "Calendar → “Calendar Feed” (bottom right)."),
            ("iCloud", "Share the calendar → Public Calendar → copy the webcal link."),
        })
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            text.Inlines.Add(new System.Windows.Documents.Run(where + ": ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run(how) { });
            stack.Children.Add(text);
        }
        stack.Children.Add(new TextBlock
        {
            Text = "The link lets anyone with it read that calendar, so Notchly keeps it encrypted on this PC and never includes it in settings backups. Events stay in memory. Invitations you declined may still show, since a feed doesn't say which attendee is you.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 12, 0, 12),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var file = new Button { Content = "Choose .ics file…", Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel" };
        buttons.Children.Add(file);
        buttons.Children.Add(_add);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;

        _add.Click += async (_, _) => await AddAsync(_link.Text);
        cancel.Click += (_, _) => Close();
        file.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Calendar files (*.ics)|*.ics", Title = "Choose a calendar file" };
            if (dialog.ShowDialog(this) == true) await AddAsync(dialog.FileName);
        };
    }

    private async Task AddAsync(string input)
    {
        _add.IsEnabled = false;
        _status.Text = "Reading the calendar…";
        _status.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        try
        {
            var (feed, problem) = await CalendarFeedStore.AddAsync(input);
            if (feed is not null) { Close(); return; }
            _status.Text = problem;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x00));
        }
        finally { _add.IsEnabled = true; }
    }
}
