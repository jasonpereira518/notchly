// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the icon of the app behind a window, for the menu bar's app title. The program's
// own icon where there is one, cached per program; Store apps hosted by ApplicationFrameHost give
// their window's icon instead, since the host's file icon would be the same for all of them.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace Notchly.App.Platform.Windowing;

public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> ByProgram = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(ForegroundApp app)
    {
        var hosted = string.Equals(app.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
        if (!hosted && app.ImagePath is { } path)
        {
            var icon = ByProgram.GetOrAdd(path, ProgramIcon);
            if (icon is not null) return icon;
        }
        return WindowIcon(app.Window);
    }

    private static ImageSource? ProgramIcon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero) return null;
        try { return FromIcon(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>The window's own icon, which belongs to the window: not destroyed here.</summary>
    private static ImageSource? WindowIcon(IntPtr window)
    {
        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeout(window, WM_GETICON, (IntPtr)kind, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var icon) != IntPtr.Zero
                && icon != IntPtr.Zero)
                return FromIcon(icon);
        }
        var classIcon = GetClassLongPtr(window, GCLP_HICON);
        return classIcon != IntPtr.Zero ? FromIcon(classIcon) : null;
    }

    private static ImageSource? FromIcon(IntPtr icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(e, "App icon could not be read");
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
    private const uint WM_GETICON = 0x7F, SMTO_ABORTIFHUNG = 0x2;
    private const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2, GCLP_HICON = -14;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
}
