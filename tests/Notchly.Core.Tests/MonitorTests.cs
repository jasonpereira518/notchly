// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/MetricsFeatureTests.swift, SystemMonitorCPUTests.swift,
// SystemMonitorPlanTests.swift, NetworkFeatureTests.swift, NotchTests.swift (system rows) and SpeedTestTests.swift.
using System.Globalization;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class MonitorTests
{
    public MonitorTests() => MetricFormat.Culture = CultureInfo.InvariantCulture;

    // MARK: Formatting

    [Theory]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(10240UL, "10 KB")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1_048_063UL, "1023 KB")]
    [InlineData(1_048_064UL, "1.0 MB")]
    [InlineData(1_048_575UL, "1.0 MB")]
    [InlineData(1_000_000_000UL, "954 MB")]
    public void BytesAreBinary(ulong bytes, string expected) => Assert.Equal(expected, MetricFormat.Bytes(bytes));

    [Fact]
    public void DiskSizesAreDecimal()
    {
        Assert.Equal("245 GB", MetricFormat.DiskBytes(245_107_195_904));
        Assert.Equal("1.0 TB", MetricFormat.DiskBytes(1_000_204_845_056));
        Assert.Equal("14.88 TB", MetricFormat.DiskBytesPrecise(14_878_047_232_000));
    }

    [Fact]
    public void ByteRates()
    {
        Assert.Equal("1.5 MB/s", MetricFormat.BytesPerSecond(1500 * 1024));
        Assert.Equal("1023 B/s", MetricFormat.BytesPerSecond(1023.4));
        Assert.Equal("1.0 KB/s", MetricFormat.BytesPerSecond(1023.6));
        Assert.Equal("0B", MetricFormat.BytesPerSecondCompact(0));
        Assert.Equal("320K", MetricFormat.BytesPerSecondCompact(320 * 1024));
        Assert.Equal("1.2M", MetricFormat.BytesPerSecondCompact(1.2 * 1024 * 1024));
        Assert.Equal("10K", MetricFormat.BytesPerSecondCompact(9.96 * 1024));
    }

    [Fact]
    public void BitRates()
    {
        Assert.Equal("12 Kbps", MetricFormat.BitsPerSecond(1500));
        Assert.Equal("9.6 Mbps", MetricFormat.BitsPerSecond(1_200_000));
        Assert.Equal("1.0 Gbps", MetricFormat.BitsPerSecond(125_000_000));
        Assert.Equal("999 bps", MetricFormat.BitsPerSecond(124.9));
        Assert.Equal("1.0 Kbps", MetricFormat.BitsPerSecond(124.95));
        Assert.Equal("320Kb", MetricFormat.BitsPerSecondCompact(40_000));
        Assert.Equal("9.6Mb", MetricFormat.BitsPerSecondCompact(1_200_000));
        Assert.Equal("10Mb", MetricFormat.BitsPerSecondCompact(1_245_000));
        Assert.Equal("8.0Gb", MetricFormat.BitsPerSecondCompact(1e9));
        Assert.Equal("0b", MetricFormat.BitsPerSecondCompact(double.NaN));
    }

    [Fact]
    public void CompactRatesNeverExceedFiveCharacters()
    {
        foreach (var rate in new[] { 0.0, 1, 999, 1023.6, 10 * 1024, 999.9 * 1024, 1024.0 * 1024, 1e9, 1e12, 1e15 })
        {
            Assert.True(MetricFormat.BytesPerSecondCompact(rate).Length <= 5, MetricFormat.BytesPerSecondCompact(rate));
            Assert.True(MetricFormat.BitsPerSecondCompact(rate).Length <= 5, MetricFormat.BitsPerSecondCompact(rate));
        }
    }

    [Fact]
    public void GraphCeilingsLandOnRoundNumbers()
    {
        Assert.Equal(2 * 1024 * 1024, MetricFormat.GraphCeiling(1.3 * 1024 * 1024, 1024));
        Assert.Equal(1024 * 1024, MetricFormat.GraphCeiling(600 * 1024, 1024));
        Assert.Equal(20, MetricFormat.GraphCeiling(12.4, 1000));
        Assert.Equal("20 Kbps", MetricFormat.BitsPerSecond(MetricFormat.NetworkGraphCeiling(2000, inBits: true)));
        Assert.Equal(2500, MetricFormat.NetworkGraphCeiling(2000, inBits: true));
        Assert.Equal(2048, MetricFormat.NetworkGraphCeiling(1500, inBits: false));
        Assert.Equal("2.0 KB/s", MetricFormat.BytesPerSecond(MetricFormat.NetworkGraphCeiling(1500, inBits: false)));
    }

    [Fact]
    public void SavedUnitOtherThanBitsReadsAsBytes()
    {
        Assert.True(MetricFormat.InBits("bits"));
        Assert.False(MetricFormat.InBits("bytes"));
        Assert.False(MetricFormat.InBits(null));
        Assert.False(MetricFormat.InBits("Mbps"));
        MetricFormat.Culture = new CultureInfo("pt-BR");
        Assert.Equal("1,5 KB", MetricFormat.Bytes(1536));
    }

    [Fact]
    public void WattsAndPercent()
    {
        Assert.Equal("8.5 W", MetricFormat.Watts(8.5));
        Assert.Equal("23 W", MetricFormat.Watts(23.4));
        Assert.Equal("9W", MetricFormat.WattsCompact(8.6));
        Assert.Equal("13%", MetricFormat.Percent(0.125));
        Assert.Equal("100%", MetricFormat.Percent(7));
        Assert.Equal("0%", MetricFormat.Percent(-1));
        Assert.Equal("79%", MetricFormat.MemoryPercentText(79, 100));
        Assert.Equal("--%", MetricFormat.MemoryPercentText(null, 100));
        Assert.Equal("--%", MetricFormat.MemoryPercentText(5, 0));
        Assert.Equal("59min".Replace("59", "0"), MetricFormat.Uptime(59));
        Assert.Equal("1d 2h", MetricFormat.Uptime(93_600));
        Assert.Equal("3h 42min", MetricFormat.Uptime(13_320));
    }

    [Fact]
    public void GpuUsageIsStabilized()
    {
        Assert.Equal(0.23, MetricFormat.StabilizedGpuUsage(0.03, 0.80), 6);
        Assert.Equal(0.43, MetricFormat.StabilizedGpuUsage(0.23, 0.80), 6);
        Assert.Equal(0.275, MetricFormat.StabilizedGpuUsage(0.60, 0.10), 6);
        Assert.Equal(1.0, MetricFormat.StabilizedGpuUsage(null, 1.4));
    }

    [Fact]
    public void ProcessShares()
    {
        Assert.Equal(25, MetricFormat.ProcessCpuPercentage(0, 1_000_000_000, 1, 4), 6);
        Assert.Equal(100, MetricFormat.ProcessCpuPercentage(0, 100_000_000_000, 1, 4));
        Assert.Equal(0, MetricFormat.ProcessCpuPercentage(5, 4, 1, 4));
        Assert.Equal(0, MetricFormat.ProcessCpuPercentage(0, 5, 0, 4));
        Assert.Equal(0.5, MetricFormat.ProcessReconciliationScale(80, 40), 6);
        Assert.Equal(1, MetricFormat.ProcessReconciliationScale(30, 40));
        Assert.Equal(1, MetricFormat.ProcessReconciliationScale(0, 40));
        Assert.Equal(0, MetricFormat.BoundedPercentage(double.NaN));
        Assert.Equal(100, MetricFormat.BoundedPercentage(250));
    }

    [Fact]
    public void MemoryUsedIsWhatIsNotAvailable()
    {
        Assert.Equal(6UL, MetricFormat.MemoryUsed(10, 4));
        Assert.Equal(0UL, MetricFormat.MemoryUsed(10, 40));
        Assert.Equal(0UL, MetricFormat.MemoryUsed(0, 4));
        Assert.Equal("app", MetricFormat.SelectedMemory("used", "app", "app"));
        Assert.Equal("used", MetricFormat.SelectedMemory("used", "app", "garbage"));
    }

    // MARK: Speeds

    private static NetworkCounters Counters(ulong rx, ulong tx, params string[] names) => new(rx, tx, new HashSet<string>(names));

    [Fact]
    public void SpeedsFromCounters()
    {
        var (down, up) = MetricFormat.NetworkSpeed(Counters(0, 0), Counters(2048, 1024), 2);
        Assert.Equal((1024.0, 512.0), (down, up));
        Assert.Equal((0.0, 0.0), MetricFormat.NetworkSpeed(Counters(0, 0), Counters(2048, 1024), 0));
        Assert.Equal((0.0, 0.0), MetricFormat.NetworkSpeed(Counters(5000, 5000), Counters(100, 100), 1));
        var (read, write) = MetricFormat.DiskSpeed(new DiskIoCounters(0, 0), new DiskIoCounters(2048, 1024), 2);
        Assert.Equal((1024.0, 512.0), (read, write));
        Assert.Equal((0.0, 0.0), MetricFormat.DiskSpeed(new DiskIoCounters(10, 10), new DiskIoCounters(1, 1), 1));
    }

    [Theory]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", 71, true, true)]
    [InlineData("Realtek PCIe GbE Family Controller", 6, true, true)]
    [InlineData("Realtek PCIe GbE Family Controller", 6, false, false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", 6, true, false)]
    [InlineData("VMware Virtual Ethernet Adapter for VMnet8", 6, true, false)]
    [InlineData("TAP-Windows Adapter V9", 6, true, false)]
    [InlineData("WireGuard Tunnel", 131, true, false)]
    [InlineData("Software Loopback Interface 1", 24, true, false)]
    [InlineData("Bluetooth Device (Personal Area Network)", 6, true, false)]
    [InlineData("", 6, true, false)]
    public void OnlyPhysicalAdaptersCount(string description, int type, bool up, bool expected) =>
        Assert.Equal(expected, NetworkRules.Counts(description, type, up));

    // MARK: CPU

    [Fact]
    public void CpuReaderMatchesTheTickContract()
    {
        var reader = new CpuUsageReader();
        // user, system, idle, nice: busy = user + system + nice
        static CpuTicks T(ulong user, ulong system, ulong idle, ulong nice) => new(user + system + nice, user + system + idle + nice);
        Assert.Null(reader.Read(T(100, 100, 800, 0), 0));
        Assert.Equal(0.25, reader.Read(T(200, 200, 1400, 0), 5));
        Assert.Null(reader.Read(T(1200, 1200, 1600, 0), 65));
        Assert.Null(reader.LastUsage);
        Assert.Null(reader.LastReadAt);
        Assert.Equal(0.5, reader.Read(T(1300, 1300, 1800, 0), 70));
        Assert.Equal(70, reader.LastReadAt);
    }

    [Fact]
    public void UnchangedOrResetTicksGiveNoReading()
    {
        Assert.Null(CpuTicks.Usage(new CpuTicks(10, 100), new CpuTicks(10, 100)));
        Assert.Null(CpuTicks.Usage(new CpuTicks(50, 100), new CpuTicks(5, 120)));
        Assert.Equal(0.5, CpuTicks.Usage(new CpuTicks(20, 100), new CpuTicks(70, 200)));
    }

    // MARK: Memory pressure

    [Fact]
    public void PressureIsDerivedFromAvailabilityAndCommit()
    {
        const ulong gb = 1UL << 30;
        Assert.Equal(MemoryPressure.Normal, MemoryMath.Pressure(16 * gb, 8 * gb, 10 * gb, 40 * gb));
        Assert.Equal(MemoryPressure.Warning, MemoryMath.Pressure(16 * gb, 1 * gb, 10 * gb, 40 * gb));
        Assert.Equal(MemoryPressure.Warning, MemoryMath.Pressure(16 * gb, 8 * gb, 35 * gb, 40 * gb));
        Assert.Equal(MemoryPressure.Critical, MemoryMath.Pressure(16 * gb, gb / 4, 10 * gb, 40 * gb));
        Assert.Equal(MemoryPressure.Critical, MemoryMath.Pressure(16 * gb, 8 * gb, 39 * gb, 40 * gb));
        Assert.Equal(MemoryPressure.Unknown, MemoryMath.Pressure(0, 0, 0, 0));
    }

    // MARK: Disk

    [Fact]
    public void DiskRulesFollowUpstream()
    {
        Assert.Equal(1024UL, DiskRules.NvmeBytes(2, 0)! / 1000);
        Assert.Equal(1_024_000UL, DiskRules.NvmeBytes(2, null));
        Assert.Equal(2_199_023_256_064_000UL, DiskRules.NvmeBytes(1, 1));
        Assert.Null(DiskRules.NvmeBytes(null, 1));
        Assert.Null(DiskRules.NvmeBytes(1, 1UL << 40));
        Assert.Equal(28.85, DiskRules.Celsius(302)!.Value, 2);
        Assert.Equal(33, DiskRules.Celsius(33));
        Assert.Null(DiskRules.Celsius(999));
        Assert.Equal(99, DiskRules.HealthPercent(1));
        Assert.Equal(0, DiskRules.HealthPercent(150));
        Assert.Equal(0, DiskRules.HealthPercent(ulong.MaxValue));
        Assert.Null(DiskRules.HealthPercent(null));
        Assert.Equal((250UL, 750UL), DiskRules.Capacity(1000, 250));
        Assert.Equal((1000UL, 0UL), DiskRules.Capacity(1000, 5000));
        Assert.Null(DiskRules.Capacity(0, 5));
        Assert.Null(DiskRules.Capacity(1000, null));
        Assert.Equal("NTFS", DiskRules.FileSystemLabel("NTFS"));
        Assert.Equal("exFAT", DiskRules.FileSystemLabel("exfat"));
        Assert.Equal("XFS", DiskRules.FileSystemLabel("xfs"));
        Assert.Null(DiskRules.FileSystemLabel("verylongtoken"));
        Assert.Null(DiskRules.FileSystemLabel(""));
        Assert.Null(DiskRules.FileSystemLabel("1234"));
    }

    [Fact]
    public void DiskMenuText()
    {
        Assert.Equal("67%", DiskRules.MenuText("percent", 245_000_000_000, 80_000_000_000));
        Assert.Equal("80 GB", DiskRules.MenuText("free", 245_000_000_000, 80_000_000_000));
        Assert.Equal("165 GB", DiskRules.MenuText("used", 245_000_000_000, 80_000_000_000));
        Assert.Equal("0%", DiskRules.MenuText("percent", 0, 0));
    }

    // MARK: Battery

    [Fact]
    public void BatteryReadings()
    {
        Assert.Equal("3h 42m", BatteryRules.FormattedTime(13_320));
        Assert.Equal("1h 0m", BatteryRules.FormattedTime(3600));
        Assert.Equal("0h 1m", BatteryRules.FormattedTime(30));
        Assert.Null(BatteryRules.FormattedTime(1e21));
        Assert.Equal(13_320, BatteryRules.RemainingSeconds(222, false, false));
        Assert.Null(BatteryRules.RemainingSeconds(-1, false, false));
        Assert.Null(BatteryRules.RemainingSeconds(65535, false, false));
        Assert.Null(BatteryRules.RemainingSeconds(222, true, false));
        Assert.Null(BatteryRules.RemainingSeconds(222, false, true));
        Assert.Equal((100, true), BatteryRules.Glyph(10, true, false));
        Assert.Equal((100, true), BatteryRules.Glyph(10, false, true));
        Assert.Equal((100, false), BatteryRules.Glyph(85, false, false));
        Assert.Equal((75, false), BatteryRules.Glyph(60, false, false));
        Assert.Equal((50, false), BatteryRules.Glyph(35, false, false));
        Assert.Equal((25, false), BatteryRules.Glyph(10, false, false));
        Assert.Equal((0, false), BatteryRules.Glyph(9, false, false));
        Assert.Equal(BatteryPowerState.Charging, BatteryRules.State(true, true, true));
        Assert.Equal(BatteryPowerState.ExternalPower, BatteryRules.State(false, true, true));
        Assert.Equal(BatteryPowerState.OnBattery, BatteryRules.State(false, false, true));
        Assert.Equal(BatteryPowerState.Unavailable, BatteryRules.State(false, false, false));
        Assert.Equal(94, BatteryRules.HealthPercent(47_000, 50_000));
        Assert.Equal(100, BatteryRules.HealthPercent(52_000, 50_000));
        Assert.Null(BatteryRules.HealthPercent(null, 50_000));
    }

    // MARK: Connected devices

    [Fact]
    public void OnlyExternalPeripheralsCount()
    {
        UsbDeviceInfo Device(int vendor = 0x0781, int product = 0x5583, int? deviceClass = null, bool nonRemovable = false, string? serial = null,
                             ulong location = 0, string? instance = null, string? name = "Cruzer") =>
            new(vendor, product, deviceClass, nonRemovable, name, "SanDisk", serial, location, instance);
        Assert.NotNull(UsbDeviceRules.Parse(Device()));
        Assert.Null(UsbDeviceRules.Parse(Device(nonRemovable: true)));
        Assert.Null(UsbDeviceRules.Parse(Device(vendor: 0, product: 0)));
        Assert.Null(UsbDeviceRules.Parse(Device(deviceClass: 9)));
        Assert.Null(UsbDeviceRules.Parse(Device(deviceClass: 17)));
        Assert.NotNull(UsbDeviceRules.Parse(Device(vendor: 0, product: 1)));
        Assert.Equal("", UsbDeviceRules.Parse(Device(name: null))!.Name);
        Assert.Equal("1921-21891-4C530", UsbDeviceRules.Parse(Device(instance: "4C530"))!.Id);
        Assert.Equal("1921-21891-ABC", UsbDeviceRules.Parse(Device(serial: "ABC"))!.Id);
        Assert.Equal("1921-21891-loc77", UsbDeviceRules.Parse(Device(location: 77))!.Id);
        Assert.Equal("1921-21891-Cruzer", UsbDeviceRules.Parse(Device())!.Id);
        var one = UsbDeviceRules.Parse(Device(serial: "A"))!;
        Assert.Single(UsbDeviceRules.Deduplicated([one, one]));
    }

    // MARK: Alerts

    [Fact]
    public void TheGateWaitsTwelveSeconds()
    {
        var gate = new SustainedAlertGate();
        Assert.False(gate.ShouldAlert(0.95, 0.9, 100));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 108));
        Assert.True(gate.ShouldAlert(0.95, 0.9, 112));
        // The same reading served again between reads never ages into an alert on its own.
        gate.Reset();
        Assert.False(gate.ShouldAlert(0.95, 0.9, 100));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 100));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 100));
        // Dropping below the threshold starts over.
        Assert.False(gate.ShouldAlert(0.95, 0.9, 101));
        Assert.False(gate.ShouldAlert(0.2, 0.9, 105));
        Assert.False(gate.ShouldAlert(0.95, 0.9, 113));
        Assert.False(gate.ShouldAlert(null, 0.9, 120));
        Assert.False(gate.ShouldAlert(0.95, 0.9, null));
    }

    [Fact]
    public void AlertSettingsFallBackWhenOutOfRange()
    {
        Assert.Equal(90, AlertRules.CpuThreshold(0));
        Assert.Equal(80, AlertRules.CpuThreshold(80));
        Assert.Equal(90, AlertRules.CpuThreshold(40));
        Assert.Equal(10, AlertRules.DiskFreePercent(2));
        Assert.Equal(15, AlertRules.DiskFreePercent(15));
        Assert.Equal(15, AlertRules.BatteryPercent(0));
        Assert.Equal(50, AlertRules.BatteryPercent(50));
        Assert.Equal(15, AlertRules.CooldownMinutes(7));
        Assert.Equal(60, AlertRules.CooldownMinutes(60));
    }

    private sealed record Disk(string Name, ulong Total, ulong Free);

    [Fact]
    public void AlertTriggers()
    {
        var disks = new[] { new Disk("Small", 5_000_000_000, 100), new Disk("System", 500_000_000_000, 20_000_000_000), new Disk("Data", 1_000_000_000_000, 500_000_000_000) };
        Assert.Equal("System", AlertRules.LowDisk(disks, d => (d.Total, d.Free), 10)!.Name.Replace("System", "System"));
        Assert.Null(AlertRules.LowDisk(disks, d => (d.Total, d.Free), 3));
        Assert.Equal(12, AlertRules.LowBattery(true, false, 12, 15));
        Assert.Null(AlertRules.LowBattery(true, true, 12, 15));
        Assert.Null(AlertRules.LowBattery(false, false, 12, 15));
        Assert.Null(AlertRules.LowBattery(true, false, 40, 15));
        var now = new DateTime(2026, 11, 24, 12, 0, 0);
        Assert.True(AlertRules.CooldownPassed(null, now, 15));
        Assert.False(AlertRules.CooldownPassed(now.AddMinutes(-14), now, 15));
        Assert.True(AlertRules.CooldownPassed(now.AddMinutes(-15), now, 15));
    }

    // MARK: Tray readouts

    [Fact]
    public void TrayThresholdsAndBatteryColours()
    {
        Assert.Equal(UsageLevel.Normal, TrayReadout.Level(69));
        Assert.Equal(UsageLevel.Elevated, TrayReadout.Level(70));
        Assert.Equal(UsageLevel.Critical, TrayReadout.Level(90));
        Assert.Equal((99, 100), TrayReadout.Thresholds(500, 3));
        Assert.Equal((1, 2), TrayReadout.Thresholds(-4, 0));
        Assert.Equal(BatteryTone.Red, TrayReadout.BatteryColor(10, false));
        Assert.Equal(BatteryTone.Amber, TrayReadout.BatteryColor(20, false));
        Assert.Equal(BatteryTone.Normal, TrayReadout.BatteryColor(21, false));
        Assert.Equal(BatteryTone.Normal, TrayReadout.BatteryColor(5, true));
        Assert.Equal("RAM", TrayReadout.Label("memory"));
        Assert.Equal("DSK", TrayReadout.Label("diskUsage"));
    }

    // MARK: Sampling

    [Theory]
    [InlineData(MonitorSamplingKind.Cpu, 2, false, 1)]
    [InlineData(MonitorSamplingKind.Disk, 2, false, 5)]
    [InlineData(MonitorSamplingKind.PeripheralBattery, 2, false, 30)]
    [InlineData(MonitorSamplingKind.Disk, 2, true, 1)]
    [InlineData(MonitorSamplingKind.ConnectedDevices, 2, false, 5)]
    [InlineData(MonitorSamplingKind.Power, 1, false, 15)]
    [InlineData(MonitorSamplingKind.GpuUsage, 1, false, 10)]
    [InlineData(MonitorSamplingKind.PeripheralBattery, 1, true, 15)]
    [InlineData(MonitorSamplingKind.ConnectedDevices, 5, true, 1)]
    public void Strides(MonitorSamplingKind kind, int interval, bool foreground, int expected) =>
        Assert.Equal(expected, MonitorSamplingPolicy.SampleStride(kind, interval, foreground));

    [Fact]
    public void TheTimerWakesOnlyAsOftenAsNeeded()
    {
        Assert.Equal(5, MonitorSamplingPolicy.WakeTicks([MonitorSamplingKind.Disk, MonitorSamplingKind.PeripheralBattery], 2, false));
        Assert.Equal(15, MonitorSamplingPolicy.WakeTicks([MonitorSamplingKind.Power], 1, false));
        Assert.Equal(1, MonitorSamplingPolicy.WakeTicks([MonitorSamplingKind.Cpu, MonitorSamplingKind.Disk], 2, false));
        Assert.Equal(8, MonitorSamplingPolicy.AlignedTick(7, 8));
        Assert.Equal(16, MonitorSamplingPolicy.AlignedTick(16, 8));
        Assert.Equal(3, MonitorSamplingPolicy.AlignedTick(3, 1));
        Assert.True(MonitorSamplingPolicy.ShouldSample(MonitorSamplingKind.Power, 3, 1, false, new HashSet<MonitorSamplingKind> { MonitorSamplingKind.Power }));
        Assert.False(MonitorSamplingPolicy.ShouldSample(MonitorSamplingKind.Power, 3, 1, false));
    }

    private static readonly IReadOnlySet<AppFeature> AllInstalled = new HashSet<AppFeature>
    {
        AppFeature.MonitorCPU, AppFeature.MonitorGPU, AppFeature.MonitorMemory, AppFeature.MonitorNetwork, AppFeature.MonitorDisk,
        AppFeature.MonitorPower, AppFeature.ConnectedDevices,
    };

    [Fact]
    public void NothingSamplesWithoutAReason()
    {
        Assert.True(SamplingPlan.Compute(new MonitorDemand(AllInstalled)).IsIdle);
        Assert.True(SamplingPlan.Compute(new MonitorDemand(new HashSet<AppFeature>(), PanelOpen: true, IslandPageOpen: true)).IsIdle);
    }

    [Fact]
    public void DevicesAreReadOnlyWhereTheyShow()
    {
        var panel = SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelDevices: true));
        Assert.Contains(MonitorSamplingKind.ConnectedDevices, panel.Kinds);
        var hidden = SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelDevices: false));
        Assert.DoesNotContain(MonitorSamplingKind.ConnectedDevices, hidden.Kinds);
        var island = SamplingPlan.Compute(new MonitorDemand(AllInstalled, IslandPageOpen: true, PanelDevices: false));
        Assert.Contains(MonitorSamplingKind.ConnectedDevices, island.Kinds);
        var closed = SamplingPlan.Compute(new MonitorDemand(AllInstalled));
        Assert.DoesNotContain(MonitorSamplingKind.ConnectedDevices, closed.Kinds);
    }

    [Fact]
    public void PerCoreReadsNeedTheOpenCpuRow()
    {
        Assert.True(SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelCoresOpen: true)).PerCore);
        Assert.False(SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelCoresOpen: false)).PerCore);
        Assert.False(SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelCoresOpen: true, PanelCpuRow: false)).PerCore);
        Assert.False(SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelCoresOpen: true)).PerCore);
        Assert.True(SamplingPlan.Compute(new MonitorDemand(AllInstalled, IslandPageOpen: true, IslandDetail: MetricDetail.Cpu)).PerCore);
        var withoutCpu = AllInstalled.Where(f => f != AppFeature.MonitorCPU).ToHashSet();
        Assert.False(SamplingPlan.Compute(new MonitorDemand(withoutCpu, PanelOpen: true, PanelCoresOpen: true)).PerCore);
    }

    [Fact]
    public void PeripheralBatteriesNeedTheirRowTheTrayOrTheBatteryDetail()
    {
        Assert.Contains(MonitorSamplingKind.PeripheralBattery, SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelPeripherals: true)).Kinds);
        Assert.DoesNotContain(MonitorSamplingKind.PeripheralBattery, SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, PanelPeripherals: false)).Kinds);
        Assert.Contains(MonitorSamplingKind.PeripheralBattery, SamplingPlan.Compute(new MonitorDemand(AllInstalled, TrayMetrics: new HashSet<string> { "peripheralBattery" })).Kinds);
        Assert.Contains(MonitorSamplingKind.PeripheralBattery, SamplingPlan.Compute(new MonitorDemand(AllInstalled, IslandPageOpen: true, IslandDetail: MetricDetail.Battery)).Kinds);
        Assert.DoesNotContain(MonitorSamplingKind.PeripheralBattery, SamplingPlan.Compute(new MonitorDemand(AllInstalled, IslandPageOpen: true, IslandDetail: MetricDetail.Power)).Kinds);
    }

    [Fact]
    public void BackgroundNeedsStayOnTheirOwnStrides()
    {
        var plan = SamplingPlan.Compute(new MonitorDemand(AllInstalled, TrayMetrics: new HashSet<string> { "gpu", "network" },
            Alerts: new HashSet<MonitorAlertKind> { MonitorAlertKind.Cpu, MonitorAlertKind.Disk }));
        Assert.False(plan.Foreground);
        Assert.Equal(new HashSet<MonitorSamplingKind> { MonitorSamplingKind.GpuUsageTray, MonitorSamplingKind.Network, MonitorSamplingKind.Cpu, MonitorSamplingKind.Disk }, plan.Kinds);
        var batteryless = SamplingPlan.Compute(new MonitorDemand(AllInstalled, PanelOpen: true, HasBattery: false));
        Assert.DoesNotContain(MonitorSamplingKind.Power, batteryless.Kinds);
    }

    // MARK: Island page

    [Fact]
    public void SystemCardsBalanceAcrossRows()
    {
        Assert.Equal([(0, 3), (3, 5), (5, 7)], SystemPageLayout.RowRanges(7, 504));
        Assert.Equal([(0, 2), (2, 4), (4, 6), (6, 7)], SystemPageLayout.RowRanges(7, 304));
        Assert.Empty(SystemPageLayout.RowRanges(0, 504));
        foreach (var width in new[] { 100.0, 304, 504, 700 })
            foreach (var count in Enumerable.Range(1, 12))
            {
                var rows = SystemPageLayout.RowRanges(count, width);
                var capacity = SystemPageLayout.RailCapacity(width, SystemPageLayout.CardWidth, SystemPageLayout.RowSpacing);
                Assert.Equal(count, rows.Sum(r => r.End - r.Start));
                Assert.All(rows, r => Assert.True(r.End - r.Start <= capacity && r.End > r.Start));
                Assert.True(rows.Max(r => r.End - r.Start) - rows.Min(r => r.End - r.Start) <= 1);
            }
        Assert.Equal(5, SystemPageLayout.RailCapacity(424, 76, 8));
    }

    [Fact]
    public void ThePageGrowsWithItsRowsUntilTheBudget()
    {
        Assert.Equal(140, SystemPageLayout.Height(0, 504, 264));
        var one = SystemPageLayout.Height(3, 504, 500);
        Assert.Equal(72 + SystemPageLayout.HoverInset(504) * 2, one);
        var two = SystemPageLayout.Height(7, 504, 500);
        Assert.Equal(3 * 72 + 2 * 10 + SystemPageLayout.HoverInset(504) * 2, two);
        Assert.Equal(180, SystemPageLayout.Height(7, 504, 180));
    }

    [Fact]
    public void CardsFollowTheInstalledFeatures()
    {
        Assert.Equal(8, SystemPageLayout.CardCount(AllInstalled.Contains, hasBattery: true));
        Assert.Equal(7, SystemPageLayout.CardCount(AllInstalled.Contains, hasBattery: false));
        Assert.Equal(0, SystemPageLayout.CardCount(_ => false, hasBattery: true));
        Assert.True(SystemPageLayout.UsageIsHot(0.85));
        Assert.False(SystemPageLayout.UsageIsHot(0.84));
        Assert.True(SystemPageLayout.BatteryIsLow(20, false));
        Assert.False(SystemPageLayout.BatteryIsLow(20, true));
        Assert.True(SystemPageLayout.DiskIsLow(100, 9));
        Assert.False(SystemPageLayout.DiskIsLow(100, 10));
    }

    // MARK: History

    [Fact]
    public void TheHistoryRingKeepsTheNewest()
    {
        var ring = new MetricHistory(3);
        foreach (var value in new[] { 1.0, 2, 3, 4 }) ring.Push(value);
        Assert.Equal([2.0, 3, 4], ring.Values);
        Assert.Equal([2.0, 3, 4], ring.PublishedValues(true));
        Assert.Empty(ring.PublishedValues(false));
        var one = new MetricHistory(1);
        one.Push(5);
        one.Push(6);
        Assert.Equal([6.0], one.Values);
        Assert.Equal(1, new MetricHistory(0).Capacity);
    }

    // MARK: Settings

    [Fact]
    public void MonitorSettingsKeepUpstreamsNamesAndDefaults()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.Equal(2, store.GetInt(DefaultsKey.MonitorInterval));
        Assert.Equal("monitorIntervalSeconds", DefaultsKey.MonitorInterval);
        Assert.Equal("bytes", store.GetString(DefaultsKey.NetworkSpeedUnit));
        Assert.Equal("used", store.GetString(DefaultsKey.MonitorMemoryMetric));
        Assert.True(store.GetBool(DefaultsKey.MonitorShowSystem));
        Assert.False(store.GetBool(DefaultsKey.MonitorPwrPeripherals));
        Assert.False(store.GetBool(DefaultsKey.MonitorAlertCPU));
        Assert.Equal(90, store.GetInt(DefaultsKey.MonitorAlertCPUThreshold));
        Assert.Equal(15, store.GetInt(DefaultsKey.MonitorAlertCooldownMinutes));
        Assert.False(store.GetBool(DefaultsKey.MenuBarCPU));
        Assert.Equal(70, store.GetInt(DefaultsKey.MenuBarUsageBarMediumThreshold));
        Assert.Equal(90, store.GetInt(DefaultsKey.MenuBarUsageBarHighThreshold));
        Assert.All(FeatureCatalog.All.Where(i => i.Group == FeatureGroup.Monitor && i.Feature != AppFeature.FanControl), i => Assert.True(i.CanInstall));
    }

    // MARK: Speed test

    private sealed class ScriptedTransport(int latencyStatus = 204, int downloadStatus = 200, int uploadStatus = 200, int failDownloadAfter = int.MaxValue) : ISpeedTransport
    {
        public readonly List<string> Requests = [];
        public Action? AfterFirstChunk;
        private int _downloads;

        public Task<int> LatencyAsync(CancellationToken cancellation)
        {
            Requests.Add("latency");
            return Task.FromResult(latencyStatus);
        }

        public Task<int> DownloadAsync(int bytes, Action<long> received, CancellationToken cancellation)
        {
            Requests.Add("download");
            _downloads++;
            if (_downloads > failDownloadAfter) return Task.FromResult(503);
            // An error body never counts as downloaded bytes.
            if (downloadStatus is >= 200 and <= 299) received(1_000_000);
            if (_downloads == 1) AfterFirstChunk?.Invoke();
            return Task.FromResult(downloadStatus);
        }

        public Task<int> UploadAsync(int bytes, Action<long> sent, CancellationToken cancellation)
        {
            Requests.Add("upload");
            if (uploadStatus is >= 200 and <= 299) sent(2_000_000);
            return Task.FromResult(uploadStatus);
        }
    }

    private sealed class Clock
    {
        public double Now;
        public double Tick() => Now;
    }

    /// <summary>A time box that ends when the test says so.</summary>
    private sealed class Boxes
    {
        public readonly List<TaskCompletionSource> Pending = [];
        public Task Delay(TimeSpan span, CancellationToken cancellation)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation.Register(() => source.TrySetCanceled());
            Pending.Add(source);
            return source.Task;
        }
        public void FireAll() { foreach (var p in Pending.ToList()) p.TrySetResult(); }
    }

    private static (SpeedTest Test, ScriptedTransport Transport, Boxes Boxes, Clock Clock) Make(ScriptedTransport? transport = null)
    {
        transport ??= new ScriptedTransport();
        var boxes = new Boxes();
        var clock = new Clock();
        transport.AfterFirstChunk = () => { clock.Now += 1; boxes.FireAll(); };
        return (new SpeedTest(transport, clock.Tick, boxes.Delay), transport, boxes, clock);
    }

    [Fact]
    public async Task ASuccessfulTestMeasuresEverything()
    {
        var (test, transport, _, clock) = Make();
        await test.RunAsync();
        Assert.Equal(SpeedPhase.Done, test.Phase);
        Assert.NotNull(test.LatencyMs);
        Assert.True(test.DownloadMbps > 0);
        Assert.NotNull(test.UploadMbps);
        Assert.Equal(["latency", "latency", "latency", "latency", "latency", "download", "upload"], transport.Requests);
        _ = clock;
    }

    [Fact]
    public async Task AFailedLatencyRequestStopsTheTest()
    {
        var (test, transport, _, _) = Make(new ScriptedTransport(latencyStatus: 500));
        await test.RunAsync();
        Assert.Equal(SpeedPhase.Failed, test.Phase);
        Assert.Equal(["latency"], transport.Requests);
        Assert.Null(test.LatencyMs);
        Assert.Null(test.DownloadMbps);
    }

    [Fact]
    public async Task AnErrorDownloadNeverCountsItsBodyOrPublishesUpload()
    {
        var (test, transport, _, _) = Make(new ScriptedTransport(downloadStatus: 404));
        await test.RunAsync();
        Assert.Equal(SpeedPhase.Failed, test.Phase);
        Assert.Equal(["latency", "latency", "latency", "latency", "latency", "download"], transport.Requests);
        Assert.Null(test.DownloadMbps);
        Assert.Null(test.UploadMbps);
    }

    [Fact]
    public async Task ADownloadThatFailsAfterDataFails()
    {
        var (test, transport, boxes, clock) = Make(new ScriptedTransport(failDownloadAfter: 1));
        transport.AfterFirstChunk = null;
        await test.RunAsync();
        Assert.Equal(SpeedPhase.Failed, test.Phase);
        Assert.Equal(2, transport.Requests.Count(r => r == "download"));
        Assert.Null(test.UploadMbps);
        _ = (boxes, clock);
    }

    [Fact]
    public async Task AFailedUploadFails()
    {
        var (test, transport, _, _) = Make(new ScriptedTransport(uploadStatus: 503));
        await test.RunAsync();
        Assert.Equal(SpeedPhase.Failed, test.Phase);
        Assert.Equal("upload", transport.Requests[^1]);
        Assert.Null(test.UploadMbps);
        Assert.True(test.DownloadMbps > 0);
    }

    [Fact]
    public async Task EveryTimeBoxIsCancelledWhenTheTestEnds()
    {
        var (test, _, boxes, _) = Make();
        await test.RunAsync();
        Assert.All(boxes.Pending, p => Assert.True(p.Task.IsCompleted));
        var phase = test.Phase;
        boxes.FireAll();
        Assert.Equal(phase, test.Phase);
    }
}
