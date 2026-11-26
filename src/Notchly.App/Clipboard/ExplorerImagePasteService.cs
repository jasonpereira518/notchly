// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the paste-image part of upstream Sources/Vorssaint/Services/Finder/FinderCutPaste.swift (finderPasteImageAsFile) and
// FinderPasteImageSupport.swift: Ctrl+V in a folder, with an image on the clipboard and no files, makes a PNG file there. Anything else is
// an ordinary paste: the key is swallowed only when this will really make a file, and sent again as Notchly's own when it turns out it
// cannot (a folder that is not a place on disk, no image after all). Windows differences: File Explorer and the desktop stand for Finder,
// "focus is in the file list, not a text box" is read from the focused window's class, and the folder comes from the Shell windows list.
using System.IO;
using System.Media;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Shell;
using Notchly.Core.Clipboard;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Pasteboard;

public sealed class ExplorerImagePasteService(App app) : IFeatureService
{
    private IDisposable? _key;
    private bool _swallowedDown;
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");

    public void Sync()
    {
        var on = app.Settings.GetBool(DefaultsKey.FinderPasteImageAsFile);
        if (on && _key is null) _key = app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Expand);
        else if (!on && _key is not null)
        {
            _key.Dispose();
            _key = null;
        }
    }

    /// <summary>Hook thread: cheap checks only. True swallows the key.</summary>
    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn || e.VirtualKey != 0x56) return false;
        if (!e.IsDown)
        {
            if (!_swallowedDown) return false;
            _swallowedDown = false;
            return true;
        }
        if (_swallowedDown) return true;
        if (!KeyState.HeldExactly(["control"]) || app.InputGate.ShouldPassThrough) return false;

        var foreground = Native.GetAncestor(Native.GetForegroundWindow(), 2);
        if (foreground == IntPtr.Zero) return false;
        var windowClass = Native.ClassName(foreground);
        if (!PasteImageSupport.IsFolderWindowClass(windowClass)) return false;
        var thread = Native.GetWindowThreadProcessId(foreground, out _);
        var info = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero || !PasteImageSupport.FocusIsFileView(Native.ClassName(info.hwndFocus))) return false;
        if (!ImageWithoutFiles()) return false;

        _swallowedDown = true;
        app.Dispatcher.BeginInvoke(() => _ = HandleAsync(foreground, windowClass));
        return true;
    }

    private static bool ImageWithoutFiles() =>
        !Native.IsClipboardFormatAvailable(Native.CF_HDROP)
        && (Native.IsClipboardFormatAvailable(PngFormat) || Native.IsClipboardFormatAvailable(Native.CF_DIBV5) || Native.IsClipboardFormatAvailable(Native.CF_DIB));

    private async Task HandleAsync(IntPtr window, string windowClass)
    {
        try
        {
            var made = await TryMakeFileAsync(window, windowClass);
            if (!made) ResendPaste();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "The pasted image could not be saved");
            SystemSounds.Beep.Play();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Pasting an image as a file failed");
            ResendPaste();
        }
    }

    /// <summary>True when a file was made; false when the key should be sent on as an ordinary paste.</summary>
    private async Task<bool> TryMakeFileAsync(IntPtr window, string windowClass)
    {
        var snapshot = app.ClipboardWatch.Access.Read(includeImage: true);
        if (snapshot?.Png is not { } png || !PasteImageSupport.WithinCap(png.Length)) return false;
        var folder = PasteImageSupport.IsDesktopClass(windowClass)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : await FolderOnStaAsync(window);
        if (!PasteImageSupport.IsWritableFolderPath(folder) || !Directory.Exists(folder)) return false;

        var name = PasteImageSupport.UniqueName(PasteImageSupport.FileName(DateTime.Now), n => File.Exists(Path.Combine(folder, n)));
        var path = Path.Combine(folder, name);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await file.WriteAsync(png);
        Native.SHChangeNotify(0x00000002, 0x0005, path, IntPtr.Zero); // SHCNE_CREATE, SHCNF_PATHW
        return true;
    }

    private static Task<string?> FolderOnStaAsync(IntPtr window)
    {
        var completion = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try { completion.SetResult(ExplorerFolders.PathFor(window)); }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>The V that was swallowed goes out again as Notchly's own, so the paste happens as if nothing had intervened (Ctrl is still down).</summary>
    private void ResendPaste()
    {
        app.Injector.Key(0x56, true, InputSource.Clipboard);
        app.Injector.Key(0x56, false, InputSource.Clipboard);
    }

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
    }
}
