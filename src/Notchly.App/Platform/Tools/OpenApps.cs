// SPDX-License-Identifier: GPL-3.0-or-later
// The apps with a window you can see, for the Quit apps list (upstream lists NSWorkspace's regular apps). A window
// counts the way Alt+Tab counts it: visible, with no owner, not cloaked and not a tool window. Store apps live in
// windows owned by ApplicationFrameHost, so each of those windows stands for its own app. Quitting posts
// WM_CLOSE to every window of the app, which is what the close button does, so unsaved work still asks first.
using System.IO;
using Notchly.Core.Tools;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Tools;

/// <summary>An app and its windows. <see cref="Key"/> stands in for a pid: a Store app's windows share their host's pid.</summary>
public sealed record OpenApp(int Key, int Pid, string Id, string Name, string? ExePath, IReadOnlyList<IntPtr> Windows, bool IsFront)
{
    public QuitAppsCandidate Candidate => new(Key, Id, Name, true, IsFront);
}

public static class OpenApps
{
    private const string FrameHost = "applicationframehost.exe";

    public static IReadOnlyList<OpenApp> List()
    {
        var front = GetForegroundWindow();
        var byKey = new Dictionary<int, (int Pid, string Id, string Name, string? Path, List<IntPtr> Windows, bool Front)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var path = ImagePath(pid);
            var exe = path is null ? "" : Path.GetFileName(path).ToLowerInvariant();
            if (exe.Length == 0) return true;
            var title = WindowText(hwnd);
            var isStoreWindow = exe == FrameHost;
            // A Store app has no process of its own at this level: its window stands for it.
            var key = isStoreWindow ? -Math.Abs((int)(hwnd.ToInt64() & 0x3FFFFFFF)) - 1 : (int)pid;
            if (!byKey.TryGetValue(key, out var entry))
            {
                var name = isStoreWindow ? title : AppName(path!, exe);
                entry = ((int)pid, isStoreWindow ? "store:" + title : exe, name, path, [], false);
            }
            entry.Windows.Add(hwnd);
            byKey[key] = entry with { Front = entry.Front || hwnd == front || IsAncestorOf(front, hwnd) };
            return true;
        }, IntPtr.Zero);
        return byKey.Select(p => new OpenApp(p.Key, p.Value.Pid, p.Value.Id, p.Value.Name, p.Value.Path, p.Value.Windows, p.Value.Front)).ToList();
    }

    private static bool IsAncestorOf(IntPtr front, IntPtr window) => front != IntPtr.Zero && GetAncestor(front, 2) == window;

    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || IsCloaked(hwnd)) return false;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0) return false;
        return WindowText(hwnd).Length > 0 || ClassName(hwnd).Length > 0 && (style & WS_EX_APPWINDOW) != 0;
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

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private static string AppName(string path, string exe)
    {
        if (Names.TryGetValue(path, out var cached)) return cached;
        string name;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            name = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim()
                   ?? Path.GetFileNameWithoutExtension(exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { name = Path.GetFileNameWithoutExtension(exe); }
        return Names[path] = name;
    }

    /// <summary>Asks every window of the app to close, as its own close button does. True when at least one was asked.</summary>
    public static bool Quit(OpenApp app)
    {
        var asked = false;
        foreach (var window in app.Windows)
            if (IsWindow(window)) asked |= PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!asked) Log.Debug("{App} had no window to close", app.Name);
        return asked;
    }
}
