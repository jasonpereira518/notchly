// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/Notch/NotchDownloadService.swift: watches the top level
// of one folder (the user's Downloads unless they chose another) and turns what browsers write there
// into the island's downloads. A FileSystemWatcher stands in for the vnode sources, rename events for
// the browsers' "download finished" broadcast, and a size poll for NSProgress, which Windows browsers
// do not publish. Only names, sizes and times of that folder are read; nothing leaves the computer.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Notchly.App.Platform.Shell;
using Notchly.Core.Downloads;
using Notchly.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace Notchly.App.Island.Downloads;

public sealed class DownloadService : IDisposable
{
    private static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly DownloadProgressTracker _tracker = new();
    private readonly DispatcherTimer _scanDelay, _poll, _expiry;
    private FileSystemWatcher? _watcher;
    private string? _folder;
    private IReadOnlyDictionary<string, PartialDownload> _partials = new Dictionary<string, PartialDownload>();
    private IReadOnlyList<DownloadItem> _folderItems = [];
    private readonly List<DownloadItem> _finished = [];
    /// <summary>Partials the watcher saw renamed into a final name, with when: proof they finished.</summary>
    private readonly Dictionary<string, DateTime> _renamed = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _scanning, _rescan, _polling;

    public IReadOnlyList<DownloadItem> Items { get; private set; } = [];
    /// <summary>The folder being watched, or the one that could not be watched.</summary>
    public string? Folder { get; private set; }
    public string? FolderName => Folder is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;
    public bool FolderUnavailable { get; private set; }
    public bool IsRunning => _folder is not null;

    /// <summary>The download shown in the closed island: the newest one still arriving.</summary>
    public DownloadItem? Current => Items.FirstOrDefault(i => i.Active && !i.Completed);

    public event Action? Changed;
    public event Action<DownloadItem>? Arrived;
    /// <summary>A download went away unfinished: cancelled or failed (the mascot will react, M3).</summary>
    public event Action? Failed;

    public DownloadService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _scanDelay = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _scanDelay.Tick += (_, _) => { _scanDelay.Stop(); Scan(); };
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ActivePoll };
        _poll.Tick += (_, _) => Poll();
        _expiry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DownloadSupport.FinishedRetention };
        _expiry.Tick += (_, _) => { _expiry.Stop(); _finished.Clear(); Rebuild(); };
    }

    // MARK: Folder choice (shared by the island page and Settings)

    /// <summary>The folder to watch: the one chosen, or Windows' Downloads folder.</summary>
    public static string ResolveFolder(SettingsStore settings) =>
        settings.GetString(DefaultsKey.NotchDownloadsFolderPath) is { Length: > 0 } chosen ? chosen : KnownFolders.Downloads;

    public static bool UsesDefaultFolder(SettingsStore settings) =>
        settings.GetString(DefaultsKey.NotchDownloadsFolderPath).Length == 0;

    /// <summary>Asks for another folder; choosing one also switches Downloads on, as upstream.</summary>
    public static bool ChooseFolder(SettingsStore settings, Window? owner)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder where your browser saves downloads. Only that folder is watched.",
            InitialDirectory = Directory.Exists(ResolveFolder(settings)) ? ResolveFolder(settings) : KnownFolders.Downloads,
        };
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return false;
        var chosen = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        var isDefault = string.Equals(chosen, Path.TrimEndingDirectorySeparator(KnownFolders.Downloads), StringComparison.OrdinalIgnoreCase);
        settings.Set(DefaultsKey.NotchDownloadsFolderPath, isDefault ? "" : chosen);
        settings.Set(DefaultsKey.NotchDownloadsEnabled, true);
        return true;
    }

    public static void UseDownloadsFolder(SettingsStore settings) => settings.Set(DefaultsKey.NotchDownloadsFolderPath, "");

    // MARK: Lifecycle (upstream syncWithPreferences)

    /// <summary>Watches <paramref name="folder"/> when wanted; retries a folder that was unavailable.</summary>
    public void Sync(bool wanted, string folder)
    {
        if (!wanted) { if (IsRunning || FolderUnavailable) Stop(); return; }
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (IsRunning && string.Equals(_folder, normalized, StringComparison.OrdinalIgnoreCase)) return;
        Stop();
        Start(normalized);
    }

    private void Start(string folder)
    {
        Folder = folder;
        try
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            var generation = _generation;
            watcher.Created += (_, _) => Post(generation, ScheduleScan);
            watcher.Deleted += (_, _) => Post(generation, ScheduleScan);
            watcher.Changed += (_, _) => Post(generation, ScheduleScan);
            watcher.Renamed += (_, e) => Post(generation, () => OnRenamed(e.OldFullPath, e.FullPath));
            watcher.Error += (_, e) => Post(generation, () => OnWatcherError(e.GetException()));
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Information("Downloads folder unavailable ({Reason})", e.GetType().Name);
            FolderUnavailable = true;
            Changed?.Invoke();
            return;
        }
        _folder = folder;
        FolderUnavailable = false;
        Log.Information("Watching the downloads folder");
        Scan();
    }

    public void Stop()
    {
        var wasRunning = IsRunning || FolderUnavailable || Items.Count > 0;
        _generation++;
        _watcher?.Dispose(); _watcher = null;
        _scanDelay.Stop(); _poll.Stop(); _expiry.Stop();
        _folder = null;
        Folder = null;
        FolderUnavailable = false;
        _partials = new Dictionary<string, PartialDownload>();
        _folderItems = [];
        _finished.Clear();
        _renamed.Clear();
        _tracker.Clear();
        _scanning = _rescan = _polling = false;
        Items = [];
        if (wasRunning)
        {
            Log.Information("Stopped watching the downloads folder");
            Changed?.Invoke();
        }
    }

    public void Dispose() => Stop();

    private void Post(int generation, Action action) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (generation == _generation) action(); });

    private void OnWatcherError(Exception error)
    {
        if (_folder is { } folder && !Directory.Exists(folder))
        {
            Stop();
            Folder = folder;
            FolderUnavailable = true;
            Changed?.Invoke();
            return;
        }
        // The change buffer overflowed: read the whole folder again.
        Log.Debug(error, "Downloads watcher overflowed; rescanning");
        ScheduleScan();
    }

    // MARK: Scanning

    private void ScheduleScan()
    {
        if (_folder is null || _scanDelay.IsEnabled) return;
        _scanDelay.Start();
    }

    private async void Scan()
    {
        if (_folder is not { } folder) return;
        if (_scanning) { _rescan = true; return; }
        _scanning = true;
        var generation = _generation;
        var previous = _partials;
        var renamed = _renamed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DownloadFolderSnapshot? current;
        DownloadScanOutcome outcome;
        try
        {
            (current, outcome) = await Task.Run(() =>
            {
                var snapshot = DownloadSupport.ScanFolder(folder, FileIdentity.Read);
                return (snapshot, DownloadSupport.Compare(previous, snapshot, FileIdentity.Read, Exists,
                    p => renamed.Contains(p.Path), DateTime.UtcNow));
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Downloads scan failed");
            current = null;
            outcome = new DownloadScanOutcome([], []);
        }
        if (generation != _generation) return;
        _scanning = false;
        if (current is null)
        {
            Stop();
            Folder = folder;
            FolderUnavailable = true;
            Changed?.Invoke();
            return;
        }

        var now = DateTime.UtcNow;
        _partials = current.Partials;
        _folderItems = current.Files;
        foreach (var partial in _partials.Values) _tracker.Observe(partial.Path, partial.Bytes, partial.Modified, now);
        _tracker.Retain(_partials.Keys);
        foreach (var stale in _renamed.Where(r => now - r.Value > EvidenceLifetime).Select(r => r.Key).ToList()) _renamed.Remove(stale);
        foreach (var item in outcome.Completed) RecordCompletion(item);
        if (outcome.Failed.Count > 0) _ = ConfirmFailures(outcome.Failed, generation);
        SyncPolling();
        Rebuild();
        if (_rescan) { _rescan = false; ScheduleScan(); }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>A browser may finish just after its partial vanishes: wait for its evidence first.</summary>
    private async Task ConfirmFailures(IReadOnlyList<PartialDownload> failed, int generation)
    {
        await Task.Delay(DownloadSupport.FailureGrace);
        if (generation != _generation) return;
        var unfinished = failed.Where(f => !_renamed.ContainsKey(f.Path) && !Exists(f.ExpectedPath)).ToList();
        if (unfinished.Count == 0) return;
        Log.Debug("{Count} download(s) went away unfinished", unfinished.Count);
        Failed?.Invoke();
    }

    /// <summary>A partial renamed into a final name is a finished download, even one too fast for a scan.</summary>
    private async void OnRenamed(string oldPath, string newPath)
    {
        ScheduleScan();
        if (_folder is not { } folder || !DownloadSupport.IsPartial(oldPath) || DownloadSupport.IsPartial(newPath)) return;
        var generation = _generation;
        var item = await Task.Run(() => DownloadSupport.RenamedItem(oldPath, newPath, folder, DateTime.UtcNow));
        if (generation != _generation || item is null) return;
        _renamed[DownloadSupport.Normalize(oldPath)] = DateTime.UtcNow;
        RecordCompletion(item);
        Rebuild();
    }

    private void RecordCompletion(DownloadItem item)
    {
        if (_finished.Any(f => string.Equals(f.Id, item.Id, StringComparison.OrdinalIgnoreCase))) return;
        _finished.Insert(0, item);
        if (_finished.Count > DownloadSupport.MaximumFinished) _finished.RemoveRange(DownloadSupport.MaximumFinished, _finished.Count - DownloadSupport.MaximumFinished);
        Log.Debug("Download arrived");
        Arrived?.Invoke(item);
        _expiry.Stop();
        _expiry.Start();
    }

    // MARK: Live sizes

    /// <summary>While partials exist, their sizes are read every second (every 10 s once all are paused).</summary>
    private void SyncPolling()
    {
        if (_partials.Count == 0) { _poll.Stop(); return; }
        var now = DateTime.UtcNow;
        _poll.Interval = _partials.Keys.Any(p => _tracker.IsActive(p, now)) ? ActivePoll : IdlePoll;
        if (!_poll.IsEnabled) _poll.Start();
    }

    private async void Poll()
    {
        if (_polling || _partials.Count == 0) return;
        _polling = true;
        var generation = _generation;
        var paths = _partials.Values.Select(p => (p.Path, Read: p.ContentPath ?? p.Path)).ToList();
        var sizes = await Task.Run(() => paths.Select(p => (p.Path, Facts: FileIdentity.Read(p.Read))).ToList());
        if (generation != _generation) return;
        _polling = false;
        var now = DateTime.UtcNow;
        var vanished = false;
        foreach (var (path, facts) in sizes)
        {
            if (facts is null) { vanished = true; continue; }
            _tracker.Observe(path, facts.Bytes, now, now);
        }
        if (vanished) ScheduleScan();
        SyncPolling();
        Rebuild();
    }

    // MARK: Items

    private void Rebuild()
    {
        if (_folder is null) return;
        var now = DateTime.UtcNow;
        var active = _partials.Values.Select(p => new DownloadItem(p.Path, Path.GetFileName(p.ExpectedPath),
            _tracker.Bytes(p.Path) ?? p.Bytes, null, Completed: false, Active: _tracker.IsActive(p.Path, now), p.Created)
            { BytesPerSecond = _tracker.Rate(p.Path, now) });
        var updated = DownloadSupport.MergedItems(active, _folderItems, _finished);
        if (updated.SequenceEqual(Items)) return;
        Items = updated;
        Changed?.Invoke();
    }
}
