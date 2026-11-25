// SPDX-License-Identifier: GPL-3.0-or-later
// The Bluetooth radio's power, through Windows.Devices.Radios (upstream reads and sets the controller's power through
// IOBluetooth). Needs no administrator; Windows asks for radio access once.
using Serilog;
using Windows.Devices.Radios;

namespace Notchly.App.Platform.Bluetooth;

public static class BluetoothRadio
{
    private static async Task<Radio?> FindAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Bluetooth radio unavailable");
            return null;
        }
    }

    /// <summary>True when this PC has a Bluetooth radio Windows lets this app control.</summary>
    public static async Task<bool> IsSupportedAsync() => await FindAsync() is not null;

    /// <summary>On, off, or null when there is no radio or it cannot be read.</summary>
    public static async Task<bool?> IsOnAsync() => await FindAsync() is { } radio ? radio.State == RadioState.On : null;

    public static async Task<bool> SetAsync(bool on)
    {
        if (await FindAsync() is not { } radio) return false;
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed;
    }

    /// <summary>Blocking forms for the sleep and wake callbacks, which cannot await. Gives up after the timeout.</summary>
    public static bool? IsOn(TimeSpan timeout) => Wait(IsOnAsync(), timeout, null);

    public static bool Set(bool on, TimeSpan timeout) => Wait(SetAsync(on), timeout, false);

    private static T Wait<T>(Task<T> task, TimeSpan timeout, T fallback)
    {
        try { return task.Wait(timeout) ? task.Result : fallback; }
        catch (Exception e) { Log.Debug(e, "Bluetooth radio call failed"); return fallback; }
    }
}
