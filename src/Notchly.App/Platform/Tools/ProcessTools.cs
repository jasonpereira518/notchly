// SPDX-License-Identifier: GPL-3.0-or-later
// Stopping processes and listing listening ports (upstream's KillProcessService and PortManagerService run kill(2) and
// lsof). A kill always checks that the process still has the creation time it was listed with, so a pid Windows has
// reused for another program is never touched. A process Notchly may not stop is offered one administrator prompt,
// whose command checks the creation time again before it stops anything.
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Notchly.Core.Tools;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Tools;

public static class ProcessTools
{
    private const uint ProcessTerminate = 0x0001, ProcessQueryLimited = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>The process's creation time as a FILETIME, or null when it is gone or cannot be read.</summary>
    public static long? StartTime(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimited, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try { return GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null; }
        finally { CloseHandle(handle); }
    }

    public enum Outcome { Done, Gone, Changed, AccessDenied, Failed }

    /// <summary>
    /// Stops a process. Graceful asks its windows to close; a process with none, or a forced kill, is terminated.
    /// Nothing happens unless the creation time still matches the one listed.
    /// </summary>
    public static Outcome Stop(int pid, long? listedStart, bool force)
    {
        var current = StartTime(pid);
        if (current is null) return Process_Exists(pid) ? Outcome.AccessDenied : Outcome.Gone;
        // A process that has exited can linger while something holds a handle to it: it is gone all the same.
        if (!IsRunning(pid)) return Outcome.Gone;
        if (!KillProcessSupport.SameProcess(listedStart, current)) return Outcome.Changed;
        if (!force && AskToClose(pid)) return Outcome.Done;
        var handle = OpenProcess(ProcessTerminate, false, (uint)pid);
        if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 ? Outcome.AccessDenied : Outcome.Gone;
        try { return TerminateProcess(handle, 1) ? Outcome.Done : Outcome.Failed; }
        finally { CloseHandle(handle); }
    }

    private static bool IsRunning(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimited, false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(handle, out var code) && code == 259; }
        finally { CloseHandle(handle); }
    }

    private static bool Process_Exists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Posts a close to the process's visible windows. False when it has none.</summary>
    private static bool AskToClose(int pid)
    {
        var asked = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                asked |= PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return asked;
    }

    /// <summary>
    /// One administrator prompt for the processes Notchly may not stop. The elevated command stops a process only
    /// if its creation time is still the one listed.
    /// </summary>
    public static bool StopElevated(IEnumerable<(int Pid, long Start)> targets)
    {
        var checks = string.Join("; ", targets.Select(t =>
            $"$p = Get-Process -Id {t.Pid} -ErrorAction SilentlyContinue; if ($p -and $p.StartTime.ToFileTimeUtc() -eq {t.Start}) {{ Stop-Process -Id {t.Pid} -Force }}"));
        if (checks.Length == 0) return true;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + checks.Replace("\"", "\\\"") + "\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(15000);
            return true;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // The prompt was declined.
            Log.Information("Elevated stop declined: {Message}", e.Message);
            return false;
        }
    }
}

/// <summary>The TCP ports something is listening on, from the kernel's table (what netstat reads).</summary>
public static class TcpTable
{
    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    /// <summary>The listeners, or null when the table could not be read.</summary>
    public static IReadOnlyList<(int Port, string Address, int Pid)>? Listeners()
    {
        var all = new List<(int, string, int)>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            var rows = Read(family);
            if (rows is null) return null;
            all.AddRange(rows);
        }
        return all;
    }

    private static List<(int, string, int)>? Read(int family)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (status == 122) continue; // The table grew while it was read: ask again with the new size.
                if (status != 0) return null;
                var count = Marshal.ReadInt32(buffer);
                var rows = new List<(int, string, int)>(count);
                // IPv4 rows are 24 bytes: state, address, port, remote address, remote port, pid.
                // IPv6 rows are 56 bytes: address, scope, port, remote address, remote scope, remote port, state, pid.
                var stride = family == AfInet ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * stride);
                    if (family == AfInet)
                    {
                        var address = new IPAddress((uint)Marshal.ReadInt32(row, 4)).ToString();
                        rows.Add((HostPort(Marshal.ReadInt32(row, 8)), address, Marshal.ReadInt32(row, 20)));
                    }
                    else
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        var scope = (uint)Marshal.ReadInt32(row, 16);
                        var ip = new IPAddress(bytes, scope);
                        rows.Add((HostPort(Marshal.ReadInt32(row, 20)), "[" + ip + "]", Marshal.ReadInt32(row, 52)));
                    }
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    /// <summary>The port is in network byte order in the row's low 16 bits.</summary>
    private static int HostPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
