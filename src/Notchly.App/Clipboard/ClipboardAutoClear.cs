// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Clipboard/ClipboardAutoClearService.swift: the clipboard is emptied after it has held the
// same thing for a while, and when the PC sleeps, the display turns off or the session locks. It does not depend on history being
// on, and the history already saved is never touched. Windows differences: sleep is a power-mode event, display-off is the console display
// power setting, and lock is a session switch.
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Pasteboard;

public sealed class ClipboardAutoClear(SettingsStore settings, ClipboardMonitor monitor, Dispatcher dispatcher, Func<DateTimeOffset> clock) : IDisposable
{
    private DispatcherTimer? _timer;
    private HwndSource? _power;
    private IntPtr _powerNotification;
    private bool _events;
    private long? _lastSeen, _ownClear, _lastCleared;
    private DateTimeOffset _lastChange;

    private IClipboardAccess Access => monitor.Access;

    public void Sync()
    {
        var delay = settings.GetBool(DefaultsKey.ClipboardAutoClearOnDelay);
        if (delay && _timer is null)
        {
            // What is on the clipboard now gets a full delay from now.
            _lastSeen = Access.SequenceNumber;
            _lastChange = clock();
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        }
        else if (!delay && _timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }

        var wantsEvents = settings.GetBool(DefaultsKey.ClipboardAutoClearOnSleep) || settings.GetBool(DefaultsKey.ClipboardAutoClearOnScreenLock)
                          || settings.GetBool(DefaultsKey.ClipboardAutoClearOnDisplaySleep);
        if (wantsEvents && !_events)
        {
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.SessionSwitch += OnSession;
            _events = true;
        }
        else if (!wantsEvents && _events) StopEvents();
        if (settings.GetBool(DefaultsKey.ClipboardAutoClearOnDisplaySleep) && _power is null) StartDisplayWatch();
        else if (!settings.GetBool(DefaultsKey.ClipboardAutoClearOnDisplaySleep) && _power is not null) StopDisplayWatch();
    }

    /// <summary>One second of the timer. Public so a harness can drive it with its own clock.</summary>
    public void Tick()
    {
        var now = clock();
        var count = Access.SequenceNumber;
        var delay = settings.GetInt(DefaultsKey.ClipboardAutoClearDelaySeconds);
        switch (ClipboardAutoClearSupport.Decide(count, _lastSeen, _ownClear, _lastChange, now, delay))
        {
            case AutoClearDecision.NoteChange:
                _lastSeen = count;
                _lastChange = now;
                break;
            case AutoClearDecision.Clear:
                Clear();
                break;
        }
    }

    private void Clear()
    {
        var result = Access.Clear();
        monitor.RememberOwnWrite(result);
        if (!result.Ok) return;
        _ownClear = _lastSeen = _lastCleared = result.ToSequence;
        _lastChange = clock();
    }

    /// <summary>Sleep, display-off and lock empty the clipboard at once, unless it was already emptied at this count.</summary>
    public void OnEvent()
    {
        if (!ClipboardAutoClearSupport.EventShouldClear(Access.SequenceNumber, _lastCleared)) return;
        Clear();
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && settings.GetBool(DefaultsKey.ClipboardAutoClearOnSleep)) Run(OnEvent);
    }

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && settings.GetBool(DefaultsKey.ClipboardAutoClearOnScreenLock)) Run(OnEvent);
    }

    private void Run(Action action)
    {
        // A suspend gives only a moment: do it now when already on the UI thread, otherwise wait for it briefly.
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action, DispatcherPriority.Send, default, TimeSpan.FromSeconds(2));
    }

    // MARK: Display off

    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private const int WM_POWERBROADCAST = 0x218, PBT_POWERSETTINGCHANGE = 0x8013;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; public byte Data; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    private void StartDisplayWatch()
    {
        _power = new HwndSource(new HwndSourceParameters("NotchlyClipboardPower") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _power.AddHook(PowerProc);
        var guid = ConsoleDisplayState;
        _powerNotification = RegisterPowerSettingNotification(_power.Handle, ref guid, 0);
        if (_powerNotification == IntPtr.Zero) Log.Warning("The display power notification could not be registered");
    }

    private void StopDisplayWatch()
    {
        if (_powerNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotification);
        _powerNotification = IntPtr.Zero;
        _power?.RemoveHook(PowerProc);
        _power?.Dispose();
        _power = null;
    }

    private IntPtr PowerProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            // 0 is off, 1 on, 2 dimmed.
            if (setting.PowerSetting == ConsoleDisplayState && setting.Data == 0) OnEvent();
        }
        return IntPtr.Zero;
    }

    private void StopEvents()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.SessionSwitch -= OnSession;
        _events = false;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        if (_events) StopEvents();
        StopDisplayWatch();
    }
}
