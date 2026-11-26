// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the remappable tool shortcuts in ScreenshotSupport.Tool (upstream Services/QuickTools/ScreenshotSupport.swift)
// and of the editor-only mode of GlobalShortcut (a binding may be a bare key).
//
// Differences from macOS, all deliberate:
//  - Key codes are Windows virtual keys, so storage reads "text=:84" where upstream read "text=:17". The shape
//    ("tool=mod+mod:code,tool=...") is unchanged.
//  - The Command key of the macOS editor is Control here: Ctrl+C, Ctrl+V, Ctrl+S, Ctrl+Z, Ctrl+P, Ctrl+0, Ctrl+1, Ctrl+=,
//    Ctrl+-, Ctrl+Backspace/Delete, Ctrl+W, Ctrl+Q stay reserved. Ctrl+Y (the Windows redo) is reserved as well.
//  - "Primary modifier" (a key that cannot be a typed digit) is Control, Alt or Win; Shift alone is not primary.
//  - What a key types on the active layout comes from an injected IKeyboardLayout (the WPF host can wrap
//    ToUnicodeEx); the default is a US layout.
using Notchly.Core.Shortcuts;

namespace Notchly.Core.Capture;

/// <summary>A key the editor's tool rail can bind: Windows virtual key plus modifiers. Unlike a global shortcut it may have no modifier.</summary>
public readonly record struct ToolShortcut(int VirtualKey, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    public bool HasPrimaryModifier =>
        (Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Win)) != 0;

    private static readonly (ShortcutModifiers Flag, string Name)[] Order =
    [
        (ShortcutModifiers.Control, "control"),
        (ShortcutModifiers.Alt, "alt"),
        (ShortcutModifiers.Shift, "shift"),
        (ShortcutModifiers.Win, "win"),
    ];

    /// <summary>Modifier keys on their own (and the lock keys) are never a binding's key.</summary>
    public bool HasPrintableKey => VirtualKey is > 0 and < 256
        && VirtualKey is not (0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x90 or 0x91 or (>= 0xA0 and <= 0xA5));

    /// <summary>"control+alt:75", or ":84" for a bare key.</summary>
    public string StorageValue
    {
        get
        {
            var modifiers = Modifiers;
            return string.Join("+", Order.Where(o => (modifiers & o.Flag) != 0).Select(o => o.Name)) + ":" + VirtualKey;
        }
    }

    /// <summary>The editor-only parse: a bare key is accepted, an unknown modifier or a non-key code is not.</summary>
    public static ToolShortcut? Parse(string? storage)
    {
        if (string.IsNullOrEmpty(storage)) return null;
        int colon = storage.IndexOf(':');
        if (colon < 0 || !int.TryParse(storage.AsSpan(colon + 1), out int key)) return null;
        var modifiers = ShortcutModifiers.None;
        foreach (var part in storage[..colon].Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var flag = part switch
            {
                "control" => ShortcutModifiers.Control,
                "alt" or "option" => ShortcutModifiers.Alt,
                "shift" => ShortcutModifiers.Shift,
                "win" or "command" => ShortcutModifiers.Win,
                _ => (ShortcutModifiers?)null,
            };
            if (flag is null) return null;
            modifiers |= flag.Value;
        }
        var shortcut = new ToolShortcut(key, modifiers);
        return shortcut.HasPrintableKey ? shortcut : null;
    }

    /// <summary>"Ctrl+Alt+K", or "T" for a bare key.</summary>
    public string DisplayText(IKeyboardLayout? layout = null)
    {
        var parts = new List<string>();
        if ((Modifiers & ShortcutModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & ShortcutModifiers.Win) != 0) parts.Add("Win");
        var typed = layout?.TypedLabel(VirtualKey, shift: false, capsLock: false);
        parts.Add(!string.IsNullOrEmpty(typed) ? typed.ToUpperInvariant() : Shortcut.KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}

/// <summary>What a physical key types on the active keyboard layout. Digits decide whether a recorded key names a rail slot.</summary>
public interface IKeyboardLayout
{
    string? TypedLabel(int virtualKey, bool shift, bool capsLock);
}

/// <summary>US layout: the digit row types a digit unshifted and a symbol shifted; the numpad types digits.</summary>
public sealed class UsKeyboardLayout : IKeyboardLayout
{
    public static readonly UsKeyboardLayout Instance = new();
    private const string ShiftedDigits = ")!@#$%^&*(";

    public string? TypedLabel(int virtualKey, bool shift, bool capsLock)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
            return shift ? ShiftedDigits[virtualKey - 0x30].ToString() : ((char)virtualKey).ToString();
        if (virtualKey is >= 0x60 and <= 0x69)
            return shift ? null : ((char)('0' + (virtualKey - 0x60))).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A)
            return ((char)(shift ^ capsLock ? virtualKey : virtualKey + 32)).ToString();
        return null;
    }
}

public enum BindingRejectionKind { Reserved, Tool, Role, WindowLayout, System }

/// <summary>Why a recorded key cannot become a binding (port of Tool.BindingRejection).</summary>
public readonly record struct BindingRejection(BindingRejectionKind Kind, ScreenshotTool? Tool = null, string? Detail = null)
{
    public static readonly BindingRejection Reserved = new(BindingRejectionKind.Reserved);
    public static readonly BindingRejection System = new(BindingRejectionKind.System);
    public static BindingRejection ForTool(ScreenshotTool tool) => new(BindingRejectionKind.Tool, tool);
    public static BindingRejection ForRole(string role) => new(BindingRejectionKind.Role, null, role);
    public static BindingRejection ForWindowLayout(string title) => new(BindingRejectionKind.WindowLayout, null, title);
}

public static class ToolShortcuts
{
    private static IKeyboardLayout Layout(IKeyboardLayout? layout) => layout ?? UsKeyboardLayout.Instance;

    /// <summary>
    /// Saved bindings, one owner per key. Edited backups can contain duplicates: keep one owner in rail-default order so
    /// routing and the displayed keys agree. Invalid and reserved entries are dropped.
    /// </summary>
    public static Dictionary<ScreenshotTool, ToolShortcut> Bindings(string? raw)
    {
        var result = new Dictionary<ScreenshotTool, ToolShortcut>();
        foreach (var entry in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = entry.IndexOf('=');
            if (equals < 0) continue;
            if (ScreenshotTools.FromRaw(entry[..equals]) is not { } tool || tool == ScreenshotTool.Image) continue;
            if (ToolShortcut.Parse(entry[(equals + 1)..]) is not { } shortcut || IsReservedEditorKey(shortcut)) continue;
            result[tool] = shortcut;
        }
        var seen = new HashSet<ToolShortcut>();
        foreach (var tool in ScreenshotTools.EditorTools)
        {
            if (result.TryGetValue(tool, out var shortcut) && !seen.Add(shortcut)) result.Remove(tool);
        }
        return result;
    }

    /// <summary>
    /// A layout or Caps Lock change can make a saved symbol type a rail digit. Suspend it in that context without
    /// erasing the saved choice.
    /// </summary>
    public static Dictionary<ScreenshotTool, ToolShortcut> ActiveBindings(string? raw, bool capsLockOn = false,
                                                                          IKeyboardLayout? layout = null) =>
        Bindings(raw).Where(p => ShortcutDigit(p.Value, capsLockOn, layout) is null)
            .ToDictionary(p => p.Key, p => p.Value);

    public static string BindingsStorage(IReadOnlyDictionary<ScreenshotTool, ToolShortcut> bindings) =>
        string.Join(",", ScreenshotTools.EditorTools.Where(bindings.ContainsKey)
            .Select(t => $"{t.Raw()}={bindings[t].StorageValue}"));

    /// <summary>Editor and menu commands that a tool binding must never shadow.</summary>
    public static bool IsReservedEditorKey(ToolShortcut shortcut)
    {
        int key = shortcut.VirtualKey;
        if ((shortcut.Modifiers & ShortcutModifiers.Control) != 0)
        {
            // The editor's Control branch also accepts extra modifiers.
            return key is 0x43 or 0x56 or 0x53 or 0x5A or 0x59 or 0x50   // C V S Z Y P
                or 0x30 or 0x31 or 0x60 or 0x61                          // 0 1 (row and numpad)
                or 0xBB or 0xBD or 0x6B or 0x6D                          // = - (row and numpad)
                or 0x08 or 0x2E or 0x57 or 0x51;                         // Backspace Delete W Q
        }
        return !shortcut.HasPrimaryModifier && key is 0x1B or 0x0D or 0x08 or 0x2E;   // Esc Enter Backspace Delete
    }

    /// <summary>
    /// The tool a key press selects. The printed digit is authoritative when present, including input methods whose
    /// output differs from the physical key; otherwise an exactly matching custom binding wins.
    /// </summary>
    public static ScreenshotTool? ShortcutTool(int virtualKey, ShortcutModifiers modifiers, int? number, string? orderRaw,
                                               string? bindingsRaw, bool enabled, bool capsLockOn = false,
                                               IKeyboardLayout? layout = null)
    {
        if (!enabled) return null;
        var bindings = ActiveBindings(bindingsRaw, capsLockOn, layout);
        var probe = new ToolShortcut(virtualKey, modifiers);
        if (!probe.HasPrimaryModifier && number is { } n && n >= 1 && n <= ScreenshotTools.ShortcutLimit)
        {
            if (ScreenshotTools.ShortcutTool(n, orderRaw, true) is not { } tool || bindings.ContainsKey(tool)) return null;
            return tool;
        }
        foreach (var tool in ScreenshotTools.EditorTools)
        {
            if (bindings.TryGetValue(tool, out var bound) && bound == probe) return tool;
        }
        return null;
    }

    /// <summary>
    /// The rail slot a recorded key names, read from what the key types on the active layout rather than from its
    /// position: AZERTY's 1 is Shift on the &amp; key, and a numerical variant also reaches it through Caps Lock.
    /// </summary>
    public static int? ShortcutDigit(ToolShortcut shortcut, bool capsLockOn = false, IKeyboardLayout? layout = null)
    {
        if (shortcut.HasPrimaryModifier) return null;
        bool shifted = (shortcut.Modifiers & ShortcutModifiers.Shift) != 0;
        var typed = Layout(layout).TypedLabel(shortcut.VirtualKey, shifted, capsLockOn);
        return typed is { Length: 1 } && typed[0] is >= '1' and <= '9' ? typed[0] - '0' : null;
    }

    /// <summary>What the rail badge and the recorder show for a tool: its own binding's caps, or the position digit of an unbound tool.</summary>
    public static string? ShortcutLabel(ScreenshotTool tool, string? orderRaw, string? bindingsRaw, bool enabled,
                                        bool capsLockOn = false, IKeyboardLayout? layout = null)
    {
        if (!enabled) return null;
        if (ActiveBindings(bindingsRaw, capsLockOn, layout).TryGetValue(tool, out var binding))
            return binding.DisplayText(layout);
        return ScreenshotTools.ShortcutNumber(tool, orderRaw, true)?.ToString();
    }

    /// <summary>
    /// Why a recorded key cannot become a binding, or null when it can. The outside checks are passed in by the field so
    /// the order and the early return are testable; the system table is asked last because it is the one read that
    /// leaves the process.
    /// </summary>
    public static BindingRejection? Rejection(ToolShortcut shortcut, ScreenshotTool excluding, string? bindingsRaw,
                                              Func<ToolShortcut, string?> roleConflict,
                                              Func<ToolShortcut, string?> windowLayoutConflict,
                                              Func<ToolShortcut, bool> systemConflict)
    {
        if (IsReservedEditorKey(shortcut)) return BindingRejection.Reserved;
        var bindings = Bindings(bindingsRaw);
        foreach (var tool in ScreenshotTools.EditorTools)
        {
            if (tool != excluding && bindings.TryGetValue(tool, out var bound) && bound == shortcut)
                return BindingRejection.ForTool(tool);
        }
        if (roleConflict(shortcut) is { } role) return BindingRejection.ForRole(role);
        if (windowLayoutConflict(shortcut) is { } title) return BindingRejection.ForWindowLayout(title);
        return systemConflict(shortcut) ? BindingRejection.System : null;
    }

    /// <summary>
    /// A recorded digit moves the tool; clearing moves it below the first nine. Other bindings stay attached to their
    /// tools through reorders.
    /// </summary>
    public static (string OrderRaw, string BindingsRaw) AssigningBinding(ToolShortcut? shortcut, int? digit, ScreenshotTool tool,
                                                                        string? orderRaw, string? bindingsRaw)
    {
        var bindings = Bindings(bindingsRaw);
        if (digit is null && shortcut is { } value) bindings[tool] = value;
        else bindings.Remove(tool);
        var order = shortcut is null || digit is not null
            ? ScreenshotTools.AssigningShortcut(digit, tool, orderRaw)
            : ScreenshotTools.Ordered(orderRaw);
        return (ScreenshotTools.OrderStorage(order), BindingsStorage(bindings));
    }
}
