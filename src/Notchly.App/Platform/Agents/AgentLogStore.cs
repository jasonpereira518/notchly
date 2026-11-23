// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the reading half of upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageService.swift and the I/O
// of AgentUsageStore.swift/AgentUsageArchive.swift: reads Claude Code, Codex and GitHub Copilot session logs and
// OpenCode's database where they are, incrementally, on one private thread, and keeps only counters. Windows uses a
// FileSystemWatcher per root where upstream uses FSEvents, NetworkChange for NWPathMonitor, and keeps its progress
// archive in %LOCALAPPDATA%\Notchly\AgentUsage. No UI: the island's service drives it and listens for changes.
using System.Collections.Concurrent;
using System.IO;
using System.Net.NetworkInformation;
using Notchly.App.Platform.Shell;
using Notchly.Core;
using Notchly.Core.Agents;
using Serilog;

namespace Notchly.App.Platform.Agents;

/// <summary>
/// Reads the agents' usage from their local logs while it runs. Every call is safe from any thread; the store itself
/// lives on a private thread, where <see cref="Changed"/> and <see cref="Finished"/> are raised: handlers must hand
/// their work to the UI thread asynchronously (Dispatcher.BeginInvoke), never wait on it.
/// </summary>
public sealed class AgentLogStore : IDisposable
{
    /// <summary>The history the island can show: thirteen weeks for the activity map (seconds).</summary>
    public const double Horizon = AgentUsageSnapshot.DayCount * 86_400.0;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    /// <summary>Some agents keep their log open for the whole session, and Windows reports a write late or not at all
    /// until the file closes: logs written in the last half hour, or holding a turn, are checked this often instead.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private const double PollWindow = 30 * 60;
    /// <summary>Bursts of writes publish once.</summary>
    private static readonly TimeSpan PublishDelay = TimeSpan.FromSeconds(1);
    /// <summary>File events arrive one per write; they are gathered this long before reading.</summary>
    private static readonly TimeSpan EventDelay = TimeSpan.FromMilliseconds(300);
    private const double SaveInterval = 5 * 60;
    private const double RootCheckInterval = 300;

    private readonly BlockingCollection<Action> work = [];
    private readonly Thread thread;
    private readonly string archivePath;

    // Confined to the thread.
    private int generation;
    private bool running, paused;
    private CancellationTokenSource cancellation = new();
    private HashSet<AgentProvider> enabled = [];
    private AgentUsageStore store = new();
    private readonly Dictionary<string, AgentLogCursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AgentProvider, IAgentHarnessReader> readers = [];
    private readonly Dictionary<AgentProvider, AgentPlan> plans = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private List<AgentLogRoot> watchedRoots = [];
    private readonly HashSet<string> pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool pendingRescan;
    private Timer? tickTimer, pollTimer, publishTimer, eventTimer;
    private bool polling, publishScheduled, eventsScheduled;
    private (DateTimeOffset Date, ulong Awake)? offlineSince;
    private AgentUsageSnapshot? published;
    private DateTimeOffset lastRootCheck = AgentTime.DistantPast, lastSave = AgentTime.DistantPast;
    /// <summary>Nil until this reading saved or resumed progress: the first save always replaces what is on disk.</summary>
    private long? savedMark;
    private readonly byte[] chunk = new byte[AgentLineSplitter.ChunkSize];

    /// <summary>A store keeping its progress in <paramref name="archiveDirectory"/>, by default
    /// <c>%LOCALAPPDATA%\Notchly\AgentUsage</c>.</summary>
    public AgentLogStore(string? archiveDirectory = null)
    {
        archivePath = Path.Combine(archiveDirectory ?? DefaultArchiveDirectory, AgentUsageArchive.FileName);
        thread = new Thread(() =>
        {
            foreach (var job in work.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception e) { Log.Warning("Agent usage reading failed: {Error}", e.GetType().Name); }
            }
        })
        {
            IsBackground = true, Name = "Notchly agent usage", Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public static string DefaultArchiveDirectory => Path.Combine(AppIdentity.LocalDataDirectory, "AgentUsage");

    /// <summary>Raised on the store's thread with a fresh snapshot, at most once a second, once the first read is done
    /// and whenever stored values or the clock change what it shows.</summary>
    public event Action<AgentUsageSnapshot>? Changed;
    /// <summary>A turn ended while the store was reading live, not history. Filter it with
    /// <see cref="AgentAlerts.Delivers"/>; raised on the store's thread.</summary>
    public event Action<AgentUsageEvent.Finished>? Finished;

    /// <summary>Whether the first read since <see cref="StartAsync"/> is done.</summary>
    public bool Loaded { get; private set; }
    /// <summary>How long the first read took, archive included: for diagnostics.</summary>
    public TimeSpan? FirstReadDuration { get; private set; }
    /// <summary>Whether the first read resumed from the archive.</summary>
    public bool ResumedFromArchive { get; private set; }

    // MARK: Control

    /// <summary>Starts reading <paramref name="providers"/>' logs (Cursor reads none; its limits come through
    /// <see cref="NoteLimits"/>) on the store's thread, resuming from the archive. A running store reading another set
    /// starts over without its progress, as upstream: an agent turned on is read from its start. The task completes
    /// once the first read is done (or was cut short by <see cref="Stop"/>); nothing needs to wait for it.</summary>
    public Task StartAsync(IEnumerable<AgentProvider> providers)
    {
        var wanted = providers.ToHashSet();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var same = wanted.SetEquals(enabled);
                if (running && same && Loaded)
                {
                    if (paused) ResumeReading();
                    return;
                }
                // A first read a stop cut short starts again and resumes its progress; another set of agents does not.
                if (running) StopReading(keepProgress: same);
                StartReading(wanted);
            }
            finally
            {
                completion.TrySetResult();
            }
        });
        return completion.Task;
    }

    /// <summary>Stops reading and forgets what was read, saving progress first (or removing the archive when
    /// <paramref name="keepProgress"/> is false). Returns once the archive is settled.</summary>
    public void Stop(bool keepProgress = true)
    {
        cancellation.Cancel();
        Invoke(() => StopReading(keepProgress));
    }

    /// <summary>Stops reading while the island is away, keeping what was read and saving progress.</summary>
    public void Pause() => Post(() =>
    {
        if (!running || paused) return;
        paused = true;
        StopWatching();
        pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        polling = false;
        SaveProgress();
    });

    /// <summary>Reads what the agents wrote while paused.</summary>
    public void Resume() => Post(() =>
    {
        if (running && paused) ResumeReading();
    });

    /// <summary>Removes the saved progress, for when the feature is turned off.</summary>
    public void DeleteArchive() => Invoke(() => RemoveArchive(archivePath));

    public static void RemoveArchive(string? path = null)
    {
        try { File.Delete(path ?? Path.Combine(DefaultArchiveDirectory, AgentUsageArchive.FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Stop();
        work.CompleteAdding();
    }

    // MARK: Readings from elsewhere

    /// <summary>Limits an agent or service read from the account on request: kept when newer than what the logs said.</summary>
    public void NoteLimits(AgentLimits reading) => Post(() =>
    {
        if (!running || !enabled.Contains(reading.Provider)) return;
        store.UpdateLimits(reading);
        SchedulePublish();
    });

    /// <summary>Replaces a provider's limits, as an expiring account reading does (see <see cref="AgentClaudeServer.Current"/>).</summary>
    public void SetLimits(AgentLimits reading) => Post(() =>
    {
        if (!running || !enabled.Contains(reading.Provider)) return;
        store.SetLimits(reading);
        SchedulePublish();
    });

    public void ClearLimits(AgentProvider provider) => Post(() =>
    {
        if (!running) return;
        store.ClearLimits(provider);
        SchedulePublish();
    });

    /// <summary>The plan a provider's account is on, from outside the logs (Claude's <c>.claude.json</c>, Cursor's
    /// summary); Codex's comes from its logs. Null removes it.</summary>
    public void SetPlan(AgentProvider provider, AgentPlan? plan) => Post(() =>
    {
        if (plan is null ? !plans.Remove(provider) : plans.TryGetValue(provider, out var old) && old == plan) return;
        if (plan is not null) plans[provider] = plan;
        if (running) SchedulePublish();
    });

    /// <summary>Prices every response again, after <see cref="AgentPriceUpdater"/> installed a newer list.</summary>
    public void Reprice() => Post(() =>
    {
        if (!running) return;
        store.Reprice();
        SchedulePublish();
    });

    /// <summary>A snapshot now, built on the store's thread.</summary>
    public Task<AgentUsageSnapshot> SnapshotAsync(DateTimeOffset? now = null) => ReadAsync(_ => MakeSnapshot(now ?? DateTimeOffset.UtcNow));

    /// <summary>A snapshot now; blocks until the store's thread builds it. Never call it from a <see cref="Changed"/> handler
    /// that waits on the UI thread.</summary>
    public AgentUsageSnapshot Snapshot(DateTimeOffset? now = null) => Read(_ => MakeSnapshot(now ?? DateTimeOffset.UtcNow));

    /// <summary>Runs <paramref name="read"/> on the store's thread, as alerts need the records and limits.</summary>
    public Task<T> ReadAsync<T>(Func<AgentUsageStore, T> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { completion.SetResult(read(store)); }
            catch (Exception e) { completion.SetException(e); }
        });
        return completion.Task;
    }

    public T Read<T>(Func<AgentUsageStore, T> read)
    {
        if (Thread.CurrentThread == thread) return read(store);
        return ReadAsync(read).GetAwaiter().GetResult();
    }

    // MARK: Thread

    private void Post(Action action)
    {
        try { work.Add(action); }
        catch (InvalidOperationException) { }
    }

    private void Invoke(Action action)
    {
        if (Thread.CurrentThread == thread)
        {
            action();
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        try
        {
            work.Add(() =>
            {
                try { action(); }
                catch (Exception e) { failure = e; }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException) { return; }
        done.Wait();
        if (failure is not null) Log.Warning("Agent usage control failed: {Error}", failure.GetType().Name);
    }

    /// <summary>A timer callback that runs <paramref name="action"/> on the thread, unless the store moved on meanwhile.</summary>
    private Timer MakeTimer(Action action)
    {
        var mine = generation;
        return new Timer(_ => Post(() =>
        {
            if (running && generation == mine) action();
        }), null, Timeout.Infinite, Timeout.Infinite);
    }

    // MARK: Reading

    private IAgentHarnessReader? ReaderFor(AgentProvider provider)
    {
        if (readers.TryGetValue(provider, out var reader)) return reader;
        if (AgentReaders.For(provider) is not { } created) return null;
        readers[provider] = created;
        return created;
    }

    private void StartReading(HashSet<AgentProvider> providers)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        generation++;
        running = true;
        paused = false;
        Loaded = false;
        ResumedFromArchive = false;
        FirstReadDuration = null;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        enabled = providers;
        store = new AgentUsageStore();
        cursors.Clear();
        savedMark = null;
        // Prices first, so the first read is already priced.
        AgentPriceUpdater.InstallCached();
        var now = DateTimeOffset.UtcNow;
        var horizon = now.Plus(-Horizon);
        var roots = AgentPaths.Roots(enabled).ToList();
        var files = AgentLogFiles.Discover(roots, horizon, token);
        // Resumes where the last launch stopped, among the logs there now.
        if (LoadArchive() is { } saved && saved.Providers.SetEquals(enabled))
        {
            var resumed = AgentUsageArchive.Resume(saved, files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal), AgentLogFiles.Intact, horizon);
            store = resumed.Store;
            foreach (var progress in resumed.Cursors) cursors[progress.Path] = AgentLogCursor.Resumed(progress);
            ResumedFromArchive = true;
            // What the resume took back leaves the disk at the next save.
            if (resumed.Unchanged) savedMark = ProgressMark();
        }
        foreach (var file in files)
        {
            // A stop while reading leaves the rest for the next start.
            if (token.IsCancellationRequested) return;
            ReadFile(file.Path, file.Provider);
        }
        if (token.IsCancellationRequested) return;
        now = DateTimeOffset.UtcNow;
        // A turn left open by a crash would otherwise stay working.
        store.CloseIdleTurns(now, NotchAgentSupport.IdleTurn);
        CloseEndedTurns(roots, atLaunch: true);
        store.ReportsTransitions = true;
        tickTimer = MakeTimer(OnTick);
        tickTimer.Change(Tick, Tick);
        pollTimer = MakeTimer(OnPoll);
        publishTimer = MakeTimer(() =>
        {
            publishScheduled = false;
            Publish();
        });
        eventTimer = MakeTimer(FlushEvents);
        Watch(roots);
        WatchNetwork();
        SyncPolling(now);
        FirstReadDuration = started.Elapsed;
        Loaded = true;
        Publish();
        SaveProgress();
    }

    private void ResumeReading()
    {
        paused = false;
        Watch(AgentPaths.Roots(enabled).ToList());
        pendingRescan = true;
        FlushEvents();
        // Time went by meanwhile: a window or the day may have moved on.
        SchedulePublish();
    }

    private void StopReading(bool keepProgress)
    {
        if (keepProgress) SaveProgress();
        else RemoveArchive(archivePath);
        if (!running) return;
        running = false;
        paused = false;
        generation++;
        cancellation.Cancel();
        StopWatching();
        foreach (var timer in new[] { tickTimer, pollTimer, publishTimer, eventTimer }) timer?.Dispose();
        tickTimer = pollTimer = publishTimer = eventTimer = null;
        polling = publishScheduled = eventsScheduled = false;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        offlineSince = null;
        store = new AgentUsageStore();
        cursors.Clear();
        pendingPaths.Clear();
        pendingRescan = false;
        published = null;
        savedMark = null;
        lastSave = lastRootCheck = AgentTime.DistantPast;
        Loaded = false;
    }

    /// <summary>True when the log had entries to apply, or was gone and took a working turn with it.</summary>
    private bool ReadFile(string path, AgentProvider provider)
    {
        var token = cancellation.Token;
        if (token.IsCancellationRequested || ReaderFor(provider) is not { } reader) return false;
        if (!File.Exists(path))
        {
            var known = cursors.TryGetValue(path, out var gone) ? gone.Path : path;
            cursors.Remove(path);
            return store.Forget(known);
        }
        if (!cursors.TryGetValue(path, out var cursor))
        {
            cursor = new AgentLogCursor(path, provider);
            cursors[path] = cursor;
        }
        var changed = false;
        var now = DateTimeOffset.UtcNow;
        void Consume(ReadOnlyMemory<byte> line)
        {
            // Applied in log order while the chunk is alive, instead of keeping every parsed entry of a large file.
            var entries = reader.Parse(line, cursor.State, now);
            if (entries.Count == 0) return;
            changed = true;
            var (file, tracks, parent) = AgentLogRouting.Route(provider, cursor.Path, cursor.State, cursor.CodexParent(cursors));
            foreach (var finished in store.Apply(entries, file, provider, tracks, cursor.Modified, now, parent))
                if (finished is AgentUsageEvent.Finished done) Finished?.Invoke(done);
        }
        bool Continue() => !token.IsCancellationRequested;
        if (provider == AgentProvider.OpenCode)
            OpenCodeDatabase.ReadAppended(cursor, now.Plus(-Horizon), Continue, Consume);
        else if (reader.ReadsFullHistoryFirst && cursor.Offset == 0)
            AgentLogFiles.ReadAppended(cursor, chunk, Continue, Consume, AgentLogParser.CopilotHistoryLine);
        else
            AgentLogFiles.ReadAppended(cursor, chunk, Continue, Consume);
        return changed;
    }

    /// <summary>Reads the logs that grew, were replaced or disappeared, among those an agent may still be writing.</summary>
    private bool PollOpenLogs(double window)
    {
        var now = DateTimeOffset.UtcNow;
        // A turn gone quiet, as while it waits for an approval, is noticed as soon as its work resumes. An OpenCode
        // turn is kept by database and session.
        var working = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in store.Turns.Keys.Concat(store.Waiting.Keys))
        {
            working.Add(key);
            var mark = key.IndexOf('#');
            if (mark >= 0) working.Add(key[..mark]);
        }
        var changed = false;
        foreach (var cursor in cursors.Values.ToList())
        {
            if (!working.Contains(cursor.Path) && now.Since(cursor.Modified) >= window) continue;
            if (cursor.Provider == AgentProvider.OpenCode)
            {
                // A database changes in place: its write-ahead log grows instead.
                if (OpenCodeDatabase.Modified(cursor.Path) is { } modified && modified <= cursor.Modified) continue;
            }
            else if (FileIdentity.Read(cursor.Path) is { } facts && facts.Bytes == cursor.Offset && facts.Identity == cursor.Identity)
            {
                continue;
            }
            if (ReadFile(cursor.Path, cursor.Provider)) changed = true;
        }
        return changed;
    }

    private void OnPoll()
    {
        if (paused) return;
        var now = DateTimeOffset.UtcNow;
        var read = PollOpenLogs(PollWindow);
        var stopped = store.CloseSettledTurns(now);
        // After the logs too, which can hold a reply or a command's result written meanwhile.
        if (offlineSince is { } offline && store.CloseOfflineTurns(offline.Date, AwakeSeconds(offline.Awake), RunningCommands())) stopped = true;
        // After the logs, so a turn its last lines ended ends as usual.
        if (CloseEndedTurns(watchedRoots) || read || stopped) SchedulePublish();
        SyncPolling(now);
    }

    private void OnTick()
    {
        if (paused) return;
        var now = DateTimeOffset.UtcNow;
        var before = Inputs();
        store.CloseIdleTurns(now, NotchAgentSupport.IdleTurn);
        store.DropRecords(now.Plus(-Horizon));
        // A log kept open through a long quiet spell reports nothing when work resumes.
        var changed = PollOpenLogs(86_400);
        // A folder that appears later, like a first Codex session, is picked up without a restart.
        if (now.Since(lastRootCheck) > RootCheckInterval)
        {
            var roots = AgentPaths.Roots(enabled).ToList();
            if (!roots.Where(r => r.Exists).SequenceEqual(watchedRoots))
            {
                foreach (var file in AgentLogFiles.Discover(roots, now.Plus(-Horizon), cancellation.Token))
                    if (ReadFile(file.Path, file.Provider)) changed = true;
                Watch(roots);
            }
            lastRootCheck = now;
        }
        if (now.Since(lastSave) >= SaveInterval) SaveProgress();
        // Publish only when stored values or sliding time windows change.
        if (changed || Inputs() != before || published is null || AgentUsageSummary.MovesWithClock(published, now)) SchedulePublish();
        SyncPolling(now);
    }

    /// <summary>The fast poll has no work once recent logs and live turns are gone; file events and the tick arm it again.</summary>
    private void SyncPolling(DateTimeOffset now)
    {
        if (pollTimer is null) return;
        var wanted = !paused && (store.Turns.Count > 0 || store.Waiting.Count > 0 || cursors.Values.Any(c => now.Since(c.Modified) < PollWindow));
        if (wanted == polling) return;
        polling = wanted;
        if (wanted) pollTimer.Change(Poll, Poll);
        else pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The logs whose turn waits on a shell command of its own.</summary>
    private HashSet<string> RunningCommands() =>
        cursors.Values.Where(c => c.State.RunningCommands.Count > 0).Select(c => c.Path).ToHashSet();

    /// <summary>Ends the Claude turns whose process is gone. True when one was showing.</summary>
    private bool CloseEndedTurns(IEnumerable<AgentLogRoot> roots, bool atLaunch = false)
    {
        if (!store.ShowsClaudeTurn) return false;
        return store.CloseEndedTurns(ClaudeProcessRecords.Read(AgentPaths.ClaudeSessionFolders(roots)), atLaunch);
    }

    private (int, int, long, int, long, string?) Inputs() =>
        (store.Records.Count, store.Turns.Count, store.Turns.Values.Sum(t => t.LastActivity.UtcTicks % 1_000_000_007),
         store.Limits.Count, store.Limits.Values.Sum(l => l.ObservedAt.UtcTicks % 1_000_000_007), store.CodexPlan);

    // MARK: Publishing

    private AgentUsageSnapshot MakeSnapshot(DateTimeOffset now)
    {
        var all = new Dictionary<AgentProvider, AgentPlan>(plans);
        if (AgentPlans.Codex(store.CodexPlan) is { } codex) all[AgentProvider.Codex] = codex;
        return store.Snapshot(all, enabled, now);
    }

    private void SchedulePublish()
    {
        if (publishScheduled || publishTimer is null) return;
        publishScheduled = true;
        publishTimer.Change(PublishDelay, Timeout.InfiniteTimeSpan);
    }

    private void Publish()
    {
        if (!running || !Loaded) return;
        var next = MakeSnapshot(DateTimeOffset.UtcNow);
        if (published is not null && published.Equals(next)) return;
        published = next;
        Changed?.Invoke(next);
    }

    // MARK: Watching

    private void Watch(List<AgentLogRoot> roots)
    {
        StopWatching();
        watchedRoots = roots.Where(r => r.Exists).ToList();
        lastRootCheck = DateTimeOffset.UtcNow;
        foreach (var root in watchedRoots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = root.Provider != AgentProvider.OpenCode,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                if (root.Provider == AgentProvider.OpenCode) watcher.Filter = OpenCodeRows.Database + "*";
                watcher.Changed += (_, e) => Noticed(e.FullPath);
                watcher.Created += (_, e) => Noticed(e.FullPath);
                watcher.Deleted += (_, e) => Noticed(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Noticed(e.OldFullPath);
                    Noticed(e.FullPath);
                };
                watcher.Error += (_, _) => Post(() =>
                {
                    pendingRescan = true;
                    ScheduleEvents();
                });
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception e) when (e is ArgumentException or IOException or FileNotFoundException or PlatformNotSupportedException) { }
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        watchers.Clear();
    }

    private void Noticed(string? path)
    {
        if (path is null) return;
        Post(() =>
        {
            if (!running || paused) return;
            pendingPaths.Add(path);
            ScheduleEvents();
        });
    }

    private void ScheduleEvents()
    {
        if (eventsScheduled || eventTimer is null) return;
        eventsScheduled = true;
        eventTimer.Change(EventDelay, Timeout.InfiniteTimeSpan);
    }

    private void FlushEvents()
    {
        eventsScheduled = false;
        if (watchedRoots.Count == 0 && !pendingRescan) return;
        var changed = false;
        if (pendingRescan)
        {
            pendingRescan = false;
            pendingPaths.Clear();
            foreach (var file in AgentLogFiles.Discover(watchedRoots, DateTimeOffset.UtcNow.Plus(-Horizon), cancellation.Token))
                if (ReadFile(file.Path, file.Provider)) changed = true;
            // Logs gone meanwhile give back what they gave.
            foreach (var cursor in cursors.Values.ToList())
                if (!File.Exists(cursor.Path) && ReadFile(cursor.Path, cursor.Provider)) changed = true;
        }
        else
        {
            var paths = pendingPaths.ToList();
            pendingPaths.Clear();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var noticed in paths)
            {
                var path = noticed.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || noticed.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                    ? noticed[..^4] : noticed;
                if (watchedRoots.FirstOrDefault(r => r.Accepts(path)) is { } root)
                {
                    if (done.Add(path) && ReadFile(path, root.Provider)) changed = true;
                    continue;
                }
                // A folder removed or renamed takes its logs with it.
                var prefix = path + Path.DirectorySeparatorChar;
                foreach (var cursor in cursors.Values.Where(c => c.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    if (done.Add(cursor.Path) && ReadFile(cursor.Path, cursor.Provider)) changed = true;
                // A folder created or moved in brings logs of its own.
                if (Directory.Exists(path) && watchedRoots.FirstOrDefault(r => path.StartsWith(r.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } parent)
                {
                    foreach (var file in AgentLogFiles.Discover([parent], DateTimeOffset.UtcNow.Plus(-Horizon), cancellation.Token)
                                 .Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        if (done.Add(file.Path) && ReadFile(file.Path, file.Provider)) changed = true;
                }
            }
        }
        SyncPolling(DateTimeOffset.UtcNow);
        // Tool output and lines with nothing to keep change nothing shown.
        if (changed) SchedulePublish();
    }

    // MARK: Network

    /// <summary>Claude Code retries for minutes without a word while the computer is offline; the poll ends its turns
    /// once the computer stays offline, counting only the time it was awake.</summary>
    private void WatchNetwork()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NoteNetwork(NetworkInterface.GetIsNetworkAvailable());
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => Post(() =>
    {
        if (running) NoteNetwork(e.IsAvailable);
    });

    private void NoteNetwork(bool online)
    {
        if (online) offlineSince = null;
        else offlineSince ??= (DateTimeOffset.UtcNow, Awake());
    }

    private static ulong Awake() => Native.QueryUnbiasedInterruptTime(out var time) ? time : 0;

    private static double AwakeSeconds(ulong since) => Math.Max(0, (double)(Awake() - Math.Min(Awake(), since)) / TimeSpan.TicksPerSecond);

    // MARK: Archive

    private AgentUsageArchive.Contents? LoadArchive()
    {
        try
        {
            if (!File.Exists(archivePath)) return null;
            return AgentUsageArchive.Decode(File.ReadAllBytes(archivePath), AgentUsageArchive.Build);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves what was read when reading moved on since the last save. A log that started over while running
    /// still counts what its old contents gave, so it is left out; nothing from OpenCode's database is saved.</summary>
    private void SaveProgress()
    {
        if (!running || !Loaded) return;
        var mark = ProgressMark();
        lastSave = DateTimeOffset.UtcNow;
        if (mark == savedMark) return;
        var kept = cursors.Values.Where(c => !c.Restarted && c.Provider != AgentProvider.OpenCode).Select(c => c.Saved()).ToList();
        var contents = new AgentUsageArchive.Contents(enabled, store.Save(), kept);
        try
        {
            var folder = Path.GetDirectoryName(archivePath)!;
            Directory.CreateDirectory(folder);
            var temporary = archivePath + ".tmp";
            File.WriteAllBytes(temporary, AgentUsageArchive.Encode(contents, AgentUsageArchive.Build));
            File.Move(temporary, archivePath, overwrite: true);
            savedMark = mark;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Agent usage progress could not be saved: {Error}", e.GetType().Name);
        }
    }

    /// <summary>Changes whenever a log is read further, replaced or let go. OpenCode, which is never saved, leaves it alone.</summary>
    private long ProgressMark()
    {
        long mark = store.Records.Count(r => r.Provider != AgentProvider.OpenCode);
        foreach (var cursor in cursors.Values)
        {
            if (cursor.Provider == AgentProvider.OpenCode) continue;
            mark = unchecked(mark + HashCode.Combine(cursor.Path, cursor.Offset, cursor.Identity, cursor.Restarted));
        }
        return mark;
    }
}
