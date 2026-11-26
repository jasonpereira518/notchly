// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ColorSamplerService.swift: the shortcut opens a frozen screen with a
// magnifier; a click copies the colour under the pointer in the chosen format and leaves, and C copies it and keeps picking. The value
// goes on the clipboard as ordinary text, so the clipboard history keeps every pick. Upstream's fallback to the system sampler has no
// counterpart here: Windows asks for no permission to read the screen.
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class ColorPickerService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher) : IFeatureService
{
    public const string Purpose = "Pick a color";
    public const string Hint = "Click to copy the color. Press C to copy and keep picking, the arrow keys to move one pixel, Esc to finish.";

    private IDisposable? _registration;
    private string? _registered;
    private CaptureOverlay? _overlay;

    public bool ShortcutUnavailable { get; private set; }

    public void Sync()
    {
        var wanted = settings.GetBool(DefaultsKey.ColorPickerShortcutEnabled) ? settings.GetString(DefaultsKey.ColorPickerShortcut) : "";
        if (wanted == _registered) return;
        _registration?.Dispose();
        _registration = null;
        _registered = wanted;
        ShortcutUnavailable = false;
        if (wanted.Length == 0 || Shortcut.Parse(wanted) is not { } shortcut) return;
        _registration = hotkeys.Register(shortcut, () => dispatcher.BeginInvoke(Pick));
        ShortcutUnavailable = _registration is null;
    }

    public ColorCopyFormat Format => ColorCopyFormats.Sanitized(settings.GetString(DefaultsKey.ColorPickerFormat));
    private bool BareHex => settings.GetBool(DefaultsKey.ColorPickerBareHex);

    /// <summary>The value written for a pixel in the chosen copy format.</summary>
    public string TextOf(PixelSample sample) =>
        ColorValue.Format(sample.Red / 255.0, sample.Green / 255.0, sample.Blue / 255.0, null, Format, BareHex);

    /// <summary>Opens the picker. A second call while it is open closes it.</summary>
    public void Pick()
    {
        if (_overlay is not null) { _overlay.Cancel(); return; }
        var overlay = new CaptureOverlay(CaptureMode.Pixel, Purpose, Hint) { Describe = TextOf };
        overlay.PixelPicked += (sample, final) => Copy(overlay, sample, final);
        overlay.Closed += () => _overlay = null;
        _overlay = overlay;
        try
        {
            if (!overlay.Open()) { _overlay = null; QuickToolHud.Show("The screen could not be read"); }
        }
        catch (Exception e)
        {
            _overlay = null;
            Log.Warning(e, "Color picker could not open");
        }
    }

    private void Copy(CaptureOverlay overlay, PixelSample sample, bool final)
    {
        var text = TextOf(sample);
        if (!ClipboardWrite.TrySetText(text))
        {
            QuickToolHud.Show("The color could not be copied");
            return;
        }
        if (final) QuickToolHud.Show(text, Color.FromRgb(sample.Red, sample.Green, sample.Blue));
        else overlay.ShowCopied(text);
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _overlay?.Cancel();
    }
}
