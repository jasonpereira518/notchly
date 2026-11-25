// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream's MouseClickDebounceService and KeyboardDebounceService: a worn mouse button or key can send two presses for
// one. These sit first in the hook order, so a bounce is dropped before anything else sees it. The rules (strict comparisons,
// windows, per-key overrides) are in Core; here they are fed from the hook. Mouse movement is never held back, only the buttons.
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;

namespace Notchly.App.MouseInput;

public sealed class ClickDebounceService(App app) : IFeatureService
{
    private IDisposable? _subscription;
    private readonly ClickDebounceSupport.Filter _filter = new(ClickDebounceSupport.DefaultWindowMs);

    public void Sync()
    {
        var settings = app.Settings;
        _filter.WindowMs = ClickDebounceSupport.SanitizedWindow(settings.GetInt(DefaultsKey.MouseClickDebounceWindowMs, ClickDebounceSupport.DefaultWindowMs));
        var on = settings.GetBool(DefaultsKey.MouseClickDebounceEnabled);
        if (on && _subscription is null) _subscription = app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Filter);
        else if (!on && _subscription is not null) { _subscription.Dispose(); _subscription = null; _filter.Reset(); }
    }

    private bool OnMouse(in MouseEvent e)
    {
        if (e.IsOwn) return false;
        // Left, right and middle, down and up: 0x201 and 0x202, 0x204 and 0x205, 0x207 and 0x208.
        var (button, down) = e.Message switch
        {
            0x0201 => (0, true), 0x0202 => (0, false),
            0x0204 => (1, true), 0x0205 => (1, false),
            0x0207 => (2, true), 0x0208 => (2, false),
            _ => (-1, false),
        };
        if (button < 0) return false;
        var verdict = down ? _filter.Down(button, e.Time) : _filter.Up(button, e.Time);
        return verdict == DebounceVerdict.Suppress;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}

public sealed class KeyDebounceService(App app) : IFeatureService
{
    private IDisposable? _subscription;
    private readonly KeyDebounceSupport.Filter _filter = new(KeyDebounceSupport.DefaultWindowMs);
    private readonly HashSet<int> _pressed = [];

    public void Sync()
    {
        var settings = app.Settings;
        _filter.WindowMs = KeyDebounceSupport.SanitizedWindow(settings.GetInt(DefaultsKey.KeyboardDebounceWindowMs, KeyDebounceSupport.DefaultWindowMs));
        _filter.PerKey = KeyDebounceSupport.Decode(settings.GetString(DefaultsKey.KeyboardDebounceKeyWindows));
        var on = settings.GetBool(DefaultsKey.KeyboardDebounceEnabled);
        if (on && _subscription is null) _subscription = app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Filter);
        else if (!on && _subscription is not null) { _subscription.Dispose(); _subscription = null; _filter.Reset(); _pressed.Clear(); }
    }

    /// <summary>Shift, Ctrl, Alt, Win and the lock keys are held on purpose and never filtered.</summary>
    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn || IsModifier(e.VirtualKey)) return false;
        if (!e.IsDown) { _pressed.Remove(e.VirtualKey); _filter.Up(e.VirtualKey, e.Time); return false; }
        // A key that is already down and sends another press is the keyboard's auto-repeat.
        var repeat = !_pressed.Add(e.VirtualKey);
        return _filter.Down(e.VirtualKey, repeat, e.Time) == DebounceVerdict.Suppress;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
