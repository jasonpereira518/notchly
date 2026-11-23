// SPDX-License-Identifier: GPL-3.0-or-later
// Audio sessions on the default output, with the process behind each one: what the live
// equalizer uses to find the player's process (and what the M4 mixer will list). Windows
// stand-in for upstream's AppVolumeMixer.audioProcessObjects + ResponsibleProcess.
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Notchly.Core.Notch;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace Notchly.App.Platform.Audio;

public sealed record AudioSessionInfo(uint ProcessId, string ExeName, string? PackageFamilyName, bool IsActive);

public static class AudioSessions
{
    /// <summary>Sessions of real apps (not system sounds) on the default multimedia output.</summary>
    public static IReadOnlyList<AudioSessionInfo> List()
    {
        var result = new List<AudioSessionInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null) return result;
            using (device)
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    var pid = session.GetProcessID;
                    if (pid == 0) continue;
                    var (exe, family) = Describe(pid);
                    if (exe.Length == 0) continue;
                    result.Add(new AudioSessionInfo(pid, exe, family, session.State == AudioSessionState.AudioSessionStateActive));
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Debug(e, "Could not list audio sessions");
        }
        return result;
    }

    /// <summary>
    /// The process to capture for a media session's app id: the matching session's process,
    /// raised to the top of its same-named parent chain so a browser's whole tree is heard. With
    /// no match, the only playing app if there is exactly one; otherwise none (keep synthetic bars).
    /// </summary>
    public static uint? FindPlayerProcess(string? appUserModelId)
    {
        var sessions = List();
        var parents = ParentMap();
        var matched = sessions.Where(s => PlayerProcessMatch.Matches(appUserModelId, s.ExeName, s.PackageFamilyName))
                              .Select(s => Root(s.ProcessId, s.ExeName, parents)).Distinct().ToList();
        if (matched.Count >= 1) return matched[0];
        var playing = sessions.Where(s => s.IsActive).Select(s => Root(s.ProcessId, s.ExeName, parents)).Distinct().ToList();
        return playing.Count == 1 ? playing[0] : null;
    }

    private static uint Root(uint pid, string exe, Dictionary<uint, (uint Parent, string Exe)> parents)
    {
        var current = pid;
        for (var depth = 0; depth < 16 && parents.TryGetValue(current, out var entry); depth++)
        {
            if (!parents.TryGetValue(entry.Parent, out var parent) || !parent.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) break;
            current = entry.Parent;
        }
        return current;
    }

    // MARK: Win32

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    private static (string Exe, string? Family) Describe(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return ("", null);
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            var exe = QueryFullProcessImageName(handle, 0, path, ref size) ? Path.GetFileName(path.ToString()) : "";
            uint length = 0;
            string? family = null;
            if (GetPackageFamilyName(handle, ref length, null) == 122 /* ERROR_INSUFFICIENT_BUFFER */ && length > 0)
            {
                var name = new StringBuilder((int)length);
                if (GetPackageFamilyName(handle, ref length, name) == 0) family = name.ToString();
            }
            return (exe, family);
        }
        finally { CloseHandle(handle); }
    }

    private static Dictionary<uint, (uint Parent, string Exe)> ParentMap()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do map[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile);
            while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return map;
    }
}
