// SPDX-License-Identifier: GPL-3.0-or-later
// Settings pages built from small row helpers bound to settings keys through SettingsProxy.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Islet.App.Island.Downloads;
using Islet.App.Platform.Shell;
using Islet.Core;
using Islet.Core.Features;
using Islet.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Islet.App.Shell;

internal sealed class SettingsPages(App app, SettingsProxy proxy)
{
    public FrameworkElement Build(SettingsPage page) => page switch
    {
        SettingsPage.Features => FeaturesPage(),
        SettingsPage.Island => IslandPage(),
        SettingsPage.About => AboutPage(),
        _ => GeneralPage(),
    };

    // MARK: Row helpers

    private static TextBlock PageTitle(string text) => new() { Text = text, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };

    private static TextBlock Section(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["SectionHeader"] };

    private static TextBlock Caption(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["Caption"], Margin = new Thickness(0, 0, 0, 8) };

    private static FrameworkElement Header(string title, string? description)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description))
            stack.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0) });
        return stack;
    }

    private static CardControl Card(string title, string? description, string glyph, object content) => new()
    {
        Header = Header(title, description),
        Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons") },
        Content = content,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private Binding Bind(string key) => new($"[{key}]") { Source = proxy, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    private CardControl Toggle(string title, string? description, string key, string glyph = "")
    {
        var toggle = new ToggleSwitch();
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Bind(key));
        return Card(title, description, glyph, toggle);
    }

    private CardControl SliderRow(string title, string? description, string key, double min, double max, double step,
                                  string format, string glyph = "", double scale = 1)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        slider.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Bind(key));
        var label = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.SetBinding(TextBlock.TextProperty, new Binding("Value") { Source = slider, StringFormat = format, Converter = new ScaleConverter(scale) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(slider);
        return Card(title, description, glyph, row);
    }

    private CardControl Choice(string title, string? description, string key, (string Value, string Label)[] options, string glyph = "")
    {
        var combo = new ComboBox { MinWidth = 180, SelectedValuePath = "Tag" };
        foreach (var (value, label) in options) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, Bind(key));
        return Card(title, description, glyph, combo);
    }

    private static Button ActionButton(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Page(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    // MARK: General

    private FrameworkElement GeneralPage()
    {
        var page = Page(
            PageTitle("General"),
            Toggle("Launch at sign-in", "Start Islet in the background when you sign in to Windows.", DefaultsKey.LaunchAtLogin, ""),
            Choice("Appearance", "Settings and panel colours.", DefaultsKey.AppTheme,
                   [("system", "Use Windows setting"), ("light", "Light"), ("dark", "Dark")], ""),
            Section("Settings file"),
            Caption($"Settings live in {AppIdentity.SettingsFile}. A file at {AppIdentity.DotfilePath} is applied at every launch, so you can keep them in your dotfiles."));
        var buttons = new WrapPanel();
        buttons.Children.Add(ActionButton("Export settings…", ExportSettings));
        buttons.Children.Add(ActionButton("Import settings…", ImportSettings));
        buttons.Children.Add(ActionButton("Open settings folder", () => OpenFolder(AppIdentity.DataDirectory)));
        buttons.Children.Add(ActionButton("Open logs", () => OpenFolder(AppIdentity.LogDirectory)));
        page.Children.Add(buttons);
        return page;
    }

    private void ExportSettings()
    {
        var dialog = new SaveFileDialog { FileName = "Islet Settings.json", Filter = "Islet settings|*.json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SettingsBackup.Export(app.Settings));
    }

    private void ImportSettings()
    {
        var dialog = new OpenFileDialog { Filter = "Islet settings|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var result = SettingsBackup.Import(app.Settings, File.ReadAllText(dialog.FileName));
            System.Windows.MessageBox.Show($"Imported {result.Applied} settings." +
                (result.Rejected.Count > 0 ? $" Skipped {result.Rejected.Count} unknown or invalid ones." : ""), AppIdentity.Name);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            System.Windows.MessageBox.Show(e.Message, AppIdentity.Name);
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // MARK: Features hub

    private FrameworkElement FeaturesPage()
    {
        var ported = FeatureCatalog.All.Count(i => i.Status == WindowsStatus.Ported);
        var native = FeatureCatalog.All.Count(i => i.Status is WindowsStatus.Native or WindowsStatus.Dropped);
        var page = Page(
            PageTitle("Features"),
            Caption($"Install only what you use. Uninstalled features stop loading and keep their settings. " +
                    $"{ported} of {FeatureCatalog.All.Count} features run in this build; {native} are built into Windows or macOS-only, " +
                    "and the rest arrive milestone by milestone (see docs/PARITY.md)."));
        var presets = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        presets.Children.Add(ActionButton("Island preset", () => app.Settings.Apply(FeaturePreset.Island), primary: true));
        presets.Children.Add(ActionButton("Essential", () => app.Settings.Apply(FeaturePreset.Essential)));
        presets.Children.Add(ActionButton("Windows", () => app.Settings.Apply(FeaturePreset.Windows)));
        presets.Children.Add(ActionButton("Battery", () => app.Settings.Apply(FeaturePreset.Battery)));
        page.Children.Add(presets);

        foreach (var group in Enum.GetValues<FeatureGroup>())
        {
            page.Children.Add(Section(FeatureCatalog.GroupTitle(group)));
            foreach (var info in FeatureCatalog.InGroup(group)) page.Children.Add(FeatureRow(info));
        }
        return page;
    }

    private CardControl FeatureRow(FeatureInfo info)
    {
        var (badge, color) = info.Status switch
        {
            WindowsStatus.Ported => ("Available", Color.FromRgb(0x3F, 0xB9, 0x50)),
            WindowsStatus.Planned => ($"Planned · {info.Milestone}", Color.FromRgb(0x4C, 0x8D, 0xFF)),
            WindowsStatus.NativeToggle => ($"Windows setting · {info.Milestone}", Color.FromRgb(0x4C, 0x8D, 0xFF)),
            WindowsStatus.Native => ("Built into Windows", Color.FromRgb(0x8A, 0x8A, 0x8A)),
            _ => ("Not on Windows", Color.FromRgb(0x8A, 0x8A, 0x8A)),
        };
        var description = info.Summary + (info.WindowsNote is null ? "" : "  " + info.WindowsNote);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
            Child = new TextBlock { Text = badge, FontSize = 11, Foreground = new SolidColorBrush(color) },
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (info.SettingsUri is { } uri)
            right.Children.Add(ActionButton("Open setting", () => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })));
        if (info.CanInstall)
        {
            var toggle = new ToggleSwitch { IsChecked = app.Settings.IsAvailable(info.Feature) };
            toggle.Checked += (_, _) => Install(info.Feature, true);
            toggle.Unchecked += (_, _) => Install(info.Feature, false);
            right.Children.Add(toggle);
        }
        var card = Card(info.Title, description, info.Glyph, right);
        if (!info.CanInstall) card.Opacity = 0.75;
        return card;
    }

    private void Install(AppFeature feature, bool install)
    {
        foreach (var f in install ? FeatureAvailability.InitialInstallGroup(feature) : [feature])
            app.Settings.SetAvailable(f, install);
        if (!install && feature == AppFeature.Notch)
        {
            // The island's extensions do nothing without it.
            foreach (var extension in FeatureCatalog.DynamicIslandExtensions) app.Settings.SetAvailable(extension, false);
        }
    }

    // MARK: Dynamic Island

    private FrameworkElement IslandPage()
    {
        return Page(
            PageTitle("Dynamic Island"),
            Caption("Music, notices, timers and controls around the webcam. Your laptop has no camera cutout, so Islet draws one at the top centre of the screen; transparent areas around it pass clicks through."),
            Toggle("Show the Dynamic Island", null, DefaultsKey.NotchEnabled, ""),

            Section("Shape and place"),
            Choice("Shape", "A cutout hanging from the top edge, or a floating capsule as on a phone.", DefaultsKey.NotchSilhouette,
                   [("notch", "Notch"), ("capsule", "Capsule")], ""),
            SliderRow("Height", "How tall the closed island is. The cutout's width follows it.", DefaultsKey.NotchHeight, 16, 40, 1, "{0:0} px", ""),
            SliderRow("Horizontal position", "Move the island to sit exactly under an off-centre webcam.", DefaultsKey.NotchOffsetX, -300, 300, 1, "{0:+0;-0;0} px", ""),
            Choice("Open size", null, DefaultsKey.NotchSize, [("spacious", "Spacious"), ("compact", "Compact"), ("custom", "Custom")], ""),
            SliderRow("Custom width", null, DefaultsKey.NotchCustomWidth, 360, 600, 10, "{0:0} px", ""),
            SliderRow("Custom height", null, DefaultsKey.NotchCustomHeight, 260, 640, 10, "{0:0} px", ""),
            Choice("Display", "Automatic uses the laptop's own screen, where the webcam is.", DefaultsKey.NotchDisplay,
                   [("automatic", "Automatic"), ("builtIn", "Built-in display"), ("main", "Main display"), ("pointer", "Follow the pointer")], ""),
            Toggle("Menu bar at the top", "A black bar like a Mac's: the app in front and its menus on the left, the clock on the right. Maximized windows start below it. Off: the island floats over windows.", DefaultsKey.NotchReserveStrip, ""),
            Toggle("Show the app and its menus", "Apps without menus get File, Edit, View, Window and Help, whose items send the usual shortcut to the app.", DefaultsKey.NotchStripMenus, ""),
            Toggle("Show the clock and date", "Click it for notifications and the calendar.", DefaultsKey.NotchStripClock, ""),

            Section("Behaviour"),
            Toggle("Open on hover", "Off: hovering only grows the island slightly, and a click opens it.", DefaultsKey.NotchOpenOnHover, ""),
            SliderRow("Hover delay", null, DefaultsKey.NotchHoverDelay, 0.1, 1.0, 0.05, "{0:0.00} s", ""),
            SliderRow("Close delay", "How long the island waits after the pointer leaves.", DefaultsKey.NotchCloseDelay, 0.1, 2.0, 0.05, "{0:0.00} s", ""),
            Toggle("Hide in full screen", "Out of the way of games, videos and presentations.", DefaultsKey.NotchHideInFullscreen, ""),
            Toggle("Hide until hover", "Keep only a thin line at rest.", DefaultsKey.NotchHideUntilHover, ""),
            Toggle("Show in screenshots and recordings", null, DefaultsKey.NotchShowInCaptures, ""),
            Toggle("Gestures", "Scroll down on the island to open it, up to close it; swipe sideways on a touchpad to change tracks.", DefaultsKey.NotchGesturesEnabled, ""),

            Section("Music"),
            Toggle("Show what's playing", "Spotify, browsers and other players that use Windows media controls.", DefaultsKey.NotchShowPlayingMusic, ""),
            Toggle("Announce track changes", null, DefaultsKey.NotchTrackChange, ""),
            Toggle("Live equalizer", "Listens to the player's own audio output to move the bars. Nothing is stored or sent. " +
                   "Without it, the bars keep their usual motion.", DefaultsKey.NotchLiveEqualizer, ""),

            Section("Lyrics"),
            Toggle("Show lyrics", "Follow the lyrics of the current song on the island's Music page.", DefaultsKey.NotchLyricsEnabled, ""),
            Toggle("Find lyrics online", "While lyrics are open, send the song title, artist, album and duration to lrclib.net. " +
                   "Song titles, artist names and albums may also be checked with musicbrainz.org. Imported lyrics stay on this PC.",
                   DefaultsKey.NotchLyricsOnline, ""),

            Section("Files"),
            Toggle("Files on the island", "Drop files, links, text or images on the island to keep them on a shelf, then drag them out anywhere.",
                   DefaultsKey.NotchShelf, ""),
            Toggle("Reveal while dragging", "Holding something near the notch grows it into a drop target.", DefaultsKey.NotchDragReveal, ""),
            Toggle("Remove items after dragging them out", "Pinned items always stay.", DefaultsKey.ShelfRemoveAfterDrop, ""),
            Toggle("Close the island after dragging out", null, DefaultsKey.ShelfCloseAfterDrop, ""),

            Section("Downloads"),
            Toggle("Downloads in the island", "Files arriving in the watched folder show beside the camera, then wait on the island's Downloads page. " +
                   "Browsers on Windows don't share download progress, so most downloads show the size received instead of a percentage.",
                   DefaultsKey.NotchDownloadsEnabled, "\uE896"),
            DownloadsFolderCard(),

            Section("Notices"),
            Toggle("Volume", null, DefaultsKey.NotchVolume, ""),
            Toggle("Use the volume keys in the island", "The island handles the volume keys in 2% steps instead of Windows' own volume pop-up.", DefaultsKey.NotchSwallowVolumeKeys, ""),
            Toggle("Microphone mute", null, DefaultsKey.NotchMicrophone, ""),
            Toggle("Brightness", "Windows still shows its own brightness pop-up: the laptop's keys are handled by its firmware.", DefaultsKey.NotchBrightness, ""),
            Toggle("Battery and charger", null, DefaultsKey.NotchBattery, ""),
            Toggle("Tint when the battery is low", null, DefaultsKey.NotchLowBatteryTint, ""),
            SliderRow("Low battery level", null, DefaultsKey.NotchLowBatteryThreshold, 1, 99, 1, "{0:0} %", ""),

            Section("Timer"),
            Toggle("Timer", "Timer, Pomodoro and stopwatch in the island.", DefaultsKey.NotchTimerEnabled, ""),
            Toggle("Play a sound when a timer ends", null, DefaultsKey.NotchTimerSoundEnabled, ""),
            SliderRow("Focus length", "Pomodoro", DefaultsKey.NotchPomodoroFocusMinutes, 1, 180, 1, "{0:0} min", ""),
            SliderRow("Short break", "Pomodoro", DefaultsKey.NotchPomodoroShortBreakMinutes, 1, 60, 1, "{0:0} min", ""),
            SliderRow("Long break", "Pomodoro", DefaultsKey.NotchPomodoroLongBreakMinutes, 1, 60, 1, "{0:0} min", ""),
            SliderRow("Long break every", "Pomodoro", DefaultsKey.NotchPomodoroLongBreakInterval, 1, 24, 1, "{0:0} sessions", ""),
            SliderRow("Sessions", "Pomodoro", DefaultsKey.NotchPomodoroTotalSessions, 1, 24, 1, "{0:0}", ""));
    }

    /// <summary>The watched folder with its choices (upstream NotchDownloadsSettingsControls).</summary>
    private CardControl DownloadsFolderCard()
    {
        var path = new TextBlock { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360, Margin = new Thickness(0, 2, 0, 0) };
        path.SetBinding(TextBlock.TextProperty, new Binding($"[{DefaultsKey.NotchDownloadsFolderPath}]")
            { Source = proxy, Mode = BindingMode.OneWay, Converter = new FolderConverter() });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Watched folder", FontSize = 14 });
        header.Children.Add(path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ActionButton("Choose folder…", () => DownloadService.ChooseFolder(app.Settings, Window.GetWindow(path))));
        buttons.Children.Add(ActionButton("Use Downloads folder", () => DownloadService.UseDownloadsFolder(app.Settings)));
        buttons.Children.Add(ActionButton("Open folder", () => OpenFolder(DownloadService.ResolveFolder(app.Settings))));
        return new CardControl
        {
            Header = header,
            Icon = new FontIcon { Glyph = "\uE8B7", FontFamily = new FontFamily("Segoe Fluent Icons") },
            Content = buttons,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    /// <summary>An empty choice is Windows' own Downloads folder.</summary>
    private sealed class FolderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string { Length: > 0 } chosen ? chosen : KnownFolders.Downloads + "  (Windows Downloads folder)";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // MARK: About

    private FrameworkElement AboutPage() => Page(
        PageTitle(AppIdentity.Name),
        Caption($"Version {AppIdentity.Version} · working name, to be renamed"),
        Section("Credits"),
        Caption($"A Windows 11 port of vorssaint-utils ({AppIdentity.UpstreamRepository}), Copyright © 2026 Vorssaint, licensed under GPL-3.0-or-later. " +
                "This port is a derivative work under the same licence. The Vorssaint name, icon and look belong to their owner and are not used here."),
        Section("Licence"),
        Caption("This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License " +
                "as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version."));

    private sealed class ScaleConverter(double scale) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is double d ? d * scale : value;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
