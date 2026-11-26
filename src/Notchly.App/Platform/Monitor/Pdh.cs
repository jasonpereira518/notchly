// SPDX-License-Identifier: GPL-3.0-or-later
// A small wrapper over Windows' performance data helper (pdh.dll), used for the counters Task Manager reads: GPU engine
// utilization (per adapter and per process) and physical-disk throughput. A wildcard path is expanded when it is added,
// so a query is reopened now and then to pick up engines and processes that appeared since.
using System.Runtime.InteropServices;
using Serilog;

namespace Notchly.App.Platform.Monitor;

internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x200, PdhMoreData = 0x800007D2;
    private IntPtr _query;
    private readonly List<IntPtr> _counters = [];

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Adds a counter by its English path (so it works in any display language); returns its index or -1.</summary>
    public int Add(string path)
    {
        if (!IsOpen || PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != 0) return -1;
        _counters.Add(counter);
        return _counters.Count - 1;
    }

    /// <summary>Takes one sample; rate counters need two samples before they read.</summary>
    public bool Collect() => IsOpen && PdhCollectQueryData(_query) == 0;

    /// <summary>The current value of a counter that has no instances (like a total).</summary>
    public double? Value(int counter)
    {
        if (counter < 0 || counter >= _counters.Count) return null;
        return PdhGetFormattedCounterValue(_counters[counter], PdhFmtDouble, out _, out var value) == 0 && value.CStatus is 0 or 0x00000001
            ? value.Double : null;
    }

    /// <summary>Every instance of a wildcard counter with its value.</summary>
    public IReadOnlyList<(string Instance, double Value)> Instances(int counter)
    {
        var result = new List<(string, double)>();
        if (counter < 0 || counter >= _counters.Count) return result;
        uint size = 0, count = 0;
        var status = PdhGetFormattedCounterArrayW(_counters[counter], PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counters[counter], PdhFmtDouble, ref size, ref count, buffer) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then a status and a double (24 bytes on x64).
            var stride = IntPtr.Size == 8 ? 24 : 16;
            for (var i = 0; i < count; i++)
            {
                var item = IntPtr.Add(buffer, i * stride);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                var itemStatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if (itemStatus is 0 or 1) result.Add((name, value));
            }
        }
        catch (Exception e) when (e is AccessViolationException or ArgumentException)
        {
            Log.Debug(e, "A performance counter array could not be read");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counters.Clear();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
