// SPDX-License-Identifier: GPL-3.0-or-later
// The top strip's menu bar (Windows-only; macOS draws its own): what each title and item is and
// what choosing it does. Titles come from the app's classic Win32 menu, its UI Automation menu bar,
// or the standard set for apps with no menus.
using Notchly.Core.Shortcuts;

namespace Notchly.Core.MenuBar;

public enum WindowAction { Minimize, ToggleMaximize, SnapLeft, SnapRight, Close, HideApp, QuitApp }

/// <summary>What choosing an item does.</summary>
public abstract record MenuCommand
{
    private MenuCommand() { }

    /// <summary>The usual shortcut, sent to the app in front.</summary>
    public sealed record SendKeys(Shortcut Chord) : MenuCommand;

    /// <summary>Done to the window directly (WM_SYSCOMMAND, SetWindowPos), never with keys.</summary>
    public sealed record Window(WindowAction Action) : MenuCommand;

    /// <summary>A classic menu item: WM_COMMAND with its id, exactly what the app's own menu sends.</summary>
    public sealed record NativeItem(int Id) : MenuCommand;
}

public enum MenuNodeKind { Command, Separator, Submenu }

public sealed record MenuNode(string Title, MenuNodeKind Kind = MenuNodeKind.Command)
{
    public MenuCommand? Command { get; init; }
    /// <summary>Shown at the right of the item, "Ctrl+S".</summary>
    public string? Shortcut { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Checked { get; init; }
    public IReadOnlyList<MenuNode> Children { get; init; } = [];
    /// <summary>Why an item is unavailable, shown as its tooltip.</summary>
    public string? Note { get; init; }

    public static MenuNode Separator { get; } = new("", MenuNodeKind.Separator);
}

public enum MenuSource
{
    /// <summary>Built by Notchly: the app menu and the standard set.</summary>
    Standard,
    /// <summary>A classic Win32 menu: its items are read when the title is clicked, and drop down under the bar.</summary>
    Native,
    /// <summary>A UI Automation menu bar: clicking opens the app's own menu in its window.</summary>
    Automation,
}

/// <summary>A title in the bar. <see cref="Index"/> is its position in the app's own menu bar.</summary>
public sealed record MenuTitle(string Title, MenuSource Source, int Index = -1)
{
    public IReadOnlyList<MenuNode> Items { get; init; } = [];
    public bool Bold { get; init; }
}
