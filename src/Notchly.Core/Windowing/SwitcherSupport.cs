// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Switcher/SwitcherSupport.swift and WindowUseOrder.swift (the pure parts): where the
// selection starts, which entries survive the cap, the grid, the most-recently-used order, the filters, the search, what
// closing a window does to the selection, and how a wheel moves it. Windows differences: no Spaces (virtual desktops are
// handled by the inventory), and the W, Q and S commands go by virtual key, which Windows keeps by key position on
// every layout.
using System.Globalization;
using System.Text;

namespace Notchly.Core.Windowing;

public enum SwitcherScope { AllApps, FrontmostApp, AllWindows }
public enum MinimizedPlacement { Normal, End, Hidden }
public enum WindowlessMode { Off, Explorer, All }
public enum AppRule { None, ShowWithoutWindows, WindowsOnly, Hidden }
public enum SwitcherLetter { None, CloseWindow, QuitApp, PinSearch }

/// <summary>One entry of the switcher as the pure logic sees it.</summary>
public sealed record SwitcherEntry(int Id, int Pid, string AppId, string AppName, string Title, bool IsMinimized = false, bool IsHiddenApp = false,
                                   bool IsFullscreen = false, bool HasWindow = true, bool IsForeground = false);

public static class SwitcherSupport
{
    public const int MaxEntries = 48;
    public const int HistoryLimit = 256;
    public const double CloseRemovalDelay = 0.18;
    public const double ScrollIdleSeconds = 0.25;
    public const double PrecisionStep = 30;

    // MARK: Sanitizers

    /// <summary>0 means automatic; anything else is kept between 2 and 10 columns.</summary>
    public static int? SanitizedMaxColumns(int columns) => columns <= 0 ? null : Math.Clamp(columns, 2, 10);

    public static int SanitizedAppearanceDelay(int milliseconds) => Math.Clamp(milliseconds, 0, 500);

    // MARK: Where it starts

    /// <summary>
    /// The first selection: the foreground window is entry 0, so the first press lands on 1 (the last entry when going backwards);
    /// without a foreground entry it is the first entry of another app. A lone entry is entry 0.
    /// </summary>
    public static int InitialSelection(IReadOnlyList<int> pids, bool hasForegroundEntry, int frontmostPid, bool reversed)
    {
        if (pids.Count <= 1) return 0;
        if (hasForegroundEntry) return reversed ? pids.Count - 1 : 1;
        if (reversed) return pids.Count - 1;
        for (var i = 0; i < pids.Count; i++) if (pids[i] != frontmostPid) return i;
        return 0;
    }

    /// <summary>
    /// Which entries stay when there are too many: the first two, then the first window of every app in order, then whatever
    /// room is left from the front. The result is in list order.
    /// </summary>
    public static IReadOnlyList<int> VisibleSelectionIndices(IReadOnlyList<int> pids, int limit)
    {
        if (limit <= 0) return [];
        if (pids.Count <= limit) return Enumerable.Range(0, pids.Count).ToList();
        var kept = new SortedSet<int>(Enumerable.Range(0, Math.Min(2, pids.Count)).Take(limit));
        var seen = kept.Select(i => pids[i]).ToHashSet();
        for (var i = 0; i < pids.Count && kept.Count < limit; i++)
            if (!kept.Contains(i) && seen.Add(pids[i])) kept.Add(i);
        for (var i = 0; i < pids.Count && kept.Count < limit; i++) kept.Add(i);
        return kept.ToList();
    }

    /// <summary>Columns of the grid: as many as fit up to the limit, with the rows balanced (10 entries on 8 columns is 5 and 5).</summary>
    public static int GridColumnCount(int count, int maxColumns)
    {
        if (count <= 0) return 1;
        maxColumns = Math.Max(1, maxColumns);
        if (count <= maxColumns) return count;
        var rows = (int)Math.Ceiling(count / (double)maxColumns);
        return (int)Math.Ceiling(count / (double)rows);
    }

    // MARK: Most recently used

    /// <summary>
    /// The order of the windows: those in the focus history first, newest first; then the rest by how recently their app was used,
    /// then front-to-back.
    /// </summary>
    public static IReadOnlyList<int> Order(IReadOnlyList<(int Id, int Pid)> frontToBack, IReadOnlyList<int> focusHistory, IReadOnlyList<int> appRank)
    {
        var live = frontToBack.Select(w => w.Id).ToHashSet();
        var ranked = focusHistory.Where(live.Contains).Distinct().ToList();
        var rest = frontToBack.Select((w, z) => (w, z)).Where(p => !ranked.Contains(p.w.Id))
            .OrderBy(p => { var at = appRank.ToList().IndexOf(p.w.Pid); return at < 0 ? int.MaxValue : at; }).ThenBy(p => p.z).Select(p => p.w.Id);
        return [.. ranked, .. rest];
    }

    /// <summary>Choosing a window puts it first and the one it replaced second, so a quick repeat flips between the two.</summary>
    public static IReadOnlyList<int> Promoting(IReadOnlyList<int> history, int? target, int? previous)
    {
        var result = new List<int>();
        foreach (var id in new[] { target, previous }) if (id is { } v && !result.Contains(v)) result.Add(v);
        result.AddRange(history.Where(h => !result.Contains(h)));
        return result.Take(HistoryLimit).ToList();
    }

    /// <summary>The history without windows that are gone, with the ones it never saw added behind it, front to back.</summary>
    public static IReadOnlyList<int> Reconciled(IReadOnlyList<int> history, IReadOnlyList<int> liveFrontToBack)
    {
        var live = liveFrontToBack.ToHashSet();
        var kept = history.Where(live.Contains).Distinct().ToList();
        return [.. kept, .. liveFrontToBack.Where(id => !kept.Contains(id))];
    }

    // MARK: Filters

    /// <summary>Minimized windows stay in place, go to the end (keeping their order), or are left out.</summary>
    public static IReadOnlyList<SwitcherEntry> PlaceMinimized(IReadOnlyList<SwitcherEntry> entries, MinimizedPlacement placement, bool hiddenAppsLikeMinimized)
    {
        bool away(SwitcherEntry e) => e.IsMinimized || hiddenAppsLikeMinimized && e.IsHiddenApp;
        return placement switch
        {
            MinimizedPlacement.Hidden => entries.Where(e => !away(e)).ToList(),
            MinimizedPlacement.End => [.. entries.Where(e => !away(e)), .. entries.Where(away)],
            _ => entries,
        };
    }

    /// <summary>An app's own rule: hidden removes its entries, "windows only" removes its windowless entry, "show without windows" keeps it.</summary>
    public static IReadOnlyList<SwitcherEntry> ApplyAppRules(IReadOnlyList<SwitcherEntry> entries, IReadOnlyDictionary<string, AppRule> rules) =>
        entries.Where(e =>
        {
            var rule = rules.TryGetValue(e.AppId, out var r) ? r : AppRule.None;
            return rule switch
            {
                AppRule.Hidden => false,
                AppRule.WindowsOnly => e.HasWindow,
                _ => true,
            };
        }).ToList();

    /// <summary>Which apps with no window still get an entry: none, only File Explorer, or all (plus any app a rule asks for).</summary>
    public static bool ShowsWithoutWindow(string appId, WindowlessMode mode, IReadOnlyDictionary<string, AppRule> rules)
    {
        var rule = rules.TryGetValue(appId, out var r) ? r : AppRule.None;
        if (rule == AppRule.ShowWithoutWindows) return true;
        if (rule is AppRule.WindowsOnly or AppRule.Hidden) return false;
        return mode == WindowlessMode.All || mode == WindowlessMode.Explorer && string.Equals(appId, "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    // MARK: Search

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>Every word of the query appears in the title or the app's name, ignoring case and accents.</summary>
    public static bool Matches(string query, string title, string appName)
    {
        var tokens = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;
        var haystack = Fold(title + " " + appName);
        return tokens.All(haystack.Contains);
    }

    /// <summary>With an empty query the first letter is a command: W closes the window, Q quits its app, S keeps the search open.</summary>
    public static SwitcherLetter LetterAction(int virtualKey, bool pinEnabled) => virtualKey switch
    {
        0x57 => SwitcherLetter.CloseWindow,
        0x51 => SwitcherLetter.QuitApp,
        0x53 when pinEnabled => SwitcherLetter.PinSearch,
        _ => SwitcherLetter.None,
    };

    // MARK: Closing

    public readonly record struct CloseResult(IReadOnlyList<int> Ids, int Selected, bool SessionEnds);

    /// <summary>After a window is closed: it leaves the list, the selection keeps its place (or the one before when the one it was on was
    /// before it), and the session ends with the last entry.</summary>
    public static CloseResult CloseState(IReadOnlyList<int> ids, int selected, int removed)
    {
        var at = ids.ToList().IndexOf(removed);
        if (at < 0) return new CloseResult(ids, selected, false);
        var remaining = ids.Where(id => id != removed).ToList();
        if (remaining.Count == 0) return new CloseResult(remaining, 0, true);
        var next = at < selected ? selected - 1 : selected;
        return new CloseResult(remaining, Math.Clamp(next, 0, remaining.Count - 1), false);
    }

    /// <summary>The entry to switch to when Alt is released while some are still closing: the selection, else the next that is not closing, else an earlier one.</summary>
    public static int? CommitTarget(IReadOnlyList<int> ids, int selected, IReadOnlySet<int> closing)
    {
        if (ids.Count == 0) return null;
        selected = Math.Clamp(selected, 0, ids.Count - 1);
        for (var i = selected; i < ids.Count; i++) if (!closing.Contains(ids[i])) return ids[i];
        for (var i = selected - 1; i >= 0; i--) if (!closing.Contains(ids[i])) return ids[i];
        return null;
    }

    // MARK: The wheel

    /// <summary>A mouse wheel moves one entry per notch; a touchpad adds up small deltas, 30 to a step, and forgets them after a pause.</summary>
    public sealed class WheelNavigator
    {
        private double _accumulated, _lastTime = double.NegativeInfinity;

        /// <summary>Whole steps for a wheel delta (positive is a step back), given a notch of 120 or smaller touchpad deltas.</summary>
        public int Add(int delta, double now)
        {
            if (now - _lastTime > ScrollIdleSeconds) _accumulated = 0;
            _lastTime = now;
            if (delta % 120 == 0) return delta / 120;
            _accumulated += delta;
            var steps = (int)(_accumulated / PrecisionStep);
            _accumulated -= steps * PrecisionStep;
            return steps;
        }
    }
}
