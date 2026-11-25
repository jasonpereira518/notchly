// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Audio/SoundOutputSwitcher.swift: a global shortcut that steps through
// the outputs the user ticked. Switching the system output clears per-app routes, as choosing one in the mixer does.
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Sound;
using Serilog;

namespace Notchly.App.Sound;

public sealed class OutputSwitcherService(SettingsStore settings, HotkeyService hotkeys) : IFeatureService
{
    private IDisposable? _registration;
    private string? _registeredStorage;

    /// <summary>The shortcut could not be registered because another app owns it.</summary>
    public bool ShortcutUnavailable { get; private set; }
    /// <summary>Raised after a switch with the new output's name, or null when nothing could be selected.</summary>
    public event Action<string?>? Switched;

    public void Sync()
    {
        var enabled = settings.GetBool(DefaultsKey.SoundOutputSwitcherEnabled);
        if (enabled && Selected().Count == 0)
        {
            // Turning it on starts the cycle with the output in use.
            if (AudioDevices.DefaultId(false) is { } current) settings.Set(DefaultsKey.SoundOutputSwitcherDeviceUIDs, new List<string> { current });
        }
        var storage = enabled ? settings.GetString(DefaultsKey.SoundOutputSwitcherShortcut) : null;
        if (storage == _registeredStorage && (storage is null || _registration is not null || ShortcutUnavailable)) return;
        _registration?.Dispose();
        _registration = null;
        ShortcutUnavailable = false;
        _registeredStorage = storage;
        if (storage is null || Shortcut.Parse(storage) is not { } shortcut) return;
        _registration = hotkeys.Register(shortcut, () => SwitchToNext());
        ShortcutUnavailable = _registration is null;
    }

    public IReadOnlyList<string> Selected() => MixerSupport.SanitizedIdList(settings.Get(DefaultsKey.SoundOutputSwitcherDeviceUIDs, new List<string>()));

    public void SetSelected(IEnumerable<string> ids) => settings.Set(DefaultsKey.SoundOutputSwitcherDeviceUIDs, MixerSupport.SanitizedIdList(ids).ToList());

    /// <summary>Steps to the next ticked output. True when it switched, or when the only ticked output is already playing.</summary>
    public bool SwitchToNext()
    {
        var outputs = AudioDevices.List(false);
        var available = outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var current = outputs.FirstOrDefault(o => o.IsDefault)?.Id;
        var selected = Selected();
        var next = MixerSupport.NextSelectedOutputId(current, selected, available);
        if (next is null)
        {
            var already = current is not null && selected.Contains(current);
            Switched?.Invoke(already ? outputs.First(o => o.IsDefault).Name : null);
            return already;
        }
        var ok = MixerService.UseSystemOutput(settings, next);
        Log.Information("Output switcher: {Ok}", ok);
        Switched?.Invoke(ok ? outputs.First(o => o.Id == next).Name : null);
        return ok;
    }

    public void Dispose() => _registration?.Dispose();
}
