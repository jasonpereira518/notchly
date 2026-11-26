// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools → Screenshot: the Windows counterpart of upstream's ScreenshotSettings view. Shortcuts for each action (all off until
// turned on), how a capture is taken, where it goes and what it is called, the quick preview, and the editor's remembered choices.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Notchly.App.Screenshots;
using Notchly.Core.Capture;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string ScreenshotNote = "Capture an area, a window or the whole screen, then mark it up, pin it or keep it. The capture shortcuts are off until you turn them on. Tokens in a file name pattern: %year %month %y %mo %d %h %mi %s, and %# for a running number.";

    private void AddScreenshotSection(StackPanel page)
    {
        var settings = app.Settings;
        page.Children.Add(Section("Screenshot"));
        page.Children.Add(Caption(ScreenshotNote));
        if (!settings.IsAvailable(AppFeature.Screenshot)) page.Children.Add(Caption("Install Screenshot in Features to use it."));
        (string Title, string Enabled, string Key)[] shortcuts =
        [
            ("Capture an area", DefaultsKey.ScreenshotShortcutEnabled, DefaultsKey.ScreenshotShortcut),
            ("Scrolling capture", DefaultsKey.ScreenshotScrollingShortcutEnabled, DefaultsKey.ScreenshotScrollingShortcut),
            ("Capture the whole screen", DefaultsKey.ScreenshotFullScreenShortcutEnabled, DefaultsKey.ScreenshotFullScreenShortcut),
            ("Edit the last capture", DefaultsKey.ScreenshotLastCaptureShortcutEnabled, DefaultsKey.ScreenshotLastCaptureShortcut),
            ("Recent captures", DefaultsKey.RecentCapturesShortcutEnabled, DefaultsKey.RecentCapturesShortcut),
            ("Edit the picture on the clipboard", DefaultsKey.ScreenshotClipboardShortcutEnabled, DefaultsKey.ScreenshotClipboardShortcut),
        ];
        foreach (var (title, enabled, key) in shortcuts)
        {
            page.Children.Add(Toggle(title, null, enabled, ""));
            page.Children.Add(Card("Shortcut", ShortcutNote, "", new ShortcutRecorder(settings, key)));
        }
        page.Children.Add(Toggle("Show the capture menu on a shortcut", "The strip with Screenshot, Copy text, Color and Scrolling when you press the area shortcut.", DefaultsKey.ScreenshotShowCaptureMenuOnShortcut, ""));
        page.Children.Add(Toggle("Freeze the screen while choosing", "Takes a picture of the screen first, so what you choose is what you saw. Off, or with a countdown, the screen stays live.", DefaultsKey.ScreenshotFreeze, ""));
        page.Children.Add(Toggle("Hide Notchly's own windows", "Notchly's windows stay out of captures and cannot be picked.", DefaultsKey.ScreenshotHideOwnWindows, ""));
        page.Children.Add(Toggle("Include the pointer", null, DefaultsKey.ScreenshotIncludePointer, ""));
        page.Children.Add(Toggle("Highlight windows under the pointer", "Click a highlighted window to capture it whole.", DefaultsKey.ScreenshotHighlightWindows, ""));
        page.Children.Add(Toggle("Show the last area", "A dashed outline of the previous area; R repeats it.", DefaultsKey.ScreenshotShowLastRegion, ""));
        page.Children.Add(Toggle("Loupe starts on", "L shows or hides it while choosing; the wheel changes its zoom.", DefaultsKey.ScreenshotLoupeStartsOn, ""));
        page.Children.Add(Toggle("Remember the loupe zoom", null, DefaultsKey.ScreenshotLoupeRememberZoom, ""));
        page.Children.Add(Toggle("Save at 1x on high-density displays", "Halves the pixels of a capture taken at a display scale above 100%.", DefaultsKey.ScreenshotDownscale, ""));
        page.Children.Add(NumberChoice("Countdown", "Seconds to wait after choosing an area before the picture is taken.", DefaultsKey.ScreenshotDelay,
            [(0, "None"), (3, "3 seconds"), (5, "5 seconds"), (10, "10 seconds")]));
        page.Children.Add(Scrolling());

        page.Children.Add(Choice("After a capture", "What happens first; the preview shows afterwards.", DefaultsKey.ScreenshotDefaultAction,
            [("", "Show the preview only"), ("save", "Save"), ("saveAndCopy", "Save and copy"), ("copy", "Copy"), ("edit", "Open the editor")]));
        page.Children.Add(Toggle("Always copy to the clipboard", null, DefaultsKey.ScreenshotCopyToClipboard, ""));
        page.Children.Add(Toggle("Add captures to the Shelf", "Needs the Shelf installed and on.", DefaultsKey.ScreenshotAddToShelf, ""));
        page.Children.Add(FolderCard());
        page.Children.Add(TextRow("Dated subfolder", "A pattern for a folder made inside it, such as %year-%mo. Empty keeps the files in the folder.", DefaultsKey.ScreenshotSaveSubfolder));
        page.Children.Add(TextRow("File name", "Empty names a file “Screenshot 2026-11-26 at 14.03.09”. Use the tokens above.", DefaultsKey.ScreenshotFileNamePattern));
        page.Children.Add(NumberRow("Next number", "The value of %# for the next file.", DefaultsKey.ScreenshotFileNumberNext));

        page.Children.Add(Toggle("Show the quick preview", "A small card with Edit, Pin, Copy and Save beside each capture.", DefaultsKey.ScreenshotPreviewEnabled, ""));
        page.Children.Add(NumberChoice("Preview stays", "How long it waits before closing; the pointer on it keeps it open.", DefaultsKey.ScreenshotPreviewDuration,
            [(1, "1 second"), (2, "2 seconds"), (3, "3 seconds"), (5, "5 seconds"), (10, "10 seconds"), (0, "Until dismissed")]));
        page.Children.Add(Choice("Preview position", null, DefaultsKey.ScreenshotPreviewPosition,
            [("automatic", "Beside the capture"), ("topLeft", "Top left"), ("topRight", "Top right"), ("bottomLeft", "Bottom left"), ("bottomRight", "Bottom right")]));
        page.Children.Add(Toggle("Preview takes the keyboard", "Off keeps your typing where it is.", DefaultsKey.ScreenshotPreviewTakesFocus, ""));
        page.Children.Add(Toggle("Digit keys choose editor tools", "1 to 9 follow the tool order in the editor's rail.", DefaultsKey.ScreenshotToolShortcutsEnabled, ""));
        page.Children.Add(Toggle("Shadows on marks", null, DefaultsKey.ScreenshotAnnotationShadows, ""));

        var buttons = new WrapPanel();
        buttons.Children.Add(ActionButton("Capture an area", () => app.Features.Get<ScreenshotService>(AppFeature.Screenshot)?.CaptureArea()));
        buttons.Children.Add(ActionButton("Recent captures", () => app.Features.Get<ScreenshotService>(AppFeature.Screenshot)?.ShowHistory()));
        buttons.Children.Add(ActionButton("Reset tool order", () => { settings.Set(DefaultsKey.ScreenshotToolOrder, ""); Refresh(); }));
        buttons.Children.Add(ActionButton("Restart numbering at 1", () => { settings.Set(DefaultsKey.ScreenshotFileNumberNext, 1); Refresh(); }));
        buttons.Children.Add(ActionButton("Clear recent captures", () => app.Captures.Clear()));
        page.Children.Add(buttons);
    }

    private CardControl Scrolling() => Toggle("Scroll for you in a scrolling capture", "Sends the wheel to the page under the area. Off: you scroll, then press Done.", DefaultsKey.ScreenshotScrollingAutoScroll, "");

    private CardControl NumberChoice(string title, string? description, string key, (int Value, string Label)[] options)
    {
        var combo = new ComboBox { MinWidth = 180 };
        foreach (var (value, label) in options) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var current = app.Settings.GetInt(key);
        foreach (ComboBoxItem item in combo.Items) if ((int)item.Tag == current) combo.SelectedItem = item;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: int value }) app.Settings.Set(key, value); };
        return Card(title, description, "", combo);
    }

    private CardControl TextRow(string title, string? description, string key)
    {
        var box = new TextBox { Width = 260, Text = app.Settings.GetString(key), VerticalAlignment = VerticalAlignment.Center };
        box.LostFocus += (_, _) => app.Settings.Set(key, box.Text.Trim());
        return Card(title, description, "", box);
    }

    private CardControl NumberRow(string title, string? description, string key)
    {
        var box = new TextBox { Width = 90, Text = app.Settings.GetInt(key, 1).ToString(), VerticalAlignment = VerticalAlignment.Center };
        box.LostFocus += (_, _) => { if (int.TryParse(box.Text, out var value) && value >= 0) app.Settings.Set(key, value); else box.Text = app.Settings.GetInt(key, 1).ToString(); };
        return Card(title, description, "", box);
    }

    private CardControl FolderCard()
    {
        var path = new TextBlock { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360, Margin = new Thickness(0, 2, 0, 0) };
        string Show() => app.Settings.GetString(DefaultsKey.ScreenshotSaveFolder) is { Length: > 0 } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) + "  (Desktop)";
        path.Text = Show();
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Save to", FontSize = 14 });
        header.Children.Add(path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ActionButton("Choose folder…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose where screenshots are saved" };
            if (dialog.ShowDialog(Window.GetWindow(path)) != true) return;
            app.Settings.Set(DefaultsKey.ScreenshotSaveFolder, dialog.FolderName);
            path.Text = Show();
        }));
        buttons.Children.Add(ActionButton("Use the Desktop", () => { app.Settings.Set(DefaultsKey.ScreenshotSaveFolder, ""); path.Text = Show(); }));
        buttons.Children.Add(ActionButton("Open folder", () =>
        {
            var folder = app.Settings.GetString(DefaultsKey.ScreenshotSaveFolder);
            Platform.Shell.ShellActions.Open(Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        }));
        return new CardControl { Header = header, Icon = new FontIcon { Glyph = "\uE8B7", FontFamily = new FontFamily("Segoe Fluent Icons") }, Content = buttons, Margin = new Thickness(0, 0, 0, 4) };
    }
}
