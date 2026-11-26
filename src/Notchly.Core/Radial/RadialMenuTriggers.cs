// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/RadialMenu/RadialMenuSupport.swift: RadialMenuMouseTrigger,
// RadialMenuActivationMode and RadialMenuReleaseAction. The four-finger trackpad tap is not ported (Windows deviation).
namespace Notchly.Core.Radial;

/// <summary>
/// The optional second summoner: any extra mouse button. The raw values stay stable for stored settings; buttons other
/// than Back and Forward use "button:N" with N in 3...31 (3 is Back, 4 is Forward, then the USB order).
/// </summary>
public readonly record struct RadialMouseTrigger
{
    public const int MinButton = 3;
    public const int MaxButton = 31;
    public const int BackButtonNumber = 3;
    public const int ForwardButtonNumber = 4;

    /// <summary>The extra button, or null when the trigger is off.</summary>
    public int? ButtonNumber { get; }

    private RadialMouseTrigger(int? button) => ButtonNumber = button;

    public static RadialMouseTrigger Off => default;
    public static RadialMouseTrigger Back => new(BackButtonNumber);
    public static RadialMouseTrigger Forward => new(ForwardButtonNumber);

    /// <summary>A trigger for the button; a number outside 3...31 reads as off.</summary>
    public static RadialMouseTrigger Button(int number) =>
        number is >= MinButton and <= MaxButton ? new(number) : Off;

    public static IEnumerable<int> ButtonRange => Enumerable.Range(MinButton, MaxButton - MinButton + 1);

    public bool IsOff => ButtonNumber is null;

    /// <summary>The stored form: "off", "back", "forward" or "button:N".</summary>
    public string RawValue => ButtonNumber switch
    {
        null => "off",
        BackButtonNumber => "back",
        ForwardButtonNumber => "forward",
        { } number => "button:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Reads a stored value; anything unknown or out of range is off.</summary>
    public static RadialMouseTrigger Sanitized(string? raw)
    {
        switch (raw)
        {
            case "back": return Back;
            case "forward": return Forward;
            case null: return Off;
        }
        const string prefix = "button:";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal)) return Off;
        return int.TryParse(raw.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? Button(number)
            : Off;
    }
}

/// <summary>How the summoning shortcut or side button owns a radial-menu session. Raw values are persisted; never rename them.</summary>
public enum RadialMenuActivationMode
{
    /// <summary>The adaptive gesture: release over a slice to run it, or near the center to leave the wheel open for clicking.</summary>
    PressOrHold,
    /// <summary>A press opens a sticky wheel. Releasing the summoner has no effect.</summary>
    Press,
    /// <summary>The wheel exists only while the summoner is down. Release runs the highlighted slice, or dismisses.</summary>
    Hold,
}

public enum RadialMenuReleaseAction { StayOpen, Dismiss, Select }

public static class RadialMenuActivationModes
{
    public static string RawValue(this RadialMenuActivationMode mode) => mode switch
    {
        RadialMenuActivationMode.Press => "press",
        RadialMenuActivationMode.Hold => "hold",
        _ => "pressOrHold",
    };

    /// <summary>Known values decode; anything else falls back to the adaptive gesture.</summary>
    public static RadialMenuActivationMode Sanitized(string? raw) => raw switch
    {
        "press" => RadialMenuActivationMode.Press,
        "hold" => RadialMenuActivationMode.Hold,
        _ => RadialMenuActivationMode.PressOrHold,
    };

    /// <summary>Whether the session starts in the held phase (the summoner is still down).</summary>
    public static bool StartsHeld(this RadialMenuActivationMode mode, bool requestedHold, bool hasHeldButton,
                                  bool shortcutHasModifiers)
    {
        if (mode == RadialMenuActivationMode.Press) return false;
        return hasHeldButton || (requestedHold && shortcutHasModifiers);
    }

    public static RadialMenuReleaseAction ReleaseAction(this RadialMenuActivationMode mode, bool hasSelection)
    {
        if (hasSelection) return RadialMenuReleaseAction.Select;
        return mode == RadialMenuActivationMode.Hold ? RadialMenuReleaseAction.Dismiss : RadialMenuReleaseAction.StayOpen;
    }
}
