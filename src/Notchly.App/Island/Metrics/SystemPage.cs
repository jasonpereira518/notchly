// SPDX-License-Identifier: GPL-3.0-or-later
// The island's System page: the Windows counterpart of upstream UI/Notch/NotchSystemView.swift and MetricDetailView.swift.
// A grid of cards (CPU, GPU, memory, battery, network, disk, power, devices) with a meter each, balanced across the
// island's width; a card opens its detail (value, graph, rows, top apps) in place, and the speed test lives in
// Network's. The monitor runs only while this page is open.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Notchly.App.Monitor;
using Notchly.App.Platform.Monitor;
using Notchly.App.Platform.Shell;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Button = System.Windows.Controls.Button;

namespace Notchly.App.Island.Metrics;

public sealed class SystemPage
{
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CardBack = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Track = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Normal = Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Hot = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Color Accent = Color.FromRgb(0x64, 0xD2, 0xFF);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Pink = Color.FromRgb(0xFF, 0x6B, 0x9D);

    private readonly NotchWindow _window;
    private readonly Func<SystemMonitor?> _source;
    private readonly SettingsStore _settings;
    private readonly Grid _root;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private SystemMonitor? _monitor;
    private bool _visible;
    private MetricDetail? _detail;
    private double _height = 180, _width = 440;
    private ScrollViewer? _scroller;
    private double _scrollOffset;

    public SystemPage(NotchWindow window, Func<SystemMonitor?> source, SettingsStore settings)
    {
        _window = window;
        _source = source;
        _settings = settings;
        _root = window.SystemPage;
    }

    public void Layout(double height, double width)
    {
        _height = height;
        _width = width;
        _root.Height = Height(height);
        if (_visible) Rebuild();
    }

    /// <summary>The page is as tall as its rows need, up to the budget (then it scrolls); an open detail takes the budget.</summary>
    public double Height(double budget)
    {
        if (_detail is not null) return budget;
        return SystemPageLayout.Height(Cards().Count, _width, budget);
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
            {
                _monitor = _source();
                if (_monitor is null) { Rebuild(); return; }
                _monitor.Updated += OnUpdated;
                _monitor.SetIsland(true, _detail, ProcessKinds());
                Rebuild();
            }
            else
            {
                if (_monitor is not null) { _monitor.Updated -= OnUpdated; _monitor.SetIsland(false); }
                _monitor = null;
                _detail = null;
            }
        }
    }

    private void OnUpdated() => Rebuild(keepScroll: true);

    /// <summary>The page's height changed because a detail opened or closed: the island resizes around it.</summary>
    public event Action? Resized;

    // MARK: Cards

    private sealed record Card(MetricDetail Kind, string Title, string Value, string? Detail, double? Fraction, bool Hot);

    private bool Installed(AppFeature feature) => _settings.IsAvailable(feature);

    private IReadOnlyList<Card> Cards()
    {
        var cards = new List<Card>();
        var s = _monitor?.Snapshot;
        if (Installed(AppFeature.MonitorCPU))
            cards.Add(new Card(MetricDetail.Cpu, "CPU", s?.CpuUsage is { } cpu ? MetricFormat.Percent(cpu) : "--", null, s?.CpuUsage, s?.CpuUsage is { } c && SystemPageLayout.UsageIsHot(c)));
        if (Installed(AppFeature.MonitorGPU))
            cards.Add(new Card(MetricDetail.Gpu, "GPU", s?.GpuUsage is { } gpu ? MetricFormat.Percent(gpu) : "--", null, s?.GpuUsage, s?.GpuUsage is { } g && SystemPageLayout.UsageIsHot(g)));
        if (Installed(AppFeature.MonitorMemory))
        {
            var memory = s?.Memory;
            var fraction = memory is null ? (double?)null : MonitorText.MemoryFraction(memory, _settings);
            cards.Add(new Card(MetricDetail.Memory, "Memory", memory is null ? "--" : MonitorText.MemoryValue(memory, _settings),
                memory is null ? null : $"of {MetricFormat.Bytes(memory.Total)}", fraction, fraction is { } f && SystemPageLayout.UsageIsHot(f)));
        }
        if (Installed(AppFeature.MonitorPower) && _monitor?.HasBattery == true)
        {
            var power = s?.Power;
            cards.Add(new Card(MetricDetail.Battery, "Battery", power is null ? "--" : $"{power.Percent}%", power is null ? null : MonitorText.BatteryState(power),
                power is null ? null : power.Percent / 100.0, power is not null && SystemPageLayout.BatteryIsLow(power.Percent, power.ExternalConnected)));
        }
        if (Installed(AppFeature.MonitorNetwork))
        {
            var inBits = MonitorText.InBits(_settings);
            cards.Add(new Card(MetricDetail.Network, "Network", s?.Network is { } n ? "↓ " + MetricFormat.NetworkRateCompact(n.Down, inBits) : "--",
                s?.Network is { } up ? "↑ " + MetricFormat.NetworkRateCompact(up.Up, inBits) : null, null, false));
        }
        if (Installed(AppFeature.MonitorDisk))
        {
            var volume = s is null ? null : MonitorText.SystemVolume(s.Disks);
            cards.Add(new Card(MetricDetail.Disk, "Disk", volume is null ? "--" : MetricFormat.DiskBytes(volume.Free), volume is null ? null : "free",
                volume is null || volume.Total == 0 ? null : (double)volume.Used / volume.Total, volume is not null && SystemPageLayout.DiskIsLow(volume.Total, volume.Free)));
        }
        if (Installed(AppFeature.MonitorPower))
        {
            var power = s?.Power;
            var (flow, caption) = power is null ? ("--", null) : MonitorText.PowerCard(power);
            cards.Add(new Card(MetricDetail.Power, "Power", flow, caption, null, false));
        }
        if (Installed(AppFeature.ConnectedDevices))
            cards.Add(new Card(MetricDetail.Devices, "Devices", s is null ? "--" : s.Devices.Count.ToString(CultureInfo.CurrentCulture), s?.Devices.Count == 1 ? "connected" : "connected", null, false));
        return cards;
    }

    private IReadOnlySet<ProcessKind> ProcessKinds() => _detail switch
    {
        MetricDetail.Cpu => new HashSet<ProcessKind> { ProcessKind.Cpu },
        MetricDetail.Gpu => new HashSet<ProcessKind> { ProcessKind.Gpu },
        MetricDetail.Memory => new HashSet<ProcessKind> { ProcessKind.Memory },
        _ => new HashSet<ProcessKind>(),
    };

    // MARK: Building

    private void Rebuild(bool keepScroll = false)
    {
        if (keepScroll && _scroller is { } old) _scrollOffset = old.VerticalOffset;
        else if (!keepScroll) _scrollOffset = 0;
        _root.Children.Clear();
        _scroller = null;
        _root.Height = Height(_height);
        if (_monitor is null)
        {
            _root.Children.Add(Centered("Install a System Monitor feature in Settings → Features."));
            return;
        }
        var content = _detail is { } detail ? DetailView(detail) : CardGrid();
        var scroller = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Tag = "SystemPage",
        };
        ScrollEdgeFade.Attach(scroller);
        var offset = _scrollOffset;
        if (offset > 0) scroller.Loaded += (_, _) => scroller.ScrollToVerticalOffset(offset);
        _scroller = scroller;
        _root.Children.Add(scroller);
    }

    private static TextBlock Centered(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };

    private FrameworkElement CardGrid()
    {
        var cards = Cards();
        if (cards.Count == 0) return Centered("No monitor features are installed.");
        var inset = SystemPageLayout.HoverInset(_width);
        var stack = new StackPanel { Margin = new Thickness(inset) };
        var rows = SystemPageLayout.RowRanges(cards.Count, _width - inset * 2);
        for (var r = 0; r < rows.Count; r++)
        {
            var (start, end) = rows[r];
            var grid = new Grid { Margin = new Thickness(0, r == 0 ? 0 : SystemPageLayout.RowSpacing, 0, 0) };
            for (var i = start; i < end; i++)
            {
                if (i > start) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SystemPageLayout.RowSpacing) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var view = CardView(cards[i]);
                Grid.SetColumn(view, (i - start) * 2);
                grid.Children.Add(view);
            }
            stack.Children.Add(grid);
        }
        return stack;
    }

    private FrameworkElement CardView(Card card)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        body.Children.Add(new TextBlock { Text = card.Title, FontSize = 11, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock
        {
            Text = card.Value, FontSize = card.Detail is null ? 22 : 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
        });
        if (card.Detail is not null)
            body.Children.Add(new TextBlock { Text = card.Detail, FontSize = 10, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        if (card.Fraction is { } fraction) body.Children.Add(Meter(fraction, card.Hot));
        var border = new Border
        {
            Height = SystemPageLayout.CardHeight, CornerRadius = new CornerRadius(14), Background = CardBack, Child = body, Cursor = System.Windows.Input.Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, _) => Open(card.Kind);
        return border;
    }

    private static FrameworkElement Meter(double fraction, bool hot, double height = SystemPageLayout.MeterHeight)
    {
        var track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Background = Track, Margin = new Thickness(0, 6, 0, 0) };
        var grid = new Grid();
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = hot ? Hot : Normal, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(fill);
        grid.SizeChanged += (_, e) => fill.Width = Math.Max(height, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
        track.Child = grid;
        return track;
    }

    private void Open(MetricDetail kind)
    {
        _detail = kind;
        _monitor?.SetIsland(true, kind, ProcessKinds());
        Rebuild();
        Resized?.Invoke();
    }

    private void Close()
    {
        _detail = null;
        _monitor?.SetIsland(true, null, ProcessKinds());
        Rebuild();
        Resized?.Invoke();
    }

    // MARK: Detail

    private FrameworkElement DetailView(MetricDetail kind)
    {
        var s = _monitor!.Snapshot;
        var stack = new StackPanel { Margin = new Thickness(2, 0, 2, 4) };
        var title = kind switch
        {
            MetricDetail.Cpu => "CPU", MetricDetail.Gpu => "GPU", MetricDetail.Memory => "Memory", MetricDetail.Network => "Network", MetricDetail.Disk => "Disk",
            MetricDetail.Battery => "Battery", MetricDetail.Power => "Power", _ => "Connected devices",
        };
        var back = new Button { Style = (Style)_window.Resources["ChipButton"], Content = "‹  " + title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        back.Click += (_, _) => Close();
        stack.Children.Add(back);

        switch (kind)
        {
            case MetricDetail.Cpu:
                Headline(stack, s.CpuUsage is { } cpu ? MetricFormat.Percent(cpu) : "--", _monitor.History(_monitor.Cpu), 1, Accent, "of all processors");
                if (s.Cores is { Count: > 0 } cores) stack.Children.Add(CoreGrid(cores));
                Rows(stack, ("Logical processors", Environment.ProcessorCount.ToString(CultureInfo.CurrentCulture)),
                    ("Efficiency cores", EfficiencyCores(s.Cores)));
                Processes(stack, ProcessKind.Cpu, p => MetricFormat.Percent(p.Value / 100));
                break;
            case MetricDetail.Gpu:
                Headline(stack, s.GpuUsage is { } gpu ? MetricFormat.Percent(gpu) : "--", _monitor.History(_monitor.Gpu), 1, Accent, "busiest engine");
                if (s.Gpus is { } gpus) Rows(stack, gpus.Select(g => (MonitorText.ShortAdapter(g.Adapter.Name), (string?)MetricFormat.Percent(g.Usage))).ToArray());
                Processes(stack, ProcessKind.Gpu, p => MetricFormat.Percent(p.Value / 100));
                break;
            case MetricDetail.Memory:
                if (s.Memory is { } memory)
                {
                    Headline(stack, MonitorText.MemoryValue(memory, _settings), _monitor.History(_monitor.Memory), 1, Accent, $"of {MetricFormat.Bytes(memory.Total)}");
                    Rows(stack, ("Pressure", MonitorText.Pressure(memory.Pressure)), ("In use", MetricFormat.Bytes(memory.Used)), ("Apps", MetricFormat.Bytes(memory.App)),
                        ("Cached files", MetricFormat.Bytes(memory.Cached)), ("Compressed", memory.Compressed > 0 ? MetricFormat.Bytes(memory.Compressed) : null),
                        ("Committed", $"{MetricFormat.Bytes(memory.CommitBytes)} of {MetricFormat.Bytes(memory.CommitLimit)}"), ("Available", MetricFormat.Bytes(memory.Available)));
                }
                Processes(stack, ProcessKind.Memory, p => MetricFormat.Bytes((ulong)p.Value));
                break;
            case MetricDetail.Network: NetworkDetail(stack, s); break;
            case MetricDetail.Disk: DiskDetail(stack, s); break;
            case MetricDetail.Battery:
                if (s.Power is { } battery)
                {
                    Headline(stack, $"{battery.Percent}%", _monitor.History(_monitor.Battery), 1, Green, MonitorText.BatteryState(battery));
                    Rows(stack, ("Health", battery.HealthPercent is { } h ? $"{h}%" : null), ("Cycles", battery.CycleCount?.ToString(CultureInfo.CurrentCulture)),
                        ("Chemistry", battery.Chemistry), ("Capacity", battery.FullMilliwattHours is { } full ? $"{full / 1000.0:0.0} Wh of {(battery.DesignMilliwattHours ?? full) / 1000.0:0.0} Wh design" : null));
                    foreach (var p in s.Peripherals) Rows(stack, (p.Name, $"{p.Percent}%"));
                }
                break;
            case MetricDetail.Power:
                if (s.Power is { } power)
                {
                    Headline(stack, MonitorText.PowerFlow(power), [], 1, Accent, "the battery's own flow");
                    Rows(stack, ("Remaining", power.RemainingMilliwattHours is { } rem ? $"{rem / 1000.0:0.0} Wh" : null), ("Time left", MonitorText.RemainingText(power)),
                        ("Charger", power.ExternalConnected ? "Connected" : "Not connected"));
                }
                break;
            default:
                if (s.Devices.Count == 0) stack.Children.Add(Centered("No external USB devices are connected."));
                foreach (var device in s.Devices)
                    Rows(stack, (device.Name.Length > 0 ? device.Name : "USB device", device.Vendor));
                break;
        }
        return stack;
    }

    private void Headline(StackPanel stack, string value, IReadOnlyList<double> history, double ceiling, Color color, string caption)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom });
        head.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = Dim, Margin = new Thickness(8, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(head);
        if (history.Count > 1)
        {
            var graph = new Sparkline { Color = color, Height = SystemPageLayout.DetailGraphHeight, Margin = new Thickness(0, 6, 0, 6) };
            graph.Set(history, ceiling);
            stack.Children.Add(graph);
        }
    }

    private static void Rows(StackPanel stack, params (string Label, string? Value)[] rows)
    {
        foreach (var (label, value) in rows)
        {
            if (value is null) continue;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
            var text = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
    }

    private static string? EfficiencyCores(IReadOnlyList<CoreLoad>? cores) =>
        cores?.Count(core => core.IsEfficiency) is > 0 and var count ? count.ToString(CultureInfo.CurrentCulture) : null;

    private FrameworkElement CoreGrid(IReadOnlyList<CoreLoad> cores)
    {
        var panel = new UniformGrid { Columns = Math.Min(cores.Count, Math.Max(4, (int)(_width / 36))), Margin = new Thickness(0, 0, 0, 6) };
        foreach (var core in cores)
        {
            var cell = new StackPanel { Margin = new Thickness(2) };
            cell.Children.Add(new TextBlock { Text = core.IsEfficiency ? "E" : "P", FontSize = 9, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(Meter(core.Usage ?? 0, core.Usage is { } u && SystemPageLayout.UsageIsHot(u), 4));
            panel.Children.Add(cell);
        }
        return panel;
    }

    private void Processes(StackPanel stack, ProcessKind kind, Func<ProcessRow, string> format)
    {
        stack.Children.Add(new TextBlock { Text = "Top apps", FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 8, 0, 2) });
        if (!_monitor!.Snapshot.TopProcesses.TryGetValue(kind, out var rows) || rows.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "Reading…", FontSize = 12, Foreground = Faint });
            return;
        }
        foreach (var row in rows.Take(8))
        {
            var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            if (row.ExePath is { } path) LoadIcon(path, icon);
            line.Children.Add(icon);
            var name = new TextBlock { Text = row.Name + (row.Processes > 1 ? $"  ×{row.Processes}" : ""), FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);
            var value = new TextBlock { Text = format(row), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(value, 2);
            line.Children.Add(value);
            stack.Children.Add(line);
        }
    }

    private async void LoadIcon(string path, Image image)
    {
        if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = await ShellThumbnails.GetAsync(path, 32);
        image.Source = icon;
    }

    private void NetworkDetail(StackPanel stack, SystemSnapshot s)
    {
        var inBits = MonitorText.InBits(_settings);
        var down = s.Network?.Down ?? 0;
        var ceiling = MetricFormat.NetworkGraphCeiling(Math.Max(_monitor!.NetDown.Values.DefaultIfEmpty(0).Max(), _monitor.NetUp.Values.DefaultIfEmpty(0).Max()), inBits);
        Headline(stack, "↓ " + MetricFormat.NetworkRate(down, inBits), _monitor.History(_monitor.NetDown), ceiling, Accent, "↑ " + MetricFormat.NetworkRate(s.Network?.Up ?? 0, inBits));
        var up = new Sparkline { Color = Green, Height = 24, Margin = new Thickness(0, 0, 0, 6) };
        up.Set(_monitor.History(_monitor.NetUp), ceiling);
        stack.Children.Add(up);
        Rows(stack, ("Received this session", MetricFormat.Bytes(s.NetworkTotals.Down)), ("Sent this session", MetricFormat.Bytes(s.NetworkTotals.Up)));
        foreach (var address in s.Addresses) Rows(stack, (address.Adapter, address.Address));

        var test = _monitor.SpeedTest;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var start = new Button
        {
            Style = (Style)_window.Resources["ChipButton"], IsEnabled = !test.IsRunning,
            Content = test.IsRunning ? test.Phase switch { SpeedPhase.Latency => "Testing latency…", SpeedPhase.Download => "Testing download…", _ => "Testing upload…" }
                : test.Phase == SpeedPhase.Done ? "Test again" : "Speed test",
        };
        start.Click += (_, _) =>
        {
            test.Changed -= OnSpeedChanged;
            test.Changed += OnSpeedChanged;
            _monitor.StartSpeedTest();
        };
        line.Children.Add(start);
        stack.Children.Add(line);
        if (test.Phase == SpeedPhase.Failed) stack.Children.Add(new TextBlock { Text = "The test failed: " + test.Error, FontSize = 11, Foreground = Hot, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        Rows(stack, ("Latency", test.LatencyMs is { } ms ? $"{ms:0} ms" : null), ("Download", test.DownloadMbps is { } d ? $"{d:0.0} Mbps" : null), ("Upload", test.UploadMbps is { } u ? $"{u:0.0} Mbps" : null));
    }

    private void OnSpeedChanged() => _window.Dispatcher.BeginInvoke(() => { if (_visible && _detail == MetricDetail.Network) Rebuild(keepScroll: true); });

    private void DiskDetail(StackPanel stack, SystemSnapshot s)
    {
        var activity = s.DiskActivity;
        var ceiling = MetricFormat.GraphCeiling(Math.Max(_monitor!.DiskRead.Values.DefaultIfEmpty(0).Max(), _monitor.DiskWrite.Values.DefaultIfEmpty(0).Max()), 1024);
        Headline(stack, activity is { } io ? "R " + MetricFormat.BytesPerSecondCompact(io.Read) : "--", _monitor.History(_monitor.DiskRead), ceiling, Accent,
            activity is { } w ? "W " + MetricFormat.BytesPerSecondCompact(w.Write) : "");
        var write = new Sparkline { Color = Pink, Height = 24, Margin = new Thickness(0, 0, 0, 6) };
        write.Set(_monitor.History(_monitor.DiskWrite), ceiling);
        stack.Children.Add(write);
        foreach (var volume in s.Disks)
        {
            var block = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            Rows(block, ($"{volume.Label} ({volume.Name})" + (volume.FileSystem is { } fs ? $" · {fs}" : ""), $"{MetricFormat.DiskBytes(volume.Free)} free of {MetricFormat.DiskBytes(volume.Total)}"));
            block.Children.Add(Meter(volume.Total == 0 ? 0 : (double)volume.Used / volume.Total, SystemPageLayout.DiskIsLow(volume.Total, volume.Free)));
            stack.Children.Add(block);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
