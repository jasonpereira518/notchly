// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/MouseButtons/MouseButtonShortcutService.swift and the Spaces drag. A mapped extra button
// (Back, Forward) or a wheel tilt sends its keyboard shortcut instead of its own action, and the button's whole press is consumed.
// A chosen button held and dragged switches virtual desktops (sideways) or opens Task View (up), as upstream's drag
// switches Spaces. Apps that you list, or that run as administrator, keep the button as it is.
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using static Notchly.Core.Input.ButtonShortcutSupport;

namespace Notchly.App.MouseInput;

public sealed class MouseButtonService(App app) : IFeatureService
{
    private const uint WmMouseMove = 0x0200, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C, WmMouseHWheel = 0x020E;

    private IDisposable? _subscription;
    private volatile IReadOnlyDictionary<int, string> _map = new Dictionary<int, string>();
    private volatile IReadOnlyList<string> _exceptions = [];
    private volatile int _dragButton;
    private readonly SideWheelGate _gate = new();
    private readonly DesktopDragTracker _drag = new();
    // The button held for the desktop drag: what it did so far decides whether its click is replayed.
    private int _dragging;
    private bool _dragFired;
    private int _lastX, _lastY;

    public void Sync()
    {
        _map = Parse(app.Settings.GetString(DefaultsKey.MouseButtonShortcuts));
        _exceptions = app.Settings.GetList(DefaultsKey.MouseButtonExceptions);
        var settings = app.Settings;
        var shortcuts = settings.GetBool(DefaultsKey.MouseButtonShortcutsEnabled);
        _dragButton = settings.GetBool(DefaultsKey.MouseSpacesGestureEnabled) ? settings.GetInt(DefaultsKey.MouseSpacesGestureButton) : 0;
        if (!shortcuts) _map = new Dictionary<int, string>();
        var wanted = _map.Count > 0 || _dragButton is 3 or 4;
        if (wanted && _subscription is null) _subscription = app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Transform);
        else if (!wanted && _subscription is not null) { _subscription.Dispose(); _subscription = null; _dragging = 0; }
    }

    private bool Excepted(int x, int y)
    {
        var list = _exceptions;
        if (list.Count == 0) return false;
        return ScrollSupport.IsExcepted(WindowFrame.ProgramOf(WindowFrame.TopLevelAt(x, y)), list) || ScrollSupport.IsExcepted(WindowFrame.ProgramOf(WindowInventory.Foreground()), list);
    }

    private bool OnMouse(in MouseEvent e)
    {
        if (e.IsOwn) return false;
        if (app.InputGate.ShouldPassThrough && _dragging == 0) return false;
        switch (e.Message)
        {
            case WmXButtonDown:
            case WmXButtonUp:
            {
                var button = e.XButton + 2;
                var down = e.Message == WmXButtonDown;
                if (button == _dragButton) return Drag(button, down, e.X, e.Y);
                if (!_map.TryGetValue(button, out var storage)) return false;
                if (down && (Excepted(e.X, e.Y) || Shortcut.Parse(storage) is null)) return false;
                // The up of a press that was consumed is consumed too.
                if (down) Send(storage);
                return true;
            }
            case WmMouseHWheel:
            {
                if (SideWheelButton(e.WheelDelta, 0) is not { } tilt || !_map.TryGetValue(tilt, out var storage)) return false;
                if (Excepted(e.X, e.Y)) return false;
                if (_gate.Fires(tilt, e.Time)) Send(storage);
                return true;
            }
            case WmMouseMove when _dragging != 0:
            {
                var action = _drag.Add(e.X - _lastX, e.Y - _lastY, e.Time / 1000.0);
                _lastX = e.X;
                _lastY = e.Y;
                if (action != DesktopAction.None) { _dragFired = true; SendDesktopAction(action); }
                return false;
            }
        }
        return false;
    }

    private bool Drag(int button, bool down, int x, int y)
    {
        if (down)
        {
            if (Excepted(x, y)) return false;
            _dragging = button;
            _dragFired = false;
            _lastX = x;
            _lastY = y;
            _drag.Reset();
            return true;
        }
        if (_dragging != button) return false;
        _dragging = 0;
        // A press that never switched anything is replayed as the ordinary click it was.
        if (!_dragFired)
        {
            app.Injector.Button(button, true, InputSource.MouseButton);
            app.Injector.Button(button, false, InputSource.MouseButton);
        }
        return true;
    }

    private void SendDesktopAction(DesktopAction action)
    {
        switch (action)
        {
            case DesktopAction.Left: Chord([0x11, 0x5B], 0x25); break;
            case DesktopAction.Right: Chord([0x11, 0x5B], 0x27); break;
            case DesktopAction.TaskView: Chord([0x5B], 0x09); break;
        }
    }

    private void Send(string storage)
    {
        if (Shortcut.Parse(storage) is not { } shortcut) return;
        var mods = new List<int>();
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) mods.Add(0x11);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) mods.Add(0x12);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) mods.Add(0x10);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Win)) mods.Add(0x5B);
        Chord(mods, shortcut.VirtualKey);
    }

    private void Chord(IReadOnlyList<int> modifiers, int key)
    {
        var injector = app.Injector;
        var extended = key is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
        foreach (var m in modifiers) injector.Key(m, true, InputSource.MouseButton);
        injector.Key(key, true, InputSource.MouseButton, extended);
        injector.Key(key, false, InputSource.MouseButton, extended);
        foreach (var m in modifiers.Reverse()) injector.Key(m, false, InputSource.MouseButton);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
