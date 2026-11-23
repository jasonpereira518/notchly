// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/Harness/OpenCodeReader.swift, plus the pure half of
// AgentOpenCodeReader.swift: the query that takes only the values the parser reads out of opencode.db, and
// how one row of it becomes the small JSON line the reader parses. Opening SQLite is the app's job.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notchly.Core.Agents;

/// <summary>
/// Reads OpenCode database rows as <see cref="OpenCodeRows.Line"/> hands them over. Every session keeps its own
/// turn and model tracking inside the shared state, and a row that arrives after its session went quiet starts a
/// task of its own.
/// </summary>
public sealed class OpenCodeReader : IAgentHarnessReader
{
    public AgentProvider Provider => AgentProvider.OpenCode;
    public bool ReadsFullHistoryFirst => false;

    public IReadOnlyList<AgentLogEntry> Parse(ReadOnlyMemory<byte> line, AgentLogState state, DateTimeOffset now)
    {
        if (!AgentLogParser.Contains(line.Span, "\"role\":\""u8) && !AgentLogParser.Contains(line.Span, "\"type\":\"reset\""u8)) return [];
        using var document = AgentLogParser.Object(line);
        if (document is null) return [];
        var json = document.RootElement;
        if (json.Str("type") == "reset")
        {
            state.OpenCodeSessions.Clear();
            state.TurnOpen = false;
            return [new AgentLogEntry.Reset(now)];
        }
        if (json.Str("role") is not { } role) return [];
        var id = json.Str("id") ?? "";
        var session = json.Str("session_id") ?? "";
        var sessionId = session.Length == 0 ? state.Session : session;
        state.Session = sessionId;
        state.ParentSession = json.Str("parent_session_id") ?? "";

        // A copy, written back only where upstream writes its value type back.
        var sessionState = state.OpenCodeSessions.TryGetValue(sessionId, out var known) ? known.Clone() : new OpenCodeSessionState();

        var cwd = json.Obj("path").Str("cwd") ?? json.Str("directory") ?? "";
        if (cwd.Length > 0) sessionState.Project = AgentLogParser.ProjectName(cwd);

        var timeCreated = json.Obj("time") is { } time && time.Has("created") ? time.Field("created") : json.Field("time_created");
        var date = AgentLogParser.Seconds(timeCreated) ?? now;
        // A session quiet this long with no reply being written starts its next prompt as a task of its own, so
        // what it kept to tell its rows apart can go. Sessions are looked over only as a new one appears.
        if (known is null)
        {
            foreach (var pair in state.OpenCodeSessions.ToList())
            {
                var kept = pair.Value;
                if (kept.WritingReplyId.Length == 0 && date.Since(kept.LastActivity ?? date) >= NotchAgentSupport.IdleTurn)
                    state.OpenCodeSessions.Remove(pair.Key);
            }
        }
        var lastActivity = sessionState.LastActivity;
        sessionState.LastActivity = AgentTime.Max(lastActivity ?? date, date);

        switch (role)
        {
            case "user":
            {
                if (id.Length > 0)
                {
                    // A prompt read again, as when its summary is saved, is no new one.
                    if (sessionState.SeenUserMessageIds.Contains(id)) return [];
                    sessionState.SeenUserMessageIds.Remember(id);
                    sessionState.ActiveUserMessageId = id;
                }
                // OpenCode writes prompts of its own in the middle of a task, and a prompt sent while it works
                // joins the running loop too, so inside a turn each is activity. A prompt that arrives after the
                // session went quiet is a task of its own.
                var entries = new List<AgentLogEntry>();
                bool quiet;
                if (sessionState.SettledAt is { } settled) quiet = date.Since(settled) >= AgentLogParser.OpenCodeSettle;
                else if (sessionState.WritingReplyId.Length == 0 && lastActivity is { } last) quiet = date.Since(last) >= NotchAgentSupport.IdleTurn;
                else quiet = false;
                sessionState.SettledAt = null;
                sessionState.ActivePromptDate = date;
                if (sessionState.TurnOpen && !quiet)
                {
                    entries.Add(new AgentLogEntry.TurnActive(date));
                }
                else
                {
                    if (sessionState.TurnOpen) entries.Add(new AgentLogEntry.TurnEnded(lastActivity, false, null));
                    sessionState.TurnOpen = true;
                    sessionState.TurnStarted = date;
                    entries.Add(new AgentLogEntry.TurnBegan(date));
                }
                if (ModelOf(json) is { Length: > 0 } userModel) sessionState.Model = userModel;
                state.OpenCodeSessions[sessionId] = sessionState;
                state.Project = sessionState.Project;
                state.Model = sessionState.Model;
                state.TurnOpen = true;
                return entries;
            }
            case "assistant":
            {
                if (ModelOf(json) is { Length: > 0 } rawModel) sessionState.Model = rawModel;
                var parentId = json.Str("parentID") ?? "";
                var isAlreadyCompleted = (id.Length > 0 && sessionState.CompletedAssistantMessageIds.Contains(id))
                    || (parentId.Length > 0 && sessionState.CompletedUserMessageIds.Contains(parentId));
                var matchesActivePrompt = parentId.Length == 0 || parentId == sessionState.ActiveUserMessageId;
                var completed = AgentLogParser.Seconds(json.Obj("time").Field("completed"));
                var hasError = json.Field("error") is { } error && error.ValueKind != JsonValueKind.Null;
                var finish = json.Str("finish");
                if (completed is { } done) sessionState.LastActivity = AgentTime.Max(sessionState.LastActivity ?? done, done);
                // A step that starts after the last one settled is the loop going on.
                if (sessionState.SettledAt is { } settledAt && date >= settledAt) sessionState.SettledAt = null;

                var entries = new List<AgentLogEntry>();
                // OpenCode completes each reply before it answers a prompt sent meanwhile. A reply to a newer prompt
                // while an older one never completed means OpenCode stopped in the middle of it, and the newer
                // prompt started a task of its own.
                if (id.Length > 0 && sessionState.WritingReplyId.Length > 0 && sessionState.WritingReplyId != id
                    && sessionState.TurnOpen && parentId.Length > 0 && parentId == sessionState.ActiveUserMessageId
                    && sessionState.ActivePromptDate is { } prompt && sessionState.TurnStarted is { } started && prompt > started)
                {
                    entries.Add(new AgentLogEntry.TurnEnded(null, false, null));
                    entries.Add(new AgentLogEntry.TurnBegan(prompt));
                    sessionState.TurnStarted = prompt;
                }
                if (completed is null && finish is null && !hasError && id.Length > 0) sessionState.WritingReplyId = id;
                else if (sessionState.WritingReplyId == id) sessionState.WritingReplyId = "";
                if (!isAlreadyCompleted && matchesActivePrompt && !sessionState.TurnOpen)
                {
                    sessionState.TurnOpen = true;
                    sessionState.TurnStarted = date;
                    entries.Add(new AgentLogEntry.TurnBegan(date));
                }
                if (Usage(json, id, sessionId, date, sessionState.Model, sessionState.Project) is { } usage) entries.Add(usage);

                // OpenCode's loop goes on after tool calls, after a reply that still holds calls whatever its
                // finish says, and after the summary of a compaction it started on its own. Any other finish stops
                // it, as does an error or a reply that completes without a finish, like a shell command's.
                bool ends;
                if (hasError) ends = true;
                else if (finish is not null)
                    ends = finish is not ("tool-calls" or "unknown") && json.Bool("tool_calls") != true && json.Bool("auto_compaction") != true;
                else ends = completed is not null;

                if (ends)
                {
                    if (!isAlreadyCompleted && matchesActivePrompt && sessionState.TurnOpen)
                    {
                        var endDate = completed ?? AgentLogParser.Seconds(json.Field("time_updated")) ?? date;
                        double? duration = null;
                        if (sessionState.TurnStarted is { } turnStarted)
                        {
                            if (endDate >= turnStarted) duration = endDate.Since(turnStarted);
                        }
                        else if (endDate >= date)
                        {
                            duration = endDate.Since(date);
                        }
                        sessionState.TurnOpen = false;
                        sessionState.TurnStarted = null;
                        sessionState.ActiveUserMessageId = "";
                        sessionState.SettledAt = null;
                        entries.Add(new AgentLogEntry.TurnEnded(endDate, !hasError, duration));
                    }
                    if (id.Length > 0) sessionState.CompletedAssistantMessageIds.Remember(id);
                    if (parentId.Length > 0) sessionState.CompletedUserMessageIds.Remember(parentId);
                }
                else if (!isAlreadyCompleted && matchesActivePrompt)
                {
                    // A step that completed hands over to the next one at once, unless the loop stopped there.
                    // OpenCode 2 saves the next step only once the model starts to answer, and marks where its loop
                    // stopped with a row of its own instead.
                    if (completed is { } stepDone && sessionState.TurnOpen)
                    {
                        if (json.Num("version") == 2)
                        {
                            entries.Add(new AgentLogEntry.TurnActive(stepDone));
                        }
                        else
                        {
                            sessionState.SettledAt = stepDone;
                            entries.Add(new AgentLogEntry.TurnSettled(stepDone));
                        }
                    }
                    else
                    {
                        entries.Add(new AgentLogEntry.TurnActive(date));
                    }
                }
                state.OpenCodeSessions[sessionId] = sessionState;
                state.Project = sessionState.Project;
                state.Model = sessionState.Model;
                state.TurnOpen = sessionState.TurnOpen;
                return entries;
            }
            case "compaction":
            {
                // OpenCode 2 compacts in a row of its own, saved as it starts and again with what its request cost
                // once it completes or fails. The turn it runs in goes on meanwhile.
                var ownModel = json.Obj("model").Str("id") is { Length: > 0 } own ? own : null;
                var entries = new List<AgentLogEntry>();
                if (Usage(json, id, sessionId, date, ownModel ?? sessionState.Model, sessionState.Project) is { } usage) entries.Add(usage);
                if (sessionState.TurnOpen)
                {
                    var moment = AgentTime.Max(date, AgentLogParser.Seconds(json.Field("time_updated")) ?? date);
                    sessionState.LastActivity = AgentTime.Max(sessionState.LastActivity ?? moment, moment);
                    sessionState.SettledAt = null;
                    entries.Add(new AgentLogEntry.TurnActive(moment));
                }
                state.OpenCodeSessions[sessionId] = sessionState;
                state.Project = sessionState.Project;
                state.Model = sessionState.Model;
                state.TurnOpen = sessionState.TurnOpen;
                return entries;
            }
            case "idle":
            {
                // OpenCode 2 marks where each turn ended and how, unless it quit in the middle of one.
                sessionState.WritingReplyId = "";
                sessionState.SettledAt = null;
                var entries = new List<AgentLogEntry>();
                if (sessionState.TurnOpen)
                {
                    double? duration = null;
                    if (sessionState.TurnStarted is { } turnStarted && date >= turnStarted) duration = date.Since(turnStarted);
                    sessionState.TurnOpen = false;
                    sessionState.TurnStarted = null;
                    sessionState.ActiveUserMessageId = "";
                    entries.Add(new AgentLogEntry.TurnEnded(date, json.Str("outcome") == "succeeded", duration));
                }
                state.OpenCodeSessions[sessionId] = sessionState;
                state.TurnOpen = false;
                return entries;
            }
            default:
                return [];
        }
    }

    private static string? ModelOf(JsonElement json) =>
        json.Str("model_id") ?? json.Str("modelID") ?? json.Obj("model").Str("modelID") ?? json.Obj("model").Str("id") ?? json.Str("model");

    /// <summary>What an OpenCode reply or compaction used, priced from the list when it knows the model; null while
    /// it holds no usage. A model the list does not know costs what OpenCode recorded; a recorded zero stays an
    /// estimate, so a price the list learns later still applies.</summary>
    private static AgentLogEntry? Usage(JsonElement json, string id, string session, DateTimeOffset date, string model, string project)
    {
        var tokensField = json.Obj("tokens");
        var reasoning = AgentLogParser.Int(tokensField.Field("reasoning"));
        var cache = tokensField.Obj("cache");
        var tokens = new AgentTokens(AgentLogParser.Int(tokensField.Field("input")), AgentLogParser.Int(cache.Field("write")),
                                     AgentLogParser.Int(cache.Field("read")), AgentLogParser.Int(tokensField.Field("output")) + reasoning, reasoning);
        var recordedCost = json.Num("cost");
        if (tokens.Total <= 0 && (recordedCost ?? 0) <= 0) return null;
        var billable = new AgentBillable(tokens);
        var priced = AgentPricing.Cost(billable, model);
        double? cost;
        bool reported;
        if (priced.Cost is { } calculated) { cost = calculated; reported = false; }
        else if (recordedCost is { } recorded) { cost = recorded; reported = recorded > 0; }
        else { cost = null; reported = false; }
        var key = $"opencode:{session}:{(id.Length == 0 ? AgentTime.KeyText(date) : id)}";
        return new AgentLogEntry.Usage(key, new AgentUsageRecord(AgentProvider.OpenCode, date, model, project, session, tokens, cost,
                                                                 priced.Savings, reportedCost: reported), billable);
    }
}

/// <summary>One row of <see cref="OpenCodeRows.Select"/>, column for column.</summary>
/// <param name="Values">Column 8: the JSON array of the role and <see cref="OpenCodeRows.Values"/>, or null.</param>
/// <param name="ToolCalls">Column 9: a reply that still holds tool calls its loop goes on with.</param>
/// <param name="AutoCompaction">Column 10: the summary of a compaction OpenCode started on its own.</param>
/// <param name="Writing">Column 11: a reply or compaction still being written, to look at again.</param>
/// <param name="ErrorType">Column 12: json_type of $.error, or null.</param>
public sealed record OpenCodeRow(long RowId, string? Id, string? SessionId, long? TimeCreated, long? TimeUpdated, long? Length,
                                 string? Directory, string? ParentSessionId, string? Values, bool ToolCalls, bool AutoCompaction,
                                 bool Writing, string? ErrorType);

/// <summary>
/// The SQL that reads OpenCode's messages and how each row becomes a line for <see cref="OpenCodeReader"/>. Only
/// the values the parser reads leave the database: prompts, replies, summaries and error text never do.
/// </summary>
public static class OpenCodeRows
{
    /// <summary>The one database OpenCode keeps in its data folder.</summary>
    public const string Database = "opencode.db";
    /// <summary>Rows read last, newest last, to notice when the newest were deleted.</summary>
    public const int TailLength = 64;
    /// <summary>A reply that has not changed for this long (seconds) is not being written any more.</summary>
    public const double Abandoned = 86_400;

    /// <summary>The values the parser reads from a message, under the names it reads them by.</summary>
    public static readonly IReadOnlyList<(string Name, string Path)> Values =
    [
        ("role", "$.role"), ("parentID", "$.parentID"), ("modelID", "$.modelID"), ("model_id", "$.model_id"),
        ("model", "$.model"), ("tokens", "$.tokens"), ("cost", "$.cost"), ("finish", "$.finish"),
        ("time", "$.time"), ("cwd", "$.path.cwd"), ("status", "$.status"), ("outcome", "$.outcome"),
    ];

    /// <summary>OpenCode 1 keeps a session_message table of its own since 1.15, so only session_v2 tells 2 apart.</summary>
    public const string Version2Probe = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_v2'";
    public const string PartsProbe = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'part'";
    public static string ParentProbe(bool version2) =>
        $"SELECT 1 FROM pragma_table_info('{SessionTable(version2)}') WHERE name = 'parent_id'";
    public static string MessageTable(bool version2) => version2 ? "session_message" : "message";
    public static string SessionTable(bool version2) => version2 ? "session_v2" : "session";

    /// <summary>A JSON value that reads as null, instead of failing the whole query, when a row holds something
    /// other than JSON.</summary>
    private static string Field(string column, string path) =>
        $"(CASE WHEN json_valid({column}) THEN json_extract({column}, '{path}') END)";

    /// <summary>
    /// The SELECT every read runs, ending before its WHERE clause; add <c>WHERE m.rowid &gt; ? ORDER BY m.rowid</c>
    /// for new rows, or <c>WHERE m.id IN (…) ORDER BY m.rowid</c> for replies still being written.
    /// <paramref name="floorMilliseconds"/> passes older OpenCode 2 rows by their times alone.
    /// </summary>
    public static string Select(bool version2, bool hasParts, bool hasParentId, long floorMilliseconds = 0)
    {
        var recent = version2 && floorMilliseconds > 0 ? $"m.time_created >= {floorMilliseconds} AND " : "";
        var role = version2 ? "m.type" : Field("m.data", "$.role");
        var toolCalls = version2
            ? $"""
              CASE WHEN {recent}{role} = 'assistant'
                  AND {Field("m.data", "$.finish")} NOT IN ('tool-calls', 'unknown')
              THEN EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(m.data)
                  THEN json_extract(m.data, '$.content') ELSE '[]' END) c
                  WHERE json_extract(c.value, '$.type') = 'tool'
                      AND coalesce(json_extract(c.value, '$.executed'), 0) = 0)
              ELSE 0 END
              """
            : hasParts
                ? $"""
                  CASE WHEN {Field("m.data", "$.role")} = 'assistant'
                      AND {Field("m.data", "$.finish")} NOT IN ('tool-calls', 'unknown')
                  THEN EXISTS (SELECT 1 FROM part p WHERE p.message_id = m.id
                      AND {Field("p.data", "$.type")} = 'tool'
                      AND coalesce({Field("p.data", "$.metadata.providerExecuted")}, 0) = 0
                      AND NOT (coalesce({Field("p.data", "$.state.status")}, '') = 'error'
                          AND coalesce({Field("p.data", "$.state.metadata.interrupted")}, 0) = 1))
                  ELSE 0 END
                  """
                : "0";
        var autoCompaction = !version2 && hasParts
            ? $"""
              CASE WHEN {Field("m.data", "$.role")} = 'assistant' AND {Field("m.data", "$.summary")} = 1
              THEN EXISTS (SELECT 1 FROM part p WHERE p.message_id = {Field("m.data", "$.parentID")}
                  AND {Field("p.data", "$.type")} = 'compaction' AND {Field("p.data", "$.auto")} = 1)
              ELSE 0 END
              """
            : "0";
        var writing = $"""
            ({recent}(coalesce({role} = 'assistant'
                AND {Field("m.data", "$.time.completed")} IS NULL, 0)
            OR coalesce({role} = 'compaction' AND {Field("m.data", "$.status")} = 'running', 0)))
            """;
        var parentColumn = hasParentId ? "s.parent_id" : "NULL";
        var fields = string.Join(", ", Values.Select(v => $"json_extract(m.data, '{v.Path}')"));
        var size = recent.Length == 0 ? "length(m.data)" : $"CASE WHEN {recent}1 THEN length(m.data) END";
        return $"""
            SELECT m.rowid, m.id, m.session_id, m.time_created, m.time_updated, {size}, s.directory,
                {parentColumn}, CASE WHEN {recent}json_valid(m.data) THEN json_array({role}, {fields}) END,
                {toolCalls}, {autoCompaction}, {writing},
                CASE WHEN {recent}json_valid(m.data) THEN json_type(m.data, '$.error') END
            FROM {MessageTable(version2)} m
            JOIN {SessionTable(version2)} s ON m.session_id = s.id
            """;
    }

    /// <summary>The line the reader parses for <paramref name="row"/>; null when a column a later version could
    /// leave empty is empty, or the row's data cannot be read, or an OpenCode 2 step was cut short.</summary>
    public static byte[]? Line(OpenCodeRow row, bool version2)
    {
        if (row.Id is null || row.SessionId is null || row.TimeCreated is not { } created) return null;
        var updated = row.TimeUpdated ?? created;
        if (row.Values is null) return null;
        JsonArray found;
        try
        {
            if (JsonNode.Parse(row.Values) is not JsonArray array || array.Count != Values.Count + 1) return null;
            found = array;
        }
        catch (JsonException) { return null; }
        var json = new JsonObject();
        if (found[0] is not null) json["role"] = found[0]!.DeepClone();
        for (var i = 0; i < Values.Count; i++)
        {
            if (found[i + 1] is { } value) json[Values[i].Name] = value.DeepClone();
        }
        if (json["cwd"] is { } cwd)
        {
            json.Remove("cwd");
            json["path"] = new JsonObject { ["cwd"] = cwd };
        }
        json["id"] = row.Id;
        json["session_id"] = row.SessionId;
        json["parent_session_id"] = row.ParentSessionId ?? "";
        json["directory"] = row.Directory ?? "";
        json["time_created"] = created;
        json["time_updated"] = updated;
        if (row.ToolCalls) json["tool_calls"] = true;
        if (row.AutoCompaction) json["auto_compaction"] = true;
        if (row.ErrorType is { } error && error != "null") json["error"] = true;
        // OpenCode 2 completes a step it cut short only as the next step starts. Without a finish, an error or
        // usage, it neither ends nor starts a turn.
        if (version2 && json["role"]?.GetValueKind() == JsonValueKind.String && (string?)json["role"] == "assistant"
            && json["finish"] is null && json["error"] is null && json["tokens"] is null
            && json["time"] is JsonObject time && time["completed"] is not null) return null;
        // OpenCode 2 marks where its loop stopped, so the parser does not wait out the gap before a late step.
        if (version2) json["version"] = 2;
        return Encoding.UTF8.GetBytes(json.ToJsonString(Relaxed));
    }

    /// <summary>The line that tells the reader to forget what an earlier reading left open, as after the
    /// database was replaced or a revert reused its rowids.</summary>
    public static byte[] ResetLine => "{\"type\":\"reset\"}"u8.ToArray();

    /// <summary>Unix milliseconds for the floor a first read starts from.</summary>
    public static long Floor(DateTimeOffset horizon) =>
        (long)(Math.Max(0, AgentTime.ToUnixSeconds(horizon)) * 1000);

    /// <summary>Text stays as written, so the reader's byte filters see the same keys and values.</summary>
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
