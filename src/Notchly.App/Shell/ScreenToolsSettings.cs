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
    private const string CleaningNote = "Locks the keyboard so you can clean safely: every key, the volume and media keys and the wheel do nothing. Press Escape five times, or click Unlock, to give it back. Windows keeps Ctrl+Alt+Delete for itself, so that one cannot be blocked.";
    private const string CameraNote = "Opens a floating mirror with your camera, to check how you look before a call. Esc, a click elsewhere, or another app coming forward closes it, and the camera turns off with it. Nothing is recorded.";
    private const string WallpaperNote = "Pick a still wallpaper without opening Windows Settings. Add your own pictures or folders; a picture is applied to every display or only the one with the pointer.";
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

        page.Children.Add(Section("Camera preview"));
        page.Children.Add(Caption(CameraNote));
        if (!settings.IsAvailable(AppFeature.CameraPreview)) page.Children.Add(Caption("Install Camera preview in Features to use it."));
        page.Children.Add(Toggle("Open it with a shortcut", null, DefaultsKey.CameraPreviewShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", ShortcutNote, "", new ShortcutRecorder(settings, DefaultsKey.CameraPreviewShortcut)));
        page.Children.Add(ActionButton("Open preview", () => app.Features.Get<CameraWindowService>(AppFeature.CameraPreview)?.Toggle()));

        page.Children.Add(Section("Cleaning mode"));
        page.Children.Add(Caption(CleaningNote));
        if (!settings.IsAvailable(AppFeature.CleaningMode)) page.Children.Add(Caption("Install Cleaning mode in Features to use it."));
        page.Children.Add(Toggle("Keep screen visible", "Shows a discreet indicator in the corner of the screen instead of blacking out content.", DefaultsKey.CleaningModeKeepScreenVisible, ""));
        page.Children.Add(ActionButton(CleaningModeService.StartNow, () => app.Features.Get<CleaningModeService>(AppFeature.CleaningMode)?.Start()));

        page.Children.Add(Section("Wallpaper"));
        page.Children.Add(Caption(WallpaperNote));
        if (!settings.IsAvailable(AppFeature.Wallpaper)) page.Children.Add(Caption("Install Wallpaper in Features to use it."));
        page.Children.Add(Toggle("Show on all displays", "Off: a picture goes only on the display with the pointer.", DefaultsKey.WallpaperApplyAllDisplays, ""));
        page.Children.Add(ActionButton("Open wallpaper gallery", () => app.Features.Get<WallpaperService>(AppFeature.Wallpaper)?.Open()));
        if (app.Features.Get<WallpaperService>(AppFeature.Wallpaper) is { } wallpaper)
        {
            foreach (var path in wallpaper.OwnPaths)
                page.Children.Add(Card(System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path, path, "",
                    ActionButton("Remove", () => { wallpaper.RemovePath(path); Refresh(); })));
            if (wallpaper.HiddenCount > 0)
                page.Children.Add(ActionButton($"Show {wallpaper.HiddenCount} hidden pictures again", () => { wallpaper.ShowHidden(); Refresh(); }));
        }
    }
}
