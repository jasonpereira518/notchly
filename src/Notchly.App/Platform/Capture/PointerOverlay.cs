// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: upstream's "include pointer" option (the capture engine asks ScreenCaptureKit to draw the cursor). A GDI copy
// of the screen has no cursor in it, so the current cursor is drawn onto the copy with DrawIconEx.
using System.Runtime.InteropServices;
using Notchly.App.Platform.Windowing;

namespace Notchly.App.Platform.Capture;

public static class PointerOverlay
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public int Flags; public IntPtr Cursor; public Native.POINT Position; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    private const int CursorShowing = 1, DiNormal = 3;

    /// <summary>Draws the cursor, if it is showing and over the area, into top-down BGRA pixels of a capture of <paramref name="area"/> (screen pixels).</summary>
    public static void Draw(byte[] pixels, int width, int height, int stride, Native.RECT area)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return;
        if (!GetIconInfo(info.Cursor, out var icon)) return;
        try
        {
            var x = info.Position.X - icon.HotspotX - area.Left;
            var y = info.Position.Y - icon.HotspotY - area.Top;
            // A 64 pixel sheet covers the largest standard cursors; the cursor is drawn onto transparent black, then composited.
            const int side = 96;
            var bmi = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = side, Height = -side, Planes = 1, BitCount = 32 };
            var dc = CreateCompatibleDC(IntPtr.Zero);
            var section = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (section == IntPtr.Zero) { DeleteDC(dc); return; }
            var old = SelectObject(dc, section);
            try
            {
                var sheet = new byte[side * side * 4];
                Marshal.Copy(sheet, 0, bits, sheet.Length);
                DrawIconEx(dc, 0, 0, info.Cursor, 0, 0, 0, IntPtr.Zero, DiNormal);
                Marshal.Copy(bits, sheet, 0, sheet.Length);
                // DrawIconEx leaves alpha 0 on cursors with an AND mask; treat any non-black pixel as opaque.
                for (var row = 0; row < side; row++)
                    for (var col = 0; col < side; col++)
                    {
                        var px = x + col;
                        var py = y + row;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        var from = (row * side + col) * 4;
                        byte b = sheet[from], g = sheet[from + 1], r = sheet[from + 2], a = sheet[from + 3];
                        if (a == 0 && (r | g | b) == 0) continue;
                        if (a == 0) a = 255;
                        var to = py * stride + px * 4;
                        var inverse = 255 - a;
                        pixels[to] = (byte)((b * a + pixels[to] * inverse) / 255);
                        pixels[to + 1] = (byte)((g * a + pixels[to + 1] * inverse) / 255);
                        pixels[to + 2] = (byte)((r * a + pixels[to + 2] * inverse) / 255);
                    }
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(section);
                DeleteDC(dc);
            }
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }
}
