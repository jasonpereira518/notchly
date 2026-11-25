// SPDX-License-Identifier: GPL-3.0-or-later
// The scroll, mouse button and debounce parts of Settings → Mouse: the Windows counterpart of upstream's MouseSettings.swift,
// MouseButtonSettings.swift and the click-debounce card. Each feature is off until you turn it on; only mouse wheels are changed,
// never a touchpad.
using System.Windows;
using System.Windows.Controls;
using Notchly.Core.Features;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string ScrollNote = "These change what a mouse wheel does, and leave a touchpad alone. A tick that has to change is replaced by Notchly's own, so " +
        "an app running as administrator is out of reach unless Notchly runs as administrator too, and everything steps aside in full-screen games.";

    private void AddMouseInputSections(StackPanel page)
    {
        var settings = app.Settings;
        string[] modifiers = ["shift", "alt", "control", "win"];
        (string, string)[] modifierChoices = [("shift", "Shift"), ("alt", "Alt"), ("control", "Ctrl"), ("win", "Win")];

        page.Children.Add(Section("Scrolling"));
        page.Children.Add(Caption(ScrollNote));
        if (!settings.IsAvailable(AppFeature.ScrollInverter)) page.Children.Add(Caption("Install the scroll features in Features to use them."));
        page.Children.Add(Toggle("Reverse the wheel", "Scrolling down moves up, as on a touchpad with natural scrolling.", DefaultsKey.ScrollInverterEnabled, ""));
        page.Children.Add(Toggle("Reverse sideways scrolling too", null, DefaultsKey.ScrollInverterHorizontalEnabled, ""));
        page.Children.Add(Card("Apps that keep the wheel as it is", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.ScrollInverterExceptions, "code.exe")));
        page.Children.Add(Toggle("Scroll sideways with a key", "Hold the key and use the wheel to scroll left and right.", DefaultsKey.ScrollHorizontalEnabled, ""));
        page.Children.Add(Choice("Key", null, DefaultsKey.ScrollHorizontalModifier, modifierChoices, ""));
        page.Children.Add(Toggle("Zoom with a key and the wheel", "Alt or Win plus the wheel zooms, as Ctrl plus the wheel does.", DefaultsKey.PinchZoomEnabled, ""));
        page.Children.Add(Choice("Key", null, DefaultsKey.PinchZoomModifier, [("alt", "Alt"), ("win", "Win"), ("control", "Ctrl"), ("none", "None")], ""));
        page.Children.Add(Toggle("One notch at a time", "However fast the wheel spins, a tick is one notch of the length you choose.", DefaultsKey.LinearScrollEnabled, ""));
        page.Children.Add(Card("Lines per notch", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.LinearScrollLines, Enumerable.Range(1, 10).Select(v => (double)v).ToList(), 3, v => $"{v:0}")));
        page.Children.Add(Card("Apps that keep their own", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.LinearScrollExceptions, "game.exe")));
        page.Children.Add(Toggle("Scroll faster with a key", "Hold the key to multiply the wheel.", DefaultsKey.ScrollSpeedEnabled, ""));
        page.Children.Add(Choice("Key", null, DefaultsKey.ScrollSpeedModifier, modifierChoices, ""));
        page.Children.Add(Card("Times faster", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.ScrollSpeedMultiplier, Enumerable.Range(2, 14).Select(v => (double)v).ToList(), 3, v => $"{v:0}\u00D7")));
        page.Children.Add(Card("Apps that keep their own", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.ScrollSpeedExceptions, "game.exe")));
        page.Children.Add(Toggle("Smooth scrolling", "Each notch glides instead of jumping. Apps that read only whole notches take the sum.", DefaultsKey.SmoothScrollEnabled, ""));
        page.Children.Add(Card("Distance", "How far a notch glides.", "", AgentSettings.NumberChoice(settings, DefaultsKey.SmoothScrollStep, [20, 30, 40, 50, 60, 70, 80, 90, 100], 40, v => $"{v:0}")));
        page.Children.Add(Card("Response", "Lower is softer, higher is quicker.", "", AgentSettings.NumberChoice(settings, DefaultsKey.SmoothScrollResponse, [0, 25, 50, 65, 75, 100], 65, v => $"{v:0}")));
        page.Children.Add(Card("Coast", "How long the glide trails off.", "", AgentSettings.NumberChoice(settings, DefaultsKey.SmoothScrollCoast, [0, 25, 50, 75, 100], 0, v => $"{v:0}")));
        page.Children.Add(Card("Apps that scroll on their own", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.SmoothScrollExceptions, "game.exe")));

        page.Children.Add(Section("Mouse buttons"));
        page.Children.Add(Caption("Send a keyboard shortcut from a side button or a tilt of the wheel. A button you map no longer does its own job in the apps that are not on the list below."));
        if (!settings.IsAvailable(AppFeature.MouseButtonShortcuts)) page.Children.Add(Caption("Install Mouse button shortcuts in Features to use them."));
        page.Children.Add(Toggle("Use button shortcuts", null, DefaultsKey.MouseButtonShortcutsEnabled, ""));
        foreach (var button in new[] { 3, 4, ButtonShortcutSupport.SideWheelLeft, ButtonShortcutSupport.SideWheelRight })
        {
            var captured = button;
            string Read() => ButtonShortcutSupport.Parse(settings.GetString(DefaultsKey.MouseButtonShortcuts)).TryGetValue(captured, out var s) ? s : "none";
            void Write(string value)
            {
                var map = ButtonShortcutSupport.Parse(settings.GetString(DefaultsKey.MouseButtonShortcuts)).ToDictionary(p => p.Key, p => p.Value);
                if (value == Notchly.Core.Shortcuts.Shortcut.NoneStorage) map.Remove(captured); else map[captured] = value;
                settings.Set(DefaultsKey.MouseButtonShortcuts, ButtonShortcutSupport.Format(map));
            }
            page.Children.Add(Card(ButtonShortcutSupport.Name(button), "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(Read, Write)));
        }
        page.Children.Add(Card("Apps that keep their buttons", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.MouseButtonExceptions, "game.exe")));
        page.Children.Add(Toggle("Switch desktops by dragging", "Hold a button and drag sideways to change virtual desktop, or up for Task View.", DefaultsKey.MouseSpacesGestureEnabled, ""));
        page.Children.Add(Card("Button", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.MouseSpacesGestureButton, [0, 3, 4], 0, v => v == 0 ? "None" : v == 3 ? "Back" : "Forward")));

        page.Children.Add(Section("Bouncing buttons and keys"));
        page.Children.Add(Caption("A worn mouse button or key can send two presses for one. These drop the second one if it comes too soon."));
        page.Children.Add(Toggle("Ignore double clicks from a worn button", null, DefaultsKey.MouseClickDebounceEnabled, ""));
        page.Children.Add(Card("A second click within", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.MouseClickDebounceWindowMs, [5, 10, 15, 25, 40, 60, 100], 25, v => $"{v:0} ms")));
        page.Children.Add(Toggle("Ignore doubled key presses", "Auto-repeat is never touched.", DefaultsKey.KeyboardDebounceEnabled, ""));
        page.Children.Add(Card("A second press within", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.KeyboardDebounceWindowMs, [0, 5, 10, 20, 30, 50, 100, 200], 5, v => $"{v:0} ms")));
        _ = modifiers;
    }
}
