// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Energy: the Windows counterpart of upstream's UI/Settings/EnergySettings.swift: the keep-awake card and
// its options, the displays card and Bluetooth on sleep. Upstream's strings, with Windows' names for its controls.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Energy;
using Notchly.Core.Energy;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string AwakeNote = "A session keeps Windows from sleeping on idle and, unless you allow it, keeps the display on. It can end on a timer, on a low battery or by rule, " +
        "and a timed session carries on after a restart.";
    private const string LidNote = "While a session runs the lid does nothing. Notchly puts Windows' own lid setting back when the session ends, and again at the next start if it was stopped before that.";
    private const string DisplaysNote = "Brightness for the built-in panel, for external monitors that accept DDC/CI, and a software dim for displays that do not. " +
        "The bottom quarter of a slider can dim the picture below the panel's own minimum.";
    private const string BluetoothNote = "Bluetooth goes off as the PC sleeps and comes back at wake, only if it was on. A restore still owed is paid at the next start.";

    private FrameworkElement EnergyPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Energy"));

        // Keep awake
        page.Children.Add(Section("Keep awake"));
        page.Children.Add(Caption(AwakeNote));
        if (!settings.IsAvailable(AppFeature.KeepAwake)) page.Children.Add(Caption("Install Keep awake in Features to use it."));
        page.Children.Add(Toggle("Show it on the island", "A small countdown beside the camera while a session runs.", DefaultsKey.NotchKeepAwakeActivity, ""));
        page.Children.Add(Toggle("Start when Notchly starts", null, DefaultsKey.KeepAwakeAutoStart, ""));
        page.Children.Add(Card("Default length", "What a plain on switch uses.", "",
            AgentSettings.NumberChoice(settings, DefaultsKey.DefaultDurationMinutes, KeepAwakeSupport.AllowedDurations.Select(v => (double)v).ToList(), 0,
                v => v == 0 ? "Until I turn it off" : v < 60 ? $"{v:0} minutes" : $"{v / 60:0} hours")));
        if (Platform.Power.PowerService.ReadBattery().HasBattery)
            page.Children.Add(Card("Stop on low battery", "A session ends, and will not start, on battery at or under this level.", "",
                AgentSettings.NumberChoice(settings, DefaultsKey.BatteryLimitPercent, KeepAwakeSupport.AllowedBatteryLimits.Select(v => (double)v).ToList(), 10,
                    v => v == 0 ? "Never" : $"{v:0}%")));
        page.Children.Add(Toggle("Let the display sleep", "Only the PC stays awake.", DefaultsKey.KeepAwakeAllowDisplaySleep, ""));
        page.Children.Add(Toggle("Pause while the PC is locked", null, DefaultsKey.KeepAwakePauseWhenLocked, ""));
        page.Children.Add(Toggle("Keep the pointer moving", "Nudges it by a pixel now and then, for apps that watch for activity.", DefaultsKey.KeepAwakeMouseJiggleEnabled, ""));
        page.Children.Add(Card("Every", null, "",
            AgentSettings.NumberChoice(settings, DefaultsKey.KeepAwakeMouseJiggleIntervalMinutes, KeepAwakeSupport.AllowedJiggleIntervals.Select(v => (double)v).ToList(), 5,
                v => v == 1 ? "1 minute" : $"{v:0} minutes")));
        page.Children.Add(Toggle("Toggle with a shortcut", null, DefaultsKey.HotkeyEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.KeepAwakeShortcut)));

        page.Children.Add(Section("Closed lid"));
        page.Children.Add(Caption(LidNote));
        page.Children.Add(Toggle("Keep running with the lid closed", null, DefaultsKey.ClamshellPreferred, ""));
        page.Children.Add(Toggle("Dim the screen when the lid closes", "The built-in panel goes dark while the lid is closed, and comes back when it opens.", DefaultsKey.DimScreenOnLidClose, ""));

        page.Children.Add(Section("Start by itself"));
        page.Children.Add(Caption("A session starts while these hold and ends when they stop. A session you start yourself is never ended by them."));
        page.Children.Add(Toggle("While an external display is connected", null, DefaultsKey.KeepAwakeExternalDisplay, ""));
        page.Children.Add(Toggle("While the PC is plugged in", null, DefaultsKey.KeepAwakeConnectedToPower, ""));
        page.Children.Add(Toggle("While certain apps run", null, DefaultsKey.KeepAwakeRunningApps, ""));
        page.Children.Add(RunningAppsCard());
        page.Children.Add(Toggle("Need all of them", "Off: any one of the rules is enough.", DefaultsKey.KeepAwakeAutomationRequireAll, ""));

        // Displays
        page.Children.Add(Section("Displays"));
        page.Children.Add(Caption(DisplaysNote));
        if (!settings.IsAvailable(AppFeature.Brightness)) page.Children.Add(Caption("Install Displays in Features to use it."));
        page.Children.Add(Toggle("Control display brightness", "Sliders in the panel and on the island, and the shortcuts below.", DefaultsKey.BrightnessControlEnabled, ""));
        page.Children.Add(Choice("Shortcut step", "How far one press moves the level.", DefaultsKey.BrightnessKeyStep,
            [("standard", "Standard"), ("half", "Half"), ("quarter", "Quarter")], ""));
        page.Children.Add(Toggle("Change brightness with shortcuts", "Acts on the display under the pointer.", DefaultsKey.DisplayBrightnessShortcutsEnabled, ""));
        page.Children.Add(Card("Darker", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.DisplayBrightnessDecreaseShortcut)));
        page.Children.Add(Card("Brighter", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.DisplayBrightnessIncreaseShortcut)));
        var brightness = app.Features.Get<BrightnessService>(AppFeature.Brightness);
        if (brightness is not null)
            foreach (var display in brightness.Displays) page.Children.Add(DisplayCard(brightness, display));

        // Bluetooth
        page.Children.Add(Section("Bluetooth on sleep"));
        page.Children.Add(Caption(BluetoothNote));
        if (!settings.IsAvailable(AppFeature.BluetoothSleep)) page.Children.Add(Caption("Install Bluetooth on sleep in Features to use it."));
        var bluetooth = app.Features.Get<BluetoothSleepService>(AppFeature.BluetoothSleep);
        if (bluetooth is { Supported: false }) page.Children.Add(Caption("This PC has no Bluetooth radio that Windows lets Notchly control."));
        page.Children.Add(Toggle("Turn Bluetooth off while the PC sleeps", null, DefaultsKey.BluetoothSleepEnabled, ""));
        page.Children.Add(Toggle("Turn it back on at wake", null, DefaultsKey.BluetoothSleepRestoreOnWake, ""));
        return page;
    }

    private CardControl RunningAppsCard()
    {
        var box = new TextBox { PlaceholderText = "code.exe, steam.exe", MinWidth = 260, Text = string.Join(", ", app.Settings.GetList(DefaultsKey.KeepAwakeRunningAppBundleIDs)) };
        box.LostFocus += (_, _) =>
        {
            var names = box.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe").Distinct(StringComparer.OrdinalIgnoreCase);
            app.Settings.SetList(DefaultsKey.KeepAwakeRunningAppBundleIDs, names);
        };
        return Card("Apps", "Program names, separated by commas.", "", box);
    }

    private CardControl DisplayCard(BrightnessService service, BrightnessDisplay display)
    {
        var how = display.Kind switch
        {
            DisplayRouteKind.Internal => "Built-in panel",
            DisplayRouteKind.Ddc => "Monitor brightness (DDC/CI)",
            _ => "Software dim",
        };
        var stack = new StackPanel();
        var extended = new CheckBox { Content = "Dim below the minimum", IsChecked = display.ExtendedDimming, Margin = new Thickness(0, 0, 0, 4) };
        extended.Click += (_, _) => service.SetExtendedDimming(display.Id, extended.IsChecked == true);
        stack.Children.Add(extended);
        if (display.CanChooseSoftware)
        {
            var software = new CheckBox { Content = "Use software dimming", IsChecked = display.Kind == DisplayRouteKind.Software };
            software.Click += (_, _) => service.ForceSoftware(display.Id, software.IsChecked == true);
            stack.Children.Add(software);
        }
        return Card(display.Name, how, "", stack);
    }
}
