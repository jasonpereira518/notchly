// SPDX-License-Identifier: GPL-3.0-or-later
// The bar's titles for the app in front: its bold app menu, then its own menus, or for an app with
// none the Mac's usual File, Edit, View, Window and Help, whose items send the usual Windows shortcut.
using Islet.Core.Shortcuts;

namespace Islet.Core.MenuBar;

public static class StandardMenus
{
    public const string ElevatedNote = "Runs as administrator: Windows doesn't let other apps send it keys or commands.";

    private const ShortcutModifiers Ctrl = ShortcutModifiers.Control;
    private const ShortcutModifiers None = ShortcutModifiers.None;

    private static MenuNode Keys(string title, ShortcutModifiers modifiers, int key, bool elevated)
    {
        var chord = new Shortcut(key, modifiers);
        return new MenuNode(title)
        {
            Command = new MenuCommand.SendKeys(chord), Shortcut = chord.DisplayText,
            Enabled = !elevated, Note = elevated ? ElevatedNote : null,
        };
    }

    private static MenuNode Window(string title, WindowAction action, bool elevated) => new(title)
    {
        Command = new MenuCommand.Window(action), Enabled = !elevated, Note = elevated ? ElevatedNote : null,
    };

    public static MenuTitle AppMenu(string app, bool elevated) => new(app, MenuSource.Standard)
    {
        Bold = true,
        Items = [Window($"Hide {app}", WindowAction.HideApp, elevated), MenuNode.Separator, Window($"Quit {app}", WindowAction.QuitApp, elevated)],
    };

    public static MenuTitle File(bool elevated) => new("File", MenuSource.Standard)
    {
        Items =
        [
            Keys("New window", Ctrl, 'N', elevated), Keys("Open…", Ctrl, 'O', elevated), MenuNode.Separator,
            Keys("Save", Ctrl, 'S', elevated), Keys("Save as…", Ctrl | ShortcutModifiers.Shift, 'S', elevated), MenuNode.Separator,
            Keys("Print…", Ctrl, 'P', elevated), MenuNode.Separator, Window("Close window", WindowAction.Close, elevated),
        ],
    };

    public static MenuTitle Edit(bool elevated) => new("Edit", MenuSource.Standard)
    {
        Items =
        [
            Keys("Undo", Ctrl, 'Z', elevated), Keys("Redo", Ctrl, 'Y', elevated), MenuNode.Separator,
            Keys("Cut", Ctrl, 'X', elevated), Keys("Copy", Ctrl, 'C', elevated), Keys("Paste", Ctrl, 'V', elevated),
            Keys("Select all", Ctrl, 'A', elevated), MenuNode.Separator, Keys("Find…", Ctrl, 'F', elevated),
        ],
    };

    public static MenuTitle View(bool elevated) => new("View", MenuSource.Standard)
    {
        Items =
        [
            Keys("Zoom in", Ctrl, 0xBB, elevated), Keys("Zoom out", Ctrl, 0xBD, elevated), Keys("Actual size", Ctrl, '0', elevated),
            MenuNode.Separator, Keys("Refresh", None, 0x74, elevated), Keys("Full screen", None, 0x7A, elevated),
        ],
    };

    public static MenuTitle WindowMenu(bool maximized, bool elevated) => new("Window", MenuSource.Standard)
    {
        Items =
        [
            Window("Minimize", WindowAction.Minimize, elevated),
            Window(maximized ? "Restore" : "Maximize", WindowAction.ToggleMaximize, elevated), MenuNode.Separator,
            Window("Snap to left half", WindowAction.SnapLeft, elevated), Window("Snap to right half", WindowAction.SnapRight, elevated),
            MenuNode.Separator, Window("Close window", WindowAction.Close, elevated),
        ],
    };

    public static MenuTitle Help(string app, bool elevated) => new("Help", MenuSource.Standard)
    {
        Items = [Keys($"{app} Help", None, 0x70, elevated)],
    };

    /// <summary>
    /// The bar for an app: its bold app menu, then its own menus (with the Window menu added when it
    /// has none) or, without menus, the standard File, Edit, View, Window and Help.
    /// </summary>
    public static IReadOnlyList<MenuTitle> Compose(string app, bool elevated, bool maximized, IReadOnlyList<MenuTitle>? own)
    {
        var titles = new List<MenuTitle> { AppMenu(app, elevated) };
        if (own is { Count: > 0 })
        {
            titles.AddRange(own);
            if (!own.Any(t => string.Equals(t.Title, "Window", StringComparison.OrdinalIgnoreCase)))
                titles.Add(WindowMenu(maximized, elevated));
        }
        else titles.AddRange([File(elevated), Edit(elevated), View(elevated), WindowMenu(maximized, elevated), Help(app, elevated)]);
        return titles;
    }

    /// <summary>
    /// A classic menu string: "&amp;File" is "File", "&amp;&amp;" a literal ampersand, and "Save\tCtrl+S" carries its
    /// shortcut after the tab.
    /// </summary>
    public static (string Title, string? Shortcut) SplitMenuText(string raw)
    {
        var tab = raw.IndexOf('\t');
        var title = tab >= 0 ? raw[..tab] : raw;
        var shortcut = tab >= 0 ? raw[(tab + 1)..].Trim() : null;
        var clean = new System.Text.StringBuilder(title.Length);
        for (var i = 0; i < title.Length; i++)
        {
            if (title[i] != '&') { clean.Append(title[i]); continue; }
            if (i + 1 < title.Length && title[i + 1] == '&') { clean.Append('&'); i++; }
        }
        return (clean.ToString().Trim(), string.IsNullOrEmpty(shortcut) ? null : shortcut);
    }
}
