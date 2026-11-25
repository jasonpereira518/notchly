// SPDX-License-Identifier: GPL-3.0-or-later
// Which modifiers are down right now, by the names the Core rules use (control, alt, shift, win). Cheap enough to ask from a hook.
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public static class KeyState
{
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static HashSet<string> HeldModifiers()
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (IsDown(0x11)) held.Add("control");
        if (IsDown(0x12)) held.Add("alt");
        if (IsDown(0x10)) held.Add("shift");
        if (IsDown(0x5B) || IsDown(0x5C)) held.Add("win");
        return held;
    }

    /// <summary>Exactly these modifiers are down, no others.</summary>
    public static bool HeldExactly(IEnumerable<string> modifiers)
    {
        var held = HeldModifiers();
        var wanted = modifiers.ToHashSet();
        return held.SetEquals(wanted);
    }
}
