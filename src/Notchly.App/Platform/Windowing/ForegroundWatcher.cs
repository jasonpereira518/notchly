// SPDX-License-Identifier: GPL-3.0-or-later
// Foreground-window changes (SetWinEventHook) and full-screen detection: the Windows version of
// upstream's NotchAppFullscreen.swift. Must be created on a thread with a message loop (the UI).
using System.Diagnostics;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;

    public event Action<IntPtr>? ForegroundChanged;

    public ForegroundWatcher()
    {
        _proc = (_, _, hwnd, _, _, _, _) => ForegroundChanged?.Invoke(hwnd);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}

public static class FullscreenDetector
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// Whether a full-screen app, game, video or presentation covers `display`: the shell's own
    /// notification state first (D3D exclusive, presentation mode), then any visible foreground
    /// window that covers the whole monitor (borderless games, F11 browsers).
    /// </summary>
    public static bool IsFullscreen(DisplayInfo display)
    {
        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
        {
            return true;
        }
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == GetShellWindow() || foreground == GetDesktopWindow()) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == OwnProcessId || !IsWindowVisible(foreground) || IsCloaked(foreground)) return false;
        if (ShellClasses.Contains(ClassName(foreground))) return false;
        if (!GetWindowRect(foreground, out var rect)) return false;
        var monitor = display.Bounds;
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    public static string ForegroundDescription()
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var pid);
        try { return $"{Process.GetProcessById((int)pid).ProcessName} ({ClassName(foreground)})"; }
        catch (ArgumentException) { return ClassName(foreground); }
    }
}
