// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/SuperKey/SuperKeySupport.swift: the key that stands for Ctrl+Alt+Shift+Win while it is
// held, and does a small thing of its own when it is only tapped. Windows differences: the trigger is caught by the keyboard hook
// (upstream remaps it with hidutil), the trigger can be Caps Lock or a right-hand modifier, and the modifiers include Win.
namespace Notchly.Core.Input;

public enum SoloAction { None, CapsLock, InputSource, Escape }
public enum SoloOutcome { Nothing, Tap, Hold }
public enum SoloEffect { None, PressEscape, ToggleCapsLock, NextInputSource }

public static class SuperKeySupport
{
    /// <summary>A press this long or longer, alone, is a hold; shorter is a tap.</summary>
    public const double HoldThresholdMs = 500;

    public static readonly string[] AllModifiers = ["control", "alt", "shift", "win"];

    /// <summary>
    /// The modifiers the held key stands for, from storage text. Nothing, shift alone or unknown words give all four: it needs
    /// at least one of Ctrl, Alt or Win to be a shortcut at all.
    /// </summary>
    public static IReadOnlyList<string> Modifiers(string? stored)
    {
        var parts = (stored ?? "").Split('+', StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant() switch { "option" => "alt", "command" => "win", var other => other }).Distinct().ToList();
        if (parts.Count == 0 || parts.Any(p => p.Length == 0) || parts.Any(p => !AllModifiers.Contains(p)) || !parts.Any(p => p is "control" or "alt" or "win")) return AllModifiers;
        return AllModifiers.Where(parts.Contains).ToList();
    }

    public static string SanitizedSource(string? source) =>
        source is "capsLock" or "rightControl" or "rightAlt" or "rightShift" or "rightWin" ? source : "capsLock";

    /// <summary>The virtual key behind a source.</summary>
    public static int VirtualKey(string source) => SanitizedSource(source) switch
    {
        "rightControl" => 0xA3, "rightAlt" => 0xA5, "rightShift" => 0xA1, "rightWin" => 0x5C, _ => 0x14,
    };

    public static SoloAction ParseSolo(string? stored) => stored switch
    {
        "capsLock" => SoloAction.CapsLock,
        "inputSource" => SoloAction.InputSource,
        "escape" => SoloAction.Escape,
        _ => SoloAction.None,
    };

    /// <summary>Whether the press, alone, was a tap or a hold.</summary>
    public static SoloOutcome Classify(double heldMs) => heldMs >= HoldThresholdMs ? SoloOutcome.Hold : SoloOutcome.Tap;

    /// <summary>
    /// What a solo press does. Escape and Caps Lock act once and not on a repeat; the input-source action switches to the next
    /// language on a tap and toggles Caps Lock on a long hold, even a repeated one.
    /// </summary>
    public static SoloEffect Effect(SoloAction action, bool longHold, bool repeated) => action switch
    {
        SoloAction.Escape => repeated ? SoloEffect.None : SoloEffect.PressEscape,
        SoloAction.CapsLock => repeated ? SoloEffect.None : SoloEffect.ToggleCapsLock,
        SoloAction.InputSource => longHold ? SoloEffect.ToggleCapsLock : repeated ? SoloEffect.None : SoloEffect.NextInputSource,
        _ => SoloEffect.None,
    };

    /// <summary>The next input language in the list, wrapping; null with one or none to switch to.</summary>
    public static string? NextInputSource(IReadOnlyList<string> ids, string? current)
    {
        if (ids.Count <= 1) return null;
        var at = ids.ToList().IndexOf(current ?? "");
        return ids[(at + 1) % ids.Count];
    }

    /// <summary>How long a hold may go without the key being seen released before it is let go (twice the key-repeat delay, 3 to 30 s).</summary>
    public static double WatchdogSeconds(double? keyRepeatDelaySeconds) =>
        keyRepeatDelaySeconds is { } d && double.IsFinite(d) ? Math.Clamp(d * 2, 3, 30) : 3;

    /// <summary>
    /// The held key as a state machine. While it is held, the next key or click gets its modifiers and the solo action is off; any
    /// other modifier change also cancels it; when it is released alone, it was a tap or a hold.
    /// </summary>
    public sealed class State
    {
        private bool _held, _soloPossible;
        private double _pressedAt;

        public bool Held => _held;

        /// <summary>The trigger went down. Returns true when this is a new press (not an auto-repeat).</summary>
        public bool TriggerDown(double timeMs)
        {
            if (_held) return false;
            _held = true;
            _soloPossible = true;
            _pressedAt = timeMs;
            return true;
        }

        /// <summary>Another key or a click while held: it should carry the modifiers, and the solo action is off.</summary>
        public bool OtherInput()
        {
            if (!_held) return false;
            _soloPossible = false;
            return true;
        }

        /// <summary>A real modifier changed while held: the solo action is off too.</summary>
        public void ModifierChanged() { if (_held) _soloPossible = false; }

        /// <summary>The trigger came up: how it went when nothing else was pressed in between.</summary>
        public SoloOutcome TriggerUp(double timeMs)
        {
            if (!_held) return SoloOutcome.Nothing;
            _held = false;
            var outcome = _soloPossible ? Classify(timeMs - _pressedAt) : SoloOutcome.Nothing;
            _soloPossible = false;
            return outcome;
        }

        public void Release() { _held = false; _soloPossible = false; }

        /// <summary>How long it has been held.</summary>
        public double HeldFor(double nowMs) => _held ? nowMs - _pressedAt : 0;
    }
}
