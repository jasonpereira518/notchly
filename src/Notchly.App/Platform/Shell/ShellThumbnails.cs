// SPDX-License-Identifier: GPL-3.0-or-later
// Thumbnails and icons from Explorer's own thumbnail cache (IShellItemImageFactory), made on
// one STA worker thread and cached by path and modification time. Upstream uses QuickLook.
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notchly.App.Platform.Shell;

public static class ShellThumbnails
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    static ShellThumbnails()
    {
        var thread = new Thread(() => { foreach (var job in Work.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true,
            Name = "Notchly thumbnails",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>A frozen thumbnail (or the file's icon) about `pixels` across, or null.</summary>
    public static Task<ImageSource?> GetAsync(string path, int pixels)
    {
        var key = $"{path}|{pixels}|{Stamp(path)}";
        if (Cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
        var completion = new TaskCompletionSource<ImageSource?>();
        Work.Add(() =>
        {
            ImageSource? image = null;
            try { image = Load(path, pixels); }
            catch (Exception e) when (e is COMException or FileNotFoundException or ArgumentException or UnauthorizedAccessException) { }
            Cache[key] = image;
            completion.SetResult(image);
        });
        return completion.Task;
    }

    private static string Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks.ToString(); }
        catch (Exception) { return "0"; }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid,
                                                           [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, out DIBSECTION section);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    private static ImageSource? Load(string path, int pixels)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            // SIIGBF_BIGGERSIZEOK: a thumbnail when Explorer has one, otherwise the file's icon.
            if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, 0x1, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            try { return ToImage(bitmap); }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Copies a 32-bit DIB section with its alpha (CreateBitmapSourceFromHBitmap loses it).</summary>
    private static ImageSource? ToImage(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), out var section) == 0 || section.dsBm.bmBits == IntPtr.Zero
            || section.dsBm.bmBitsPixel != 32) return null;
        int width = section.dsBm.bmWidth, height = section.dsBm.bmHeight, stride = section.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(section.dsBm.bmBits, pixels, 0, pixels.Length);
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        // A positive height is a bottom-up DIB: flip it upright.
        if (section.biHeight > 0) image = new TransformedBitmap(image, new ScaleTransform(1, -1));
        image.Freeze();
        return image;
    }
}
