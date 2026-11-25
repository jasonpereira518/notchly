// SPDX-License-Identifier: GPL-3.0-or-later
// The lid-close action of the active power scheme, and the lid switch itself. Closed-lid mode sets the action to
// "do nothing" for the length of a session and puts it back after; the callers save what was there first.
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Notchly.Core.Energy;
using Serilog;

namespace Notchly.App.Platform.Power;

public static class LidAction
{
    private static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid LidActionSetting = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    /// <summary>The lid action now, on mains power and on battery; null when this PC has no such setting.</summary>
    public static LidRules.LidActionBackup? Read()
    {
        if (ActiveScheme() is not { } scheme) return null;
        var sub = SubButtons;
        var setting = LidActionSetting;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) != 0) return null;
        if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) != 0) return null;
        return new LidRules.LidActionBackup((int)ac, (int)dc);
    }

    /// <summary>Writes the lid action for both power sources and applies it. False when Windows refused.</summary>
    public static bool Write(LidRules.LidActionBackup values)
    {
        if (ActiveScheme() is not { } scheme) return false;
        var sub = SubButtons;
        var setting = LidActionSetting;
        if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)values.OnPower) != 0
            || PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)values.OnBattery) != 0)
        {
            Log.Warning("The lid action could not be written");
            return false;
        }
        return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
    }

    public static bool DoNothing() => Write(new LidRules.LidActionBackup(LidRules.DoNothing, LidRules.DoNothing));
}

/// <summary>Reports the lid opening and closing (GUID_LIDSWITCH_STATE_CHANGE); the current state arrives at once.</summary>
public sealed class LidWatcher : IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid LidSwitch = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; public byte Data; }

    private readonly HwndSource _source;
    private readonly IntPtr _registration;

    /// <summary>True when the lid is closed. Null until Windows has said.</summary>
    public bool? Closed { get; private set; }
    /// <summary>Raised on the thread that created the watcher.</summary>
    public event Action<bool>? Changed;

    public LidWatcher()
    {
        _source = new HwndSource(new HwndSourceParameters("NotchlyLid") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(WndProc);
        var guid = LidSwitch;
        _registration = RegisterPowerSettingNotification(_source.Handle, ref guid, 0);
        if (_registration == IntPtr.Zero) Log.Information("No lid switch on this PC");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == LidSwitch)
            {
                // The data is 0 for closed and 1 for open.
                var closed = setting.Data == 0;
                Closed = closed;
                Changed?.Invoke(closed);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration);
        _source.Dispose();
    }
}
