// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/FocusFollowsMouse/FocusFollowsMouseSupport.swift (the delay rule) and
// Services/MouseAcceleration/MouseAccelerationSupport.swift (the journal that gives the old setting back).
// Windows differences: both features flip Windows' own system parameters (hover activation and "enhance pointer
// precision"), so Windows does the work and there is no hook, no hover state machine and no per-app exceptions; what
// is left to port is the delay's range and the rule that the previous values are written down first and put back
// on off, on quit and after a crash.
using System.Globalization;

namespace Notchly.Core.Tools;

public static class FocusFollowsSupport
{
    public const int DefaultDelay = 250;
    public const int MinDelay = 100;
    public const int MaxDelay = 3000;

    public static int SanitizedDelay(int milliseconds) => Math.Clamp(milliseconds, MinDelay, MaxDelay);

    /// <summary>What Windows had before: hover activation on or off, whether it raises, and its delay.</summary>
    public readonly record struct Original(bool Tracking, bool Raise, int Milliseconds)
    {
        public string Storage => $"{(Tracking ? 1 : 0)};{(Raise ? 1 : 0)};{Milliseconds.ToString(CultureInfo.InvariantCulture)}";

        public static Original? Parse(string? storage)
        {
            if (string.IsNullOrWhiteSpace(storage)) return null;
            var parts = storage.Split(';');
            if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms < 0) return null;
            return parts[0] is "0" or "1" && parts[1] is "0" or "1" ? new Original(parts[0] == "1", parts[1] == "1", ms) : null;
        }
    }
}

public static class PointerAccelerationSupport
{
    /// <summary>The mouse parameters before: the two speed thresholds and the acceleration level (0 off, 1 or 2 on).</summary>
    public readonly record struct Original(int Threshold1, int Threshold2, int Acceleration)
    {
        public string Storage => $"{Threshold1.ToString(CultureInfo.InvariantCulture)};{Threshold2.ToString(CultureInfo.InvariantCulture)};{Acceleration.ToString(CultureInfo.InvariantCulture)}";

        public static Original? Parse(string? storage)
        {
            if (string.IsNullOrWhiteSpace(storage)) return null;
            var parts = storage.Split(';');
            if (parts.Length != 3) return null;
            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t1)
                   && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t2)
                   && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && t1 >= 0 && t2 >= 0 && a is >= 0 and <= 2
                ? new Original(t1, t2, a) : null;
        }

        /// <summary>The same thresholds with acceleration off.</summary>
        public Original WithoutAcceleration => this with { Acceleration = 0 };
    }

    public enum Step { Nothing, CaptureAndApply, Reapply, Restore }

    /// <summary>
    /// What to do now. Turning it on writes the original down first, once, then applies; while it is on a change made
    /// behind its back is applied again; turning it off, quitting or restarting after a crash gives the original back.
    /// </summary>
    public static Step Plan(bool wantsOff, bool journaled, bool accelerationOnNow) => (wantsOff, journaled) switch
    {
        (true, false) => accelerationOnNow ? Step.CaptureAndApply : Step.Nothing,
        (true, true) => accelerationOnNow ? Step.Reapply : Step.Nothing,
        (false, true) => Step.Restore,
        _ => Step.Nothing,
    };
}
