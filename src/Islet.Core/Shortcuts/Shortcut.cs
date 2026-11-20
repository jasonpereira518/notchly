// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/GlobalShortcut.swift (model and storage format).
// macOS modifiers map to Windows ones: control→Control, option→Alt, shift→Shift, command→Win.
// Storage keeps the upstream shape: "control+alt:65" (decimal virtual-key code), "none" = cleared.
namespace Islet.Core.Shortcuts;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

public readonly record struct Shortcut(int VirtualKey, ShortcutModifiers Modifiers)
{
    public const string NoneStorage = "none";

    private static readonly (ShortcutModifiers Flag, string Name, string Display)[] Order =
    [
        (ShortcutModifiers.Control, "control", "Ctrl"),
        (ShortcutModifiers.Alt, "alt", "Alt"),
        (ShortcutModifiers.Shift, "shift", "Shift"),
        (ShortcutModifiers.Win, "win", "Win"),
    ];

    /// <summary>A global shortcut needs at least one modifier, except function keys F13–F24.</summary>
    public bool IsValid => VirtualKey is > 0 and < 256
        && (Modifiers != ShortcutModifiers.None || VirtualKey is >= 0x7C and <= 0x87);

    public string StorageValue
    {
        get
        {
            var modifiers = Modifiers;
            var names = Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name);
            return string.Join("+", names) + ":" + VirtualKey;
        }
    }

    public static Shortcut? Parse(string? storage)
    {
        if (string.IsNullOrWhiteSpace(storage) || storage == NoneStorage) return null;
        var colon = storage.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(storage.AsSpan(colon + 1), out var key)) return null;
        var modifiers = ShortcutModifiers.None;
        foreach (var part in storage[..colon].Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Order.FirstOrDefault(o => o.Name == part || (o.Name == "alt" && part == "option")
                                                  || (o.Name == "win" && part == "command"));
            if (match.Name is null) return null;
            modifiers |= match.Flag;
        }
        var shortcut = new Shortcut(key, modifiers);
        return shortcut.IsValid ? shortcut : null;
    }

    /// <summary>"Ctrl+Alt+Space" for display.</summary>
    public string DisplayText
    {
        get
        {
            var modifiers = Modifiers;
            var parts = Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Display).ToList();
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    public override string ToString() => DisplayText;

    public static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x1B => "Esc", 0x08 => "Backspace", 0x2E => "Delete",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x21 => "PageUp", 0x22 => "PageDown", 0x24 => "Home", 0x23 => "End", 0x2D => "Insert",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => "0x" + vk.ToString("X2"),
    };
}
