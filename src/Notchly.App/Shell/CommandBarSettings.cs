// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools → Command bar: the Windows counterpart of upstream's UI/Settings/CommandBarSettings.swift. The shortcut, compact mode,
// the emoji skin tone, what the bar searches, the folders it looks for files in, your saved links, and the names, pins and hidden rows
// you gave it. Upstream's strings, with Windows' names for its controls.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Notchly.App.CommandBar;
using Notchly.Core.CommandBar;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string CommandBarNote = "One shortcut opens a field over whatever you are doing. Type a few letters, press Return and it happens. Nothing you type is saved.";
    private const string CommandBarShortcutNote = "Click, then press the keys. Alt+Space normally opens a window's own menu; the bar takes it over while this is on.";

    private void AddCommandBarSection(StackPanel page)
    {
        var settings = app.Settings;
        page.Children.Add(Section("Command bar"));
        page.Children.Add(Caption(CommandBarNote));
        if (!settings.IsAvailable(AppFeature.CommandBar)) page.Children.Add(Caption("Install Command bar in Features to use it."));
        page.Children.Add(Toggle("Open it with a shortcut", null, DefaultsKey.CommandBarShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", CommandBarShortcutNote, "", new ShortcutRecorder(settings, DefaultsKey.CommandBarShortcut)));
        page.Children.Add(Toggle("Compact mode", "Bar opens without suggestions. Results appear as you type.", DefaultsKey.CommandBarCompactMode, ""));
        page.Children.Add(Choice("Skin tone", "By default, emoji that can take a tone use this one.", DefaultsKey.CommandBarEmojiSkinTone,
            [("", "Default"), ("light", "Light"), ("mediumLight", "Medium light"), ("medium", "Medium"), ("mediumDark", "Medium dark"), ("dark", "Dark")]));

        page.Children.Add(Section("What the bar searches"));
        page.Children.Add(Caption("Turn off what you never want to see. Your own actions always stay."));
        var disabled = CommandBarPreferences.DisabledSources(settings.GetString(DefaultsKey.CommandBarDisabledSources));
        (CommandBarSource Source, string Title)[] sources =
        [
            (CommandBarSource.Apps, CommandBarStrings.SourceApps), (CommandBarSource.Windows, CommandBarStrings.SourceWindows),
            (CommandBarSource.Menus, CommandBarStrings.SourceMenus), (CommandBarSource.QuitApps, CommandBarStrings.SourceQuitApps),
            (CommandBarSource.SettingsPages, CommandBarStrings.SourceSettingsPages), (CommandBarSource.MacSettings, CommandBarStrings.SourceMacSettings),
            (CommandBarSource.Clipboard, CommandBarStrings.SourceClipboard), (CommandBarSource.Emoji, CommandBarStrings.SourceEmoji),
            (CommandBarSource.Folders, CommandBarStrings.SourceFolders), (CommandBarSource.Answers, CommandBarStrings.SourceAnswers),
            (CommandBarSource.Calculator, CommandBarStrings.SourceCalculator), (CommandBarSource.Selection, CommandBarStrings.SourceSelection),
            (CommandBarSource.Links, CommandBarStrings.SourceLinks), (CommandBarSource.Files, CommandBarStrings.SourceFiles),
        ];
        foreach (var (source, title) in sources)
        {
            var toggle = new ToggleSwitch { IsChecked = !disabled.Contains(source), Margin = new Thickness(8, 0, 0, 0) };
            var captured = source;
            toggle.Click += (_, _) =>
            {
                var now = CommandBarPreferences.DisabledSources(settings.GetString(DefaultsKey.CommandBarDisabledSources));
                if (toggle.IsChecked == true) now.Remove(captured); else now.Add(captured);
                settings.Set(DefaultsKey.CommandBarDisabledSources, CommandBarPreferences.StorageValue(now));
            };
            page.Children.Add(Card(title, null, "", toggle));
        }

        page.Children.Add(Section("Search your files"));
        page.Children.Add(Caption("Name the folders to look in and the bar finds files by name as you type, through Windows Search. It looks only where you say."));
        var scopes = CommandBarFileSearchSupport.DecodeList(settings.GetString(DefaultsKey.CommandBarFileScopes));
        if (scopes.Count == 0) page.Children.Add(Caption(CommandBarStrings.FilesNoFolders));
        foreach (var scope in scopes)
        {
            var captured = scope;
            page.Children.Add(Card(Path.GetFileName(scope.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : scope, scope, "",
                ActionButton("Remove", () =>
                {
                    settings.Set(DefaultsKey.CommandBarFileScopes, CommandBarFileSearchSupport.EncodeList(scopes.Where(s => s != captured)));
                    Refresh();
                })));
        }
        page.Children.Add(ActionButton("Add a folder", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder to search" };
            if (dialog.ShowDialog() != true) return;
            var list = scopes.ToList();
            if (!list.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) list.Add(dialog.FolderName);
            settings.Set(DefaultsKey.CommandBarFileScopes, CommandBarFileSearchSupport.EncodeList(list));
            Refresh();
        }));
        var ignores = new TextBox { PlaceholderText = "node_modules, *.log", MinWidth = 260, Text = string.Join(", ", CommandBarFileSearchSupport.DecodeList(settings.GetString(DefaultsKey.CommandBarFileIgnores))) };
        ignores.LostFocus += (_, _) => settings.Set(DefaultsKey.CommandBarFileIgnores,
            CommandBarFileSearchSupport.EncodeList(ignores.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        page.Children.Add(Card("Never show", "Names never worth showing: a whole folder or file name, or an extension written as *.log.", "", ignores));

        AddCommandBarLinks(page);
        AddCommandBarLists(page);

        page.Children.Add(ActionButton("Open the bar now", () => app.Features.Get<CommandBarService>(AppFeature.CommandBar)?.Open()));
        page.Children.Add(ActionButton("Recenter the bar", () => settings.Set(DefaultsKey.CommandBarPositionOffset, "")));
        page.Children.Add(ActionButton("Forget what I use most", () => app.Features.Get<CommandBarService>(AppFeature.CommandBar)?.ForgetLearning()));
    }

    private void AddCommandBarLinks(StackPanel page)
    {
        var settings = app.Settings;
        page.Children.Add(Section("Your shortcuts"));
        page.Children.Add(Caption("A site, a search or a folder you open every day. Type its name in the bar. Use {query}, {clipboard}, {selection} or {date} in a destination and it is filled in when it opens."));
        var links = CommandBarLinks.Decode(settings.GetString(DefaultsKey.CommandBarLinks));
        if (links.Count == 0) page.Children.Add(Caption("Nothing saved yet. Add a site, folder or search you use every day."));
        foreach (var link in links)
        {
            var captured = link;
            page.Children.Add(Card(link.Name, link.Destination, "", ActionButton("Remove", () =>
            {
                settings.Set(DefaultsKey.CommandBarLinks, CommandBarLinks.Encode(links.Where(l => l.Id != captured.Id)));
                Refresh();
            })));
        }
        var name = new TextBox { PlaceholderText = "Name", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var destination = new TextBox { PlaceholderText = "https://www.bing.com/search?q={query}  or  C:\\Folder", MinWidth = 300, Margin = new Thickness(0, 0, 8, 0) };
        var kind = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
        kind.Items.Add(new ComboBoxItem { Content = "Site or link", Tag = CommandBarLinkKind.Link });
        kind.Items.Add(new ComboBoxItem { Content = "Folder or file", Tag = CommandBarLinkKind.Place });
        kind.SelectedIndex = 0;
        var bare = new CheckBox { Content = "Also open when its name is typed on its own", Margin = new Thickness(0, 6, 0, 0) };
        var form = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { name, destination, kind } });
        form.Children.Add(bare);
        form.Children.Add(ActionButton("Add shortcut", () =>
        {
            if (name.Text.Trim().Length == 0 || destination.Text.Trim().Length == 0 || links.Count >= CommandBarLinks.Limit) return;
            var added = new CommandBarLink
            {
                Name = name.Text.Trim(), Destination = destination.Text.Trim(),
                Kind = (CommandBarLinkKind)((ComboBoxItem)kind.SelectedItem!).Tag, RunsWithoutArgument = bare.IsChecked == true,
            };
            settings.Set(DefaultsKey.CommandBarLinks, CommandBarLinks.Encode(links.Append(added)));
            Refresh();
        }));
        page.Children.Add(form);
    }

    private void AddCommandBarLists(StackPanel page)
    {
        var settings = app.Settings;
        var aliases = CommandBarPreferences.DecodeAliases(settings.GetString(DefaultsKey.CommandBarAliases));
        page.Children.Add(Section("Names you gave"));
        if (aliases.Count == 0) page.Children.Add(Caption("Nothing named yet. Open the bar, pick a row and press Ctrl+K."));
        foreach (var (key, alias) in aliases)
        {
            var captured = key;
            page.Children.Add(Card(alias, Friendly(key), "", ActionButton("Remove", () =>
            {
                settings.Set(DefaultsKey.CommandBarAliases, CommandBarPreferences.EncodeAliases(CommandBarPreferences.RemovingAliases(new HashSet<string> { captured }, aliases)));
                Refresh();
            })));
        }
        var pins = CommandBarPreferences.DecodePins(settings.GetString(DefaultsKey.CommandBarPins));
        page.Children.Add(Section(CommandBarStrings.PinnedTitle));
        if (pins.Count == 0) page.Children.Add(Caption("Nothing pinned yet."));
        foreach (var pin in pins)
        {
            var captured = pin;
            page.Children.Add(Card(Friendly(pin), null, "", ActionButton("Remove", () =>
            {
                settings.Set(DefaultsKey.CommandBarPins, CommandBarPreferences.EncodePins(CommandBarPreferences.RemovingPins(new HashSet<string> { captured }, pins)));
                Refresh();
            })));
        }
        var hidden = CommandBarPreferences.DecodeHidden(settings.GetString(DefaultsKey.CommandBarHidden));
        page.Children.Add(Section("Never shown"));
        if (hidden.Count == 0) page.Children.Add(Caption("Nothing hidden."));
        foreach (var key in hidden.Order(StringComparer.Ordinal))
        {
            var captured = key;
            page.Children.Add(Card(Friendly(key), null, "", ActionButton("Show again", () =>
            {
                settings.Set(DefaultsKey.CommandBarHidden, CommandBarPreferences.EncodeHidden(CommandBarPreferences.RemovingHidden(new HashSet<string> { captured }, hidden)));
                Refresh();
            })));
        }
    }

    /// <summary>A stored row key as something a person can read: the part after the source's prefix.</summary>
    private static string Friendly(string key)
    {
        var dot = key.IndexOf('.');
        var rest = dot >= 0 ? key[(dot + 1)..] : key;
        return rest.Length > 70 ? rest[..70] + "…" : rest;
    }
}
