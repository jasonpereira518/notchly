// SPDX-License-Identifier: GPL-3.0-or-later
// The translucent rectangle that shows where a window will land while a snap is pending (upstream draws the same preview with an
// NSPanel). A click-through, no-activate layer, kept out of screenshots, placed in physical pixels.
using System.Windows;
using System.Windows.Media;
using Notchly.Core.Windowing;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Layout;

public sealed class SnapPreview : IDisposable
{
    private const int GwlExStyle = -20;
    private const long ExTransparent = 0x20, ExToolWindow = 0x80, ExNoActivate = 0x08000000, ExLayered = 0x80000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Window _window;
    private IntPtr _handle;
    private WindowRect? _pending;

    public SnapPreview()
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            IsHitTestVisible = false, Focusable = false, ResizeMode = ResizeMode.NoResize, Left = -32000, Top = -32000, Width = 1, Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xA3, 0xFF)),
        };
        _window.SourceInitialized += (_, _) =>
        {
            _handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(style | ExTransparent | ExToolWindow | ExNoActivate | ExLayered));
            SetWindowDisplayAffinity(_handle, 0x11);
            if (_pending is { } rect) Move(rect);
        };
        _window.Opacity = 0;
        _window.Show();
    }

    /// <summary>Shows the preview over a rectangle in physical pixels.</summary>
    public void Show(WindowRect rect)
    {
        _pending = rect;
        _window.Opacity = 1;
        Move(rect);
    }

    public void Hide()
    {
        _pending = null;
        _window.Opacity = 0;
        if (_handle != IntPtr.Zero) SetWindowPos(_handle, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 | 0x0004);
    }

    private void Move(WindowRect rect)
    {
        if (_handle == IntPtr.Zero) return;
        SetWindowPos(_handle, new IntPtr(-1), (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, 0x0010 | 0x0040);
    }

    public void Dispose()
    {
        try { _window.Close(); } catch { /* already closed */ }
    }
}
