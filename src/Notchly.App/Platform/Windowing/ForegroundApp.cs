// SPDX-License-Identifier: GPL-3.0-or-later
// The app behind a window, as the menu bar names it: its process, the name its program gives itself,
// whether it runs as administrator (Windows then refuses other apps' keys and commands to it) and
// whether the window is maximized.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Notchly.Core.MenuBar;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public sealed record ForegroundApp(IntPtr Window, uint ProcessId, string ProcessName, string ClassName, string Name,
                                   bool Elevated, bool Maximized);

public static class ForegroundApps
{
    private static readonly ConcurrentDictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static ForegroundApp? Describe(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var pid);
        var className = ClassName(window);
        var path = ImagePath(pid);
        var processName = path is null ? "" : Path.GetFileNameWithoutExtension(path);
        var description = path is null ? null : Descriptions.GetOrAdd(path, p =>
        {
            try { return FileVersionInfo.GetVersionInfo(p).FileDescription; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        });
        var name = ForegroundRules.DisplayName(description, processName, WindowText(window));
        return new ForegroundApp(window, pid, processName, className, name, IsElevated(pid), IsZoomed(window));
    }

    private static string? ImagePath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>An app whose token Notchly may not even read is treated as elevated: it is out of Notchly's reach.</summary>
    private static bool IsElevated(uint pid)
    {
        if (pid == Environment.ProcessId) return false;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return true;
            try { return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// The app's visible top-level windows of the same kind as <paramref name="app"/>'s: the same process
    /// and window class, so File Explorer never reaches the taskbar and one Store app never its neighbours
    /// in ApplicationFrameHost.
    /// </summary>
    public static IReadOnlyList<IntPtr> SiblingWindows(ForegroundApp app)
    {
        if (string.Equals(app.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return [app.Window];
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == app.ProcessId && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && !IsCloaked(hwnd)
                && ClassName(hwnd) == app.ClassName)
                windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        if (!windows.Contains(app.Window)) windows.Add(app.Window);
        return windows;
    }
}
