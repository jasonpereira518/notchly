// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Metrics/MetricFormat.swift: number formatting for the monitor (binary
// byte sizes, decimal disk sizes, byte and bit rates, watts, percent, graph ceilings), the history ring and the
// network and disk speed maths. Numbers follow the reader's region, held in one place so a test can pin it.
using System.Globalization;

namespace Notchly.Core.Monitor;

/// <summary>Cumulative interface byte counters (since boot); another interface set reads as other counters.</summary>
public sealed record NetworkCounters(ulong Received, ulong Sent, IReadOnlySet<string> Interfaces)
{
    public static NetworkCounters Empty { get; } = new(0, 0, new HashSet<string>());
}

public readonly record struct DiskIoCounters(ulong Read, ulong Written);

public static class MetricFormat
{
    public static CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    private static double RoundHalfUp(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static string Fixed(double value, int digits) => value.ToString("F" + digits, Culture);

    // MARK: Memory

    /// <summary>Memory in use: what Windows holds that is not available to start something with.</summary>
    public static ulong MemoryUsed(ulong totalBytes, ulong availableBytes) =>
        totalBytes == 0 ? 0 : totalBytes - Math.Min(totalBytes, availableBytes);

    /// <summary>One persisted choice drives every memory surface; unknown values fall back to total memory in use.</summary>
    public static T SelectedMemory<T>(T used, T app, string metric) => metric == "app" ? app : used;

    /// <summary>Memory keeps its slot while a sample is unavailable, so the readout does not come and go.</summary>
    public static string MemoryPercentText(ulong? used, ulong? total) =>
        used is { } u && total is { } t && t > 0 ? Percent((double)u / t) : "--%";

    // MARK: Byte sizes

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>A byte count as a value and unit, base 1024; a value that only crosses 1024 once rounded reads as the larger unit.</summary>
    public static (double Value, string Unit) Scale(double bytes)
    {
        var value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < ByteUnits.Length - 1) { value /= 1024; index++; }
        while (index < ByteUnits.Length - 1 && RoundHalfUp(value) >= 1024 && (index == 0 || value >= 10)) { value /= 1024; index++; }
        return (value, ByteUnits[index]);
    }

    private static string Number(double value, string unit) =>
        unit == "B" ? Fixed(value, 0) : value < 10 ? Fixed(value, 1) : Fixed(value, 0);

    public static string Bytes(ulong bytes)
    {
        var (value, unit) = Scale(bytes);
        return $"{Number(value, unit)} {unit}";
    }

    /// <summary>Storage vendors count in thousands, as Windows' own disk sizes do.</summary>
    public static string DiskBytes(ulong bytes) => Decimal(bytes, precise: false);

    public static string DiskBytesPrecise(ulong bytes) => Decimal(bytes, precise: true);

    private static string Decimal(ulong bytes, bool precise)
    {
        var value = Math.Max(0, (double)bytes);
        var index = 0;
        while (value >= 1000 && index < ByteUnits.Length - 1) { value /= 1000; index++; }
        var unit = ByteUnits[index];
        if (unit == "B") return $"{Fixed(value, 0)} B";
        if (precise && unit is "TB" or "PB") return $"{Fixed(value, 2)} {unit}";
        return value < 10 ? $"{Fixed(value, 1)} {unit}" : $"{Fixed(value, 0)} {unit}";
    }

    public static string BytesPerSecond(double bytesPerSecond)
    {
        var (value, unit) = Scale(bytesPerSecond);
        return $"{Number(value, unit)} {unit}/s";
    }

    /// <summary>A compact throughput for tight spaces: "1.2M", "320K", "0B".</summary>
    public static string BytesPerSecondCompact(double bytesPerSecond)
    {
        string[] units = ["B", "K", "M", "G", "T", "P"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        while (index < units.Length - 1)
        {
            if (index == 0) { if (RoundHalfUp(value) < 1024) break; }
            else if (value >= 10) { if (RoundHalfUp(value) < 1024) break; }
            else break;
            value /= 1024;
            index++;
        }
        if (index == 0) return $"{(long)RoundHalfUp(value)}B";
        if (value < 10)
        {
            var rounded = RoundHalfUp(value * 10) / 10;
            return rounded >= 10 ? $"{(long)RoundHalfUp(rounded)}{units[index]}" : $"{Fixed(rounded, 1)}{units[index]}";
        }
        return $"{(long)RoundHalfUp(value)}{units[index]}";
    }

    /// <summary>A network bit rate in decimal steps, as links are quoted: "12 Mbps".</summary>
    public static string BitsPerSecond(double bytesPerSecond)
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps", "Pbps"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) * 8 : 0;
        var index = 0;
        while (index < units.Length - 1 && RoundHalfUp(value) >= 1000) { value /= 1000; index++; }
        if (index == 0) return $"{Fixed(value, 0)} {units[index]}";
        return value < 10 ? $"{Fixed(value, 1)} {units[index]}" : $"{Fixed(value, 0)} {units[index]}";
    }

    /// <summary>A compact bit rate, never wider than "999Kb": "9.6Mb", "320Kb", "0b".</summary>
    public static string BitsPerSecondCompact(double bytesPerSecond)
    {
        string[] units = ["b", "Kb", "Mb", "Gb", "Tb", "Pb"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) * 8 : 0;
        var index = 0;
        while (index < units.Length - 1)
        {
            var shown = value < 10 && index > 0 ? RoundHalfUp(value * 10) / 10 : RoundHalfUp(value);
            if (shown < 1000) break;
            value /= 1000;
            index++;
        }
        if (index == 0) return $"{(long)RoundHalfUp(value)}b";
        if (value < 10)
        {
            var rounded = RoundHalfUp(value * 10) / 10;
            return rounded >= 10 ? $"{(long)RoundHalfUp(rounded)}{units[index]}" : $"{Fixed(rounded, 1)}{units[index]}";
        }
        return $"{(long)RoundHalfUp(value)}{units[index]}";
    }

    public static string NetworkRate(double bytesPerSecond, bool inBits) => inBits ? BitsPerSecond(bytesPerSecond) : BytesPerSecond(bytesPerSecond);

    public static string NetworkRateCompact(double bytesPerSecond, bool inBits) =>
        inBits ? BitsPerSecondCompact(bytesPerSecond) : BytesPerSecondCompact(bytesPerSecond);

    /// <summary>Only an explicit choice of bits reads as bits; anything else keeps the bytes people always saw.</summary>
    public static bool InBits(string? savedUnit) => savedUnit == "bits";

    // MARK: Graphs, watts and percentages

    private static readonly double[] CeilingSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500];

    /// <summary>The top of an auto-scaled graph: the peak rounded up to a round number of a unit (1024 for rates, 1000 for watts).</summary>
    public static double GraphCeiling(double peak, double unitStep)
    {
        for (var power = 0; power < 6; power++)
            foreach (var step in CeilingSteps)
            {
                var ceiling = step * Math.Pow(unitStep, power);
                if (ceiling >= peak) return ceiling;
            }
        return peak;
    }

    /// <summary>A network graph ceiling in bytes per second that labels as a round number in the chosen unit.</summary>
    public static double NetworkGraphCeiling(double peakBytesPerSecond, bool inBits) =>
        inBits ? GraphCeiling(peakBytesPerSecond * 8, 1000) / 8 : GraphCeiling(peakBytesPerSecond, 1024);

    public static string Watts(double value) => Math.Abs(value) < 10 ? $"{Fixed(value, 1)} W" : $"{Fixed(value, 0)} W";

    public static string WattsCompact(double value) => $"{Fixed(RoundHalfUp(value), 0)}W";

    public static string Percent(double fraction) => $"{(int)RoundHalfUp(Math.Clamp(fraction, 0, 1) * 100)}%";

    /// <summary>Process rows stay on the same 0…100 scale as the aggregate meters.</summary>
    public static double BoundedPercentage(double percentage) => double.IsFinite(percentage) ? Math.Clamp(percentage, 0, 100) : 0;

    /// <summary>A process's CPU-time delta as its share of the machine over the same wall-clock interval.</summary>
    public static double ProcessCpuPercentage(ulong previousNanoseconds, ulong currentNanoseconds, double elapsedSeconds, int processorCount)
    {
        if (currentNanoseconds < previousNanoseconds || elapsedSeconds <= 0 || !double.IsFinite(elapsedSeconds)) return 0;
        var capacity = elapsedSeconds * 1_000_000_000 * Math.Max(1, processorCount);
        return BoundedPercentage((currentNanoseconds - previousNanoseconds) / capacity * 100);
    }

    /// <summary>Sampling APIs do not share a clock; process rows never claim more than the matching aggregate meter.</summary>
    public static double ProcessReconciliationScale(double sampledTotal, double? aggregatePercentage)
    {
        if (!double.IsFinite(sampledTotal) || sampledTotal <= 0 || aggregatePercentage is not { } aggregate || !double.IsFinite(aggregate)) return 1;
        return Math.Min(1, BoundedPercentage(aggregate) / sampledTotal);
    }

    /// <summary>Hides one-sample compositor spikes without hiding sustained load: a rise is capped at +0.20, a fall blends.</summary>
    public static double StabilizedGpuUsage(double? previous, double current)
    {
        var value = double.IsFinite(current) ? Math.Clamp(current, 0, 1) : 0;
        if (previous is not { } before || !double.IsFinite(before)) return value;
        var baseline = Math.Clamp(before, 0, 1);
        return value > baseline ? Math.Min(value, baseline + 0.20) : baseline * 0.35 + value * 0.65;
    }

    public static string Uptime(long seconds)
    {
        var total = Math.Max(0, seconds);
        var days = total / 86_400;
        var hours = total % 86_400 / 3_600;
        var minutes = total % 3_600 / 60;
        if (days > 0) return $"{days}d {hours}h";
        return hours > 0 ? $"{hours}h {minutes}min" : $"{minutes}min";
    }

    // MARK: Network and disk speed

    /// <summary>Per-second download and upload from two cumulative readings; a counter that went back reads 0 for the tick, never a spike.</summary>
    public static (double Down, double Up) NetworkSpeed(NetworkCounters previous, NetworkCounters current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return (0, 0);
        var down = current.Received >= previous.Received ? (current.Received - previous.Received) / elapsedSeconds : 0;
        var up = current.Sent >= previous.Sent ? (current.Sent - previous.Sent) / elapsedSeconds : 0;
        return (down, up);
    }

    public static (double Read, double Write) DiskSpeed(DiskIoCounters previous, DiskIoCounters current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return (0, 0);
        var read = current.Read >= previous.Read ? (current.Read - previous.Read) / elapsedSeconds : 0;
        var write = current.Written >= previous.Written ? (current.Written - previous.Written) / elapsedSeconds : 0;
        return (read, write);
    }
}

/// <summary>Fixed-size ring of recent samples (oldest to newest) for the history graphs.</summary>
public sealed class MetricHistory(int capacity)
{
    private readonly List<double> _values = [];
    public int Capacity { get; } = Math.Max(1, capacity);
    public IReadOnlyList<double> Values => _values;

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity) _values.RemoveRange(0, _values.Count - Capacity);
    }

    /// <summary>Graph data matters only while a graph shows; at rest nothing is copied and the ring stays intact.</summary>
    public IReadOnlyList<double> PublishedValues(bool whileVisible) => whileVisible ? _values.ToList() : [];
}
