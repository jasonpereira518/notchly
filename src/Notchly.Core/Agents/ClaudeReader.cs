// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/Harness/ClaudeReader.swift. Claude Code on Windows
// writes the same JSONL under %USERPROFILE%\.claude\projects; its cwd uses backslashes, which ProjectName reads.
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>
/// Reads Claude Code session logs. A prompt opens a turn, a reply keeps it going and records its usage, and an
/// interruption, a local command's output or a turn-ending tool result closes the turn again.
/// </summary>
public sealed class ClaudeReader : IAgentHarnessReader
{
    public AgentProvider Provider => AgentProvider.Claude;
    public bool ReadsFullHistoryFirst => false;

    public IReadOnlyList<AgentLogEntry> Parse(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now)
    {
        var bytes = line.Span;
        if (AgentLogParser.Contains(bytes, "\"type\":\"assistant\""u8)) return Assistant(line, state, now);
        if (!AgentLogParser.Contains(bytes, "\"type\":\"user\""u8)) return [];
        // A local command prints its output without asking the model anything. An output that arrives while a
        // turn is open closes it again. Only the person's own text counts: a tool result can quote the words.
        if (AgentLogParser.Contains(bytes, "[Request interrupted by user"u8) || AgentLogParser.Contains(bytes, "<local-command-std"u8))
        {
            using var document = AgentLogParser.Object(line);
            if (document is not null && document.RootElement.Str("type") == "user" && EndsTurn(document.RootElement))
            {
                if (document.RootElement.Bool("isSidechain") == true) return [];
                var open = state.TurnOpen;
                state.TurnOpen = false;
                return open ? [new AgentLogEntry.TurnEnded(null, false, null)] : [];
            }
        }
        // A tool that ends the turn, like the structured output a scripted session returns, gets no reply after
        // its result: the session is done.
        if (AgentLogParser.Contains(bytes, "\"toolEndsTurn\":true"u8))
        {
            using var document = AgentLogParser.Object(line);
            if (document is not null && document.RootElement is var json && json.Str("type") == "user"
                && json.Bool("toolEndsTurn") == true && json.Bool("isSidechain") != true)
            {
                var open = state.TurnOpen;
                state.TurnOpen = false;
                return open ? [new AgentLogEntry.TurnEnded(AgentLogParser.Timestamp(json.Field("timestamp")) ?? now, true, null)] : [];
            }
        }
        // /clear resets the context and may have no reply. A project command uses the same tags and starts work
        // as soon as the person invokes it.
        if (AgentLogParser.Contains(bytes, "<command-name>"u8))
        {
            using var document = AgentLogParser.Object(line);
            if (document is not null && document.RootElement is var json && json.Str("type") == "user"
                && json.Bool("isMeta") != true && json.Bool("isSidechain") != true
                && MessageTexts(json, singleBlock: true).Select(LocalCommandOf).FirstOrDefault(c => c is not null) is { } command)
            {
                Adopt(json, state);
                if (command == LocalCommand.Clear)
                {
                    // A resumed transcript can still have an unfinished tool call. Resetting the context
                    // discards it without a completion notice.
                    var wasOpen = state.TurnOpen;
                    state.TurnOpen = false;
                    state.RunningCommands.Clear();
                    if (wasOpen) return [new AgentLogEntry.TurnEnded(null, false, null)];
                }
                return [];
            }
        }
        // Tool results arrive inside a turn and can be large; while a turn is open, the line only has to say
        // that work goes on.
        if (state.TurnOpen)
        {
            if (state.RunningCommands.Count > 0)
            {
                using var document = AgentLogParser.Object(line);
                if (document is not null && document.RootElement is var json && json.Str("type") == "user"
                    && json.Bool("isMeta") != true && json.Bool("isSidechain") != true)
                {
                    var content = json.Obj("message").Field("content");
                    var results = content.Items().Where(b => b.ValueKind == JsonValueKind.Object && b.Str("type") == "tool_result").ToList();
                    if (results.Count > 0)
                    {
                        // A result can quote other commands or contain a log. Only its own call identifier
                        // completes that command.
                        foreach (var result in results)
                            if (result.Str("tool_use_id") is { } id) state.RunningCommands.Remove(id);
                    }
                    else
                    {
                        // A prompt inside an open turn, as when a session killed in the middle of a command
                        // resumes, leaves it behind.
                        state.RunningCommands.Clear();
                    }
                }
            }
            return [new AgentLogEntry.TurnActive(null)];
        }
        // A subagent's prompts and tool output never open a turn, and can be large.
        if (AgentLogParser.Contains(bytes, "\"isSidechain\":true"u8)) return [];
        using (var document = AgentLogParser.Object(line))
        {
            if (document is null) return [];
            var json = document.RootElement;
            if (json.Str("type") != "user" || json.Bool("isMeta") == true || json.Bool("isSidechain") == true) return [];
            Adopt(json, state);
            state.TurnOpen = true;
            state.RunningCommands.Clear();
            return [new AgentLogEntry.TurnBegan(AgentLogParser.Timestamp(json.Field("timestamp")) ?? now)];
        }
    }

    private static IReadOnlyList<AgentLogEntry> Assistant(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now)
    {
        using var document = AgentLogParser.Object(line);
        if (document is null) return [];
        var json = document.RootElement;
        if (json.Str("type") != "assistant" || json.Obj("message") is not { } message) return [];
        Adopt(json, state);
        var date = AgentLogParser.Timestamp(json.Field("timestamp")) ?? now;
        var entries = new List<AgentLogEntry>();
        var model = message.Str("model") ?? "";
        var id = message.Str("id") ?? "";
        var request = json.Str("requestId") ?? "";
        if (message.Obj("usage") is { } usage && model.Length > 0 && !model.StartsWith('<'))
        {
            state.Model = model;
            var tokens = new AgentTokens(
                AgentLogParser.Int(usage.Field("input_tokens")),
                AgentLogParser.Int(usage.Field("cache_creation_input_tokens")),
                AgentLogParser.Int(usage.Field("cache_read_input_tokens")),
                AgentLogParser.Int(usage.Field("output_tokens")),
                AgentLogParser.Int(usage.Obj("output_tokens_details").Field("thinking_tokens")));
            var billable = new AgentBillable(tokens,
                LongCacheWrite: AgentLogParser.Int(usage.Obj("cache_creation").Field("ephemeral_1h_input_tokens")),
                Fast: usage.Str("speed") == "fast",
                Domestic: usage.Str("inference_geo") == "us",
                WebSearches: AgentLogParser.Int(usage.Obj("server_tool_use").Field("web_search_requests")));
            var key = id.Length == 0 && request.Length == 0
                ? $"claude:{state.Session}:{AgentTime.KeyText(date)}" : $"claude:{id}:{request}";
            var priced = AgentPricing.Cost(billable, model);
            entries.Add(new AgentLogEntry.Usage(key, new AgentUsageRecord(AgentProvider.Claude, date, model, state.Project, state.Session,
                                                                          tokens, priced.Cost, priced.Savings), billable));
        }
        // A subagent's own ending is not the end of the turn it serves.
        if (json.Bool("isSidechain") == true) return entries;
        // A client can write bare local output with the assistant role. A model reply can quote the same
        // output and still start or end work.
        var stop = message.Str("stop_reason");
        if (stop is null && request.Length == 0 && (model == "<synthetic>" || (model.Length == 0 && id.Length == 0))
            && MessageTexts(json, singleBlock: true).Any(IsLocalOutput)) return entries;
        switch (stop)
        {
            case "end_turn" or "stop_sequence" or "max_tokens" or "refusal":
                // An error written in place of a reply, like a spent limit, stops the turn without finishing it.
                var failed = json.Bool("isApiErrorMessage") == true || model.StartsWith('<');
                if (state.TurnOpen) entries.Add(new AgentLogEntry.TurnEnded(date, !failed, null));
                state.TurnOpen = false;
                break;
            default:
                foreach (var block in message.Field("content").Items())
                {
                    if (block.ValueKind == JsonValueKind.Object && block.Str("type") == "tool_use" && block.Str("name") == "Bash"
                        && block.Str("id") is { } call) state.RunningCommands.Add(call);
                }
                // Ahead of the usage, so a reply that opens the turn again, as after an offline end, counts toward it.
                entries.Insert(0, state.TurnOpen ? new AgentLogEntry.TurnActive(date) : new AgentLogEntry.TurnBegan(date));
                state.TurnOpen = true;
                break;
        }
        return entries;
    }

    /// <summary>The interruption or local command output the person's message holds, never text inside a tool result.</summary>
    private static bool EndsTurn(JsonElement json) =>
        MessageTexts(json).Any(t => t.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        || MessageTexts(json, singleBlock: true).Any(IsLocalOutput);

    private enum LocalCommand { Clear, Model }

    /// <summary>Every character Swift counts as a new line.</summary>
    private static readonly char[] LineBreaks = [(char)0x0A, (char)0x0D, (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    private static LocalCommand? LocalCommandOf(string text)
    {
        if (!text.StartsWith("<command-", StringComparison.Ordinal)) return null;
        // Arguments can contain other command names, including on a new line.
        var header = text;
        var args = text.IndexOf("<command-args>", StringComparison.Ordinal);
        if (args >= 0)
        {
            if (!text.Trim().EndsWith("</command-args>", StringComparison.Ordinal)) return null;
            header = text[..args];
        }
        var names = header.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim(' ', '\t'))
            .Where(f => f.Length > 0 && !(f.StartsWith("<command-message>", StringComparison.Ordinal)
                                          && f.EndsWith("</command-message>", StringComparison.Ordinal)))
            .ToList();
        if (names.Count != 1) return null;
        return names[0] switch
        {
            "<command-name>/clear</command-name>" or "<command-name>/reset</command-name>" or "<command-name>/new</command-name>" => LocalCommand.Clear,
            "<command-name>/model</command-name>" => LocalCommand.Model,
            _ => null,
        };
    }

    private static bool IsLocalOutput(string text)
    {
        var output = text.Trim();
        return (output.StartsWith("<local-command-stdout>", StringComparison.Ordinal) && output.EndsWith("</local-command-stdout>", StringComparison.Ordinal))
            || (output.StartsWith("<local-command-stderr>", StringComparison.Ordinal) && output.EndsWith("</local-command-stderr>", StringComparison.Ordinal));
    }

    /// <summary>Direct text only. A command occupies the whole message, while an interruption may be one of
    /// several blocks.</summary>
    private static List<string> MessageTexts(JsonElement json, bool singleBlock = false)
    {
        var content = json.Obj("message").Field("content");
        if (content is { ValueKind: JsonValueKind.String } text) return [text.GetString() ?? ""];
        if (content is not { ValueKind: JsonValueKind.Array } blocks) return [];
        var items = blocks.EnumerateArray().ToList();
        // Upstream reads the blocks only when every one is an object.
        if (items.Any(b => b.ValueKind != JsonValueKind.Object) || (singleBlock && items.Count != 1)) return [];
        return items.Where(b => b.Str("type") == "text").Select(b => b.Str("text")).OfType<string>().ToList();
    }

    private static void Adopt(JsonElement json, AgentLogState state)
    {
        if (json.Str("sessionId") is { Length: > 0 } session) state.Session = session;
        if (json.Str("cwd") is { Length: > 0 } cwd) state.Project = AgentLogParser.ProjectName(cwd);
    }
}
