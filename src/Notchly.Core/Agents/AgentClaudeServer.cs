// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure part of upstream Sources/Vorssaint/Services/AgentUsage/AgentClaudeServer.swift: the stdin
// control requests that ask Claude Code for /usage's data, reading its get_usage reply into limit windows, how
// long a reading lasts, how often to ask, and when a reply came from Claude Code's saved copy instead of the
// account (the fetchedAtMs rule, read from ClaudeAccountProfile.swift's .claude.json). Launching is the app's job.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notchly.Core.Agents;

/// <summary>
/// Reads the same account-wide data as /usage through Claude Code's structured control protocol: the app starts
/// <c>claude</c> with <see cref="Arguments"/>, writes <see cref="InitializeRequest"/> then <see cref="UsageRequest"/>
/// on its stdin, and reads stdout lines with <see cref="Response"/>. No user message, model request, MCP server,
/// tool or hook is run.
/// </summary>
public static class AgentClaudeServer
{
    public enum Failure { Missing, NeedsSignIn, Outdated, Unreachable }

    /// <summary>A check gives up after this long (seconds).</summary>
    public const double Timeout = 25;
    /// <summary>While the AI page is on screen a reading this old (seconds) is asked again.</summary>
    public const double Freshness = 60;
    /// <summary>Otherwise Claude Code starts at most this often (seconds).</summary>
    public const double BackgroundFreshness = 5 * 60;
    public const double RetryAfter = 5 * 60;
    /// <summary>Only the person can install, update or sign in to Claude Code.</summary>
    public const double SettledRetry = 30 * 60;
    /// <summary>Claude Code answers from the copy it saved, without asking the account, while that copy is younger
    /// than this (seconds).</summary>
    public const double Reuse = 60;
    /// <summary>The opt-outs a person may keep in Claude Code's user settings.</summary>
    public static readonly IReadOnlyList<string> OptOuts = ["DISABLE_TELEMETRY", "DISABLE_ERROR_REPORTING", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"];
    /// <summary>The proxy and certificates a network may need, which can be kept there too.</summary>
    public static readonly IReadOnlyList<string> NetworkSettings = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "NODE_EXTRA_CA_CERTS"];

    /// <summary>The command line of a check: print mode over stream JSON, without tools, MCP servers, settings
    /// sources, hooks or a saved session.</summary>
    public static readonly IReadOnlyList<string> Arguments =
    [
        "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
        "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "",
        "--settings", "{\"disableAllHooks\":true}",
    ];

    /// <summary>How long (seconds) after the last check Claude Code is asked again. Each check in a row without an
    /// answer doubles the wait, up to the one for what only the person can fix.</summary>
    public static double Interval(Failure? failure, int repeated = 1, bool pageVisible = false) => failure switch
    {
        null => pageVisible ? Freshness : BackgroundFreshness,
        Failure.Unreachable => Math.Min(SettledRetry, RetryAfter * (1 << Math.Min(3, Math.Max(0, repeated - 1)))),
        _ => SettledRetry,
    };

    /// <summary>One stdin line: <c>{"type":"control_request","request_id":…,"request":…}</c> and a line break.</summary>
    public static string ControlRequest(string id, JsonObject request) =>
        new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }.ToJsonString() + "\n";

    public const string InitializeId = "initialize";
    public const string UsageId = "usage";
    public static string InitializeRequest => ControlRequest(InitializeId, new JsonObject { ["subtype"] = "initialize" });
    public static string UsageRequest => ControlRequest(UsageId, new JsonObject { ["subtype"] = "get_usage", ["skip_behaviors"] = true });

    /// <summary>What one stdout line says about request <paramref name="id"/>: null when it is another message (keep
    /// reading), a failure, or the success's response object as JSON text.</summary>
    public static (string? Result, Failure? Failure)? Response(string line, string id)
    {
        using var document = AgentLogParser.Object(line);
        if (document is null) return null;
        var message = document.RootElement;
        if (message.Str("type") != "control_response" || message.Obj("response") is not { } response || response.Str("request_id") != id) return null;
        if (response.Str("subtype") != "success")
        {
            var error = (response.Str("error") ?? "").ToLowerInvariant();
            // A CLI from before the request answers "Unsupported control request subtype".
            if (error.Contains("supported") || error.Contains("unknown")) return (null, Failure.Outdated);
            if (error.Contains("auth") || error.Contains("login") || error.Contains("sign in")) return (null, Failure.NeedsSignIn);
            return (null, Failure.Unreachable);
        }
        return (response.Obj("response")?.GetRawText() ?? "{}", null);
    }

    /// <summary>Whether a get_usage result says the account's limits are available; when not, the check needs a sign-in.</summary>
    public static bool Available(string result)
    {
        using var document = AgentLogParser.Object(result);
        return document is not null && document.RootElement.Bool("rate_limits_available") == true;
    }

    /// <summary>The windows a get_usage result reports, read at <paramref name="observed"/>; null when the account
    /// has none to report.</summary>
    public static AgentLimits? Limits(string result, DateTimeOffset observed)
    {
        using var document = AgentLogParser.Object(result);
        if (document is null || document.RootElement.Bool("rate_limits_available") != true
            || document.RootElement.Obj("rate_limits") is not { } limits) return null;
        var specs = new (string Key, string Id, AgentLimitKind Kind, int Minutes, string? Scope)[]
        {
            ("five_hour", "fh", AgentLimitKind.Session, 300, null), ("seven_day", "sd", AgentLimitKind.Weekly, 10_080, null),
            ("seven_day_opus", "so", AgentLimitKind.Weekly, 10_080, "Opus"), ("seven_day_sonnet", "sn", AgentLimitKind.Weekly, 10_080, "Sonnet"),
        };
        var windows = new List<AgentLimitWindow>();
        foreach (var spec in specs)
        {
            if (limits.Obj(spec.Key) is not { } window || window.Num("utilization") is not { } percent || !double.IsFinite(percent)) continue;
            // An idle window has no renewal yet and keeps its percentage; one that renewed before the reading says
            // nothing about the window now.
            var resets = window.Field("resets_at");
            var present = resets is { } r && r.ValueKind != JsonValueKind.Null;
            var end = resets is { ValueKind: JsonValueKind.String } text ? AgentTimestamp.Parse(text.GetString() ?? "") : null;
            if (present && !(end is { } e && e > observed)) continue;
            windows.Add(new AgentLimitWindow("claude." + spec.Id, spec.Kind, spec.Minutes, spec.Scope, Math.Min(100, Math.Max(0, percent)), end));
        }
        return windows.Count == 0 ? null : new AgentLimits(AgentProvider.Claude, windows, observed, AgentLimitSource.Account);
    }

    /// <summary>Expires each window without inventing zero usage after a reset. A window without a renewal lasts as it
    /// would in the Claude app's history: a session's length, or a day.</summary>
    public static AgentLimits? Current(AgentLimits? reading, DateTimeOffset now)
    {
        if (reading is null) return null;
        var age = now.Since(reading.ObservedAt);
        var windows = reading.Windows.Where(window =>
            window.ResetsAt is { } resets ? resets > now
                : age < (window.Kind == AgentLimitKind.Session ? (window.Minutes ?? 300) * 60.0 : 86_400)).ToList();
        return windows.Count == 0 ? null : reading with { Windows = windows };
    }

    /// <summary>
    /// The fetchedAtMs rule, once Claude Code has exited: a copy saved more than <see cref="Reuse"/> seconds before
    /// the check began was answered in place of the account, which was not reached (null). Otherwise the instant the
    /// reading describes: when the copy was fetched, or now, whichever is earlier.
    /// </summary>
    public static DateTimeOffset? Observed(DateTimeOffset started, DateTimeOffset? usageFetched, DateTimeOffset now)
    {
        if (usageFetched is { } fetched && started.Since(fetched) > Reuse) return null;
        return usageFetched is { } at && at < now ? at : now;
    }

    /// <summary>
    /// The environment a check gives Claude Code: the app's own, without another agent's tokens, provider routing or
    /// config overrides (opt-outs and network settings still apply), never auto-updating, with the opt-outs and proxy
    /// the person keeps in Claude Code's <c>settings.json</c> <c>env</c> (<paramref name="settingsJson"/>).
    /// </summary>
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> baseEnvironment, string? settingsJson,
                                                         string? profileDirectory = null)
    {
        var environment = baseEnvironment
            .Where(p => OptOuts.Contains(p.Key) || (!p.Key.StartsWith("CLAUDE_", StringComparison.Ordinal) && !p.Key.StartsWith("ANTHROPIC_", StringComparison.Ordinal)))
            .ToDictionary(p => p.Key, p => p.Value);
        environment.Remove("CLAUDECODE");
        if (profileDirectory is not null) environment["CLAUDE_CONFIG_DIR"] = profileDirectory;
        environment["DISABLE_AUTOUPDATER"] = "1";
        using var document = settingsJson is null ? null : AgentLogParser.Object(settingsJson);
        if (document?.RootElement.Obj("env") is not { } settings) return environment;
        foreach (var key in OptOuts.Concat(NetworkSettings))
        {
            switch (settings.Field(key))
            {
                case { ValueKind: JsonValueKind.String } text: environment[key] = text.GetString() ?? ""; break;
                case { ValueKind: JsonValueKind.True }: environment[key] = "true"; break;
                case { ValueKind: JsonValueKind.False }: environment[key] = "false"; break;
                case { ValueKind: JsonValueKind.Number } number: environment[key] = number.GetRawText(); break;
            }
        }
        return environment;
    }
}

/// <summary>What Claude Code's <c>.claude.json</c> says about its account: only the identity, plan and when it last
/// fetched usage survive, in memory. No token enters the usage archive.</summary>
public sealed record ClaudeAccountReading(ClaudeAccountReading.AccountIdentity? Identity, AgentPlan? Plan, DateTimeOffset? UsageFetched)
{
    public sealed record AccountIdentity(string? Account, string? Email, string? Organization);

    public static readonly ClaudeAccountReading None = new(null, null, null);

    public static ClaudeAccountReading Parse(ReadOnlySpan<byte> json, AgentPriceList? list = null)
    {
        if (json.Length > 4 << 20) return None;
        using var document = AgentLogParser.Object(json.ToArray());
        if (document is null) return None;
        var root = document.RootElement;
        var account = root.Obj("oauthAccount");
        var identity = account is { } a ? new AccountIdentity(Text(a.Field("accountUuid")), Text(a.Field("emailAddress")), Text(a.Field("organizationUuid"))) : null;
        var plan = account is { } p ? AgentPlans.Claude(p.Str("organizationType"), p.Str("organizationRateLimitTier") ?? p.Str("userRateLimitTier"), list) : null;
        DateTimeOffset? fetched = root.Obj("cachedUsageUtilization").Num("fetchedAtMs") is { } ms && double.IsFinite(ms) && ms > 0
            ? AgentTime.FromUnixMilliseconds(ms) : null;
        return new ClaudeAccountReading(identity, plan, fetched);
    }

    private static string? Text(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.String } text) return null;
        var clean = new StringBuilder();
        foreach (var c in text.GetString() ?? "") if (!char.IsControl(c)) clean.Append(c);
        var trimmed = clean.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed[..Math.Min(254, trimmed.Length)];
    }
}
