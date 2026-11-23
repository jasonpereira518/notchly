// SPDX-License-Identifier: GPL-3.0-or-later
// Reads a Windows drag (OLE data object) into what the shelf needs: dropped files, a single
// virtual file (a browser image, an Outlook attachment) or image data written into the shelf's
// own folder, a link, or text. The Windows counterpart of upstream ShelfService's pasteboard
// reading; the priority itself is Islet.Core.Shelf.ShelfDrop.
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Islet.Core.Shelf;
using Serilog;
using IDataObject = System.Windows.IDataObject;

namespace Islet.App.Platform.Shell;

public static class DropReader
{
    private const string FileGroupDescriptor = "FileGroupDescriptorW";
    private const string FileContents = "FileContents";
    private const string UrlFormat = "UniformResourceLocatorW";

    public static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(FileGroupDescriptor) || data.GetDataPresent("PNG")
        || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(UrlFormat) || data.GetDataPresent(DataFormats.UnicodeText)
        || data.GetDataPresent(DataFormats.Text);

    /// <summary>Reads a drop. Files the shelf had to write are listed in `written` so a refused drop can delete them.</summary>
    public static ShelfDropContent Read(IDataObject data, string storeDirectory, List<string> written)
    {
        var files = data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (files.Length == 0 && data.GetDataPresent(FileGroupDescriptor) && ReadVirtualFile(data, storeDirectory) is { } virtualFile)
        {
            written.Add(virtualFile);
            files = [virtualFile];
        }
        string? image = null;
        if (files.Length == 0 && ReadImage(data, storeDirectory) is { } imageFile)
        {
            written.Add(imageFile);
            image = imageFile;
        }
        return new ShelfDropContent(files, image, ReadUrl(data), data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string);
    }

    private static string? ReadUrl(IDataObject data)
    {
        try
        {
            if (data.GetData(UrlFormat) is MemoryStream stream)
                return Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0').Trim();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
        return null;
    }

    private static string? ReadImage(IDataObject data, string storeDirectory)
    {
        try
        {
            if (data.GetData("PNG") is MemoryStream png && png.Length > 0)
                return Write(png.ToArray(), "Image.png", storeDirectory);
            if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return Write(stream.ToArray(), "Image.png", storeDirectory);
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            Log.Debug(e, "Dropped image could not be read");
        }
        return null;
    }

    /// <summary>A file kept in its own folder in the store, under its own name.</summary>
    public static string Write(byte[] bytes, string name, string storeDirectory)
    {
        var safe = string.Concat(Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
        if (safe.Length == 0 || safe is "." or "..") safe = "Dropped file";
        var folder = Path.Combine(storeDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, safe);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // MARK: Virtual files (FILEGROUPDESCRIPTORW + FILECONTENTS, first file only)

    private const int DescriptorSize = 592, NameOffset = 72, NameBytes = 520;
    private const int MaximumVirtualBytes = 512 * 1024 * 1024;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    private static string? ReadVirtualFile(IDataObject data, string storeDirectory)
    {
        try
        {
            if (data.GetData(FileGroupDescriptor) is not MemoryStream descriptor) return null;
            var bytes = descriptor.ToArray();
            if (bytes.Length < 4 + DescriptorSize || BitConverter.ToUInt32(bytes, 0) < 1) return null;
            var name = Encoding.Unicode.GetString(bytes, 4 + NameOffset, NameBytes).Split('\0')[0];
            if (data is not System.Runtime.InteropServices.ComTypes.IDataObject com) return null;
            var format = new FORMATETC
            {
                cfFormat = (short)RegisterClipboardFormat(FileContents),
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = 0,
                tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM,
            };
            com.GetData(ref format, out var medium);
            try
            {
                var content = ReadMedium(medium);
                return content is null ? null : Write(content, name, storeDirectory);
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or IOException or ArgumentException)
        {
            Log.Information("A dropped virtual file could not be read: {Message}", e.Message);
            return null;
        }
    }

    private static byte[]? ReadMedium(STGMEDIUM medium)
    {
        if (medium.tymed == TYMED.TYMED_HGLOBAL)
        {
            var size = (long)GlobalSize(medium.unionmember).ToUInt64();
            if (size <= 0 || size > MaximumVirtualBytes) return null;
            var pointer = GlobalLock(medium.unionmember);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, (int)size);
                return bytes;
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        if (medium.tymed == TYMED.TYMED_ISTREAM && Marshal.GetObjectForIUnknown(medium.unionmember) is IStream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            var readPointer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPointer);
                    var read = Marshal.ReadInt32(readPointer);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    if (output.Length > MaximumVirtualBytes) return null;
                }
            }
            finally { Marshal.FreeHGlobal(readPointer); }
            return output.ToArray();
        }
        return null;
    }
}
