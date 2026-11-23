// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentPricing.swift (the price list and its rules,
// model matching, costs, readable names, plans) and the pure parts of AgentPriceSource.swift. The list
// shipped with the app is embedded in this assembly; downloading and caching a newer one is the app's job.
using System.Globalization;
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>US dollars per million tokens, from each provider's published API list.</summary>
/// <param name="CacheWrite">Short-lived cache writes; providers that do not bill writes separately charge the input price.</param>
/// <param name="CacheWriteLong">One-hour cache writes.</param>
/// <param name="FastMultiplier">Premium for the fast tier, on every token category, where it is sold.</param>
/// <param name="LongContext">A premium on the whole request once the prompt passes a threshold.</param>
public sealed record AgentPrice(double Input, double Output, double CacheRead, double CacheWrite, double CacheWriteLong,
                                double FastMultiplier = 1, AgentLongContext? LongContext = null);

/// <summary>Past <paramref name="Above"/> prompt tokens, input and cache rates are multiplied by
/// <paramref name="Input"/> and output by <paramref name="Output"/>, on top of any fast tier.</summary>
public sealed record AgentLongContext(int Above, double Input, double Output);

/// <summary>What one response bills, beyond its model.</summary>
/// <param name="IsAggregate">Session totals do not reveal individual prompt sizes: they are priced at base
/// rates instead of inferring a long-context request from their sum.</param>
/// <param name="LongCacheWrite">The part of the cache writes kept for an hour.</param>
/// <param name="Domestic">Inference pinned to the United States.</param>
public readonly record struct AgentBillable(AgentTokens Tokens = default, bool IsAggregate = false, long LongCacheWrite = 0,
                                            bool Fast = false, bool Domestic = false, long WebSearches = 0);

/// <summary>
/// Every price the island knows, kept out of the code: a copy ships with the app, and a newer one published
/// with the project replaces it. Fields a later list adds are ignored; a list that breaks these rules is
/// refused as a whole, and the last good one stays.
/// </summary>
public sealed record AgentPriceList(DateTimeOffset Updated, IReadOnlyList<AgentPriceList.Model> Claude,
                                    IReadOnlyList<AgentPriceList.Model> Codex, IReadOnlyList<AgentPriceList.Plan> ClaudePlans,
                                    IReadOnlyList<AgentPriceList.Plan> CodexPlans, double WebSearch, double UsOnlyMultiplier)
{
    public sealed record Model(string Id, AgentPrice Price);

    /// <param name="Match">Found inside the plan the agents record, like "max_20x".</param>
    public sealed record Plan(string Match, string Name, double? Monthly);

    /// <summary>Only a list written for this reader is used.</summary>
    public const int Schema = 1;
    public const int MaximumSize = 256 << 10;

    public static readonly AgentPriceList Empty = new(AgentTime.DistantPast, [], [], [], [], 0, 1);

    public bool Equals(AgentPriceList? other) =>
        other is not null && Updated == other.Updated && Claude.SequenceEqual(other.Claude) && Codex.SequenceEqual(other.Codex)
        && ClaudePlans.SequenceEqual(other.ClaudePlans) && CodexPlans.SequenceEqual(other.CodexPlans)
        && WebSearch.Equals(other.WebSearch) && UsOnlyMultiplier.Equals(other.UsOnlyMultiplier);

    public override int GetHashCode() => HashCode.Combine(Updated, Claude.Count, Codex.Count);

    /// <summary>The list in <paramref name="data"/>, or null when any field breaks a rule.</summary>
    public static AgentPriceList? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumSize) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(data.ToArray()); }
        catch (JsonException) { return null; }
        using (document)
        {
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return null;
            if (Number(json.Field("schema"), 1, 1_000) != Schema) return null;
            if (json.Str("updated") is not { } updatedText || Day(updatedText) is not { } updated) return null;
            if (json.Obj("claude") is not { } claude || json.Obj("codex") is not { } codex) return null;
            if (Models(claude.Field("models"), "claude-") is not { } claudeModels
                || Models(codex.Field("models"), null) is not { } codexModels
                || Plans(claude.Field("plans")) is not { } claudePlans || Plans(codex.Field("plans")) is not { } codexPlans
                || Number(claude.Field("webSearch"), 0, 1) is not { } webSearch
                || Number(claude.Field("usOnlyMultiplier"), 1, 3) is not { } usOnly) return null;
            return new AgentPriceList(updated, claudeModels, codexModels, claudePlans, codexPlans, webSearch, usOnly);
        }
    }

    /// <summary>The newer of the list inside the app and the last one downloaded. On the same day the download
    /// wins, since it may carry a correction.</summary>
    public static AgentPriceList? Newer(AgentPriceList? shipped, AgentPriceList? downloaded)
    {
        if (downloaded is null) return shipped;
        if (shipped is null) return downloaded;
        return downloaded.Updated >= shipped.Updated ? downloaded : shipped;
    }

    private static List<Model>? Models(JsonElement? value, string? prefix)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array) return null;
        var entries = array.EnumerateArray().ToList();
        if (entries.Count is < 1 or > 500 || entries.Any(e => e.ValueKind != JsonValueKind.Object)) return null;
        var result = new List<Model>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Str("id") is not { } id || !Identifier(id, 64) || !seen.Add(id)) return null;
            if (prefix is not null ? !id.StartsWith(prefix, StringComparison.Ordinal) : id.StartsWith("claude-", StringComparison.Ordinal)) return null;
            if (Number(entry.Field("input"), 0, 1_000) is not { } input || Number(entry.Field("output"), 0, 1_000) is not { } output
                || Number(entry.Field("cacheRead"), 0, 1_000) is not { } cacheRead) return null;
            // Fields left out fall back to plain input pricing, the way providers without separate cache
            // writes bill them.
            var cacheWrite = entry.Has("cacheWrite") ? Number(entry.Field("cacheWrite"), 0, 1_000) : input;
            var cacheWriteLong = entry.Has("cacheWriteLong") ? Number(entry.Field("cacheWriteLong"), 0, 1_000) : cacheWrite;
            var fast = entry.Has("fastMultiplier") ? Number(entry.Field("fastMultiplier"), 1, 10) : 1;
            if (cacheWrite is null || cacheWriteLong is null || fast is null) return null;
            AgentLongContext? longContext = null;
            if (entry.Has("longContext"))
            {
                if (entry.Obj("longContext") is not { } context
                    || Number(context.Field("above"), 1_000, 10_000_000) is not { } above
                    || Number(context.Field("inputMultiplier"), 1, 10) is not { } longInput
                    || Number(context.Field("outputMultiplier"), 1, 10) is not { } longOutput) return null;
                longContext = new AgentLongContext((int)above, longInput, longOutput);
            }
            result.Add(new Model(id, new AgentPrice(input, output, cacheRead, cacheWrite.Value, cacheWriteLong.Value, fast.Value, longContext)));
        }
        return result;
    }

    private static List<Plan>? Plans(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array) return null;
        var entries = array.EnumerateArray().ToList();
        if (entries.Count > 50 || entries.Any(e => e.ValueKind != JsonValueKind.Object)) return null;
        var result = new List<Plan>();
        foreach (var entry in entries)
        {
            if (entry.Str("match") is not { } match || !Identifier(match, 40)) return null;
            if (entry.Str("name")?.Trim() is not { } name || new StringInfo(name).LengthInTextElements is < 1 or > 24) return null;
            double? monthly = null;
            if (entry.Has("monthly"))
            {
                monthly = Number(entry.Field("monthly"), 0, 100_000);
                if (monthly is null) return null;
            }
            result.Add(new Plan(match, name, monthly));
        }
        return result;
    }

    /// <summary>Lowercase letters, digits, dots, dashes and underscores: the shape of every model and plan the
    /// agents record.</summary>
    private static bool Identifier(string text, int length) =>
        new StringInfo(text).LengthInTextElements >= 1 && new StringInfo(text).LengthInTextElements <= length
        && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    /// <summary>A finite number inside the range; a JSON true or false is not one.</summary>
    private static double? Number(JsonElement? value, double low, double high)
    {
        if (value is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDouble(out var d)) return null;
        return double.IsFinite(d) && d >= low && d <= high ? d : null;
    }

    /// <summary>"2026-09-22", read as that day in UTC.</summary>
    private static DateTimeOffset? Day(string text)
    {
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
            .Where(n => n is not null).Select(n => n!.Value).ToList();
        if (parts.Count != 3 || parts[0] is < 2024 or > 2100) return null;
        if (parts[1] is < 1 or > 12 || parts[2] < 1 || parts[2] > DateTime.DaysInMonth(parts[0], parts[1])) return null;
        return new DateTimeOffset(parts[0], parts[1], parts[2], 0, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>The list that ships with the app, embedded in Notchly.Core.</summary>
public static class AgentPriceSource
{
    public const string ResourceName = "Notchly.Core.Agents.agent-prices.json";
    /// <summary>Where the project publishes newer lists.</summary>
    public static readonly Uri Remote = new("https://raw.githubusercontent.com/vorssaint/vorssaint-utils/main/Resources/agent-prices.json");
    /// <summary>A newer list is looked for at most this often (seconds).</summary>
    public const double RefreshInterval = 86_400;
    /// <summary>After a failed download, the next attempt waits this long (seconds).</summary>
    public const double RetryInterval = 6 * 3_600;

    private static readonly Lazy<AgentPriceList?> Shipped = new(() =>
    {
        using var stream = typeof(AgentPriceSource).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return AgentPriceList.Decode(memory.ToArray());
    });

    public static AgentPriceList? Bundled() => Shipped.Value;

    /// <summary>Whether a download is due: a day after the list on disk was saved, and, after a failure,
    /// <see cref="RetryInterval"/> after the last attempt.</summary>
    public static bool DownloadDue(DateTimeOffset now, DateTimeOffset saved, DateTimeOffset attempted, bool failed) =>
        now.Since(saved) >= RefreshInterval && now.Since(attempted) >= (failed ? RetryInterval : RefreshInterval);
}

/// <summary>
/// Costs are what the same traffic would bill through the API. A plan pays a flat price instead, so the island
/// presents them as API value. Prices come from the installed list, the shipped one until another is installed.
/// </summary>
public static class AgentPricing
{
    private static readonly Lock Gate = new();
    private static AgentPriceList? installed;

    /// <summary>The list prices come from, safe to read from any thread.</summary>
    public static AgentPriceList List
    {
        get
        {
            lock (Gate) return installed ??= AgentPriceSource.Bundled() ?? AgentPriceList.Empty;
        }
    }

    /// <summary>Makes <paramref name="list"/> the one prices come from; false when nothing changes.</summary>
    public static bool Install(AgentPriceList list)
    {
        lock (Gate)
        {
            installed ??= AgentPriceSource.Bundled() ?? AgentPriceList.Empty;
            if (installed.Equals(list)) return false;
            installed = list;
            return true;
        }
    }

    /// <summary>Provider prefixes, snapshot dates and context tags vary by where the model runs; the family name
    /// in the middle does not.</summary>
    public static string Normalized(string model)
    {
        var id = model.ToLowerInvariant().Trim();
        var claude = id.IndexOf("claude-", StringComparison.Ordinal);
        if (claude >= 0) id = id[claude..];
        // Copilot names Claude point releases with a dot where Anthropic's own logs use a dash.
        if (id.StartsWith("claude-", StringComparison.Ordinal)) id = id.Replace('.', '-');
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        foreach (var marker in new[] { '@', '[' })
        {
            var index = id.IndexOf(marker);
            if (index >= 0) id = id[..index];
        }
        return id;
    }

    /// <summary>Words that make a variant a model of its own, with its own price.</summary>
    public static readonly IReadOnlyList<string> Siblings =
        ["mini", "nano", "pro", "lite", "research", "search", "audio", "realtime", "transcribe", "tts", "cyber", "image"];

    /// <summary>The longest matching family wins, so a point release the list names never inherits the price of
    /// the version it extends; an unnamed sibling gets no price rather than a wrong one.</summary>
    public static AgentPrice? Price(string model, AgentPriceList? list = null)
    {
        list ??= List;
        var id = Normalized(model);
        var table = id.StartsWith("claude-", StringComparison.Ordinal) ? list.Claude : list.Codex;
        AgentPriceList.Model? match = null;
        foreach (var entry in table)
        {
            if (Matches(id, entry.Id) && (match is null || entry.Id.Length > match.Id.Length)) match = entry;
        }
        if (match is null) return null;
        var rest = id[match.Id.Length..];
        if (Siblings.Any(word => rest.Contains(word, StringComparison.Ordinal))) return null;
        return match.Price;
    }

    /// <summary>A family matches at a word boundary: "gpt-5" is not "gpt-5.1", and a number after it names another
    /// version, so "claude-opus-5" is not "claude-opus-5-6". A snapshot date after it is the same model.</summary>
    private static bool Matches(string id, string family)
    {
        if (!id.StartsWith(family, StringComparison.Ordinal)) return false;
        var rest = id[family.Length..];
        if (rest.Length == 0) return true;
        if (rest[0] is not ('-' or '_' or ':')) return false;
        var digits = rest.Skip(1).TakeWhile(char.IsDigit).Count();
        return digits == 0 || digits >= 4;
    }

    /// <summary>What <paramref name="billable"/> costs on <paramref name="model"/>, and what its cache reads saved;
    /// a null cost when the model has no price.</summary>
    public static (double? Cost, double Savings) Cost(AgentBillable billable, string model, AgentPriceList? list = null)
    {
        // Activity-only records establish a date, not unpriced token usage.
        if (billable.IsAggregate && billable.Tokens.Total == 0) return (0, 0);
        list ??= List;
        if (Price(model, list) is not { } price) return (null, 0);
        var tokens = billable.Tokens;
        var longWrite = Math.Min(Math.Max(0, billable.LongCacheWrite), tokens.CacheWrite);
        var multiplier = billable.Fast ? price.FastMultiplier : 1;
        if (billable.Domestic) multiplier *= list.UsOnlyMultiplier;
        var inputRate = multiplier;
        var outputRate = multiplier;
        if (!billable.IsAggregate && price.LongContext is { } longContext && tokens.Prompt > longContext.Above)
        {
            inputRate *= longContext.Input;
            outputRate *= longContext.Output;
        }
        var cost = ((double)tokens.Input * price.Input * inputRate
                    + (double)(tokens.CacheWrite - longWrite) * price.CacheWrite * inputRate
                    + (double)longWrite * price.CacheWriteLong * inputRate
                    + (double)tokens.CacheRead * price.CacheRead * inputRate
                    + (double)tokens.Output * price.Output * outputRate) / 1_000_000
                   + (double)Math.Max(0, billable.WebSearches) * list.WebSearch;
        var savings = (double)tokens.CacheRead * Math.Max(0, price.Input - price.CacheRead) * inputRate / 1_000_000;
        return (cost, savings);
    }

    /// <summary>Readable model names: "claude-opus-5-5" reads "Opus 5.5" and "gpt-6-astra" reads "GPT-6 Astra".
    /// Built from the name itself, so a model no list knows yet still reads well.</summary>
    public static string DisplayName(string model)
    {
        var id = Normalized(model);
        if (id.Length == 0) return "";
        static bool AllDigits(string s) => s.All(char.IsDigit);
        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            // A router's tag after a colon, like ":thinking", is a mode of the same model.
            var family = id.Split(':', 2)[0];
            var parts = family[7..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Snapshot dates and version suffixes add nothing to a name.
            parts.RemoveAll(p => p.Length >= 6 && AllDigits(p));
            parts.RemoveAll(p => p.Length > 1 && p[0] == 'v' && char.IsDigit(p[1]));
            parts.RemoveAll(p => p == "latest");
            static bool IsVersion(string p) => p.All(c => char.IsDigit(c) || c == '.');
            var words = parts.Where(p => !IsVersion(p)).ToList();
            var version = string.Join(".", parts.Where(IsVersion));
            var name = new List<string> { words.Count > 0 ? Capitalized(words[0]) : "", version };
            name.AddRange(words.Skip(1).Select(Capitalized));
            return string.Join(" ", name.Where(n => n.Length > 0));
        }
        if (!id.StartsWith("gpt-", StringComparison.Ordinal)) return id;
        var segments = id[4..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        // An alias names whatever it points to today; a dated snapshot is the same model.
        segments.RemoveAll(p => p == "latest");
        segments.RemoveAll(p => p.Length >= 6 && AllDigits(p));
        if (segments.Count >= 4 && int.TryParse(segments[^3], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 2000 and <= 2100 && segments.TakeLast(2).All(p => p.Length == 2 && AllDigits(p)))
        {
            segments.RemoveRange(segments.Count - 3, 3);
        }
        if (segments.Count == 0) return "GPT";
        var first = segments[0];
        var head = char.IsDigit(first[0]) ? "GPT-" + first : "GPT " + Capitalized(first);
        return string.Join(" ", new[] { head }.Concat(segments.Skip(1).Select(Capitalized)));
    }

    /// <summary>Foundation's capitalized: every letter that follows a non-letter is uppercased, the rest lowercased,
    /// so "astra:20b" reads "Astra:20B".</summary>
    internal static string Capitalized(string text)
    {
        var chars = text.ToCharArray();
        var previousLetter = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = previousLetter ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                previousLetter = true;
            }
            else
            {
                previousLetter = false;
            }
        }
        return new string(chars);
    }
}

/// <summary>Subscriptions, recognized from what the agents keep on disk.</summary>
public static class AgentPlans
{
    public static AgentPlan? Claude(string? organizationType, string? rateLimitTier, AgentPriceList? list = null)
    {
        list ??= AgentPricing.List;
        var type = (organizationType ?? "").ToLowerInvariant();
        var recorded = (rateLimitTier ?? "").ToLowerInvariant() + " " + type;
        if (list.ClaudePlans.FirstOrDefault(p => recorded.Contains(p.Match, StringComparison.Ordinal)) is { } plan)
            return new AgentPlan(plan.Name, plan.Monthly);
        // A plan newer than the list still shows its own name.
        var name = type.Replace("claude_", "").Replace('_', ' ').Trim(' ');
        return name.Length == 0 ? null : new AgentPlan(AgentPricing.Capitalized(name), null);
    }

    public static AgentPlan? Codex(string? planType, AgentPriceList? list = null)
    {
        list ??= AgentPricing.List;
        var raw = planType?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return null;
        if (list.CodexPlans.FirstOrDefault(p => p.Match == raw) is { } plan) return new AgentPlan(plan.Name, plan.Monthly);
        return new AgentPlan(char.ToUpperInvariant(raw[0]) + raw[1..], null);
    }
}
