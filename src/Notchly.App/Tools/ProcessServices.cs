// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Sources/Vorssaint/Services/KillProcess/KillProcessService.swift and PortManager/PortManagerService.swift.
// The process list is the monitor's own reading (one NtQuerySystemInformation call); a snapshot that fails never
// wipes what is shown. Every kill is checked against the creation time the row was listed with, and the ports come
// from the kernel's TCP table, with each owner's creation time read before and after so a reused pid is never offered.
using System.Collections.ObjectModel;
using System.Diagnostics;
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Monitor;
using Notchly.App.Platform.Tools;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

/// <summary>A row of the kill list: an app (with its helpers folded in when grouping is on) or one process.</summary>
public sealed record ProcessView(int Pid, string Name, string? Path, double Cpu, long MemoryBytes, int Processes, bool IsProtected, long? StartTime)
{
    public KillRow Row => new(Pid, Name, Cpu, MemoryBytes);
}

public sealed class KillProcessService(SettingsStore settings) : IFeatureService
{
    private readonly ProcessUsageSampler _sampler = new();
    private IReadOnlyList<ProcessView> _rows = [];
    private IReadOnlyList<ProcessUsageSampler.ProcessInfo> _all = [];

    public bool HasLoadedOnce { get; private set; }
    public IReadOnlyList<ProcessView> Rows => _rows;

    public void Sync() { }
    public void Dispose() { }

    /// <summary>Takes a reading (off the UI thread). CPU shares need two readings, so the first leaves them at zero.</summary>
    public void Refresh()
    {
        _sampler.Refresh(null);
        var snapshot = _sampler.Snapshot();
        // A failed reading, or one with nothing in it, keeps the list that was shown.
        if (snapshot.Count == 0) return;
        _all = snapshot;
        var group = settings.GetBool(DefaultsKey.KillProcessGroupRelated);
        var owners = group ? _sampler.Owners() : null;
        var names = snapshot.ToDictionary(p => p.Pid, p => p.Name);
        var ownPid = Environment.ProcessId;
        var groups = snapshot.GroupBy(p => owners?.GetValueOrDefault(p.Pid, p.Pid) ?? p.Pid);
        var rows = new List<ProcessView>();
        foreach (var g in groups)
        {
            var key = g.Key;
            var start = ProcessTools.StartTime(key);
            var name = DisplayName(key, names.GetValueOrDefault(key, g.First().Name));
            var path = AudioSessions.ProcessImage((uint)key).Path;
            rows.Add(new ProcessView(key, name, path.Length == 0 ? null : path, g.Sum(p => p.Cpu), (long)g.Sum(p => (double)p.PrivateBytes), g.Count(),
                KillProcessSupport.IsProtected(key, names.GetValueOrDefault(key, name), ownPid, start is not null), start));
        }
        _rows = rows;
        HasLoadedOnce = true;
    }

    private static string DisplayName(int pid, string fallback)
    {
        var path = AudioSessions.ProcessImage((uint)pid).Path;
        if (path.Length == 0) return System.IO.Path.GetFileNameWithoutExtension(fallback);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim()
                   ?? System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return System.IO.Path.GetFileNameWithoutExtension(path); }
    }

    /// <summary>The rows to show: matching the search, in the chosen order.</summary>
    public IReadOnlyList<ProcessView> Visible(string query)
    {
        var sort = KillProcessSupport.ParseSort(settings.GetString(DefaultsKey.KillProcessSortBy));
        var ascending = settings.GetBool(DefaultsKey.KillProcessSortAscending);
        var matching = _rows.Where(r => KillProcessSupport.Matches(query, r.Name, r.Pid)).ToDictionary(r => r.Pid);
        return KillProcessSupport.Sorted(matching.Values.Select(r => r.Row), sort, ascending).Select(r => matching[r.Pid]).ToList();
    }

    /// <summary>Clicking a column: the same column flips, a new one opens in its natural direction.</summary>
    public void SortBy(KillSort sort)
    {
        var current = KillProcessSupport.ParseSort(settings.GetString(DefaultsKey.KillProcessSortBy));
        if (current == sort) settings.Set(DefaultsKey.KillProcessSortAscending, !settings.GetBool(DefaultsKey.KillProcessSortAscending));
        else
        {
            settings.Set(DefaultsKey.KillProcessSortBy, KillProcessSupport.SortName(sort));
            settings.Set(DefaultsKey.KillProcessSortAscending, KillProcessSupport.DefaultAscending(sort));
        }
    }

    /// <summary>The processes a tree kill reaches, deepest first.</summary>
    public IReadOnlyList<int> TreeOf(int pid) => KillProcessSupport.TreeKillOrder(_all.Select(p => (p.Pid, p.Parent)), pid);

    /// <summary>The pids of every process with the same name as a row.</summary>
    public IReadOnlyList<int> SameName(string name) =>
        _all.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToList();

    /// <summary>Stops one process. A refusal for want of rights is returned so the caller can offer the administrator prompt once.</summary>
    public ProcessTools.Outcome Stop(int pid, long? listedStart, bool force) => ProcessTools.Stop(pid, listedStart, force);

    public string? NameOf(int pid) => _all.FirstOrDefault(p => p.Pid == pid)?.Name;
}

public sealed class PortManagerService(SettingsStore settings, App app) : IFeatureService
{
    private readonly PortManagerSupport.RefreshState _state = new();

    public IReadOnlyList<PortEntry> Entries => _state.Entries;
    public bool HasLoadedOnce => _state.HasLoadedOnce;
    public bool RefreshFailed => _state.RefreshFailed;
    public bool IsRefreshing => _state.IsRefreshing;
    public event Action? Changed;

    public void Sync() { }
    public void Dispose() { }

    /// <summary>Reads the table off the UI thread. A second call while one runs is ignored.</summary>
    public async Task RefreshAsync()
    {
        if (!_state.TryBegin()) return;
        Changed?.Invoke();
        var snapshot = await Task.Run(Read);
        _state.Complete(snapshot);
        Changed?.Invoke();
    }

    private static IReadOnlyList<PortEntry>? Read()
    {
        try
        {
            // A creation time kept only when it was the same before and after the table was read.
            var listeners = TcpTable.Listeners();
            if (listeners is null) return null;
            var pids = listeners.Select(l => l.Pid).Distinct().ToList();
            var before = pids.ToDictionary(p => p, p => ProcessTools.StartTime(p));
            var names = pids.ToDictionary(p => p, NameOf);
            var after = pids.ToDictionary(p => p, p => ProcessTools.StartTime(p));
            var entries = listeners.Select(l => new PortEntry(l.Port, "TCP", l.Address, l.Pid, names[l.Pid],
                PortManagerSupport.KeepStart(before[l.Pid], after[l.Pid])));
            return PortManagerSupport.DedupedAndSorted(entries);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Ports could not be listed");
            return null;
        }
    }

    private static string NameOf(int pid)
    {
        if (pid == 4) return "System";
        try { using var process = Process.GetProcessById(pid); return process.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return $"Process {pid}"; }
    }

    public IReadOnlyList<PortEntry> Filtered(string query) => Entries.Where(e => PortManagerSupport.Matches(e, query)).ToList();

    /// <summary>Stopping a port's owner goes through Kill process, which has to be installed.</summary>
    public bool CanTerminate => settings.IsAvailable(AppFeature.KillProcess);

    public ProcessTools.Outcome Terminate(PortEntry entry, bool force)
    {
        if (!CanTerminate || entry.StartedAt is null || KillProcessSupport.IsProtected(entry.Pid, entry.ProcessName, Environment.ProcessId, true))
            return ProcessTools.Outcome.Failed;
        _ = app;
        return ProcessTools.Stop(entry.Pid, entry.StartedAt, force);
    }
}
