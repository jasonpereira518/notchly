// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/MicMuteService.swift: one switch that mutes every
// microphone, not just the default one, and gives each back exactly what it had. It uses each endpoint's mute switch
// (the control recording apps honour), falling back to its input level where there is none. While it is on, a
// device that is unmuted again is muted again, backing off for stubborn drivers. It is not a privacy lock:
// a recording app that takes the device exclusively can still hear it.
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Sound;
using Serilog;

namespace Notchly.App.Sound;

public sealed class MicMuteService : IFeatureService
{
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly AudioDeviceWatcher _watcher;
    private readonly DispatcherTimer _reassert;
    private readonly Dictionary<string, MicMuteSupport.Retry> _retries = new(StringComparer.Ordinal);
    private readonly List<MMDevice> _listened = [];
    private IDisposable? _registration;
    private string? _registeredStorage;
    private bool _disposed;

    public bool Active => _settings.GetBool(DefaultsKey.MicMuteActive);
    public bool ShortcutUnavailable { get; private set; }
    /// <summary>Muted or unmuted; false means some microphones could not be reached (partial).</summary>
    public event Action<bool, bool>? StateChanged;
    /// <summary>The mute turned on or off (also when it was found on at start): what the tray's badge follows.</summary>
    public static event Action<bool>? ActiveChanged;

    public MicMuteService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher;
        _reassert = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _reassert.Tick += (_, _) => { _reassert.Stop(); Reassert(); };
        _watcher = new AudioDeviceWatcher(dispatcher);
        _watcher.Changed += (_, _) => { if (Active) { Listen(); _reassert.Start(); } };
    }

    public void Sync()
    {
        var storage = _settings.GetBool(DefaultsKey.MicMuteShortcutEnabled) ? _settings.GetString(DefaultsKey.MicMuteShortcut) : null;
        if (storage != _registeredStorage || storage is not null && _registration is null && !ShortcutUnavailable)
        {
            _registration?.Dispose();
            _registration = null;
            ShortcutUnavailable = false;
            _registeredStorage = storage;
            if (storage is not null && Shortcut.Parse(storage) is { } shortcut)
            {
                _registration = _hotkeys.Register(shortcut, () => Toggle());
                ShortcutUnavailable = _registration is null;
            }
        }
        // A mute that survived a restart is held again.
        if (Active && _listened.Count == 0) { Listen(); Reassert(); ActiveChanged?.Invoke(true); }
    }

    /// <summary>Mutes every microphone, or gives them back. False when some could not be reached.</summary>
    public bool Toggle() => Active ? Unmute() : Mute();

    public bool Mute()
    {
        var complete = Sweep();
        _settings.Set(DefaultsKey.MicMuteActive, true);
        Listen();
        StateChanged?.Invoke(true, complete);
        ActiveChanged?.Invoke(true);
        return complete;
    }

    public bool Unmute()
    {
        StopListening();
        _retries.Clear();
        var complete = Restore();
        _settings.Set(DefaultsKey.MicMuteActive, false);
        StateChanged?.Invoke(false, complete);
        ActiveChanged?.Invoke(false);
        return complete;
    }

    // MARK: Muting

    private List<string> Claims() => _settings.IsSaved(DefaultsKey.MicMuteMutedDevices)
        ? _settings.Get(DefaultsKey.MicMuteMutedDevices, new List<string>()) : [];

    /// <summary>Silences every present microphone this app is allowed to; the claims record exactly which.</summary>
    private bool Sweep()
    {
        var recorded = Claims();
        var saved = _settings.Get(DefaultsKey.MicMuteSavedVolumes, new Dictionary<string, double>());
        var claims = new HashSet<string>(recorded, StringComparer.Ordinal);
        var complete = true;
        var present = AudioDevices.List(true);
        foreach (var device in present)
        {
            var owned = claims.Contains(device.Id);
            var (muted, volume) = Read(device.Id);
            if (MicMuteSupport.ShouldLeaveUntouched(owned, MicMuteSupport.IsCaptureSilenced(muted, volume))) continue;
            if (MicMuteSupport.ShouldReplaceSavedVolume(saved.TryGetValue(device.Id, out var existing) ? existing : null, volume))
                saved[device.Id] = volume!.Value;
            complete &= Silence(device.Id);
            claims.Add(device.Id);
        }
        _settings.Set(DefaultsKey.MicMuteSavedVolumes, saved);
        _settings.Set(DefaultsKey.MicMuteMutedDevices, claims.ToList());
        return complete;
    }

    private (bool? Muted, double? Volume) Read(string id) =>
        AudioDevices.WithEndpoint<(bool?, double?)>(id, d =>
        {
            bool? muted = null;
            double? volume = null;
            try { muted = d.AudioEndpointVolume.Mute; } catch (Exception e) when (e is System.Runtime.InteropServices.COMException) { }
            try { volume = d.AudioEndpointVolume.MasterVolumeLevelScalar; } catch (Exception e) when (e is System.Runtime.InteropServices.COMException) { }
            return (muted, volume);
        });

    private static bool Silence(string id) =>
        AudioDevices.WithEndpoint(id, d =>
        {
            try { d.AudioEndpointVolume.Mute = true; return true; }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException)
            {
                // No mute switch: the input level is the control left.
                try { d.AudioEndpointVolume.MasterVolumeLevelScalar = 0; return true; }
                catch (Exception inner) when (inner is System.Runtime.InteropServices.COMException) { return false; }
            }
        });

    // MARK: Restoring

    private bool Restore()
    {
        var saved = _settings.Get(DefaultsKey.MicMuteSavedVolumes, new Dictionary<string, double>());
        var recorded = _settings.IsSaved(DefaultsKey.MicMuteMutedDevices) ? Claims() : null;
        var present = AudioDevices.List(true).Select(d => d.Id).ToList();
        var complete = true;
        var legacy = _settings.GetDouble(DefaultsKey.MicMuteSavedVolume, MicMuteSupport.FallbackVolume);
        foreach (var id in MicMuteSupport.RestoreTargets(recorded, present))
        {
            var (muted, volume) = Read(id);
            var need = MicMuteSupport.NeedFor(muted, volume, saved.ContainsKey(id));
            switch (need.Kind)
            {
                case MicMuteSupport.RestoreKind.Unreadable: complete = false; break;
                case MicMuteSupport.RestoreKind.Restore:
                    complete &= AudioDevices.WithEndpoint(id, d =>
                    {
                        if (need.ClearMute) d.AudioEndpointVolume.Mute = false;
                        if (need.RestoreVolume) d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)MicMuteSupport.VolumeToRestore(id, saved, legacy);
                        return true;
                    });
                    break;
            }
            saved.Remove(id);
        }
        // A headset unplugged while muted is still ours to release when it returns.
        var absent = MicMuteSupport.AbsentClaims(recorded, present);
        _settings.Set(DefaultsKey.MicMuteMutedDevices, absent.ToList());
        _settings.Set(DefaultsKey.MicMuteSavedVolumes, saved.Where(pair => absent.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
        return complete;
    }

    // MARK: Holding the mute

    /// <summary>Listens to every microphone's own volume and mute, so an app or driver that undoes the mute is answered.</summary>
    private void Listen()
    {
        StopListening();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var id = device.ID;
                device.AudioEndpointVolume.OnVolumeNotification += _ => _dispatcher.BeginInvoke(() => { if (!_disposed && Active && Claims().Contains(id)) _reassert.Start(); });
                _listened.Add(device);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Log.Debug(e, "Microphone changes could not be watched");
        }
    }

    private void StopListening()
    {
        foreach (var device in _listened) device.Dispose();
        _listened.Clear();
    }

    private void Reassert()
    {
        if (!Active) return;
        var now = Environment.TickCount64 / 1000.0;
        var claims = Claims().ToHashSet(StringComparer.Ordinal);
        DateTime? retryAt = null;
        var saved = _settings.Get(DefaultsKey.MicMuteSavedVolumes, new Dictionary<string, double>());
        foreach (var device in AudioDevices.List(true))
        {
            if (!claims.Contains(device.Id))
            {
                // A new microphone plugged in while muted is muted too.
                var (m, v) = Read(device.Id);
                if (MicMuteSupport.IsCaptureSilenced(m, v)) continue;
                if (MicMuteSupport.ShouldSaveVolume(v)) saved[device.Id] = v!.Value;
                claims.Add(device.Id);
            }
            var (muted, volume) = Read(device.Id);
            if (MicMuteSupport.IsCaptureSilenced(muted, volume)) { _retries.Remove(device.Id); continue; }
            var retry = MicMuteSupport.Retry.Next(device.Id, false, _retries.GetValueOrDefault(device.Id), now);
            _retries[device.Id] = retry;
            if (retry.Deadline is { } deadline && deadline > now)
            {
                // A stubborn driver: try again after the backoff, not at once.
                var at = DateTime.Now.AddSeconds(deadline - now);
                retryAt = retryAt is { } soonest && soonest < at ? soonest : at;
                continue;
            }
            Silence(device.Id);
        }
        _settings.Set(DefaultsKey.MicMuteMutedDevices, claims.ToList());
        _settings.Set(DefaultsKey.MicMuteSavedVolumes, saved);
        if (retryAt is { } next)
        {
            _reassert.Interval = TimeSpan.FromSeconds(Math.Clamp((next - DateTime.Now).TotalSeconds, 0.25, 30));
            _reassert.Start();
        }
        else _reassert.Interval = TimeSpan.FromMilliseconds(250);
    }

    public void Dispose()
    {
        _disposed = true;
        _reassert.Stop();
        _registration?.Dispose();
        _watcher.Dispose();
        StopListening();
        // Switching the feature off unmutes; quitting with it on leaves the mute held, as upstream persists it.
        if (Active && !_settings.IsAvailable(AppFeature.MicMute))
        {
            Restore();
            _settings.Set(DefaultsKey.MicMuteActive, false);
            ActiveChanged?.Invoke(false);
        }
    }
}
