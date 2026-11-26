// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarSupport.swift (CommandBarUse, CommandBarUsage,
// CommandBarQueryHabits, CommandBarQueryHabitStoreCache, CommandBarLearning) and CommandBarQueryMemory.swift.
//
// Windows differences: the installation key is not read from or written to any store here. The caller supplies the
// 32 key bytes (and decides where they live); InstallationKey only validates, generates and reports when old
// digests must be dropped. Times are plain seconds (any consistent clock) as in upstream.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchly.Core.CommandBar;

/// <summary>
/// How often and how recently one command ran. Query habits reuse it behind keyed digests; the typed query itself
/// is never written anywhere. JSON shape matches upstream: {"count":2,"lastUsed":1800000010}.
/// </summary>
public sealed record CommandBarUse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("lastUsed")] double LastUsed);

/// <summary>Lenient JSON helpers: one damaged entry is skipped, a damaged document reads as empty.</summary>
internal static class LearningJson
{
    /// <summary>Plain, readable output: "alt+win:66" is not escaped to "alt+win:66", emoji stay emoji.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Dictionary<string, CommandBarUse> DecodeUses(JsonElement element)
    {
        var map = new Dictionary<string, CommandBarUse>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return map;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (!property.Value.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt32(out var countValue)) continue;
            if (!property.Value.TryGetProperty("lastUsed", out var last) || last.ValueKind != JsonValueKind.Number
                || !last.TryGetDouble(out var lastValue)) continue;
            map[property.Name] = new CommandBarUse(countValue, lastValue);
        }
        return map;
    }

    public static JsonDocument? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Usage bookkeeping behind "the order privileges what the person uses most".</summary>
public static class CommandBarUsage
{
    public const int StoredIdLimit = 200;
    public const int CountCap = 999;

    public static Dictionary<string, CommandBarUse> Decode(string? raw)
    {
        using var document = LearningJson.Parse(raw);
        return document is null ? new(StringComparer.Ordinal) : LearningJson.DecodeUses(document.RootElement);
    }

    public static string Encode(IReadOnlyDictionary<string, CommandBarUse> usage) => JsonSerializer.Serialize(usage, LearningJson.Options);

    /// <summary>The map after one more run of <paramref name="id"/>. Oldest ids fall off past the cap so the store never grows with the years.</summary>
    public static Dictionary<string, CommandBarUse> Recording(IReadOnlyDictionary<string, CommandBarUse> usage, string id, double now)
    {
        var next = new Dictionary<string, CommandBarUse>(usage, StringComparer.Ordinal);
        var use = next.TryGetValue(id, out var existing) ? existing : new CommandBarUse(0, now);
        next[id] = new CommandBarUse(Math.Min(use.Count + 1, CountCap), now);
        if (next.Count > StoredIdLimit)
        {
            foreach (var entry in next.OrderBy(e => e.Value.LastUsed).Take(next.Count - StoredIdLimit).ToList())
                next.Remove(entry.Key);
        }
        return next;
    }

    /// <summary>
    /// The ranking boost habit earns: min(count, 40) times a recency weight of 12 / 8 / 5 / 2 for under 2 hours,
    /// 48 hours, 14 days, older. Capped well below a literal text hit so what was typed beats what is usually run.
    /// </summary>
    public static int Boost(CommandBarUse? use, double now)
    {
        if (use is null || use.Count <= 0) return 0;
        var age = Math.Max(0, now - use.LastUsed);
        var weight = age < 2 * 3600 ? 12 : age < 48 * 3600 ? 8 : age < 14 * 86400 ? 5 : 2;
        return Math.Min(use.Count, 40) * weight;
    }

    /// <summary>What an empty bar offers: the most used commands first, then a few curated ones worth discovering, never more than <paramref name="limit"/>.</summary>
    public static List<string> SuggestionIds(IReadOnlyDictionary<string, CommandBarUse> usage, IEnumerable<string> available,
                                             IEnumerable<string> curated, int limit)
    {
        var availableSet = available.ToHashSet(StringComparer.Ordinal);
        var picked = new List<string>();
        var used = usage.Where(e => availableSet.Contains(e.Key) && e.Value.Count > 0)
            .OrderByDescending(e => e.Value.Count).ThenByDescending(e => e.Value.LastUsed)
            .Select(e => e.Key);
        picked.AddRange(used.Take(limit));
        foreach (var id in curated)
        {
            if (picked.Count >= limit) break;
            if (availableSet.Contains(id) && !picked.Contains(id)) picked.Add(id);
        }
        return picked;
    }

    /// <summary>
    /// Empty category browsers keep their catalog order until the person chooses something. Used rows then lead by
    /// frequency and recency; unseen rows keep their original relative order.
    /// </summary>
    public static List<string> CategoryIds(IReadOnlyDictionary<string, CommandBarUse> usage, IEnumerable<string> available)
    {
        CommandBarUse? UseOf(string id) => usage.TryGetValue(id, out var use) && use.Count > 0 ? use : null;
        return available.Select((id, offset) => (id, offset, use: UseOf(id)))
            .OrderBy(e => e.use is null ? 1 : 0)
            .ThenByDescending(e => e.use?.Count ?? 0)
            .ThenByDescending(e => e.use?.LastUsed ?? 0)
            .ThenBy(e => e.offset)
            .Select(e => e.id).ToList();
    }
}

/// <summary>
/// Which result won after a typed query. Only bounded, keyed query digests and row identities are saved locally;
/// the typed query is never stored verbatim.
/// </summary>
public static class CommandBarQueryHabits
{
    public const int StoredQueryLimit = 320;
    public const int ResultLimit = 4;
    public const int KeyLength = 32;
    public const int MaxQueryCharacters = 24;
    public const int MaximumBoost = 720;

    /// <summary>One query's saved choices: digest of the typed prefix to the rows chosen for it.</summary>
    public static Dictionary<string, Dictionary<string, CommandBarUse>> NewStore() => new(StringComparer.Ordinal);

    /// <summary>A keyed digest of one typed prefix and how many characters it covers.</summary>
    public readonly record struct QueryKey(string Digest, int Specificity);

    public sealed class PreparedQuery
    {
        public static readonly PreparedQuery Empty = new([]);
        public IReadOnlyList<QueryKey> Keys { get; }
        public PreparedQuery(IReadOnlyList<QueryKey> keys) => Keys = keys;
        public int KeyCount => Keys.Count;
        public bool IsEmpty => Keys.Count == 0;
    }

    /// <summary>The active field's keyed prefixes. Typing one more character hashes that character's prefix, not every prefix again.</summary>
    public sealed class PreparationCache
    {
        internal string NormalizedQuery = "";
        internal byte[]? Key;
        internal PreparedQuery Prepared = PreparedQuery.Empty;

        public void Reset()
        {
            NormalizedQuery = "";
            Key = null;
            Prepared = PreparedQuery.Empty;
        }
    }

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Decode(string? raw)
    {
        var store = NewStore();
        using var document = LearningJson.Parse(raw);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return store;
        foreach (var query in document.RootElement.EnumerateObject())
        {
            var choices = LearningJson.DecodeUses(query.Value);
            if (choices.Count > 0) store[query.Name] = choices;
        }
        return store;
    }

    public static string Encode(IReadOnlyDictionary<string, Dictionary<string, CommandBarUse>> store) => JsonSerializer.Serialize(store, LearningJson.Options);

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Recording(
        IReadOnlyDictionary<string, Dictionary<string, CommandBarUse>> store, PreparedQuery preparedQuery, string resultId, double now)
    {
        var next = NewStore();
        foreach (var entry in store) next[entry.Key] = new Dictionary<string, CommandBarUse>(entry.Value, StringComparer.Ordinal);
        foreach (var queryKey in preparedQuery.Keys)
        {
            if (!next.TryGetValue(queryKey.Digest, out var choices)) next[queryKey.Digest] = choices = new(StringComparer.Ordinal);
            var use = choices.TryGetValue(resultId, out var existing) ? existing : new CommandBarUse(0, now);
            choices[resultId] = new CommandBarUse(Math.Min(use.Count + 1, CommandBarUsage.CountCap), now);
            if (choices.Count > ResultLimit)
            {
                foreach (var choice in choices.OrderBy(c => c.Value.LastUsed).Take(choices.Count - ResultLimit).ToList())
                    choices.Remove(choice.Key);
            }
        }
        if (next.Count > StoredQueryLimit)
        {
            foreach (var entry in next.OrderBy(e => LatestUse(e.Value)).Take(next.Count - StoredQueryLimit).ToList())
                next.Remove(entry.Key);
        }
        return next;
    }

    /// <summary>boost = min(use boost x 5 + prefix length x 6, 720), the best over the typed prefixes that learned this result.</summary>
    public static int Boost(string resultId, PreparedQuery preparedQuery, IReadOnlyDictionary<string, Dictionary<string, CommandBarUse>> store, double now)
    {
        var best = 0;
        foreach (var queryKey in preparedQuery.Keys)
        {
            if (!store.TryGetValue(queryKey.Digest, out var choices) || !choices.TryGetValue(resultId, out var use)) continue;
            var learned = CommandBarUsage.Boost(use, now) * 5 + queryKey.Specificity * 6;
            best = Math.Max(best, Math.Min(learned, MaximumBoost));
        }
        return best;
    }

    public static Dictionary<string, Dictionary<string, CommandBarUse>> Removing(
        string resultId, IReadOnlyDictionary<string, Dictionary<string, CommandBarUse>> store)
    {
        var result = NewStore();
        foreach (var entry in store)
        {
            var choices = new Dictionary<string, CommandBarUse>(entry.Value, StringComparer.Ordinal);
            choices.Remove(resultId);
            if (choices.Count > 0) result[entry.Key] = choices;
        }
        return result;
    }

    /// <summary>The keyed prefixes of a query, with no cache. The key must be exactly 32 bytes or nothing is prepared.</summary>
    public static PreparedQuery Prepare(string query, byte[]? key) => Prepare(query, key, new PreparationCache());

    /// <summary>
    /// Digest of the first 24 characters of the normalised query, each prefix hashed separately. The digest function
    /// is injectable so tests can count the hashing work instead of timing it.
    /// </summary>
    public static PreparedQuery Prepare(string query, byte[]? key, PreparationCache cache, Func<string, byte[], string>? digest = null)
    {
        digest ??= Digest;
        var characters = CommandBarSearch.Elements(CommandBarSearch.Normalized(query)).Take(MaxQueryCharacters).ToArray();
        var normalized = string.Concat(characters);
        if (key is null || key.Length != KeyLength)
        {
            cache.NormalizedQuery = normalized;
            cache.Key = null;
            cache.Prepared = PreparedQuery.Empty;
            return cache.Prepared;
        }
        if (cache.Key is null || !cache.Key.AsSpan().SequenceEqual(key))
        {
            cache.NormalizedQuery = normalized;
            cache.Key = (byte[])key.Clone();
            cache.Prepared = new PreparedQuery(PreparedKeys(characters, key, digest));
            return cache.Prepared;
        }
        if (normalized == cache.NormalizedQuery) return cache.Prepared;

        var old = cache.NormalizedQuery;
        if (normalized.StartsWith(old, StringComparison.Ordinal))
        {
            var keys = cache.Prepared.Keys.ToList();
            for (var length = CommandBarSearch.ElementCount(old) + 1; length <= characters.Length; length++)
                keys.Add(new QueryKey(digest(string.Concat(characters.Take(length)), key), length));
            cache.Prepared = new PreparedQuery(keys);
        }
        else if (old.StartsWith(normalized, StringComparison.Ordinal))
        {
            cache.Prepared = new PreparedQuery(cache.Prepared.Keys.Where(k => k.Specificity <= characters.Length).ToList());
        }
        else
        {
            cache.Prepared = new PreparedQuery(PreparedKeys(characters, key, digest));
        }
        cache.NormalizedQuery = normalized;
        return cache.Prepared;
    }

    private static List<QueryKey> PreparedKeys(string[] characters, byte[] key, Func<string, byte[], string> digest)
    {
        var keys = new List<QueryKey>(characters.Length);
        for (var length = 1; length <= characters.Length; length++)
            keys.Add(new QueryKey(digest(string.Concat(characters.Take(length)), key), length));
        return keys;
    }

    /// <summary>The first 12 bytes (24 hex characters) of HMAC-SHA256 of the prefix, keyed by the installation key.</summary>
    public static string Digest(string prefix, byte[] key)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(prefix));
        return Convert.ToHexString(mac.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static double LatestUse(Dictionary<string, CommandBarUse> choices) =>
        choices.Count == 0 ? 0 : choices.Values.Max(c => c.LastUsed);
}

/// <summary>Decoded query choices, kept in memory while ranking and saved on changes.</summary>
public sealed class CommandBarQueryHabitStoreCache
{
    public Dictionary<string, Dictionary<string, CommandBarUse>> Store { get; private set; } = CommandBarQueryHabits.NewStore();

    public void Reload(string? raw, Func<string?, Dictionary<string, Dictionary<string, CommandBarUse>>>? decode = null) =>
        Store = (decode ?? CommandBarQueryHabits.Decode)(raw);

    public void Record(CommandBarQueryHabits.PreparedQuery preparedQuery, string resultId, double now) =>
        Store = CommandBarQueryHabits.Recording(Store, preparedQuery, resultId, now);

    public void Remove(string resultId) => Store = CommandBarQueryHabits.Removing(resultId, Store);

    public void ForgetAll() => Store = CommandBarQueryHabits.NewStore();
}

/// <summary>
/// The random key that makes saved query digests mean the same thing after a relaunch. Persistence (and any
/// protection of the key at rest) belongs to the caller; this only generates and validates.
/// </summary>
public static class CommandBarLearning
{
    public readonly record struct ResolvedKey(byte[] Key, string StorageValue, bool WasReplaced);

    /// <summary>A fresh 256-bit key.</summary>
    public static byte[] NewInstallationKey() => RandomNumberGenerator.GetBytes(CommandBarQueryHabits.KeyLength);

    /// <summary>The stored key (base64) when it decodes to exactly 32 bytes, otherwise a fresh one.</summary>
    /// <returns>The key to use, its base64 text to store, and whether a new one replaced a missing or damaged one:
    /// then every saved query digest can no longer be matched and the caller should drop the saved habits.</returns>
    public static ResolvedKey ResolveInstallationKey(string? storedBase64)
    {
        if (!string.IsNullOrWhiteSpace(storedBase64))
        {
            try
            {
                var key = Convert.FromBase64String(storedBase64);
                if (key.Length == CommandBarQueryHabits.KeyLength) return new ResolvedKey(key, storedBase64, false);
            }
            catch (FormatException) { }
        }
        var fresh = NewInstallationKey();
        return new ResolvedKey(fresh, Convert.ToBase64String(fresh), true);
    }
}

/// <summary>
/// What the bar noticed about this session: which row was chosen after which few letters, so typing those letters
/// again lands on it. In memory only; the stronger query-habit ranking saves keyed digests separately.
/// </summary>
public sealed class CommandBarQueryMemory
{
    /// <summary>Enough prefixes for a session's worth of typing.</summary>
    public const int QueryLimit = 60;
    /// <summary>Two rows can honestly compete for the same letters; more than a handful is a list of everything.</summary>
    public const int IdsPerQuery = 4;
    /// <summary>Past this, what was typed is a whole word and the ranking already knows what to do with it.</summary>
    public const int LongestPrefix = 12;
    /// <summary>What one remembered choice is worth, at most: a tie-breaker, never a better match.</summary>
    public const int MaximumBoost = 3;
    public const int CountCap = 99;

    private sealed class Pick
    {
        public int Count;
        /// <summary>When it was last chosen, counted in choices rather than seconds.</summary>
        public int Step;
    }

    private readonly Dictionary<string, Dictionary<string, Pick>> _picks = new(StringComparer.Ordinal);

    public bool IsEmpty => _picks.Count == 0;

    /// <summary>Every leading piece of what was typed, folded the way the ranking folds.</summary>
    public static List<string> Prefixes(string query)
    {
        var normalized = CommandBarSearch.Normalized(query);
        var result = new List<string>();
        if (normalized.Length == 0) return result;
        var characters = CommandBarSearch.Elements(normalized);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var length = 1; length <= Math.Min(characters.Length, LongestPrefix); length++)
        {
            var prefix = CommandBarSearch.TrimWhitespaces(string.Concat(characters.Take(length)));
            if (prefix.Length == 0 || !seen.Add(prefix)) continue;
            result.Add(prefix);
        }
        return result;
    }

    /// <summary>Notes that this row was the answer to what was typed. <paramref name="step"/> counts choices and only has to grow.</summary>
    public void Record(string query, string id, int step)
    {
        foreach (var prefix in Prefixes(query))
        {
            if (!_picks.TryGetValue(prefix, out var forPrefix)) _picks[prefix] = forPrefix = new(StringComparer.Ordinal);
            if (!forPrefix.TryGetValue(id, out var pick)) forPrefix[id] = pick = new Pick { Count = 0, Step = step };
            pick.Count = Math.Min(pick.Count + 1, CountCap);
            pick.Step = step;
            // The least chosen goes first, so a row picked once by mistake cannot hold a place forever.
            if (forPrefix.Count > IdsPerQuery)
            {
                foreach (var entry in forPrefix.OrderBy(e => e.Value.Count).ThenBy(e => e.Value.Step).Take(forPrefix.Count - IdsPerQuery).ToList())
                    forPrefix.Remove(entry.Key);
            }
        }
        if (_picks.Count <= QueryLimit) return;
        // And the prefixes themselves: the ones not used for longest go.
        foreach (var entry in _picks.OrderBy(e => e.Value.Values.Select(p => p.Step).DefaultIfEmpty(0).Max()).Take(_picks.Count - QueryLimit).ToList())
            _picks.Remove(entry.Key);
    }

    /// <summary>What this row is worth for exactly these letters. Nothing for a row never chosen after them.</summary>
    public int Boost(string query, string id) => BoostNormalized(CommandBarSearch.Normalized(query), id);

    /// <summary>The same answer for letters the caller has already folded, so a pass over the pool folds the query once.</summary>
    public int BoostNormalized(string normalizedQuery, string id)
    {
        if (normalizedQuery.Length == 0 || !_picks.TryGetValue(normalizedQuery, out var forPrefix) || !forPrefix.TryGetValue(id, out var pick)) return 0;
        return Math.Min(pick.Count, 3) * (MaximumBoost / 3);
    }

    /// <summary>Forgets one row everywhere, for the person who asks the bar to stop putting it first.</summary>
    public void Forget(string id)
    {
        foreach (var prefix in _picks.Keys.ToList())
        {
            var forPrefix = _picks[prefix];
            if (!forPrefix.Remove(id)) continue;
            if (forPrefix.Count == 0) _picks.Remove(prefix);
        }
    }

    public void Clear() => _picks.Clear();
}
