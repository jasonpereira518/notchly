// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/Notch/NotchAccessoryConnectionMonitor.swift (NotchAccessoryBluetoothSource)
// and of the Bluetooth part of its battery sampler. Windows-specific: two device watchers stand in for IOBluetooth's
// notifications, one on paired Bluetooth endpoints (connections, class of device) and one on Bluetooth device nodes
// (DEVPKEY_Bluetooth_Battery). They are joined by the device's container, so a phone's classic and LE halves stay one.
using Microsoft.Win32;
using Notchly.Core.Notch;
using Serilog;
using Windows.Devices.Enumeration;

namespace Notchly.App.Platform.Bluetooth;

/// <summary>One paired accessory as Windows reports it right now.</summary>
/// <param name="Key">Its container (or address): the same for all of its endpoints and device nodes.</param>
/// <param name="Percent">Its battery when Windows knows it.</param>
public sealed record BluetoothAccessory(string Key, string Name, uint MajorClass, uint MinorClass, bool Connected, int? Percent)
{
    public bool Announces => NotchAccessorySupport.AnnouncesConnection(MajorClass);
    public AccessoryKind Kind => NotchAccessorySupport.Kind(Name, MajorClass, MinorClass);
}

/// <summary>
/// Paired Bluetooth accessories, read from Windows' device watchers. Everything runs through <c>perform</c>, which
/// serializes the watchers' thread-pool callbacks; events leave only as values. The endpoints Windows lists at start
/// are a silent baseline. A connection waits up to two seconds for its battery, so the notice can show it.
/// </summary>
public sealed class BluetoothAccessorySource(Action<Action> perform) : IAccessorySource
{
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LeProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    /// <summary>The device setup classes of Bluetooth device nodes: Bluetooth, and System for the hands-free profiles.</summary>
    private const string NodeFilter = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.ClassGuid:=\"{4d36e97d-e325-11ce-bfc1-08002be10318}\"";
    private const string EndpointFilter = "(System.Devices.Aep.ProtocolId:=\"" + ClassicProtocol + "\" OR System.Devices.Aep.ProtocolId:=\"" + LeProtocol
        + "\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string Address = "System.Devices.Aep.DeviceAddress";
    private const string EndpointContainer = "System.Devices.Aep.ContainerId";
    private const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string AppearanceCategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Category";
    private const string AppearanceSubcategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory";
    /// <summary>DEVPKEY_Bluetooth_Battery: the level Windows' own Bluetooth page shows, 0–100.</summary>
    private const string Battery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string NodeContainer = "System.Devices.ContainerId";

    private static readonly string[] EndpointProperties =
        [IsConnected, Address, EndpointContainer, CodMajor, CodMinor, AppearanceCategory, AppearanceSubcategory];
    private static readonly string[] NodeProperties = [Battery, NodeContainer];

    private static readonly TimeSpan BatteryWait = TimeSpan.FromSeconds(2);
    /// <summary>Some drivers update the battery without telling the watcher: read it again now and then.</summary>
    private static readonly TimeSpan Reread = TimeSpan.FromSeconds(60);

    private sealed record Reading(string Key, int Percent, double ObservedAt);

    private Func<bool> _isCancelled = () => true;
    private Action<AccessoryEvent>? _onEvent;
    private DeviceWatcher? _endpoints, _nodes;
    private bool _endpointsReady, _nodesReady;
    private readonly Dictionary<string, DeviceInformation> _endpointInfo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reading> _readings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nodeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly NotchAccessoryConnectionState _connections = new();
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _holds = new(StringComparer.Ordinal);
    private Timer? _reread;

    private bool Live(DeviceWatcher? watcher, DeviceWatcher sender) => !_isCancelled() && ReferenceEquals(watcher, sender);

    public void Start(Func<bool> isCancelled, Action<AccessoryEvent> onEvent)
    {
        _isCancelled = isCancelled;
        _onEvent = onEvent;
        if (isCancelled()) return;
        try
        {
            var endpoints = DeviceInformation.CreateWatcher(EndpointFilter, EndpointProperties, DeviceInformationKind.AssociationEndpoint);
            endpoints.Added += OnEndpointAdded;
            endpoints.Updated += OnEndpointUpdated;
            endpoints.Removed += OnEndpointRemoved;
            endpoints.EnumerationCompleted += OnEndpointsListed;
            var nodes = DeviceInformation.CreateWatcher(NodeFilter, NodeProperties, DeviceInformationKind.Device);
            nodes.Added += OnNodeAdded;
            nodes.Updated += OnNodeUpdated;
            nodes.Removed += OnNodeRemoved;
            nodes.EnumerationCompleted += OnNodesListed;
            _endpoints = endpoints;
            _nodes = nodes;
            endpoints.Start();
            nodes.Start();
        }
        catch (Exception e) { Log.Warning(e, "Bluetooth accessories could not be watched"); }
    }

    public void Stop()
    {
        _isCancelled = () => true;
        _onEvent = null;
        if (_endpoints is { } endpoints)
        {
            endpoints.Added -= OnEndpointAdded;
            endpoints.Updated -= OnEndpointUpdated;
            endpoints.Removed -= OnEndpointRemoved;
            endpoints.EnumerationCompleted -= OnEndpointsListed;
            StopWatcher(endpoints);
        }
        if (_nodes is { } nodes)
        {
            nodes.Added -= OnNodeAdded;
            nodes.Updated -= OnNodeUpdated;
            nodes.Removed -= OnNodeRemoved;
            nodes.EnumerationCompleted -= OnNodesListed;
            StopWatcher(nodes);
        }
        _endpoints = _nodes = null;
        _reread?.Dispose(); _reread = null;
        foreach (var hold in _holds.Values) hold.Dispose();
        _holds.Clear();
        _endpointInfo.Clear();
        _readings.Clear();
        _nodeKeys.Clear();
        _connected.Clear();
        _names.Clear();
    }

    private static void StopWatcher(DeviceWatcher watcher)
    {
        try { if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); }
        catch (Exception e) { Log.Debug(e, "Bluetooth watcher stop"); }
    }

    /// <summary>Every paired accessory as it stands; for the log and the self-check.</summary>
    public IReadOnlyList<BluetoothAccessory> Snapshot() =>
        _endpointInfo.Values.Select(EndpointKey).Where(k => k.Length > 0).Distinct()
            .Select(Describe).OfType<BluetoothAccessory>().ToList();

    // MARK: Endpoints (connections and classes)

    private void OnEndpointAdded(DeviceWatcher sender, DeviceInformation info) => perform(() =>
    {
        if (!Live(_endpoints, sender)) return;
        _endpointInfo[info.Id] = info;
        Reconcile(EndpointKey(info));
    });

    private void OnEndpointUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => perform(() =>
    {
        if (!Live(_endpoints, sender) || !_endpointInfo.TryGetValue(update.Id, out var info)) return;
        var before = EndpointKey(info);
        info.Update(update);
        var after = EndpointKey(info);
        Reconcile(after);
        if (after != before) Reconcile(before);
    });

    private void OnEndpointRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => perform(() =>
    {
        if (!Live(_endpoints, sender) || !_endpointInfo.Remove(update.Id, out var info)) return;
        Reconcile(EndpointKey(info));
    });

    /// <summary>What is connected when the list is first complete is the silent baseline.</summary>
    private void OnEndpointsListed(DeviceWatcher sender, object args) => perform(() =>
    {
        if (!Live(_endpoints, sender)) return;
        _endpointsReady = true;
        foreach (var accessory in Snapshot())
        {
            _names[accessory.Key] = accessory.Name;
            if (accessory.Connected) _connected.Add(accessory.Key);
        }
        _connections.EstablishBaseline(_connected);
        if (_nodesReady) BaselineBatteries();
    });

    /// <summary>A change in whether an accessory is connected, from any of its endpoints.</summary>
    private void Reconcile(string key)
    {
        if (!_endpointsReady || key.Length == 0) return;
        var accessory = Describe(key);
        if (accessory is not null && accessory.Name.Length > 0) _names[key] = accessory.Name;
        var connected = accessory?.Connected == true;
        if (connected && _connected.Add(key))
        {
            // Phones and computers link up on their own: never announced (upstream announcesConnection).
            if (accessory!.Announces && accessory.Name.Length > 0 && _connections.Connected(key)) Hold(key);
        }
        else if (!connected && _connected.Remove(key))
        {
            _connections.Disconnected(key);
            if (_holds.Remove(key, out var hold)) hold.Dispose();
            Emit(new AccessoryEvent.Disconnected(_names.GetValueOrDefault(key), key));
        }
    }

    /// <summary>A new connection waits briefly for its battery, which Windows reads just after connecting.</summary>
    private void Hold(string key)
    {
        if (_holds.ContainsKey(key)) return;
        _holds[key] = new Timer(_ => perform(() => Release(key)), null, BatteryWait, Timeout.InfiniteTimeSpan);
    }

    private void Release(string key)
    {
        if (_isCancelled() || !_holds.Remove(key, out var hold)) return;
        hold.Dispose();
        if (Describe(key) is { Connected: true } accessory)
            Emit(new AccessoryEvent.Connected(accessory.Name, accessory.MajorClass, accessory.MinorClass, accessory.Percent, key));
    }

    // MARK: Device nodes (batteries)

    private static bool IsBluetoothNode(string id) =>
        id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase);

    private void OnNodeAdded(DeviceWatcher sender, DeviceInformation info) => perform(() =>
    {
        if (!Live(_nodes, sender) || !IsBluetoothNode(info.Id)) return;
        var key = NodeKey(info);
        if (key.Length == 0) return;
        _nodeKeys[info.Id] = key;
        Observe(info.Id, key, BatteryOf(info.Properties));
    });

    private void OnNodeUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => perform(() =>
    {
        if (!Live(_nodes, sender) || !_nodeKeys.TryGetValue(update.Id, out var key)) return;
        if (update.Properties.ContainsKey(Battery)) Observe(update.Id, key, BatteryOf(update.Properties));
    });

    private void OnNodeRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => perform(() =>
    {
        if (!Live(_nodes, sender)) return;
        _nodeKeys.Remove(update.Id);
        _readings.Remove(update.Id);
    });

    private void OnNodesListed(DeviceWatcher sender, object args) => perform(() =>
    {
        if (!Live(_nodes, sender)) return;
        _nodesReady = true;
        _reread = new Timer(_ => RereadBatteries(), null, Reread, Reread);
        if (_endpointsReady) BaselineBatteries();
    });

    private static int? BatteryOf(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(Battery, out var value) && value is byte level && level <= 100 ? level : null;

    /// <summary>A node's battery as Windows reports it; a missing value is no reading, never a change.</summary>
    private void Observe(string nodeId, string key, int? percent)
    {
        if (percent is not { } level) return;
        if (_readings.TryGetValue(nodeId, out var known) && known.Percent == level) return;
        _readings[nodeId] = new Reading(key, level, NotchAccessorySupport.Now);
        if (_endpointsReady && _nodesReady) BatteryChanged(key);
    }

    /// <summary>A new reading of a connected accessory: it completes a waiting connection, then goes out.</summary>
    private void BatteryChanged(string key)
    {
        if (Describe(key) is not { Connected: true, Announces: true, Percent: { } percent } accessory) return;
        if (_holds.ContainsKey(key)) Release(key);
        var observedAt = _readings.Values.Where(r => r.Key == key).Max(r => r.ObservedAt);
        Emit(new AccessoryEvent.Battery(accessory.Name, accessory.MajorClass, accessory.MinorClass, percent, observedAt, key));
    }

    /// <summary>The batteries of accessories already connected: the battery state's silent baseline.</summary>
    private void BaselineBatteries()
    {
        foreach (var key in _connected.ToList()) BatteryChanged(key);
    }

    private async void RereadBatteries()
    {
        try
        {
            var nodes = await DeviceInformation.FindAllAsync(NodeFilter, NodeProperties, DeviceInformationKind.Device);
            perform(() =>
            {
                if (_isCancelled() || _nodes is null) return;
                foreach (var info in nodes)
                    if (_nodeKeys.TryGetValue(info.Id, out var key)) Observe(info.Id, key, BatteryOf(info.Properties));
            });
        }
        catch (Exception e) { Log.Debug(e, "Bluetooth battery reread"); }
    }

    // MARK: Joining endpoints and nodes

    private static string EndpointKey(DeviceInformation info)
    {
        if (info.Properties.TryGetValue(EndpointContainer, out var container) && container is Guid id && id != Guid.Empty) return "C:" + id;
        var address = NotchAccessorySupport.NormalizeAddress(info.Properties.TryGetValue(Address, out var a) ? a as string : null);
        return address.Length > 0 ? "A:" + address : "";
    }

    private static string NodeKey(DeviceInformation info)
    {
        if (info.Properties.TryGetValue(NodeContainer, out var container) && container is Guid id && id != Guid.Empty) return "C:" + id;
        var address = NotchAccessorySupport.AddressFromInstanceId(info.Id);
        return address.Length > 0 ? "A:" + address : "";
    }

    private static bool IsLe(DeviceInformation info) => info.Id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An accessory from all of its endpoints: the classic name first, a declared class of device (else its LE
    /// appearance), connected when any endpoint is, and the newest battery reading of its nodes.
    /// </summary>
    private BluetoothAccessory? Describe(string key)
    {
        var endpoints = _endpointInfo.Values.Where(i => EndpointKey(i) == key).OrderBy(IsLe).ToList();
        if (endpoints.Count == 0) return null;
        var name = endpoints.Select(i => i.Name?.Trim() ?? "").FirstOrDefault(n => n.Length > 0) ?? "";
        uint major = 0, minor = 0;
        if (endpoints.FirstOrDefault(i => !IsLe(i) && Number(i, CodMajor) is > 0) is { } classic)
            (major, minor) = ((uint)Number(classic, CodMajor)!.Value, (uint)(Number(classic, CodMinor) ?? 0));
        else if (endpoints.FirstOrDefault(i => IsLe(i) && Number(i, AppearanceCategory) is not null) is { } le)
            (major, minor) = NotchAccessorySupport.ClassFromAppearance(Number(le, AppearanceCategory)!.Value, Number(le, AppearanceSubcategory) ?? 0);
        var connected = endpoints.Any(i => i.Properties.TryGetValue(IsConnected, out var c) && c is true);
        var reading = _readings.Values.Where(r => r.Key == key).OrderByDescending(r => r.ObservedAt).ThenBy(r => r.Percent).FirstOrDefault();
        return new BluetoothAccessory(key, name, major, minor, connected, reading?.Percent);
    }

    private static int? Number(DeviceInformation info, string property) =>
        info.Properties.TryGetValue(property, out var value) ? value switch
        {
            ushort u => u, byte b => b, uint i => (int)i, int i => i, _ => null,
        } : null;

    private void Emit(AccessoryEvent e)
    {
        if (!_isCancelled()) _onEvent?.Invoke(e);
    }
}

/// <summary>
/// The island's accessory events on the UI thread (upstream NotchAccessoryService's connection monitor). Locking the
/// PC or sleeping stops the watchers; unlocking or waking starts them again with a new silent baseline.
/// </summary>
public sealed class BluetoothAccessories : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _deliver;
    private readonly NotchAccessoryConnectionMonitor _monitor;
    private bool _enabled, _paused;

    public BluetoothAccessories(Action<Action> deliver)
    {
        _deliver = deliver;
        _monitor = new NotchAccessoryConnectionMonitor(work => { lock (_gate) work(); }, deliver,
            perform => new BluetoothAccessorySource(perform));
    }

    public event Action<AccessoryEvent.Connected>? Connected;
    public event Action<AccessoryEvent.Disconnected>? Disconnected;
    public event Action<AccessoryEvent.Battery>? Battery;
    /// <summary>The watchers stopped for a lock or sleep: notices waiting to show are stale.</summary>
    public event Action? Paused;

    /// <summary>Watches while the island announces accessories.</summary>
    public void Sync(bool enabled)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            if (!_paused) _monitor.Start(OnEvent);
        }
        else
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _monitor.Stop();
            _paused = false;
        }
    }

    private void OnEvent(AccessoryEvent e)
    {
        switch (e)
        {
            case AccessoryEvent.Connected connected: Connected?.Invoke(connected); break;
            case AccessoryEvent.Disconnected disconnected: Disconnected?.Invoke(disconnected); break;
            case AccessoryEvent.Battery battery: Battery?.Invoke(battery); break;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _deliver(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) Pause();
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) Resume();
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => _deliver(() =>
    {
        if (e.Mode == PowerModes.Suspend) Pause();
        else if (e.Mode == PowerModes.Resume) Resume();
    });

    private void Pause()
    {
        if (!_enabled || _paused) return;
        _paused = true;
        _monitor.Stop();
        Paused?.Invoke();
    }

    private void Resume()
    {
        if (!_enabled || !_paused) return;
        _paused = false;
        _monitor.Start(OnEvent);
    }

    public void Dispose() => Sync(false);
}
