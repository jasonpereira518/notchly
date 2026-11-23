// SPDX-License-Identifier: GPL-3.0-or-later
// The default output's volume and the default microphone's mute, with change notifications for
// the island's notices. WASAPI endpoint volume via NAudio; follows default-device changes.
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Serilog;

namespace Notchly.App.Platform.Audio;

public sealed class VolumeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private MMDevice? _output;
    private MMDevice? _input;
    private readonly MMDeviceNotificationClient _notifications;

    /// <summary>(level 0–1, muted). Raised on the UI thread.</summary>
    public event Action<double, bool>? VolumeChanged;
    /// <summary>Microphone muted. Raised on the UI thread.</summary>
    public event Action<bool>? MicrophoneMuteChanged;

    public VolumeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // Raised on an audio worker thread; Attach is posted to the UI thread.
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if ((e.Flow == DataFlow.Render && e.Role == Role.Multimedia) || (e.Flow == DataFlow.Capture && e.Role == Role.Communications))
                _dispatcher.BeginInvoke(Attach);
        };
        Attach();
    }

    public double Volume
    {
        get { lock (_gate) return _output?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; }
        set { lock (_gate) if (_output is not null) _output.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value, 0, 1); }
    }

    public bool Muted
    {
        get { lock (_gate) return _output?.AudioEndpointVolume.Mute ?? false; }
        set { lock (_gate) if (_output is not null) _output.AudioEndpointVolume.Mute = value; }
    }

    public string OutputName { get { lock (_gate) return _output?.FriendlyName ?? "No output"; } }

    /// <summary>Steps like the volume keys (2%), unmuting on the way up.</summary>
    public void Step(int direction, double step = 0.02)
    {
        lock (_gate)
        {
            if (_output is null) return;
            var endpoint = _output.AudioEndpointVolume;
            if (direction > 0 && endpoint.Mute) endpoint.Mute = false;
            endpoint.MasterVolumeLevelScalar = (float)Math.Clamp(Math.Round((endpoint.MasterVolumeLevelScalar + direction * step) / step) * step, 0, 1);
        }
    }

    public void ToggleMute() => Muted = !Muted;

    private void Attach()
    {
        lock (_gate)
        {
            if (_output is not null) _output.AudioEndpointVolume.OnVolumeNotification -= OnOutputNotification;
            if (_input is not null) _input.AudioEndpointVolume.OnVolumeNotification -= OnInputNotification;
            _output?.Dispose();
            _input?.Dispose();
            _output = _enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var output) ? output : null;
            _input = _enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var input) ? input : null;
            try
            {
                if (_output is not null) _output.AudioEndpointVolume.OnVolumeNotification += OnOutputNotification;
                if (_input is not null) _input.AudioEndpointVolume.OnVolumeNotification += OnInputNotification;
            }
            catch (Exception e) { Log.Warning(e, "Volume notifications unavailable"); }
            Log.Information("Audio output: {Output}", _output?.FriendlyName ?? "none");
        }
    }

    private bool _lastInputMute;

    private void OnOutputNotification(AudioVolumeNotificationData data) =>
        _dispatcher.BeginInvoke(() => VolumeChanged?.Invoke(data.MasterVolume, data.Muted));

    private void OnInputNotification(AudioVolumeNotificationData data)
    {
        // Input level changes arrive here too; only a mute flip is a notice.
        if (data.Muted == _lastInputMute) return;
        _lastInputMute = data.Muted;
        _dispatcher.BeginInvoke(() => MicrophoneMuteChanged?.Invoke(data.Muted));
    }

    public void Dispose()
    {
        _notifications.Dispose();
        lock (_gate)
        {
            if (_output is not null) _output.AudioEndpointVolume.OnVolumeNotification -= OnOutputNotification;
            if (_input is not null) _input.AudioEndpointVolume.OnVolumeNotification -= OnInputNotification;
            _output?.Dispose();
            _input?.Dispose();
        }
        _enumerator.Dispose();
    }
}
