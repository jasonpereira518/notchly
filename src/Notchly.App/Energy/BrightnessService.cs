// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Display/BrightnessService.swift. Every display gets a route: the built-in
// panel through WMI, an external monitor through DDC/CI, or a software dim (an overlay) for a display that does
// neither or that the user sent there. Hardware calls are slow and run on one worker with the newest value per
// display, as upstream coalesces writes; overlays are cheap and follow at once. Levels are 0...1.
using System.Management;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Display;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Energy;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Energy;

public enum DisplayRouteKind { Internal, Ddc, Software }

/// <summary>One controllable display as the panel and the island see it.</summary>
public sealed class BrightnessDisplay
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DisplayRouteKind Kind { get; internal set; }
    public bool IsBuiltIn { get; init; }
    public bool IsPrimary { get; internal set; }
    /// <summary>The slider level 0...1, including the extended-dimming quarter when it is on.</summary>
    public double Level { get; internal set; }
    public bool ExtendedDimming { get; internal set; }
    internal IntPtr Handle { get; set; }
    internal RECT Bounds { get; set; }
    internal DdcMonitor? Ddc { get; set; }
    internal DimOverlay? Overlay { get; set; }
    internal DateTime LevelAt { get; set; } = DateTime.MinValue;
    /// <summary>The picture's own dimming (1 is none) of the extended range or of a software dim.</summary>
    public double Picture { get; internal set; } = 1;
    public bool HasOverlay => Overlay is not null;
    /// <summary>Only an external monitor can be sent to software dimming by hand.</summary>
    public bool CanChooseSoftware => !IsBuiltIn;
}

public sealed class BrightnessService : IFeatureService
{
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, (double? Level, double Delta)> _pending = [];
    private readonly Dictionary<string, double> _lastSoftwareLevels = [];
    private readonly AutoResetEvent _signal = new(false);
    private readonly DispatcherTimer _rebuild;
    private readonly List<IDisposable> _registrations = [];
    private Thread? _worker;
    private volatile bool _stopping;
    private bool _running, _disposed;
    private ManagementEventWatcher? _internalWatcher;
    private DateTime _internalWrittenAt = DateTime.MinValue;
    private string? _registeredShortcuts;
    private int _retryIndex;
    private DispatcherTimer? _retry;

    private List<BrightnessDisplay> _displays = [];

    public IReadOnlyList<BrightnessDisplay> Displays => _displays;
    public bool ShortcutUnavailable { get; private set; }
    /// <summary>The list or a level changed. On the UI thread.</summary>
    public event Action? Changed;
    /// <summary>A level was set from here (not by the monitor itself): what the island's notice shows.</summary>
    public event Action<BrightnessDisplay>? LevelSet;
    /// <summary>Any display's level or list changed, for the island's slider, which looks the service up when it draws.</summary>
    public static event Action? AnyChanged;
    /// <summary>A shortcut stepped a display: the island shows its notice (the built-in panel's own events do for it).</summary>
    public static event Action<BrightnessDisplay>? Stepped;

    public BrightnessService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher;
        _rebuild = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(BrightnessSupport.RebuildDebounceSeconds) };
        _rebuild.Tick += (_, _) => { _rebuild.Stop(); Rebuild(); };
    }

    // MARK: Lifecycle

    public void Sync()
    {
        var on = _settings.GetBool(DefaultsKey.BrightnessControlEnabled);
        if (on && !_running) Start();
        else if (!on && _running) Stop();
        RegisterShortcuts();
    }

    private void Start()
    {
        _running = true;
        _stopping = false;
        _worker = new Thread(Work) { IsBackground = true, Name = "Notchly brightness" };
        _worker.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChange;
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSession;
        StartInternalWatcher();
        Rebuild();
    }

    private void Stop()
    {
        _running = false;
        _stopping = true;
        _signal.Set();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.SessionSwitch -= OnSession;
        _retry?.Stop();
        _rebuild.Stop();
        try { _internalWatcher?.Stop(); _internalWatcher?.Dispose(); } catch { /* already gone */ }
        _internalWatcher = null;
        foreach (var d in _displays) { d.Overlay?.Dispose(); d.Ddc?.Dispose(); }
        _displays = [];
        RaiseChanged();
    }

    private void OnDisplayChange(object? sender, EventArgs e) => _dispatcher.BeginInvoke(ScheduleRebuild);

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => ScheduleRebuild(wake: true));
    }

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) _dispatcher.BeginInvoke(() => ScheduleRebuild());
    }

    private void ScheduleRebuild(bool wake = false)
    {
        if (!_running) return;
        _rebuild.Interval = TimeSpan.FromSeconds(wake ? BrightnessSupport.WakeSettleSeconds : BrightnessSupport.RebuildDebounceSeconds);
        _rebuild.Stop();
        _rebuild.Start();
    }

    // MARK: Building the list

    private void Rebuild()
    {
        if (!_running) return;
        var forced = _settings.GetList(DefaultsKey.BrightnessForcedSoftwarePaths).ToHashSet();
        var extended = _settings.GetList(DefaultsKey.BrightnessExtendedDimmingPaths).ToHashSet();
        var previous = _displays.ToDictionary(d => d.Id);
        var list = new List<BrightnessDisplay>();
        var needsRetry = false;
        var panelIds = InternalPanel.HardwareIds();
        var hasInternal = panelIds.Count > 0;
        var panelTaken = false;
        foreach (var info in Platform.Windowing.Displays.All())
        {
            var identity = MonitorIdentity.Of(info.DeviceName);
            // The panel is the display marked internal, or on a hybrid-graphics PC the one Windows can set the backlight of.
            var isPanel = !panelTaken && hasInternal
                          && (info.IsBuiltIn || MonitorIdentity.HardwareId(identity.Id) is { } hardware && panelIds.Contains(hardware));
            panelTaken |= isPanel;
            var (id, name) = isPanel ? ("internal", "Built-in display") : identity;
            previous.TryGetValue(id, out var old);
            var display = new BrightnessDisplay { Id = id, Name = name, IsBuiltIn = isPanel }
                .WithGeometry(info);
            display.ExtendedDimming = extended.Contains(id);
            display.Overlay = old?.Overlay;
            display.Picture = old?.Picture ?? 1;
            if (isPanel)
            {
                display.Kind = DisplayRouteKind.Internal;
                display.Level = ReadInternalLevel(display);
            }
            else if (!isPanel && !forced.Contains(id) && (old?.Ddc ?? DdcMonitor.Open(info.Handle)) is { } ddc)
            {
                display.Kind = DisplayRouteKind.Ddc;
                display.Ddc = ddc;
                var read = ddc.Read();
                display.Level = display.ExtendedDimming ? BrightnessSupport.LevelFrom(read ?? 1, display.Picture) : read ?? old?.Level ?? 1;
            }
            else
            {
                display.Kind = DisplayRouteKind.Software;
                var remembered = old is { Kind: DisplayRouteKind.Software } ? old.Level
                    : _lastSoftwareLevels.TryGetValue(id, out var last) ? BrightnessSupport.ReconnectedDimLevel(last) : 1;
                display.Level = remembered;
                display.Picture = remembered;
                if (!isPanel && !forced.Contains(id)) needsRetry = true;
            }
            if (old is not null && old.Ddc is not null && display.Ddc != old.Ddc) old.Ddc.Dispose();
            display.LevelAt = DateTime.Now;
            list.Add(display);
            ApplyOverlay(display);
        }
        // A display that went away takes its overlay and monitor handle with it.
        foreach (var gone in previous.Values.Where(p => list.All(d => d.Id != p.Id)))
        {
            if (gone.Kind == DisplayRouteKind.Software) _lastSoftwareLevels[gone.Id] = gone.Level;
            gone.Overlay?.Dispose();
            gone.Ddc?.Dispose();
        }
        var primary = list.FirstOrDefault(d => d.Bounds.Left == 0 && d.Bounds.Top == 0) ?? list.FirstOrDefault();
        foreach (var d in list) d.IsPrimary = d == primary;
        _displays = list;
        if (needsRetry) ScheduleDiscoveryRetry();
        else { _retry?.Stop(); _retryIndex = 0; }
        RaiseChanged();
    }

    private double ReadInternalLevel(BrightnessDisplay display)
    {
        var read = InternalPanel.Read();
        if (read is null) return display.Level > 0 ? display.Level : 1;
        return display.ExtendedDimming ? BrightnessSupport.LevelFrom(read.Value, display.Picture) : read.Value;
    }

    /// <summary>A monitor that was asleep may answer DDC a moment later: look again after 2, 5 and 15 seconds.</summary>
    private void ScheduleDiscoveryRetry()
    {
        if (_retryIndex >= BrightnessSupport.DiscoveryRetrySeconds.Length) return;
        _retry?.Stop();
        _retry = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(BrightnessSupport.DiscoveryRetrySeconds[_retryIndex++]),
        };
        _retry.Tick += (_, _) => { _retry!.Stop(); Rebuild(); };
        _retry.Start();
    }

    // MARK: Setting levels

    private void RaiseChanged()
    {
        Changed?.Invoke();
        AnyChanged?.Invoke();
    }

    public BrightnessDisplay? Find(string id) => _displays.FirstOrDefault(d => d.Id == id);

    /// <summary>The display the island's slider follows: the built-in panel, else the first one.</summary>
    public BrightnessDisplay? Focus => _displays.FirstOrDefault(d => d.IsBuiltIn) ?? _displays.FirstOrDefault();

    public void SetLevel(string id, double level)
    {
        if (Find(id) is not { } display) return;
        level = BrightnessSupport.Clamp(level);
        display.Level = level;
        display.LevelAt = DateTime.Now;
        ApplyOverlay(display);
        lock (_gate) _pending[id] = (level, 0);
        _signal.Set();
        RaiseChanged();
        LevelSet?.Invoke(display);
    }

    /// <summary>A key or shortcut press: one step of the configured size up or down.</summary>
    public void Step(string id, int direction)
    {
        if (Find(id) is not { } display) return;
        var step = BrightnessSupport.ParseStep(_settings.GetString(DefaultsKey.BrightnessKeyStep));
        var delta = direction * BrightnessSupport.Fraction(BrightnessKeyStep.Standard);
        delta = BrightnessSupport.Limited(step, delta);
        var age = (DateTime.Now - display.LevelAt).TotalSeconds;
        if (display.Kind == DisplayRouteKind.Software || BrightnessSupport.TrustsRememberedLevel(age))
        {
            SetLevel(id, BrightnessSupport.SteppedLevel(display.Level, delta));
            return;
        }
        // The monitor may have moved on its own: read it first, merging presses made meanwhile into one step.
        lock (_gate)
        {
            var current = _pending.TryGetValue(id, out var p) ? p : (null, 0);
            _pending[id] = (current.Item1, current.Item2 + delta);
        }
        _signal.Set();
    }

    public void ForceSoftware(string id, bool on)
    {
        var list = _settings.GetList(DefaultsKey.BrightnessForcedSoftwarePaths).ToList();
        if (on && !list.Contains(id)) list.Add(id);
        if (!on) list.Remove(id);
        // The monitor goes back to full before it changes route, so no old-scale level is left behind.
        if (Find(id) is { } display && display.Kind != DisplayRouteKind.Internal) SetLevel(id, 1);
        _settings.SetList(DefaultsKey.BrightnessForcedSoftwarePaths, list);
        Rebuild();
    }

    public void SetExtendedDimming(string id, bool on)
    {
        var list = _settings.GetList(DefaultsKey.BrightnessExtendedDimmingPaths).ToList();
        if (on && !list.Contains(id)) list.Add(id);
        if (!on) list.Remove(id);
        _settings.SetList(DefaultsKey.BrightnessExtendedDimmingPaths, list);
        if (Find(id) is not { } display) return;
        display.ExtendedDimming = on;
        // The picture is restored before the panel brightens, never the other way round.
        if (!on) display.Picture = 1;
        SetLevel(id, display.Level);
    }

    /// <summary>The picture's dim as an overlay: the software route's whole range, or the extended quarter.</summary>
    private void ApplyOverlay(BrightnessDisplay display)
    {
        double factor = display.Kind switch
        {
            DisplayRouteKind.Software => BrightnessSupport.SoftwareDimFactor(display.Level),
            _ when display.ExtendedDimming => BrightnessSupport.ExtendedDimming(display.Level).Picture,
            _ => 1,
        };
        display.Picture = factor;
        if (factor >= 1)
        {
            display.Overlay?.Dispose();
            display.Overlay = null;
            return;
        }
        display.Overlay ??= new DimOverlay(display.Bounds);
        display.Overlay.Cover(display.Bounds);
        display.Overlay.Factor = factor;
    }

    // MARK: The worker

    private void Work()
    {
        var lastCommand = DateTime.MinValue;
        while (!_stopping)
        {
            _signal.WaitOne();
            if (_stopping) return;
            Dictionary<string, (double? Level, double Delta)> batch;
            lock (_gate) { batch = new(_pending); _pending.Clear(); }
            foreach (var (id, job) in batch)
            {
                BrightnessDisplay? display = null;
                _dispatcher.Invoke(() => display = Find(id));
                if (display is null) continue;
                var gap = TimeSpan.FromMilliseconds(BrightnessSupport.CommandIntervalMilliseconds) - (DateTime.Now - lastCommand);
                if (gap > TimeSpan.Zero) Thread.Sleep(gap);
                try { Apply(display, job); }
                catch (Exception e) { Log.Debug(e, "Brightness write failed"); }
                lastCommand = DateTime.Now;
            }
        }
    }

    private void Apply(BrightnessDisplay display, (double? Level, double Delta) job)
    {
        var level = job.Level;
        if (level is null)
        {
            // A step on a stale level: read the real one first.
            double? hardware = display.Kind switch
            {
                DisplayRouteKind.Internal => InternalPanel.Read(),
                DisplayRouteKind.Ddc => display.Ddc?.Read(),
                _ => null,
            };
            var current = hardware is { } h && display.ExtendedDimming ? BrightnessSupport.LevelFrom(h, display.Picture) : hardware ?? display.Level;
            level = BrightnessSupport.SteppedLevel(current, job.Delta);
            _dispatcher.Invoke(() => { display.Level = level.Value; display.LevelAt = DateTime.Now; ApplyOverlay(display); RaiseChanged(); LevelSet?.Invoke(display); });
        }
        var components = display.ExtendedDimming ? BrightnessSupport.ExtendedDimming(level.Value) : new BrightnessSupport.Components(level.Value, 1);
        switch (display.Kind)
        {
            case DisplayRouteKind.Internal:
                _internalWrittenAt = DateTime.Now;
                InternalPanel.Write(components.Hardware);
                break;
            case DisplayRouteKind.Ddc:
                if (display.Ddc is { } ddc && !ddc.Write(components.Hardware))
                    Log.Debug("DDC write refused for {Display}", display.Name);
                break;
        }
    }

    // MARK: Brightness changed outside this app

    private void StartInternalWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            _internalWatcher = new ManagementEventWatcher(scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _internalWatcher.EventArrived += (_, e) =>
            {
                if ((DateTime.Now - _internalWrittenAt).TotalMilliseconds < 800) return;
                var percent = Convert.ToInt32(e.NewEvent["Brightness"]);
                _dispatcher.BeginInvoke(() =>
                {
                    if (!_running || _displays.FirstOrDefault(d => d.Kind == DisplayRouteKind.Internal) is not { } internalDisplay) return;
                    var hardware = Math.Clamp(percent / 100.0, 0, 1);
                    internalDisplay.Level = internalDisplay.ExtendedDimming ? BrightnessSupport.LevelFrom(hardware, internalDisplay.Picture) : hardware;
                    internalDisplay.LevelAt = DateTime.Now;
                    RaiseChanged();
                });
            };
            _internalWatcher.Start();
        }
        catch (Exception e) { Log.Information("Brightness events unavailable: {Message}", e.Message); }
    }

    // MARK: Shortcuts

    private void RegisterShortcuts()
    {
        var wanted = _running && _settings.GetBool(DefaultsKey.DisplayBrightnessShortcutsEnabled)
            ? _settings.GetString(DefaultsKey.DisplayBrightnessDecreaseShortcut) + "|" + _settings.GetString(DefaultsKey.DisplayBrightnessIncreaseShortcut)
            : null;
        if (wanted == _registeredShortcuts) return;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        ShortcutUnavailable = false;
        _registeredShortcuts = wanted;
        if (wanted is null) return;
        var parts = wanted.Split('|');
        void Register(string storage, int direction)
        {
            if (Shortcut.Parse(storage) is not { } shortcut) return;
            var handle = _hotkeys.Register(shortcut, () => StepFromShortcut(direction));
            if (handle is null) ShortcutUnavailable = true;
            else _registrations.Add(handle);
        }
        Register(parts[0], -1);
        Register(parts[1], +1);
    }

    /// <summary>The display under the pointer, or the primary one; nothing when that display cannot be controlled.</summary>
    private void StepFromShortcut(int direction)
    {
        GetCursorPos(out var point);
        var pointer = _displays.FirstOrDefault(d => point.X >= d.Bounds.Left && point.X < d.Bounds.Right && point.Y >= d.Bounds.Top && point.Y < d.Bounds.Bottom);
        var primary = _displays.FirstOrDefault(d => d.IsPrimary);
        var target = BrightnessSupport.ShortcutDisplay(true, pointer?.Id, primary?.Id, _displays.Select(d => d.Id).ToList());
        if (target is null) return;
        Step(target, direction);
        if (Find(target) is { } stepped) Stepped?.Invoke(stepped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        if (_running) Stop();
        _signal.Dispose();
    }
}

internal static class BrightnessDisplayExtensions
{
    public static BrightnessDisplay WithGeometry(this BrightnessDisplay display, DisplayInfo info)
    {
        display.Handle = info.Handle;
        display.Bounds = info.Bounds;
        return display;
    }
}
