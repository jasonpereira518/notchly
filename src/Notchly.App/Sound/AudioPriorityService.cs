// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Audio/AudioPriorityService.swift: the highest-priority output (and
// microphone) that is connected becomes the default as devices come and go. Enforcement runs only on a connect or
// disconnect, a list edit or a flag turning on, never on a mere change of default, so the user's own pick holds.
using System.Windows.Threading;
using Notchly.App.Platform.Audio;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Sound;
using Serilog;

namespace Notchly.App.Sound;

public sealed record PriorityEntry(string Id, string Name, bool Available, bool IsCurrent);

public sealed class AudioPriorityService : IFeatureService
{
    /// <summary>How long a device nobody ranked yet is given before it is placed, so the system's own pick settles first.</summary>
    public static readonly TimeSpan NewDevicePlacementDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan EventEnforcementDelay = TimeSpan.FromMilliseconds(250);

    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly AudioDeviceWatcher _watcher;
    private readonly DispatcherTimer _placement, _enforce;
    private readonly IReadOnlySet<string>?[] _previous = new IReadOnlySet<string>?[2];
    private readonly bool[] _pendingEnforce = new bool[2];
    private bool _outputOn, _inputOn;

    /// <summary>The lists or devices changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    public AudioPriorityService(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _placement = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = NewDevicePlacementDelay };
        _placement.Tick += (_, _) => { _placement.Stop(); PlaceNewDevices(); };
        _enforce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = EventEnforcementDelay };
        _enforce.Tick += (_, _) => { _enforce.Stop(); EnforcePending(); };
        _watcher = new AudioDeviceWatcher(dispatcher);
        _watcher.Changed += (_, _) => OnDevicesChanged();
    }

    public void Sync()
    {
        var output = _settings.GetBool(DefaultsKey.AudioPriorityOutputEnabled);
        var input = _settings.GetBool(DefaultsKey.AudioPriorityInputEnabled);
        // A flag turning on enforces at once; turning off forgets its baseline.
        if (output && !_outputOn) { Seed(false); _pendingEnforce[0] = true; }
        if (input && !_inputOn) { Seed(true); _pendingEnforce[1] = true; }
        if (!output) _previous[0] = null;
        if (!input) _previous[1] = null;
        (_outputOn, _inputOn) = (output, input);
        foreach (var flow in new[] { false, true })
            if (On(flow) && _previous[Index(flow)] is null) _previous[Index(flow)] = Available(flow);
        if (_pendingEnforce.Any(p => p)) _enforce.Start();
    }

    private static int Index(bool input) => input ? 1 : 0;
    private bool On(bool input) => input ? _inputOn : _outputOn;
    private string ListKey(bool input) => input ? DefaultsKey.AudioPriorityInputUIDs : DefaultsKey.AudioPriorityOutputUIDs;

    private IReadOnlyList<string> List(bool input) => MixerSupport.SanitizedIdList(_settings.Get(ListKey(input), new List<string>()));

    private HashSet<string> Available(bool input) => AudioDevices.List(input).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

    private Dictionary<string, string> Names() => _settings.Get(DefaultsKey.AudioPriorityDeviceNames, new Dictionary<string, string>());

    /// <summary>A first-time list starts as: the device in use, then built-in, other hardware, virtual.</summary>
    private void Seed(bool input)
    {
        RememberNames(input);
        if (List(input).Count > 0) return;
        var devices = AudioDevices.List(input);
        var tiers = devices.ToDictionary(d => d.Id, d => d.Tier, StringComparer.Ordinal);
        var list = MixerSupport.InitialPriorityList(devices.Select(d => d.Id), devices.FirstOrDefault(d => d.IsDefault)?.Id, id => tiers.GetValueOrDefault(id, PriorityTier.Hardware));
        _settings.Set(ListKey(input), list.ToList());
    }

    private void RememberNames(bool input)
    {
        var names = Names();
        var changed = false;
        foreach (var device in AudioDevices.List(input))
            if (!names.TryGetValue(device.Id, out var known) || known != device.Name) { names[device.Id] = device.Name; changed = true; }
        if (changed) _settings.Set(DefaultsKey.AudioPriorityDeviceNames, names);
    }

    private void OnDevicesChanged()
    {
        foreach (var input in new[] { false, true })
        {
            if (!On(input)) continue;
            var snapshot = Available(input);
            var changed = MixerSupport.DeviceAvailabilityChanged(_previous[Index(input)], snapshot);
            _previous[Index(input)] = snapshot;
            RememberNames(input);
            if (snapshot.Except(List(input)).Any()) _placement.Start();
            if (changed) _pendingEnforce[Index(input)] = true;
        }
        if (_pendingEnforce.Any(p => p)) { _enforce.Stop(); _enforce.Start(); }
        Changed?.Invoke();
    }

    /// <summary>Devices the list never saw join it once the system has settled on them.</summary>
    private void PlaceNewDevices()
    {
        foreach (var input in new[] { false, true })
        {
            if (!On(input)) continue;
            var devices = AudioDevices.List(input);
            var tiers = devices.ToDictionary(d => d.Id, d => d.Tier, StringComparer.Ordinal);
            var list = List(input);
            var placed = list;
            foreach (var device in devices.Where(d => !list.Contains(d.Id)))
                placed = MixerSupport.PlacingNewPriorityDevice(device.Id, placed, device.IsDefault, id => tiers.GetValueOrDefault(id, PriorityTier.Hardware));
            if (!placed.SequenceEqual(list)) _settings.Set(ListKey(input), placed.ToList());
            _pendingEnforce[Index(input)] |= !placed.SequenceEqual(list);
        }
        if (_pendingEnforce.Any(p => p)) _enforce.Start();
        Changed?.Invoke();
    }

    private void EnforcePending()
    {
        foreach (var input in new[] { false, true })
        {
            var index = Index(input);
            if (!_pendingEnforce[index] || !On(input)) { _pendingEnforce[index] = false; continue; }
            var devices = AudioDevices.List(input);
            // An unplaced device that is current blocks enforcement until it has a place; the placement timer re-runs this.
            var list = List(input);
            if (devices.Any(d => !list.Contains(d.Id) && d.IsDefault)) continue;
            _pendingEnforce[index] = false;
            var target = MixerSupport.FirstAvailablePriorityId(list, devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal));
            var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
            if (!MixerSupport.ShouldSwitchToDevice(target, current)) continue;
            // Priority keeps per-app routes: only the default moves.
            var ok = AudioOutputSwitcher.SetDefault(target!);
            Log.Information("Audio priority set the default {Kind} ({Ok})", input ? "microphone" : "output", ok);
        }
    }

    // MARK: Lists for Settings

    public IReadOnlyList<PriorityEntry> Entries(bool input)
    {
        var devices = AudioDevices.List(input);
        var names = Names();
        return List(input).Select(id =>
        {
            var device = devices.FirstOrDefault(d => d.Id == id);
            return new PriorityEntry(id, device?.Name ?? names.GetValueOrDefault(id, "Unknown device"), device is not null, device?.IsDefault == true);
        }).ToList();
    }

    public void Move(bool input, string id, int offset)
    {
        var list = List(input).ToList();
        var index = list.IndexOf(id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return;
        (list[index], list[target]) = (list[target], list[index]);
        _settings.Set(ListKey(input), list);
        _pendingEnforce[Index(input)] = true;
        _enforce.Start();
        Changed?.Invoke();
    }

    /// <summary>Only a disconnected entry can be removed.</summary>
    public void Remove(bool input, string id)
    {
        if (AudioDevices.List(input).Any(d => d.Id == id)) return;
        _settings.Set(ListKey(input), List(input).Where(item => item != id).ToList());
        var names = Names();
        if (names.Remove(id)) _settings.Set(DefaultsKey.AudioPriorityDeviceNames, names);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _placement.Stop();
        _enforce.Stop();
        _watcher.Dispose();
    }
}
