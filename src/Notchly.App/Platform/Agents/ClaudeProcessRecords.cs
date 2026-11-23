// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream AgentSessionRegistry.read and isRunning (Services/AgentUsage/AgentUsageStore.swift): the
// sessions\<pid>.json records Claude Code keeps beside its logs while it runs. On Windows a record names its
// domain "win32:<computer>" and the process's creation time as a FILETIME ("procStart"), which tells the recorded
// process from a later one given the same id; kill(pid, 0) becomes OpenProcess and GetProcessTimes.
using System.IO;
using System.Text.RegularExpressions;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

internal static partial class ClaudeProcessRecords
{
    /// <summary>The process domain Claude Code writes for this computer.</summary>
    public static string Domain => "win32:" + Environment.MachineName;

    [GeneratedRegex(@"^\d+\.json$")]
    private static partial Regex RecordName();

    /// <summary>The registry from every <c>&lt;pid&gt;.json</c> in <paramref name="folders"/>.</summary>
    public static AgentSessionRegistry Read(IEnumerable<string> folders)
    {
        var records = new List<string?>();
        var listed = false;
        foreach (var folder in folders)
        {
            IEnumerable<string> names;
            try { names = Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OfType<string>().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            listed = true;
            foreach (var name in names.Where(n => RecordName().IsMatch(n))) records.Add(ReadRecord(Path.Combine(folder, name)));
        }
        return AgentSessionRegistry.FromRecords(records, IsRunning, listed, Domain);
    }

    private static string? ReadRecord(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length >= 1 << 16) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether <paramref name="pid"/> runs, and is the process created at <paramref name="procStart"/> (a FILETIME)
    /// when the record says. A process this user may not query still runs.</summary>
    public static bool IsRunning(int pid, string? procStart)
    {
        if (pid <= 0) return false;
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED;
        try
        {
            if (Native.GetExitCodeProcess(handle, out var code) && code != Native.STILL_ACTIVE) return false;
            if (procStart is null || !long.TryParse(procStart, out var expected)) return true;
            return !Native.GetProcessTimes(handle, out var created, out _, out _, out _) || created == expected;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
