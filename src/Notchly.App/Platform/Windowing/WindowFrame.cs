// SPDX-License-Identifier: GPL-3.0-or-later
// Moving and sizing other apps' windows the way a layout needs it. A window's own rectangle includes an invisible border (about
// 8 pixels on three sides), so a layout is placed by what shows: the visible frame DWM reports is compared with the window
// rectangle and the difference is added back. A maximized or minimized window is restored first, and a window that moves
// to a display with another scale is told twice, since most apps only settle their size on the second call.
using System.Runtime.InteropServices;
using Notchly.Core.Windowing;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public static class WindowFrame
{
    private const int DwmExtendedFrameBounds = 9;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, SwpAsync = 0x4000;
    private const int SwRestore = 9, SwMaximize = 3;
    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>The frame that shows, without the invisible border.</summary>
    public static WindowRect? Visible(IntPtr hwnd)
    {
        if (DwmGetWindowRect(hwnd, DwmExtendedFrameBounds, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            return new WindowRect(r.Left, r.Top, r.Width, r.Height);
        return GetWindowRect(hwnd, out var w) ? new WindowRect(w.Left, w.Top, w.Width, w.Height) : null;
    }

    public static bool IsMaximized(IntPtr hwnd) => IsZoomed(hwnd);

    /// <summary>The top-level window under a screen point, or zero.</summary>
    public static IntPtr TopLevelAt(int x, int y)
    {
        var hit = WindowFromPoint(new POINT { X = x, Y = y });
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
    }

    /// <summary>A window a layout may move: a visible, uncloaked application window that is not the desktop, the taskbar or one of Notchly's own.</summary>
    public static bool IsManageable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsCloaked(hwnd)) return false;
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow() || ShellClasses.Contains(ClassName(hwnd))) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return false;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return (style & WS_EX_TOOLWINDOW) == 0 || (style & WS_EX_APPWINDOW) != 0;
    }

    /// <summary>The window a layout command acts on: the one in front, if it can be moved.</summary>
    public static IntPtr Front()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hwnd, GaRoot);
        return IsManageable(root) ? root : IntPtr.Zero;
    }

    public static string Key(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return $"{pid}:{hwnd.ToInt64()}";
    }

    /// <summary>The executable name of the window's program, such as "chrome.exe".</summary>
    public static string? ProgramOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? System.IO.Path.GetFileName(new string(buffer, 0, (int)size)).ToLowerInvariant() : null;
        }
        finally { CloseHandle(process); }
    }

    public static DisplayInfo? DisplayOf(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return Displays.All().FirstOrDefault(d => d.Handle == monitor);
    }

    public static WindowRect WorkArea(DisplayInfo display) => new(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
    public static WindowRect Bounds(DisplayInfo display) => new(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height);

    /// <summary>
    /// Puts the visible frame of a window at a rectangle. <paramref name="moveOnly"/> leaves the size alone (the gesture's move).
    /// </summary>
    public static bool Place(IntPtr hwnd, WindowRect visible, bool async = false, bool moveOnly = false)
    {
        if (!IsWindow(hwnd)) return false;
        if (!moveOnly && (IsZoomed(hwnd) || IsIconic(hwnd))) ShowWindow(hwnd, SwRestore);
        if (!GetWindowRect(hwnd, out var window)) return false;
        var frame = Visible(hwnd) ?? new WindowRect(window.Left, window.Top, window.Width, window.Height);
        var left = (int)Math.Round(frame.X - window.Left);
        var top = (int)Math.Round(frame.Y - window.Top);
        var right = (int)Math.Round(window.Right - frame.Right);
        var bottom = (int)Math.Round(window.Bottom - frame.Bottom);
        var flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | (async ? SwpAsync : 0u) | (moveOnly ? SwpNoSize : 0u);
        return SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(visible.X) - left, (int)Math.Round(visible.Y) - top,
                            (int)Math.Round(visible.Width) + left + right, (int)Math.Round(visible.Height) + top + bottom, flags);
    }

    public static void Maximize(IntPtr hwnd) => ShowWindow(hwnd, IsZoomed(hwnd) ? SwRestore : SwMaximize);

    public static bool MoveCursorTo(int x, int y) => SetCursorPos(x, y);
}
