// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Sound: the Windows counterpart of upstream's GeneralToolSettings sound cards (MixerSection in settings
// mode, "Output switcher", "Audio priority") and the microphone-mute rows of QuickToolsSettings. Upstream's strings,
// with Windows' names for its controls.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Platform.Audio;
using Notchly.App.Sound;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Sound;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string MixerNote = "Apps that use audio show up in the island's mixer and the panel. Windows plays an app up to 100%, so there is no boost. " +
        "Call apps, Zoom and music-production apps keep their own volume.";
    private const string CallNote = "Changing a call app's volume can echo into a screen share, so Notchly leaves its volume alone unless you allow it.";
    private const string MicNote = "Uses each microphone's mute switch (or its input level where it has none) and gives every microphone back exactly what it had. " +
        "It is not a privacy lock: an app that takes a microphone exclusively can still hear it.";

    private FrameworkElement SoundPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Sound"));

        // Volume mixer
        page.Children.Add(Section("Volume mixer"));
        page.Children.Add(Caption(MixerNote));
        if (!settings.IsAvailable(AppFeature.Mixer)) page.Children.Add(Caption("Install the Volume mixer in Features to use it."));
        page.Children.Add(Toggle("Hide inactive apps", "Idle apps leave the list; an app with its own volume or output stays.", DefaultsKey.MixerHideInactiveApps, ""));
        page.Children.Add(Toggle("Show system sounds", "A row for Windows' own sounds.", DefaultsKey.MixerShowSystemSounds, ""));
        page.Children.Add(Toggle("Allow volume control for call apps", CallNote, DefaultsKey.MixerCallAppVolumeEnabled, ""));
        page.Children.Add(Toggle("Lower volume when headphones disconnect", "When headphones are unplugged, the speakers take over at a lower volume, and go back when the headphones return.",
            DefaultsKey.MixerLowerVolumeOnHeadphonesDisconnect, ""));
        page.Children.Add(SliderRow("Volume after disconnect", null, DefaultsKey.MixerHeadphonesDisconnectVolumePercent, 0, 100, 5, "{0:0}%", ""));
        page.Children.Add(HiddenAppsCard());

        // Output switcher
        page.Children.Add(Section("Output switcher"));
        if (!settings.IsAvailable(AppFeature.SoundOutputSwitcher)) page.Children.Add(Caption("Install the Output switcher in Features to use it."));
        page.Children.Add(Toggle("Switch outputs with a shortcut", "Each press moves to the next output you tick below.", DefaultsKey.SoundOutputSwitcherEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.SoundOutputSwitcherShortcut)));
        page.Children.Add(OutputCycleCard());

        // Audio priority
        page.Children.Add(Section("Audio priority"));
        if (!settings.IsAvailable(AppFeature.AudioPriority)) page.Children.Add(Caption("Install Device priority in Features to use it."));
        page.Children.Add(Toggle("Automatically switch to the highest-priority output", "When a device connects or disconnects, the first one in your list that is connected plays.",
            DefaultsKey.AudioPriorityOutputEnabled, ""));
        page.Children.Add(PriorityList(input: false));
        page.Children.Add(Toggle("Automatically switch to the highest-priority microphone", null, DefaultsKey.AudioPriorityInputEnabled, ""));
        page.Children.Add(PriorityList(input: true));

        // Microphone mute
        page.Children.Add(Section("Mute microphone"));
        page.Children.Add(Caption(MicNote));
        if (!settings.IsAvailable(AppFeature.MicMute)) page.Children.Add(Caption("Install Microphone mute in Features to use it."));
        page.Children.Add(MicMuteCard());
        page.Children.Add(Toggle("Show a badge on the tray icon while muted", null, DefaultsKey.MicMuteMenuBarIndicator, ""));
        page.Children.Add(Toggle("Mute microphone with a shortcut", null, DefaultsKey.MicMuteShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.MicMuteShortcut)));
        return page;
    }

    private CardControl MicMuteCard()
    {
        var button = new Button { MinWidth = 180 };
        void Show() => button.Content = app.Features.Get<MicMuteService>(AppFeature.MicMute)?.Active == true ? "Unmute microphone" : "Mute microphone";
        button.Click += (_, _) =>
        {
            if (app.Features.Get<MicMuteService>(AppFeature.MicMute) is { } service && !service.Toggle())
                System.Windows.MessageBox.Show("Some microphones could not be reached.", Notchly.Core.AppIdentity.Name);
            Show();
        };
        Show();
        return Card("Mute microphone", "Mutes every microphone, not just the one in use.", "", button);
    }

    private CardControl HiddenAppsCard()
    {
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var hidden = app.Settings.Get(DefaultsKey.MixerHiddenApps, new Dictionary<string, string>());
            if (hidden.Count == 0) { list.Children.Add(new TextBlock { Text = "No hidden apps", Opacity = 0.7, FontSize = 12 }); return; }
            foreach (var (id, name) in hidden.OrderBy(h => h.Value, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var show = new Button { Content = "Show", Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(show, Dock.Right);
                show.Click += (_, _) =>
                {
                    var current = app.Settings.Get(DefaultsKey.MixerHiddenApps, new Dictionary<string, string>());
                    if (current.Remove(id)) app.Settings.Set(DefaultsKey.MixerHiddenApps, current);
                    Fill();
                };
                row.Children.Add(show);
                row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
        }
        Fill();
        return Card("Apps in the list", "Apps you hid from the mixer.", "", list);
    }

    private CardControl OutputCycleCard()
    {
        var list = new StackPanel { MinWidth = 260 };
        var selected = MixerSupport.SanitizedIdList(app.Settings.Get(DefaultsKey.SoundOutputSwitcherDeviceUIDs, new List<string>())).ToHashSet(StringComparer.Ordinal);
        foreach (var output in AudioDevices.List(false))
        {
            var box = new CheckBox { Content = output.Name, IsChecked = selected.Contains(output.Id), Margin = new Thickness(0, 2, 0, 2) };
            void Save()
            {
                var current = MixerSupport.SanitizedIdList(app.Settings.Get(DefaultsKey.SoundOutputSwitcherDeviceUIDs, new List<string>())).ToList();
                if (box.IsChecked == true) { if (!current.Contains(output.Id)) current.Add(output.Id); }
                else current.Remove(output.Id);
                app.Settings.Set(DefaultsKey.SoundOutputSwitcherDeviceUIDs, current);
            }
            box.Checked += (_, _) => Save();
            box.Unchecked += (_, _) => Save();
            list.Children.Add(box);
        }
        if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "No outputs found", Opacity = 0.7, FontSize = 12 });
        return Card("Outputs in cycle", "Select at least one available output.", "", list);
    }

    private FrameworkElement PriorityList(bool input)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Fill()
        {
            host.Children.Clear();
            var service = app.Features.Get<AudioPriorityService>(AppFeature.AudioPriority);
            if (service is null) return;
            var entries = service.Entries(input);
            if (entries.Count == 0) { host.Children.Add(new TextBlock { Text = "Your list fills in as devices connect.", Opacity = 0.7, FontSize = 12 }); return; }
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var row = new Grid { Margin = new Thickness(8, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = $"{i + 1}.  {entry.Name}", VerticalAlignment = VerticalAlignment.Center, Opacity = entry.Available ? 1 : 0.5 });
                if (entry.IsCurrent || !entry.Available)
                    label.Children.Add(new TextBlock { Text = entry.IsCurrent ? "  Current" : "  Unavailable", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(label);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(Mini("", "Move up", i > 0, () => { service.Move(input, entry.Id, -1); Fill(); }));
                buttons.Children.Add(Mini("", "Move down", i < entries.Count - 1, () => { service.Move(input, entry.Id, 1); Fill(); }));
                if (!entry.Available) buttons.Children.Add(Mini("", "Remove", true, () => { service.Remove(input, entry.Id); Fill(); }));
                Grid.SetColumn(buttons, 1);
                row.Children.Add(buttons);
                host.Children.Add(row);
            }
        }
        Fill();
        return host;
    }

    private static Button Mini(string glyph, string tip, bool enabled, Action act)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"], FontSize = 12 },
            ToolTip = tip, IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4),
        };
        button.Click += (_, _) => act();
        return button;
    }
}
