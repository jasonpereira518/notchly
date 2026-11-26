// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure parts of upstream Services/CommandBar/CommandBarService.swift: how a typed query becomes the list of rows
// (searchRows: the pool, aliases, learned boosts, the per-kind caps, the answer rows above) and how the empty bar is composed
// (suggestionRows: the selection, pins, most-used suggestions, browse groups, the newest copies). The service gathers the rows from
// the sources; this only decides which ones show and in what order. Platform-free.
using System.Runtime.CompilerServices;

namespace Notchly.Core.CommandBar;

/// <summary>What the person decided about the bar: sources switched off, names, pins and hidden rows.</summary>
public sealed record CommandBarPreferenceSnapshot(
    IReadOnlySet<CommandBarSource> Disabled,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> PinOrder,
    IReadOnlySet<string> Hidden)
{
    public static CommandBarPreferenceSnapshot Empty { get; } = new(new HashSet<CommandBarSource>(), new Dictionary<string, string>(), [], new HashSet<string>());

    public bool IsEnabled(CommandBarSource source) => CommandBarPreferences.IsEnabled(source, Disabled);
    public bool Allows(CommandBarEntry entry) => !Hidden.Contains(entry.StableKey) && IsEnabled(CommandBarPreferences.SourceOfRowId(entry.Id));
}

/// <summary>What the bar learned: how often rows were run, which row answered which letters, and what this session chose.</summary>
public sealed class CommandBarLearningContext
{
    public IReadOnlyDictionary<string, CommandBarUse> Usage { get; set; } = new Dictionary<string, CommandBarUse>();
    public CommandBarQueryHabitStoreCache Habits { get; init; } = new();
    public byte[]? HabitKey { get; set; }
    public CommandBarQueryHabits.PreparationCache Preparation { get; } = new();
    public CommandBarQueryMemory Memory { get; init; } = new();
}

/// <summary>The empty bar: its rows, and the headings that start groups (keyed by the index of the group's first row).</summary>
public sealed record CommandBarBrowse(IReadOnlyList<CommandBarEntry> Rows, IReadOnlyDictionary<int, string> Titles);

public static class CommandBarEngine
{
    public const int ResultLimit = 12, CategoryLimit = 40, SuggestionSlots = 7, BrowseGroupLimit = 12, ClipboardBrowseLimit = 6;

    /// <summary>How many rows each kind may contribute, so one provider never floods the list. Actions have no cap.</summary>
    public static readonly IReadOnlyList<(string Prefix, int Limit)> KindLimits =
    [
        ("app.", 5), ("window.", 4), ("quit.", 3), ("menu.", 5), ("emoji.", 6), ("settings.", 4), ("macsettings.", 4),
        ("clipboard.", 4), ("snippet.", 4), ("file.", 4), ("toggle.", 5),
    ];

    /// <summary>The ids of the commands offered first on a bar nobody has used yet (upstream's curated list, for what exists on Windows).</summary>
    public static readonly IReadOnlyList<string> CuratedSuggestionIds =
        ["action.screenOCR", "action.keepAwake", "action.clipboardWindow", "action.colorPicker", "action.darkMode", "action.snippetLibrary", "action.scratchpad", "action.layout.maximize"];

    private sealed record Folded(string Title, string Keywords);
    private static readonly ConditionalWeakTable<CommandBarEntry, Folded> FoldedCache = new();

    private static Folded FoldedOf(CommandBarEntry entry) =>
        FoldedCache.GetValue(entry, e => new Folded(CommandBarSearch.Normalized(e.SearchTitle), CommandBarSearch.Normalized(e.Keywords)));

    /// <summary>
    /// The folded text a row is ranked against. A row that reads an argument answers to the whole query instead: scored against its name
    /// alone it would leave the list the moment the argument was typed, which is exactly when it was about to run.
    /// </summary>
    private static string RankingTitle(CommandBarEntry entry, string query) =>
        entry.TakesArgument ? CommandBarSearch.Normalized(CommandBarLinks.RankingTitle(entry.Title, query)) : FoldedOf(entry).Title;

    /// <summary>System answers need a name, not a short prefix or a broad keyword: "st" must not answer "Storage".</summary>
    public static bool MatchesAnswerName(CommandBarEntry entry, string firstToken)
    {
        if (firstToken.Length < 3) return false;
        return CommandBarSearch.Normalized(entry.Title).StartsWith(firstToken, StringComparison.Ordinal)
               || (entry.EnglishTitle is { } english && CommandBarSearch.Normalized(english).StartsWith(firstToken, StringComparison.Ordinal));
    }

    // MARK: Typed search

    /// <summary>
    /// The rows for a typed query: answers on top, then the ranked pool with its caps, up to twelve. <paramref name="pool"/> is every
    /// row the sources offer right now, selection rows first so a tie goes to what the person is looking at.
    /// </summary>
    public static List<CommandBarEntry> Search(
        string trimmed, IReadOnlyList<CommandBarEntry> pool, CommandBarEntry? answer, CommandBarEntry? openUrl,
        CommandBarPreferenceSnapshot preferences, CommandBarLearningContext learning, double now,
        Func<string, bool>? numericCommandMatches = null, IReadOnlyList<CommandBarEntry>? leading = null)
    {
        var kept = new List<CommandBarEntry>(pool.Count);
        var sources = new List<CommandBarSource>(pool.Count);
        foreach (var entry in pool)
        {
            if (preferences.Hidden.Contains(entry.StableKey)) continue;
            var source = CommandBarPreferences.SourceOfRowId(entry.Id);
            if (!preferences.IsEnabled(source)) continue;
            kept.Add(entry);
            sources.Add(source);
        }

        // "volume 40" is a command with a value; "code 1234" is a search for something copied. The number is only taken off when a
        // command that actually takes one answers to what is left.
        var split = CommandBarSearch.SplitTrailingNumber(trimmed);
        var effective = split.Number is not null && numericCommandMatches?.Invoke(split.Text) == true ? split.Text : trimmed;

        var foldedQuery = CommandBarSearch.Normalized(effective);
        var habitQuery = CommandBarQueryHabits.Prepare(effective, learning.HabitKey, learning.Preparation);
        var pinned = preferences.PinOrder.ToHashSet(StringComparer.Ordinal);
        var candidates = new List<CommandBarCandidate>(kept.Count);
        for (var index = 0; index < kept.Count; index++)
        {
            var entry = kept[index];
            // A name the person gave outranks every title in the catalog: that is the whole point of giving it.
            var aliasBoost = preferences.Aliases.TryGetValue(entry.StableKey, out var alias)
                ? (int?)CommandBarPreferences.AliasHitNormalized(alias, foldedQuery) ?? 0 : 0;
            var habit = entry.CountsUsage ? CommandBarQueryHabits.Boost(entry.Id, habitQuery, learning.Habits.Store, now) : 0;
            var usage = entry.CountsUsage && learning.Usage.TryGetValue(entry.Id, out var use) ? CommandBarUsage.Boost(use, now) : 0;
            var memory = entry.CountsUsage ? learning.Memory.BoostNormalized(foldedQuery, entry.Id) : 0;
            var boost = usage + (entry.IsActive ? 20 : 0) + aliasBoost + memory + CommandBarPreferences.RankBias(sources[index])
                        + (pinned.Contains(entry.StableKey) ? CommandBarPreferences.PinTieBreak : 0);
            // The name the person gave is something the row answers to, so it has to be matchable text too.
            var keywords = alias is null ? FoldedOf(entry).Keywords : (FoldedOf(entry).Keywords + " " + CommandBarSearch.Normalized(alias)).Trim();
            candidates.Add(new CommandBarCandidate(index, RankingTitle(entry, effective), keywords, Math.Max(aliasBoost, habit), boost));
        }
        var ranked = CommandBarSearch.FeatureOrdered(
            CommandBarSearch.RankedIndexes(candidates, effective), i => kept[i].Id, i => candidates[i].Priority);

        var firstToken = foldedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var colorPreview = answer?.Id == "color.preview" ? answer : null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<CommandBarEntry>();
        if (answer is not null && colorPreview is null) result.Add(answer);
        if (openUrl is not null) result.Add(openUrl);
        if (leading is not null) result.AddRange(leading);
        foreach (var index in ranked)
        {
            var entry = kept[index];
            if (entry.Id.StartsWith("answer.", StringComparison.Ordinal) && !MatchesAnswerName(entry, firstToken)) continue;
            var kind = KindLimits.FirstOrDefault(k => entry.Id.StartsWith(k.Prefix, StringComparison.Ordinal));
            if (kind.Prefix is not null)
            {
                counts.TryGetValue(kind.Prefix, out var used);
                if (used >= kind.Limit) continue;
                counts[kind.Prefix] = used + 1;
            }
            result.Add(entry);
            if (result.Count >= ResultLimit) break;
        }
        if (colorPreview is not null)
        {
            result.Insert(Math.Min(result.Count, CommandBarSearch.ColorPreviewIndex(result.Select(r => r.Title), trimmed)), colorPreview);
            if (result.Count > ResultLimit) result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    /// <summary>Typing inside one category filters that category and nothing else: no answer row, no caps, up to forty rows.</summary>
    public static List<CommandBarEntry> SearchCategory(
        string trimmed, IReadOnlyList<CommandBarEntry> pool, CommandBarPreferenceSnapshot preferences, CommandBarLearningContext learning, double now)
    {
        var rows = pool.Where(e => !preferences.Hidden.Contains(e.StableKey)).ToList();
        var habitQuery = rows.Any(r => r.CountsUsage) ? CommandBarQueryHabits.Prepare(trimmed, learning.HabitKey, learning.Preparation) : null;
        var candidates = new List<CommandBarCandidate>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var entry = rows[index];
            var habit = entry.CountsUsage && habitQuery is not null ? CommandBarQueryHabits.Boost(entry.Id, habitQuery, learning.Habits.Store, now) : 0;
            candidates.Add(new CommandBarCandidate(index, RankingTitle(entry, trimmed), FoldedOf(entry).Keywords, habit));
        }
        return CommandBarSearch.RankedIndexes(candidates, trimmed).Take(CategoryLimit).Select(i => rows[i]).ToList();
    }

    /// <summary>The emoji path: a leading colon scopes the search to emoji, an empty one lists the first forty.</summary>
    public static List<CommandBarEntry> SearchEmoji(
        string emojiQuery, IReadOnlyList<CommandBarEntry> pool, CommandBarLearningContext learning, double now)
    {
        if (emojiQuery.Length == 0) return pool.Take(CategoryLimit).ToList();
        var habitQuery = CommandBarQueryHabits.Prepare(emojiQuery, learning.HabitKey, learning.Preparation);
        var candidates = new List<CommandBarCandidate>(pool.Count);
        for (var index = 0; index < pool.Count; index++)
        {
            var entry = pool[index];
            candidates.Add(new CommandBarCandidate(index, FoldedOf(entry).Title, FoldedOf(entry).Keywords,
                CommandBarQueryHabits.Boost(entry.Id, habitQuery, learning.Habits.Store, now)));
        }
        return CommandBarSearch.RankedIndexes(candidates, emojiQuery).Take(CategoryLimit).Select(i => pool[i]).ToList();
    }

    // MARK: The empty bar

    /// <summary>
    /// What the bar shows before anything is typed: what is selected, then the pins in the order they were pinned, then the most used
    /// commands (topped up from the curated list), then everything the bar can do grouped by area, then the newest copies. Apps and
    /// windows stay out: hundreds of them would bury the commands, and they are what search is for.
    /// </summary>
    public static CommandBarBrowse Browse(
        IReadOnlyList<CommandBarEntry> selection, string selectionPreview, IReadOnlyList<CommandBarEntry> offerable,
        IReadOnlyList<CommandBarEntry> catalog, IReadOnlyList<CommandBarEntry> clipboard,
        CommandBarPreferenceSnapshot preferences, CommandBarLearningContext learning, double now)
    {
        var rows = new List<CommandBarEntry>();
        var titles = new Dictionary<int, string>();
        if (selection.Count > 0)
        {
            titles[0] = selectionPreview.Length == 0 ? CommandBarStrings.SelectedTitle : CommandBarStrings.SelectedTitle + " · " + selectionPreview;
            rows.AddRange(selection);
        }

        var allowed = offerable.Where(preferences.Allows).ToList();
        var byKey = new Dictionary<string, CommandBarEntry>(StringComparer.Ordinal);
        foreach (var entry in allowed) byKey.TryAdd(entry.StableKey, entry);
        var pinnedRows = CommandBarPreferences.LeadingPins(preferences.PinOrder, byKey.Keys.ToHashSet(StringComparer.Ordinal))
            .Select(key => byKey[key]).ToList();
        if (pinnedRows.Count > 0)
        {
            titles[rows.Count] = CommandBarStrings.PinnedTitle;
            rows.AddRange(pinnedRows);
        }

        var pinnedIds = pinnedRows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var ids = CommandBarUsage.SuggestionIds(learning.Usage, allowed.Select(e => e.Id).Where(id => !pinnedIds.Contains(id)),
                                                CuratedSuggestionIds, Math.Max(0, SuggestionSlots - pinnedRows.Count));
        var byId = new Dictionary<string, CommandBarEntry>(StringComparer.Ordinal);
        foreach (var entry in allowed) byId.TryAdd(entry.Id, entry);
        var suggestions = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (suggestions.Count > 0)
        {
            titles[rows.Count] = CommandBarStrings.SuggestionsLabel;
            rows.AddRange(suggestions);
        }

        var shown = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        var groups = new Dictionary<string, List<CommandBarEntry>>(StringComparer.Ordinal);
        foreach (var entry in catalog)
        {
            if (shown.Contains(entry.Id) || !preferences.Allows(entry)) continue;
            shown.Add(entry.Id);
            var group = BrowseGroup(entry);
            if (!groups.TryGetValue(group, out var members)) { groups[group] = members = []; order.Add(group); }
            // The heading already says it; the row saying it again is noise.
            members.Add(entry.Subtitle == group ? entry.WithSubtitle("") : entry);
        }
        foreach (var group in order)
        {
            titles[rows.Count] = group;
            rows.AddRange(groups[group].Take(BrowseGroupLimit));
        }

        if (preferences.IsEnabled(CommandBarSource.Clipboard))
        {
            var copied = clipboard.Where(e => !preferences.Hidden.Contains(e.StableKey) && shown.Add(e.Id)).Take(ClipboardBrowseLimit).ToList();
            if (copied.Count > 0)
            {
                titles[rows.Count] = CommandBarStrings.KindClipboard;
                rows.AddRange(copied);
            }
        }
        return new CommandBarBrowse(rows, titles);
    }

    /// <summary>
    /// The heading a row browses under: most rows already carry the name of their area as their caption; the ones whose caption is a
    /// live detail (a battery reading, a folder path) are grouped by what they are.
    /// </summary>
    public static string BrowseGroup(CommandBarEntry entry) => CommandBarPreferences.SourceOfRowId(entry.Id) switch
    {
        CommandBarSource.Answers => CommandBarStrings.KindAnswer,
        CommandBarSource.Links => CommandBarStrings.KindLink,
        CommandBarSource.Snippets => CommandBarStrings.KindSnippet,
        CommandBarSource.Folders => CommandBarStrings.KindFolder,
        _ => entry.Subtitle.Length == 0 ? CommandBarStrings.EverythingTitle : entry.Subtitle,
    };
}
