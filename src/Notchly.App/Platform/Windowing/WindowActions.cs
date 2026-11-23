// SPDX-License-Identifier: GPL-3.0-or-later
// The menu bar's window commands, done to the window itself (WM_SYSCOMMAND, SetWindowPos) rather than
// with keys: minimize, maximize or restore, snap to half the display, close, hide and quit the app.
using Notchly.Core.MenuBar;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public static class WindowActions
{
    public static void Perform(WindowAction action, ForegroundApp app)
    {
        var window = app.Window;
        switch (action)
        {
            case WindowAction.Minimize: SysCommand(window, SC_MINIMIZE); break;
            case WindowAction.ToggleMaximize: SysCommand(window, IsZoomed(window) ? SC_RESTORE : SC_MAXIMIZE); break;
            case WindowAction.Close: SysCommand(window, SC_CLOSE); break;
            case WindowAction.SnapLeft: Snap(window, left: true); break;
            case WindowAction.SnapRight: Snap(window, left: false); break;
            case WindowAction.HideApp:
                foreach (var sibling in ForegroundApps.SiblingWindows(app)) SysCommand(sibling, SC_MINIMIZE);
                break;
            case WindowAction.QuitApp:
                // As the taskbar's "Close all windows": each window is asked, so unsaved work still prompts.
                foreach (var sibling in ForegroundApps.SiblingWindows(app)) PostMessage(sibling, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    private static void SysCommand(IntPtr window, uint command)
    {
        if (!PostMessage(window, WM_SYSCOMMAND, (IntPtr)command, IntPtr.Zero)) Log.Debug("Window command refused");
    }

    /// <summary>
    /// Half of the display's work area. Windows draws invisible resize borders around most windows, so the
    /// rectangle grows by them and the visible frame lands exactly on the half.
    /// </summary>
    private static void Snap(IntPtr window, bool left)
    {
        if (IsZoomed(window) || IsIconic(window)) ShowWindow(window, SW_RESTORE);
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        var half = work.Width / 2;
        var target = new RECT { Left = left ? work.Left : work.Left + half, Top = work.Top, Right = left ? work.Left + half : work.Right, Bottom = work.Bottom };
        if (GetWindowRect(window, out var outer)
            && DwmGetWindowRect(window, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
        {
            target.Left -= frame.Left - outer.Left;
            target.Top -= frame.Top - outer.Top;
            target.Right += outer.Right - frame.Right;
            target.Bottom += outer.Bottom - frame.Bottom;
        }
        SetWindowPos(window, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
