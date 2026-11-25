// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/KeepAwakeManager.swift. A session holds a power request so Windows does
// not sleep on idle (and keeps the display on unless allowed); it can end on a timer, on a low battery, or by
// rule, resumes after a restart, pauses while the PC is locked, and can nudge the pointer. Closed-lid mode
// sets the lid action to "do nothing" for the session and always puts it back, with a marker that survives a crash.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Display;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Power;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Energy;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;
using static Notchly.App.Platform.Native;
using static Notchly.Core.Energy.KeepAwakeSupport;

namespace Notchly.App.Energy;

public sealed class KeepAwakeService : IFeatureService
{
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    private static bool s_launched;

    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly PowerRequest _request = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _battery;
    private readonly DispatcherTimer _jiggle;
    private readonly DispatcherTimer _rules;
    private LidWatcher? _lid;
    private IDisposable? _registration;
    private string? _registeredStorage;
    private DateTime? _end;
    private int? _minutes;
    private SessionTrigger _trigger;
    private bool _active, _paused, _suppressed, _disposed;

    public bool Active => _active;
    /// <summary>The rules started this session, not the user.</summary>
    public bool IsAutomatic => _active && _trigger == SessionTrigger.Automation;
    public bool Paused => _paused;
    public bool ShortcutUnavailable { get; private set; }
    /// <summary>The planned end; null for a session with none.</summary>
    public DateTime? End => _end;
    public int? Minutes => _minutes;
    public double? RemainingSeconds => _active && _end is { } end ? Math.Max(0, (end - DateTime.Now).TotalSeconds) : null;

    /// <summary>The state changed (started, stopped, extended, paused). On the UI thread.</summary>
    public event Action? Changed;
    /// <summary>Once a second while a session runs, for countdowns.</summary>
    public event Action? Tick;
    /// <summary>A session ended on its timer or the battery: worth telling the user.</summary>
    public event Action<EndReason>? Ended;
    /// <summary>Started or stopped; what the tray and island follow.</summary>
    public static event Action<bool>? ActiveChanged;

    public KeepAwakeService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher;
        _tick = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _battery = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(BatteryWatchSeconds) };
        _battery.Tick += (_, _) => CheckBattery();
        _jiggle = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _jiggle.Tick += (_, _) => Jiggle();
        _rules = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _rules.Tick += (_, _) => Evaluate();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.DisplaySettingsChanged += OnDisplays;
    }

    // MARK: Lifecycle

    public void Sync()
    {
        RegisterShortcut();
        var first = !s_launched;
        s_launched = true;
        if (first) RecoverLid();
        if (first && !_active) ResumeOrAutoStart();
        // The rules and the jiggle follow their settings.
        SyncRules();
        if (_active) { UpdateJiggle(); _request.SetDisplay(!_settings.GetBool(DefaultsKey.KeepAwakeAllowDisplaySleep) && !_paused); }
        Evaluate();
    }

    private void RegisterShortcut()
    {
        var storage = _settings.GetBool(DefaultsKey.HotkeyEnabled) ? _settings.GetString(DefaultsKey.KeepAwakeShortcut) : null;
        if (storage == _registeredStorage && (storage is null || _registration is not null || ShortcutUnavailable)) return;
        _registration?.Dispose();
        _registration = null;
        ShortcutUnavailable = false;
        _registeredStorage = storage;
        if (storage is not null && Shortcut.Parse(storage) is { } shortcut)
        {
            _registration = _hotkeys.Register(shortcut, Toggle);
            ShortcutUnavailable = _registration is null;
        }
    }

    private void ResumeOrAutoStart()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var restored = Restore(StoredSession.Parse(_settings.GetString(DefaultsKey.KeepAwakeSessionEnd)), now);
        switch (restored.Kind)
        {
            case RestoreKind.Timed:
                var stored = StoredSession.Parse(_settings.GetString(DefaultsKey.KeepAwakeSessionEnd));
                Begin(DateTime.Now.AddSeconds(restored.RemainingSeconds), stored?.Minutes, SessionTrigger.Manual);
                return;
            case RestoreKind.Indefinite:
                Begin(null, null, SessionTrigger.Manual);
                return;
        }
        _settings.Set(DefaultsKey.KeepAwakeSessionEnd, "");
        if (_settings.GetBool(DefaultsKey.KeepAwakeAutoStart))
        {
            var minutes = SanitizedDuration(_settings.GetInt(DefaultsKey.DefaultDurationMinutes));
            Begin(minutes == 0 ? null : DateTime.Now.AddMinutes(minutes), minutes, SessionTrigger.Launch);
        }
    }

    // MARK: Starting and stopping

    /// <summary>Stops a session, or starts one for the last pick: the saved "until" time or the default duration.</summary>
    public void Toggle()
    {
        if (_active) { Stop(); return; }
        StartLastPick();
    }

    public bool StartLastPick()
    {
        var until = _settings.GetDouble(DefaultsKey.KeepAwakeUntilTime);
        if (_settings.GetBool(DefaultsKey.KeepAwakeSwitchUsesUntil) && until > 0)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds((long)(until * 1000)).LocalDateTime;
            if (date > DateTime.Now) return ActivateUntil(date);
        }
        return Activate(SanitizedDuration(_settings.GetInt(DefaultsKey.DefaultDurationMinutes)));
    }

    /// <summary>Starts a session of the given length (0 is endless) and remembers it as the default.</summary>
    public bool Activate(int minutes)
    {
        minutes = SanitizedDuration(minutes);
        _settings.Set(DefaultsKey.DefaultDurationMinutes, minutes);
        _settings.Set(DefaultsKey.KeepAwakeSwitchUsesUntil, false);
        return Begin(minutes == 0 ? null : DateTime.Now.AddMinutes(minutes), minutes, SessionTrigger.Manual);
    }

    public bool ActivateUntil(DateTime end)
    {
        _settings.Set(DefaultsKey.KeepAwakeSwitchUsesUntil, true);
        _settings.Set(DefaultsKey.KeepAwakeUntilTime, new DateTimeOffset(end).ToUnixTimeMilliseconds() / 1000.0);
        return Begin(end, null, SessionTrigger.Manual);
    }

    /// <summary>+15, +30 or +60 minutes on a timed session.</summary>
    public void Extend(int minutes)
    {
        if (!_active || _end is not { } end) return;
        _end = ExtendedEnd(end, DateTime.Now, minutes);
        _minutes = null;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>The user ends the session (a rule-made one stays quiet until the rules clear).</summary>
    public void Stop()
    {
        if (!_active) return;
        if (_trigger == SessionTrigger.Automation) _suppressed = true;
        EndSession(EndReason.Manual);
    }

    /// <summary>The percentage when a start would be refused because the battery is at or under the limit.</summary>
    public int? BatteryProtectionPercent()
    {
        var battery = PowerService.ReadBattery();
        return KeepAwakeSupport.BatteryProtectionPercent(battery.HasBattery, !battery.PluggedIn, battery.Percent,
            SanitizedBatteryLimit(_settings.GetInt(DefaultsKey.BatteryLimitPercent, DefaultBatteryLimit)));
    }

    private bool Begin(DateTime? end, int? minutes, SessionTrigger trigger)
    {
        if (BatteryProtectionPercent() is not null) return false;
        if (!_request.Hold("Notchly keep awake", keepDisplayOn: !_settings.GetBool(DefaultsKey.KeepAwakeAllowDisplaySleep)))
            return false;
        var wasActive = _active;
        _active = true;
        _paused = false;
        _end = end;
        _minutes = minutes;
        _trigger = trigger;
        if (trigger != SessionTrigger.Automation) _suppressed = false;
        Persist();
        _tick.Start();
        _battery.Start();
        CheckBatteryNow();
        if (!_active) return false;
        UpdateJiggle();
        ApplyLidMode();
        if (!wasActive) ActiveChanged?.Invoke(true);
        Changed?.Invoke();
        Log.Information("Keep awake started ({Trigger}, {End})", trigger, end?.ToString("t") ?? "no end");
        return true;
    }

    private void EndSession(EndReason reason, bool allowSleep = true)
    {
        if (!_active) return;
        _active = false;
        _tick.Stop();
        _battery.Stop();
        _jiggle.Stop();
        _request.Release();
        _end = null;
        _minutes = null;
        if (reason != EndReason.Quit) _settings.Set(DefaultsKey.KeepAwakeSessionEnd, "");
        RestoreLidMode(allowSleep);
        ActiveChanged?.Invoke(false);
        Changed?.Invoke();
        Log.Information("Keep awake ended ({Reason})", reason);
        if (NotifiesOnEnd(reason)) Ended?.Invoke(reason);
    }

    private void Persist()
    {
        if (!ShouldPersist(_trigger)) return;
        var end = _end is { } e ? new DateTimeOffset(e).ToUnixTimeMilliseconds() / 1000.0 : 0;
        _settings.Set(DefaultsKey.KeepAwakeSessionEnd, new StoredSession(end, _minutes).Storage);
    }

    // MARK: Ticking

    private void OnTick()
    {
        if (!_active) return;
        if (!_paused && _end is { } end && DateTime.Now >= end) { EndByTimer(); return; }
        Tick?.Invoke();
    }

    private void EndByTimer()
    {
        var wasAutomatic = _trigger == SessionTrigger.Automation;
        var handOver = KeepAwakeAutomation.ShouldHandOver(EndReason.Timer, wasAutomatic, true, _suppressed,
            batteryBlocks: BatteryProtectionPercent() is not null, conditionsHold: RulesHold());
        EndSession(EndReason.Timer);
        if (handOver) Begin(null, null, SessionTrigger.Automation);
    }

    private void CheckBattery() => CheckBatteryNow();

    private void CheckBatteryNow()
    {
        if (!_active || _paused) return;
        var battery = PowerService.ReadBattery();
        var limit = SanitizedBatteryLimit(_settings.GetInt(DefaultsKey.BatteryLimitPercent, DefaultBatteryLimit));
        if (battery.HasBattery && BatteryEndsSession(!battery.PluggedIn, battery.Percent, limit)) EndSession(EndReason.Battery);
    }

    // MARK: Rules

    private bool RuleOn => _settings.GetBool(DefaultsKey.KeepAwakeExternalDisplay) || _settings.GetBool(DefaultsKey.KeepAwakeConnectedToPower)
                           || _settings.GetBool(DefaultsKey.KeepAwakeRunningApps);

    private void SyncRules()
    {
        if (RuleOn) { if (!_rules.IsEnabled) _rules.Start(); }
        else _rules.Stop();
    }

    private IReadOnlyList<string> SelectedApps() => _settings.GetList(DefaultsKey.KeepAwakeRunningAppBundleIDs);

    private bool RulesHold()
    {
        var externalOn = _settings.GetBool(DefaultsKey.KeepAwakeExternalDisplay);
        var powerOn = _settings.GetBool(DefaultsKey.KeepAwakeConnectedToPower);
        var appsOn = _settings.GetBool(DefaultsKey.KeepAwakeRunningApps);
        var selected = SelectedApps();
        var enabled = KeepAwakeAutomation.EnabledConditions(externalOn, powerOn, appsOn, selected.Count > 0);
        if (enabled.Count == 0) return false;
        var external = externalOn && KeepAwakeAutomation.HasExternalDisplay(Displays.All().Select(d => d.IsBuiltIn));
        var onPower = powerOn && PowerService.ReadBattery() is { PluggedIn: true } or { HasBattery: false };
        var apps = appsOn && selected.Count > 0 && KeepAwakeAutomation.SelectedAppsAreRunning(selected, RunningApps());
        var matching = KeepAwakeAutomation.MatchingConditions(externalOn, external, powerOn, onPower, appsOn, apps);
        return KeepAwakeAutomation.ConditionsSatisfied(matching, enabled, _settings.GetBool(DefaultsKey.KeepAwakeAutomationRequireAll));
    }

    private static List<string> RunningApps()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process) names.Add(process.ProcessName + ".exe");
        }
        return names;
    }

    /// <summary>Starts or stops a rule-made session as the rules say.</summary>
    private void Evaluate()
    {
        if (_disposed) return;
        var holds = RuleOn && RulesHold();
        _suppressed = KeepAwakeAutomation.StillSuppressed(_suppressed, holds);
        switch (KeepAwakeAutomation.Action(true, holds, _active, IsAutomatic, _suppressed))
        {
            case KeepAwakeAutomation.AutomationAction.Activate: Begin(null, null, SessionTrigger.Automation); break;
            case KeepAwakeAutomation.AutomationAction.Deactivate: EndSession(EndReason.Manual); break;
        }
    }

    // MARK: Pause while locked

    private void OnSession(object? sender, SessionSwitchEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed || !_active || !_settings.GetBool(DefaultsKey.KeepAwakePauseWhenLocked)) return;
        if (e.Reason is SessionSwitchReason.SessionLock) Pause();
        else if (e.Reason is SessionSwitchReason.SessionUnlock) Resume();
    });

    private void Pause()
    {
        if (_paused) return;
        _paused = true;
        _request.Release();
        _jiggle.Stop();
        RestoreLidMode();
        Changed?.Invoke();
    }

    private void Resume()
    {
        if (!_paused) return;
        _paused = false;
        if (_end is { } end && DateTime.Now >= end) { EndByTimer(); return; }
        if (_trigger == SessionTrigger.Automation && !(RuleOn && RulesHold())) { EndSession(EndReason.Manual); return; }
        _request.Hold("Notchly keep awake", !_settings.GetBool(DefaultsKey.KeepAwakeAllowDisplaySleep));
        UpdateJiggle();
        ApplyLidMode();
        Changed?.Invoke();
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (e.Mode == PowerModes.StatusChange) { CheckBatteryNow(); Evaluate(); }
    });

    private void OnDisplays(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => { if (!_disposed) Evaluate(); });

    // MARK: Pointer jiggle

    private void UpdateJiggle()
    {
        var on = _active && !_paused && _settings.GetBool(DefaultsKey.KeepAwakeMouseJiggleEnabled);
        if (!on) { _jiggle.Stop(); return; }
        var interval = TimeSpan.FromMinutes(SanitizedJiggleInterval(_settings.GetInt(DefaultsKey.KeepAwakeMouseJiggleIntervalMinutes, DefaultJiggleMinutes)));
        if (_jiggle.IsEnabled && _jiggle.Interval == interval) return;
        _jiggle.Interval = interval;
        _jiggle.Start();
    }

    private void Jiggle()
    {
        if (!_active || _paused) return;
        GetCursorPos(out var point);
        var display = Displays.AtPoint(point.X, point.Y);
        // One pixel along x, away from the display's right edge, then back a moment later.
        var step = display is { } d && point.X >= d.Bounds.Right - 3 ? -1 : 1;
        Move(step);
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            GetCursorPos(out var now);
            // Only if the pointer is still where the nudge left it.
            if (Math.Abs(now.X - (point.X + step)) <= 2 && Math.Abs(now.Y - point.Y) <= 2) Move(-step);
        };
        timer.Start();
    }

    private static void Move(int dx)
    {
        var input = new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = 0, dwFlags = 0x0001 } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // MARK: Closed-lid mode

    private bool LidModeWanted => _active && !_paused && _settings.GetBool(DefaultsKey.ClamshellPreferred);

    private void ApplyLidMode()
    {
        if (!LidModeWanted) return;
        // Save what was there before changing it, once: a second start must not record "do nothing" as the original.
        if (LidRules.LidActionBackup.Parse(_settings.GetString(DefaultsKey.LidActionBackup)) is null)
        {
            if (LidAction.Read() is not { } current) { Log.Information("This PC has no lid action to change"); return; }
            _settings.Set(DefaultsKey.LidActionBackup, current.Storage);
        }
        if (!LidAction.DoNothing()) { Log.Warning("Closed-lid mode could not be applied"); return; }
        // The watcher tells the dimming when the lid moves, and tells the end of the session whether it is still closed.
        if (_lid is null)
        {
            _lid = new LidWatcher();
            _lid.Changed += OnLid;
        }
    }

    private void RestoreLidMode(bool allowSleep = true)
    {
        var closed = _lid?.Closed;
        if (_lid is not null) RestoreDim();
        var backup = LidRules.LidActionBackup.Parse(_settings.GetString(DefaultsKey.LidActionBackup));
        var restored = backup is null;
        if (backup is { } saved && LidAction.Write(saved))
        {
            _settings.Set(DefaultsKey.LidActionBackup, "");
            restored = true;
        }
        // Windows reacts to the lid only when it moves: a lid still closed puts the PC to sleep now.
        if (restored && backup is not null && closed == true && allowSleep) TrySleep();
        else DisposeLid();
    }

    private void DisposeLid()
    {
        if (_lid is null || LidModeWanted) return;
        _lid.Changed -= OnLid;
        _lid.Dispose();
        _lid = null;
    }

    private void OnLid(bool closed)
    {
        if (!_active || !_settings.GetBool(DefaultsKey.DimScreenOnLidClose)) return;
        if (closed)
        {
            var action = LidRules.LidClosed(InternalPanel.Read());
            if (action.Kind != LidRules.DimKind.Dim) return;
            _settings.Set(DefaultsKey.LidDimSavedBrightness, action.Saved);
            InternalPanel.Write(0);
        }
        else RestoreDim();
    }

    private void RestoreDim()
    {
        var action = LidRules.Restoring(_settings.GetDouble(DefaultsKey.LidDimSavedBrightness));
        if (action.Kind != LidRules.DimKind.Restore) return;
        if (InternalPanel.Write(action.Saved)) { _settings.Set(DefaultsKey.LidDimSavedBrightness, 0.0); return; }
        // The panel may not be back yet after a wake: try again a few times.
        var tries = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            tries++;
            if (InternalPanel.Write(action.Saved)) { timer.Stop(); _settings.Set(DefaultsKey.LidDimSavedBrightness, 0.0); }
            else if (tries >= LidRules.DimRestoreRetries) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>After a crash, the marker says the lid action was left changed: put it back.</summary>
    private void RecoverLid()
    {
        if (LidRules.LidActionBackup.Parse(_settings.GetString(DefaultsKey.LidActionBackup)) is { } backup)
        {
            if (LidAction.Write(backup)) _settings.Set(DefaultsKey.LidActionBackup, "");
            Log.Information("Lid action restored after an unclean exit");
        }
        if (_settings.GetDouble(DefaultsKey.LidDimSavedBrightness) > 0) RestoreDim();
    }

    private void TrySleep()
    {
        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(LidRules.SleepRetrySeconds) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            var external = Displays.All().Any(d => !d.IsBuiltIn);
            var battery = PowerService.ReadBattery();
            var onPower = !battery.HasBattery || battery.PluggedIn;
            if (LidRules.SleepIsAllowed(_lid?.Closed, external, onPower))
            {
                timer.Stop();
                Log.Information("Lid still closed after the session: sleeping");
                DisposeLid();
                SetSuspendState(false, false, false);
            }
            else if (attempts >= LidRules.SleepRetries || _lid?.Closed != true) { timer.Stop(); DisposeLid(); }
        };
        timer.Start();
    }

    // MARK: Teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.SessionSwitch -= OnSession;
        SystemEvents.DisplaySettingsChanged -= OnDisplays;
        _rules.Stop();
        _registration?.Dispose();
        // A session saved for the next launch keeps its record; everything the session changed is put back.
        EndSession(EndReason.Quit, allowSleep: false);
        if (_lid is not null) { _lid.Changed -= OnLid; _lid.Dispose(); _lid = null; }
        _request.Dispose();
    }
}
