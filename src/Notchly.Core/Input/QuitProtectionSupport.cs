// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/QuitProtectionSupport.swift and the decisions of QuitProtectionService.swift: an
// accidental quit or close is made to take a deliberate hold, a double press or an extra modifier. Windows differences: upstream guards
// Command-Q and Command-W; here Quit is Alt+F4 or Ctrl+Q and Close is Ctrl+W, each with its own settings.
using System.Globalization;

namespace Notchly.Core.Input;

public enum QuitGuardKind { Quit, Close }
public enum QuitGuardMode { Hold, DoublePress, ExtraModifier }
public enum QuitGuardScope { All, SelectedOnly, AllExceptSelected }

/// <summary>What the guard does with a protected key press.</summary>
public enum GuardDecision { Pass, Swallow, SwallowAndStartHold, SwallowAndArm, SwallowAndHint, Confirm, Cancel }

public static class QuitProtectionSupport
{
    public const int DefaultHoldMs = 800, MinHoldMs = 250, MaxHoldMs = 2000;
    public const int DefaultDoubleMs = 600, MinDoubleMs = 200, MaxDoubleMs = 1500;
    public const double HintSeconds = 1.5;
    /// <summary>The first press of a double press stays armed a little past the interval.</summary>
    public const int ArmSlackMs = 100;

    public static int SanitizedHold(double ms) => double.IsFinite(ms) ? (int)Math.Clamp(ms, MinHoldMs, MaxHoldMs) : DefaultHoldMs;
    public static int SanitizedDouble(double ms) => double.IsFinite(ms) ? (int)Math.Clamp(ms, MinDoubleMs, MaxDoubleMs) : DefaultDoubleMs;

    public static QuitGuardMode ParseMode(string? stored) => stored switch
    {
        "doublePress" => QuitGuardMode.DoublePress,
        "extraModifier" => QuitGuardMode.ExtraModifier,
        _ => QuitGuardMode.Hold,
    };

    public static QuitGuardScope ParseScope(string? stored) => stored switch
    {
        "selectedOnly" => QuitGuardScope.SelectedOnly,
        "allExceptSelected" => QuitGuardScope.AllExceptSelected,
        _ => QuitGuardScope.All,
    };

    /// <summary>The extra modifier that confirms at once: Shift, Alt or Ctrl, whichever the chord does not already use.</summary>
    public static string SanitizedExtraModifier(string? stored) => stored is "shift" or "alt" or "control" ? stored : "shift";

    // MARK: Which presses are guarded

    private const int VkF4 = 0x73, VkQ = 0x51, VkW = 0x57;

    /// <summary>
    /// The modifiers the plain chord uses, or null when this key is not guarded for the kind: Alt+F4 and Ctrl+Q quit, Ctrl+W closes.
    /// </summary>
    public static string? ChordModifier(QuitGuardKind kind, int virtualKey) => (kind, virtualKey) switch
    {
        (QuitGuardKind.Quit, VkF4) => "alt",
        (QuitGuardKind.Quit, VkQ) => "control",
        (QuitGuardKind.Close, VkW) => "control",
        _ => null,
    };

    /// <summary>The held modifiers are exactly the chord's.</summary>
    public static bool IsPlainChord(QuitGuardKind kind, int virtualKey, ISet<string> held) =>
        ChordModifier(kind, virtualKey) is { } modifier && held.Count == 1 && held.Contains(modifier);

    /// <summary>The chord plus exactly one extra modifier, the one chosen.</summary>
    public static bool IsExtraChord(QuitGuardKind kind, int virtualKey, ISet<string> held, string extra) =>
        ChordModifier(kind, virtualKey) is { } modifier && extra != modifier && held.Count == 2 && held.Contains(modifier) && held.Contains(extra);

    /// <summary>Whether the front app is covered: everywhere, only the listed programs, or all but them.</summary>
    public static bool Applies(QuitGuardScope scope, IEnumerable<string> listed, string? exe)
    {
        var inList = !string.IsNullOrEmpty(exe) && listed.Any(l => string.Equals(l.Trim(), exe, StringComparison.OrdinalIgnoreCase));
        return scope switch
        {
            QuitGuardScope.SelectedOnly => inList,
            QuitGuardScope.AllExceptSelected => !inList,
            _ => true,
        };
    }

    /// <summary>The second press of a double press counts if it comes within the interval, the edge included.</summary>
    public static bool IsWithinDoublePressInterval(double firstMs, double secondMs, double intervalMs) => secondMs - firstMs <= intervalMs;

    /// <summary>How far through the hold it is, for the progress bar.</summary>
    public static double HoldProgress(double elapsedMs, double holdMs) => holdMs <= 0 ? 1 : Math.Clamp(elapsedMs / holdMs, 0, 1);

    // MARK: The guard

    /// <summary>
    /// The guard for one kind of key. It is told what the user does and answers what to do with it; the caller owns the timers and
    /// tells it when they fire. A confirmed press is replayed by the caller as Notchly's own event.
    /// </summary>
    public sealed class Guard(QuitGuardMode mode, int holdMs, int doubleMs)
    {
        private enum Phase { Idle, Holding, Confirmed, Armed }
        private Phase _phase;
        private double _startedAt;

        public QuitGuardMode Mode { get; set; } = mode;
        public int HoldMs { get; set; } = SanitizedHold(holdMs);
        public int DoubleMs { get; set; } = SanitizedDouble(doubleMs);
        public bool Pending => _phase is Phase.Holding or Phase.Armed;

        /// <summary>The plain chord went down.</summary>
        public GuardDecision PlainDown(bool isRepeat, double nowMs)
        {
            switch (Mode)
            {
                case QuitGuardMode.ExtraModifier:
                    return isRepeat ? GuardDecision.Swallow : GuardDecision.SwallowAndHint;
                case QuitGuardMode.DoublePress:
                    if (isRepeat) return GuardDecision.Swallow;
                    if (_phase == Phase.Armed && IsWithinDoublePressInterval(_startedAt, nowMs, DoubleMs))
                    {
                        _phase = Phase.Idle;
                        return GuardDecision.Confirm;
                    }
                    _phase = Phase.Armed;
                    _startedAt = nowMs;
                    return GuardDecision.SwallowAndArm;
                default:
                    if (isRepeat) return GuardDecision.Swallow;
                    if (_phase == Phase.Holding || _phase == Phase.Confirmed) return GuardDecision.Swallow;
                    _phase = Phase.Holding;
                    _startedAt = nowMs;
                    return GuardDecision.SwallowAndStartHold;
            }
        }

        /// <summary>The chord plus the extra modifier went down: confirmed at once, in the mode that allows it.</summary>
        public GuardDecision ExtraDown() => Mode == QuitGuardMode.ExtraModifier ? GuardDecision.Confirm : GuardDecision.Pass;

        /// <summary>The hold timer fired: if the key is still held, it is confirmed once.</summary>
        public GuardDecision HoldElapsed(double nowMs)
        {
            if (_phase != Phase.Holding || nowMs - _startedAt < HoldMs) return GuardDecision.Swallow;
            _phase = Phase.Confirmed;
            return GuardDecision.Confirm;
        }

        /// <summary>The key or the modifier was released.</summary>
        public GuardDecision Released()
        {
            var wasHolding = _phase == Phase.Holding;
            if (_phase != Phase.Armed) _phase = Phase.Idle;
            return wasHolding ? GuardDecision.Cancel : GuardDecision.Pass;
        }

        /// <summary>Esc, or the armed window running out.</summary>
        public void Cancel() => _phase = Phase.Idle;

        /// <summary>The armed first press has waited too long.</summary>
        public bool ArmExpired(double nowMs) => _phase == Phase.Armed && nowMs - _startedAt > DoubleMs + ArmSlackMs;
    }

    public static string SettingKey(QuitGuardKind kind, string name) => "quitProtection" + kind + name;
    public static string Describe(QuitGuardKind kind) => kind == QuitGuardKind.Quit ? "Alt+F4 and Ctrl+Q" : "Ctrl+W";
    public static string Format(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);
}
