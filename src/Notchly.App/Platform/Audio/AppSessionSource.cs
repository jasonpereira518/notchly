// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's AppVolumeMixer process discovery and per-app gain (kAudioHardwarePropertyProcessObjectList
// and the process taps): WASAPI sessions on every active output, grouped by owning app, with each session's own
// volume and mute (ISimpleAudioVolume) and level meter (IAudioMeterInformation). No tap or virtual device is needed.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Notchly.Core.Sound;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Audio;

/// <summary>One app in the mixer: every session of the process family that holds an audio connection.</summary>
public sealed record SessionApp(string RowId, string? PersistenceId, string Name, string? ExePath, string? ExeName,
                                IReadOnlyList<uint> Pids, bool IsPlaying, bool IsSystemSounds, double Volume, bool Muted,
                                bool BypassesControl);

public sealed class AppSessionSource : IDisposable
{
    private sealed record Held(MMDevice Device, AudioSessionControl Session, uint Pid, string InstanceId);

    private readonly Dictionary<string, List<Held>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MMDevice> _devices = [];
    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>A session appeared on an output; raised on an audio thread.</summary>
    public event Action? SessionCreated;

    public bool AllowCallAppVolume { get; set; }

    /// <summary>Lists the apps now holding audio connections, and keeps their sessions for volume, mute and meters.</summary>
    public IReadOnlyList<SessionApp> Read()
    {
        Release();
        var apps = new List<SessionApp>();
        try
        {
            var parents = AudioSessions.ParentMap();
            var windowed = WindowedProcesses();
            var raw = new List<Held>();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(device);
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.OnSessionCreated += (_, _) => SessionCreated?.Invoke();
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                        raw.Add(new Held(device, session, session.GetProcessID, session.GetSessionInstanceIdentifier ?? ""));
                    }
                }
                catch (Exception e) when (e is COMException or InvalidCastException)
                {
                    Log.Debug(e, "Sessions of {Device} could not be read", device.ID);
                }
            }

            var systemSounds = raw.Where(h => h.Session.IsSystemSoundsSession || h.Pid == 0).ToList();
            var byOwner = new Dictionary<uint, List<Held>>();
            foreach (var held in raw.Except(systemSounds))
            {
                var owner = MixerSupport.OwningAppPid((int)held.Pid,
                    pid => windowed.Contains((uint)pid) || !parents.ContainsKey((uint)pid),
                    pid => parents.TryGetValue((uint)pid, out var entry) ? (int)entry.Parent : 0);
                var key = (uint)(owner ?? (int)held.Pid);
                (byOwner.TryGetValue(key, out var list) ? list : byOwner[key] = []).Add(held);
            }

            foreach (var (ownerPid, helds) in byOwner)
            {
                var (path, family) = AudioSessions.ProcessImage(ownerPid);
                if (path.Length == 0 && helds.Select(h => AudioSessions.ProcessImage(h.Pid).Path).FirstOrDefault(p => p.Length > 0) is { } fallback)
                    path = fallback;
                var exe = path.Length == 0 ? null : Path.GetFileName(path);
                var name = DisplayName(path, exe);
                var identity = MixerSupport.RowIdentity(family ?? exe, (int)ownerPid, name);
                var active = helds.Any(h => h.Session.State == AudioSessionState.AudioSessionStateActive);
                var volume = helds[0].Session.SimpleAudioVolume;
                var key = identity.RowId;
                // Same-named rows without an identity stay apart by process.
                if (apps.Any(a => a.RowId == key)) key = $"{key}#{ownerPid}";
                _held[key] = helds;
                apps.Add(new SessionApp(key, identity.PersistenceId, name, path.Length == 0 ? null : path, exe,
                    helds.Select(h => h.Pid).Distinct().ToList(), active, false, volume.Volume, volume.Mute,
                    MixerSupport.BypassesVolumeControl(exe, name, AllowCallAppVolume)));
            }

            if (systemSounds.Count > 0)
            {
                _held[MixerSupport.SystemSoundsId] = systemSounds;
                var volume = systemSounds[0].Session.SimpleAudioVolume;
                apps.Add(new SessionApp(MixerSupport.SystemSoundsId, MixerSupport.SystemSoundsId, "System sounds", null, null, [],
                    systemSounds.Any(h => h.Session.State == AudioSessionState.AudioSessionStateActive), true, volume.Volume, volume.Mute, false));
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Debug(e, "Audio sessions could not be read");
        }
        return apps;
    }

    /// <summary>The ids of the sessions an app holds now, so a saved level is applied once to each new one.</summary>
    public IReadOnlyList<string> SessionIds(string rowId) =>
        _held.TryGetValue(rowId, out var held) ? held.Select(h => h.InstanceId).ToList() : [];

    public void SetVolume(string rowId, double volume)
    {
        if (!_held.TryGetValue(rowId, out var held)) return;
        foreach (var h in held)
            try { h.Session.SimpleAudioVolume.Volume = (float)Math.Clamp(volume, 0, 1); }
            catch (Exception e) when (e is COMException or InvalidCastException) { Log.Debug(e, "Volume could not be set"); }
    }

    public void SetMute(string rowId, bool muted)
    {
        if (!_held.TryGetValue(rowId, out var held)) return;
        foreach (var h in held)
            try { h.Session.SimpleAudioVolume.Mute = muted; }
            catch (Exception e) when (e is COMException or InvalidCastException) { Log.Debug(e, "Mute could not be set"); }
    }

    /// <summary>The loudest peak (0–1) of any session the app holds right now.</summary>
    public double Peak(string rowId)
    {
        if (!_held.TryGetValue(rowId, out var held)) return 0;
        var peak = 0.0;
        foreach (var h in held)
            try { peak = Math.Max(peak, h.Session.AudioMeterInformation.MasterPeakValue); }
            catch (Exception e) when (e is COMException or InvalidCastException) { }
        return peak;
    }

    private string DisplayName(string path, string? exe)
    {
        if (path.Length == 0) return exe ?? "App";
        if (_names.TryGetValue(path, out var cached)) return cached;
        string name;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim()
                   ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { name = Path.GetFileNameWithoutExtension(path); }
        return _names[path] = name;
    }

    private static HashSet<uint> WindowedProcesses()
    {
        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private void Release()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }
        _devices.Clear();
        _held.Clear();
    }

    public void Dispose()
    {
        Release();
        _enumerator.Dispose();
    }
}
