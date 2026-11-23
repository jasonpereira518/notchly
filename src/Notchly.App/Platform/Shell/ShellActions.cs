// SPDX-License-Identifier: GPL-3.0-or-later
// What a shelf tile's menu does with Windows' own shell: open, "Open with", show in Explorer,
// the share sheet, and the type name Explorer shows for a file.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Notchly.App.Platform.Shell;

public static class ShellActions
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warning(e, "Could not open {Target}", target); }
    }

    /// <summary>Windows' own "Open with" chooser.</summary>
    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }); }
        catch (Exception e) { Log.Warning(e, "Could not offer apps for {Path}", path); }
    }

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Warning(e, "Could not show {Path} in Explorer", path); }
    }

    // MARK: Share sheet

    private static IReadOnlyList<string> _shareFiles = [];
    private static string? _shareText;
    private static Uri? _shareLink;
    private static readonly HashSet<IntPtr> HookedWindows = [];

    /// <summary>Opens the Windows share sheet for files, a link or text, anchored to `window`.</summary>
    public static void Share(IntPtr window, IReadOnlyList<string> files, string? text, Uri? link)
    {
        try
        {
            _shareFiles = files;
            _shareText = text;
            _shareLink = link;
            var manager = DataTransferManagerInterop.GetForWindow(window);
            if (HookedWindows.Add(window)) manager.DataRequested += OnDataRequested;
            DataTransferManagerInterop.ShowShareUIForWindow(window);
        }
        catch (Exception e) { Log.Warning(e, "Share sheet unavailable"); }
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var request = args.Request;
        request.Data.Properties.Title = _shareFiles.Count > 0 ? Path.GetFileName(_shareFiles[0]) : "Notchly";
        var deferral = request.GetDeferral();
        try
        {
            if (_shareFiles.Count > 0)
            {
                var items = new List<IStorageItem>();
                foreach (var path in _shareFiles)
                {
                    try
                    {
                        items.Add(Directory.Exists(path)
                            ? await StorageFolder.GetFolderFromPathAsync(path)
                            : await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException) { }
                }
                request.Data.SetStorageItems(items);
            }
            if (_shareLink is not null) request.Data.SetWebLink(_shareLink);
            if (_shareText is not null) request.Data.SetText(_shareText);
        }
        finally { deferral.Complete(); }
    }

    // MARK: Type names

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

    private const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;

    /// <summary>"Text Document", "PNG File"…, as Explorer's Type column shows it.</summary>
    public static string? TypeName(string path)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_TYPENAME | (File.Exists(path) || Directory.Exists(path) ? 0 : SHGFI_USEFILEATTRIBUTES);
        return SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero && info.szTypeName.Length > 0
            ? info.szTypeName : null;
    }
}
