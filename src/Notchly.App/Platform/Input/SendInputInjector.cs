// SPDX-License-Identifier: GPL-3.0-or-later
// The real input injector: everything the input features send goes through SendInput with Notchly's marker in the
// extra-info field, so the hooks recognise it as their own. Text goes out as Unicode, one UTF-16 unit at a time (a
// surrogate pair as two units), so it does not depend on the keyboard layout.
using System.Runtime.InteropServices;
using Notchly.Core.Input;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public sealed class SendInputInjector : IInputInjector
{
    private const uint KeyEventExtended = 0x1, KeyEventUnicode = 0x4;
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010,
        MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040, MouseXDown = 0x0080, MouseXUp = 0x0100, MouseWheel = 0x0800, MouseHWheel = 0x1000;
    private const uint InputMouse = 0;

    public void Key(int virtualKey, bool down, InputSource source, bool extended = false) =>
        Send(KeyInput((ushort)virtualKey, 0, (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KeyEventExtended : 0), source));

    public void Text(string text, InputSource source)
    {
        if (text.Length == 0) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(KeyInput(0, unit, KeyEventUnicode, source));
            inputs.Add(KeyInput(0, unit, KeyEventUnicode | KEYEVENTF_KEYUP, source));
        }
        Send([.. inputs]);
    }

    public void Wheel(int delta, bool horizontal, InputSource source) =>
        Send(MouseInput(0, 0, unchecked((uint)delta), horizontal ? MouseHWheel : MouseWheel, source));

    public void Button(int button, bool down, InputSource source)
    {
        var (flags, data) = button switch
        {
            0 => (down ? MouseLeftDown : MouseLeftUp, 0u),
            1 => (down ? MouseRightDown : MouseRightUp, 0u),
            2 => (down ? MouseMiddleDown : MouseMiddleUp, 0u),
            3 => (down ? MouseXDown : MouseXUp, 1u),
            4 => (down ? MouseXDown : MouseXUp, 2u),
            _ => (0u, 0u),
        };
        if (flags != 0) Send(MouseInput(0, 0, data, flags, source));
    }

    public void Move(int dx, int dy, InputSource source) => Send(MouseInput(dx, dy, 0, MouseMove, source));

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags, InputSource source) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = new IntPtr(InputMarker.Make(source)) } },
    };

    private static INPUT MouseInput(int dx, int dy, uint data, uint flags, InputSource source) => new()
    {
        type = InputMouse,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = new IntPtr(InputMarker.Make(source)) } },
    };

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
