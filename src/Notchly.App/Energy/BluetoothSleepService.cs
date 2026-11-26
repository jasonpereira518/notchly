// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Bluetooth/BluetoothSleepService.swift: Bluetooth is turned off as the PC goes
// to sleep and back on at wake, putting back only what it took away. The debt is written down before the radio
// is touched, so a PC that was shut down while asleep still gets Bluetooth back at the next launch, even if the
// feature has been switched off since.
using Microsoft.Win32;
using Notchly.App.Platform.Bluetooth;
using Notchly.App.Shell;
using Notchly.Core.Energy;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Energy;

public sealed class BluetoothSleepService : IFeatureService
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _settings;
    private bool _disposed;

    /// <summary>False when this PC has no Bluetooth radio Windows lets us control (known after the first check).</summary>
    public bool? Supported { get; private set; }

    public BluetoothSleepService(SettingsStore settings)
    {
        _settings = settings;
        SystemEvents.PowerModeChanged += OnPowerMode;
        _ = CheckSupportAsync();
    }

    private async Task CheckSupportAsync() => Supported = await BluetoothRadio.IsSupportedAsync();

    public void Sync() => PayDebt(_settings);

    /// <summary>Called at every launch, whether or not the feature is installed: an owed restore is always paid.</summary>
    public static void PayDebt(SettingsStore settings)
    {
        var owes = settings.GetBool(DefaultsKey.BluetoothSleepRestorePending);
        if (!owes) return;
        // The radio is read only when something is owed, so a launch that owes nothing never touches it.
        var restores = BluetoothSleepSupport.Restores(owes, () => BluetoothRadio.IsOn(Wait) ?? true);
        if (restores) Log.Information("Bluetooth turned back on: {Result}", BluetoothRadio.Set(true, Wait));
        settings.Set(DefaultsKey.BluetoothSleepRestorePending, false);
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Mode == PowerModes.Suspend && _settings.GetBool(DefaultsKey.BluetoothSleepEnabled)) WillSleep();
        else if (e.Mode == PowerModes.Resume) PayDebt(_settings);
    }

    private void WillSleep()
    {
        var isOn = BluetoothRadio.IsOn(Wait);
        if (isOn is null) return;
        var plan = BluetoothSleepSupport.SleepPlan(isOn.Value, _settings.GetBool(DefaultsKey.BluetoothSleepRestoreOnWake));
        if (!plan.PowersOff) return;
        // Written down first: if the PC loses power before it wakes, the next launch still owes the restore.
        _settings.Set(DefaultsKey.BluetoothSleepRestorePending, plan.OwesRestore);
        BluetoothRadio.Set(false, Wait);
        Log.Information("Bluetooth turned off for sleep (restore owed: {Owed})", plan.OwesRestore);
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerMode;
    }
}
