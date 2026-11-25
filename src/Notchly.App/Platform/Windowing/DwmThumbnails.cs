// SPDX-License-Identifier: GPL-3.0-or-later
// Live window previews with the desktop window manager (upstream captures with ScreenCaptureKit and needs Screen Recording).
// A thumbnail is registered from a source window into a destination window and drawn by DWM itself, so it stays live, costs
// nothing to keep and needs no permission. It is drawn above everything in the destination window, so a card keeps its frame and
// text outside the preview rectangle.
using System.Runtime.InteropServices;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public sealed class DwmThumbnail : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Properties
    {
        public uint Flags;
        public RECT Destination;
        public RECT Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);

    private const uint FlagDestination = 0x1, FlagOpacity = 0x4, FlagVisible = 0x8, FlagClientOnly = 0x10;
    private IntPtr _handle;

    private DwmThumbnail(IntPtr handle) => _handle = handle;

    /// <summary>Registers a thumbnail of <paramref name="source"/> in <paramref name="destination"/>, or null when DWM refuses.</summary>
    public static DwmThumbnail? Register(IntPtr destination, IntPtr source) =>
        DwmRegisterThumbnail(destination, source, out var handle) == 0 && handle != IntPtr.Zero ? new DwmThumbnail(handle) : null;

    /// <summary>The size of the source window's picture, to keep its shape in a card.</summary>
    public (int Width, int Height)? SourceSize() =>
        _handle != IntPtr.Zero && DwmQueryThumbnailSourceSize(_handle, out var s) == 0 && s.Width > 0 ? (s.Width, s.Height) : null;

    /// <summary>Draws the picture in a rectangle of the destination window (client pixels), fitted to keep its shape, or hides it.</summary>
    public void Show(int left, int top, int width, int height, bool visible = true)
    {
        if (_handle == IntPtr.Zero) return;
        var rect = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
        if (SourceSize() is { } size && size.Width > 0 && size.Height > 0)
        {
            var scale = Math.Min(width / (double)size.Width, height / (double)size.Height);
            var w = Math.Max(1, (int)(size.Width * scale));
            var h = Math.Max(1, (int)(size.Height * scale));
            rect = new RECT { Left = left + (width - w) / 2, Top = top + (height - h) / 2, Right = left + (width - w) / 2 + w, Bottom = top + (height - h) / 2 + h };
        }
        var properties = new Properties { Flags = FlagDestination | FlagOpacity | FlagVisible | FlagClientOnly, Destination = rect, Opacity = 255, Visible = visible ? 1 : 0, SourceClientAreaOnly = 0 };
        DwmUpdateThumbnailProperties(_handle, ref properties);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_handle);
        _handle = IntPtr.Zero;
    }
}
