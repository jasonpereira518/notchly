// SPDX-License-Identifier: GPL-3.0-or-later
// The island's optional reserved strip: a black AppBar along the top edge, so maximized windows
// start below it, like a Mac menu bar with a notch. Windows-only (no upstream counterpart).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Notchly.App.Platform.Windowing;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Shell;

public sealed class AppBarStrip : Window
{
    private readonly uint _callbackMessage = RegisterWindowMessage("NotchlyAppBarMessage");
    private DisplayInfo _display;
    private double _heightDips;
    private bool _registered;
    private IntPtr _handle;
    private HwndSource? _source;

    /// <summary>
    /// The island's window. The strip slots in right below it rather than at the very top, so it
    /// never covers the island's own strip or the open island's header beside the camera.
    /// </summary>
    public IntPtr KeepBelow { get; set; }

    private IntPtr TopmostSlot => KeepBelow != IntPtr.Zero && IsWindow(KeepBelow) ? KeepBelow : HWND_TOPMOST;

    /// <summary>A full-screen app took the display: the strip steps aside until it leaves.</summary>
    public event Action<bool>? FullscreenAppChanged;

    public AppBarStrip(DisplayInfo display, double heightDips)
    {
        _display = display;
        _heightDips = heightDips;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "Notchly strip";
        SourceInitialized += (_, _) => Register();
        // While the window still exists: by Closed it is destroyed, and the shell would keep its space
        // reserved, leaving maximized windows below an empty band.
        Closing += (_, _) => Unregister();
        Closed += (_, _) => Unregister();
    }

    public void Update(DisplayInfo display, double heightDips)
    {
        _display = display;
        _heightDips = heightDips;
        if (_registered) Position();
    }

    private void Register()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _handle = handle;
        AddExStyle(handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_EX_APPWINDOW);
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
        var data = NewData(handle);
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
        if (!_registered) Log.Warning("Could not register the island strip as an AppBar");
        Position();
    }

    private void Position()
    {
        var handle = _handle;
        var data = NewData(handle);
        data.uEdge = ABE_TOP;
        var height = (int)Math.Ceiling(_heightDips * _display.Scale);
        data.rc = new RECT { Left = _display.Bounds.Left, Top = _display.Bounds.Top, Right = _display.Bounds.Right, Bottom = _display.Bounds.Top + height };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        // The shell may have moved the rect below another top bar; keep our height from there.
        data.rc.Bottom = data.rc.Top + height;
        SHAppBarMessage(ABM_SETPOS, ref data);
        SetWindowPos(handle, TopmostSlot, data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != _callbackMessage) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case ABN_POSCHANGED:
                Position();
                break;
            case ABN_FULLSCREENAPP:
                var fullscreen = lParam != IntPtr.Zero;
                SetWindowPos(hwnd, fullscreen ? HWND_NOTOPMOST : TopmostSlot, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                FullscreenAppChanged?.Invoke(fullscreen);
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (!_registered) return;
        var data = NewData(_handle);
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
        Log.Information("Released the top strip's space");
    }

    private static APPBARDATA NewData(IntPtr handle) => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = handle };
}
