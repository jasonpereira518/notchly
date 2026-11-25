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
    private readonly SettingsStore _settings;
    private readonly string _key;
    private bool _recording;

    public ShortcutRecorder(SettingsStore settings, string key)
    {
        _settings = settings;
        _key = key;
        MinWidth = 180;
        Show();
        Click += (_, _) => { _recording = true; Content = "Press the shortcut…"; Focus(); };
        LostKeyboardFocus += (_, _) => { _recording = false; Show(); };
        PreviewKeyDown += OnKeyDown;
    }

    private void Show() => Content = Shortcut.Parse(_settings.GetString(_key)) is { } shortcut ? shortcut.DisplayText : "Not set";

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { _recording = false; Show(); return; }
        if (key == Key.Back) { _settings.Set(_key, Shortcut.NoneStorage); _recording = false; Show(); return; }
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ShortcutModifiers.Win;
        var shortcut = new Shortcut(KeyInterop.VirtualKeyFromKey(key), modifiers);
        if (!shortcut.IsValid) { Content = "Add Ctrl, Alt, Shift or Win"; return; }
        _settings.Set(_key, shortcut.StorageValue);
        _recording = false;
        Show();
    }
}
