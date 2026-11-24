// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/NotchActivityTests.swift (accessoryContracts and the accessory gates) and the lifecycle
// contracts of Tests/NotchAccessoryConnectionTests.swift that apply to Windows' device watchers (obsolete events,
// cancelled before start). The IOBluetooth run-loop contracts are macOS-only. Windows-specific: LE appearances,
// addresses in device instance ids and the pending-notice queue.
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class AccessoryTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    [Theory]
    [InlineData("airpods", AccessoryKind.AirPods)]
    [InlineData("AIRPODS PRO", AccessoryKind.AirPodsPro)]
    [InlineData("My airpods pro 2", AccessoryKind.AirPodsPro)]
    [InlineData("airpods max", AccessoryKind.AirPodsMax)]
    [InlineData("Max's airpods", AccessoryKind.AirPods)]
    [InlineData("Wireless Headphones", AccessoryKind.Headphones)]
    public void RecognizedHeadsetFamiliesUseTheirOwnKind(string name, AccessoryKind kind) =>
        Assert.Equal(kind, NotchAccessorySupport.Kind(PeripheralBatteryKind.Audio, name));

    [Fact]
    public void ModelSpecificAudioKindsPreserveOtherAccessoryTypes()
    {
        Assert.Equal(AccessoryKind.Keyboard, NotchAccessorySupport.Kind(PeripheralBatteryKind.Keyboard, "Keyboard"));
        Assert.Equal(AccessoryKind.Device, NotchAccessorySupport.Kind(PeripheralBatteryKind.Device, "Device"));
        Assert.Equal(AccessoryKind.Trackpad, NotchAccessorySupport.Kind(PeripheralBatteryKind.Device, "Alex’s Magic Trackpad"));
        foreach (var kind in Enum.GetValues<AccessoryKind>())
            Assert.False(string.IsNullOrEmpty(NotchAccessorySupport.Glyph(kind)));
    }

    [Theory]
    [InlineData(0x05u, 0x25u, AccessoryKind.Trackpad)]
    [InlineData(0x05u, 0x20u, AccessoryKind.Mouse)]
    [InlineData(0x05u, 0x10u, AccessoryKind.Keyboard)]
    [InlineData(0x05u, 0x30u, AccessoryKind.Keyboard)]
    [InlineData(0x05u, 0x02u, AccessoryKind.GameController)]
    [InlineData(0x05u, 0x03u, AccessoryKind.Remote)]
    [InlineData(0x04u, 0x06u, AccessoryKind.Headphones)]
    [InlineData(0x04u, 0x01u, AccessoryKind.Headphones)]
    [InlineData(0x04u, 0x05u, AccessoryKind.Speaker)]
    [InlineData(0x04u, 0x08u, AccessoryKind.Car)]
    [InlineData(0x07u, 0x01u, AccessoryKind.Watch)]
    [InlineData(0x06u, 0x20u, AccessoryKind.Printer)]
    [InlineData(0x08u, 0x04u, AccessoryKind.GameController)]
    [InlineData(0x00u, 0x00u, AccessoryKind.Device)]
    [InlineData(0x1Fu, 0x00u, AccessoryKind.Device)]
    public void ARenamedAccessoryTakesItsIconFromItsClassOfDevice(uint major, uint minor, AccessoryKind kind) =>
        Assert.Equal(kind, NotchAccessorySupport.Kind("Kitchen", major, minor));

    [Fact]
    public void ANameThatSaysWhatTheAccessoryIsOutranksItsClass()
    {
        Assert.Equal(AccessoryKind.Keyboard, NotchAccessorySupport.Kind("Alex’s Magic Keyboard", 0x05, 0x25));
        Assert.Equal(AccessoryKind.AirPodsPro, NotchAccessorySupport.Kind("AirPods Pro", 0x04, 0x05));
    }

    private static PeripheralBatteryDevice Device(int percent, string id = "HID:1", string name = "Keyboard") =>
        new(id, name, percent, PeripheralBatteryKind.Keyboard);

    [Fact]
    public void EachLowEpisodeWarnsOnceAndRearmsOnlyOnAMeasuredRecharge()
    {
        var battery = new NotchAccessoryBatteryState();
        Assert.Empty(battery.Consume([Device(19)]));                     // a silent baseline even when already low
        Assert.Empty(battery.Consume([Device(18)]));                     // the same low episode
        Assert.Empty(battery.Consume([]));                               // missing readings
        Assert.Empty(battery.Consume([Device(25)]));                     // a measured recharge rearms
        Assert.Single(battery.Consume([Device(20)]));                    // dropping to 20 percent warns once
        Assert.Empty(battery.Consume([Device(21)]));
        Assert.Empty(battery.Consume([Device(20)]));                     // threshold noise cannot repeatedly alert
        Assert.Empty(battery.Consume([Device(18, id: "Bluetooth:1")]));  // switching sources keeps the episode
        Assert.Empty(battery.Consume([Device(101)]));
        Assert.Empty(battery.Consume([Device(20)]));                     // invalid telemetry is no recharge
        battery.Consume([Device(30)]);
        Assert.Single(battery.Consume([Device(10)]));                    // a new discharge after a recharge warns again
    }

    [Fact]
    public void IdentityTrimsAndIgnoresCase()
    {
        Assert.Equal("keyboard:magic keyboard", NotchAccessorySupport.Identity(Device(50, name: "  Magic Keyboard \n")));
        var battery = new NotchAccessoryBatteryState();
        battery.Consume([Device(50, name: "Magic Keyboard")]);
        Assert.Single(battery.Consume([Device(15, name: " magic keyboard ")]));
    }

    [Fact]
    public void FreshnessFollowsActivationAndTheLastObservation()
    {
        var battery = new NotchAccessoryBatteryState();
        PeripheralBatterySample Sample(int percent, double at) =>
            new([Device(percent)], new Dictionary<string, double> { ["HID:1"] = at });
        Assert.Empty(battery.Consume(Sample(50, 5), observedAfter: 10));   // before activation: ignored, not a baseline
        Assert.Empty(battery.Consume(Sample(50, 11), observedAfter: 10));  // the baseline
        Assert.Single(battery.Consume(Sample(15, 12), observedAfter: 10));
        Assert.Empty(battery.Consume(Sample(30, 12), observedAfter: 10));  // the same observation again is not fresh
        Assert.Empty(battery.Consume(Sample(14, 13), observedAfter: 10));  // so the episode did not rearm
        Assert.False(battery.IsFresh(Device(14), new([Device(14)], new Dictionary<string, double>()), 0));
        Assert.False(battery.IsFresh(Device(14), new([Device(14)], new Dictionary<string, double> { ["HID:1"] = double.NaN }), 0));
    }

    [Fact]
    public void AtMost128AccessoriesAreRemembered()
    {
        var battery = new NotchAccessoryBatteryState();
        battery.Consume([Device(50, name: "First")]);
        for (var i = 0; i < NotchAccessorySupport.RememberedDevices; i++) battery.Consume([Device(50, name: $"Other {i}")]);
        // The first was forgotten, so its next reading is a new silent baseline.
        Assert.Empty(battery.Consume([Device(10, name: "First")]));
        Assert.Single(battery.Consume([Device(10, name: "Other 127")]));
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x02u)]
    [InlineData(0x03u)]
    public void APhoneTabletComputerOrAccessPointIsNotAnnounced(uint major) =>
        Assert.False(NotchAccessorySupport.AnnouncesConnection(major));

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x04u)]
    [InlineData(0x05u)]
    [InlineData(0x06u)]
    [InlineData(0x07u)]
    [InlineData(0x08u)]
    [InlineData(0x09u)]
    [InlineData(0x1Fu)]
    public void AccessoriesAndDevicesWithoutAClassAnnounceTheirConnection(uint major) =>
        Assert.True(NotchAccessorySupport.AnnouncesConnection(major));

    [Fact]
    public void ConnectionBaselineIsSilentAndOnlyARealDisconnectionRearms()
    {
        var connections = new NotchAccessoryConnectionState();
        connections.EstablishBaseline(["AA:01"]);
        Assert.False(connections.Connected("AA:01"));
        Assert.True(connections.Connected("AA:02"));
        Assert.False(connections.Connected("AA:02"));    // duplicate system callbacks produce one event
        Assert.False(connections.Connected(""));
        connections.Disconnected("AA:01");
        Assert.True(connections.Connected("AA:01"));
    }

    [Fact]
    public void AfterLockOrSleepTheConnectionBaselineIsRebuiltWhileBatteryEpisodesSurvive()
    {
        var battery = new NotchAccessoryBatteryState();
        battery.Consume([Device(50)]);
        Assert.Single(battery.Consume([Device(15)]));
        var connections = new NotchAccessoryConnectionState();
        connections.EstablishBaseline(["AA:01"]);
        // Locked, then unlocked: a new source rebuilds its baseline from what is connected now.
        connections = new NotchAccessoryConnectionState();
        connections.EstablishBaseline(["AA:01", "AA:02"]);
        Assert.False(connections.Connected("AA:01"));
        Assert.False(connections.Connected("AA:02"));
        Assert.Empty(battery.Consume([Device(14)]));     // the same low episode, not a second warning
    }

    // MARK: Monitor lifecycle (upstream NotchAccessoryConnectionTests)

    private sealed class Source(Action? onStart = null) : IAccessorySource
    {
        public Action<AccessoryEvent>? Callback;
        public int Stops;

        public void Start(Func<bool> isCancelled, Action<AccessoryEvent> onEvent)
        {
            Callback = onEvent;
            onStart?.Invoke();
        }

        // Keep the callback to model a late framework notification.
        public void Stop() => Stops++;
    }

    private sealed class Tasks
    {
        public readonly List<Action> Pending = [];
        public void Enqueue(Action work) => Pending.Add(work);
        public void Drain()
        {
            while (Pending.Count > 0)
            {
                var work = Pending[0];
                Pending.RemoveAt(0);
                work();
            }
        }
    }

    [Fact]
    public void OnlyAnImmutableEventIsDeliveredAndOnlyThroughTheUi()
    {
        var worker = new Tasks();
        var ui = new Tasks();
        var sources = new List<Source>();
        var delivered = new List<AccessoryEvent>();
        var monitor = new NotchAccessoryConnectionMonitor(worker.Enqueue, ui.Enqueue, _ => { var s = new Source(); sources.Add(s); return s; });
        monitor.Start(delivered.Add);
        Assert.Empty(sources);                            // native work waits for the worker
        worker.Drain();
        var e = new AccessoryEvent.Connected("Keyboard", 5, 16);
        sources[0].Callback!(e);
        Assert.Empty(delivered);                          // presentation waits asynchronously for the UI
        ui.Drain();
        Assert.Equal([e], delivered);
        monitor.Stop();
        worker.Drain();
        Assert.Equal(1, sources[0].Stops);
    }

    [Fact]
    public void ObsoleteEventsAreDiscarded()
    {
        var worker = new Tasks();
        var ui = new Tasks();
        var sources = new List<Source>();
        var delivered = new List<AccessoryEvent>();
        var monitor = new NotchAccessoryConnectionMonitor(worker.Enqueue, ui.Enqueue, _ => { var s = new Source(); sources.Add(s); return s; });
        monitor.Start(delivered.Add);
        worker.Drain();
        var old = Assert.Single(sources);
        old.Callback!(new AccessoryEvent.Connected("Old keyboard", 5, 16));
        Assert.Single(ui.Pending);                        // queued for presentation before disable
        monitor.Stop();
        monitor.Start(delivered.Add);
        worker.Drain();
        ui.Drain();
        Assert.Empty(delivered);                          // an event already waiting on the UI is discarded after reenable
        old.Callback!(new AccessoryEvent.Disconnected("Keyboard"));
        ui.Drain();
        Assert.Empty(delivered);                          // a late disconnect from the old observer cannot clear new notices
        Assert.Equal(2, sources.Count);
        var current = new AccessoryEvent.Connected("Keyboard", 5, 16);
        sources[1].Callback!(current);
        ui.Drain();
        Assert.Equal([current], delivered);
        sources[1].Callback!(new AccessoryEvent.Disconnected("Keyboard"));
        monitor.Stop();
        monitor.Stop();
        worker.Drain();
        ui.Drain();
        Assert.Equal([current], delivered);               // repeated stop discards pending events
        Assert.All(sources, s => Assert.Equal(1, s.Stops)); // and unregisters each source once
    }

    [Fact]
    public void StopOrDisposeBeforeWorkerStartupMakesNoBluetoothCalls()
    {
        var worker = new Tasks();
        var creations = 0;
        var monitor = new NotchAccessoryConnectionMonitor(worker.Enqueue, w => w(), _ => { creations++; return new Source(); });
        monitor.Start(_ => { });
        monitor.Stop();
        monitor.Start(_ => { });
        monitor.Dispose();
        worker.Drain();
        Assert.Equal(0, creations);
    }

    // MARK: Gates and defaults (upstream gateContracts)

    [Fact]
    public void InstalledAccessoryNoticesStartEnabledAndFollowTheirGates()
    {
        var store = NewStore();
        store.Set(DefaultsKey.NotchEnabled, true);
        Assert.True(NotchAccessorySupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchAccessoriesEnabled, false);
        Assert.False(NotchAccessorySupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchAccessoriesEnabled, true);
        Assert.True(NotchAccessorySupport.IsEnabled(store));
        store.Set(FeatureCatalog.Info(AppFeature.NotchAccessories).AvailabilityKey, false);
        Assert.False(NotchAccessorySupport.IsEnabled(store));
        store.Set(FeatureCatalog.Info(AppFeature.NotchAccessories).AvailabilityKey, true);
        store.Set(DefaultsKey.NotchEnabled, false);
        Assert.False(NotchAccessorySupport.IsEnabled(store));
        Assert.Equal(WindowsStatus.Ported, FeatureCatalog.Info(AppFeature.NotchAccessories).Status);
    }

    // MARK: Windows-specific

    [Theory]
    [InlineData(0x01, 0x00, 0x02u, AccessoryKind.Device)]           // a phone: not announced
    [InlineData(0x02, 0x00, 0x01u, AccessoryKind.Device)]           // a computer: not announced
    [InlineData(0x0F, 0x04, 0x05u, AccessoryKind.GameController)]   // an Xbox controller over LE
    [InlineData(0x0F, 0x01, 0x05u, AccessoryKind.Keyboard)]
    [InlineData(0x0F, 0x02, 0x05u, AccessoryKind.Mouse)]
    [InlineData(0x0F, 0x09, 0x05u, AccessoryKind.Trackpad)]
    [InlineData(0x25, 0x01, 0x04u, AccessoryKind.Headphones)]
    [InlineData(0x21, 0x01, 0x04u, AccessoryKind.Speaker)]
    [InlineData(0x03, 0x00, 0x07u, AccessoryKind.Watch)]
    [InlineData(0x00, 0x00, 0x00u, AccessoryKind.Device)]
    public void AnLeAppearanceStandsInForTheClassOfDevice(int category, int subcategory, uint major, AccessoryKind kind)
    {
        var (m, minor) = NotchAccessorySupport.ClassFromAppearance(category, subcategory);
        Assert.Equal(major, m);
        Assert.Equal(kind, NotchAccessorySupport.Kind("Kitchen", m, minor));
    }

    [Fact]
    public void AddressesAreNormalizedFromWatchersAndInstanceIds()
    {
        Assert.Equal("A1B2C3D4E5F6", NotchAccessorySupport.NormalizeAddress("a1:b2:c3:d4:e5:f6"));
        Assert.Equal("", NotchAccessorySupport.NormalizeAddress("a1:b2"));
        Assert.Equal("", NotchAccessorySupport.NormalizeAddress(null));
        Assert.Equal("A1B2C3D4E5F6", NotchAccessorySupport.AddressFromInstanceId(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\7&2A1B3C4D&0&A1B2C3D4E5F6_C00000000"));
        Assert.Equal("A1B2C3D4E5F6", NotchAccessorySupport.AddressFromInstanceId(@"BTHLE\DEV_a1b2c3d4e5f6\8&1a2b3c&0&a1b2c3d4e5f6"));
        Assert.Equal("A1B2C3D4E5F6", NotchAccessorySupport.AddressFromInstanceId(@"BTHENUM\DEV_A1B2C3D4E5F6\7&1A2B3C4D&0&BLUETOOTHDEVICE_A1B2C3D4E5F6"));
        Assert.Equal("", NotchAccessorySupport.AddressFromInstanceId(@"USB\VID_8087&PID_0026\5&1A2B3C4D&0&10"));
    }

    [Fact]
    public void NoticeTextNamesTheAccessoryAndItsBattery()
    {
        Assert.Equal("AirPods Pro 2 · 80%", NotchAccessorySupport.ConnectedText("AirPods Pro 2", 80));
        Assert.Equal("AirPods Pro 2", NotchAccessorySupport.ConnectedText("AirPods Pro 2", null));
        Assert.Equal("AirPods Pro 2", NotchAccessorySupport.ConnectedText("AirPods Pro 2", 101));
        Assert.Equal("Keyboard · Low battery · 18%", NotchAccessorySupport.LowText("Keyboard", 18));
    }

    [Fact]
    public void PendingNoticesAreBoundedExpireAndLeaveWithTheirAccessory()
    {
        var queue = new NotchAccessoryNoticeQueue<string>();
        for (var i = 0; i < 10; i++) queue.Enqueue($"n{i}", $"d{i}", isLow: i % 2 == 0, now: 0);
        Assert.Equal(NotchAccessoryNoticeQueue<string>.Capacity, queue.Count);
        Assert.True(queue.TryPeek(1, out var first));
        Assert.Equal("n2", first);                        // the oldest two were dropped
        queue.RemoveLow("d2");
        Assert.True(queue.TryPeek(1, out first));
        Assert.Equal("n3", first);
        queue.RemoveDevice("d3");
        queue.RemoveLow("d5");                            // not a low notice: it stays
        Assert.True(queue.TryPeek(1, out first));
        Assert.Equal("n4", first);
        queue.Dequeue();
        Assert.True(queue.TryPeek(1, out first));
        Assert.Equal("n5", first);
        Assert.False(queue.TryPeek(NotchAccessoryNoticeQueue<string>.Lifetime, out _));
        Assert.Equal(0, queue.Count);
    }
}
