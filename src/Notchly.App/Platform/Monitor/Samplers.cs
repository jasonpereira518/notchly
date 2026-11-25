// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-ins for upstream's samplers (CPUCoreSampler, memory from vm_statistics, NetworkSampler, DiskSampler,
// the IOAccelerator GPU read): each reads one thing from Windows and returns plain numbers. The decisions about what
// the numbers mean live in Notchly.Core.Monitor and are tested there.
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Notchly.Core.Monitor;
using Serilog;

namespace Notchly.App.Platform.Monitor;

// MARK: CPU

/// <summary>One logical processor's load, and whether it is an efficiency core (hybrid CPUs).</summary>
public sealed record CoreLoad(int Index, double? Usage, bool IsEfficiency);

public sealed class CpuSampler
{
    private readonly CpuUsageReader _total = new();
    private CpuTicks[]? _previousCores;
    private double _previousCoresAt;
    private readonly Lazy<bool[]> _efficiency = new(ReadEfficiencyClasses);

    public double? Usage => _total.LastUsage;
    public double? UsageReadAt => _total.LastReadAt;
    public int LogicalProcessors { get; } = Environment.ProcessorCount;

    /// <summary>One read: the aggregate busy share (null on the first read or after a long gap) and, when asked, each core's.</summary>
    public (double? Usage, IReadOnlyList<CoreLoad>? Cores) Read(bool perCore)
    {
        var now = Clock.UnbiasedSeconds();
        var ticks = ReadTicks();
        if (ticks is null) return (null, null);
        var total = new CpuTicks((ulong)ticks.Sum(t => (decimal)t.Busy), (ulong)ticks.Sum(t => (decimal)t.Total));
        var usage = _total.Read(total, now);
        IReadOnlyList<CoreLoad>? cores = null;
        if (perCore)
        {
            var efficiency = _efficiency.Value;
            var previous = _previousCores;
            if (previous is not null && previous.Length == ticks.Length && now - _previousCoresAt <= CpuUsageReader.MaximumGapSeconds)
                cores = ticks.Select((t, i) => new CoreLoad(i, CpuTicks.Usage(previous[i], t), i < efficiency.Length && efficiency[i])).ToList();
            _previousCores = ticks;
            _previousCoresAt = now;
        }
        else _previousCores = null;
        return (usage, cores);
    }

    public void Reset() { _total.Reset(); _previousCores = null; }

    /// <summary>Busy and total ticks per processor: kernel time includes idle, so busy = kernel - idle + user.</summary>
    private static CpuTicks[]? ReadTicks()
    {
        var count = Environment.ProcessorCount;
        var size = Marshal.SizeOf<ProcessorPerformance>();
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            if (NtQuerySystemInformation(8, buffer, (uint)(size * count), out var returned) != 0) return null;
            var read = (int)returned / size;
            var result = new CpuTicks[read];
            for (var i = 0; i < read; i++)
            {
                var item = Marshal.PtrToStructure<ProcessorPerformance>(IntPtr.Add(buffer, i * size));
                var idle = (ulong)item.IdleTime;
                var kernel = (ulong)item.KernelTime;
                var user = (ulong)item.UserTime;
                result[i] = new CpuTicks(kernel - Math.Min(kernel, idle) + user, kernel + user);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The CPU sets' efficiency class: on a hybrid CPU the lower class is the efficiency cores.</summary>
    private static bool[] ReadEfficiencyClasses()
    {
        var efficiency = new bool[Environment.ProcessorCount];
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
            if (length == 0) return efficiency;
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return efficiency;
                var classes = new Dictionary<int, byte>();
                for (var offset = 0; offset < length;)
                {
                    var entrySize = Marshal.ReadInt32(buffer, offset);
                    if (entrySize <= 0) break;
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                    {
                        // CpuSet: Id (4), Group (2), LogicalProcessorIndex, CoreIndex, LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass.
                        var group = Marshal.ReadInt16(buffer, offset + 12);
                        var logical = Marshal.ReadByte(buffer, offset + 14);
                        if (group == 0) classes[logical] = Marshal.ReadByte(buffer, offset + 18);
                    }
                    offset += entrySize;
                }
                if (classes.Count > 0 && classes.Values.Distinct().Count() > 1)
                {
                    var lowest = classes.Values.Min();
                    foreach (var (index, value) in classes)
                        if (index < efficiency.Length) efficiency[index] = value == lowest;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Debug(e, "CPU topology unavailable");
        }
        return efficiency;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformance
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint length, out uint returned, IntPtr process, uint flags);
}

/// <summary>Time that stops while the PC sleeps, so baselines and the alert gate never count a sleep as a stretch of load.</summary>
internal static class Clock
{
    public static double UnbiasedSeconds() => QueryUnbiasedInterruptTime(out var value) ? value / 10_000_000.0 : Environment.TickCount64 / 1000.0;

    [DllImport("kernel32.dll")] private static extern bool QueryUnbiasedInterruptTime(out ulong value);
}

// MARK: Memory

public sealed record MemoryReading(ulong Total, ulong Used, ulong App, ulong Cached, ulong Compressed, ulong CommitBytes, ulong CommitLimit,
                                   ulong Available, MemoryPressure Pressure);

public static class MemorySampler
{
    public static MemoryReading? Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GlobalMemoryStatusEx(ref status) || !GetPerformanceInfo(out performance, performance.Size)) return null;
        var page = (ulong)performance.PageSize;
        var total = status.TotalPhys;
        var available = Math.Min(total, status.AvailPhys);
        var used = MetricFormat.MemoryUsed(total, available);
        var cached = Math.Min(total, (ulong)performance.SystemCache * page);
        var compressed = CompressedBytes();
        var kernel = (ulong)performance.KernelTotal * page;
        // "App" memory: what is in use beyond the kernel and the compression store.
        var app = used > kernel + compressed ? used - kernel - compressed : 0;
        var commit = (ulong)performance.CommitTotal * page;
        var limit = (ulong)performance.CommitLimit * page;
        return new MemoryReading(total, used, app, cached, compressed, commit, limit, available, MemoryMath.Pressure(total, available, commit, limit));
    }

    private static ulong CompressedBytes()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Memory Compression");
            try { return processes.Length > 0 ? (ulong)processes[0].WorkingSet64 : 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("psapi.dll")] private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);
}

// MARK: Network

public sealed record AddressInfo(string Adapter, string Address);

public sealed class NetworkSampler
{
    private NetworkCounters? _previous;
    private double _previousAt;
    private ulong _sessionDown, _sessionUp;

    public (double Down, double Up)? Speed { get; private set; }
    public (ulong Down, ulong Up) Totals => (_sessionDown, _sessionUp);

    /// <summary>
    /// One read. A gap over 10 s or a changed adapter set makes the sample a baseline only, so a reconnection never
    /// shows as a burst of traffic; session totals add only what a normal step moved.
    /// </summary>
    public (double Down, double Up)? Read()
    {
        var now = Clock.UnbiasedSeconds();
        var current = ReadCounters();
        var previous = _previous;
        var elapsed = now - _previousAt;
        _previous = current;
        _previousAt = now;
        if (previous is null || elapsed > 10 || !previous.Interfaces.SetEquals(current.Interfaces)) { Speed = null; return null; }
        var speed = MetricFormat.NetworkSpeed(previous, current, elapsed);
        if (current.Received >= previous.Received) _sessionDown += current.Received - previous.Received;
        if (current.Sent >= previous.Sent) _sessionUp += current.Sent - previous.Sent;
        return Speed = speed;
    }

    private static NetworkCounters ReadCounters()
    {
        ulong received = 0, sent = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!NetworkRules.Counts(adapter.Description, (int)adapter.NetworkInterfaceType, adapter.OperationalStatus == OperationalStatus.Up)) continue;
                var statistics = adapter.GetIPStatistics();
                received += (ulong)Math.Max(0, statistics.BytesReceived);
                sent += (ulong)Math.Max(0, statistics.BytesSent);
                names.Add(adapter.Id);
            }
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException) { Log.Debug(e, "Network counters unavailable"); }
        return new NetworkCounters(received, sent, names);
    }

    /// <summary>Local IPv4 addresses of the adapters that count, with the adapter's friendly name.</summary>
    public static IReadOnlyList<AddressInfo> Addresses()
    {
        var result = new List<AddressInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!NetworkRules.Counts(adapter.Description, (int)adapter.NetworkInterfaceType, adapter.OperationalStatus == OperationalStatus.Up)) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        result.Add(new AddressInfo(adapter.Name, address.Address.ToString()));
            }
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException) { Log.Debug(e, "Addresses unavailable"); }
        return result;
    }
}

// MARK: Disk

public sealed record DiskVolume(string Name, string Label, string? FileSystem, ulong Total, ulong Free, bool IsSystem)
{
    public ulong Used => Total - Math.Min(Total, Free);
}

public sealed class DiskSampler : IDisposable
{
    private PdhQuery? _query;
    private int _read = -1, _write = -1;
    private bool _primed;

    /// <summary>Fixed, ready drives with their capacity.</summary>
    public static IReadOnlyList<DiskVolume> Volumes()
    {
        var result = new List<DiskVolume>();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var capacity = DiskRules.Capacity((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
                if (capacity is null) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                result.Add(new DiskVolume(drive.Name.TrimEnd('\\'), label, DiskRules.FileSystemLabel(drive.DriveFormat), (ulong)drive.TotalSize,
                    capacity.Value.Free, string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug(e, "Drive {Drive} unreadable", drive.Name); }
        }
        return result;
    }

    /// <summary>Read and write bytes per second across every physical disk (the first call only primes the counters).</summary>
    public (double Read, double Write)? Activity()
    {
        if (_query is null)
        {
            _query = new PdhQuery();
            _read = _query.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _write = _query.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _primed = false;
        }
        if (!_query.Collect()) return null;
        if (!_primed) { _primed = true; return null; }
        return _query.Value(_read) is { } read && _query.Value(_write) is { } write ? (Math.Max(0, read), Math.Max(0, write)) : null;
    }

    /// <summary>Forget the counters: after a gap the next reading is only a baseline.</summary>
    public void Reset() { _query?.Dispose(); _query = null; }

    public void Dispose() => Reset();
}

// MARK: GPU

public sealed record GpuAdapter(string Name, long LuidHigh, uint LuidLow, ulong DedicatedBytes, bool IsSoftware);

public sealed record GpuLoad(GpuAdapter Adapter, double Usage);

public sealed class GpuSampler : IDisposable
{
    private static readonly TimeSpan Reopen = TimeSpan.FromSeconds(30);
    private PdhQuery? _query;
    private int _engines = -1;
    private DateTime _openedAt;
    private bool _primed;
    private readonly Dictionary<(long, uint), double> _smoothed = [];
    private IReadOnlyList<GpuAdapter>? _adapters;

    public IReadOnlyList<GpuAdapter> Adapters => _adapters ??= GpuAdapters.List();

    /// <summary>
    /// One read of every adapter's busiest engine, the way Task Manager shows it, smoothed against single-sample spikes.
    /// The engine and process list is reopened every 30 s so engines that appeared since are counted.
    /// </summary>
    public IReadOnlyList<GpuLoad>? Read()
    {
        if (_query is null || DateTime.UtcNow - _openedAt > Reopen)
        {
            _query?.Dispose();
            _query = new PdhQuery();
            _engines = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
            _openedAt = DateTime.UtcNow;
            _primed = false;
        }
        if (_engines < 0 || !_query.Collect()) return null;
        if (!_primed) { _primed = true; return null; }

        var perEngine = new Dictionary<(long, uint, string), double>();
        foreach (var (instance, value) in _query.Instances(_engines))
        {
            if (!TryParse(instance, out var luid, out var engine)) continue;
            perEngine[(luid.High, luid.Low, engine)] = perEngine.GetValueOrDefault((luid.High, luid.Low, engine)) + value;
        }
        var loads = new List<GpuLoad>();
        foreach (var adapter in Adapters.Where(a => !a.IsSoftware))
        {
            var busiest = perEngine.Where(e => e.Key.Item1 == adapter.LuidHigh && e.Key.Item2 == adapter.LuidLow).Select(e => e.Value).DefaultIfEmpty(0).Max();
            var raw = Math.Clamp(busiest / 100, 0, 1);
            var key = (adapter.LuidHigh, adapter.LuidLow);
            var stable = MetricFormat.StabilizedGpuUsage(_smoothed.TryGetValue(key, out var previous) ? previous : null, raw);
            _smoothed[key] = stable;
            loads.Add(new GpuLoad(adapter, stable));
        }
        return loads;
    }

    /// <summary>Each process's GPU share (busiest engine, as a fraction), by pid, from the same counters.</summary>
    public IReadOnlyDictionary<int, double> ProcessUsage()
    {
        var result = new Dictionary<int, double>();
        if (_query is null || _engines < 0 || !_primed) return result;
        var perEngine = new Dictionary<(int, string), double>();
        foreach (var (instance, value) in _query.Instances(_engines))
        {
            if (!instance.StartsWith("pid_", StringComparison.Ordinal)) continue;
            var end = instance.IndexOf('_', 4);
            if (end < 0 || !int.TryParse(instance.AsSpan(4, end - 4), out var pid) || !TryParse(instance, out _, out var engine)) continue;
            perEngine[(pid, engine)] = perEngine.GetValueOrDefault((pid, engine)) + value;
        }
        foreach (var group in perEngine.GroupBy(e => e.Key.Item1))
            result[group.Key] = Math.Clamp(group.Max(e => e.Value), 0, 100);
        return result;
    }

    // pid_1234_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D
    private static bool TryParse(string instance, out (long High, uint Low) luid, out string engine)
    {
        luid = default;
        engine = "";
        var luidAt = instance.IndexOf("luid_0x", StringComparison.Ordinal);
        var engAt = instance.IndexOf("_eng_", StringComparison.Ordinal);
        if (luidAt < 0 || engAt < 0) return false;
        var parts = instance[(luidAt + 5)..].Split('_');
        if (parts.Length < 2 || !long.TryParse(parts[0].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var high)
            || !uint.TryParse(parts[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var low)) return false;
        luid = (high, low);
        engine = instance[(engAt + 1)..];
        return true;
    }

    public void Reset() { _query?.Dispose(); _query = null; _smoothed.Clear(); }

    public void Dispose() => Reset();
}

internal static class GpuAdapters
{
    public static IReadOnlyList<GpuAdapter> List()
    {
        var result = new List<GpuAdapter>();
        object? factoryObject = null;
        try
        {
            var iid = typeof(IDxgiFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factoryObject) != 0 || factoryObject is not IDxgiFactory1 factory) return result;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    adapter.GetDesc1(out var desc);
                    result.Add(new GpuAdapter(desc.Description.Trim(), desc.LuidHigh, desc.LuidLow, (ulong)desc.DedicatedVideoMemory, (desc.Flags & 2) != 0));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(e, "Graphics adapters could not be listed");
        }
        finally { if (factoryObject is not null) Marshal.ReleaseComObject(factoryObject); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiFactory1
    {
        // IDXGIObject (4), IDXGIFactory (5), then IDXGIFactory1: only the ones used are typed; the rest hold their places.
        [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters(); [PreserveSig] int MakeWindowAssociation(); [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain(); [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDxgiAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAdapter1
    {
        [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs(); [PreserveSig] int GetDesc(); [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
}
