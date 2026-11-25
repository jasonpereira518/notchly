// SPDX-License-Identifier: GPL-3.0-or-later
// A button that records a global shortcut: click it, press the combination. Escape cancels; Backspace clears.
// Storage keeps upstream's shape ("control+alt+win:83"), so a shortcut means the same in a backup.
using System.Windows;
using System.Windows.Input;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

internal sealed class ShortcutRecorder : Button
{
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private bool _recording;

    public ShortcutRecorder(SettingsStore settings, string key) : this(() => settings.GetString(key), value => settings.Set(key, value)) { }

    /// <summary>A recorder for a shortcut kept somewhere other than its own setting (one entry of a map).</summary>
    public ShortcutRecorder(Func<string> read, Action<string> write)
    {
        _read = read;
        _write = write;
        MinWidth = 180;
        Show();
        Click += (_, _) => { _recording = true; Content = "Press the shortcut…"; Focus(); };
        LostKeyboardFocus += (_, _) => { _recording = false; Show(); };
        PreviewKeyDown += OnKeyDown;
    }

    private void Show() => Content = Shortcut.Parse(_read()) is { } shortcut ? shortcut.DisplayText : "Not set";

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { _recording = false; Show(); return; }
        if (key == Key.Back) { _write(Shortcut.NoneStorage); _recording = false; Show(); return; }
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ShortcutModifiers.Win;
        var shortcut = new Shortcut(KeyInterop.VirtualKeyFromKey(key), modifiers);
        if (!shortcut.IsValid) { Content = "Add Ctrl, Alt, Shift or Win"; return; }
        _write(shortcut.StorageValue);
        _recording = false;
        Show();
    }
}
