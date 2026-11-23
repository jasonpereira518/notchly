// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentLogParser.swift (entries, per-log state, byte
// tests, shared values, ISO times), Harness/AgentHarnessReader.swift, AgentLogObject.swift (as a top-level
// JSON view) and the pure line splitting of AgentUsageStore.swift's AgentLogReader. Paths may use either
// separator, since agents on Windows write backslashes.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>
/// What one log line says, reduced to the few facts the island keeps. Only usage counters, model names,
/// times and folder names leave a line, besides the identifiers of Claude's shell commands still running,
/// held in memory only; prompts, replies and tool output are never decoded into anything that is stored.
/// </summary>
public abstract record AgentLogEntry
{
    /// <summary><paramref name="Key"/> identifies the response across duplicate lines and files.</summary>
    public sealed record Usage(string Key, AgentUsageRecord Record, AgentBillable Billable) : AgentLogEntry;
    /// <summary>A later chunk can identify the model of an already-counted request.</summary>
    public sealed record UsageModel(string Key, string Model) : AgentLogEntry;
    public sealed record Limits(AgentLimits Reading) : AgentLogEntry;
    public sealed record Plan(string Name, DateTimeOffset ObservedAt) : AgentLogEntry;
    public sealed record TurnBegan(DateTimeOffset Date) : AgentLogEntry;
    /// <summary>Model and folder context known before usage is reported.</summary>
    public sealed record TurnContext(string Model, string Project) : AgentLogEntry;
    /// <summary>Work continues; a null date when the line was not worth decoding for its time.</summary>
    public sealed record TurnActive(DateTimeOffset? Date) : AgentLogEntry;
    /// <summary>A step ended expecting the agent to go on; unless work follows soon, the turn is over.</summary>
    public sealed record TurnSettled(DateTimeOffset Date) : AgentLogEntry;
    /// <summary>A turn ended. <paramref name="Duration"/>, in seconds, is what the agent measured, when it did.</summary>
    public sealed record TurnEnded(DateTimeOffset? Date, bool Completed, double? Duration) : AgentLogEntry;
    public sealed record Reset(DateTimeOffset Date) : AgentLogEntry;
}

/// <summary>Per-file context carried from line to line.</summary>
public sealed class AgentLogState
{
    public string Session { get; set; } = "";
    public string Project { get; set; } = "";
    public string Model { get; set; } = "";
    public bool TurnOpen { get; set; }
    /// <summary>Newer Codex logs write one usage record per response; older ones only carry running totals,
    /// which are the fallback until a record appears.</summary>
    public bool SawUsageRecords { get; set; }
    public AgentTokens? LastTotal { get; set; }
    /// <summary>Codex runs the thread on the fast tier, which bills at a premium.</summary>
    public bool Fast { get; set; }
    /// <summary>A Codex subagent, review or memory task owns no turn; only the log's metadata says so.</summary>
    public bool CodexSubagent { get; set; }
    /// <summary>The first Codex metadata line describes the log's own thread; a later one is copied history.</summary>
    public bool CodexHeaderRead { get; set; }
    /// <summary>The thread a person started that a Codex subagent works for.</summary>
    public string CodexRoot { get; set; } = "";
    /// <summary>The thread whose running totals the lines read now report.</summary>
    public string CodexTotalsSession { get; set; } = "";
    /// <summary>Copilot shutdown counters are cumulative for the life of a session.</summary>
    public Dictionary<string, AgentTokens> CopilotTotals { get; set; } = [];
    /// <summary>Counted requests by model; the empty key holds unresolved attribution.</summary>
    public Dictionary<string, int> CopilotRequests { get; set; } = [];
    public Dictionary<string, string> CopilotRequestModels { get; set; } = [];
    public Dictionary<string, int> CopilotReportedRequests { get; set; } = [];
    public string? CopilotTurnId { get; set; }
    public bool CopilotFinalResponse { get; set; }
    /// <summary>The parent session when a database row belongs to a subagent.</summary>
    public string ParentSession { get; set; } = "";
    /// <summary>OpenCode stores all sessions in one database, tracking per-session state.</summary>
    public Dictionary<string, OpenCodeSessionState> OpenCodeSessions { get; set; } = [];
    /// <summary>Claude's shell commands still waiting for their result, by tool call. They run on this
    /// computer, so the turn goes on without the network.</summary>
    public HashSet<string> RunningCommands { get; set; } = [];

    /// <summary>A deep copy, for saving progress.</summary>
    public AgentLogState Clone() => new()
    {
        Session = Session, Project = Project, Model = Model, TurnOpen = TurnOpen, SawUsageRecords = SawUsageRecords,
        LastTotal = LastTotal, Fast = Fast, CodexSubagent = CodexSubagent, CodexHeaderRead = CodexHeaderRead,
        CodexRoot = CodexRoot, CodexTotalsSession = CodexTotalsSession,
        CopilotTotals = new(CopilotTotals), CopilotRequests = new(CopilotRequests),
        CopilotRequestModels = new(CopilotRequestModels), CopilotReportedRequests = new(CopilotReportedRequests),
        CopilotTurnId = CopilotTurnId, CopilotFinalResponse = CopilotFinalResponse, ParentSession = ParentSession,
        OpenCodeSessions = OpenCodeSessions.ToDictionary(p => p.Key, p => p.Value.Clone()),
        RunningCommands = [.. RunningCommands],
    };
}

/// <summary>Per-session turn and model tracking for OpenCode databases.</summary>
public sealed class OpenCodeSessionState
{
    public string Project { get; set; } = "";
    public string Model { get; set; } = "";
    public bool TurnOpen { get; set; }
    public DateTimeOffset? TurnStarted { get; set; }
    public string ActiveUserMessageId { get; set; } = "";
    /// <summary>The newest moment the session's rows tell of.</summary>
    public DateTimeOffset? LastActivity { get; set; }
    /// <summary>When a step ended expecting the loop to go on, until a row follows.</summary>
    public DateTimeOffset? SettledAt { get; set; }
    /// <summary>The reply being written, until it completes.</summary>
    public string WritingReplyId { get; set; } = "";
    /// <summary>When the prompt the session answers now was written.</summary>
    public DateTimeOffset? ActivePromptDate { get; set; }
    public BoundedIdSet SeenUserMessageIds { get; set; } = new();
    public BoundedIdSet CompletedAssistantMessageIds { get; set; } = new();
    public BoundedIdSet CompletedUserMessageIds { get; set; } = new();

    public OpenCodeSessionState Clone() => new()
    {
        Project = Project, Model = Model, TurnOpen = TurnOpen, TurnStarted = TurnStarted,
        ActiveUserMessageId = ActiveUserMessageId, LastActivity = LastActivity, SettledAt = SettledAt,
        WritingReplyId = WritingReplyId, ActivePromptDate = ActivePromptDate,
        SeenUserMessageIds = SeenUserMessageIds.Clone(), CompletedAssistantMessageIds = CompletedAssistantMessageIds.Clone(),
        CompletedUserMessageIds = CompletedUserMessageIds.Clone(),
    };
}

/// <summary>A bounded number of message ids a session has settled: past a hundred, the oldest goes.</summary>
public sealed class BoundedIdSet
{
    private readonly HashSet<string> set = [];
    private readonly Queue<string> order = new();

    public int Count => set.Count;
    public bool Contains(string id) => set.Contains(id);
    public IEnumerable<string> Items => order;

    public void Remember(string id)
    {
        if (set.Count > 100) set.Remove(order.Dequeue());
        if (set.Add(id)) order.Enqueue(id);
    }

    public BoundedIdSet Clone()
    {
        var copy = new BoundedIdSet();
        foreach (var id in order) copy.Remember(id);
        return copy;
    }
}

/// <summary>One harness's log line parser. What it carries from line to line lives in <see cref="AgentLogState"/>.</summary>
public interface IAgentHarnessReader
{
    AgentProvider Provider { get; }
    /// <summary>What one log line (one JSON object, without its line break) says.</summary>
    IReadOnlyList<AgentLogEntry> Parse(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now);
    /// <summary>True when a log's first reading passes on only Copilot's event lines
    /// (<see cref="AgentLogParser.CopilotHistoryLine"/>); only Copilot sets it.</summary>
    bool ReadsFullHistoryFirst { get; }
}

public static class AgentReaders
{
    /// <summary>The reader that parses <paramref name="provider"/>'s logs; null for Cursor, which keeps none.</summary>
    public static IAgentHarnessReader? For(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => new ClaudeReader(),
        AgentProvider.Codex => new CodexReader(),
        AgentProvider.OpenCode => new OpenCodeReader(),
        AgentProvider.Copilot => new CopilotReader(),
        _ => null,
    };

    public static IReadOnlyList<AgentLogEntry> Parse(this IAgentHarnessReader reader, string line, AgentLogState state, DateTimeOffset now) =>
        reader.Parse(Encoding.UTF8.GetBytes(line), state, now);
}

public static class AgentLogParser
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 1024 };

    // MARK: Byte tests

    /// <summary>Structural keys are written without spaces and never appear unescaped inside a string value,
    /// so a byte match is a safe first filter.</summary>
    public static bool Contains(ReadOnlySpan<byte> line, ReadOnlySpan<byte> needle) => line.IndexOf(needle) >= 0;

    /// <summary>The first <c>"type":"…"</c> value at or after <paramref name="start"/>, and where it ends. A Codex
    /// rollout line writes its own type before its payload, so the search stops early instead of crossing tool
    /// output or compacted history.</summary>
    public static (string Name, int End)? FirstType(ReadOnlySpan<byte> line, int start = 0)
    {
        if (start < 0 || start >= line.Length) return null;
        var found = line[start..].IndexOf("\"type\":\""u8);
        if (found < 0) return null;
        var value = start + found + 8;
        // Type names are short identifiers.
        var limit = Math.Min(line.Length, value + 64);
        if (value >= limit) return null;
        var quote = line[value..limit].IndexOf((byte)'"');
        if (quote < 0) return null;
        return (Encoding.UTF8.GetString(line.Slice(value, quote)), value + quote + 1);
    }

    public static readonly IReadOnlySet<string> CodexEvents =
        new HashSet<string> { "token_count", "task_started", "task_complete", "turn_aborted", "thread_settings_applied" };

    /// <summary>The line as a JSON object, or null when it is anything else.</summary>
    public static JsonDocument? Object(ReadOnlyMemory<byte> line)
    {
        try
        {
            var document = JsonDocument.Parse(line, Options);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        return null;
    }

    public static JsonDocument? Object(string json) => Object(Encoding.UTF8.GetBytes(json));

    // MARK: Codex

    /// <summary>Input counts include what came from the cache.</summary>
    public static AgentTokens CodexTokens(JsonElement usage)
    {
        var input = Int(usage.Field("input_tokens"));
        var cached = Math.Min(input, Int(usage.Field("cached_input_tokens")));
        var written = Math.Min(input - cached, Int(usage.Field("cache_write_input_tokens")));
        return new AgentTokens(input - cached - written, written, cached, Int(usage.Field("output_tokens")),
                               Int(usage.Field("reasoning_output_tokens")));
    }

    /// <summary>Fast mode, named priority before July 2026.</summary>
    public static bool FastTier(string tier) => tier.ToLowerInvariant() is "fast" or "priority";

    // MARK: OpenCode

    /// <summary>OpenCode starts the next step the moment a step's tools return, so a turn that goes this long
    /// without one after a step ended has stopped, as when a permission was rejected.</summary>
    public const double OpenCodeSettle = 30;

    // MARK: GitHub Copilot

    public static bool CopilotEvent(string type) => type is "user.message" or "assistant.turn_start" or "assistant.message"
        or "assistant.turn_end" or "session.start" or "session.context_changed" or "session.model_change"
        or "session.shutdown" or "abort";

    /// <summary>The line filter for a Copilot log's first reading: only top-level event types count.</summary>
    public static bool CopilotHistoryLine(ReadOnlyMemory<byte> line)
    {
        using var document = Object(line);
        return document is not null && document.RootElement.Str("type") is { } type && CopilotEvent(type);
    }

    /// <summary>Codex logs a reading for each allowance: the main one under "codex", which older logs leave
    /// unnamed, and one for each model that has its own. Only the main one is kept.</summary>
    public static bool IsMainBucket(JsonElement limits)
    {
        var id = (limits.Str("limit_id") ?? "").ToLowerInvariant();
        return id.Length == 0 || id == "codex";
    }

    /// <summary>The names a log gives a window's figures; Codex's server spells the same ones in camel case.</summary>
    public sealed record WindowKeys(string Used, string Minutes, string Resets, string Individual, string Remaining);

    public static readonly WindowKeys LogWindowKeys = new("used_percent", "window_minutes", "resets_at", "individual_limit", "remaining_percent");

    /// <summary>Windows are told apart by their length, never by their slot: an account can report only its
    /// weekly window, in either slot. A Business account can leave both slots empty and report its allowance
    /// as the share left of its own limit instead.</summary>
    public static IReadOnlyList<AgentLimitWindow> CodexWindows(JsonElement limits, DateTimeOffset observed, WindowKeys? keys = null)
    {
        keys ??= LogWindowKeys;
        var windows = new List<AgentLimitWindow>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (limits.Obj(slot) is not { } window || window.Num(keys.Used) is not { } used || !double.IsFinite(used)) continue;
            var minutes = window.Num(keys.Minutes) is { } m && double.IsFinite(m) ? (int?)(int)m : null;
            var resets = Seconds(window.Field(keys.Resets));
            if (resets is null && window.Num("resets_in_seconds") is { } delay && double.IsFinite(delay))
                resets = observed.Plus(Math.Max(0, delay));
            windows.Add(new AgentLimitWindow("codex." + (minutes?.ToString(CultureInfo.InvariantCulture) ?? slot), Kind(minutes), minutes,
                                             null, Math.Min(100, Math.Max(0, used)), resets));
        }
        if (windows.Count == 0 && limits.Obj(keys.Individual) is { } individual
            && individual.Num(keys.Remaining) is { } remaining && double.IsFinite(remaining))
        {
            windows.Add(new AgentLimitWindow("codex.individual", AgentLimitKind.Other, null, null,
                                             Math.Min(100, Math.Max(0, 100 - remaining)), Seconds(individual.Field(keys.Resets))));
        }
        return windows.OrderBy(w => w.Minutes ?? int.MaxValue).ToList();
    }

    public static AgentLimitKind Kind(int? minutes)
    {
        if (minutes is not { } value || value <= 0) return AgentLimitKind.Other;
        if (value <= 12 * 60) return AgentLimitKind.Session;
        if (value is >= 6 * 1440 and <= 8 * 1440) return AgentLimitKind.Weekly;
        return AgentLimitKind.Other;
    }

    // MARK: Shared values

    /// <summary>The folder an agent ran in names the project. A worktree kept inside the repository still
    /// belongs to that repository.</summary>
    public static string ProjectName(string path)
    {
        foreach (var marker in new[] { "/.claude/worktrees/", "\\.claude\\worktrees\\" })
        {
            var index = path.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) path = path[..index];
        }
        while (path.Length > 1 && (path.EndsWith('/') || path.EndsWith('\\'))) path = path[..^1];
        if (path is "/" or "\\") return path;
        var last = path.LastIndexOfAny(['/', '\\']);
        return last >= 0 ? path[(last + 1)..] : path;
    }

    /// <summary>A positive count, capped so a damaged line cannot overflow the sums it joins; zero otherwise.</summary>
    public static long Int(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDouble(out var d)) return 0;
        if (!double.IsFinite(d) || d <= 0) return 0;
        return (long)Math.Min(d, 1e12);
    }

    /// <summary>Unix seconds, or milliseconds from agents that write those.</summary>
    public static DateTimeOffset? Seconds(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDouble(out var raw)) return null;
        if (!double.IsFinite(raw) || raw <= 0) return null;
        return raw > 100_000_000_000 ? AgentTime.FromUnixMilliseconds(raw) : AgentTime.FromUnixSeconds(raw);
    }

    public static DateTimeOffset? Timestamp(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text ? AgentTimestamp.Parse(text.GetString() ?? "") : Seconds(value);
}

/// <summary>Field access on JSON objects that never throws: a missing field or another kind reads as null.</summary>
public static class AgentJson
{
    public static JsonElement? Field(this JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : null;

    public static JsonElement? Field(this JsonElement? element, string key) => element is { } e ? e.Field(key) : null;

    /// <summary>Whether the field is present at all, even as null.</summary>
    public static bool Has(this JsonElement element, string key) => element.Field(key) is not null;

    public static string? Str(this JsonElement element, string key) =>
        element.Field(key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Str(this JsonElement? element, string key) => element is { } e ? e.Str(key) : null;

    public static JsonElement? Obj(this JsonElement element, string key) =>
        element.Field(key) is { ValueKind: JsonValueKind.Object } value ? value : null;

    public static JsonElement? Obj(this JsonElement? element, string key) => element is { } e ? e.Obj(key) : null;

    public static bool? Bool(this JsonElement element, string key) => element.Field(key)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>A JSON number; a true or false is not one.</summary>
    public static double? Num(this JsonElement element, string key) =>
        element.Field(key) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var d) ? d : null;

    public static double? Num(this JsonElement? element, string key) => element is { } e ? e.Num(key) : null;

    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>Whether an array field holds anything.</summary>
    public static bool HasItems(this JsonElement element, string key) =>
        element.Field(key) is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0;

    /// <summary>False when an object repeats one of its own keys: such an object is refused, as upstream's
    /// AgentLogObject does, so an envelope's type or agent is never ambiguous.</summary>
    internal static bool HasUniqueKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
        }
        return true;
    }

    /// <summary>The object field <paramref name="key"/> when it repeats none of its own keys (upstream's
    /// AgentLogObject.object).</summary>
    internal static JsonElement? UniqueObj(this JsonElement element, string key) =>
        element.Obj(key) is { } value && HasUniqueKeys(value) ? value : null;
}

/// <summary>ISO 8601 times as the agents write them, "2026-09-21T23:42:45.078Z", read without a formatter;
/// anything else goes through one.</summary>
public static class AgentTimestamp
{
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    public static DateTimeOffset? Parse(string text)
    {
        if (Fast(text) is { } fast) return fast;
        // A time without its offset is refused, as an internet date time needs one.
        if (!(text.EndsWith('Z') || text.Length > 6 && text[^3] == ':' && text[^6] is '+' or '-')) return null;
        return DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture,
                                            DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static DateTimeOffset? Fast(string b)
    {
        if (b.Length < 20 || b[4] != '-' || b[7] != '-' || b[10] != 'T' || b[13] != ':' || b[16] != ':') return null;
        int? Number(int start, int end)
        {
            var value = 0;
            for (var i = start; i < end; i++)
            {
                var digit = b[i] - '0';
                if (digit is < 0 or > 9) return null;
                value = value * 10 + digit;
            }
            return value;
        }
        if (Number(0, 4) is not { } year || Number(5, 7) is not { } month || Number(8, 10) is not { } day
            || Number(11, 13) is not { } hour || Number(14, 16) is not { } minute || Number(17, 19) is not { } second
            || month is < 1 or > 12 || day is < 1 or > 31 || hour >= 24 || minute >= 60 || second >= 61) return null;
        var index = 19;
        long fractionTicks = 0;
        if (b[index] == '.')
        {
            long scale = TimeSpan.TicksPerSecond / 10;
            index++;
            var rounding = false;
            while (index < b.Length && b[index] is >= '0' and <= '9')
            {
                if (scale > 0) fractionTicks += (b[index] - '0') * scale;
                else if (!rounding) { rounding = true; if (b[index] >= '5') fractionTicks += 1; }
                scale /= 10;
                index++;
            }
        }
        if (index >= b.Length) return null;
        var offset = 0;
        if (b[index] == 'Z')
        {
            if (index != b.Length - 1) return null;
        }
        else if ((b[index] == '+' || b[index] == '-') && b.Length == index + 6 && b[index + 3] == ':'
                 && Number(index + 1, index + 3) is { } hours && Number(index + 4, index + 6) is { } minutes)
        {
            offset = (hours * 3600 + minutes * 60) * (b[index] == '+' ? 1 : -1);
        }
        else
        {
            return null;
        }
        // Days since 1970 from the civil date, valid for every Gregorian year.
        var y = month <= 2 ? year - 1 : year;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        long days = era * 146_097L + doe - 719_468;
        var seconds = days * 86_400 + hour * 3600 + minute * 60 + second - offset;
        return DateTimeOffset.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + fractionTicks);
    }
}

/// <summary>
/// Splits what a log appended into complete lines, carrying a line still being written to the next chunk.
/// A line longer than <see cref="MaximumLine"/> is a pasted file or a tool's output, never a usage record;
/// it is skipped up to its terminator rather than held in memory.
/// </summary>
public sealed class AgentLineSplitter
{
    public const int ChunkSize = 4 << 20;
    public const int MaximumLine = 32 << 20;

    private byte[] pending = [];

    public AgentLineSplitter(int maximumLine = MaximumLine) => Maximum = maximumLine;

    public int Maximum { get; }
    /// <summary>Bytes of a line still being written, held until its break arrives.</summary>
    public int PendingCount => pending.Length;
    /// <summary>True while skipping the rest of an oversized line.</summary>
    public bool Discarding { get; set; }

    public void Reset()
    {
        pending = [];
        Discarding = false;
    }

    /// <summary>Hands over each complete, non-empty line in <paramref name="chunk"/>, in order, that
    /// <paramref name="including"/> (when given) admits.</summary>
    public void Feed(ReadOnlySpan<byte> chunk, Action<ReadOnlyMemory<byte>> line, Func<ReadOnlyMemory<byte>, bool>? including = null)
    {
        var buffer = new byte[pending.Length + chunk.Length];
        pending.CopyTo(buffer, 0);
        chunk.CopyTo(buffer.AsSpan(pending.Length));
        var start = 0;
        // What was carried over holds no line break.
        var position = pending.Length;
        var memory = new ReadOnlyMemory<byte>(buffer);
        while (position < buffer.Length)
        {
            var found = buffer.AsSpan(position).IndexOf((byte)'\n');
            if (found < 0) break;
            var end = position + found;
            var range = memory[start..end];
            if (Discarding) Discarding = false;
            else if (range.Length > 0 && range.Length <= Maximum && (including?.Invoke(range) ?? true)) line(range);
            start = end + 1;
            position = start;
        }
        // Once a line is oversized, scan only for its terminator.
        if (Discarding || buffer.Length - start > Maximum)
        {
            pending = [];
            Discarding = true;
        }
        else
        {
            pending = start < buffer.Length ? buffer[start..] : [];
        }
    }
}
