// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/Harness/CopilotReader.swift. Copilot CLI on Windows
// keeps the same events.jsonl under %USERPROFILE%\.copilot\session-state\<session>\.
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>
/// Reads the append-only event log Copilot CLI and the GitHub Copilot app share. Only session context, turn
/// boundaries and cumulative model counters are decoded; prompts, replies, reasoning and tool arguments are ignored.
/// </summary>
public sealed class CopilotReader : IAgentHarnessReader
{
    public AgentProvider Provider => AgentProvider.Copilot;
    public bool ReadsFullHistoryFirst => true;

    public IReadOnlyList<AgentLogEntry> Parse(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now)
    {
        using var document = AgentLogParser.Object(line);
        if (document is null || !AgentJson.HasUniqueKeys(document.RootElement)) return [];
        var envelope = document.RootElement;
        if (envelope.Str("type") is not { } type || !AgentLogParser.CopilotEvent(type) || envelope.UniqueObj("data") is not { } data) return [];
        var agent = envelope.Str("agentId") is { Length: > 0 } a ? a : null;
        var root = agent is null;
        // Subagents share the root log. Their responses count as activity, but their lifecycle and model
        // changes never change the root task.
        if (!root && type != "assistant.message") return [];
        var date = AgentLogParser.Timestamp(envelope.Field("timestamp")) ?? now;
        switch (type)
        {
            case "user.message" or "assistant.turn_start":
            {
                state.CopilotFinalResponse = false;
                state.CopilotTurnId = type == "assistant.turn_start" ? data.Str("turnId") : null;
                if (data.Str("model") is { Length: > 0 } model) state.Model = model;
                AgentLogEntry entry = state.TurnOpen ? new AgentLogEntry.TurnActive(date) : new AgentLogEntry.TurnBegan(date);
                state.TurnOpen = true;
                return [entry, new AgentLogEntry.TurnContext(state.Model, state.Project)];
            }
            case "assistant.message":
            {
                // A subagent can choose a different model; missing metadata is unknown, not evidence that it used
                // the root's selected model.
                var model = data.Str("model") is { Length: > 0 } named ? named : root ? state.Model : "";
                var entries = Activity(data, envelope, agent, model, date, state);
                if (!root) return entries;
                state.Model = model;
                // Each tool iteration has its own turn_end. Only an iteration delivering a final response can
                // finish the person's task.
                state.CopilotFinalResponse = !data.HasItems("toolRequests") && data.Str("phase") is not ("thinking" or "commentary");
                if (state.TurnOpen)
                {
                    entries.Add(new AgentLogEntry.TurnContext(state.Model, state.Project));
                    entries.Add(new AgentLogEntry.TurnActive(date));
                }
                return entries;
            }
            case "assistant.turn_end":
                if (!state.TurnOpen || (state.CopilotTurnId is not null && state.CopilotTurnId != data.Str("turnId"))) return [];
                if (!state.CopilotFinalResponse) return [new AgentLogEntry.TurnActive(date)];
                state.TurnOpen = false;
                state.CopilotFinalResponse = false;
                return [new AgentLogEntry.TurnEnded(date, true, null)];
            case "session.start":
                if (data.Str("sessionId") is { Length: > 0 } session) state.Session = session;
                if (data.Str("selectedModel") is { Length: > 0 } selected) state.Model = selected;
                if (data.UniqueObj("context") is { } context && ContextPath(context) is { } path) state.Project = AgentLogParser.ProjectName(path);
                return [];
            case "session.context_changed":
                if (ContextPath(data) is { } changed) state.Project = AgentLogParser.ProjectName(changed);
                return state.TurnOpen ? [new AgentLogEntry.TurnContext(state.Model, state.Project)] : [];
            case "session.model_change":
                if (data.Str("newModel") is { Length: > 0 } newModel) state.Model = newModel;
                return state.TurnOpen ? [new AgentLogEntry.TurnContext(state.Model, state.Project)] : [];
            case "abort":
                if (!state.TurnOpen) return [];
                state.TurnOpen = false;
                state.CopilotFinalResponse = false;
                return [new AgentLogEntry.TurnEnded(date, false, null)];
            case "session.shutdown":
            {
                var entries = Usage(data.Obj("modelMetrics"), envelope.Str("id"), date, state);
                if (state.TurnOpen)
                {
                    state.TurnOpen = false;
                    entries.Add(new AgentLogEntry.TurnEnded(date, false, null));
                }
                return entries;
            }
            default:
                return [];
        }
    }

    private static string? ContextPath(JsonElement context) =>
        new[] { "gitRoot", "cwd", "repository" }.Select(key => context.Str(key)).FirstOrDefault(p => !string.IsNullOrEmpty(p));

    private static List<AgentLogEntry> Activity(JsonElement data, JsonElement envelope, string? agent, string model,
                                                DateTimeOffset date, AgentLogState state)
    {
        var call = data.Str("apiCallId") is { Length: > 0 } c ? c : null;
        var message = (data.Str("messageId") is { Length: > 0 } m ? m : null) ?? envelope.Str("id") ?? AgentTime.KeyText(date);
        // Each API call can emit several messages. Older logs lack apiCallId, so they keep message-level
        // deduplication. IDs are namespaced by kind and agent so independent calls cannot collide.
        var request = (agent is not null ? "agent:" + agent : "root") + ":" + (call is not null ? "api:" + call : "message:" + message);
        var key = $"copilot:{state.Session}:{request}:activity";
        if (state.CopilotRequestModels.TryGetValue(request, out var previous))
        {
            if (previous.Length > 0 || model.Length == 0) return [];
            state.CopilotRequestModels[request] = model;
            state.CopilotRequests[previous] = state.CopilotRequests.GetValueOrDefault(previous) - 1;
            state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + 1;
            return [new AgentLogEntry.UsageModel(key, model)];
        }
        state.CopilotRequestModels[request] = model;
        state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + 1;
        return [new AgentLogEntry.Usage(key, new AgentUsageRecord(AgentProvider.Copilot, date, model, state.Project, state.Session,
                                                                  default, 0, 0), new AgentBillable(IsAggregate: true))];
    }

    private static List<AgentLogEntry> Usage(JsonElement? metrics, string? eventId, DateTimeOffset date, AgentLogState state)
    {
        if (metrics is not { } all) return [];
        var growth = new Dictionary<string, AgentTokens>();
        foreach (var property in all.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var model = property.Name;
            var total = Tokens(property.Value);
            growth[model] = state.CopilotTotals.TryGetValue(model, out var before) && total.Total >= before.Total
                ? total.GrowthSince(before) : total;
            state.CopilotTotals[model] = total;
            var reported = (int)AgentLogParser.Int(property.Value.Obj("requests").Field("count"));
            state.CopilotReportedRequests[model] = Math.Max(state.CopilotReportedRequests.GetValueOrDefault(model),
                                                            Math.Max(reported, total.Total > 0 ? 1 : 0));
        }
        var deficits = state.CopilotReportedRequests
            .Select(p => (Model: p.Key, Count: Math.Max(0, p.Value - state.CopilotRequests.GetValueOrDefault(p.Key)))).ToList();
        var unresolved = state.CopilotRequests.GetValueOrDefault("");
        var missing = Math.Max(0, deficits.Sum(d => d.Count) - unresolved);
        var requests = new Dictionary<string, int>();
        // Unknown responses can cover any model's deficit, but only once across the whole session. Only the
        // excess that must be this model is attributed; ambiguous requests stay in the unknown bucket.
        foreach (var deficit in deficits)
        {
            var certain = Math.Max(0, deficit.Count - unresolved);
            requests[deficit.Model] = certain;
            missing -= certain;
        }
        if (missing > 0) requests[""] = requests.GetValueOrDefault("") + missing;
        var entries = new List<AgentLogEntry>();
        foreach (var model in growth.Keys.Union(requests.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var tokens = growth.GetValueOrDefault(model);
            var count = requests.GetValueOrDefault(model);
            state.CopilotRequests[model] = state.CopilotRequests.GetValueOrDefault(model) + count;
            if (tokens.Total <= 0 && count <= 0) continue;
            var billable = new AgentBillable(tokens, IsAggregate: true);
            var priced = AgentPricing.Cost(billable, model);
            var checkpoint = eventId is { Length: > 0 } id ? id : AgentTime.KeyText(date);
            entries.Add(new AgentLogEntry.Usage($"copilot:{state.Session}:{checkpoint}:{model}",
                new AgentUsageRecord(AgentProvider.Copilot, date, model, state.Project, state.Session, tokens, priced.Cost, priced.Savings,
                                     requests: count), billable));
        }
        return entries;
    }

    private static AgentTokens Tokens(JsonElement metric)
    {
        var details = metric.Obj("tokenDetails");
        var usage = metric.Obj("usage");
        long Count(string detail, string fallback) =>
            details.Obj(detail) is { } d && d.Has("tokenCount") ? AgentLogParser.Int(d.Field("tokenCount")) : AgentLogParser.Int(usage.Field(fallback));
        return new AgentTokens(Count("input", "inputTokens"), Count("cache_write", "cacheWriteTokens"), Count("cache_read", "cacheReadTokens"),
                               Count("output", "outputTokens"), AgentLogParser.Int(usage.Field("reasoningTokens")));
    }
}
