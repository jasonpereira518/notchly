// SPDX-License-Identifier: GPL-3.0-or-later
// The windows the switcher offers (upstream's WindowEnumerator over the accessibility tree): the ones Alt+Tab would show. A window
// counts when it is visible, has no owner (or asks for a taskbar button), is not a tool window and is not cloaked, as
// Windows' own switcher decides. Windows on other virtual desktops are cloaked by the shell, and are told apart from the ghost windows of
// suspended Store apps by their desktop id. Store apps live in ApplicationFrameHost windows, whose real program is the child "core
// window"; that child gives the executable and the icon.
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public sealed record SwitchWindow(IntPtr Window, int Pid, string AppId, string AppName, string Title, string? ExePath, bool IsMinimized, bool OnOtherDesktop, IntPtr Monitor);

public static class WindowInventory
{
    private const string FrameHost = "applicationframehost.exe";

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid desktop);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")] private class VirtualDesktopManagerClass { }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    private static readonly Lazy<IVirtualDesktopManager?> Desktops = new(() =>
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
        catch (Exception e) { Log.Debug(e, "Virtual desktops are not available"); return null; }
    });

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The windows in front-to-back order, as Alt+Tab would list them (every desktop, or the current one only).</summary>
    public static IReadOnlyList<SwitchWindow> List(bool currentDesktopOnly)
    {
        var result = new List<SwitchWindow>();
        var seen = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (Build(hwnd, currentDesktopOnly) is { } window && seen.Add(hwnd)) result.Add(window);
            }
            catch (Exception e) { Log.Debug(e, "A window could not be read"); }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static SwitchWindow? Build(IntPtr hwnd, bool currentDesktopOnly)
    {
        if (!IsWindowVisible(hwnd) && !IsIconic(hwnd)) return null;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var owner = GetWindow(hwnd, GW_OWNER);
        if ((style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0) return null;
        if (owner != IntPtr.Zero && (style & WS_EX_APPWINDOW) == 0) return null;
        var title = WindowText(hwnd);
        if (title.Length == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return null;
        var onOther = false;
        if (IsCloaked(hwnd))
        {
            // Another desktop's window is cloaked by the shell; a suspended Store app's ghost is cloaked on this desktop.
            if (!IsOnOtherDesktop(hwnd) || currentDesktopOnly) return null;
            onOther = true;
        }
        var className = ClassName(hwnd);
        var realPid = (int)pid;
        if (className == "ApplicationFrameWindow")
        {
            var core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core == IntPtr.Zero) return null;
            GetWindowThreadProcessId(core, out var corePid);
            realPid = (int)corePid;
        }
        var path = ImagePath((uint)realPid);
        var exe = path is null ? "" : Path.GetFileName(path).ToLowerInvariant();
        if (exe.Length == 0 || exe == FrameHost) return null;
        return new SwitchWindow(hwnd, realPid, exe, AppName(path!, exe), title, path, IsIconic(hwnd), onOther, MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
    }

    private static bool IsOnOtherDesktop(IntPtr hwnd)
    {
        if (Desktops.Value is not { } manager) return false;
        return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var current) == 0 && !current;
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

    private static string AppName(string path, string exe)
    {
        if (Names.TryGetValue(path, out var cached)) return cached;
        string name;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            name = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? Path.GetFileNameWithoutExtension(exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { name = Path.GetFileNameWithoutExtension(exe); }
        return Names[path] = name;
    }

    /// <summary>The window in front, as a root window.</summary>
    public static IntPtr Foreground()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, 2);
    }

    /// <summary>Brings a window forward, restoring it if it was minimized (and, for a window on another desktop, going there).</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        SwitchToThisWindow(hwnd, true);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool alt);
}

/// <summary>Which windows were in front recently, to order the switcher: the newest first, up to 256, kept current by the foreground events.</summary>
public sealed class WindowUseTracker : IDisposable
{
    private readonly ForegroundWatcher _watcher = new();
    private readonly List<long> _history = [];
    private readonly List<int> _apps = [];

    public WindowUseTracker()
    {
        _watcher.ForegroundChanged += OnForeground;
        // Starts with what is in front now.
        OnForeground(WindowInventory.Foreground());
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var root = GetAncestor(hwnd, 2);
        if (root == IntPtr.Zero || TitleOf(root) is { Length: 0 }) return;
        Record(root);
    }

    private static string TitleOf(IntPtr hwnd) => Native.WindowText(hwnd);

    public void Record(IntPtr hwnd)
    {
        var id = hwnd.ToInt64();
        _history.Remove(id);
        _history.Insert(0, id);
        if (_history.Count > Notchly.Core.Windowing.SwitcherSupport.HistoryLimit) _history.RemoveAt(_history.Count - 1);
        GetWindowThreadProcessId(hwnd, out var pid);
        _apps.Remove((int)pid);
        _apps.Insert(0, (int)pid);
    }

    /// <summary>Window handles, newest first.</summary>
    public IReadOnlyList<long> History => _history;
    /// <summary>Process ids, most recently used first.</summary>
    public IReadOnlyList<int> AppRank => _apps;

    public void Dispose() => _watcher.Dispose();
}
