// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Audio/AppVolumeMixer.swift (rows, volumes, mute, per-app output,
// arrangement, hidden apps and the quieter-on-headphone-disconnect guard) on Windows' own session API. Saved
// volumes and routes are applied to each session when it first appears, so what the user set holds across app
// restarts; afterwards the sliders and Windows' own mixer both move the same session volume.
using System.Windows.Threading;
using Notchly.App.Platform.Audio;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Sound;
using Serilog;

namespace Notchly.App.Sound;

/// <summary>One row of the mixer as the panel and the island show it.</summary>
public sealed record MixerRow(string Id, string? PersistenceId, string Name, string? ExePath, double Volume, bool Muted,
                              bool IsPlaying, bool IsSystemSounds, bool BypassesControl, string? SelectedOutputId,
                              string? EffectiveOutputId, bool OutputUnavailable, bool Pinned);

/// <summary>The meters at one moment: each row's peak, and the master output and microphone levels.</summary>
public sealed record MixerMeters(IReadOnlyDictionary<string, double> Peaks, double OutputVolume, bool OutputMuted, double? InputVolume, bool InputMuted);

public sealed class MixerService : IFeatureService
{
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly AppSessionSource _sessions = new();
    private readonly AudioDeviceWatcher _watcher;
    private readonly EndpointControl _control = new();
    private readonly DispatcherTimer _refreshTimer, _meterTimer, _eventTimer;
    private readonly HashSet<string> _appliedSessions = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _routedPids = [];
    private readonly Dictionary<string, double> _lastAudible = new(StringComparer.Ordinal);
    private readonly List<object> _viewers = [];
    private IReadOnlyList<SessionApp> _apps = [];
    private LoweredOutputState _guard = new();
    private IReadOnlyList<OutputInfo> _previousOutputs = [];
    private string? _previousDefault;
    private bool _started;

    public IReadOnlyList<MixerRow> Rows { get; private set; } = [];
    public IReadOnlyList<AudioEndpoint> Outputs { get; private set; } = [];
    public IReadOnlyList<AudioEndpoint> Inputs { get; private set; } = [];
    /// <summary>Whether this Windows can route one app to its own output.</summary>
    public bool PerAppOutputAvailable => AudioPolicyConfig.Available;

    /// <summary>The rows or devices changed. Raised on the UI thread.</summary>
    public event Action? Changed;
    /// <summary>A meter reading, about ten times a second while something is showing the mixer.</summary>
    public event Action<MixerMeters>? Metered;

    public MixerService(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _watcher = new AudioDeviceWatcher(dispatcher);
        _watcher.Changed += (_, _) => { OnDevicesChanged(); Refresh(); };
        _eventTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _eventTimer.Tick += (_, _) => { _eventTimer.Stop(); Refresh(); };
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _meterTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += (_, _) => Meter();
        _sessions.SessionCreated += () => _dispatcher.BeginInvoke(() => { if (!_eventTimer.IsEnabled) _eventTimer.Start(); });
    }

    public void Sync()
    {
        _sessions.AllowCallAppVolume = _settings.GetBool(DefaultsKey.MixerCallAppVolumeEnabled);
        if (!_started)
        {
            _started = true;
            _previousOutputs = Snapshot(AudioDevices.List(false));
            _previousDefault = AudioDevices.DefaultId(false);
            Refresh();
        }
        else Refresh();
    }

    // MARK: Viewing

    /// <summary>The panel or island shows the mixer: rows refresh and meters run until the handle is disposed.</summary>
    public IDisposable Observe()
    {
        var token = new Viewer(this);
        _viewers.Add(token);
        if (_viewers.Count == 1)
        {
            Refresh();
            _refreshTimer.Start();
            _meterTimer.Start();
        }
        return token;
    }

    private sealed class Viewer(MixerService owner) : IDisposable
    {
        public void Dispose()
        {
            if (!owner._viewers.Remove(this) || owner._viewers.Count > 0) return;
            owner._refreshTimer.Stop();
            owner._meterTimer.Stop();
        }
    }

    // MARK: Reading

    public void Refresh()
    {
        try
        {
            _apps = _sessions.Read();
            Outputs = AudioDevices.List(false);
            Inputs = AudioDevices.List(true);
            ApplySaved();
            Rows = BuildRows();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Log.Debug(e, "The mixer could not refresh");
        }
        Changed?.Invoke();
    }

    private Dictionary<string, double> SavedVolumes() => _settings.Get(DefaultsKey.AppVolumes, new Dictionary<string, double>());
    private Dictionary<string, string> SavedRoutes() => new(MixerSupport.SanitizedRouteMap(_settings.Get(DefaultsKey.AppOutputDevices, new Dictionary<string, string>())));
    private Dictionary<string, string> Hidden() => new(MixerSupport.SanitizedHiddenApps(_settings.Get(DefaultsKey.MixerHiddenApps, new Dictionary<string, string>())));
    private MixerAppArrangement Arrangement() => new(_settings.GetString(DefaultsKey.MixerAppArrangement));

    /// <summary>A saved volume or route is applied once to each session it has not been applied to yet.</summary>
    private void ApplySaved()
    {
        var volumes = SavedVolumes();
        var routes = SavedRoutes();
        var available = Outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var app in _apps)
        {
            if (app.BypassesControl || app.PersistenceId is null) continue;
            var ids = _sessions.SessionIds(app.RowId);
            if (volumes.TryGetValue(app.PersistenceId, out var stored) && ids.Any(id => !_appliedSessions.Contains(id)))
            {
                _sessions.SetVolume(app.RowId, MixerSupport.PlayableVolume(stored));
            }
            foreach (var id in ids) _appliedSessions.Add(id);
            if (!app.IsSystemSounds && routes.TryGetValue(app.PersistenceId, out var route) && available.Contains(route) && AudioPolicyConfig.Available)
                foreach (var pid in app.Pids.Where(pid => _routedPids.Add(pid))) AudioPolicyConfig.SetRoute(pid, route);
        }
    }

    private IReadOnlyList<MixerRow> BuildRows()
    {
        var volumes = SavedVolumes();
        var routes = SavedRoutes();
        var hidden = MixerSupport.HiddenRowIds(Hidden(), _settings.GetBool(DefaultsKey.MixerShowSystemSounds));
        var hideInactive = _settings.GetBool(DefaultsKey.MixerHideInactiveApps);
        var arrangement = Arrangement();
        var available = Outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var defaultOutput = Outputs.FirstOrDefault(o => o.IsDefault)?.Id;
        var rows = new List<MixerRow>();
        foreach (var app in _apps)
        {
            if (MixerSupport.IsHidden(app.PersistenceId, hidden)) continue;
            var stored = app.PersistenceId is not null && volumes.TryGetValue(app.PersistenceId, out var saved)
                ? MixerSupport.SanitizedAppVolume(saved) : app.Volume;
            var selected = app.PersistenceId is not null && routes.TryGetValue(app.PersistenceId, out var route) ? route : null;
            if (!MixerSupport.ShouldShowApp(app.IsPlaying, stored, selected, hideInactive) && !app.IsSystemSounds) continue;
            rows.Add(new MixerRow(app.RowId, app.PersistenceId, app.Name, app.ExePath, stored, stored <= MixerSupport.MutedThreshold,
                app.IsPlaying, app.IsSystemSounds, app.BypassesControl, selected,
                MixerSupport.EffectiveDeviceId(selected, available, defaultOutput), MixerSupport.SelectedDeviceUnavailable(selected, available),
                arrangement.IsPinned(app.PersistenceId)));
        }
        var sorted = rows.OrderBy(r => r.IsSystemSounds ? 1 : 0).ToList();
        sorted.Sort((a, b) => a.IsSystemSounds != b.IsSystemSounds ? (a.IsSystemSounds ? 1 : -1)
            : MixerSupport.DisplayOrderedBefore(a.Name, a.Id, b.Name, b.Id) ? -1 : 1);
        return arrangement.Ordered(sorted, r => r.PersistenceId);
    }

    // MARK: Changing

    public void SetVolume(string rowId, double volume)
    {
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row is null || row.BypassesControl) return;
        volume = Math.Clamp(volume, 0, MixerSupport.PlayableMaximum);
        if (volume > MixerSupport.MutedThreshold) _lastAudible[rowId] = volume;
        _sessions.SetVolume(rowId, MixerSupport.PlayableVolume(volume));
        if (row.PersistenceId is { } id)
        {
            var volumes = SavedVolumes();
            if (MixerSupport.IsUnity(volume)) volumes.Remove(id); else volumes[id] = volume;
            _settings.Set(DefaultsKey.AppVolumes, volumes);
        }
        Rows = Rows.Select(r => r.Id == rowId ? r with { Volume = volume, Muted = volume <= MixerSupport.MutedThreshold } : r).ToList();
        Changed?.Invoke();
    }

    /// <summary>Muting remembers the last audible level; unmuting returns to it (100% when there is none).</summary>
    public void ToggleMute(string rowId)
    {
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row is null) return;
        if (row.Muted) SetVolume(rowId, _lastAudible.TryGetValue(rowId, out var last) && last > MixerSupport.MutedThreshold ? last : 1);
        else
        {
            _lastAudible[rowId] = row.Volume;
            SetVolume(rowId, 0);
        }
    }

    public void ResetVolume(string rowId) => SetVolume(rowId, 1);

    /// <summary>Routes one app to its own output, or back to the system default with null.</summary>
    public void SetOutput(string rowId, string? deviceId)
    {
        var app = _apps.FirstOrDefault(a => a.RowId == rowId);
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (app is null || row?.PersistenceId is not { } id || !AudioPolicyConfig.Available) return;
        foreach (var pid in app.Pids) AudioPolicyConfig.SetRoute(pid, deviceId);
        foreach (var pid in app.Pids) _routedPids.Add(pid);
        var routes = SavedRoutes();
        if (deviceId is null) routes.Remove(id); else routes[id] = deviceId;
        _settings.Set(DefaultsKey.AppOutputDevices, routes);
        Refresh();
    }

    /// <summary>Makes an output the one every app plays to; a success clears the per-app routes and keeps the volumes.</summary>
    public bool SetSystemOutput(string deviceId) => UseSystemOutput(_settings, deviceId, () => { _routedPids.Clear(); Refresh(); });

    /// <summary>The shared "all apps" switch: the island's menu, the shortcut and this mixer all clear per-app routes the same way.</summary>
    public static bool UseSystemOutput(SettingsStore settings, string deviceId, Action? after = null)
    {
        var ok = AudioOutputSwitcher.SetDefault(deviceId);
        var preferences = MixerSupport.PreferencesAfterUniversalOutputSwitch(
            MixerSupport.SanitizedRouteMap(settings.Get(DefaultsKey.AppOutputDevices, new Dictionary<string, string>())),
            settings.Get(DefaultsKey.AppVolumes, new Dictionary<string, double>()), ok);
        if (ok)
        {
            AudioPolicyConfig.ClearAll();
            settings.Set(DefaultsKey.AppOutputDevices, preferences.OutputDeviceIds);
        }
        after?.Invoke();
        return ok;
    }

    public bool SetSoundsOutput(string deviceId) => AudioOutputSwitcher.SetCommunicationsDefault(deviceId);

    public bool SetInput(string deviceId)
    {
        var ok = AudioOutputSwitcher.SetDefault(deviceId);
        Refresh();
        return ok;
    }

    public void TogglePin(string rowId)
    {
        if (Rows.FirstOrDefault(r => r.Id == rowId)?.PersistenceId is not { } id) return;
        var arrangement = Arrangement();
        arrangement.TogglePin(id);
        _settings.Set(DefaultsKey.MixerAppArrangement, arrangement.RawValue);
        Rows = BuildRows();
        Changed?.Invoke();
    }

    /// <summary>One step left (-1) or right (+1) within the pinned or unpinned group.</summary>
    public void Move(string rowId, int offset)
    {
        var visible = Rows.Select(r => r.PersistenceId).Where(id => id is not null).Cast<string>().ToList();
        if (Rows.FirstOrDefault(r => r.Id == rowId)?.PersistenceId is not { } id) return;
        var arrangement = Arrangement();
        arrangement.Move(id, offset, visible);
        _settings.Set(DefaultsKey.MixerAppArrangement, arrangement.RawValue);
        Rows = BuildRows();
        Changed?.Invoke();
    }

    /// <summary>Drags one row beside another (the island's hold-and-drag reorder).</summary>
    public void MoveTo(string rowId, string targetRowId, bool after)
    {
        var visible = Rows.Select(r => r.PersistenceId).Where(id => id is not null).Cast<string>().ToList();
        var from = Rows.FirstOrDefault(r => r.Id == rowId)?.PersistenceId;
        var to = Rows.FirstOrDefault(r => r.Id == targetRowId)?.PersistenceId;
        if (from is null || to is null) return;
        var arrangement = Arrangement();
        arrangement.Move(from, to, after, visible);
        _settings.Set(DefaultsKey.MixerAppArrangement, arrangement.RawValue);
        Rows = BuildRows();
        Changed?.Invoke();
    }

    public void Hide(string rowId)
    {
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row?.PersistenceId is not { } id || row.IsSystemSounds) return;
        var hidden = Hidden();
        hidden[id] = row.Name;
        _settings.Set(DefaultsKey.MixerHiddenApps, hidden);
        Refresh();
    }

    public void Unhide(string persistenceId)
    {
        var hidden = Hidden();
        if (hidden.Remove(persistenceId)) _settings.Set(DefaultsKey.MixerHiddenApps, hidden);
        Refresh();
    }

    public IReadOnlyDictionary<string, string> HiddenApps => Hidden();

    // MARK: Master levels

    public double OutputVolume => AudioDevices.WithEndpoint<double?>(AudioDevices.DefaultId(false) ?? "", d => d.AudioEndpointVolume.MasterVolumeLevelScalar) ?? 0;
    public bool OutputMuted => AudioDevices.WithEndpoint<bool?>(AudioDevices.DefaultId(false) ?? "", d => d.AudioEndpointVolume.Mute) ?? false;
    public double? InputVolume => AudioDevices.WithEndpoint<double?>(AudioDevices.DefaultId(true) ?? "", d => d.AudioEndpointVolume.MasterVolumeLevelScalar);

    public void SetOutputVolume(double volume)
    {
        if (AudioDevices.DefaultId(false) is not { } id) return;
        _control.SetVolume(id, volume);
        // Like the volume keys, a level shown as 0% also throws the mute switch, and raising it unmutes.
        _control.SetMuted(id, MixerSupport.MutesOutput(volume));
    }

    public void SetOutputMuted(bool muted)
    {
        if (AudioDevices.DefaultId(false) is { } id) _control.SetMuted(id, muted);
    }

    public void SetInputVolume(double volume)
    {
        if (AudioDevices.DefaultId(true) is { } id) _control.SetVolume(id, volume);
    }

    private void Meter()
    {
        var peaks = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in Rows) peaks[row.Id] = _sessions.Peak(row.Id);
        var inputId = AudioDevices.DefaultId(true);
        Metered?.Invoke(new MixerMeters(peaks, OutputVolume, OutputMuted, InputVolume, inputId is not null && _control.Muted(inputId) == true));
    }

    // MARK: Quieter when the headphones go

    private static IReadOnlyList<OutputInfo> Snapshot(IEnumerable<AudioEndpoint> outputs) =>
        outputs.Select(o => new OutputInfo(o.Id, o.IsHeadphones)).ToList();

    private void OnDevicesChanged()
    {
        var outputs = AudioDevices.List(false);
        var next = Snapshot(outputs);
        var nextDefault = outputs.FirstOrDefault(o => o.IsDefault)?.Id;
        if (_settings.GetBool(DefaultsKey.MixerLowerVolumeOnHeadphonesDisconnect) || _guard.Lowered is not null)
            _guard = HeadphoneGuard.Apply(_guard, _previousDefault, _previousOutputs, nextDefault, next,
                _settings.GetBool(DefaultsKey.MixerLowerVolumeOnHeadphonesDisconnect),
                _settings.GetInt(DefaultsKey.MixerHeadphonesDisconnectVolumePercent, HeadphoneGuard.DefaultPercent), _control);
        _previousOutputs = next;
        _previousDefault = nextDefault;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _meterTimer.Stop();
        _eventTimer.Stop();
        // Handing the speakers back is the guard's debt: the volume it lowered is not left behind.
        if (_guard.Lowered is { } lowered) HeadphoneGuard.Restore(lowered, _previousOutputs, _control);
        _watcher.Dispose();
        _sessions.Dispose();
    }
}
