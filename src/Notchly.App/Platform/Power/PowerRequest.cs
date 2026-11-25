// SPDX-License-Identifier: GPL-3.0-or-later
// Keeps Windows from sleeping on idle, through a power request (what upstream does with IOKit assertions). The
// request shows up in `powercfg /requests` with this app's reason, and ends with the handle.
using System.Runtime.InteropServices;
using Serilog;

namespace Notchly.App.Platform.Power;

public sealed class PowerRequest : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        // The union starts with a pointer: the simple reason string.
        public IntPtr SimpleReasonString;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle;
    private IntPtr _reason;
    private bool _display;

    /// <summary>True while the request holds the system awake.</summary>
    public bool Held { get; private set; }

    public bool Hold(string reason, bool keepDisplayOn)
    {
        if (_handle == IntPtr.Zero)
        {
            _reason = Marshal.StringToHGlobalUni(reason);
            var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = _reason };
            _handle = PowerCreateRequest(ref context);
            if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
            {
                Log.Warning("Power request could not be created ({Error})", Marshal.GetLastWin32Error());
                _handle = IntPtr.Zero;
                return false;
            }
        }
        if (!Held)
        {
            if (!PowerSetRequest(_handle, PowerRequestSystemRequired)) return false;
            Held = true;
        }
        SetDisplay(keepDisplayOn);
        return true;
    }

    /// <summary>The display request is separate, so display sleep can be allowed or blocked while a session runs.</summary>
    public void SetDisplay(bool keepDisplayOn)
    {
        if (_handle == IntPtr.Zero || keepDisplayOn == _display) return;
        if (keepDisplayOn) PowerSetRequest(_handle, PowerRequestDisplayRequired);
        else PowerClearRequest(_handle, PowerRequestDisplayRequired);
        _display = keepDisplayOn;
    }

    public void Release()
    {
        if (_handle == IntPtr.Zero) return;
        if (Held) PowerClearRequest(_handle, PowerRequestSystemRequired);
        if (_display) PowerClearRequest(_handle, PowerRequestDisplayRequired);
        Held = false;
        _display = false;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
        if (_reason != IntPtr.Zero) { Marshal.FreeHGlobal(_reason); _reason = IntPtr.Zero; }
    }

    public void Dispose() => Release();
}
