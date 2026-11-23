// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/Harness/CodexReader.swift. Codex on Windows keeps
// the same rollout JSONL under %USERPROFILE%\.codex\sessions and archived_sessions.
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>
/// Reads Codex session logs. Token records and running-total events become usage, task events open and close
/// turns, and limit readings become the account's allowance windows.
/// </summary>
public sealed class CodexReader : IAgentHarnessReader
{
    public AgentProvider Provider => AgentProvider.Codex;
    public bool ReadsFullHistoryFirst => false;

    public IReadOnlyList<AgentLogEntry> Parse(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now)
    {
        if (AgentLogParser.FirstType(line.Span) is not { } kind) return [];
        switch (kind.Name)
        {
            case "token_usage_record" or "turn_context" or "session_meta":
                break;
            case "event_msg":
                if (AgentLogParser.FirstType(line.Span, kind.End) is not { } inner || !AgentLogParser.CodexEvents.Contains(inner.Name)) return [];
                break;
            default:
                return [];
        }
        using var document = AgentLogParser.Object(line);
        if (document is null || document.RootElement.Obj("payload") is not { } payload) return [];
        var json = document.RootElement;
        var date = AgentLogParser.Timestamp(json.Field("timestamp")) ?? now;
        switch (json.Str("type"))
        {
            case "session_meta":
            {
                // History copied from another thread repeats that thread's metadata; only the first line
                // describes this log's own thread.
                var id = payload.Str("id") ?? "";
                if (id.Length > 0) state.CodexTotalsSession = id;
                if (state.CodexHeaderRead) return [];
                state.CodexHeaderRead = true;
                if (id.Length > 0) state.Session = id;
                if (payload.Str("cwd") is { Length: > 0 } cwd) state.Project = AgentLogParser.ProjectName(cwd);
                // Subagents, reviews and memory tasks Codex starts itself name `subagent` or `internal`; a
                // person's thread names its client.
                var source = payload.Obj("source");
                state.CodexSubagent = source.Field("subagent") is not null || source.Field("internal") is not null;
                // A subagent's session is the thread a person started, however deep it runs.
                var root = payload.Str("session_id") ?? payload.Str("parent_thread_id") ?? "";
                if (state.CodexSubagent && root.Length > 0 && root != state.Session) state.CodexRoot = root;
                return [];
            }
            case "turn_context":
                if (payload.Str("model") is { Length: > 0 } model) state.Model = model;
                if (payload.Str("cwd") is { Length: > 0 } folder) state.Project = AgentLogParser.ProjectName(folder);
                if (payload.Str("service_tier") is { } tier) state.Fast = AgentLogParser.FastTier(tier);
                return [];
            case "token_usage_record":
            {
                if (payload.Obj("usage") is not { } usage) return [];
                state.SawUsageRecords = true;
                if (payload.Str("session_id") is { Length: > 0 } session && state.Session.Length == 0) state.Session = session;
                var response = payload.Str("response_id") ?? "";
                var key = response.Length == 0 ? $"codex:{state.Session}:{AgentTime.KeyText(date)}" : $"codex:{response}";
                return [Usage(AgentLogParser.CodexTokens(usage), key, date, state)];
            }
            case "event_msg":
                return Event(payload, date, state);
            default:
                return [];
        }
    }

    private static IReadOnlyList<AgentLogEntry> Event(JsonElement payload, DateTimeOffset date, AgentLogState state)
    {
        switch (payload.Str("type"))
        {
            case "token_count":
            {
                var entries = new List<AgentLogEntry>();
                if (payload.Obj("rate_limits") is { } limits)
                {
                    if (AgentLogParser.IsMainBucket(limits) && AgentLogParser.CodexWindows(limits, date) is { Count: > 0 } windows)
                        entries.Add(new AgentLogEntry.Limits(new AgentLimits(AgentProvider.Codex, windows, date, AgentLimitSource.SessionLog)));
                    if (limits.Str("plan_type") is { Length: > 0 } plan) entries.Add(new AgentLogEntry.Plan(plan, date));
                }
                // Running totals repeat when only the limits changed; a response is whatever the total grew by
                // since the previous reading.
                if (!state.SawUsageRecords && payload.Obj("info") is { } info && info.Obj("total_token_usage") is { } totalUsage)
                {
                    var total = AgentLogParser.CodexTokens(totalUsage);
                    if (total != state.LastTotal && total.Total > (state.LastTotal?.Total ?? 0))
                    {
                        var delta = info.Obj("last_token_usage") is { } last
                            ? AgentLogParser.CodexTokens(last)
                            : total.GrowthSince(state.LastTotal ?? default);
                        entries.Add(Usage(delta, $"codex:{state.CodexTotalsSession}:total:{total.Total}", date, state));
                    }
                    state.LastTotal = total;
                }
                return entries;
            }
            case "thread_settings_applied":
                if (payload.Obj("thread_settings").Str("service_tier") is { } tier) state.Fast = AgentLogParser.FastTier(tier);
                return [];
            case "task_started":
                state.TurnOpen = true;
                return [new AgentLogEntry.TurnBegan(AgentLogParser.Seconds(payload.Field("started_at")) ?? date)];
            case "task_complete" or "turn_aborted":
            {
                var open = state.TurnOpen;
                state.TurnOpen = false;
                if (!open) return [];
                var duration = payload.Num("duration_ms") is { } ms ? ms / 1000 : (double?)null;
                return [new AgentLogEntry.TurnEnded(AgentLogParser.Seconds(payload.Field("completed_at")) ?? date,
                                                    payload.Str("type") == "task_complete", duration)];
            }
            default:
                return [];
        }
    }

    private static AgentLogEntry Usage(AgentTokens tokens, string key, DateTimeOffset date, AgentLogState state)
    {
        var billable = new AgentBillable(tokens, Fast: state.Fast);
        var priced = AgentPricing.Cost(billable, state.Model);
        return new AgentLogEntry.Usage(key, new AgentUsageRecord(AgentProvider.Codex, date, state.Model, state.Project, state.Session,
                                                                 tokens, priced.Cost, priced.Savings), billable);
    }
}
