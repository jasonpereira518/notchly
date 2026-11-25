// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Keyboard: the Windows counterpart of upstream's SuperKeySettings.swift, TextSnippetsSettings.swift and
// QuitProtectionSettings.swift. The super key and its solo action, the snippet list with its editor, and the quit and close
// guards. Everything is off until you turn it on; the guards and the super key step aside in full-screen games.
using System.Windows;
using System.Windows.Controls;
using Notchly.Core.Features;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string SuperNote = "Hold the key and press another to send Ctrl, Alt, Shift and Win with it; tap it for the small action below. The key is not Caps Lock any more while this is on. " +
        "It cannot reach apps running as administrator unless Notchly does too.";
    private const string SnippetNote = "Type a trigger and it turns into its text. Triggers need 2 to 40 characters with no spaces. {{date}}, {{time}}, {{datetime}}, " +
        "{{date:yyyy-MM-dd}}, {{time-tz(Asia/Tokyo):HH:mm}} and {{clipboard}} are filled in. Nothing is recorded in password fields.";
    private const string GuardNote = "Alt+F4 and Ctrl+Q quit an app; Ctrl+W closes a tab or window. Each is held back until you mean it. In the extra-modifier mode, " +
        "Ctrl+Shift+W already means something in some apps, so pick the modifier that suits.";

    private TextSnippet? _editingSnippet;
    private bool _newSnippet;

    private FrameworkElement KeyboardPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Keyboard"));

        // Super key
        page.Children.Add(Section("Super key"));
        page.Children.Add(Caption(SuperNote));
        if (!settings.IsAvailable(AppFeature.SuperKey)) page.Children.Add(Caption("Install Super key in Features to use it."));
        page.Children.Add(Toggle("Turn on the super key", null, DefaultsKey.SuperKeyEnabled, ""));
        page.Children.Add(Choice("The key", null, DefaultsKey.SuperKeySource, [("capsLock", "Caps Lock"), ("rightControl", "Right Ctrl"), ("rightAlt", "Right Alt"), ("rightShift", "Right Shift"), ("rightWin", "Right Win")], ""));
        page.Children.Add(Choice("It stands for", null, DefaultsKey.SuperKeyModifiers,
            [("control+alt+shift+win", "Ctrl + Alt + Shift + Win"), ("control+alt+shift", "Ctrl + Alt + Shift"), ("control+alt+win", "Ctrl + Alt + Win"), ("control+win", "Ctrl + Win"), ("alt+win", "Alt + Win")], ""));
        page.Children.Add(Choice("When tapped on its own", null, DefaultsKey.SuperKeySoloAction,
            [("none", "Nothing"), ("escape", "Escape"), ("capsLock", "Caps Lock"), ("inputSource", "Next input language")], ""));
        page.Children.Add(Card("Apps that keep the key", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.SuperKeyExceptions, "game.exe")));
        page.Children.Add(Toggle("Free Windows' Microsoft 365 shortcuts", "Ctrl+Alt+Shift+Win with a letter opens Microsoft 365 apps. This turns that off for your account, and back on when you switch it off here.", DefaultsKey.SuperKeyOfficeMask, ""));

        // Snippets
        page.Children.Add(Section("Text snippets"));
        page.Children.Add(Caption(SnippetNote));
        if (!settings.IsAvailable(AppFeature.TextSnippets)) page.Children.Add(Caption("Install Text snippets in Features to use them."));
        page.Children.Add(Toggle("Expand snippets as you type", null, DefaultsKey.TextSnippetsEnabled, ""));
        page.Children.Add(Toggle("Open the library with a shortcut", "A window to search your snippets and send one.", DefaultsKey.SnippetLibraryEnabled, ""));
        page.Children.Add(Card("Library shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.SnippetLibraryShortcut)));
        AddSnippetEditor(page);

        // Quit and close protection
        page.Children.Add(Section("Quit and close protection"));
        page.Children.Add(Caption(GuardNote));
        if (!settings.IsAvailable(AppFeature.QuitWindowProtection)) page.Children.Add(Caption("Install Quit and close protection in Features to use it."));
        foreach (var kind in new[] { QuitGuardKind.Quit, QuitGuardKind.Close }) AddGuard(page, kind);
        return page;
    }

    private void AddGuard(StackPanel page, QuitGuardKind kind)
    {
        var settings = app.Settings;
        string Key(string name) => QuitProtectionSupport.SettingKey(kind, name);
        page.Children.Add(Toggle(kind == QuitGuardKind.Quit ? "Guard quitting (" + QuitProtectionSupport.Describe(kind) + ")" : "Guard closing (" + QuitProtectionSupport.Describe(kind) + ")", null,
            kind == QuitGuardKind.Quit ? DefaultsKey.QuitProtectionQuitEnabled : DefaultsKey.QuitProtectionCloseEnabled, ""));
        page.Children.Add(Choice("It takes", null, Key("Mode"), [("hold", "Holding the keys"), ("doublePress", "Pressing twice"), ("extraModifier", "An extra modifier")], ""));
        page.Children.Add(Card("Hold for", null, "", AgentSettings.NumberChoice(settings, Key("HoldDurationMs"), [250, 400, 600, 800, 1000, 1500, 2000], 800, v => $"{v:0} ms")));
        page.Children.Add(Card("Second press within", null, "", AgentSettings.NumberChoice(settings, Key("DoubleIntervalMs"), [200, 400, 600, 800, 1000, 1500], 600, v => $"{v:0} ms")));
        page.Children.Add(Choice("Extra modifier", null, Key("ExtraModifier"), [("shift", "Shift"), ("alt", "Alt"), ("control", "Ctrl")], ""));
        page.Children.Add(Choice("Applies to", null, Key("Scope"), [("all", "Every app"), ("selectedOnly", "Only the apps below"), ("allExceptSelected", "Every app except those below")], ""));
        page.Children.Add(Card("Apps", "Program names, separated by commas.", "", ProgramListBox(Key("Exceptions"), "steam.exe")));
        page.Children.Add(Toggle("Show a panel while waiting", null, Key("ShowFeedback"), ""));
    }

    // MARK: The snippet editor

    private void AddSnippetEditor(StackPanel page)
    {
        var settings = app.Settings;
        var snippets = SnippetSupport.Decode(settings.GetString(DefaultsKey.TextSnippets)).ToList();
        foreach (var snippet in snippets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = new Button { Content = "Edit", Margin = new Thickness(0, 0, 6, 0), Appearance = ControlAppearance.Secondary };
            edit.Click += (_, _) => { _editingSnippet = snippet; _newSnippet = false; Refresh(); };
            var delete = new Button { Content = "Delete", Appearance = ControlAppearance.Danger };
            delete.Click += (_, _) =>
            {
                settings.Set(DefaultsKey.TextSnippets, SnippetSupport.Encode(snippets.Where(s => s.Id != snippet.Id)));
                if (_editingSnippet?.Id == snippet.Id) _editingSnippet = null;
                Refresh();
            };
            row.Children.Add(edit);
            row.Children.Add(delete);
            var preview = snippet.Replacement.ReplaceLineEndings(" ");
            page.Children.Add(Card(string.IsNullOrWhiteSpace(snippet.Name) ? snippet.Trigger : snippet.Name,
                snippet.Trigger + "  \u2192  " + (preview.Length > 70 ? preview[..70] + "\u2026" : preview) + (snippet.Enabled ? "" : "  (off)"), "", row));
        }
        if (_editingSnippet is { } editing) page.Children.Add(SnippetForm(editing, snippets));
        else
        {
            var add = ActionButton("Add a snippet", () => { _editingSnippet = new TextSnippet(); _newSnippet = true; Refresh(); }, primary: true);
            page.Children.Add(add);
        }
    }

    private FrameworkElement SnippetForm(TextSnippet editing, List<TextSnippet> snippets)
    {
        var settings = app.Settings;
        var name = new TextBox { Text = editing.Name, PlaceholderText = "Name (optional)", Margin = new Thickness(0, 0, 0, 6) };
        var trigger = new TextBox { Text = editing.Trigger, PlaceholderText = "Trigger, like ;email", Margin = new Thickness(0, 0, 0, 6) };
        var text = new TextBox { Text = editing.Replacement, PlaceholderText = "What it turns into", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Margin = new Thickness(0, 0, 0, 6) };
        var folder = new TextBox { Text = editing.Folder, PlaceholderText = "Folder (optional)", Margin = new Thickness(0, 0, 0, 6) };
        var immediate = new CheckBox { Content = "Expand as soon as the last character is typed (otherwise after Space, Tab or Return)", IsChecked = editing.Expansion == "immediate", Margin = new Thickness(0, 0, 0, 4) };
        var ignoresCase = new CheckBox { Content = "Ignore case in the trigger", IsChecked = editing.IgnoresCase, Margin = new Thickness(0, 0, 0, 4) };
        var enabled = new CheckBox { Content = "On", IsChecked = editing.Enabled, Margin = new Thickness(0, 0, 0, 4) };
        var inLibrary = new CheckBox { Content = "Show in the library", IsChecked = editing.ShowsInLibrary, Margin = new Thickness(0, 0, 0, 8) };
        var message = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        var save = new Button { Content = _newSnippet ? "Add" : "Save", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var clean = SnippetSupport.SanitizedTrigger(trigger.Text);
            var check = SnippetSupport.Check(clean, ignoresCase.IsChecked == true, snippets.Where(s => s.Id != editing.Id));
            if (check != TriggerCheck.Valid)
            {
                message.Text = check switch
                {
                    TriggerCheck.TooShort => "A trigger needs at least 2 characters.",
                    TriggerCheck.TooLong => "A trigger can be at most 40 characters.",
                    TriggerCheck.Duplicate => "Another snippet already uses that trigger.",
                    _ => "A trigger cannot contain spaces.",
                };
                return;
            }
            var updated = editing with
            {
                Name = name.Text.Trim(), Trigger = clean, Replacement = text.Text, Folder = SnippetSupport.SanitizedFolder(folder.Text),
                Expansion = immediate.IsChecked == true ? "immediate" : "afterDelimiter", IgnoresCase = ignoresCase.IsChecked == true,
                Enabled = enabled.IsChecked == true, ShowsInLibrary = inLibrary.IsChecked == true,
            };
            var list = snippets.Where(s => s.Id != editing.Id).Append(updated).OrderBy(s => s.Trigger, StringComparer.OrdinalIgnoreCase);
            settings.Set(DefaultsKey.TextSnippets, SnippetSupport.Encode(list));
            _editingSnippet = null;
            Refresh();
        };
        var cancel = new Button { Content = "Cancel", Appearance = ControlAppearance.Secondary };
        cancel.Click += (_, _) => { _editingSnippet = null; Refresh(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var form = new StackPanel();
        foreach (UIElement child in new UIElement[] { name, trigger, text, folder, immediate, ignoresCase, enabled, inLibrary, message, buttons }) form.Children.Add(child);
        return Card(_newSnippet ? "New snippet" : "Edit snippet", null, "", form);
    }
}
