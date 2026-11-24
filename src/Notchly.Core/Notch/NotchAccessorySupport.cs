// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Notch/NotchAccessorySupport.swift, the lifecycle of NotchAccessoryConnectionMonitor.swift
// and the pending notices of NotchAccessoryService.swift. Windows-specific: icons are Segoe Fluent glyphs, LE devices
// without a class of device take one from their appearance, and Windows reads its own batteries (no system monitor).
using System.Diagnostics;
using System.Text.RegularExpressions;
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Notch;

/// <summary>What an accessory is, for its icon (upstream's SF Symbol names).</summary>
public enum AccessoryKind
{
    /// <summary>Upstream dot.radiowaves.left.and.right: any other Bluetooth device.</summary>
    Device,
    Headphones, AirPods, AirPodsPro, AirPodsMax,
    Microphone, Speaker, Car, Tv, GameController, Remote,
    Keyboard, Mouse, Trackpad, Printer, Watch, Glasses,
}

/// <summary>Upstream PeripheralBatteryKind, in its order: the family a battery reading belongs to.</summary>
public enum PeripheralBatteryKind { Keyboard, Mouse, Trackpad, Audio, Device }

/// <summary>One battery reading of an accessory (upstream PeripheralBatteryDevice).</summary>
/// <param name="Id">The source of the reading; the same accessory may move between sources.</param>
public sealed record PeripheralBatteryDevice(string Id, string Name, int Percent, PeripheralBatteryKind Kind);

/// <summary>Readings with the time each was observed (upstream PeripheralBatterySample).</summary>
public sealed record PeripheralBatterySample(IReadOnlyList<PeripheralBatteryDevice> Devices, IReadOnlyDictionary<string, double> ObservedAt);

public static class NotchAccessorySupport
{
    public const int LowThreshold = 20;
    public const int RecoveryThreshold = 25;
    /// <summary>Accessories whose battery episodes are remembered, oldest forgotten first.</summary>
    public const int RememberedDevices = 128;
    /// <summary>Upstream NotchEvent.accessory: 4 s at the battery's priority.</summary>
    public const double NoticeSeconds = 4;
    public const int NoticePriority = 1;

    // Upstream NotchActivityStrings.enUS.
    public const string Connected = "Connected";
    public const string LowBattery = "Low battery";

    /// <summary>
    /// The island announces accessories (upstream isEnabled). Windows reads the batteries itself, so the
    /// system monitor's Power feature is not required as upstream's sampler is.
    /// </summary>
    public static bool IsEnabled(SettingsStore store) =>
        NotchSupport.IsEnabled(store) && store.IsAvailable(AppFeature.NotchAccessories)
        && store.GetBool(DefaultsKey.NotchAccessoriesEnabled);

    /// <summary>Seconds on a monotonic clock, for when readings were observed.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The family a name says (upstream PeripheralBatterySupport.kind with a product name only).</summary>
    public static PeripheralBatteryKind NamedKind(string name)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized.Contains("keyboard")) return PeripheralBatteryKind.Keyboard;
        if (normalized.Contains("mouse")) return PeripheralBatteryKind.Mouse;
        if (normalized.Contains("trackpad") || normalized.Contains("track pad")) return PeripheralBatteryKind.Trackpad;
        if (normalized.Contains("airpods") || normalized.Contains("headphone") || normalized.Contains("headset") || normalized.Contains("buds"))
            return PeripheralBatteryKind.Audio;
        return PeripheralBatteryKind.Device;
    }

    /// <summary>The icon for a battery family, with AirPods models by name (upstream symbol(for:name:)).</summary>
    public static AccessoryKind Kind(PeripheralBatteryKind kind, string name)
    {
        if (kind == PeripheralBatteryKind.Device) kind = NamedKind(name);
        switch (kind)
        {
            case PeripheralBatteryKind.Audio:
                var model = name.ToLowerInvariant();
                if (model.Contains("airpods max")) return AccessoryKind.AirPodsMax;
                if (model.Contains("airpods pro")) return AccessoryKind.AirPodsPro;
                if (model.Contains("airpods")) return AccessoryKind.AirPods;
                return AccessoryKind.Headphones;
            case PeripheralBatteryKind.Keyboard: return AccessoryKind.Keyboard;
            case PeripheralBatteryKind.Mouse: return AccessoryKind.Mouse;
            case PeripheralBatteryKind.Trackpad: return AccessoryKind.Trackpad;
            default: return AccessoryKind.Device;
        }
    }

    /// <summary>
    /// Phones, tablets, computers and network access points are not accessories, and their Bluetooth links come
    /// and go without anyone reaching for them. Every other class announces a connection, including a device that
    /// declares no class at all.
    /// </summary>
    public static bool AnnouncesConnection(uint majorClass) => majorClass is not (>= 0x01 and <= 0x03);

    /// <summary>
    /// A name says what an accessory is only until someone renames it; the Bluetooth class of device it announces
    /// still does. Major and minor classes as the Bluetooth assigned numbers define them; a telling name wins.
    /// </summary>
    public static AccessoryKind Kind(string name, uint majorClass, uint minorClass)
    {
        var named = NamedKind(name);
        if (named != PeripheralBatteryKind.Device) return Kind(named, name);
        return (majorClass, minorClass) switch
        {
            (0x04, 0x04) => AccessoryKind.Microphone,
            (0x04, 0x05) or (0x04, 0x07) or (0x04, 0x0A) => AccessoryKind.Speaker,
            (0x04, 0x08) => AccessoryKind.Car,
            (0x04, 0x0E) or (0x04, 0x0F) => AccessoryKind.Tv,
            (0x04, 0x12) => AccessoryKind.GameController,
            (0x04, 0x01) or (0x04, 0x02) or (0x04, 0x06) => Kind(PeripheralBatteryKind.Audio, name),
            (0x05, var minor) when (minor & 0x0F) is 0x01 or 0x02 => AccessoryKind.GameController,
            (0x05, var minor) when (minor & 0x0F) == 0x03 => AccessoryKind.Remote,
            (0x05, var minor) when (minor & 0x30) == 0x20 =>
                (minor & 0x0F) == 0x05 ? Kind(PeripheralBatteryKind.Trackpad, name) : Kind(PeripheralBatteryKind.Mouse, name),
            (0x05, var minor) when (minor & 0x10) != 0 => Kind(PeripheralBatteryKind.Keyboard, name),
            (0x06, var minor) when (minor & 0x20) != 0 => AccessoryKind.Printer,
            (0x07, 0x01) => AccessoryKind.Watch,
            (0x07, 0x05) => AccessoryKind.Glasses,
            (0x08, _) => AccessoryKind.GameController,
            _ => Kind(PeripheralBatteryKind.Device, name),
        };
    }

    /// <summary>The battery family of an accessory: its name first, then its class (Windows-specific).</summary>
    public static PeripheralBatteryKind BatteryKind(string name, uint majorClass, uint minorClass) =>
        Kind(name, majorClass, minorClass) switch
        {
            AccessoryKind.Headphones or AccessoryKind.AirPods or AccessoryKind.AirPodsPro or AccessoryKind.AirPodsMax => PeripheralBatteryKind.Audio,
            AccessoryKind.Keyboard => PeripheralBatteryKind.Keyboard,
            AccessoryKind.Mouse => PeripheralBatteryKind.Mouse,
            AccessoryKind.Trackpad => PeripheralBatteryKind.Trackpad,
            _ => PeripheralBatteryKind.Device,
        };

    /// <summary>
    /// Windows-specific: a Bluetooth LE device declares an appearance (category and subcategory) instead of a class
    /// of device. This gives it the class that means the same, so the rules above apply; (0, 0) when none fits.
    /// </summary>
    public static (uint Major, uint Minor) ClassFromAppearance(int category, int subcategory) => (category, subcategory) switch
    {
        (0x01, _) => (0x02, 0x03),             // phone → smartphone
        (0x02, _) => (0x01, 0x00),             // computer
        (0x03, _) => (0x07, 0x01),             // watch → wristwatch
        (0x05, _) => (0x04, 0x0F),             // display → video display
        (0x06, _) => (0x05, 0x03),             // remote control
        (0x07, _) => (0x07, 0x05),             // eyeglasses
        (0x0F, 0x01) => (0x05, 0x10),          // HID keyboard
        (0x0F, 0x02) => (0x05, 0x20),          // HID mouse
        (0x0F, 0x03) => (0x05, 0x01),          // HID joystick
        (0x0F, 0x04) => (0x05, 0x02),          // HID gamepad
        (0x0F, 0x09) => (0x05, 0x25),          // HID touchpad → pointer and digitizer
        (0x0F, 0x0A) => (0x05, 0x03),          // HID presentation remote
        (0x0F, _) => (0x05, 0x00),             // other HID
        (0x14, _) => (0x03, 0x00),             // network device
        (0x21, _) => (0x04, 0x05),             // audio sink → loudspeaker
        (0x22, _) => (0x04, 0x04),             // audio source → microphone
        (0x25, _) or (0x29, _) => (0x04, 0x06), // wearable audio, hearing aid → headphones
        (0x2A, _) => (0x08, 0x00),             // gaming → toy
        _ => (0x00, 0x00),
    };

    /// <summary>The same physical accessory as its reading moves between sources (upstream identity).</summary>
    public static string Identity(PeripheralBatteryDevice device) =>
        device.Kind.ToString().ToLowerInvariant() + ":" + device.Name.Trim().ToLowerInvariant();

    /// <summary>Upstream's sorted: the lowest battery first, then by family and name.</summary>
    public static List<PeripheralBatteryDevice> Sorted(IEnumerable<PeripheralBatteryDevice> devices) =>
        devices.OrderBy(d => d.Percent).ThenBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The Segoe Fluent glyph that stands in for upstream's symbol.</summary>
    public static string Glyph(AccessoryKind kind) => kind switch
    {
        AccessoryKind.Headphones or AccessoryKind.AirPods or AccessoryKind.AirPodsPro or AccessoryKind.AirPodsMax => "",
        AccessoryKind.Speaker => "",
        AccessoryKind.Microphone => "",
        AccessoryKind.GameController => "",
        AccessoryKind.Keyboard => "",
        AccessoryKind.Mouse or AccessoryKind.Trackpad => "",
        AccessoryKind.Car => "",
        AccessoryKind.Tv or AccessoryKind.Remote => "",
        AccessoryKind.Printer => "",
        AccessoryKind.Watch => "",
        _ => "",
    };

    /// <summary>The connected notice's words: the name, and its battery when Windows knows it.</summary>
    public static string ConnectedText(string name, int? percent) =>
        percent is >= 0 and <= 100 ? $"{name} · {percent}%" : name;

    /// <summary>The low-battery notice's words, after the accessory's name (upstream lowBattery · percent).</summary>
    public static string LowText(string name, int percent) => $"{name} · {LowBattery} · {percent}%";

    /// <summary>A Bluetooth address as 12 upper-case hex digits; empty when it isn't one.</summary>
    public static string NormalizeAddress(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        var hex = new string(address.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return hex.Length == 12 ? hex : "";
    }

    private static readonly Regex InstanceAddress = new(@"[&_]([0-9A-F]{12})(?:_|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The address inside a Bluetooth device instance id (BTHENUM\…&amp;ADDR_C…, BTHLE\DEV_ADDR\…); empty without one.</summary>
    public static string AddressFromInstanceId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return "";
        var match = InstanceAddress.Match(instanceId);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }
}

/// <summary>
/// Missing telemetry is not a disconnection or a recharge. A low episode survives both, until an actual reading
/// reaches the recovery threshold. The first reading of an accessory is a silent baseline.
/// </summary>
public sealed class NotchAccessoryBatteryState
{
    private readonly Dictionary<string, bool> _lowEpisodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();
    private readonly Dictionary<string, double> _lastObservation = new(StringComparer.Ordinal);

    /// <summary>A valid reading observed since activation and after the accessory's last one.</summary>
    public bool IsFresh(PeripheralBatteryDevice device, PeripheralBatterySample sample, double observedAfter)
    {
        if (device.Percent is < 0 or > 100 || !sample.ObservedAt.TryGetValue(device.Id, out var observedAt) || !double.IsFinite(observedAt))
            return false;
        var last = _lastObservation.TryGetValue(NotchAccessorySupport.Identity(device), out var seen) ? seen : double.MinValue;
        return observedAt >= observedAfter && observedAt > last;
    }

    /// <summary>The fresh readings of a sample; returns the accessories that just ran low.</summary>
    public List<PeripheralBatteryDevice> Consume(PeripheralBatterySample sample, double observedAfter)
    {
        var fresh = sample.Devices.Where(d => IsFresh(d, sample, observedAfter)).ToList();
        foreach (var device in fresh) _lastObservation[NotchAccessorySupport.Identity(device)] = sample.ObservedAt[device.Id];
        return Consume(fresh);
    }

    /// <summary>Readings in order; returns the accessories that just ran low, lowest first.</summary>
    public List<PeripheralBatteryDevice> Consume(IEnumerable<PeripheralBatteryDevice> devices)
    {
        var alerts = new List<PeripheralBatteryDevice>();
        foreach (var device in devices)
        {
            if (device.Percent is < 0 or > 100) continue;
            var key = NotchAccessorySupport.Identity(device);
            if (!_lowEpisodes.TryGetValue(key, out var low))
            {
                _lowEpisodes[key] = device.Percent <= NotchAccessorySupport.LowThreshold;
                _insertionOrder.AddLast(key);
                if (_insertionOrder.Count > NotchAccessorySupport.RememberedDevices)
                {
                    var oldest = _insertionOrder.First!.Value;
                    _insertionOrder.RemoveFirst();
                    _lowEpisodes.Remove(oldest);
                    _lastObservation.Remove(oldest);
                }
                continue;
            }
            if (device.Percent >= NotchAccessorySupport.RecoveryThreshold) _lowEpisodes[key] = false;
            else if (device.Percent <= NotchAccessorySupport.LowThreshold && !low)
            {
                _lowEpisodes[key] = true;
                alerts.Add(device);
            }
        }
        return NotchAccessorySupport.Sorted(alerts);
    }
}

/// <summary>Which accessories are connected, so a baseline and duplicate callbacks stay silent.</summary>
public sealed class NotchAccessoryConnectionState
{
    private HashSet<string> _connected = new(StringComparer.Ordinal);

    public void EstablishBaseline(IEnumerable<string> ids) => _connected = new HashSet<string>(ids, StringComparer.Ordinal);

    /// <summary>True only for a real new connection.</summary>
    public bool Connected(string id) => id.Length > 0 && _connected.Add(id);

    public void Disconnected(string id) => _connected.Remove(id);
}

/// <summary>Only values cross into presentation (upstream NotchAccessoryConnectionEvent, plus Windows' batteries).</summary>
public abstract record AccessoryEvent
{
    /// <param name="Percent">Its battery when Windows knows it.</param>
    /// <param name="Device">The accessory's key, the same in its battery and disconnect events.</param>
    public sealed record Connected(string Name, uint MajorClass, uint MinorClass, int? Percent = null, string Device = "") : AccessoryEvent;
    public sealed record Disconnected(string? Name, string Device = "") : AccessoryEvent;
    /// <summary>A battery reading of a connected accessory, observed at <paramref name="ObservedAt"/> (<see cref="NotchAccessorySupport.Now"/>).</summary>
    public sealed record Battery(string Name, uint MajorClass, uint MinorClass, int Percent, double ObservedAt, string Device = "") : AccessoryEvent;
}

/// <summary>Where accessory events come from: Windows' device watchers, or a test fixture.</summary>
public interface IAccessorySource
{
    void Start(Func<bool> isCancelled, Action<AccessoryEvent> onEvent);
    void Stop();
}

/// <summary>
/// Lifecycle belongs to the UI thread (upstream NotchAccessoryConnectionMonitor). Native work and cleanup go through
/// <c>perform</c>; events reach the UI through <c>deliver</c>, and only while their activation is still alive, so a late
/// callback from a stopped source can never publish into a new one. Neither start nor stop waits for the source.
/// </summary>
public sealed class NotchAccessoryConnectionMonitor : IDisposable
{
    private sealed class Session
    {
        private volatile bool _cancelled;
        // Accessed only through perform.
        public IAccessorySource? Source;
        public bool IsCancelled => _cancelled;
        public void Cancel() => _cancelled = true;
    }

    private readonly Action<Action> _perform, _deliver;
    private readonly Func<Action<Action>, IAccessorySource> _makeSource;
    private Session? _session;

    public NotchAccessoryConnectionMonitor(Action<Action> perform, Action<Action> deliver, Func<Action<Action>, IAccessorySource> makeSource)
    {
        _perform = perform;
        _deliver = deliver;
        _makeSource = makeSource;
    }

    /// <summary>Starts a new activation, replacing the current one.</summary>
    public void Start(Action<AccessoryEvent> onEvent)
    {
        Stop();
        var session = new Session();
        _session = session;
        var makeSource = _makeSource;
        var perform = _perform;
        var deliver = _deliver;
        perform(() =>
        {
            if (session.IsCancelled) return;
            var source = makeSource(perform);
            session.Source = source;
            source.Start(() => session.IsCancelled, e =>
            {
                if (session.IsCancelled) return;
                deliver(() => { if (!session.IsCancelled) onEvent(e); });
            });
        });
    }

    /// <summary>Invalidates the activation at once; the source is unregistered through perform.</summary>
    public void Stop()
    {
        if (_session is not { } session) return;
        _session = null;
        session.Cancel();
        _perform(() =>
        {
            session.Source?.Stop();
            session.Source = null;
        });
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Accessory notices waiting for the island (upstream NotchAccessoryService.pending): at most eight, each for 30 s,
/// shown one after another.
/// </summary>
public sealed class NotchAccessoryNoticeQueue<T>
{
    public const int Capacity = 8;
    public const double Lifetime = 30;

    private readonly record struct Entry(T Notice, string Device, bool IsLow, double ExpiresAt);
    private readonly List<Entry> _pending = [];

    public int Count => _pending.Count;

    public void Enqueue(T notice, string device, bool isLow, double now)
    {
        if (_pending.Count >= Capacity) _pending.RemoveAt(0);
        _pending.Add(new Entry(notice, device, isLow, now + Lifetime));
    }

    /// <summary>A disconnected accessory takes its waiting notices with it.</summary>
    public void RemoveDevice(string device) => _pending.RemoveAll(e => e.Device == device);

    /// <summary>A measured recharge withdraws the accessory's waiting low-battery notice.</summary>
    public void RemoveLow(string device) => _pending.RemoveAll(e => e.IsLow && e.Device == device);

    /// <summary>The next notice still current at <paramref name="now"/>, without taking it.</summary>
    public bool TryPeek(double now, out T notice)
    {
        _pending.RemoveAll(e => e.ExpiresAt <= now);
        notice = _pending.Count > 0 ? _pending[0].Notice : default!;
        return _pending.Count > 0;
    }

    public void Dequeue() { if (_pending.Count > 0) _pending.RemoveAt(0); }

    public void Clear() => _pending.Clear();
}
