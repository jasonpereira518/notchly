// SPDX-License-Identifier: GPL-3.0-or-later
// The character a key types on the layout the foreground window uses, for the switcher's search and the snippet buffer. The key
// state is built by hand (Shift and Caps Lock) and the call asks Windows not to change its dead-key state, so asking never eats
// an accent a person is in the middle of typing.
using System.Runtime.InteropServices;
using System.Text;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public static class KeyText
{
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int size, uint flags, IntPtr layout);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    private const uint DoNotChangeState = 0x4;

    /// <summary>The text of a key press, or null when it types nothing (a dead key, a modifier, a control key).</summary>
    public static string? Of(int virtualKey, int scanCode, bool shift, bool altGr = false)
    {
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 0x01;
        if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var buffer = new StringBuilder(8);
        var count = ToUnicodeEx((uint)virtualKey, (uint)scanCode, state, buffer, buffer.Capacity, DoNotChangeState, layout);
        if (count <= 0) return null;
        var text = buffer.ToString(0, Math.Min(count, buffer.Length));
        return text.Length > 0 && !char.IsControl(text[0]) ? text : null;
    }
}
