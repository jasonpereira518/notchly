// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarRowShortcuts.swift and CommandBarAppSort
// (Services/CommandBar/CommandBarSupport.swift).
//
// Shortcuts are Notchly.Core.Shortcuts.Shortcut, stored in its string form ("control+alt:65"). Not ported: the
// macOS take-over decision (takeOverKey/takeOverDecision: symbolic hotkeys have no Windows counterpart), and the
// window-server front-window lookup (the caller passes ownsFrontWindow to HidesAppInFront).
using System.Text.Json;
using Notchly.Core.Shortcuts;

namespace Notchly.Core.CommandBar;

public enum CommandBarAssignmentIssueKind { Invalid, Occupied, Full }

/// <summary>Why a combination cannot be given to a row. <see cref="OccupiedBy"/> names the row that already owns it.</summary>
public readonly record struct CommandBarAssignmentIssue(CommandBarAssignmentIssueKind Kind, string? OccupiedBy = null)
{
    public static readonly CommandBarAssignmentIssue Invalid = new(CommandBarAssignmentIssueKind.Invalid);
    public static readonly CommandBarAssignmentIssue Full = new(CommandBarAssignmentIssueKind.Full);
    public static CommandBarAssignmentIssue Occupied(string owner) => new(CommandBarAssignmentIssueKind.Occupied, owner);
}

/// <summary>
/// The combinations the person tied to single rows of the bar. An alias makes something faster to find; a shortcut
/// makes it instant, with no bar at all. Nothing is registered unless the person asked for it.
/// </summary>
public static class CommandBarRowShortcuts
{
    /// <summary>Bound global hotkey registrations, leaving room for a shortcut for every letter and for other commands.</summary>
    public const int Limit = 64;

    /// <summary>
    /// An app's own combination puts it away only when the app is in front and its window is the one in front of
    /// every other app's. Otherwise the app comes forward. The window list is read only for an app already in
    /// front (<paramref name="ownsFrontWindow"/> is lazy), so the common press never waits on the window system.
    /// </summary>
    public static bool HidesAppInFront(bool isFrontmost, bool isHidden, Func<bool> ownsFrontWindow) =>
        isFrontmost && !isHidden && ownsFrontWindow();

    /// <summary>A cold catalog may arrive after the person changed their shortcut. Only the latest request, with its original binding intact, runs.</summary>
    public sealed class PendingAppLaunch
    {
        private (string Key, Shortcut Shortcut)? _pending;

        public void Schedule(string key, IReadOnlyDictionary<string, Shortcut> shortcuts) =>
            _pending = shortcuts.TryGetValue(key, out var shortcut) ? (key, shortcut) : null;

        public void Cancel() => _pending = null;

        public string? Take(IReadOnlyDictionary<string, Shortcut> shortcuts, bool isAvailable)
        {
            var pending = _pending;
            _pending = null;
            if (!isAvailable || pending is not { } p) return null;
            return shortcuts.TryGetValue(p.Key, out var current) && current == p.Shortcut ? p.Key : null;
        }
    }

    public static CommandBarAssignmentIssue? AssignmentIssue(Shortcut shortcut, string key, IReadOnlyDictionary<string, Shortcut> shortcuts)
    {
        if (!IsUsable(shortcut)) return CommandBarAssignmentIssue.Invalid;
        if (KeyFor(shortcut, shortcuts) is { } owner && owner != key) return CommandBarAssignmentIssue.Occupied(owner);
        return HasRoom(key, shortcuts) ? null : CommandBarAssignmentIssue.Full;
    }

    /// <summary>The row an app is listed under in the bar: its package or bundle id when it has one, otherwise where it lives.</summary>
    public static string AppKey(string? bundleId, string path) => bundleId is not null ? $"app.bundle.{bundleId}" : $"app.{path}";

    public static Dictionary<string, Shortcut> Decode(string? raw)
    {
        var result = new Dictionary<string, Shortcut>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                if (Shortcut.Parse(property.Value.GetString()) is { } shortcut) result[property.Name] = shortcut;
            }
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
        return result;
    }

    public static string Encode(IReadOnlyDictionary<string, Shortcut> shortcuts) =>
        JsonSerializer.Serialize(shortcuts.ToDictionary(s => s.Key, s => s.Value.StorageValue, StringComparer.Ordinal), LearningJson.Options);

    /// <summary>
    /// The map after binding (or clearing, with null) one row. A combination already tied to another row moves,
    /// because the last thing a person said is what they mean: two rows never share one. A full list refuses a new row.
    /// </summary>
    public static Dictionary<string, Shortcut> Setting(Shortcut? shortcut, string key, IReadOnlyDictionary<string, Shortcut> shortcuts)
    {
        var next = new Dictionary<string, Shortcut>(shortcuts, StringComparer.Ordinal);
        if (shortcut is not { } value)
        {
            next.Remove(key);
            return next;
        }
        foreach (var other in next.Where(e => e.Value == value && e.Key != key).Select(e => e.Key).ToList()) next.Remove(other);
        if (!next.ContainsKey(key) && next.Count >= Limit) return next;
        next[key] = value;
        return next;
    }

    /// <summary>Whether one more row can still be bound. Asked before the keys are taken, so a full list can say so.</summary>
    public static bool HasRoom(string key, IReadOnlyDictionary<string, Shortcut> shortcuts) => shortcuts.ContainsKey(key) || shortcuts.Count < Limit;

    /// <summary>The row a combination belongs to, so the press can be routed without walking the whole catalog.</summary>
    public static string? KeyFor(Shortcut shortcut, IReadOnlyDictionary<string, Shortcut> shortcuts) =>
        shortcuts.Where(e => e.Value == shortcut).Select(e => e.Key).FirstOrDefault();

    /// <summary>Candidate stable keys for an application defined by its identifiers and paths (both its launcher and uninstall rows).</summary>
    public static HashSet<string> ApplicationStableKeys(IEnumerable<string> bundleIds, IEnumerable<string> paths)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in bundleIds.Where(i => i.Length > 0))
        {
            keys.Add($"app.bundle.{id}");
            keys.Add($"uninstall.bundle.{id}");
        }
        foreach (var path in paths.Where(p => p.Length > 0))
        {
            keys.Add($"app.{path}");
            keys.Add($"uninstall.{path}");
        }
        return keys;
    }

    /// <summary>The map after removing the specified row keys.</summary>
    public static Dictionary<string, Shortcut> Removing(IReadOnlySet<string> keys, IReadOnlyDictionary<string, Shortcut> shortcuts) =>
        keys.Count == 0
            ? new(shortcuts, StringComparer.Ordinal)
            : shortcuts.Where(e => !keys.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>Whether a combination is worth registering at all. A bare letter would take that letter away from every app.</summary>
    public static bool IsUsable(Shortcut shortcut) => shortcut.Modifiers != ShortcutModifiers.None;
}

/// <summary>Sorting for the app table in settings (name, alias, shortcut, pinned columns).</summary>
public static class CommandBarAppSort
{
    public enum Column { Name, Alias, Shortcut, Pinned }

    public static List<TItem> Sorted<TItem>(IEnumerable<TItem> items, Column column, bool ascending,
                                            Func<TItem, string> title, Func<TItem, string> key,
                                            IReadOnlyDictionary<string, string> aliases,
                                            IReadOnlyDictionary<string, Shortcut> shortcuts,
                                            IReadOnlySet<string> pins)
    {
        string? Text(TItem item) => column switch
        {
            Column.Name => title(item),
            Column.Alias => aliases.TryGetValue(key(item), out var alias) && alias.Length > 0 ? alias : null,
            Column.Shortcut => shortcuts.TryGetValue(key(item), out var shortcut) ? shortcut.DisplayText : null,
            _ => null,
        };
        bool ByTitle(TItem l, TItem r) => LocalizedStandardCompare(title(l), title(r)) < 0;
        bool Less(TItem l, TItem r)
        {
            if (column == Column.Pinned)
            {
                var left = pins.Contains(key(l));
                var right = pins.Contains(key(r));
                if (left != right) return ascending ? left : right;
                return ByTitle(l, r);
            }
            var a = Text(l);
            var b = Text(r);
            if (a is null && b is null) return ByTitle(l, r);
            if (a is null) return false;
            if (b is null) return true;
            var order = LocalizedStandardCompare(a, b);
            if (order == 0) return ByTitle(l, r);
            return ascending ? order < 0 : order > 0;
        }
        return items.Select((item, index) => (item, index))
            .OrderBy(e => e, Comparer<(TItem item, int index)>.Create((x, y) =>
                Less(x.item, y.item) ? -1 : Less(y.item, x.item) ? 1 : x.index.CompareTo(y.index)))
            .Select(e => e.item).ToList();
    }

    /// <summary>
    /// The Finder-style comparison of file names: case, accent and width blind, and runs of digits compare as
    /// numbers ("App 2" before "App 10"). Culture-invariant.
    /// </summary>
    public static int LocalizedStandardCompare(string left, string right)
    {
        var a = Segments(left);
        var b = Segments(right);
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            int order;
            if (a[i].IsDigits && b[i].IsDigits)
            {
                var x = a[i].Text.TrimStart('0');
                var y = b[i].Text.TrimStart('0');
                order = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else
            {
                order = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.Compare(
                    a[i].Text, b[i].Text,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace
                    | System.Globalization.CompareOptions.IgnoreWidth | System.Globalization.CompareOptions.IgnoreKanaType);
            }
            if (order != 0) return Math.Sign(order);
        }
        return a.Count.CompareTo(b.Count);
    }

    private static List<(string Text, bool IsDigits)> Segments(string value)
    {
        var result = new List<(string, bool)>();
        var start = 0;
        for (var i = 1; i <= value.Length; i++)
        {
            if (i < value.Length && char.IsAsciiDigit(value[i]) == char.IsAsciiDigit(value[start])) continue;
            result.Add((value[start..i], char.IsAsciiDigit(value[start])));
            start = i;
        }
        return value.Length == 0 ? [] : result;
    }
}
