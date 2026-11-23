// SPDX-License-Identifier: GPL-3.0-or-later
// Sends a menu item's shortcut to the app it was chosen for, the way a keyboard would: the target is
// brought back to the front first (Notchly may, having just received the click), and nothing is sent
// while another key is still held, so Notchly never releases or presses a key the user is holding.
using System.Runtime.InteropServices;
using Notchly.Core.Shortcuts;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public static class KeySender
{
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    /// <summary>Sends <paramref name="chord"/> to <paramref name="target"/>, or to whatever is in front when it is zero.</summary>
    public static async Task<bool> SendAsync(IntPtr target, Shortcut chord)
    {
        if (target != IntPtr.Zero && GetForegroundWindow() != target)
        {
            SetForegroundWindow(target);
            for (var i = 0; i < 15 && GetForegroundWindow() != target; i++) await Task.Delay(20);
            if (GetForegroundWindow() != target)
            {
                Log.Debug("Shortcut not sent: its app did not come back to the front");
                return false;
            }
        }
        for (var i = 0; i < 25 && AnyKeyHeld(); i++) await Task.Delay(20);
        if (AnyKeyHeld())
        {
            Log.Debug("Shortcut not sent: a key is still held");
            return false;
        }

        var modifiers = new List<ushort>();
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers.Add(VK_CONTROL);
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers.Add(VK_MENU);
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers.Add(VK_SHIFT);
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Win)) modifiers.Add(VK_LWIN);
        var inputs = new List<INPUT>();
        foreach (var key in modifiers) inputs.Add(Key(key, up: false));
        inputs.Add(Key((ushort)chord.VirtualKey, up: false));
        inputs.Add(Key((ushort)chord.VirtualKey, up: true));
        for (var i = modifiers.Count - 1; i >= 0; i--) inputs.Add(Key(modifiers[i], up: true));
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count) Log.Debug("Shortcut partly refused ({Sent}/{Count})", sent, inputs.Count);
        return sent == inputs.Count;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    /// <summary>Any keyboard key down (mouse buttons, codes 1–6, do not count).</summary>
    private static bool AnyKeyHeld()
    {
        for (var vk = 0x08; vk <= 0xFE; vk++)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }
}
