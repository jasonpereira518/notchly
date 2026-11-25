// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's ProcessUsageService (proc_pid_rusage, IOAccelerator per-app GPU time): the apps using
// the most CPU, GPU, memory or energy. One call lists every process with its CPU time and private memory; processes
// that are helpers of an app (a browser's renderers) fold under that app, using the same parent walk the mixer uses.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Notchly.App.Platform.Audio;
using Notchly.Core.Monitor;
using Notchly.Core.Sound;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Monitor;

public enum ProcessKind { Cpu, Gpu, Memory, Energy }

/// <summary>One app in a top list: its value (percent for CPU, GPU and energy; bytes for memory) and how many processes it folds in.</summary>
public sealed record ProcessRow(string Name, string? ExePath, int Pid, double Value, int Processes);

public sealed class ProcessUsageSampler
{
    private sealed record Entry(int Pid, int Parent, string Name, ulong CpuNanoseconds, ulong PrivateBytes);

    private Dictionary<int, ulong> _previousCpu = [];
    private double _previousAt;
    private IReadOnlyDictionary<int, double> _cpuPercent = new Dictionary<int, double>();
    private IReadOnlyList<Entry> _entries = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes one reading; CPU shares need two, so the first call only sets the baseline.</summary>
    public void Refresh(double? aggregateCpuPercent)
    {
        var now = Clock.UnbiasedSeconds();
        var entries = Read();
        if (entries is null) return;
        var elapsed = now - _previousAt;
        var percents = new Dictionary<int, double>();
        if (_previousCpu.Count > 0 && elapsed > 0 && elapsed < 30)
            foreach (var entry in entries)
                if (_previousCpu.TryGetValue(entry.Pid, out var before))
                    percents[entry.Pid] = MetricFormat.ProcessCpuPercentage(before, entry.CpuNanoseconds, elapsed, Environment.ProcessorCount);
        // The sampling clocks differ, so rows never claim more than the aggregate meter does.
        var scale = MetricFormat.ProcessReconciliationScale(percents.Values.Sum(), aggregateCpuPercent);
        _cpuPercent = scale < 1 ? percents.ToDictionary(p => p.Key, p => p.Value * scale) : percents;
        _previousCpu = entries.ToDictionary(e => e.Pid, e => e.CpuNanoseconds);
        _previousAt = now;
        _entries = entries;
    }

    public void Reset() { _previousCpu = []; _cpuPercent = new Dictionary<int, double>(); _entries = []; }

    /// <summary>One process as the last reading saw it.</summary>
    public sealed record ProcessInfo(int Pid, int Parent, string Name, double Cpu, ulong PrivateBytes);

    /// <summary>Every process of the last reading, unfolded (the kill list's rows).</summary>
    public IReadOnlyList<ProcessInfo> Snapshot() =>
        _entries.Where(e => e.Pid != 0).Select(e => new ProcessInfo(e.Pid, e.Parent, e.Name, _cpuPercent.GetValueOrDefault(e.Pid), e.PrivateBytes)).ToList();

    /// <summary>The app a process belongs to, by the same walk the top lists use: its nearest ancestor with a window.</summary>
    public IReadOnlyDictionary<int, int> Owners()
    {
        var parents = _entries.ToDictionary(e => e.Pid, e => e.Parent);
        var windowed = WindowedProcesses();
        return _entries.Where(e => e.Pid != 0).ToDictionary(e => e.Pid,
            e => MixerSupport.OwningAppPid(e.Pid, pid => windowed.Contains(pid), pid => parents.GetValueOrDefault(pid, 0)) ?? e.Pid);
    }

    /// <summary>The top apps for a kind, helpers folded under their app.</summary>
    public IReadOnlyList<ProcessRow> Top(ProcessKind kind, int limit, IReadOnlyDictionary<int, double>? gpuByPid = null)
    {
        if (_entries.Count == 0) return [];
        var parents = _entries.ToDictionary(e => e.Pid, e => e.Parent);
        var windowed = WindowedProcesses();
        var groups = new Dictionary<int, (double Value, int Count)>();
        foreach (var entry in _entries)
        {
            if (entry.Pid == 0) continue;
            var value = kind switch
            {
                ProcessKind.Cpu => _cpuPercent.GetValueOrDefault(entry.Pid),
                ProcessKind.Gpu => gpuByPid?.GetValueOrDefault(entry.Pid) ?? 0,
                ProcessKind.Memory => (double)entry.PrivateBytes,
                _ => _cpuPercent.GetValueOrDefault(entry.Pid) + (gpuByPid?.GetValueOrDefault(entry.Pid) ?? 0),
            };
            if (value <= 0) continue;
            var owner = MixerSupport.OwningAppPid(entry.Pid, pid => windowed.Contains(pid), pid => parents.GetValueOrDefault(pid, 0)) ?? entry.Pid;
            var current = groups.GetValueOrDefault(owner);
            groups[owner] = (current.Value + value, current.Count + 1);
        }
        var names = _entries.ToDictionary(e => e.Pid, e => e.Name);
        return groups.Where(g => kind != ProcessKind.Energy || g.Value.Value >= 2)
            .OrderByDescending(g => g.Value.Value).Take(limit)
            .Select(g =>
            {
                var path = AudioSessions.ProcessImage((uint)g.Key).Path;
                var fallback = names.GetValueOrDefault(g.Key, "Process");
                return new ProcessRow(Display(path, fallback), path.Length == 0 ? null : path, g.Key,
                    kind == ProcessKind.Memory ? g.Value.Value : MetricFormat.BoundedPercentage(g.Value.Value), g.Value.Count);
            }).ToList();
    }

    private string Display(string path, string fallback)
    {
        if (path.Length == 0) return Path.GetFileNameWithoutExtension(fallback);
        if (_names.TryGetValue(path, out var cached)) return cached;
        string name;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { name = Path.GetFileNameWithoutExtension(path); }
        return _names[path] = name;
    }

    private static HashSet<int> WindowedProcesses()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    // MARK: SYSTEM_PROCESS_INFORMATION (x64 layout)

    private const int OffsetNext = 0, OffsetPrivate = 8, OffsetUser = 40, OffsetKernel = 48, OffsetNameLength = 56, OffsetNameBuffer = 64,
        OffsetPid = 80, OffsetParent = 88;

    private static IReadOnlyList<Entry>? Read()
    {
        if (IntPtr.Size != 8) return null;
        uint size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var status = NtQuerySystemInformation(5, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, needed + (1 << 16)); continue; }
                if (status != 0) return null;
                var result = new List<Entry>();
                for (var offset = 0L; ;)
                {
                    var item = IntPtr.Add(buffer, (int)offset);
                    var nameLength = Marshal.ReadInt16(item, OffsetNameLength);
                    var nameBuffer = Marshal.ReadIntPtr(item, OffsetNameBuffer);
                    var name = nameBuffer != IntPtr.Zero && nameLength > 0 ? Marshal.PtrToStringUni(nameBuffer, nameLength / 2) ?? "" : "";
                    var cpu = (ulong)(Marshal.ReadInt64(item, OffsetUser) + Marshal.ReadInt64(item, OffsetKernel)) * 100;
                    result.Add(new Entry((int)Marshal.ReadIntPtr(item, OffsetPid), (int)Marshal.ReadIntPtr(item, OffsetParent), name, cpu,
                        (ulong)Marshal.ReadInt64(item, OffsetPrivate)));
                    var next = Marshal.ReadInt32(item, OffsetNext);
                    if (next == 0) break;
                    offset += next;
                }
                return result;
            }
            catch (Exception e) when (e is AccessViolationException or ArgumentException)
            {
                Log.Debug(e, "Processes could not be listed");
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, uint length, out uint returned);
}
