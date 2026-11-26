// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the list of windows the screenshot overlay can pick, in place of upstream's ScreenCaptureKit window list
// (ScreenshotCaptureEngine.pickableWindows). EnumWindows hands them over top-most first, so the order is the z-order the pick uses.
using System.Diagnostics;
using Notchly.App.Platform.Windowing;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Capture;

/// <summary>A window that can be picked: where it is on the screen (in pixels, without the invisible resize margins), and who owns it.</summary>
public sealed record CapturableWindow(IntPtr Handle, RECT Bounds, string Title, int ProcessId, int ZIndex, string ProcessName)
{
    public bool Contains(int x, int y) => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}

public static class CapturableWindows
{
    private const int DwmaExtendedFrameBounds = 9;
    private const int MinimumSide = 24;

    /// <summary>Visible, un-minimised, un-cloaked top-level windows, top-most first, without this app's own windows.</summary>
    public static IReadOnlyList<CapturableWindow> List(bool includeOwn = false)
    {
        var self = Environment.ProcessId;
        var result = new List<CapturableWindow>();
        var z = 0;
        EnumWindows((hwnd, _) =>
        {
            z++;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            // Tool windows and click-through decorations (borders, overlays other apps draw) are not windows a person means.
            if ((style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0) return true;
            if ((style & WS_EX_TRANSPARENT) != 0 && (style & WS_EX_LAYERED) != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!includeOwn && pid == self) return true;
            if (DwmGetWindowRect(hwnd, DwmaExtendedFrameBounds, out var rect, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(hwnd, out rect)) return true;
            if (rect.Width < MinimumSide || rect.Height < MinimumSide) return true;
            var title = WindowText(hwnd);
            var cls = ClassName(hwnd);
            // The desktop and the taskbar are not windows to capture.
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            result.Add(new CapturableWindow(hwnd, rect, title, (int)pid, z, ProcessName((int)pid)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The top-most window holding a screen point (pixels).</summary>
    public static CapturableWindow? At(IReadOnlyList<CapturableWindow> windows, int x, int y) =>
        windows.OrderBy(w => w.ZIndex).FirstOrDefault(w => w.Contains(x, y));

    private static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }
}
