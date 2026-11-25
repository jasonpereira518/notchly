// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: how the clipboard features read and write the clipboard (upstream's GeneralPasteboardAccess). Everything goes through
// IClipboardAccess, so the monitor, history, cleaner and auto-clear can be exercised against a fake and never touch the real clipboard.
// The real one is WPF's clipboard with retries (another program may hold it open for a moment), on the UI thread.
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Clipboard;
using Serilog;

namespace Notchly.App.Pasteboard;

/// <summary>What the clipboard held when it changed, read once.</summary>
public sealed record ClipboardSnapshot(
    long Sequence, IReadOnlyList<string> Formats, string? Text = null, string? WebUrl = null, string? Html = null,
    IReadOnlyList<string>? Files = null, byte[]? Png = null, int PixelWidth = 0, int PixelHeight = 0, string? SourceProgram = null,
    int? CanIncludeInHistory = null, int? CanUploadToCloud = null, string? SourcePath = null)
{
    public IReadOnlyList<string> FileList => Files ?? [];
    public bool IsExcluded => ClipboardFormats.IsExcluded(Formats, CanIncludeInHistory, CanUploadToCloud);
}

/// <summary>The sequence numbers a write of ours went through: all of them are our own change, none a copy by the user.</summary>
public readonly record struct ClipboardWriteResult(bool Ok, long FromSequence, long ToSequence)
{
    public static ClipboardWriteResult Failed(long sequence) => new(false, sequence, sequence);
}

public interface IClipboardAccess
{
    /// <summary>The clipboard's update count (it only goes up while Windows runs).</summary>
    long SequenceNumber { get; }

    /// <summary>The clipboard's content, or null when it could not be opened. The image is read only when asked for.</summary>
    ClipboardSnapshot? Read(bool includeImage);

    ClipboardWriteResult WriteText(string text, bool excludeFromMonitoring);
    ClipboardWriteResult WriteFiles(IReadOnlyList<string> paths, bool excludeFromMonitoring);
    ClipboardWriteResult WriteImage(byte[] png, bool excludeFromMonitoring);
    ClipboardWriteResult Clear();
}

public sealed class Win32ClipboardAccess : IClipboardAccess
{
    private const int Tries = 5, RetryMs = 20;

    public long SequenceNumber => Native.GetClipboardSequenceNumber();

    private static T? Retry<T>(Func<T> action, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (Exception e) when (e is COMException or ExternalException or InvalidOperationException)
            {
                if (attempt >= Tries) { Log.Debug(e, "The clipboard could not be used to {What}", what); return default; }
                Thread.Sleep(RetryMs);
            }
        }
    }

    // MARK: Reading

    public ClipboardSnapshot? Read(bool includeImage) => Retry(() => ReadOnce(includeImage), "read it");

    private ClipboardSnapshot ReadOnce(bool includeImage)
    {
        var sequence = SequenceNumber;
        var data = System.Windows.Clipboard.GetDataObject();
        if (data is null) return new ClipboardSnapshot(sequence, []);
        var formats = data.GetFormats(false).ToList();

        string? text = null, url = null, html = null;
        string[]? files = null;
        if (data.GetDataPresent(DataFormats.UnicodeText, false)) text = data.GetData(DataFormats.UnicodeText, false) as string;
        else if (data.GetDataPresent(DataFormats.Text, false)) text = data.GetData(DataFormats.Text, false) as string;
        if (data.GetDataPresent("UniformResourceLocatorW", false) && data.GetData("UniformResourceLocatorW", false) is MemoryStream urlBytes)
            url = Encoding.Unicode.GetString(urlBytes.ToArray()).TrimEnd('\0').Trim();
        if (data.GetDataPresent(DataFormats.Html, false) && data.GetData(DataFormats.Html, false) is string cf)
            html = ClipboardFormats.HtmlFragment(Encoding.UTF8.GetBytes(cf)) ?? cf;
        if (data.GetDataPresent(DataFormats.FileDrop, false)) files = data.GetData(DataFormats.FileDrop, false) as string[];

        byte[]? png = null;
        int width = 0, height = 0;
        if (includeImage && files is null && ClipboardFormats.HasImage(formats)) (png, width, height) = ReadImage(data);

        var (program, path) = OwnerProgram();
        return new ClipboardSnapshot(sequence, formats, text, url, html, files, png, width, height, program,
            ReadDword(data, ClipboardFormats.CanIncludeInHistory), ReadDword(data, ClipboardFormats.CanUploadToCloud), path);
    }

    private static int? ReadDword(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format, false) || data.GetData(format, false) is not MemoryStream stream) return null;
        var bytes = stream.ToArray();
        return bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : null;
    }

    private static (byte[]? Png, int Width, int Height) ReadImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png && png.Length is > 0 and <= PasteImageSupport.MaxBytes)
            {
                var bytes = png.ToArray();
                var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return (bytes, decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            }
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } source)
            {
                if ((long)source.PixelWidth * source.PixelHeight * 4 > PasteImageSupport.MaxBytes) return (null, 0, 0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return (stream.ToArray(), source.PixelWidth, source.PixelHeight);
            }
        }
        catch (Exception e) when (e is COMException or IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            Log.Debug(e, "The clipboard image could not be read");
        }
        return (null, 0, 0);
    }

    /// <summary>The program that put the content on the clipboard: its window's owner, else whatever was in front.</summary>
    private static (string? Program, string? Path) OwnerProgram()
    {
        var owner = Native.GetClipboardOwner();
        var app = (owner != IntPtr.Zero ? ForegroundApps.Describe(owner) : null) ?? ForegroundApps.Describe(Native.GetForegroundWindow());
        return app?.ImagePath is { } path ? (Path.GetFileName(path).ToLowerInvariant(), path) : (null, null);
    }

    // MARK: Writing

    private static readonly byte[] Flag = [0, 0, 0, 0];

    private ClipboardWriteResult Write(DataObject data, bool exclude)
    {
        if (exclude)
        {
            data.SetData(ClipboardFormats.ExcludeFromMonitoring, new MemoryStream(Flag));
            data.SetData(ClipboardFormats.CanIncludeInHistory, new MemoryStream(Flag));
            data.SetData(ClipboardFormats.CanUploadToCloud, new MemoryStream(Flag));
        }
        var before = SequenceNumber;
        var ok = Retry(() => { System.Windows.Clipboard.SetDataObject(data, true); return true; }, "write it");
        var after = SequenceNumber;
        return ok ? new ClipboardWriteResult(true, before, after) : ClipboardWriteResult.Failed(after);
    }

    public ClipboardWriteResult WriteText(string text, bool excludeFromMonitoring)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        return Write(data, excludeFromMonitoring);
    }

    public ClipboardWriteResult WriteFiles(IReadOnlyList<string> paths, bool excludeFromMonitoring)
    {
        var data = new DataObject();
        data.SetFileDropList([.. paths]);
        return Write(data, excludeFromMonitoring);
    }

    public ClipboardWriteResult WriteImage(byte[] png, bool excludeFromMonitoring)
    {
        var data = new DataObject();
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            data.SetImage(frame);
            data.SetData("PNG", new MemoryStream(png));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            Log.Warning(e, "A clipboard image could not be decoded");
            return ClipboardWriteResult.Failed(SequenceNumber);
        }
        return Write(data, excludeFromMonitoring);
    }

    public ClipboardWriteResult Clear()
    {
        var before = SequenceNumber;
        var ok = Retry(() => { System.Windows.Clipboard.Clear(); return true; }, "empty it");
        var after = SequenceNumber;
        return ok ? new ClipboardWriteResult(true, before, after) : ClipboardWriteResult.Failed(after);
    }
}
