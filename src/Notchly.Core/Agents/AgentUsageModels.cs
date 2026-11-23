// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageModels.swift: the agents, token counts,
// billed responses, plan allowances, live turns and the island's events. Windows adds Cursor, which keeps
// no logs and only reports an allowance. Instants are UTC DateTimeOffsets; durations are seconds.
namespace Notchly.Core.Agents;

/// <summary>
/// The coding agents whose usage the island reads. Their names are product names and stay untranslated.
/// The order is upstream's (Claude, Codex, OpenCode, Copilot) with Cursor last.
/// </summary>
public enum AgentProvider { Claude, Codex, OpenCode, Copilot, Cursor }

public static class AgentProviders
{
    /// <summary>Every provider, in the order the island lists them.</summary>
    public static readonly IReadOnlyList<AgentProvider> All =
        [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode, AgentProvider.Copilot, AgentProvider.Cursor];

    /// <summary>The identifier upstream stores and builds ids from: "claude", "codex", "opencode", "copilot", "cursor".</summary>
    public static string RawValue(this AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "claude",
        AgentProvider.Codex => "codex",
        AgentProvider.OpenCode => "opencode",
        AgentProvider.Copilot => "copilot",
        _ => "cursor",
    };

    public static AgentProvider? FromRawValue(string? raw) =>
        All.Cast<AgentProvider?>().FirstOrDefault(p => p!.Value.RawValue() == raw);

    public static string DisplayName(this AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.OpenCode => "OpenCode",
        AgentProvider.Copilot => "GitHub Copilot",
        _ => "Cursor",
    };

    /// <summary>Whether the provider reports a plan allowance: all but Copilot.</summary>
    public static bool ReportsLimits(this AgentProvider provider) => provider != AgentProvider.Copilot;

    /// <summary>Whether usage comes from local logs; Cursor only reports its allowance over the network.</summary>
    public static bool ReadsLogs(this AgentProvider provider) => provider != AgentProvider.Cursor;
}

/// <summary>
/// Token counts in the shape every log can be reduced to. <see cref="Input"/> excludes cache traffic, and
/// <see cref="Output"/> already contains the reasoning tokens.
/// </summary>
public readonly record struct AgentTokens(long Input = 0, long CacheWrite = 0, long CacheRead = 0, long Output = 0, long Reasoning = 0)
{
    public long Total => Input + CacheWrite + CacheRead + Output;
    /// <summary>Everything the model read, cached or not.</summary>
    public long Prompt => Input + CacheWrite + CacheRead;
    public double? CacheHitRate => Prompt > 0 ? (double)CacheRead / Prompt : null;

    public static AgentTokens operator +(AgentTokens a, AgentTokens b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output, a.Reasoning + b.Reasoning);

    public static AgentTokens operator -(AgentTokens a, AgentTokens b) =>
        new(a.Input - b.Input, a.CacheWrite - b.CacheWrite, a.CacheRead - b.CacheRead, a.Output - b.Output, a.Reasoning - b.Reasoning);

    /// <summary>Streamed replies are logged once per content block, and the earlier blocks carry the output
    /// counted so far. The largest reading is final.</summary>
    public AgentTokens Merged(AgentTokens other) =>
        new(Math.Max(Input, other.Input), Math.Max(CacheWrite, other.CacheWrite), Math.Max(CacheRead, other.CacheRead),
            Math.Max(Output, other.Output), Math.Max(Reasoning, other.Reasoning));

    /// <summary>What grew from <paramref name="previous"/>, never below zero in any category.</summary>
    public AgentTokens GrowthSince(AgentTokens previous) =>
        new(Math.Max(0, Input - previous.Input), Math.Max(0, CacheWrite - previous.CacheWrite),
            Math.Max(0, CacheRead - previous.CacheRead), Math.Max(0, Output - previous.Output),
            Math.Max(0, Reasoning - previous.Reasoning));
}

/// <summary>One billed model response. Immutable: the store replaces a record when a streamed reply grows.</summary>
public sealed record AgentUsageRecord
{
    public AgentUsageRecord(AgentProvider provider, DateTimeOffset date, string model, string project, string session,
                            AgentTokens tokens, double? cost, double savings, int requests = 1, bool reportedCost = false)
    {
        Provider = provider;
        Date = date;
        Model = model;
        Project = project;
        Session = session;
        Tokens = tokens;
        Cost = cost;
        Savings = savings;
        Requests = requests;
        ReportedCost = reportedCost;
    }

    public AgentProvider Provider { get; init; }
    public DateTimeOffset Date { get; init; }
    public string Model { get; init; }
    public string Project { get; init; }
    public string Session { get; init; }
    /// <summary>Model requests represented by this record; checkpoints may hold several.</summary>
    public int Requests { get; init; }
    public AgentTokens Tokens { get; init; }
    /// <summary>What the response would cost at API list prices, in US dollars; null when the model has no known price.</summary>
    public double? Cost { get; init; }
    /// <summary>What cache reads saved against paying the full input price.</summary>
    public double Savings { get; init; }
    /// <summary>Whether the provider reported the cost itself rather than it being derived from list pricing.</summary>
    public bool ReportedCost { get; init; }
}

public enum AgentLimitKind { Session, Weekly, Other }

/// <summary>A usage allowance and how much of it is spent, as the provider reports it.</summary>
/// <param name="Minutes">How long the window lasts, when the provider says so.</param>
/// <param name="Scope">A model the allowance applies to; null when it covers everything.</param>
/// <param name="EstimatedResetsAt">A presentation estimate, never a deadline for clearing usage or alerts.</param>
public sealed record AgentLimitWindow(string Id, AgentLimitKind Kind, int? Minutes, string? Scope, double UsedPercent,
                                      DateTimeOffset? ResetsAt, DateTimeOffset? EstimatedResetsAt = null)
{
    public double UsedFraction => Math.Min(1, Math.Max(0, UsedPercent / 100));
    public double RemainingFraction => 1 - UsedFraction;
}

public enum AgentLimitSource
{
    /// <summary>Saved on this computer by the Claude app (upstream's macOS history; unused on Windows so far).</summary>
    ClaudeApp,
    /// <summary>Copied by the agent into its session log with each response.</summary>
    SessionLog,
    /// <summary>Asked of the agent or the provider's service on request, which checks the account itself.</summary>
    Account,
}

/// <summary>An account's allowance windows, read at <see cref="ObservedAt"/>.</summary>
public sealed record AgentLimits(AgentProvider Provider, IReadOnlyList<AgentLimitWindow> Windows, DateTimeOffset ObservedAt,
                                 AgentLimitSource Source)
{
    public bool Equals(AgentLimits? other) =>
        other is not null && Provider == other.Provider && ObservedAt == other.ObservedAt && Source == other.Source
        && Windows.SequenceEqual(other.Windows);

    public override int GetHashCode() => HashCode.Combine(Provider, ObservedAt, Source, Windows.Count);
}

/// <summary>The subscription an account is on. <see cref="MonthlyPrice"/> is in US dollars and only compares API value.</summary>
public sealed record AgentPlan(string Name, double? MonthlyPrice);

/// <summary>A turn an agent is working on right now, from its session log.</summary>
public sealed record AgentLiveSession(string Id, AgentProvider Provider, DateTimeOffset Started, DateTimeOffset LastActivity,
                                      string Model, string Project, AgentTokens Tokens, double Cost);

/// <summary>Something worth a moment in the closed island.</summary>
public abstract record AgentUsageEvent
{
    /// <summary>A turn ended. <paramref name="Duration"/> is in seconds.</summary>
    public sealed record Finished(AgentProvider Provider, double Duration, double Cost, long Tokens, string Project) : AgentUsageEvent;
    public sealed record LimitWarning(AgentProvider Provider, AgentLimitWindow Window) : AgentUsageEvent;
    public sealed record LimitReset(AgentProvider Provider, AgentLimitWindow Window) : AgentUsageEvent;
    public sealed record BudgetReached(double Spent, double Budget) : AgentUsageEvent;
}

/// <summary>Instants as the logs write them: Unix seconds (or milliseconds), kept to the tick.</summary>
public static class AgentTime
{
    /// <summary>Upstream's Date.distantPast.</summary>
    public static readonly DateTimeOffset DistantPast = DateTimeOffset.MinValue;

    public static DateTimeOffset FromUnixSeconds(double seconds)
    {
        var whole = Math.Floor(seconds);
        var ticks = (long)whole * TimeSpan.TicksPerSecond + (long)Math.Round((seconds - whole) * TimeSpan.TicksPerSecond);
        return DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }

    public static DateTimeOffset FromUnixMilliseconds(double milliseconds)
    {
        var whole = Math.Floor(milliseconds);
        var ticks = (long)whole * TimeSpan.TicksPerMillisecond + (long)Math.Round((milliseconds - whole) * TimeSpan.TicksPerMillisecond);
        return DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }

    public static double ToUnixSeconds(DateTimeOffset date) => (date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    /// <summary>Seconds from <paramref name="earlier"/> to <paramref name="later"/>, like Swift's timeIntervalSince.</summary>
    public static double Since(this DateTimeOffset later, DateTimeOffset earlier) => (later.UtcTicks - earlier.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    /// <summary>The instant <paramref name="seconds"/> later, kept to the tick.</summary>
    public static DateTimeOffset Plus(this DateTimeOffset date, double seconds) =>
        date.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    /// <summary>The text a log key uses for an instant when the log names nothing better.</summary>
    internal static string KeyText(DateTimeOffset date) =>
        ToUnixSeconds(date).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
