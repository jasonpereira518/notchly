// SPDX-License-Identifier: GPL-3.0-or-later
// Battery charge and the charger, for the island's battery notices (upstream reads IOKit power
// sources), and the built-in panel's brightness changes via WMI (WmiMonitorBrightnessEvent). The
// home page also sets that brightness (WmiSetBrightness) and reads the battery's report: its flow
// in watts and its capacities, which is all Windows says about power without a vendor's driver.
using System.Management;
using System.Windows.Threading;
using Notchly.Core.Power;
using Serilog;
using Windows.System.Power;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Power;

public readonly record struct BatteryState(bool HasBattery, int Percent, bool PluggedIn, bool Charging);

public sealed class PowerService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private ManagementEventWatcher? _brightnessWatcher;

    public BatteryState Battery { get; private set; }

    /// <summary>(previous, current). Raised on the UI thread.</summary>
    public event Action<BatteryState, BatteryState>? BatteryChanged;
    /// <summary>Built-in panel brightness 0–100. Raised on the UI thread.</summary>
    public event Action<int>? BrightnessChanged;

    public PowerService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Battery = Read();
        PowerManager.BatteryStatusChanged += OnPowerEvent;
        PowerManager.PowerSupplyStatusChanged += OnPowerEvent;
        PowerManager.RemainingChargePercentChanged += OnPowerEvent;
        StartBrightnessWatcher();
    }

    private void OnPowerEvent(object? sender, object e) => _dispatcher.BeginInvoke(() =>
    {
        var previous = Battery;
        Battery = Read();
        if (Battery != previous) BatteryChanged?.Invoke(previous, Battery);
    });

    /// <summary>The battery right now, for callers that do not follow changes.</summary>
    public static BatteryState ReadBattery() => Read();

    private static BatteryState Read()
    {
        if (!GetSystemPowerStatus(out var status)) return default;
        // BatteryFlag 128 = no system battery; 255 = unknown.
        var hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
        var percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;
        var plugged = status.ACLineStatus == 1;
        var charging = (status.BatteryFlag & 8) != 0 || (plugged && PowerManager.BatteryStatus == Windows.System.Power.BatteryStatus.Charging);
        return new BatteryState(hasBattery, percent, plugged, charging);
    }

    private void StartBrightnessWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            _brightnessWatcher = new ManagementEventWatcher(scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightnessWatcher.EventArrived += (_, e) =>
            {
                var level = Convert.ToInt32(e.NewEvent["Brightness"]);
                _dispatcher.BeginInvoke(() => BrightnessChanged?.Invoke(level));
            };
            _brightnessWatcher.Start();
        }
        catch (Exception e)
        {
            // Desktops and external-only setups have no WMI brightness.
            Log.Information("Brightness events unavailable: {Message}", e.Message);
            _brightnessWatcher = null;
        }
    }

    /// <summary>The battery's watts and capacities now; null without a battery.</summary>
    public BatteryReading? Report()
    {
        var state = Battery;
        if (!state.HasBattery) return null;
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.Status == Windows.System.Power.BatteryStatus.NotPresent) return null;
            return new BatteryReading(state.Percent, state.PluggedIn, state.Charging,
                report.ChargeRateInMilliwatts, report.RemainingCapacityInMilliwattHours,
                report.FullChargeCapacityInMilliwattHours, report.DesignCapacityInMilliwattHours);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Battery report unavailable");
            return new BatteryReading(state.Percent, state.PluggedIn, state.Charging);
        }
    }

    private int? _pendingBrightness;
    private bool _settingBrightness;

    /// <summary>
    /// Sets the built-in panel's brightness, 0–100, off the UI thread. A drag sends many values:
    /// only the newest waiting one is applied, so the panel never lags behind the slider.
    /// </summary>
    public void SetBrightness(int level)
    {
        lock (_brightnessGate)
        {
            _pendingBrightness = Math.Clamp(level, 0, 100);
            if (_settingBrightness) return;
            _settingBrightness = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int next;
                lock (_brightnessGate)
                {
                    if (_pendingBrightness is not { } pending) { _settingBrightness = false; return; }
                    next = pending;
                    _pendingBrightness = null;
                }
                ApplyBrightness(next);
            }
        });
    }

    private readonly object _brightnessGate = new();

    private static void ApplyBrightness(int level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject monitor in searcher.Get())
                using (monitor) monitor.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)level]);
        }
        catch (Exception e) { Log.Debug(e, "Brightness could not be set"); }
    }

    /// <summary>The built-in panel's current brightness, or null when it cannot be read.</summary>
    public static int? CurrentBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get()) return Convert.ToInt32(item["CurrentBrightness"]);
        }
        catch (Exception) { }
        return null;
    }

    public void Dispose()
    {
        PowerManager.BatteryStatusChanged -= OnPowerEvent;
        PowerManager.PowerSupplyStatusChanged -= OnPowerEvent;
        PowerManager.RemainingChargePercentChanged -= OnPowerEvent;
        try { _brightnessWatcher?.Stop(); } catch (Exception) { }
        _brightnessWatcher?.Dispose();
    }
}
