// SPDX-License-Identifier: GPL-3.0-or-later
// The software dim: a black, click-through layer over one display whose opacity is the dimming. Windows rejects
// dark gamma ramps, so an overlay does what upstream's gamma scaling does. It takes no focus, does not appear
// in the taskbar or Alt+Tab, and leaves screenshots alone.
using System.Windows;
using System.Windows.Media;
using Notchly.Core.Energy;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Display;

public sealed class DimOverlay : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private const uint WdaExcludeFromCapture = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Window _window;
    private IntPtr _handle;

    public DimOverlay(RECT bounds)
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
            Opacity = 0,
            Left = bounds.Left, Top = bounds.Top, Width = 1, Height = 1,
        };
        _window.SourceInitialized += (_, _) =>
        {
            _handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered));
            SetWindowDisplayAffinity(_handle, WdaExcludeFromCapture);
            Cover(bounds);
        };
        _window.Show();
    }

    /// <summary>Sizes the layer to the display in physical pixels.</summary>
    public void Cover(RECT bounds)
    {
        if (_handle == IntPtr.Zero) return;
        SetWindowPos(_handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow);
    }

    /// <summary>1 is no dimming; lower is darker, never completely black.</summary>
    public double Factor
    {
        get => 1 - _window.Opacity;
        set => _window.Opacity = BrightnessSupport.OverlayOpacity(value);
    }

    public void Dispose()
    {
        try { _window.Close(); } catch { /* already closed */ }
    }
}
