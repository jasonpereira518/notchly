// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/ScrollWheelSupport.swift, SmoothScrollSupport.swift and the linear, speed and
// sideways rules of ScrollInverter.swift: what counts as a mouse wheel, how a tick is capped, multiplied, flipped or moved to the
// other axis, and how a glide spreads one tick over time. Windows differences: a wheel event carries a delta in units of 120 per notch
// (upstream has lines, points and fixed-point values), a touchpad sends smaller deltas, and a vertical tick is positive away from
// the user while a horizontal one is positive to the right.
namespace Notchly.Core.Input;

public static class ScrollSupport
{
    public const int Notch = 120;
    public const int DefaultSystemLines = 3;

    // MARK: Wheel or touchpad

    /// <summary>
    /// A tick that is a whole number of notches comes from a mouse wheel. Anything smaller comes from a touchpad or a free-spinning
    /// wheel and is left alone, unless the input source says plainly it is a mouse.
    /// </summary>
    public static bool IsMouseWheel(int delta, bool? sourceIsMouse = null) =>
        sourceIsMouse ?? (delta != 0 && delta % Notch == 0);

    // MARK: Linear

    public static int SanitizedLines(int lines) => lines is >= 1 and <= 10 ? lines : 3;

    /// <summary>
    /// A wheel event is capped at one notch however fast the wheel spun, then scaled to the number of lines wanted per notch (against the
    /// lines Windows turns a notch into). A smaller tick keeps its fraction; what rounding leaves over is carried to the next event.
    /// </summary>
    public static int LinearDelta(int delta, int lines, int systemLines, ref double carry)
    {
        if (delta == 0) return 0;
        var sign = Math.Sign(delta);
        // A reversal drops what was carried.
        if (carry != 0 && Math.Sign(carry) != sign) carry = 0;
        var notches = Math.Min(Math.Abs(delta) / (double)Notch, 1.0);
        var perNotch = Notch * SanitizedLines(lines) / (double)Math.Max(1, systemLines);
        var exact = sign * notches * perNotch + carry;
        var whole = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        carry = exact - whole;
        return whole;
    }

    // MARK: Speed

    public static int SanitizedMultiplier(int multiplier) => Math.Clamp(multiplier, 2, 15);

    /// <summary>The tick multiplied, kept inside what a wheel event can carry (a signed 16-bit value) without reversing.</summary>
    public static int SpeedDelta(int delta, int multiplier)
    {
        var scaled = (long)delta * SanitizedMultiplier(multiplier);
        return (int)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    // MARK: Direction

    public readonly record struct InversionPlan(bool Negate);

    /// <summary>
    /// Which ticks flip: vertical ones when the vertical switch is on; horizontal ones when the horizontal switch is on; and a vertical
    /// tick taken sideways because Shift is held follows the horizontal switch.
    /// </summary>
    public static InversionPlan Invert(bool invertVertical, bool invertHorizontal, bool horizontalTick, bool shiftHeldOnVertical) =>
        new(horizontalTick || shiftHeldOnVertical ? invertHorizontal : invertVertical);

    // MARK: Sideways and zoom

    /// <summary>The chosen modifier is held and no other one is.</summary>
    public static bool HeldAlone(ISet<string> held, string modifier) => modifier is not ("none" or "") && held.Count == 1 && held.Contains(modifier);

    /// <summary>A vertical tick becomes a horizontal one: scrolling down moves right.</summary>
    public static int SidewaysDelta(int verticalDelta) => -verticalDelta;

    public static readonly string[] SidewaysModifiers = ["shift", "alt", "control", "win"];
    public static readonly string[] ZoomModifiers = ["none", "alt", "control", "win"];
    public static readonly string[] SpeedModifiers = ["shift", "alt", "control", "win"];

    public static string SanitizedModifier(string? stored, IReadOnlyList<string> allowed, string fallback) =>
        stored is not null && allowed.Contains(stored, StringComparer.OrdinalIgnoreCase) ? stored.ToLowerInvariant() : fallback;

    /// <summary>
    /// Zoom: the chosen modifier plus the wheel is Ctrl plus the wheel, which is how Windows zooms. Nothing to do when the modifier already is Ctrl.
    /// </summary>
    public static bool ZoomNeedsCtrl(string modifier) => modifier is not ("none" or "control" or "");

    // MARK: Exceptions

    /// <summary>Whether an app is on a feature's list of exceptions (a comma-separated list of program names).</summary>
    public static bool IsExcepted(string? exe, IEnumerable<string> list) =>
        !string.IsNullOrEmpty(exe) && list.Any(item => string.Equals(item.Trim(), exe, StringComparison.OrdinalIgnoreCase));

    // MARK: Smooth scrolling

    public const double FrameInterval = 1.0 / 60;
    public const double MaxFrameTime = 1.0 / 20;
    public const double ExtendRadius = 24;

    public static int SanitizedStep(int step) => step is >= 20 and <= 100 ? step : step < 20 && step != 0 ? 20 : step > 100 ? 100 : 40;
    public static int SanitizedResponse(int response) => Math.Clamp(response, 0, 100);
    public static int SanitizedCoast(int coast) => Math.Clamp(coast, 0, 100);

    /// <summary>One notch moves 120 units at the default step of 40; the step scales it.</summary>
    public static double GlideDistance(int delta, int step) => delta * SanitizedStep(step) / 40.0;

    /// <summary>The glide slows as it travels: a stretch factor from 1 up to 1 + 2·coast (as a fraction) at the end of the distance.</summary>
    public static double CoastStretch(int coast, double travelledFraction)
    {
        var t = Math.Clamp(double.IsFinite(travelledFraction) ? travelledFraction : 0, 0, 1);
        return 1 + SanitizedCoast(coast) / 100.0 * 2 * t * t;
    }

    /// <summary>
    /// How much of the distance still to go one frame covers: a share that falls as the glide runs out, never less than a steady
    /// 60 units a second, and a leftover under one unit is finished at once.
    /// </summary>
    public static double FrameDelta(double remaining, double elapsed, int response, double stretch = 1)
    {
        var distance = Math.Abs(remaining);
        if (distance == 0 || !double.IsFinite(distance)) return 0;
        stretch = double.IsFinite(stretch) && stretch >= 1 ? stretch : 1;
        if (distance < 1.0 / stretch) return distance;
        var dt = double.IsFinite(elapsed) ? Math.Clamp(elapsed, 0, MaxFrameTime) : 0;
        var r = SanitizedResponse(response) / 100.0;
        var time = (0.16 - r * 0.12) * stretch;
        var share = distance * (1 - Math.Exp(-dt / time));
        return Math.Min(distance, Math.Max(share, 60 / stretch * dt));
    }

    /// <summary>The whole units to send this frame, the fraction carried to the next.</summary>
    public static int Emit(double amount, ref double carry)
    {
        var exact = amount + carry;
        var whole = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        carry = exact - whole;
        return whole;
    }
}
