// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarPreferences.swift: CommandBarSource and
// CommandBarPreferences (sources, aliases, pins, hidden rows, rank bias, panel position).
//
// Not ported: CommandBarSource.symbolName (SF Symbols) and skinTone(from:) (belongs with the emoji catalog).
// Storage ids and row-id prefixes are kept exactly as upstream, so saved lists stay compatible. "macSettings" is
// the system's own Settings pages; on Windows that means the ms-settings pages.
using System.Globalization;
using System.Text.Json;
using Notchly.Core.Notch;

namespace Notchly.Core.CommandBar;

/// <summary>A kind of result the bar can offer. Storage ids (<see cref="CommandBarSources.StorageId"/>) never change.</summary>
public enum CommandBarSource
{
    /// <summary>What Notchly itself can do. Always on: it is what the bar is for.</summary>
    Actions,
    Apps,
    Menus,
    Windows,
    QuitApps,
    /// <summary>Apps offered for uninstalling, browsed one at a time from the "Uninstall Application" row; never in the flat search pool.</summary>
    UninstallApps,
    SettingsPages,
    /// <summary>The system's own Settings pages, which are not Notchly's and can be switched off on their own.</summary>
    MacSettings,
    Snippets,
    Clipboard,
    Emoji,
    Folders,
    Answers,
    Calculator,
    /// <summary>What the person had selected when the bar opened.</summary>
    Selection,
    /// <summary>The links, folders and searches the person saved themselves.</summary>
    Links,
    /// <summary>Files found by name in the folders the person named. Last, because it is the one source that has to go and look.</summary>
    Files,
    KillProcess,
}

public static class CommandBarSources
{
    /// <summary>Every source in upstream's declaration order (row-id lookup depends on it).</summary>
    public static readonly IReadOnlyList<CommandBarSource> All = Enum.GetValues<CommandBarSource>();

    /// <summary>The persisted id of a source.</summary>
    public static string StorageId(this CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => "actions",
        CommandBarSource.Apps => "apps",
        CommandBarSource.Menus => "menus",
        CommandBarSource.Windows => "windows",
        CommandBarSource.QuitApps => "quitApps",
        CommandBarSource.UninstallApps => "uninstallApps",
        CommandBarSource.SettingsPages => "settingsPages",
        CommandBarSource.MacSettings => "macSettings",
        CommandBarSource.Snippets => "snippets",
        CommandBarSource.Clipboard => "clipboard",
        CommandBarSource.Emoji => "emoji",
        CommandBarSource.Folders => "folders",
        CommandBarSource.Answers => "answers",
        CommandBarSource.Calculator => "calculator",
        CommandBarSource.Selection => "selection",
        CommandBarSource.Links => "links",
        CommandBarSource.Files => "files",
        CommandBarSource.KillProcess => "killProcess",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static CommandBarSource? FromStorageId(string id) =>
        All.Cast<CommandBarSource?>().FirstOrDefault(s => s!.Value.StorageId() == id);

    /// <summary>The one source that cannot be switched off, because that would leave an empty bar and no way back.</summary>
    public static bool IsAlwaysOn(this CommandBarSource source) => source == CommandBarSource.Actions;

    /// <summary>The row ids this source owns; null for sources without a prefix of their own.</summary>
    public static string? IdPrefix(this CommandBarSource source) => source switch
    {
        CommandBarSource.Actions => null,
        CommandBarSource.Apps => "app.",
        CommandBarSource.Menus => "menu.",
        CommandBarSource.Windows => "window.",
        CommandBarSource.QuitApps => "quit.",
        CommandBarSource.UninstallApps => "uninstall.",
        CommandBarSource.SettingsPages => "settings.",
        CommandBarSource.MacSettings => "macsettings.",
        CommandBarSource.Snippets => "snippet.",
        CommandBarSource.Clipboard => "clipboard.",
        CommandBarSource.Emoji => "emoji.",
        CommandBarSource.Folders => "folder.",
        CommandBarSource.Answers => "answer.",
        CommandBarSource.Calculator => null,
        CommandBarSource.Selection => "selection.",
        CommandBarSource.Links => "link.",
        CommandBarSource.Files => "file.",
        CommandBarSource.KillProcess => "kill.",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

/// <summary>
/// Everything the person decides about the bar: which kinds of result it may offer, the names they gave things, and
/// what they pinned. Pure, so the rules are pinned by tests and the service only has to read them.
/// </summary>
public static class CommandBarPreferences
{
    /// <summary>Row shortcuts persist ids, so these must never drift.</summary>
    public const string EmojiBrowserRowId = "emoji.browse";
    public const string KillProcessBrowserRowId = "kill.browse";

    /// <summary>
    /// The rows that open another category. They are the app's own actions but carry the prefix of what they open,
    /// so <see cref="SourceOfRowId"/> files them under it and the Actions list has to name them.
    /// </summary>
    public static readonly IReadOnlySet<string> ActionBrowseRowIds = new HashSet<string>(StringComparer.Ordinal)
    {
        EmojiBrowserRowId, KillProcessBrowserRowId, "uninstall.browse", "uninstall.finder",
    };

    // ---- Sources -------------------------------------------------------------------------------------------------

    /// <summary>The sources switched off in a comma list. Unknown ids are dropped and the always-on source cannot be disabled.</summary>
    public static HashSet<CommandBarSource> DisabledSources(string raw)
    {
        var result = new HashSet<CommandBarSource>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CommandBarSources.FromStorageId(CommandBarSearch.TrimWhitespaces(part)) is { } source && !source.IsAlwaysOn())
                result.Add(source);
        }
        return result;
    }

    /// <summary>Sorted so the same set always writes the same string.</summary>
    public static string StorageValue(IEnumerable<CommandBarSource> sources) =>
        string.Join(',', sources.Where(s => !s.IsAlwaysOn()).Select(s => s.StorageId()).OrderBy(id => id, StringComparer.Ordinal));

    public static string EmojiRowId(string identity) => (CommandBarSource.Emoji.IdPrefix() ?? "") + identity;

    public static string? EmojiIdentity(string rowId)
    {
        var prefix = CommandBarSource.Emoji.IdPrefix();
        if (prefix is null || !rowId.StartsWith(prefix, StringComparison.Ordinal) || rowId.Length <= prefix.Length) return null;
        return rowId[prefix.Length..];
    }

    public static bool IsEnabled(CommandBarSource source, string disabledRaw) => IsEnabled(source, DisabledSources(disabledRaw));

    /// <summary>The same rule the empty bar and the search pool apply, over a set the caller already holds.</summary>
    public static bool IsEnabled(CommandBarSource source, IReadOnlySet<CommandBarSource> disabled) =>
        source.IsAlwaysOn() || !disabled.Contains(source);

    /// <summary>The source a row belongs to, decided by its id. Rows with no prefix of their own are the app's own actions.</summary>
    public static CommandBarSource SourceOfRowId(string id)
    {
        foreach (var source in CommandBarSources.All)
            if (source.IdPrefix() is { } prefix && id.StartsWith(prefix, StringComparison.Ordinal)) return source;
        return CommandBarSource.Actions;
    }

    /// <summary>Whether the Actions browse list shows this row: everything with no prefix of its own, plus the navigation rows.</summary>
    public static bool IsActionRow(string id) => SourceOfRowId(id) == CommandBarSource.Actions || ActionBrowseRowIds.Contains(id);

    /// <summary>
    /// Whether the Actions browse list shows this row given the sources switched off. A row that opens a category
    /// that is off stays out, as it does from the empty bar and the search pool.
    /// </summary>
    public static bool IsActionRow(string id, IReadOnlySet<CommandBarSource> disabled) =>
        IsActionRow(id) && IsEnabled(SourceOfRowId(id), disabled);

    /// <summary>
    /// What a kind of row is worth before a single letter of it is read. What this machine holds (apps, own actions,
    /// Settings pages) is what people mean; borrowed menu commands sit under an owned row of equal match quality.
    /// Small on purpose: a menu command that IS what was typed still beats an app that merely contains it.
    /// </summary>
    public static int RankBias(CommandBarSource source) => source switch
    {
        CommandBarSource.Menus => -80,
        // A file is the deepest and most numerous thing the bar can find, so it has to be a plainly better match to lead.
        CommandBarSource.Files or CommandBarSource.SettingsPages => -40,
        CommandBarSource.Apps => 80,
        _ => 0,
    };

    // ---- Aliases -------------------------------------------------------------------------------------------------

    /// <summary>Names the person gave to rows: "codex" for the chat app they think of that way. Several words are allowed and each finds the row.</summary>
    public static Dictionary<string, string> DecodeAliases(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var clean = property.Value.GetString()!.Trim();
                if (clean.Length > 0) result[property.Name] = clean;
            }
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
        return result;
    }

    public static string EncodeAliases(IReadOnlyDictionary<string, string> aliases) => JsonSerializer.Serialize(aliases, LearningJson.Options);

    /// <summary>
    /// An alias is only worth keeping on a row whose id survives: a menu item or a window is a different thing every
    /// time, and a name pinned to one would silently point somewhere else tomorrow.
    /// </summary>
    public static bool AcceptsAlias(string rowId) => SourceOfRowId(rowId) switch
    {
        CommandBarSource.Menus or CommandBarSource.Windows or CommandBarSource.Clipboard or CommandBarSource.Selection
            or CommandBarSource.Files or CommandBarSource.KillProcess or CommandBarSource.UninstallApps => false,
        _ => true,
    };

    /// <summary>The aliases after naming (or, with blank text, un-naming) a row. A name is cut to 60 characters.</summary>
    public static Dictionary<string, string> SettingAlias(string alias, string rowId, IReadOnlyDictionary<string, string> aliases)
    {
        var next = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        var clean = alias.Trim();
        if (clean.Length == 0) next.Remove(rowId);
        else next[rowId] = string.Concat(CommandBarSearch.Elements(clean).Take(60));
        return next;
    }

    public static Dictionary<string, string> RemovingAliases(IReadOnlySet<string> keys, IReadOnlyDictionary<string, string> aliases) =>
        keys.Count == 0
            ? new(aliases, StringComparer.Ordinal)
            : aliases.Where(a => !keys.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

    /// <summary>
    /// How well what was typed names an alias. A name the person gave has to outrank the app's own titles; a name
    /// being typed ranks just under a finished one. The values are ranking priorities.
    /// </summary>
    public enum AliasMatch
    {
        Exact = 2400,
        Prefix = 1100,
    }

    public static AliasMatch? AliasHit(string alias, string query) => AliasHitNormalized(alias, CommandBarSearch.Normalized(query));

    /// <summary>The same answer for letters the caller has already folded, so a pass over the pool folds the query once.</summary>
    public static AliasMatch? AliasHitNormalized(string alias, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return null;
        AliasMatch? best = null;
        foreach (var word in CommandBarSearch.Normalized(alias).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == normalizedQuery) return AliasMatch.Exact;
            if (word.StartsWith(normalizedQuery, StringComparison.Ordinal)) best = AliasMatch.Prefix;
        }
        return best;
    }

    public static bool AliasMatches(string alias, string query) => AliasHit(alias, query) is not null;

    /// <summary>Another row already answers to this name. Silently moving it would be worse than saying so.</summary>
    public static string? RowUsingAlias(string alias, IReadOnlyDictionary<string, string> aliases, string excluding)
    {
        var wanted = CommandBarSearch.Normalized(alias).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return null;
        foreach (var (rowKey, value) in aliases)
        {
            if (rowKey == excluding) continue;
            var words = CommandBarSearch.Normalized(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(wanted.Contains)) return rowKey;
        }
        return null;
    }

    // ---- Pins ----------------------------------------------------------------------------------------------------

    public const int PinLimit = 30;

    /// <summary>
    /// What a pin is worth on a typed query: enough to win a tie between two equally good matches, never enough to
    /// jump over a better one.
    /// </summary>
    public const int PinTieBreak = 60;

    /// <summary>A pin only means anything on a row that can lead the empty bar: not a menu command, quit row, emoji, clipboard item, selection, file or process.</summary>
    public static bool AcceptsPin(string rowId) => SourceOfRowId(rowId) switch
    {
        CommandBarSource.Menus or CommandBarSource.QuitApps or CommandBarSource.UninstallApps or CommandBarSource.Clipboard
            or CommandBarSource.Emoji or CommandBarSource.Selection or CommandBarSource.Files or CommandBarSource.KillProcess => false,
        _ => true,
    };

    public static List<string> DecodePins(string raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return raw.Split('\n').Select(CommandBarSearch.TrimWhitespaces).Where(p => p.Length > 0 && seen.Add(p)).ToList();
    }

    public static string EncodePins(IEnumerable<string> pins) => string.Join('\n', pins);

    /// <summary>Pinning is a toggle: the same gesture puts a row at the top and takes it back off. New pins go last; past 30 the oldest fall off.</summary>
    public static List<string> TogglingPin(string rowId, IReadOnlyList<string> pins)
    {
        var next = pins.ToList();
        var index = next.IndexOf(rowId);
        if (index >= 0) { next.RemoveAt(index); return next; }
        next.Add(rowId);
        if (next.Count > PinLimit) next.RemoveRange(0, next.Count - PinLimit);
        return next;
    }

    public static List<string> RemovingPins(IReadOnlySet<string> keys, IReadOnlyList<string> pins) =>
        keys.Count == 0 ? pins.ToList() : pins.Where(p => !keys.Contains(p)).ToList();

    /// <summary>On the empty bar the pins lead, in the order they were made.</summary>
    public static List<string> LeadingPins(IEnumerable<string> pins, IReadOnlySet<string> available) =>
        pins.Where(available.Contains).ToList();

    /// <summary>
    /// Settings lists the same pins the empty bar would, except an app or folder that is not here right now still
    /// appears so it can be taken off. A feature that was removed does not: leaving its id reads as a broken pin.
    /// </summary>
    public static List<string> ListedPins(IEnumerable<string> pins, IReadOnlySet<string> present) =>
        pins.Where(p => present.Contains(p) || !IsHubOwned(p)).ToList();

    private static bool IsHubOwned(string rowId) => SourceOfRowId(rowId) is CommandBarSource.Actions or CommandBarSource.SettingsPages or CommandBarSource.Snippets;

    // ---- Rows the person never wants to see ----------------------------------------------------------------------

    public static HashSet<string> DecodeHidden(string raw) =>
        raw.Split('\n').Select(CommandBarSearch.TrimWhitespaces).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);

    public static string EncodeHidden(IEnumerable<string> hidden) => string.Join('\n', hidden.OrderBy(h => h, StringComparer.Ordinal));

    public static HashSet<string> TogglingHidden(string key, IReadOnlySet<string> hidden)
    {
        var next = new HashSet<string>(hidden, StringComparer.Ordinal);
        if (!next.Remove(key)) next.Add(key);
        return next;
    }

    public static HashSet<string> RemovingHidden(IReadOnlySet<string> keys, IReadOnlySet<string> hidden)
    {
        var next = new HashSet<string>(hidden, StringComparer.Ordinal);
        if (keys.Count > 0) next.ExceptWith(keys);
        return next;
    }

    // ---- Position ------------------------------------------------------------------------------------------------

    /// <summary>
    /// How far the person dragged the bar from the spot it opens on by default, stored as an offset rather than a
    /// place ("a hand's width lower and to the left" means the same on every display, and a place remembered from a
    /// monitor that is gone would put the bar where nobody can see it). "dx,dy" in screen pixels; Width is dx and
    /// Height is dy. Anything else, including non-finite numbers, reads as no offset.
    /// </summary>
    public static SizeD DecodePositionOffset(string raw)
    {
        var parts = raw.Split(',');
        if (parts.Length != 2) return SizeD.Zero;
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        if (!double.TryParse(parts[0], style, CultureInfo.InvariantCulture, out var width) || !double.IsFinite(width)) return SizeD.Zero;
        if (!double.TryParse(parts[1], style, CultureInfo.InvariantCulture, out var height) || !double.IsFinite(height)) return SizeD.Zero;
        return new SizeD(width, height);
    }

    /// <summary>The offset rounded to whole pixels as "dx,dy"; empty for no offset or a value that is not a finite whole number.</summary>
    public static string EncodePositionOffset(SizeD offset)
    {
        if (!double.IsFinite(offset.Width) || !double.IsFinite(offset.Height)) return "";
        var width = Math.Round(offset.Width, MidpointRounding.AwayFromZero);
        var height = Math.Round(offset.Height, MidpointRounding.AwayFromZero);
        if (width == 0 && height == 0) return "";
        if (Math.Abs(width) >= 9.2e18 || Math.Abs(height) >= 9.2e18) return "";
        return $"{(long)width},{(long)height}";
    }

    public const double PanelMargin = 16;

    /// <summary>
    /// The top-left corner of the panel, kept fully inside the work area with a 16 px margin.
    ///
    /// Windows differs from upstream: screen coordinates grow DOWNWARD from the top-left, whereas the Mac's grow
    /// upward from the bottom-left. Upstream opens the panel with its top edge at 72% of the height measured from
    /// the bottom, so here the top edge sits at 28% of the work area height measured from the top. The offset is in
    /// the same downward coordinates (a positive dy moves the bar lower), and the returned point is the panel's
    /// top-left, not its bottom-left. Check against upstream's numbers: screen x -1440 y 0, 1440x900, panel 560x380;
    /// upstream's upper-right gives (-576, 504) with y up, which is (-576, 16) here (900 - 504 - 380), and its
    /// lower-left (-1424, 16) is (-1424, 504) here.
    /// </summary>
    public static PointD ClampedPanelOrigin(SizeD size, RectD workArea, SizeD offset)
    {
        var wantedX = workArea.MidX - size.Width / 2 + offset.Width;
        var wantedY = workArea.Y + workArea.Height * 0.28 + offset.Height;
        var minX = workArea.X + PanelMargin;
        var maxX = Math.Max(minX, workArea.Right - size.Width - PanelMargin);
        var minY = workArea.Y + PanelMargin;
        var maxY = Math.Max(minY, workArea.Bottom - size.Height - PanelMargin);
        return new PointD(Math.Min(Math.Max(wantedX, minX), maxX), Math.Min(Math.Max(wantedY, minY), maxY));
    }
}
