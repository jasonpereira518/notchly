// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's CoreAudio device reads in AppVolumeMixer / AudioPriorityService: the active
// endpoints with what the sound features need to know about each (name, default, headphones, rank), the
// endpoint volume and mute switches behind IOutputControl, and a watcher that folds device changes into one
// event on the UI thread.
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Notchly.Core.Sound;
using Serilog;

namespace Notchly.App.Platform.Audio;

public sealed record AudioEndpoint(string Id, string Name, bool IsInput, bool IsDefault, bool IsHeadphones, PriorityTier Tier);

public static class AudioDevices
{
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey EnumeratorKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>The endpoints that are plugged in or paired and on, the default (multimedia / communications) one marked.</summary>
    public static IReadOnlyList<AudioEndpoint> List(bool input)
    {
        var result = new List<AudioEndpoint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var flow = input ? DataFlow.Capture : DataFlow.Render;
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(flow, input ? Role.Communications : Role.Multimedia, out var current) && current is not null)
                using (current) defaultId = current.ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (device)
                {
                    var name = device.FriendlyName;
                    var form = ReadFormFactor(device);
                    result.Add(new AudioEndpoint(device.ID, name, input, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                        !input && MixerSupport.OutputLooksLikeHeadphones(name, device.ID, form),
                        MixerSupport.TierOf(name, ReadString(device, EnumeratorKey))));
                }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Log.Debug(e, "Audio endpoints could not be listed");
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    public static string? DefaultId(bool input)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(input ? DataFlow.Capture : DataFlow.Render, input ? Role.Communications : Role.Multimedia, out var device) || device is null)
                return null;
            using (device) return device.ID;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException) { return null; }
    }

    private static EndpointFormFactor ReadFormFactor(MMDevice device)
    {
        try
        {
            if (device.Properties.Contains(FormFactorKey) && device.Properties[FormFactorKey].Value is uint value && Enum.IsDefined((EndpointFormFactor)(int)value))
                return (EndpointFormFactor)(int)value;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException) { }
        return EndpointFormFactor.Unknown;
    }

    private static string? ReadString(MMDevice device, PropertyKey key)
    {
        try { return device.Properties.Contains(key) ? device.Properties[key].Value as string : null; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException) { return null; }
    }

    /// <summary>The endpoint's own volume and mute (the scalar the volume keys move); null where it has none.</summary>
    public static T? WithEndpoint<T>(string id, Func<MMDevice, T> read)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            return read(device);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException)
        {
            return default;
        }
    }
}

/// <summary>Volume and mute of endpoints by id, for the headphone guard.</summary>
public sealed class EndpointControl : IOutputControl
{
    public double? Volume(string deviceId) => AudioDevices.WithEndpoint<double?>(deviceId, d => d.AudioEndpointVolume.MasterVolumeLevelScalar);

    public bool SetVolume(string deviceId, double volume) =>
        AudioDevices.WithEndpoint(deviceId, d => { d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(volume, 0, 1); return true; });

    public bool? Muted(string deviceId) => AudioDevices.WithEndpoint<bool?>(deviceId, d => d.AudioEndpointVolume.Mute);

    public bool SetMuted(string deviceId, bool muted) =>
        AudioDevices.WithEndpoint(deviceId, d => { d.AudioEndpointVolume.Mute = muted; return true; });
}

/// <summary>
/// Device arrivals, departures, state and default changes as one coalesced event on the UI thread
/// (upstream's HAL listeners with their 0.2 s refresh window).
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _client;
    private readonly DispatcherTimer _timer;
    private bool _devicesChanged, _defaultChanged, _stopped;

    /// <summary>(a device came, went or changed state, a default changed)</summary>
    public event Action<bool, bool>? Changed;

    public AudioDeviceWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var (devices, defaults) = (_devicesChanged, _defaultChanged);
            _devicesChanged = _defaultChanged = false;
            if (!_stopped && (devices || defaults)) Changed?.Invoke(devices, defaults);
        };
        // Raised on an audio worker thread; handled on the UI thread.
        _client = _enumerator.CreateNotificationClient(false);
        _client.DefaultDeviceChanged += (_, _) => Post(false, true);
        _client.DeviceAdded += (_, _) => Post(true, false);
        _client.DeviceRemoved += (_, _) => Post(true, false);
        _client.DeviceStateChanged += (_, _) => Post(true, false);
    }

    private void Post(bool devices, bool defaults)
    {
        if (_stopped) return;
        _dispatcher.BeginInvoke(() =>
        {
            _devicesChanged |= devices;
            _defaultChanged |= defaults;
            if (!_timer.IsEnabled) _timer.Start();
        });
    }

    public void Dispose()
    {
        _stopped = true;
        _timer.Stop();
        _client.Dispose();
        _enumerator.Dispose();
    }
}
