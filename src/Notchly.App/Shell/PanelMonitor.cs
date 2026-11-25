// SPDX-License-Identifier: GPL-3.0-or-later
// The panel's monitor sections: the Windows counterpart of upstream UI/MenuPanel/SystemSection.swift, NetworkSection,
// DiskSection and PowerSection. Each row has a value, a usage bar (blue, amber from 60%, red from 85%) and, where the
// graph is on, a 30 pt history; CPU, GPU and memory open into their top apps. Rows follow the monitorShow* and monitorSys*
// choices, and the monitor reads only while the panel is open.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchly.App.Monitor;
using Notchly.App.Platform.Monitor;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed class PanelMonitor
{
    private static readonly Color Accent = Color.FromRgb(0x64, 0xD2, 0xFF);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xD6, 0x0A);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly StackPanel _host;
    private readonly SettingsStore _settings;
    private readonly HashSet<ProcessKind> _open = [];
    private bool _devicesOpen;
    private SystemMonitor? _monitor;

    public PanelMonitor(StackPanel host, SettingsStore settings)
    {
        _host = host;
        _settings = settings;
    }

    private bool Installed(AppFeature feature) => _settings.IsAvailable(feature);

    public void Show(SystemMonitor? monitor)
    {
        Hide();
        _monitor = monitor;
        if (monitor is null) return;
        monitor.Updated += OnUpdated;
        Apply();
        Rebuild();
    }

    public void Hide()
    {
        if (_monitor is null) return;
        _monitor.Updated -= OnUpdated;
        _monitor.SetPanel(false);
        _monitor = null;
        _host.Children.Clear();
    }

    private void OnUpdated() => Rebuild();

    private void Apply() => _monitor?.SetPanel(true, _open.Contains(ProcessKind.Cpu), _open.ToHashSet());

    private void Toggle(ProcessKind kind)
    {
        if (!_open.Remove(kind)) _open.Add(kind);
        Apply();
        Rebuild();
    }

    // MARK: Sections

    private void Rebuild()
    {
        if (_monitor is not { } monitor) return;
        _host.Children.Clear();
        var s = monitor.Snapshot;
        var system = _settings.GetBool(DefaultsKey.MonitorShowSystem) && (Installed(AppFeature.MonitorCPU) || Installed(AppFeature.MonitorGPU) || Installed(AppFeature.MonitorMemory) || Installed(AppFeature.ConnectedDevices));
        if (system) _host.Children.Add(Section("System", SystemRows(monitor, s)));
        if (_settings.GetBool(DefaultsKey.MonitorShowNetwork) && Installed(AppFeature.MonitorNetwork)) _host.Children.Add(Section("Network", NetworkRows(monitor, s)));
        if (_settings.GetBool(DefaultsKey.MonitorShowDisk) && Installed(AppFeature.MonitorDisk)) _host.Children.Add(Section("Disk", DiskRows(monitor, s)));
        if (_settings.GetBool(DefaultsKey.MonitorShowPower) && Installed(AppFeature.MonitorPower) && monitor.HasBattery) _host.Children.Add(Section("Power", PowerRows(s)));
    }

    private static FrameworkElement Section(string title, IEnumerable<FrameworkElement> rows)
    {
        var block = new StackPanel();
        block.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        block.Children.Add(new Card { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Content = stack });
        return block;
    }

    private IEnumerable<FrameworkElement> SystemRows(SystemMonitor monitor, SystemSnapshot s)
    {
        if (Installed(AppFeature.MonitorCPU) && _settings.GetBool(DefaultsKey.MonitorSysCPU))
        {
            yield return UsageRow("Processor", s.CpuUsage is { } cpu ? MetricFormat.Percent(cpu) : "--", s.CpuUsage, monitor.History(monitor.Cpu), 1, _settings.GetBool(DefaultsKey.MonitorGraphCPU), ProcessKind.Cpu);
            if (_open.Contains(ProcessKind.Cpu))
            {
                if (_settings.GetBool(DefaultsKey.MonitorSysCPUCores) && s.Cores is { Count: > 0 } cores) yield return Cores(cores);
                yield return TopApps(s, ProcessKind.Cpu, p => MetricFormat.Percent(p.Value / 100));
            }
        }
        if (Installed(AppFeature.MonitorGPU) && _settings.GetBool(DefaultsKey.MonitorSysGPU))
        {
            var name = s.Gpus is { Count: > 0 } g ? MonitorText.ShortAdapter(g.OrderByDescending(x => x.Usage).First().Adapter.Name) : "";
            yield return UsageRow("Graphics" + (name.Length > 0 ? $"  ·  {name}" : ""), s.GpuUsage is { } gpu ? MetricFormat.Percent(gpu) : "--", s.GpuUsage, monitor.History(monitor.Gpu), 1,
                _settings.GetBool(DefaultsKey.MonitorGraphGPU), ProcessKind.Gpu);
            if (_open.Contains(ProcessKind.Gpu)) yield return TopApps(s, ProcessKind.Gpu, p => MetricFormat.Percent(p.Value / 100));
        }
        if (Installed(AppFeature.MonitorMemory) && _settings.GetBool(DefaultsKey.MonitorSysMemory) && s.Memory is { } memory)
        {
            yield return UsageRow($"Memory  ·  {MonitorText.Pressure(memory.Pressure)}", $"{MonitorText.MemoryValue(memory, _settings)} of {MetricFormat.Bytes(memory.Total)}",
                MonitorText.MemoryFraction(memory, _settings), monitor.History(monitor.Memory), 1, _settings.GetBool(DefaultsKey.MonitorGraphMemory), ProcessKind.Memory);
            if (_open.Contains(ProcessKind.Memory)) yield return TopApps(s, ProcessKind.Memory, p => MetricFormat.Bytes((ulong)p.Value));
        }
        if (_settings.GetBool(DefaultsKey.MonitorSysUptime)) yield return TextRow("Uptime", MetricFormat.Uptime(s.UptimeSeconds));
        if (Installed(AppFeature.ConnectedDevices) && _settings.GetBool(DefaultsKey.MonitorSysConnectedDevices))
        {
            var toggle = TextRow("Connected devices", s.Devices.Count.ToString(CultureInfo.CurrentCulture), clickable: true);
            toggle.MouseLeftButtonUp += (_, _) => { _devicesOpen = !_devicesOpen; Rebuild(); };
            yield return toggle;
            if (_devicesOpen)
                foreach (var device in s.Devices) yield return TextRow("   " + (device.Name.Length > 0 ? device.Name : "USB device"), device.Vendor ?? "");
        }
    }

    private IEnumerable<FrameworkElement> NetworkRows(SystemMonitor monitor, SystemSnapshot s)
    {
        var inBits = MonitorText.InBits(_settings);
        if (_settings.GetBool(DefaultsKey.MonitorNetSpeed))
        {
            var ceiling = MetricFormat.NetworkGraphCeiling(Math.Max(monitor.NetDown.Values.DefaultIfEmpty(0).Max(), monitor.NetUp.Values.DefaultIfEmpty(0).Max()), inBits);
            yield return TextRow("↓ Download", MetricFormat.NetworkRate(s.Network?.Down ?? 0, inBits));
            if (_settings.GetBool(DefaultsKey.MonitorGraphNetwork)) yield return Graph(monitor.History(monitor.NetDown), ceiling, Accent);
            yield return TextRow("↑ Upload", MetricFormat.NetworkRate(s.Network?.Up ?? 0, inBits));
            if (_settings.GetBool(DefaultsKey.MonitorGraphNetwork)) yield return Graph(monitor.History(monitor.NetUp), ceiling, Green);
        }
        if (_settings.GetBool(DefaultsKey.MonitorNetTotals))
        {
            yield return TextRow("Received this session", MetricFormat.Bytes(s.NetworkTotals.Down));
            yield return TextRow("Sent this session", MetricFormat.Bytes(s.NetworkTotals.Up));
        }
        if (_settings.GetBool(DefaultsKey.MonitorNetAddresses))
            foreach (var address in s.Addresses) yield return TextRow(address.Adapter, address.Address);
        if (_settings.GetBool(DefaultsKey.MonitorNetTest)) yield return SpeedTestRow(monitor);
    }

    private FrameworkElement SpeedTestRow(SystemMonitor monitor)
    {
        var test = monitor.SpeedTest;
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var button = new Button
        {
            IsEnabled = !test.IsRunning,
            Content = test.IsRunning ? test.Phase switch { SpeedPhase.Latency => "Testing latency…", SpeedPhase.Download => "Testing download…", _ => "Testing upload…" }
                : test.Phase == SpeedPhase.Done ? "Test again" : "Run speed test",
        };
        button.Click += (_, _) =>
        {
            test.Changed -= OnSpeedChanged;
            test.Changed += OnSpeedChanged;
            monitor.StartSpeedTest();
            Rebuild();
        };
        stack.Children.Add(button);
        if (test.Phase == SpeedPhase.Failed) stack.Children.Add(new TextBlock { Text = "The test failed: " + test.Error, FontSize = 11, Foreground = new SolidColorBrush(Red), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (test.LatencyMs is { } ms) stack.Children.Add(TextRow("Latency", $"{ms:0} ms"));
        if (test.DownloadMbps is { } down) stack.Children.Add(TextRow("Download", $"{down:0.0} Mbps"));
        if (test.UploadMbps is { } up) stack.Children.Add(TextRow("Upload", $"{up:0.0} Mbps"));
        return stack;
    }

    private void OnSpeedChanged() => Application.Current.Dispatcher.BeginInvoke(Rebuild);

    private IEnumerable<FrameworkElement> DiskRows(SystemMonitor monitor, SystemSnapshot s)
    {
        if (_settings.GetBool(DefaultsKey.MonitorDiskUsage))
            foreach (var volume in s.Disks)
            {
                yield return UsageRow($"{volume.Label} ({volume.Name})" + (volume.FileSystem is { } fs ? $"  ·  {fs}" : ""), $"{MetricFormat.DiskBytes(volume.Free)} free of {MetricFormat.DiskBytes(volume.Total)}",
                    volume.Total == 0 ? null : (double)volume.Used / volume.Total, [], 1, false, null);
            }
        if (_settings.GetBool(DefaultsKey.MonitorDiskActivity) && s.DiskActivity is { } io)
        {
            var ceiling = MetricFormat.GraphCeiling(Math.Max(monitor.DiskRead.Values.DefaultIfEmpty(0).Max(), monitor.DiskWrite.Values.DefaultIfEmpty(0).Max()), 1024);
            yield return TextRow("Read", MetricFormat.BytesPerSecond(io.Read));
            if (_settings.GetBool(DefaultsKey.MonitorGraphDisk)) yield return Graph(monitor.History(monitor.DiskRead), ceiling, Accent);
            yield return TextRow("Write", MetricFormat.BytesPerSecond(io.Write));
            if (_settings.GetBool(DefaultsKey.MonitorGraphDisk)) yield return Graph(monitor.History(monitor.DiskWrite), ceiling, Color.FromRgb(0xFF, 0x6B, 0x9D));
        }
    }

    private IEnumerable<FrameworkElement> PowerRows(SystemSnapshot s)
    {
        if (s.Power is not { } power) yield break;
        if (_settings.GetBool(DefaultsKey.MonitorPwrBattery))
            yield return UsageRow("Battery  ·  " + MonitorText.BatteryState(power), $"{power.Percent}%", power.Percent / 100.0, _monitor!.History(_monitor.Battery), 1, _settings.GetBool(DefaultsKey.MonitorGraphBattery), null, lowIsBad: true);
        var (flow, detail) = MonitorText.PowerCard(power);
        yield return TextRow("Power", detail is null ? flow : $"{flow} {detail}");
        if (_settings.GetBool(DefaultsKey.MonitorPwrTimeRemaining) && MonitorText.RemainingText(power) is { } left) yield return TextRow("Time remaining", left);
        if (_settings.GetBool(DefaultsKey.MonitorPwrHealth) && power.HealthPercent is { } health) yield return TextRow("Battery health", $"{health}%" + (power.CycleCount is { } cycles ? $"  ·  {cycles} cycles" : ""));
        if (_settings.GetBool(DefaultsKey.MonitorPwrPeripherals))
            foreach (var peripheral in s.Peripherals) yield return TextRow(peripheral.Name, $"{peripheral.Percent}%");
    }

    // MARK: Pieces

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color BarColor(double fraction, bool lowIsBad) =>
        lowIsBad ? fraction <= 0.2 ? Red : fraction <= 0.35 ? Amber : Green : fraction >= 0.85 ? Red : fraction >= 0.6 ? Amber : Accent;

    private FrameworkElement UsageRow(string title, string value, double? fraction, IReadOnlyList<double> history, double ceiling, bool graph, ProcessKind? opens, bool lowIsBad = false)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, Cursor = opens is null ? null : System.Windows.Input.Cursors.Hand };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        var text = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        stack.Children.Add(head);
        if (fraction is { } f)
            stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(f, 0, 1), Height = 4, Margin = new Thickness(0, 4, 0, 0), Foreground = Brush(BarColor(f, lowIsBad)) });
        if (graph && history.Count > 1) stack.Children.Add(Graph(history, ceiling, BarColor(fraction ?? 0, lowIsBad)));
        if (opens is { } kind) stack.MouseLeftButtonUp += (_, _) => Toggle(kind);
        return stack;
    }

    private static FrameworkElement Graph(IReadOnlyList<double> history, double ceiling, Color color)
    {
        var graph = new Sparkline { Color = color, Height = 30, Margin = new Thickness(0, 4, 0, 4) };
        graph.Set(history, ceiling);
        return graph;
    }

    private static Grid TextRow(string label, string value, bool clickable = false)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 4), Background = Brushes.Transparent, Cursor = clickable ? System.Windows.Input.Cursors.Hand : null };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var text = new TextBlock { Text = value, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private FrameworkElement Cores(IReadOnlyList<CoreLoad> cores)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var core in cores)
            grid.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = core.Usage ?? 0, Height = 4, Margin = new Thickness(2), Foreground = Brush(BarColor(core.Usage ?? 0, false)),
                ToolTip = $"{(core.IsEfficiency ? "E" : "P")}-core {core.Index}" });
        return grid;
    }

    private FrameworkElement TopApps(SystemSnapshot s, ProcessKind kind, Func<ProcessRow, string> format)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (!s.TopProcesses.TryGetValue(kind, out var rows) || rows.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "Reading…", FontSize = 11, Opacity = 0.6 });
            return stack;
        }
        foreach (var row in rows.Take(8)) stack.Children.Add(TextRow("   " + row.Name + (row.Processes > 1 ? $"  ×{row.Processes}" : ""), format(row)));
        return stack;
    }
}
