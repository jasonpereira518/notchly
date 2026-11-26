// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools, the sections of the M9a tools: the Windows counterparts of upstream's QuickToolsSettings.swift (colour picker,
// copy text from screen, cleaning mode, camera preview, scratchpad, wallpaper). Upstream's strings, with Windows' names for its controls.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Tools;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string ColorNote = "Grab the color of any pixel on screen and copy it in your favorite format. While picking, C copies and keeps going.";
    private const string TextNote = "Select an area of the screen and the recognized text is copied, ready to paste.";
    private const string ShortcutNote = "Click, then press the keys. Backspace clears it.";

    private void AddScreenToolSections(StackPanel page)
    {
        var settings = app.Settings;

        page.Children.Add(Section("Color picker"));
        page.Children.Add(Caption(ColorNote));
        if (!settings.IsAvailable(AppFeature.ColorPicker)) page.Children.Add(Caption("Install Color picker in Features to use it."));
        page.Children.Add(Toggle("Pick with a shortcut", null, DefaultsKey.ColorPickerShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", ShortcutNote, "", new ShortcutRecorder(settings, DefaultsKey.ColorPickerShortcut)));
        page.Children.Add(Choice("Copied format", null, DefaultsKey.ColorPickerFormat,
            [("hex", "HEX  #RRGGBB"), ("rgb", "RGB  rgb(r, g, b)"), ("hsl", "HSL  hsl(h, s%, l%)"), ("swiftui", "SwiftUI  Color(red: …)")]));
        page.Children.Add(Toggle("Copy without the # prefix", null, DefaultsKey.ColorPickerBareHex, ""));
        page.Children.Add(ActionButton("Pick color", () => app.Features.Get<ColorPickerService>(AppFeature.ColorPicker)?.Pick()));

        page.Children.Add(Section("Copy text from screen"));
        page.Children.Add(Caption(TextNote));
        if (!settings.IsAvailable(AppFeature.ScreenOCR)) page.Children.Add(Caption("Install Copy text from screen in Features to use it."));
        page.Children.Add(Toggle("Copy with a shortcut", null, DefaultsKey.ScreenOcrShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", ShortcutNote, "", new ShortcutRecorder(settings, DefaultsKey.ScreenOcrShortcut)));
        page.Children.Add(Toggle("Remove line breaks", "Removes line breaks so copied text pastes as one paragraph.", DefaultsKey.ScreenOcrRemoveLineBreaks, ""));
        page.Children.Add(Toggle("Read QR codes", "If the area has a QR code, its content is shown to copy or open.", DefaultsKey.ScreenOcrDetectQrCodes, ""));
        page.Children.Add(ActionButton("Copy text from screen", () => app.Features.Get<ScreenTextService>(AppFeature.ScreenOCR)?.Capture()));
    }
}
