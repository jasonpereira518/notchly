// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarCatalog.swift (CommandBarEntry): one row the command bar can offer, something to find,
// read and run. Platform-free: what a row does is a closure, its icon is a description the window turns into a picture.

namespace Notchly.Core.CommandBar;

/// <summary>What a row's icon is made of.</summary>
public enum CommandBarIconKind
{
    /// <summary>A Segoe Fluent Icons glyph in a tinted plate.</summary>
    Glyph,
    /// <summary>The icon of an app, by its executable, shortcut file or package id.</summary>
    App,
    /// <summary>The icon of a file or folder, by path.</summary>
    File,
    /// <summary>A colour swatch.</summary>
    Color,
}

public readonly record struct CommandBarIcon(CommandBarIconKind Kind, string Value = "", Tools.ColorValue? Color = null)
{
    public static CommandBarIcon Glyph(string glyph) => new(CommandBarIconKind.Glyph, glyph);
    public static CommandBarIcon App(string path) => new(CommandBarIconKind.App, path);
    public static CommandBarIcon File(string path) => new(CommandBarIconKind.File, path);
    public static CommandBarIcon Swatch(Tools.ColorValue color) => new(CommandBarIconKind.Color, "", color);
}

/// <summary>Why Return will not do the main thing yet: the row says so in one sentence instead of failing silently.</summary>
public abstract record CommandBarTrouble
{
    /// <summary>The feature is installed but its switch is off; Return opens the settings page where it lives.</summary>
    public sealed record NeedsSetup(string FeatureTitle, string SettingsPage) : CommandBarTrouble;
    /// <summary>The feature is not installed; Return opens Features.</summary>
    public sealed record NotInstalled(string FeatureTitle) : CommandBarTrouble;
}

public sealed record CommandBarEntry(
    string Id,
    /// <summary>What a name, a pin or a habit is filed under: it has to survive what the id cannot (an app moving folder keeps its package id).</summary>
    string StableKey,
    string Title,
    string Subtitle,
    CommandBarIcon Icon,
    Action<int?> Run)
{
    public string? EnglishTitle { get; init; }
    public string Keywords { get; init; } = "";
    /// <summary>A global shortcut the person gave the row, in storage form (<c>control+alt+win:78</c>).</summary>
    public string? Shortcut { get; init; }
    /// <summary>A shortcut that belongs to someone else's menu, already written out ("Ctrl+S").</summary>
    public string? MenuShortcut { get; init; }
    /// <summary>A live state worth a small badge (keep awake on, mic muted).</summary>
    public bool IsActive { get; init; }
    public CommandBarTrouble? Trouble { get; init; }
    /// <summary>Present on commands that take a number ("volume 40"): the bound range.</summary>
    public (int Min, int Max)? NumericRange { get; init; }
    /// <summary>True when the number is welcome but not required, so Return without one still does the obvious thing.</summary>
    public bool NumericIsOptional { get; init; }
    /// <summary>The phrase the row shows before a destructive Return.</summary>
    public string? ConfirmationPrompt { get; init; }
    /// <summary>A fact this row answers, shown on the right in the accent colour.</summary>
    public string? AnswerValue { get; init; }
    /// <summary>The calculator's row: above everything and styled as a result.</summary>
    public bool IsAnswer { get; init; }
    /// <summary>Only durable identities may retain habits; window handles and process ids are reused.</summary>
    public bool CountsUsage { get; init; } = true;
    /// <summary>The text the ranking reads instead of the title, for rows whose title carries something that is not a word (an emoji's glyph).</summary>
    public string? MatchTitle { get; init; }
    /// <summary>A row whose whole point is to leave the field standing: it puts something into the bar instead of doing something with it.</summary>
    public bool KeepsBarOpen { get; init; }
    /// <summary>A row that reads whatever is typed after its own name, the way a saved search does.</summary>
    public bool TakesArgument { get; init; }
    /// <summary>Where this row lives on the disk, for rows that are a real file: an app, a folder, a place the person saved.</summary>
    public string? RevealPath { get; init; }

    public bool CanReveal => RevealPath is not null;

    /// <summary>Whether running the row asks something first: a confirmation, a number, or a trip to settings. A global shortcut must do the same.</summary>
    public bool NeedsPrompt =>
        ConfirmationPrompt is not null || (NumericRange is not null && !NumericIsOptional) || Trouble is not null;

    /// <summary>The same row with a different caption, for the browse list under a heading that already says what it is.</summary>
    public CommandBarEntry WithSubtitle(string subtitle) => this with { Subtitle = subtitle };

    public bool IsColor => Icon.Kind == CommandBarIconKind.Color;

    /// <summary>The title the ranking reads.</summary>
    public string SearchTitle => MatchTitle ?? Title;
}
