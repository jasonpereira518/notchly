// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the colour along the top of a window, its title bar or tab strip, read from the
// screen with one small GDI copy. Layered windows such as the island are not part of a plain
// copy, so the island never samples itself.
using System.Runtime.InteropServices;
using Notchly.Core.MenuBar;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Windowing;

public static class WindowColorSampler
{
    /// <summary>Rows sampled, a little inside the window's top edge.</summary>
    private const int Rows = 2, Inset = 3, SideInset = 8, MaxWidth = 4096;

    /// <summary>
    /// The dominant colour of a thin row just inside `window`'s top edge, no higher than
    /// `minimumY` (screen pixels, below the menu bar), within `display`. Null when the window is
    /// minimized, hidden, elsewhere, or too small to say.
    /// </summary>
    public static Rgb? Sample(IntPtr window, RECT display, int minimumY)
    {
        if (window == IntPtr.Zero || IsIconic(window) || !IsWindowVisible(window) || IsCloaked(window)) return null;
        if (DwmGetWindowRect(window, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(window, out frame)) return null;
        var left = Math.Max(frame.Left + SideInset, display.Left);
        var right = Math.Min(frame.Right - SideInset, display.Right);
        var top = Math.Max(frame.Top + Inset, minimumY + Inset);
        if (right - left < MenuBarTint.MinimumPixels || top + Rows > Math.Min(frame.Bottom, display.Bottom)) return null;
        var width = Math.Min(right - left, MaxWidth);
        return Dominant(left, top, width);
    }

    private static Rgb? Dominant(int x, int y, int width)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -Rows, // top-down
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, Rows, screen, x, y, SRCCOPY);
            SelectObject(memory, previous);
            if (!copied) return null;
            var pixels = new byte[width * Rows * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return MenuBarTint.Dominant(pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}
