// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/SuperKey/SuperKeyService.swift. Caps Lock (or a right-hand modifier) becomes a key that
// stands for Ctrl, Alt, Shift and Win while it is held, and does one small thing when it is only tapped. A keyboard hook swallows
// the trigger (upstream remaps it with hidutil); the next key or click is swallowed too and sent again with the modifiers pressed in
// front of it, since a hook cannot add modifiers to an event in flight. The modifiers are only pressed when something else is
// pressed, so a held trigger alone never reaches Windows' Microsoft 365 shortcut, and a dummy key goes in before Win is
// released so the Start menu does not open.
using Microsoft.Win32;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.KeyboardInput;

public sealed class SuperKeyService(App app) : IFeatureService
{
    private const uint WmLDown = 0x0201, WmLUp = 0x0202, WmRDown = 0x0204, WmRUp = 0x0205, WmMDown = 0x0207, WmMUp = 0x0208;
    private const double ForgetAfterMs = 30000;

    private IDisposable? _key, _mouse;
    private readonly SuperKeySupport.State _state = new();
    private readonly HashSet<int> _replacedKeys = [];
    private readonly HashSet<int> _replacedButtons = [];
    private readonly List<int> _pressed = [];
    private volatile int _trigger = 0x14;
    private volatile IReadOnlyList<int> _modifiers = [0x11, 0x12, 0x10, 0x5B];
    private volatile IReadOnlyList<string> _exceptions = [];
    private SoloAction _solo;
    private bool _triggerSwallowed;

    public void Sync()
    {
        var s = app.Settings;
        _trigger = SuperKeySupport.VirtualKey(s.GetString(DefaultsKey.SuperKeySource));
        _modifiers = SuperKeySupport.Modifiers(s.GetString(DefaultsKey.SuperKeyModifiers)).Select(m => m switch { "control" => 0x11, "alt" => 0x12, "shift" => 0x10, _ => 0x5B }).ToList();
        _solo = SuperKeySupport.ParseSolo(s.GetString(DefaultsKey.SuperKeySoloAction));
        _exceptions = s.GetList(DefaultsKey.SuperKeyExceptions);
        if (s.GetBool(DefaultsKey.SuperKeyOfficeMask)) OfficeKeyMask.Apply(true); else OfficeKeyMask.Apply(false);
        var on = s.GetBool(DefaultsKey.SuperKeyEnabled);
        if (on && _key is null)
        {
            _key = app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Transform);
            _mouse = app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Transform);
        }
        else if (!on && _key is not null) Stop();
    }

    private void Stop()
    {
        ReleaseAll();
        _key?.Dispose();
        _mouse?.Dispose();
        _key = _mouse = null;
    }

    private bool Paused() => app.InputGate.ShouldPassThrough || _exceptions.Count > 0 && ScrollSupport.IsExcepted(WindowFrame.ProgramOf(WindowInventory.Foreground()), _exceptions);

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn) return false;
        var vk = e.VirtualKey;
        if (vk == _trigger)
        {
            if (e.IsDown)
            {
                if (_state.Held) return _triggerSwallowed;
                if (Paused()) { _triggerSwallowed = false; return false; }
                _state.TriggerDown(e.Time);
                _triggerSwallowed = true;
                return true;
            }
            if (!_state.Held || !_triggerSwallowed) return false;
            var outcome = _state.TriggerUp(e.Time);
            ReleaseAll();
            _triggerSwallowed = false;
            RunSolo(outcome);
            return true;
        }
        if (!_state.Held) return false;
        // A real modifier changing while the trigger is held ends its solo action; modifiers themselves pass through.
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) { _state.ModifierChanged(); return false; }
        if (_state.HeldFor(e.Time) > ForgetAfterMs) { _state.Release(); ReleaseAll(); _triggerSwallowed = false; return false; }
        if (e.IsDown)
        {
            _state.OtherInput();
            PressModifiers();
            _replacedKeys.Add(vk);
            app.Injector.Key(vk, true, InputSource.SuperKey, e.IsExtended);
            return true;
        }
        if (_replacedKeys.Remove(vk))
        {
            app.Injector.Key(vk, false, InputSource.SuperKey, e.IsExtended);
            return true;
        }
        return false;
    }

    private bool OnMouse(in MouseEvent e)
    {
        if (e.IsOwn || !_state.Held) return false;
        var (button, down) = e.Message switch
        {
            WmLDown => (0, true), WmLUp => (0, false), WmRDown => (1, true), WmRUp => (1, false), WmMDown => (2, true), WmMUp => (2, false), _ => (-1, false),
        };
        if (button < 0) return false;
        if (down)
        {
            _state.OtherInput();
            PressModifiers();
            _replacedButtons.Add(button);
            app.Injector.Button(button, true, InputSource.SuperKey);
            return true;
        }
        if (!_replacedButtons.Remove(button)) return false;
        app.Injector.Button(button, false, InputSource.SuperKey);
        return true;
    }

    private void PressModifiers()
    {
        foreach (var vk in _modifiers)
        {
            if (KeyState.IsDown(vk)) continue;
            app.Injector.Key(vk, true, InputSource.SuperKey);
            _pressed.Add(vk);
        }
    }

    /// <summary>The modifiers it pressed are let go, with a dummy key before Win so the Start menu stays shut; keys still down are released.</summary>
    private void ReleaseAll()
    {
        foreach (var vk in _replacedKeys) app.Injector.Key(vk, false, InputSource.SuperKey);
        foreach (var button in _replacedButtons) app.Injector.Button(button, false, InputSource.SuperKey);
        _replacedKeys.Clear();
        _replacedButtons.Clear();
        if (_pressed.Contains(0x5B) || _pressed.Contains(0x12))
        {
            app.Injector.Key(0xE8, true, InputSource.SuperKey);
            app.Injector.Key(0xE8, false, InputSource.SuperKey);
        }
        for (var i = _pressed.Count - 1; i >= 0; i--) app.Injector.Key(_pressed[i], false, InputSource.SuperKey);
        _pressed.Clear();
    }

    private void RunSolo(SoloOutcome outcome)
    {
        if (outcome == SoloOutcome.Nothing) return;
        switch (SuperKeySupport.Effect(_solo, outcome == SoloOutcome.Hold, repeated: false))
        {
            case SoloEffect.PressEscape:
                app.Injector.Key(0x1B, true, InputSource.SuperKey);
                app.Injector.Key(0x1B, false, InputSource.SuperKey);
                break;
            case SoloEffect.ToggleCapsLock:
                app.Injector.Key(0x14, true, InputSource.SuperKey);
                app.Injector.Key(0x14, false, InputSource.SuperKey);
                break;
            case SoloEffect.NextInputSource:
                // Win+Space switches to the next input language, as Windows does.
                app.Injector.Key(0x5B, true, InputSource.SuperKey);
                app.Injector.Key(0x20, true, InputSource.SuperKey);
                app.Injector.Key(0x20, false, InputSource.SuperKey);
                app.Injector.Key(0x5B, false, InputSource.SuperKey);
                break;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Windows reserves Ctrl+Alt+Shift+Win (and its letter combinations) for Microsoft 365. Pointing that app link at a program that does
/// nothing frees the combinations. It is a per-user setting that is removed again when this is turned off; one that already exists is left alone.
/// </summary>
public static class OfficeKeyMask
{
    private const string Key = @"Software\Classes\ms-officeapp";

    public static bool IsApplied
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key + @"\Shell\Open\Command");
            return key?.GetValue("") is string value && value.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Apply(bool on)
    {
        try
        {
            if (on)
            {
                if (IsApplied || Registry.CurrentUser.OpenSubKey(Key) is not null) return;
                using var root = Registry.CurrentUser.CreateSubKey(Key);
                root.SetValue("", "URL:ms-officeapp");
                root.SetValue("URL Protocol", "");
                using var command = root.CreateSubKey(@"Shell\Open\Command");
                command.SetValue("", "rundll32");
            }
            else if (IsApplied) Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
        }
        catch (Exception e) { Log.Warning(e, "The Microsoft 365 shortcut could not be changed"); }
    }
}
