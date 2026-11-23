// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageStore.swift: the in-memory store of
// responses, limits and turns, with its turn rules (idle, waiting, late ends, Claude's offline grace and
// process records), plus the pure parts of AgentLogCursor (which turn a log's responses count toward) and the
// session registry's data. Reading files, process ids and the archive format are the app's job.
namespace Notchly.Core.Agents;

/// <summary>Everything read so far, one record per response. Not thread-safe: confine it to one queue.</summary>
public sealed class AgentUsageStore
{
    private readonly List<AgentUsageRecord> records = [];
    private readonly List<AgentBillable> billables = [];
    /// <summary>The logs each response was read from: most in one, a resumed or archived session's again in another.</summary>
    private readonly List<List<string>> sources = [];
    private readonly Dictionary<string, int> index = [];
    private readonly AgentUsageSummaryCache summary = new();
    private readonly Dictionary<AgentProvider, AgentLimits> limits = [];
    private DateTimeOffset codexPlanObserved = AgentTime.DistantPast;
    private readonly Dictionary<string, AgentLiveSession> turns = [];
    private readonly Dictionary<string, AgentLiveSession> waiting = [];
    /// <summary>Claude turns whose process was seen running, by log file.</summary>
    private readonly HashSet<string> registered = [];
    /// <summary>Turns whose last step ended expecting more, by log file, with when.</summary>
    private readonly Dictionary<string, DateTimeOffset> settled = [];
    /// <summary>When a model last replied, by the log of the turn it counts toward.</summary>
    private readonly Dictionary<string, DateTimeOffset> replies = [];

    /// <summary>A turn that ended longer ago than this (seconds) is history found late, like an archived session, not news.</summary>
    public const double LateEnd = 5 * 60;
    /// <summary>How long (seconds) the computer stays offline, awake, before Claude's turns end: after a short drop,
    /// as when it changes networks, Claude goes on.</summary>
    public const double OfflineGrace = 20;

    /// <summary>How long (seconds) a quiet turn waits for its work to resume before it is over: an hour for Claude,
    /// whose session resumed after its process was killed reads like work going on, six hours for the others.</summary>
    public static double ResumeWindow(AgentProvider provider) => provider == AgentProvider.Claude ? 3600 : 6 * 3600;

    public IReadOnlyList<AgentUsageRecord> Records => records;
    public IReadOnlyDictionary<AgentProvider, AgentLimits> Limits => limits;
    public string? CodexPlan { get; private set; }
    /// <summary>Open turns by log file.</summary>
    public IReadOnlyDictionary<string, AgentLiveSession> Turns => turns;
    /// <summary>Turns gone quiet, by log file: not shown as working, but work that resumes goes on with them.</summary>
    public IReadOnlyDictionary<string, AgentLiveSession> Waiting => waiting;
    /// <summary>Off while the logs are first read, so history never replays as news.</summary>
    public bool ReportsTransitions { get; set; }
    /// <summary>The publication cache, for its performance counters.</summary>
    public AgentUsageSummaryCache Summary => summary;

    public IReadOnlyList<AgentLiveSession> Live => [.. turns.Values];

    public AgentUsageSnapshot Snapshot(IReadOnlyDictionary<AgentProvider, AgentPlan> plans, IReadOnlySet<AgentProvider> providers,
                                       DateTimeOffset now, TimeZoneInfo? zone = null) =>
        summary.Snapshot(records, limits, Live, plans, providers, now, zone);

    /// <summary>Applies one file's entries and returns the turns they finished. <paramref name="parent"/> is the log
    /// whose turn a subagent's responses count toward.</summary>
    public IReadOnlyList<AgentUsageEvent> Apply(IEnumerable<AgentLogEntry> entries, string file, AgentProvider provider, bool tracksTurns,
                                                DateTimeOffset modified, DateTimeOffset now, string? parent = null)
    {
        var events = new List<AgentUsageEvent>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case AgentLogEntry.Usage usage:
                {
                    // Copilot exposes response activity and session-wide totals, neither of which measures the
                    // current root task.
                    var turn = provider == AgentProvider.Copilot ? null : tracksTurns ? file : parent;
                    Add(usage.Record, usage.Billable, usage.Key, file, turn, !tracksTurns);
                    break;
                }
                case AgentLogEntry.UsageModel named:
                {
                    if (!index.TryGetValue(named.Key, out var position) || records[position].Model.Length > 0) continue;
                    records[position] = records[position] with { Model = named.Model };
                    // Attribution changes rebuild model buckets from the records.
                    summary.Invalidate();
                    break;
                }
                case AgentLogEntry.Limits reading:
                    UpdateLimits(reading.Reading);
                    break;
                case AgentLogEntry.Plan plan:
                    // An archived session read again from its start holds an older plan than the one in use.
                    if (codexPlanObserved > plan.ObservedAt) continue;
                    CodexPlan = plan.Name;
                    codexPlanObserved = plan.ObservedAt;
                    break;
                case AgentLogEntry.TurnBegan began:
                {
                    if (!tracksTurns) continue;
                    settled.Remove(file);
                    // A log rewritten in place is read again from its start; the turn it already holds keeps what
                    // its responses added, which the second reading skips as repeats.
                    var existing = turns.GetValueOrDefault(file) ?? waiting.GetValueOrDefault(file);
                    if (existing is not null && Math.Abs(existing.Started.Since(began.Date)) < 1) continue;
                    waiting.Remove(file);
                    var last = turns.TryGetValue(file, out var open) ? open.LastActivity : began.Date;
                    turns[file] = new AgentLiveSession(file, provider, began.Date, AgentTime.Max(began.Date, last), "", "", default, 0);
                    break;
                }
                case AgentLogEntry.TurnContext context:
                {
                    if (!tracksTurns) continue;
                    var target = turns.ContainsKey(file) ? turns : waiting.ContainsKey(file) ? waiting : null;
                    if (target is null) continue;
                    var turn = target[file];
                    target[file] = turn with
                    {
                        Model = context.Model.Length > 0 ? context.Model : turn.Model,
                        Project = context.Project.Length > 0 ? context.Project : turn.Project,
                    };
                    break;
                }
                case AgentLogEntry.TurnActive active:
                {
                    if (!tracksTurns) continue;
                    settled.Remove(file);
                    var moment = active.Date ?? modified;
                    if (turns.TryGetValue(file, out var turn) || waiting.Remove(file, out turn))
                        turns[file] = turn with { LastActivity = AgentTime.Max(turn.LastActivity, moment) };
                    else
                        turns[file] = new AgentLiveSession(file, provider, moment, moment, "", "", default, 0);
                    break;
                }
                case AgentLogEntry.TurnSettled step:
                {
                    if (!tracksTurns) continue;
                    if (!turns.TryGetValue(file, out var turn) && !waiting.Remove(file, out turn)) continue;
                    turns[file] = turn with { LastActivity = AgentTime.Max(turn.LastActivity, step.Date) };
                    settled[file] = step.Date;
                    break;
                }
                case AgentLogEntry.TurnEnded ended:
                {
                    if (!tracksTurns) continue;
                    settled.Remove(file);
                    // A turn that went quiet on the way ends as the whole turn.
                    waiting.Remove(file, out var quiet);
                    var turn = turns.Remove(file, out var open) ? open : quiet;
                    if (turn is null || !ended.Completed || !ReportsTransitions) continue;
                    var end = ended.Date ?? modified;
                    if (now.Since(end) > LateEnd) continue;
                    events.Add(new AgentUsageEvent.Finished(provider, Math.Max(0, ended.Duration ?? end.Since(turn.Started)),
                                                            turn.Cost, turn.Tokens.Total, turn.Project));
                    break;
                }
                case AgentLogEntry.Reset:
                    Forget(file.Split('#')[0]);
                    break;
            }
        }
        return events;
    }

    /// <summary>A log removed while its turn ran, like a deleted chat, ends that turn without a notice: nothing
    /// finished. True when one was showing. OpenCode turns, kept as "database#session", go with their database.</summary>
    public bool Forget(string file)
    {
        waiting.Remove(file);
        foreach (var key in settled.Keys.Where(k => k == file || k.StartsWith(file + "#", StringComparison.Ordinal)).ToList()) settled.Remove(key);
        var removed = turns.Remove(file);
        foreach (var key in turns.Keys.Where(k => k.StartsWith(file + "#", StringComparison.Ordinal)).ToList())
        {
            turns.Remove(key);
            removed = true;
        }
        foreach (var key in waiting.Keys.Where(k => k.StartsWith(file + "#", StringComparison.Ordinal)).ToList()) waiting.Remove(key);
        return removed;
    }

    /// <summary><paramref name="source"/> is the log the response was read from; <paramref name="file"/> names the
    /// log whose turn it counts toward. A subagent's responses leave that turn's model and project alone.</summary>
    private void Add(AgentUsageRecord record, AgentBillable billable, string key, string source, string? file, bool subagent)
    {
        var delta = record.Tokens;
        var extra = record.Cost ?? 0;
        if (index.TryGetValue(key, out var position))
        {
            if (!sources[position].Contains(source)) sources[position].Add(source);
            var old = records[position];
            var merged = old.Tokens.Merged(record.Tokens);
            if (merged == old.Tokens)
            {
                if (record.Provider == AgentProvider.OpenCode)
                {
                    var (cost, reported) = OpenCodeCost(record, old, record.Cost ?? old.Cost);
                    if (cost != old.Cost || reported != old.ReportedCost)
                    {
                        summary.RecordChanged(position, old);
                        var change = (cost ?? 0) - (old.Cost ?? 0);
                        records[position] = old with { Cost = cost, ReportedCost = reported };
                        if (file is not null && (turns.GetValueOrDefault(file) ?? waiting.GetValueOrDefault(file)) is { } turn
                            && record.Date >= turn.Started.Plus(-1))
                        {
                            waiting.Remove(file);
                            turns[file] = turn with { Cost = turn.Cost + change };
                        }
                    }
                }
                return;
            }
            summary.RecordChanged(position, old);
            var combined = billables[position] with
            {
                Tokens = merged,
                LongCacheWrite = Math.Max(billables[position].LongCacheWrite, billable.LongCacheWrite),
                WebSearches = Math.Max(billables[position].WebSearches, billable.WebSearches),
                Fast = billables[position].Fast || billable.Fast,
                Domestic = billables[position].Domestic || billable.Domestic,
                IsAggregate = billables[position].IsAggregate || billable.IsAggregate,
            };
            var priced = AgentPricing.Cost(combined, old.Model);
            double? newCost;
            bool isReported;
            if (record.Provider == AgentProvider.OpenCode) (newCost, isReported) = OpenCodeCost(record, old, priced.Cost ?? record.Cost ?? old.Cost);
            else (newCost, isReported) = (priced.Cost, false);
            delta = merged - old.Tokens;
            extra = (newCost ?? 0) - (old.Cost ?? 0);
            billables[position] = combined;
            records[position] = old with { Tokens = merged, Cost = newCost, Savings = priced.Savings, ReportedCost = isReported };
        }
        else
        {
            summary.RecordChanged(records.Count, null);
            index[key] = records.Count;
            records.Add(record);
            billables.Add(billable);
            sources.Add([source]);
        }
        if (file is null) return;
        // Noted before the turn check, which skips a reply dated before its turn started: the model answered all the same.
        replies[file] = AgentTime.Max(replies.GetValueOrDefault(file, record.Date), record.Date);
        if ((turns.GetValueOrDefault(file) ?? waiting.GetValueOrDefault(file)) is not { } current
            || record.Date < current.Started.Plus(-1)) return;
        waiting.Remove(file);
        turns[file] = current with
        {
            Tokens = current.Tokens + delta,
            Cost = current.Cost + extra,
            LastActivity = AgentTime.Max(current.LastActivity, record.Date),
            Model = !subagent && record.Model.Length > 0 ? record.Model : current.Model,
            Project = !subagent && record.Project.Length > 0 ? record.Project : current.Project,
        };
    }

    /// <summary>A charge OpenCode reported stays, whichever reading brings it; otherwise <paramref name="estimate"/>.</summary>
    private static (double? Cost, bool Reported) OpenCodeCost(AgentUsageRecord record, AgentUsageRecord old, double? estimate)
    {
        if (record.ReportedCost) return (record.Cost, true);
        if (old.ReportedCost) return (old.Cost, true);
        return (estimate, false);
    }

    /// <summary>Prices every response again, after a newer list arrives. Reported provider charges stay intact.</summary>
    public void Reprice()
    {
        summary.Invalidate();
        for (var position = 0; position < records.Count; position++)
        {
            var record = records[position];
            if (record.ReportedCost) continue;
            var priced = AgentPricing.Cost(billables[position], record.Model);
            // A zero OpenCode recorded for a model the list still does not know stays that reply's cost.
            var recordedZero = record.Provider == AgentProvider.OpenCode && record.Cost == 0;
            records[position] = record with { Cost = priced.Cost ?? (recordedZero ? 0 : null), Savings = priced.Savings };
        }
    }

    public void SetLimits(AgentLimits reading) => limits[reading.Provider] = reading;

    /// <summary>Keeps the newer of two readings of an account, whichever way each one arrived.</summary>
    public void UpdateLimits(AgentLimits reading)
    {
        if ((limits.TryGetValue(reading.Provider, out var current) ? current.ObservedAt : AgentTime.DistantPast) <= reading.ObservedAt)
            limits[reading.Provider] = reading;
    }

    public void ClearLimits(AgentProvider provider) => limits.Remove(provider);

    /// <summary>A turn that has written nothing for <paramref name="idle"/> seconds is not being worked on. It waits
    /// aside for <see cref="ResumeWindow"/>, since work can resume after an approval or a long command.</summary>
    public void CloseIdleTurns(DateTimeOffset now, double idle)
    {
        foreach (var (file, turn) in turns.ToList())
        {
            if (now.Since(turn.LastActivity) < idle) continue;
            turns.Remove(file);
            waiting[file] = turn;
        }
        foreach (var (file, turn) in waiting.ToList())
            if (now.Since(turn.LastActivity) >= ResumeWindow(turn.Provider)) waiting.Remove(file);
        foreach (var file in replies.Keys.ToList())
            if (!turns.ContainsKey(file) && !waiting.ContainsKey(file)) replies.Remove(file);
    }

    /// <summary>A turn whose last step ended expecting more, with nothing after it for <see cref="AgentLogParser.OpenCodeSettle"/>
    /// seconds, stopped there: it ends without a notice. True when one was showing.</summary>
    public bool CloseSettledTurns(DateTimeOffset now)
    {
        var removed = false;
        foreach (var (file, date) in settled.ToList())
        {
            if (now.Since(date) < AgentLogParser.OpenCodeSettle) continue;
            settled.Remove(file);
            waiting.Remove(file);
            if (turns.Remove(file)) removed = true;
        }
        return removed;
    }

    /// <summary>
    /// A Claude session quit or killed in the middle of a turn writes nothing that ends it. Its process record says
    /// so sooner than the quiet wait: the record names a process that no longer runs, or a record seen running is
    /// gone. At launch, a turn the logs left open without a record is over too, once the records could be read.
    /// True when a turn was showing.
    /// </summary>
    public bool CloseEndedTurns(AgentSessionRegistry processes, bool atLaunch = false)
    {
        registered.IntersectWith(turns.Keys);
        var closed = false;
        foreach (var (file, turn) in turns.ToList())
        {
            if (turn.Provider != AgentProvider.Claude) continue;
            var session = AgentLogRouting.SessionId(file);
            if (processes.Running.Contains(session))
            {
                registered.Add(file);
            }
            else if (processes.Ended.Contains(session)
                     || (processes.Complete && (registered.Contains(file) || (atLaunch && processes.Listed))))
            {
                registered.Remove(file);
                closed = Forget(file) || closed;
            }
        }
        return closed;
    }

    /// <summary>
    /// Claude Code retries for minutes without a word while the computer is offline. Once offline since
    /// <paramref name="offline"/> for <paramref name="elapsed"/> seconds awake (at least <see cref="OfflineGrace"/>),
    /// Claude's turns end without a notice, quiet ones too, so a retry that gets through opens a turn of its own. A
    /// turn whose model replied since then runs on a local model, and a turn in <paramref name="running"/> (logs with
    /// a shell command still running) waits on a command that runs here. Other agents are left alone.
    /// </summary>
    public bool CloseOfflineTurns(DateTimeOffset offline, double elapsed, IReadOnlySet<string>? running = null)
    {
        if (elapsed < OfflineGrace) return false;
        var commands = running ?? new HashSet<string>();
        bool Ends(string file, AgentLiveSession turn) =>
            turn.Provider == AgentProvider.Claude && !commands.Contains(file) && replies.GetValueOrDefault(file, AgentTime.DistantPast) < offline;
        var closed = turns.Any(p => Ends(p.Key, p.Value));
        foreach (var (file, turn) in turns.ToList()) if (Ends(file, turn)) turns.Remove(file);
        foreach (var (file, turn) in waiting.ToList()) if (Ends(file, turn)) waiting.Remove(file);
        foreach (var file in settled.Keys.ToList())
            if (!turns.ContainsKey(file) && !waiting.ContainsKey(file)) settled.Remove(file);
        return closed;
    }

    public bool ShowsClaudeTurn => turns.Values.Any(t => t.Provider == AgentProvider.Claude);

    /// <summary>What is kept between launches: every counter the logs gave, and none of their text. OpenCode's
    /// database is read again at each launch, so nothing it gave is kept.</summary>
    public sealed record Saved(IReadOnlyList<SavedRecord> Records, IReadOnlyList<AgentLimits> Limits, string? CodexPlan,
                               DateTimeOffset CodexPlanObserved, IReadOnlyList<AgentLiveSession> Turns, IReadOnlyList<AgentLiveSession> Waiting);

    public sealed record SavedRecord(string Key, AgentUsageRecord Record, AgentBillable Billable, IReadOnlyList<string> Sources);

    public Saved Save()
    {
        var keys = new string[records.Count];
        foreach (var (key, position) in index) keys[position] = key;
        var kept = Enumerable.Range(0, records.Count).Where(i => records[i].Provider != AgentProvider.OpenCode)
            .Select(i => new SavedRecord(keys[i] ?? "", records[i], billables[i], [.. sources[i]])).ToList();
        return new Saved(kept, limits.Values.OrderBy(l => l.Provider.RawValue(), StringComparer.Ordinal).ToList(), CodexPlan, codexPlanObserved,
                         turns.Values.Where(t => t.Provider != AgentProvider.OpenCode).OrderBy(t => t.Id, StringComparer.Ordinal).ToList(),
                         waiting.Values.Where(t => t.Provider != AgentProvider.OpenCode).OrderBy(t => t.Id, StringComparer.Ordinal).ToList());
    }

    public static AgentUsageStore Restore(Saved saved)
    {
        var store = new AgentUsageStore();
        foreach (var entry in saved.Records)
        {
            store.index[entry.Key] = store.records.Count;
            store.records.Add(entry.Record);
            store.billables.Add(entry.Billable);
            store.sources.Add([.. entry.Sources]);
        }
        foreach (var reading in saved.Limits) store.limits[reading.Provider] = reading;
        store.CodexPlan = saved.CodexPlan;
        store.codexPlanObserved = saved.CodexPlanObserved;
        foreach (var turn in saved.Turns) store.turns[turn.Id] = turn;
        foreach (var turn in saved.Waiting) store.waiting[turn.Id] = turn;
        return store;
    }

    /// <summary>Keeps memory bounded to the history the island can show.</summary>
    public void DropRecords(DateTimeOffset before)
    {
        if (!records.Any(r => r.Date < before)) return;
        KeepRecords(i => records[i].Date >= before);
    }

    /// <summary>Takes back what logs gone or rewritten since they were read gave, as reading every log from its start
    /// would: a response only they held goes, one another log also holds stays. Their turns end without a notice.</summary>
    public void Forget(IReadOnlySet<string> files)
    {
        foreach (var file in files) Forget(file);
        var emptied = false;
        foreach (var list in sources)
        {
            if (!list.Any(files.Contains)) continue;
            list.RemoveAll(files.Contains);
            emptied = emptied || list.Count == 0;
        }
        if (emptied) KeepRecords(i => sources[i].Count > 0);
    }

    /// <summary>The logs holding a response that one of <paramref name="files"/> also holds.</summary>
    public IReadOnlySet<string> FilesSharing(IReadOnlySet<string> files)
    {
        var sharing = new HashSet<string>();
        foreach (var list in sources) if (list.Any(files.Contains)) sharing.UnionWith(list);
        return sharing;
    }

    /// <summary>Every log that gave a response or holds a turn.</summary>
    public IReadOnlySet<string> Files => sources.SelectMany(s => s).Concat(turns.Keys).Concat(waiting.Keys).ToHashSet();

    private void KeepRecords(Func<int, bool> keep)
    {
        summary.Invalidate();
        var positions = new Dictionary<int, int>();
        var kept = new List<AgentUsageRecord>();
        var keptBillables = new List<AgentBillable>();
        var keptSources = new List<List<string>>();
        for (var offset = 0; offset < records.Count; offset++)
        {
            if (!keep(offset)) continue;
            positions[offset] = kept.Count;
            kept.Add(records[offset]);
            keptBillables.Add(billables[offset]);
            keptSources.Add(sources[offset]);
        }
        foreach (var (key, position) in index.ToList())
        {
            if (positions.TryGetValue(position, out var moved)) index[key] = moved; else index.Remove(key);
        }
        records.Clear();
        records.AddRange(kept);
        billables.Clear();
        billables.AddRange(keptBillables);
        sources.Clear();
        sources.AddRange(keptSources);
    }
}

/// <summary>
/// The Claude sessions with a process, from the <c>sessions/&lt;pid&gt;.json</c> records Claude Code keeps beside its
/// logs while it runs. Reading the folder and checking process ids is the app's job; this is what it found.
/// </summary>
public sealed record AgentSessionRegistry
{
    /// <summary>Session ids, which name their log files, with a running process.</summary>
    public IReadOnlySet<string> Running { get; init; } = new HashSet<string>();
    /// <summary>Session ids whose recorded process no longer runs.</summary>
    public IReadOnlySet<string> Ended { get; init; } = new HashSet<string>();
    /// <summary>False when a record could not be read, as while it is being written, so a missing session proves nothing.</summary>
    public bool Complete { get; init; } = true;
    /// <summary>A sessions folder could be listed, so this Claude Code keeps records.</summary>
    public bool Listed { get; init; }

    /// <summary>
    /// The registry from the records read: <paramref name="records"/> holds each <c>&lt;pid&gt;.json</c> file's text, or
    /// null when it could not be read. A record naming a process domain other than <paramref name="domain"/> (as one
    /// from a container sharing the folder) proves nothing.
    /// </summary>
    public static AgentSessionRegistry FromRecords(IEnumerable<string?> records, Func<int, bool> isRunning, bool listed, string domain = "windows")
    {
        var running = new HashSet<string>();
        var ended = new HashSet<string>();
        var complete = true;
        foreach (var text in records)
        {
            using var document = text is null || text.Length >= 1 << 16 ? null : AgentLogParser.Object(text);
            if (document is null || document.RootElement.Num("pid") is not { } pid || pid != Math.Floor(pid)
                || document.RootElement.Str("sessionId") is not { Length: > 0 } session)
            {
                complete = false;
                continue;
            }
            if (document.RootElement.Str("pidDomain") is { } named && named != domain) continue;
            if (isRunning((int)Math.Clamp(pid, int.MinValue, int.MaxValue))) running.Add(session); else ended.Add(session);
        }
        // A session resumed by a new process after an old one was killed.
        ended.ExceptWith(running);
        return new AgentSessionRegistry { Running = running, Ended = ended, Complete = complete, Listed = listed };
    }

    public bool Equals(AgentSessionRegistry? other) =>
        other is not null && Running.SetEquals(other.Running) && Ended.SetEquals(other.Ended) && Complete == other.Complete && Listed == other.Listed;

    public override int GetHashCode() => HashCode.Combine(Running.Count, Ended.Count, Complete, Listed);
}

/// <summary>Which turn a log's responses count toward, as upstream's AgentLogCursor and AgentUsageService.read decide.</summary>
public static class AgentLogRouting
{
    /// <summary>Claude Code keeps a session's subagents in <c>&lt;session&gt;/subagents/</c>, beside the session's own
    /// <c>&lt;session&gt;.jsonl</c>: the session log a subagent works for, or null.</summary>
    public static string? ClaudeParent(string path)
    {
        var slash = path.LastIndexOf("/subagents/", StringComparison.Ordinal);
        var backslash = path.LastIndexOf("\\subagents\\", StringComparison.Ordinal);
        var at = Math.Max(slash, backslash);
        return at < 0 ? null : path[..at] + ".jsonl";
    }

    /// <summary>Whether a log owns its turns: a Claude subagent's and a Codex subagent's report to another one.</summary>
    public static bool TracksTurns(AgentProvider provider, string path, AgentLogState state) => provider switch
    {
        AgentProvider.Claude => ClaudeParent(path) is null,
        AgentProvider.Codex => !state.CodexSubagent,
        _ => true,
    };

    /// <summary>
    /// The arguments for <see cref="AgentUsageStore.Apply"/> after a line of <paramref name="path"/> was parsed: the
    /// file the turn is kept under (an OpenCode database's sessions each as "path#session"), whether it tracks turns,
    /// and the parent turn a subagent's responses count toward. <paramref name="codexParent"/> is the Codex parent
    /// log found by <see cref="CodexParentLog"/>.
    /// </summary>
    public static (string File, bool TracksTurns, string? Parent) Route(AgentProvider provider, string path, AgentLogState state,
                                                                        string? codexParent = null)
    {
        var subagent = provider == AgentProvider.OpenCode && state.ParentSession.Length > 0;
        var file = provider == AgentProvider.OpenCode && state.Session.Length > 0 ? path + "#" + state.Session : path;
        var tracks = !subagent && TracksTurns(provider, path, state);
        var parent = subagent ? path + "#" + state.ParentSession
            : provider == AgentProvider.Claude ? ClaudeParent(path) : provider == AgentProvider.Codex && !tracks ? codexParent : null;
        return (file, tracks, parent);
    }

    /// <summary>The log of the thread a Codex subagent works for: among the Codex logs that track turns, the one
    /// written last whose session is <paramref name="root"/>.</summary>
    public static string? CodexParentLog(string root, IEnumerable<(string Path, bool TracksTurns, string Session, DateTimeOffset Modified)> logs) =>
        root.Length == 0 ? null
            : logs.Where(l => l.TracksTurns && l.Session == root).OrderBy(l => l.Modified).Select(l => l.Path).LastOrDefault();

    /// <summary>The session id a Claude log is named after: its file name without the extension.</summary>
    public static string SessionId(string file)
    {
        var name = file[(file.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
