// SPDX-License-Identifier: GPL-3.0-or-later
// The monitor's tray readouts: the Windows counterpart of upstream App/MenuBarRenderer.swift and StatusItemController.
// Windows gives each notification-area icon one small square, so each chosen metric gets an icon of its own (at most six,
// in the person's order): a label above a value, or above a bar. The value colours follow the usage thresholds (normal,
// elevated from 70%, critical from 90%) and the battery turns amber and red as it runs low. New icons start in the
// overflow until pinned, which Settings says. All off by default.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Win32;
using Notchly.App.Monitor;
using Notchly.App.Platform.Monitor;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Shell;

public sealed class TrayReadoutService(App app) : IDisposable
{
    public const int MaximumIcons = 6;
    private static readonly Color Normal = Color.FromRgb(0x64, 0xD2, 0xFF), Elevated = Color.FromRgb(0xFF, 0xD6, 0x0A), Critical = Color.FromRgb(0xFF, 0x45, 0x3A),
        Charging = Color.FromRgb(0x30, 0xD1, 0x58);

    private sealed record Shown(string Metric, string Lines, string Look);

    private readonly Dictionary<string, (TaskbarIcon Icon, Shown Last)> _icons = new(StringComparer.Ordinal);
    private SystemMonitor? _monitor;

    private SettingsStore Settings => app.Settings;

    private static readonly (string Key, string Metric, AppFeature Feature)[] Metrics =
    [
        (DefaultsKey.MenuBarCPU, "cpu", AppFeature.MonitorCPU), (DefaultsKey.MenuBarGPU, "gpu", AppFeature.MonitorGPU), (DefaultsKey.MenuBarMemory, "memory", AppFeature.MonitorMemory),
        (DefaultsKey.MenuBarBattery, "battery", AppFeature.MonitorPower), (DefaultsKey.MenuBarBatteryTime, "batteryTime", AppFeature.MonitorPower),
        (DefaultsKey.MenuBarPeripheralBattery, "peripheralBattery", AppFeature.MonitorPower), (DefaultsKey.MenuBarNetwork, "network", AppFeature.MonitorNetwork),
        (DefaultsKey.MenuBarDiskUsage, "diskUsage", AppFeature.MonitorDisk), (DefaultsKey.MenuBarDiskActivity, "diskActivity", AppFeature.MonitorDisk),
        (DefaultsKey.MenuBarConnectedDevices, "connectedDevices", AppFeature.ConnectedDevices),
    ];

    /// <summary>The metrics to show, in the person's order, for features that are installed.</summary>
    private IReadOnlyList<string> Chosen()
    {
        var order = Settings.GetList(DefaultsKey.MenuBarMetricOrder).ToList();
        var enabled = Metrics.Where(m => Settings.GetBool(m.Key) && Settings.IsAvailable(m.Feature)).Select(m => m.Metric).ToHashSet();
        return order.Where(enabled.Contains).Concat(enabled.Except(order)).Take(MaximumIcons).ToList();
    }

    /// <summary>Follows the settings: creates, removes and redraws icons.</summary>
    public void Sync()
    {
        var chosen = Chosen();
        foreach (var gone in _icons.Keys.Except(chosen).ToList())
        {
            _icons[gone].Icon.Dispose();
            _icons.Remove(gone);
        }
        if (chosen.Count == 0)
        {
            if (_monitor is not null) { _monitor.Updated -= Redraw; _monitor = null; }
            return;
        }
        if (_monitor is null)
        {
            _monitor = app.Monitor;
            _monitor.Updated += Redraw;
        }
        _monitor.PlanChanged();
        Redraw();
    }

    private void Redraw()
    {
        if (_monitor is not { } monitor) return;
        foreach (var metric in Chosen())
        {
            var shown = Describe(metric, monitor.Snapshot);
            if (shown is null) continue;
            if (_icons.TryGetValue(metric, out var existing) && existing.Last == shown) continue;
            try
            {
                var icon = existing.Icon ?? CreateIcon(metric);
                var previous = icon.Icon;
                icon.Icon = Render(shown, Settings.GetString(DefaultsKey.MenuBarMetricAppearance));
                icon.ToolTipText = $"{Notchly.Core.AppIdentity.Name} · {TrayReadout.Label(metric)}  {shown.Lines.Replace('\n', ' ')}";
                previous?.Dispose();
                _icons[metric] = (icon, shown);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) { Log.Debug(e, "Tray readout {Metric} could not be drawn", metric); }
        }
    }

    private TaskbarIcon CreateIcon(string metric)
    {
        var icon = new TaskbarIcon { NoLeftClickDelay = true, ToolTipText = TrayReadout.Label(metric) };
        icon.TrayLeftMouseUp += (_, _) => app.TogglePanel();
        icon.ForceCreate(false);
        return icon;
    }

    // MARK: What each metric reads

    private Shown? Describe(string metric, SystemSnapshot s)
    {
        var medium = Settings.GetInt(DefaultsKey.MenuBarUsageBarMediumThreshold, TrayReadout.DefaultMediumThreshold);
        var high = Settings.GetInt(DefaultsKey.MenuBarUsageBarHighThreshold, TrayReadout.DefaultHighThreshold);
        var inBits = MonitorText.InBits(Settings);
        string Look(double percent) => TrayReadout.Level(percent, medium, high).ToString();
        switch (metric)
        {
            case "cpu" when s.CpuUsage is { } cpu: return new Shown(metric, Number(cpu * 100), Look(cpu * 100) + "|" + cpu);
            case "gpu" when s.GpuUsage is { } gpu: return new Shown(metric, Number(gpu * 100), Look(gpu * 100) + "|" + gpu);
            case "memory" when s.Memory is { } memory:
                var fraction = MonitorText.MemoryFraction(memory, Settings);
                return new Shown(metric, Number(fraction * 100), Look(fraction * 100) + "|" + fraction);
            case "diskUsage" when MonitorText.SystemVolume(s.Disks) is { } volume:
                var used = volume.Total == 0 ? 0 : (double)volume.Used / volume.Total;
                var text = Settings.GetString(DefaultsKey.MenuBarDiskStyle) switch
                {
                    "free" => MetricFormat.DiskBytes(volume.Free).Replace(" ", "").Replace("B", ""),
                    "used" => MetricFormat.DiskBytes(volume.Used).Replace(" ", "").Replace("B", ""),
                    _ => Number(used * 100),
                };
                return new Shown(metric, text, Look(used * 100) + "|" + used);
            case "diskActivity" when s.DiskActivity is { } io:
                return new Shown(metric, MetricFormat.BytesPerSecondCompact(io.Read) + "\n" + MetricFormat.BytesPerSecondCompact(io.Write), "Normal|0");
            case "network" when s.Network is { } net:
                var down = MetricFormat.NetworkRateCompact(net.Down, inBits);
                var up = MetricFormat.NetworkRateCompact(net.Up, inBits);
                return new Shown(metric, Settings.GetBool(DefaultsKey.MenuBarNetworkUploadFirst) ? up + "\n" + down : down + "\n" + up, "Normal|0");
            case "battery" when s.Power is { } power:
                var tone = TrayReadout.BatteryColor(power.Percent, power.ExternalConnected, Settings.GetInt(DefaultsKey.NotchLowBatteryThreshold, 10), Settings.GetInt(DefaultsKey.NotchLowBatteryEarlyThreshold, 20));
                return new Shown(metric, power.Percent.ToString(), (power.IsCharging ? "Charging" : tone.ToString()) + "|" + power.Percent / 100.0);
            case "batteryTime" when s.Power is { } timed:
                var left = BatteryRules.RemainingSeconds(timed.TimeToEmptyMinutes, timed.ExternalConnected, timed.IsCharging);
                return new Shown(metric, left is { } seconds ? $"{(int)(seconds / 3600)}h{(int)(seconds % 3600 / 60):00}" : "--", "Normal|0");
            case "peripheralBattery" when s.Peripherals.Count > 0:
                var lowest = s.Peripherals.MinBy(p => p.Percent ?? 100)!;
                return new Shown(metric, $"{lowest.Percent}", (lowest.Percent <= 10 ? "Red" : lowest.Percent <= 20 ? "Amber" : "Normal") + "|" + (lowest.Percent ?? 0) / 100.0);
            case "connectedDevices": return new Shown(metric, s.Devices.Count.ToString(), "Normal|0");
        }
        return null;
    }

    private static string Number(double percent) => ((int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero)).ToString();

    // MARK: Drawing

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private static System.Drawing.Icon Render(Shown shown, string appearance)
    {
        const int size = 32;
        var light = TaskbarIsLight();
        var ink = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var parts = shown.Look.Split('|');
        var tone = parts[0] switch
        {
            "Elevated" or "Amber" => Elevated, "Critical" or "Red" => Critical, "Charging" => Charging, _ => Normal,
        };
        var fraction = double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
        var bars = appearance == "bars" && shown.Metric is "cpu" or "gpu" or "memory" or "diskUsage" or "battery";
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            void Text(string text, double emSize, Brush brush, double y)
            {
                var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, emSize, brush, 1.0);
                dc.DrawText(formatted, new Point((size - formatted.Width) / 2, y));
            }
            Text(TrayReadout.Label(shown.Metric), 9, new SolidColorBrush(Color.FromArgb(0xB3, ink.R, ink.G, ink.B)), -1);
            if (bars)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, ink.R, ink.G, ink.B)), null, new Rect(6, 12, size - 12, 15), 3, 3);
                var height = Math.Max(3, 15 * Math.Clamp(fraction, 0, 1));
                dc.DrawRoundedRectangle(new SolidColorBrush(tone), null, new Rect(6, 27 - height, size - 12, height), 3, 3);
            }
            else if (shown.Lines.Contains('\n'))
            {
                var lines = shown.Lines.Split('\n');
                Text(lines[0], 11, new SolidColorBrush(ink), 8);
                Text(lines[1], 11, new SolidColorBrush(ink), 19);
            }
            else
            {
                Text(shown.Lines, shown.Lines.Length > 3 ? 13 : 17, new SolidColorBrush(shown.Look.StartsWith("Normal", StringComparison.Ordinal) ? ink : tone), 9);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var data = png.ToArray();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
            writer.Write((byte)size); writer.Write((byte)size); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((short)1); writer.Write((short)32);
            writer.Write(data.Length); writer.Write(22);
            writer.Write(data);
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    public void Dispose()
    {
        if (_monitor is not null) _monitor.Updated -= Redraw;
        _monitor = null;
        foreach (var (icon, _) in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
