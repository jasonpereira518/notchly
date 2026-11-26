// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarSupport.swift: CommandBarCandidate,
// CommandBarSearch (normalize/fold, score, rankedIndexes, featureOrdered, highlightOffsets,
// splitTrailingNumber, argumentValue, colorPreviewIndex, emojiQuery, matchesVerb) and the app-name
// alias filter of Services/SpotlightNamesSupport.swift.
//
// Not ported: pinyin keywords (Apple's CFStringTransform). Folding is accent, case and width only:
// NFKD, combining marks stripped, recomposed, lower-cased. Hangul and the like therefore stay whole.
using System.Globalization;
using System.Text;

namespace Notchly.Core.CommandBar;

/// <summary>
/// One searchable row offered to the ranking pass. <see cref="Boost"/> carries whatever the caller wants to
/// privilege (usage, pinning) so the ranking itself stays a pure function of text. <see cref="Priority"/> is a
/// deliberate preference (an alias, a learned query choice) that leads ordinary textual matches.
/// </summary>
/// <param name="Index">The caller's own handle for the row, returned by the ranking.</param>
/// <param name="NormalizedTitle">Already folded, so a long list is not re-folded on every keystroke.</param>
public sealed record CommandBarCandidate(
    int Index, string NormalizedTitle, string NormalizedKeywords, int Priority = 0, int Boost = 0)
{
    /// <summary>A candidate from raw text: the title and keywords are folded here.</summary>
    public static CommandBarCandidate Create(int index, string title, string keywords = "", int priority = 0, int boost = 0) =>
        new(index, CommandBarSearch.Normalized(title), CommandBarSearch.Normalized(keywords), priority, boost);
}

/// <summary>The split of "brilho 40" into the verb and its value. A number alone is a search, not a command.</summary>
public readonly record struct CommandBarNumberSplit(string Text, int? Number);

/// <summary>
/// Pure text matching and ranking for the command bar. A token matches as a whole word, a word prefix, a substring,
/// an in-order subsequence of a word ("brlho" finds "brilho") and, as a last resort, within one edit of a word.
/// </summary>
public static class CommandBarSearch
{
    // ---- Folding -------------------------------------------------------------------------------------------------

    /// <summary>A leading colon scopes the global search to emoji; the marker is not part of the matched text.</summary>
    public static string? EmojiQuery(string query)
    {
        var trimmed = TrimWhitespaces(query);
        if (!trimmed.StartsWith(':')) return null;
        return TrimWhitespaces(trimmed[1..]);
    }

    /// <summary>
    /// Case, accent and width differences never matter, nor do invisible format characters, and any run of
    /// whitespace (including the full-width space) is one space. Culture-free on purpose: Turkish lower-casing of
    /// a capital I must not stop "insta" from finding a title that starts with it.
    /// </summary>
    public static string Normalized(string value)
    {
        if (value.Length == 0) return "";
        var folded = Fold(StripInvisibles(value));
        var words = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }

    /// <summary>The folding alone, with whitespace left as it is: lower case, no accents, narrow width.</summary>
    public static string Fold(string value)
    {
        if (IsAscii(value)) return value.ToLowerInvariant();
        string decomposed;
        try { decomposed = value.Normalize(NormalizationForm.FormKD); }
        catch (ArgumentException) { return value.ToLowerInvariant(); }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c == 'ß') { sb.Append("ss"); continue; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Drops the characters that take up no space and that nobody can type: direction marks, zero-width joiners,
    /// soft hyphens. One invisible mark in front of an app's name was enough to sink it below everything else.
    /// </summary>
    private static string StripInvisibles(string value)
    {
        var any = false;
        foreach (var c in value)
        {
            if (c < 0x00AD) continue;
            if (char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) { any = true; break; }
        }
        if (!any) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (!(rune.Value >= 0x00AD && Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format))
                sb.Append(rune.ToString());
        return sb.ToString();
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value) if (c >= 0x80) return false;
        return true;
    }

    /// <summary>Swift's <c>.whitespaces</c>: spaces and tabs, but not line breaks.</summary>
    internal static string TrimWhitespaces(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsHorizontalSpace(value[start])) start++;
        while (end > start && IsHorizontalSpace(value[end - 1])) end--;
        return value[start..end];
    }

    private static bool IsHorizontalSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>The user-perceived characters of a string (grapheme clusters).</summary>
    internal static string[] Elements(string value)
    {
        if (IsAscii(value))
        {
            var result = new string[value.Length];
            for (var i = 0; i < value.Length; i++) result[i] = value[i].ToString();
            return result;
        }
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(value);
        while (e.MoveNext()) list.Add((string)e.Current);
        return [.. list];
    }

    internal static int ElementCount(string value) => IsAscii(value) ? value.Length : new StringInfo(value).LengthInTextElements;

    // ---- App names -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The names an app answers to besides its title: other names the system lists plus the name on disk when it
    /// differs. <paramref name="phoneticName"/> is the hook for a romanizer (upstream's pinyin), unset by default.
    /// </summary>
    public static string ApplicationKeywords(string title, string diskName, IEnumerable<string> alternateNames,
                                             Func<string, string>? phoneticName = null)
    {
        var names = alternateNames.ToList();
        if (diskName.Length > 0 && Normalized(diskName) != Normalized(title)) names.Add(diskName);
        var phonetic = phoneticName?.Invoke(title) ?? "";
        if (phonetic.Length > 0) names.Add(phonetic);
        return string.Join(' ', names);
    }

    /// <summary>
    /// Which of the names the system keeps for an app are worth searching by (SpotlightNamesSupport upstream).
    /// Junk is dropped: the bundle's own file name, untranslated placeholders, names repeating the display name,
    /// and aliases that differ from an earlier one only by accent or case.
    /// </summary>
    public static List<string> UsableAlternateNames(IEnumerable<string> raw, string displayName, string fileName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var known in new[] { displayName, fileName, Path.GetFileNameWithoutExtension(fileName) })
            seen.Add(Fold(known));
        var names = new List<string>();
        foreach (var name in raw)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0 || IsPlaceholder(trimmed) || LooksLikeFileName(trimmed)) continue;
            if (!seen.Add(Fold(trimmed))) continue;
            names.Add(trimmed);
        }
        return names;
    }

    private static bool IsPlaceholder(string value)
    {
        var upper = value.ToUpperInvariant();
        return upper.StartsWith("ALTERNATE_NAME", StringComparison.Ordinal) || upper.StartsWith("CFBUNDLE", StringComparison.Ordinal);
    }

    /// <summary>A file name teaches nothing the name above the icon does not already say. Windows adds .exe and .lnk to the Mac's .app.</summary>
    private static bool LooksLikeFileName(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.EndsWith(".app", StringComparison.Ordinal) || lower.EndsWith(".exe", StringComparison.Ordinal)
            || lower.EndsWith(".lnk", StringComparison.Ordinal);
    }

    // ---- Verbs ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the query names the verb of a format like "Quit %@". Used to keep a heavy, rare command out of the
    /// list until it is asked for, in every language. "{0}" is accepted as the name placeholder as well as "%@".
    /// </summary>
    public static bool MatchesVerb(string query, IEnumerable<string> formats)
    {
        foreach (var raw in formats)
        {
            var format = raw.Replace("{0}", "%@", StringComparison.Ordinal);
            if (MatchesVerb(query, format)) return true;
            // Japanese can place the verb directly after the app name: match the fixed ends of the format too,
            // without admitting a name alone.
            var pieces = format.Split("%@");
            if (pieces.Length != 2) continue;
            var prefix = Normalized(pieces[0]);
            var suffix = Normalized(pieces[1]);
            if (prefix.Length == 0 && suffix.Length == 0) continue;
            var text = Normalized(query);
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static bool MatchesVerb(string query, string format)
    {
        var verb = TrimWhitespaces(Normalized(format.Replace("{0}", " ", StringComparison.Ordinal).Replace("%@", " ", StringComparison.Ordinal)));
        if (verb.Length == 0) return false;
        var normalizedQuery = Normalized(query);
        if (normalizedQuery.Length == 0) return false;
        var verbWords = verb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ElementCount(token) < 2) continue;
            foreach (var word in verbWords)
                if (word.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(word, StringComparison.Ordinal)) return true;
            // Japanese, Chinese and Korean write the verb against the name with no space to split on.
            if (verb.Contains(token, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Where a color typed on its own goes in the list: first, unless a row already there spells what was typed
    /// ("#1234" is also an issue number), in which case the swatch sits right under it.
    /// </summary>
    public static int ColorPreviewIndex(IEnumerable<string> rowTitles, string query)
    {
        var typed = Normalized(query);
        if (typed.Length == 0) return 0;
        return rowTitles.Any(t => Normalized(t).Contains(typed, StringComparison.Ordinal)) ? 1 : 0;
    }

    // ---- Scoring -------------------------------------------------------------------------------------------------

    public static bool Matches(string title, string query, string keywords = "") => Score(title, query, keywords) is not null;

    /// <summary>
    /// Null when the query does not match; otherwise a comparable score. Whole-query hits on the title dominate,
    /// then per-token quality (whole word 140 > word prefix 80 > substring 44 > subsequence 24 > one typo 16).
    /// </summary>
    public static int? Score(string title, string query, string keywords = "") =>
        ScoreNormalized(Normalized(title), Normalized(keywords), Normalized(query));

    /// <summary>The scoring itself, over text that is already folded. Everything the bar ranks per keystroke comes through here.</summary>
    public static int? ScoreNormalized(string normalizedTitle, string normalizedKeywords, string normalizedQuery) =>
        ScoreTokens(normalizedTitle, normalizedKeywords, normalizedQuery, QueryToken.TokensOf(normalizedQuery));

    /// <summary>A query token, measured once per keystroke rather than once per row.</summary>
    private sealed class QueryToken
    {
        public readonly string Text;
        public readonly string[] Elements;
        public readonly bool IsAscii;
        /// <summary>Digit tokens are values, never typos.</summary>
        public readonly bool IsFuzzy;
        public int Count => Elements.Length;

        private QueryToken(string text)
        {
            Text = text;
            Elements = CommandBarSearch.Elements(text);
            IsAscii = CommandBarSearch.IsAscii(text);
            IsFuzzy = !text.All(char.IsNumber);
        }

        public static QueryToken[] TokensOf(string normalizedQuery) =>
            [.. normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => new QueryToken(t))];
    }

    private static int? ScoreTokens(string normalizedTitle, string normalizedKeywords, string normalizedQuery, QueryToken[] tokens)
    {
        if (normalizedQuery.Length == 0) return null;
        var haystack = normalizedKeywords.Length == 0 ? normalizedTitle : normalizedTitle + " " + normalizedKeywords;
        var words = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystackIsAscii = IsAscii(haystack);

        var score = 0;
        foreach (var token in tokens)
        {
            if (BestTokenScore(token, words, haystack, haystackIsAscii && token.IsAscii) is not { } tokenScore) return null;
            score += tokenScore;
        }

        if (normalizedTitle == normalizedQuery) score += 1200;
        else if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal)) score += 900;
        else if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal)) score += 700;
        else if (normalizedKeywords.Length > 0 && normalizedKeywords.Contains(normalizedQuery, StringComparison.Ordinal)) score += 350;
        return score;
    }

    private static int? BestTokenScore(QueryToken token, string[] words, string haystack, bool ascii)
    {
        int? best = null;
        foreach (var word in words)
        {
            if (string.Equals(word, token.Text, StringComparison.Ordinal)) return 140;
            if (word.StartsWith(token.Text, StringComparison.Ordinal)) best = Math.Max(best ?? 0, 80);
        }
        if (best is null && haystack.Contains(token.Text, StringComparison.Ordinal)) best = 44;
        // Fuzzy passes only rescue typing mistakes, so they need some length to bite on.
        if (best is not null || !token.IsFuzzy || token.Count < 3) return best;

        if (ascii)
        {
            var needle = token.Text.ToCharArray();
            foreach (var word in words)
                if (word.Length >= token.Count && IsSubsequence(needle, word.ToCharArray())) return 24;
            foreach (var word in words)
                if (word.Length == token.Count && AdjacentTransposition(needle, word.ToCharArray())) return 16;
            if (token.Count < 4) return null;
            foreach (var word in words)
                if (Math.Abs(word.Length - token.Count) <= 1 && OneEditApart(needle, word.ToCharArray())) return 16;
            return null;
        }

        foreach (var word in words)
            if (IsSubsequence(token.Elements, Elements(word))) return 24;
        foreach (var word in words)
            if (AdjacentTransposition(token.Elements, Elements(word))) return 16;
        if (token.Count < 4) return null;
        foreach (var word in words)
            if (OneEditApart(token.Elements, Elements(word))) return 16;
        return null;
    }

    /// <summary>
    /// Indexes of the matching candidates, best first. Deliberate preferences (priority) lead match quality
    /// (tier), which leads the score with its boost; ties keep the caller's order.
    /// </summary>
    public static List<int> RankedIndexes(IReadOnlyList<CommandBarCandidate> candidates, string query)
    {
        var normalizedQuery = Normalized(query);
        var tokens = QueryToken.TokensOf(normalizedQuery);
        var scored = new List<(int Index, int Priority, int Tier, int Score, int Position)>();
        for (var position = 0; position < candidates.Count; position++)
        {
            var candidate = candidates[position];
            if (ScoreTokens(candidate.NormalizedTitle, candidate.NormalizedKeywords, normalizedQuery, tokens) is not { } baseScore) continue;
            var tier = MatchTier(candidate.NormalizedTitle, candidate.NormalizedKeywords, normalizedQuery);
            scored.Add((candidate.Index, candidate.Priority, tier, baseScore + candidate.Boost, position));
        }
        scored.Sort((a, b) =>
        {
            if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
            if (a.Tier != b.Tier) return b.Tier.CompareTo(a.Tier);
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            return a.Position.CompareTo(b.Position);
        });
        return [.. scored.Select(s => s.Index)];
    }

    /// <summary>Broad text quality is compared before passive signals such as usage and source preference.</summary>
    private static int MatchTier(string title, string keywords, string query)
    {
        if (title == query) return 5;
        if (title.StartsWith(query, StringComparison.Ordinal)) return 4;
        if (title.Contains(query, StringComparison.Ordinal)) return 3;
        if (keywords.Length > 0 && keywords.Contains(query, StringComparison.Ordinal)) return 2;
        return 1;
    }

    /// <summary>
    /// A feature's own rows keep one order whatever their titles score: its main command (switch), then its presets,
    /// then its Settings page. The rows trade places among the slots they already hold, so nothing else moves, and a
    /// row chosen on purpose (non-zero priority) keeps its place. <paramref name="id"/> and <paramref name="priority"/>
    /// read a candidate by its index.
    /// </summary>
    public static List<int> FeatureOrdered(IReadOnlyList<int> ranked, Func<int, string> id, Func<int, int> priority)
    {
        // The feature a row belongs to, and its turn among that feature's rows.
        static (string Feature, int Turn)? Role(string id)
        {
            if (id.StartsWith("settings.feature.", StringComparison.Ordinal)) return (id["settings.feature.".Length..], 2);
            if (id.StartsWith("settings.", StringComparison.Ordinal))
            {
                var page = id["settings.".Length..];
                return page.Contains('.') ? null : (page, 2);
            }
            if (id.StartsWith("toggle.", StringComparison.Ordinal))
            {
                var name = id["toggle.".Length..].TrimStart('.');
                var dot = name.IndexOf('.');
                return (dot < 0 ? name : name[..dot], 0);
            }
            if (!id.StartsWith("action.", StringComparison.Ordinal)) return null;
            var action = id["action.".Length..];
            var cut = action.IndexOf('.');
            return cut < 0 ? (action, 0) : (action[..cut], 1);
        }

        var slots = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var position = 0; position < ranked.Count; position++)
        {
            var index = ranked[position];
            if (priority(index) != 0) continue;
            if (Role(id(index)) is not { } role) continue;
            if (!slots.TryGetValue(role.Feature, out var list)) slots[role.Feature] = list = [];
            list.Add(position);
        }
        var result = ranked.ToList();
        foreach (var positions in slots.Values)
        {
            if (positions.Count <= 1) continue;
            var members = positions.Select(p => ranked[p]).ToList();
            // Rows with the same turn keep the order they ranked in.
            var ordered = members.Select((index, offset) => (index, offset))
                .OrderBy(m => Role(id(m.index))?.Turn ?? 0).ThenBy(m => m.offset)
                .Select(m => m.index).ToList();
            for (var i = 0; i < positions.Count; i++) result[positions[i]] = ordered[i];
        }
        return result;
    }

    /// <summary>The positions to keep when ids repeat: the first of each, in order.</summary>
    public static List<int> FirstOccurrences(IReadOnlyList<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<int>();
        for (var i = 0; i < ids.Count; i++) if (seen.Add(ids[i])) kept.Add(i);
        return kept;
    }

    // ---- Highlighting --------------------------------------------------------------------------------------------

    /// <summary>
    /// Character positions of the title the query literally matched, so the row can show why it is there. Only
    /// literal hits are marked: a fuzzy rescue has no honest range to point at.
    /// </summary>
    public static HashSet<int> HighlightOffsets(string title, string query)
    {
        var offsets = new HashSet<int>();
        var folded = FoldedCharacters(title);
        if (folded.Length == 0) return offsets;
        foreach (var raw in Normalized(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Elements(raw);
            if (token.Length == 0 || FirstIndex(token, folded) is not { } start) continue;
            for (var position = start; position < start + token.Length; position++) offsets.Add(position);
        }
        return offsets;
    }

    /// <summary>Folds each character on its own so the folded array stays aligned with the original, one position per character.</summary>
    private static string[] FoldedCharacters(string value) =>
        [.. Elements(value).Select(element =>
        {
            var folded = Fold(element);
            return folded.Length == 0 ? element : Elements(folded)[0];
        })];

    private static int? FirstIndex(string[] token, string[] haystack)
    {
        if (token.Length > haystack.Length) return null;
        // Word starts first: highlighting "set" inside "Reset" when the title also begins with "Settings" would point at the wrong place.
        for (var start = 0; start <= haystack.Length - token.Length; start++)
        {
            var isWordStart = start == 0 || haystack[start - 1] == " ";
            if (isWordStart && SliceEquals(haystack, start, token)) return start;
        }
        for (var start = 0; start <= haystack.Length - token.Length; start++)
            if (SliceEquals(haystack, start, token)) return start;
        return null;
    }

    private static bool SliceEquals(string[] haystack, int start, string[] token)
    {
        for (var i = 0; i < token.Length; i++) if (haystack[start + i] != token[i]) return false;
        return true;
    }

    // ---- Typo tolerance ------------------------------------------------------------------------------------------

    /// <summary>True when every letter of the needle appears in the word, in order.</summary>
    public static bool IsSubsequence(string needle, string word) => IsSubsequence(Elements(needle), Elements(word));

    private static bool IsSubsequence<T>(T[] needle, T[] word) where T : IEquatable<T>
    {
        if (needle.Length > word.Length) return false;
        var next = 0;
        foreach (var item in word)
        {
            if (next < needle.Length && item.Equals(needle[next])) next++;
        }
        return next == needle.Length;
    }

    /// <summary>True only when one neighboring pair was typed in reverse order.</summary>
    public static bool IsAdjacentTransposition(string first, string second) => AdjacentTransposition(Elements(first), Elements(second));

    private static bool AdjacentTransposition<T>(T[] a, T[] b) where T : IEquatable<T>
    {
        if (a.Length != b.Length) return false;
        int? firstMismatch = null;
        var swapped = false;
        for (var index = 0; index < a.Length; index++)
        {
            if (a[index].Equals(b[index])) continue;
            if (swapped) return false;
            if (firstMismatch is not { } previous)
            {
                firstMismatch = index;
                continue;
            }
            if (index != previous + 1 || !a[previous].Equals(b[index]) || !a[index].Equals(b[previous])) return false;
            swapped = true;
        }
        return swapped;
    }

    /// <summary>True when the strings are at most one substitution, insertion, deletion or adjacent swap apart.</summary>
    public static bool WithinOneEdit(string first, string second) => OneEditApart(Elements(first), Elements(second));

    private static bool OneEditApart<T>(T[] a, T[] b) where T : IEquatable<T>
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.AsSpan().SequenceEqual(b)) return true;
        int i = 0, j = 0;
        var edited = false;
        while (i < a.Length && j < b.Length)
        {
            if (a[i].Equals(b[j])) { i++; j++; continue; }
            if (edited) return false;
            edited = true;
            if (a.Length == b.Length)
            {
                if (i + 1 < a.Length && j + 1 < b.Length && a[i].Equals(b[j + 1]) && a[i + 1].Equals(b[j])) { i += 2; j += 2; }
                else { i++; j++; }
            }
            else if (a.Length > b.Length) i++;
            else j++;
        }
        return true;
    }

    // ---- Typed numbers -------------------------------------------------------------------------------------------

    /// <summary>
    /// "brilho 40" into the verb and its value. Only a trailing bare number counts, and only after some text.
    /// A number too large for an int saturates at <see cref="int.MaxValue"/>: callers clamp into a small range anyway.
    /// </summary>
    public static CommandBarNumberSplit SplitTrailingNumber(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var unsplit = new CommandBarNumberSplit(TrimWhitespaces(query), null);
        if (tokens.Length < 2) return unsplit;
        var digits = tokens[^1];
        if (digits.EndsWith('%')) digits = digits[..^1];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return unsplit;
        var value = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;
        return new CommandBarNumberSplit(string.Join(' ', tokens[..^1]), value);
    }

    /// <summary>
    /// The value typed while the bar asks for a number in place. Accepts an optional %, rejects everything else
    /// (at most four digits), clamps into the command's range.
    /// </summary>
    public static int? ArgumentValue(string text, int minimum, int maximum)
    {
        var digits = TrimWhitespaces(text);
        if (digits.EndsWith('%')) digits = digits[..^1];
        if (digits.Length == 0 || digits.Length > 4 || !digits.All(char.IsAsciiDigit)) return null;
        var value = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return Math.Min(Math.Max(value, minimum), maximum);
    }
}
