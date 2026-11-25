// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: which folder a File Explorer window shows. Explorer windows are listed by the Shell.Application object, and a window with
// tabs lists each tab with the same window handle, so the tab in front is the one whose shell view window is visible. Run it on an STA
// thread: the objects are apartment-threaded.
using System.Runtime.InteropServices;
using Notchly.Core.Clipboard;
using Serilog;

namespace Notchly.App.Platform.Shell;

public static class ExplorerFolders
{
    /// <summary>The folder shown in an Explorer window's front tab, or null when it is not a real folder (This PC, Home, the Recycle Bin).</summary>
    public static string? PathFor(IntPtr explorerWindow)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return null;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            dynamic windows = ((dynamic)shell).Windows();
            string? only = null, active = null;
            var matches = 0;
            foreach (object window in windows)
            {
                dynamic w = window;
                try
                {
                    if (new IntPtr((long)w.HWND) != explorerWindow) continue;
                    string? path = null;
                    try { path = w.Document.Folder.Self.Path; }
                    catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { /* a view that is not a folder */ }
                    matches++;
                    only ??= path;
                    if (matches > 1 || active is null)
                        if (IsFrontTab(window)) active = path;
                }
                catch (COMException) { /* a window that closed meanwhile */ }
            }
            var chosen = matches <= 1 ? only : active ?? only;
            return PasteImageSupport.IsWritableFolderPath(chosen) ? chosen : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Debug(e, "The Explorer folder could not be found");
            return null;
        }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static bool IsFrontTab(object window)
    {
        try
        {
            var provider = (IServiceProviderCom)window;
            var service = SidTopLevelBrowser;
            var iid = typeof(IShellBrowserCom).GUID;
            if (provider.QueryService(ref service, ref iid, out var browser) != 0 || browser is not IShellBrowserCom shellBrowser) return true;
            if (shellBrowser.QueryActiveShellView(out var view) != 0 || view is not IShellViewCom shellView) return true;
            shellView.GetWindow(out var viewWindow);
            return viewWindow == IntPtr.Zero || Native.IsWindowVisible(viewWindow);
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return true; }
    }

    private static Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserCom
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        void InsertMenusSB(IntPtr shared, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr holeMenu, IntPtr active);
        void RemoveMenusSB(IntPtr shared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellViewCom
    {
        void GetWindow(out IntPtr hwnd);
    }
}
