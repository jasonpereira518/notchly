// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/SystemMonitor/SystemMonitor.swift: one timer and one snapshot shared by the panel, the island
// page, the tray readouts and the alerts. Sampling is demand-driven: the plan (Notchly.Core.Monitor.SamplingPlan) says
// what is read and how often, and when nothing needs a reading the timer stops and every baseline is dropped.
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform.Bluetooth;
using Notchly.App.Platform.Monitor;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Monitor;

/// <summary>Everything the monitor last read. A value stays null while it is unavailable and the surfaces hide its row.</summary>
public sealed class SystemSnapshot
{
    public double? CpuUsage { get; set; }
    public double? CpuReadAt { get; set; }
    public IReadOnlyList<CoreLoad>? Cores { get; set; }
    public IReadOnlyList<GpuLoad>? Gpus { get; set; }
    public double? GpuUsage => Gpus is { Count: > 0 } ? Gpus.Max(g => g.Usage) : null;
    public MemoryReading? Memory { get; set; }
    public (double Down, double Up)? Network { get; set; }
    public (ulong Down, ulong Up) NetworkTotals { get; set; }
    public IReadOnlyList<AddressInfo> Addresses { get; set; } = [];
    public IReadOnlyList<DiskVolume> Disks { get; set; } = [];
    public (double Read, double Write)? DiskActivity { get; set; }
    public PowerReading? Power { get; set; }
    public IReadOnlyList<ConnectedUsbDevice> Devices { get; set; } = [];
    public IReadOnlyList<BluetoothAccessory> Peripherals { get; set; } = [];
    public IReadOnlyDictionary<ProcessKind, IReadOnlyList<ProcessRow>> TopProcesses { get; set; } = new Dictionary<ProcessKind, IReadOnlyList<ProcessRow>>();
    public long UptimeSeconds { get; set; }
}

public sealed class SystemMonitor : IDisposable
{
    public const int HistoryCapacity = 120;

    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CpuSampler _cpu = new();
    private readonly GpuSampler _gpu = new();
    private readonly NetworkSampler _network = new();
    private readonly DiskSampler _disk = new();
    private readonly ProcessUsageSampler _processes = new();
    private BluetoothAccessorySource? _peripherals;
    private readonly object _gate = new();
    private SamplingPlan _plan = new(new HashSet<MonitorSamplingKind>(), false, false);
    private readonly HashSet<MonitorSamplingKind> _forced = [];
    private int _tick;
    private bool _running, _pending, _disposed;
    private double _diskLastAt;

    public SystemSnapshot Snapshot { get; } = new();
    private readonly HttpSpeedTransport _speedTransport = new();

    /// <summary>The internet speed test (started by the person, from the Network detail).</summary>
    public SpeedTest SpeedTest { get; }
    public MetricHistory Cpu { get; } = new(HistoryCapacity);
    public MetricHistory Gpu { get; } = new(HistoryCapacity);
    public MetricHistory Memory { get; } = new(HistoryCapacity);
    public MetricHistory NetDown { get; } = new(HistoryCapacity);
    public MetricHistory NetUp { get; } = new(HistoryCapacity);
    public MetricHistory DiskRead { get; } = new(HistoryCapacity);
    public MetricHistory DiskWrite { get; } = new(HistoryCapacity);
    public MetricHistory Battery { get; } = new(HistoryCapacity);

    // What is looking at the monitor right now (set by the surfaces).
    private bool _panelOpen, _panelCoresOpen, _islandPage;
    private MetricDetail? _islandDetail;
    private IReadOnlySet<ProcessKind> _processKinds = new HashSet<ProcessKind>();

    /// <summary>A new snapshot is ready. Raised on the UI thread.</summary>
    public event Action? Updated;

    public SystemMonitor(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        SpeedTest = new SpeedTest(_speedTransport, Platform.Monitor.Clock.UnbiasedSeconds, (span, cancellation) => Task.Delay(span, cancellation));
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) => OnTick();
        SystemEvents.PowerModeChanged += OnPowerMode;
    }

    public void StartSpeedTest()
    {
        if (!SpeedTest.IsRunning) _ = SpeedTest.RunAsync();
    }

    public bool Foreground => _plan.Foreground;
    private bool? _hasBattery;
    public bool HasBattery => _hasBattery ??= PowerSampler.Read() is not null;

    // MARK: Demand

    public void SetPanel(bool open, bool coresOpen = false, IReadOnlySet<ProcessKind>? processKinds = null)
    {
        _panelOpen = open;
        _panelCoresOpen = coresOpen;
        if (processKinds is not null) _processKinds = processKinds;
        PlanChanged();
    }

    public void SetIsland(bool pageOpen, MetricDetail? detail = null, IReadOnlySet<ProcessKind>? processKinds = null)
    {
        _islandPage = pageOpen;
        _islandDetail = detail;
        if (processKinds is not null) _processKinds = processKinds;
        PlanChanged();
    }

    public void SetProcessKinds(IReadOnlySet<ProcessKind> kinds)
    {
        _processKinds = kinds;
        if (_plan.Foreground) _forced.Add(MonitorSamplingKind.Cpu);
    }

    /// <summary>Recomputes what is read (upstream planDidChange): after a feature, a setting or a surface changes.</summary>
    public void PlanChanged()
    {
        var installed = FeatureCatalog.All.Where(i => i.Group == FeatureGroup.Monitor && _settings.IsAvailable(i.Feature)).Select(i => i.Feature).ToHashSet();
        var demand = new MonitorDemand(installed, _panelOpen, _islandPage, _islandDetail,
            _settings.GetBool(DefaultsKey.MonitorShowSystem), _settings.GetBool(DefaultsKey.MonitorSysCPU), _settings.GetBool(DefaultsKey.MonitorSysGPU),
            _settings.GetBool(DefaultsKey.MonitorSysMemory), _settings.GetBool(DefaultsKey.MonitorSysConnectedDevices), _panelCoresOpen && _settings.GetBool(DefaultsKey.MonitorSysCPUCores),
            _settings.GetBool(DefaultsKey.MonitorShowNetwork), _settings.GetBool(DefaultsKey.MonitorShowDisk), _settings.GetBool(DefaultsKey.MonitorShowPower),
            _settings.GetBool(DefaultsKey.MonitorPwrPeripherals), TrayMetrics(), AlertKinds(installed), HasBattery);
        var plan = SamplingPlan.Compute(demand);
        var changed = !plan.Kinds.SetEquals(_plan.Kinds) || plan.Foreground != _plan.Foreground || plan.PerCore != _plan.PerCore;
        _plan = plan;
        if (plan.IsIdle)
        {
            // Nothing needs a reading: the timer stops and the baselines go, so the next opening starts clean.
            _timer.Stop();
            _cpu.Reset(); _gpu.Reset(); _disk.Reset(); _processes.Reset();
            StopPeripherals();
            _tick = 0;
            return;
        }
        var interval = Interval();
        var wake = MonitorSamplingPolicy.WakeTicks(plan.Kinds, interval, plan.Foreground);
        var cadence = TimeSpan.FromSeconds(interval * wake);
        if (_timer.Interval != cadence) _timer.Interval = cadence;
        _tick = MonitorSamplingPolicy.AlignedTick(_tick, wake);
        if (!plan.Kinds.Contains(MonitorSamplingKind.PeripheralBattery)) StopPeripherals();
        if (changed) foreach (var kind in plan.Kinds) _forced.Add(kind);
        if (!_timer.IsEnabled)
        {
            _timer.Start();
            OnTick();
        }
    }

    private int Interval() => _settings.GetInt(DefaultsKey.MonitorInterval, 2) is 1 or 2 or 5 ? _settings.GetInt(DefaultsKey.MonitorInterval, 2) : 2;

    private HashSet<string> TrayMetrics()
    {
        var metrics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, name) in new[]
                 {
                     (DefaultsKey.MenuBarCPU, "cpu"), (DefaultsKey.MenuBarGPU, "gpu"), (DefaultsKey.MenuBarMemory, "memory"), (DefaultsKey.MenuBarNetwork, "network"),
                     (DefaultsKey.MenuBarDiskUsage, "diskUsage"), (DefaultsKey.MenuBarDiskActivity, "diskActivity"), (DefaultsKey.MenuBarBattery, "battery"),
                     (DefaultsKey.MenuBarBatteryTime, "batteryTime"), (DefaultsKey.MenuBarPeripheralBattery, "peripheralBattery"),
                     (DefaultsKey.MenuBarConnectedDevices, "connectedDevices"),
                 })
            if (_settings.GetBool(key)) metrics.Add(name);
        return metrics;
    }

    private HashSet<MonitorAlertKind> AlertKinds(IReadOnlySet<AppFeature> installed)
    {
        var kinds = new HashSet<MonitorAlertKind>();
        if (_settings.GetBool(DefaultsKey.MonitorAlertCPU) && installed.Contains(AppFeature.MonitorCPU)) kinds.Add(MonitorAlertKind.Cpu);
        if (_settings.GetBool(DefaultsKey.MonitorAlertMemory) && installed.Contains(AppFeature.MonitorMemory)) kinds.Add(MonitorAlertKind.Memory);
        if (_settings.GetBool(DefaultsKey.MonitorAlertDisk) && installed.Contains(AppFeature.MonitorDisk)) kinds.Add(MonitorAlertKind.Disk);
        if (_settings.GetBool(DefaultsKey.MonitorAlertBattery) && installed.Contains(AppFeature.MonitorPower)) kinds.Add(MonitorAlertKind.Battery);
        return kinds;
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) _dispatcher.BeginInvoke(() => { _forced.Add(MonitorSamplingKind.Power); if (_timer.IsEnabled) OnTick(); });
        // After a sleep the old baselines are stale: the next reads set new ones.
        if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => { _cpu.Reset(); _disk.Reset(); _gpu.Reset(); _processes.Reset(); });
    }

    // MARK: Sampling

    private void OnTick()
    {
        if (_disposed) return;
        lock (_gate)
        {
            // A refresh that arrives while one is in flight waits for it.
            if (_running) { _pending = true; return; }
            _running = true;
        }
        var plan = _plan;
        var interval = Interval();
        var tick = _tick;
        var forced = _forced.ToHashSet();
        _forced.Clear();
        _tick += MonitorSamplingPolicy.WakeTicks(plan.Kinds, interval, plan.Foreground);
        var kinds = plan.Kinds.Where(k => MonitorSamplingPolicy.ShouldSample(k, tick, interval, plan.Foreground, forced)).ToHashSet();
        var processKinds = _processKinds;
        Task.Run(async () =>
        {
            try { await SampleAsync(plan, kinds, processKinds).ConfigureAwait(false); }
            catch (Exception e) { Log.Warning(e, "The monitor could not sample"); }
            finally
            {
                bool again;
                lock (_gate) { _running = false; again = _pending; _pending = false; }
                if (again && !_disposed) _ = _dispatcher.BeginInvoke(OnTick);
            }
        });
    }

    private async Task SampleAsync(SamplingPlan plan, HashSet<MonitorSamplingKind> kinds, IReadOnlySet<ProcessKind> processKinds)
    {
        var s = Snapshot;
        var cpuRead = false;
        if (kinds.Contains(MonitorSamplingKind.Cpu))
        {
            var (usage, cores) = _cpu.Read(plan.PerCore);
            s.CpuUsage = _cpu.Usage;
            s.CpuReadAt = _cpu.UsageReadAt;
            s.Cores = plan.PerCore ? cores : null;
            cpuRead = usage is not null;
        }
        if (kinds.Contains(MonitorSamplingKind.GpuUsage) || kinds.Contains(MonitorSamplingKind.GpuUsageTray)) s.Gpus = _gpu.Read() ?? s.Gpus;
        if (kinds.Contains(MonitorSamplingKind.Memory)) s.Memory = MemorySampler.Read();
        if (kinds.Contains(MonitorSamplingKind.Network))
        {
            s.Network = _network.Read();
            s.NetworkTotals = _network.Totals;
            if (_tick % 5 == 0 || s.Addresses.Count == 0) s.Addresses = NetworkSampler.Addresses();
        }
        if (kinds.Contains(MonitorSamplingKind.Disk))
        {
            var now = Notchly.App.Platform.Monitor.Clock.UnbiasedSeconds();
            // A gap over 15 s is dropped: the first read after it only sets a baseline.
            if (now - _diskLastAt > 15) _disk.Reset();
            _diskLastAt = now;
            s.Disks = DiskSampler.Volumes();
            s.DiskActivity = _disk.Activity();
        }
        if (kinds.Contains(MonitorSamplingKind.Power)) s.Power = PowerSampler.Read();
        if (kinds.Contains(MonitorSamplingKind.ConnectedDevices)) s.Devices = await UsbSampler.ReadAsync().ConfigureAwait(false);
        if (kinds.Contains(MonitorSamplingKind.PeripheralBattery)) s.Peripherals = ReadPeripherals();
        s.UptimeSeconds = Environment.TickCount64 / 1000;

        if (plan.Foreground && processKinds.Count > 0 && (cpuRead || kinds.Contains(MonitorSamplingKind.Cpu) || _tick % 2 == 0))
        {
            _processes.Refresh(s.CpuUsage is { } cpu ? cpu * 100 : null);
            var gpuByPid = processKinds.Contains(ProcessKind.Gpu) || processKinds.Contains(ProcessKind.Energy) ? _gpu.ProcessUsage() : null;
            s.TopProcesses = processKinds.ToDictionary(k => k, k => _processes.Top(k, 15, gpuByPid));
        }
        else if (processKinds.Count == 0) s.TopProcesses = new Dictionary<ProcessKind, IReadOnlyList<ProcessRow>>();

        _ = _dispatcher.BeginInvoke(() => Publish(plan, kinds));
    }

    private void Publish(SamplingPlan plan, HashSet<MonitorSamplingKind> kinds)
    {
        if (_disposed) return;
        var s = Snapshot;
        // History is kept for the graphs only while something is looking at them.
        if (plan.Foreground)
        {
            if (kinds.Contains(MonitorSamplingKind.Cpu) && s.CpuUsage is { } cpu) Cpu.Push(cpu);
            if (kinds.Contains(MonitorSamplingKind.GpuUsage) && s.GpuUsage is { } gpu) Gpu.Push(gpu);
            if (kinds.Contains(MonitorSamplingKind.Memory) && s.Memory is { Total: > 0 } memory)
                Memory.Push((double)MetricFormat.SelectedMemory(memory.Used, memory.App, _settings.GetString(DefaultsKey.MonitorMemoryMetric)) / memory.Total);
            if (kinds.Contains(MonitorSamplingKind.Network) && s.Network is { } net) { NetDown.Push(net.Down); NetUp.Push(net.Up); }
            if (kinds.Contains(MonitorSamplingKind.Disk) && s.DiskActivity is { } io) { DiskRead.Push(io.Read); DiskWrite.Push(io.Write); }
            if (kinds.Contains(MonitorSamplingKind.Power) && s.Power is { } power) Battery.Push(power.Percent / 100.0);
        }
        Updated?.Invoke();
    }

    // MARK: Peripherals

    private IReadOnlyList<BluetoothAccessory> ReadPeripherals()
    {
        if (_peripherals is null)
        {
            _peripherals = new BluetoothAccessorySource(work => _dispatcher.BeginInvoke(work));
            _peripherals.Start(() => _disposed || _peripherals is null, _ => { });
            return [];
        }
        return _peripherals.Snapshot().Where(a => a.Connected && a.Percent is not null).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void StopPeripherals()
    {
        _peripherals?.Stop();
        _peripherals = null;
        Snapshot.Peripherals = [];
    }

    // MARK: Helpers for the surfaces

    public IReadOnlyList<double> History(MetricHistory history) => history.PublishedValues(_plan.Foreground);

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        StopPeripherals();
        _gpu.Dispose();
        _disk.Dispose();
        SpeedTest.Cancel();
        _speedTransport.Dispose();
    }
}
