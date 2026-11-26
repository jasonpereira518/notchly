// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarFileSearchSupport.swift and the pure parts of
// CommandBarFileSearch.swift (Copyright (C) 2026 Vorssaint).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly.Core.CommandBar;

/// <summary>
/// The rules behind finding a file from the bar: what is asked of Windows Search, which folders
/// are searched and what never comes back. Pure, so every rule here is pinned by tests instead of
/// being discovered against one PC's disk.
///
/// Two of them are structural rather than preferences, and no setting can undo them: a hidden path
/// (a name starting with a dot, or a hidden item as the caller's predicate reports it) is never
/// offered, and neither is anything inside an ignored folder.
/// </summary>
public static class CommandBarFileSearchSupport
{
    /// <summary>Names that are almost never what somebody meant, and that a search through a home
    /// folder otherwise fills up with. Shipped rather than stored, so a later version can change
    /// the list and reach the PCs that already ran this one.</summary>
    public static IReadOnlyList<string> ShippedIgnores { get; } =
        ["node_modules", ".git", "DerivedData", "Pods", ".build", "vendor"];

    /// <summary>Below this a filename search says almost nothing and the index is asked for the
    /// whole disk, so the bar simply does not ask.</summary>
    public const int ShortestQuery = 2;

    /// <summary>How many names the index is allowed to hand back before anything is filtered
    /// (the <c>TOP</c> of the statement).</summary>
    public const int CandidateLimit = 1000;

    /// <summary>How many rows survive to be ranked.</summary>
    public const int ResultLimit = 200;

    /// <summary>How many file rows one search puts in the list.</summary>
    public const int RowsPerSearch = 4;

    /// <summary>The shipped ignores followed by the person's own (one name or pattern per line in
    /// the <c>commandBarFileIgnores</c> setting), without repeats.</summary>
    public static List<string> AllIgnores(string? userList) =>
        DecodeList(string.Join('\n', ShippedIgnores.Concat(DecodeList(userList))));

    // MARK: - What is asked of Windows Search

    /// <summary>
    /// The name conditions of the query, or null when there is nothing worth asking. Every word
    /// has to appear in the file's name, in any order, which is what makes "annual report" find
    /// "Report annual.pdf": one <c>System.FileName LIKE '%word%'</c> per whitespace-separated
    /// word, joined with <c>AND</c>.
    ///
    /// Escaping (see <see cref="EscapedLike"/>): Windows Search SQL has no dependable
    /// <c>ESCAPE</c> clause, so the T-SQL convention is used instead. A single quote is doubled,
    /// and <c>%</c>, <c>_</c> and <c>[</c> are wrapped in a one-character set (<c>[%]</c>,
    /// <c>[_]</c>, <c>[[]</c>) so they match themselves. A caller should still run
    /// <see cref="MatchesAllWords"/> over what comes back, which makes any provider quirk
    /// harmless.
    /// </summary>
    public static string? Expression(string query)
    {
        var words = Words(query);
        if (words.Count == 0 || TextLength(query.Trim()) < ShortestQuery) return null;
        return string.Join(" AND ", words.Select(word => $"System.FileName LIKE '%{EscapedLike(word)}%'"));
    }

    /// <summary>The whole Windows Search SQL statement for a query over some scopes, or null when
    /// the query is too short or there is no scope (an empty scope list is the feature being off).
    /// Scopes are written as <c>file:</c> URLs with forward slashes, joined with <c>OR</c>.</summary>
    public static string? Sql(string query, IReadOnlyList<string> scopes)
    {
        var expression = Expression(query);
        if (expression is null || scopes.Count == 0) return null;
        var scope = string.Join(" OR ", scopes.Select(path => $"SCOPE='file:{EscapedLiteral(path.Replace('\\', '/'))}'"));
        return $"SELECT TOP {CandidateLimit} System.ItemPathDisplay FROM SystemIndex WHERE ({scope}) AND {expression}";
    }

    /// <summary>What a word has to look like inside a quoted LIKE pattern. Always safe to embed
    /// between single quotes.</summary>
    public static string EscapedLike(string word)
    {
        var builder = new StringBuilder(word.Length + 4);
        foreach (var character in word)
        {
            switch (character)
            {
                case '\'': builder.Append("''"); break;
                case '%': builder.Append("[%]"); break;
                case '_': builder.Append("[_]"); break;
                case '[': builder.Append("[[]"); break;
                default: builder.Append(character); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>A value inside a quoted (non-pattern) literal: only the quote needs doubling.</summary>
    public static string EscapedLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Whether the file's name (the last path component) holds every word of the query,
    /// ignoring case. The same rule the SQL expresses, applied again to what comes back.</summary>
    public static bool MatchesAllWords(string path, string query)
    {
        var name = LastComponent(path);
        var words = Words(query);
        return words.Count > 0 && words.All(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name a person recognises first, then the full path, so two PCs with the same
    /// files answer in the same order.</summary>
    public static List<string> Sorted(IEnumerable<string> paths) =>
        paths.OrderBy(LastComponent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static List<string> Words(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static int TextLength(string text) => new StringInfo(text).LengthInTextElements;

    private static string LastComponent(string path) =>
        path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? "";

    // MARK: - Where it looks

    /// <summary>Expands <c>%NAME%</c> environment variables (<c>%USERPROFILE%</c>, ...). An unknown
    /// variable stays as written. There is no <c>~</c> expansion on Windows. The lookup defaults to
    /// the process environment; tests pass their own.</summary>
    public static string ExpandEnvironment(string text, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        return EnvironmentVariable.Replace(text, match =>
        {
            var value = lookup(match.Groups[1].Value);
            return string.IsNullOrEmpty(value) ? match.Value : value;
        });
    }

    private static readonly Regex EnvironmentVariable = new(@"%([^%\r\n\\/]+)%", RegexOptions.Compiled);

    /// <summary>Home folders that a filename search turns into noise: the application data
    /// folders and the legacy junction points inside a Windows user profile (the counterpart of
    /// upstream skipping <c>~/Library</c>).</summary>
    private static readonly HashSet<string> HomeExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Application Data", "Local Settings", "Cookies", "NetHood", "PrintHood",
        "Recent", "SendTo", "Start Menu", "Templates",
    };

    /// <summary>
    /// The folders a saved list actually means.
    ///
    /// Entries may use environment variables (<c>%USERPROFILE%\Notes</c>). The home folder is
    /// expanded into the folders inside it: handing the index the profile itself would drag in
    /// <c>AppData</c>, which is tens of thousands of files nobody was looking for. Comparison is
    /// case-insensitive, as paths are on Windows.
    ///
    /// An empty list searches nothing. That is the feature being off, and it is deliberate:
    /// falling back to the home folder would turn a list the person cleared into the broadest
    /// search the bar can make.
    /// </summary>
    public static List<string> ResolvedScopes(IEnumerable<string> saved, string homeDirectory,
        IReadOnlyList<string> homeChildren, Func<string, bool> isSearchableDirectory,
        Func<string, string?>? environment = null)
    {
        var home = Standardized(homeDirectory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopes = new List<string>();
        foreach (var raw in saved)
        {
            var path = Standardized(ExpandEnvironment(raw.Trim(), environment));
            if (path.Length == 0) continue;
            if (home.Length > 0 && string.Equals(path, home, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in homeChildren)
                {
                    if (child.StartsWith('.') || HomeExcluded.Contains(child)) continue;
                    var expanded = Join(home, child);
                    if (isSearchableDirectory(expanded) && seen.Add(expanded)) scopes.Add(expanded);
                }
                continue;
            }
            if (isSearchableDirectory(path) && seen.Add(path)) scopes.Add(path);
        }
        return scopes;
    }

    private static string Join(string folder, string name) =>
        folder.EndsWith('\\') ? folder + name : folder + "\\" + name;

    /// <summary>A path with backslash separators, no repeated separators, no <c>.</c> or
    /// <c>..</c> segments and no trailing separator (a drive root keeps its <c>\</c>). Purely
    /// textual: nothing touches the disk.</summary>
    public static string Standardized(string path)
    {
        var text = path.Trim().Replace('/', '\\');
        if (text.Length == 0) return "";
        var unc = text.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                // The drive (or the UNC server) is never popped.
                var floor = unc ? 2 : (parts.Count > 0 && parts[0].EndsWith(':') ? 1 : 0);
                if (parts.Count > floor) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        var joined = string.Join('\\', parts);
        if (unc) return @"\\" + joined;
        if (parts.Count == 1 && parts[0].EndsWith(':')) return joined + "\\";
        if (text.StartsWith('\\')) return "\\" + joined;
        return joined;
    }

    // MARK: - What never comes back

    /// <summary>
    /// Whether a path can be offered at all, before any preference is read: no component (the
    /// drive aside) starts with a dot, and neither the path nor any folder above it is hidden as
    /// <paramref name="isHidden"/> reports it. The predicate is the only thing that touches the
    /// disk (file attributes), so the rule stays testable.
    /// </summary>
    public static bool IsOfferable(string path, Func<string, bool> isHidden)
    {
        var standardized = Standardized(path);
        var components = standardized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components[^1].StartsWith('.')) return false;
        var ancestor = standardized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : "";
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            ancestor = index == 0 && component.EndsWith(':') ? component
                : ancestor.Length == 0 ? component
                : Join(ancestor, component);
            var isDrive = index == 0 && component.EndsWith(':');
            if (isDrive) continue;
            if (component.StartsWith('.')) return false;
            if (isHidden(ancestor)) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether one of the names the person never wants to see stands in this path. A pattern
    /// matches a whole folder or file name, never half of one ("build" must not take out
    /// "rebuild-notes.md"). A pattern written as an extension (<c>*.log</c> or <c>.log</c>) takes
    /// out the kind of file, not a folder called "log". Case-insensitive.
    /// </summary>
    public static bool IsIgnored(string path, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        var components = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(component => component.ToLowerInvariant()).ToArray();
        foreach (var pattern in patterns)
        {
            var wanted = pattern.Trim().ToLowerInvariant();
            if (wanted.Length == 0) continue;
            if (components.Contains(wanted)) return true;
            var name = components.Length > 0 ? components[^1] : null;
            if (name is null) continue;
            if (wanted.StartsWith("*.", StringComparison.Ordinal)
                && name.EndsWith(wanted[1..], StringComparison.Ordinal))
                return true;
            if (wanted.StartsWith('.') && !wanted.Contains('/') && !wanted.Contains('\\')
                && name != wanted && name.EndsWith(wanted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The paths worth showing, in the order they should be ranked in: the ones the index
    /// found, minus what is structurally out and what the person asked never to see, one row per
    /// path (case-insensitively), capped at <see cref="ResultLimit"/>.</summary>
    public static List<string> Offerable(IEnumerable<string> paths, IReadOnlyList<string> patterns,
        Func<string, bool> isHidden)
    {
        var hiddenCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool CachedIsHidden(string path)
        {
            if (hiddenCache.TryGetValue(path, out var cached)) return cached;
            var value = isHidden(path);
            hiddenCache[path] = value;
            return value;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!IsOfferable(path, CachedIsHidden) || IsIgnored(path, patterns)) continue;
            if (!seen.Add(path)) continue;
            result.Add(path);
            if (result.Count >= ResultLimit) break;
        }
        return result;
    }

    // MARK: - What is stored

    /// <summary>One path or pattern per line, which is what makes the lists readable in a
    /// settings export and easy to paste into. Both <c>\n</c> and <c>\r\n</c> are accepted (a
    /// Windows text box produces the second); blank lines and repeats (ignoring case) go.</summary>
    public static List<string> DecodeList(string? raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        if (string.IsNullOrEmpty(raw)) return result;
        foreach (var line in raw.Split('\n'))
        {
            var item = line.Trim();
            if (item.Length > 0 && seen.Add(item)) result.Add(item);
        }
        return result;
    }

    public static string EncodeList(IEnumerable<string> items) =>
        string.Join('\n', DecodeList(string.Join('\n', items)));

    /// <summary>A path written the short way, for the row underneath a file's name and for the
    /// list in Settings: the home folder becomes <c>%USERPROFILE%</c>, which
    /// <see cref="ResolvedScopes"/> reads back (there is no <c>~</c> on Windows).</summary>
    public static string Abbreviating(string path, string homeDirectory)
    {
        var home = Standardized(homeDirectory);
        if (home.Length == 0) return path;
        var comparable = path.Replace('/', '\\');
        if (string.Equals(comparable, home, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%";
        var prefix = home.EndsWith('\\') ? home : home + "\\";
        return comparable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%\\" + comparable[prefix.Length..]
            : path;
    }

    /// <summary>Only the latest query may refresh the visible bar. Older searches may finish and
    /// warm their cache, but their late completion stays invisible.</summary>
    public static bool ShouldPublishResult(string query, string? currentQuery) => query == currentQuery;
}
