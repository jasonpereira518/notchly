// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's Carbon RegisterEventHotKey (Services/HotkeyManager.swift):
// RegisterHotKey on a message-only window. A failed registration means another app owns the
// combination, which is how conflicts are detected.
using System.Windows.Interop;
using Notchly.Core.Shortcuts;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("NotchlyHotkeys") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a shortcut; returns a handle, or null when another app already owns it.</summary>
    public IDisposable? Register(Shortcut shortcut, Action action)
    {
        var id = _nextId++;
        uint modifiers = MOD_NOREPEAT;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers |= MOD_CONTROL;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= MOD_ALT;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= MOD_SHIFT;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Win)) modifiers |= MOD_WIN;
        if (!RegisterHotKey(_source.Handle, id, modifiers, (uint)shortcut.VirtualKey))
        {
            Log.Warning("Shortcut {Shortcut} is taken by another app", shortcut.DisplayText);
            return null;
        }
        _actions[id] = action;
        return new Registration(this, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    private void Unregister(int id)
    {
        if (_actions.Remove(id)) UnregisterHotKey(_source.Handle, id);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList()) Unregister(id);
        _source.Dispose();
    }

    private sealed class Registration(HotkeyService owner, int id) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Unregister(id);
        }
    }
}
