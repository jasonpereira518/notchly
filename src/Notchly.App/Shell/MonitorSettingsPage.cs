// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → System monitor: the Windows counterpart of upstream's UI/Settings/MonitorSettings.swift and
// MonitorAlertsControls.swift: how often it reads and in which units, which blocks the panel shows, the alerts with
// their thresholds, and the tray readouts. Temperatures and fans are not here until the elevated helper (M12); upstream's
// reordering of the panel's blocks is not ported.
using System.Windows;
using System.Windows.Controls;
using Notchly.Core.Features;
using Notchly.Core.Monitor;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string MonitorNote = "Readings are taken only while something shows them: the System page on the island, the panel, a tray readout or an alert. " +
        "Windows reports no temperatures, fan speeds, or system and adapter power to apps, so those wait for the elevated helper.";
    private const string TrayNote = "Each choice below adds its own icon to the notification area. Windows keeps new icons in the overflow (the ^ arrow) until you pin " +
        "them: Settings → Personalization → Taskbar → Other system tray icons.";

    private FrameworkElement MonitorPage()
    {
        var page = Page(PageTitle("System monitor"));
        page.Children.Add(Caption(MonitorNote));
        if (!app.Settings.IsAvailable(AppFeature.MonitorCPU) && !app.Settings.IsAvailable(AppFeature.MonitorMemory) && !app.Settings.IsAvailable(AppFeature.MonitorNetwork))
            page.Children.Add(Caption("Install the monitor features in Features to use them."));

        page.Children.Add(Section("Readings"));
        page.Children.Add(Card("Update every", "How often the readings refresh while they are shown.", "",
            AgentSettings.NumberChoice(app.Settings, DefaultsKey.MonitorInterval, [1, 2, 5], 2, v => v == 1 ? "1 second" : $"{v:0} seconds")));
        page.Children.Add(Choice("Network speeds", "Bytes per second, or bits per second as links are quoted.", DefaultsKey.NetworkSpeedUnit, [("bytes", "MB/s"), ("bits", "Mbps")], ""));
        page.Children.Add(Choice("Memory shown", "Everything in use, or only what apps hold.", DefaultsKey.MonitorMemoryMetric, [("used", "In use"), ("app", "Apps")], ""));
        foreach (var (title, key) in new[] { ("Processor graph", DefaultsKey.MonitorGraphCPU), ("Graphics graph", DefaultsKey.MonitorGraphGPU), ("Memory graph", DefaultsKey.MonitorGraphMemory),
                                              ("Network graph", DefaultsKey.MonitorGraphNetwork), ("Disk graph", DefaultsKey.MonitorGraphDisk), ("Battery graph", DefaultsKey.MonitorGraphBattery) })
            page.Children.Add(Toggle(title, null, key, ""));

        page.Children.Add(Section("In the panel"));
        foreach (var (title, key) in new[]
                 {
                     ("System", DefaultsKey.MonitorShowSystem), ("Processor", DefaultsKey.MonitorSysCPU), ("Each core", DefaultsKey.MonitorSysCPUCores), ("Graphics", DefaultsKey.MonitorSysGPU),
                     ("Memory", DefaultsKey.MonitorSysMemory), ("Uptime", DefaultsKey.MonitorSysUptime), ("Connected devices", DefaultsKey.MonitorSysConnectedDevices),
                     ("Network", DefaultsKey.MonitorShowNetwork), ("Speed", DefaultsKey.MonitorNetSpeed), ("Totals", DefaultsKey.MonitorNetTotals), ("Addresses", DefaultsKey.MonitorNetAddresses),
                     ("Speed test", DefaultsKey.MonitorNetTest), ("Disk", DefaultsKey.MonitorShowDisk), ("Space", DefaultsKey.MonitorDiskUsage), ("Activity", DefaultsKey.MonitorDiskActivity),
                     ("Power", DefaultsKey.MonitorShowPower), ("Battery", DefaultsKey.MonitorPwrBattery), ("Time remaining", DefaultsKey.MonitorPwrTimeRemaining),
                     ("Battery health", DefaultsKey.MonitorPwrHealth), ("Accessory batteries", DefaultsKey.MonitorPwrPeripherals),
                 })
            page.Children.Add(Toggle(title, null, key, ""));

        page.Children.Add(Section("Alerts"));
        page.Children.Add(Caption("Notifications for sustained load and low resources. A kind says nothing again until its cooldown is over."));
        page.Children.Add(Toggle("High processor use", "When the processor stays above the limit for a while.", DefaultsKey.MonitorAlertCPU, ""));
        page.Children.Add(Card("Processor limit", null, "", AgentSettings.NumberChoice(app.Settings, DefaultsKey.MonitorAlertCPUThreshold, Steps(50, 100, 5), 90, v => $"{v:0}%")));
        page.Children.Add(Toggle("Memory running out", "When Windows reports critical memory pressure.", DefaultsKey.MonitorAlertMemory, ""));
        page.Children.Add(Toggle("Disk almost full", "For drives of 10 GB or more.", DefaultsKey.MonitorAlertDisk, ""));
        page.Children.Add(Card("Free space limit", null, "", AgentSettings.NumberChoice(app.Settings, DefaultsKey.MonitorAlertDiskFreePercent, Steps(5, 30, 5), 10, v => $"{v:0}% free")));
        page.Children.Add(Toggle("Low battery", "While the battery is not charging.", DefaultsKey.MonitorAlertBattery, ""));
        page.Children.Add(Card("Battery limit", null, "", AgentSettings.NumberChoice(app.Settings, DefaultsKey.MonitorAlertBatteryPercent, Steps(5, 50, 5), 15, v => $"{v:0}%")));
        page.Children.Add(Card("Cooldown", "The least time between two alerts of one kind.", "",
            AgentSettings.NumberChoice(app.Settings, DefaultsKey.MonitorAlertCooldownMinutes, AlertRules.Cooldowns.Select(c => (double)c).ToList(), 15, v => $"{v:0} minutes")));

        page.Children.Add(Section("Tray readouts"));
        page.Children.Add(Caption(TrayNote));
        foreach (var (title, key) in new[]
                 {
                     ("Processor", DefaultsKey.MenuBarCPU), ("Graphics", DefaultsKey.MenuBarGPU), ("Memory", DefaultsKey.MenuBarMemory), ("Network", DefaultsKey.MenuBarNetwork),
                     ("Disk space", DefaultsKey.MenuBarDiskUsage), ("Disk activity", DefaultsKey.MenuBarDiskActivity), ("Battery", DefaultsKey.MenuBarBattery),
                     ("Battery time left", DefaultsKey.MenuBarBatteryTime), ("Accessory battery", DefaultsKey.MenuBarPeripheralBattery), ("Connected devices", DefaultsKey.MenuBarConnectedDevices),
                 })
            page.Children.Add(Toggle(title, null, key, ""));
        page.Children.Add(Choice("Appearance", null, DefaultsKey.MenuBarMetricAppearance, [("values", "Values"), ("bars", "Bars")], ""));
        page.Children.Add(Choice("Disk shows", null, DefaultsKey.MenuBarDiskStyle, [("percent", "Percent used"), ("free", "Free space"), ("used", "Used space")], ""));
        page.Children.Add(Toggle("Upload above download", null, DefaultsKey.MenuBarNetworkUploadFirst, ""));
        page.Children.Add(Card("Elevated from", "Bars and values turn amber at this share.", "",
            AgentSettings.NumberChoice(app.Settings, DefaultsKey.MenuBarUsageBarMediumThreshold, Steps(40, 90, 5), 70, v => $"{v:0}%")));
        page.Children.Add(Card("Critical from", "And red at this one.", "",
            AgentSettings.NumberChoice(app.Settings, DefaultsKey.MenuBarUsageBarHighThreshold, Steps(50, 100, 5), 90, v => $"{v:0}%")));
        return page;
    }

    private static IReadOnlyList<double> Steps(int from, int to, int step) => Enumerable.Range(0, (to - from) / step + 1).Select(i => (double)(from + i * step)).ToList();
}
