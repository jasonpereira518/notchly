// SPDX-License-Identifier: GPL-3.0-or-later
// The decisions of upstream's shared wheel tap (ScrollInverter.swift), apart from the hook: given a wheel tick, the modifiers held and the
// program under the pointer, what to do with it. The hook then carries the plan out: send a replacement tick, send a zoom
// as Ctrl and the wheel, hand the tick to the glide, swallow it, or let it pass. Keeping this here means every combination of
// features can be tried without a wheel.
namespace Notchly.Core.Input;

public sealed record ScrollConfig(
    bool InvertVertical, bool InvertHorizontal, bool Sideways, string SidewaysModifier, bool Zoom, string ZoomModifier,
    bool Linear, int Lines, bool Speed, string SpeedModifier, int Multiplier, bool Smooth, int Step,
    IReadOnlyList<string> InvertExceptions, IReadOnlyList<string> LinearExceptions, IReadOnlyList<string> SpeedExceptions, IReadOnlyList<string> SmoothExceptions)
{
    public bool Any => InvertVertical || InvertHorizontal || Sideways || Zoom || Linear || Speed || Smooth;

    public static ScrollConfig Off { get; } = new(false, false, false, "shift", false, "control", false, 3, false, "alt", 3, false, 40, [], [], [], []);
}

public enum ScrollOutcome { Pass, Swallow, Send, Zoom, Glide }

/// <summary>What to do with one tick. For a zoom, <see cref="Modifier"/> is the physical modifier to lift while Ctrl takes its place.</summary>
public readonly record struct ScrollPlan(ScrollOutcome Outcome, int Delta = 0, bool Horizontal = false, string? Modifier = null, double Distance = 0);

public sealed class ScrollPlanner
{
    private double _carry;

    /// <summary>Forgets what rounding left over (the next tick starts clean).</summary>
    public void Reset() => _carry = 0;

    public ScrollPlan Plan(ScrollConfig c, int delta, bool vertical, ISet<string> held, string? program, bool? sourceIsMouse, int systemLines)
    {
        if (!c.Any || delta == 0 || !ScrollSupport.IsMouseWheel(delta, sourceIsMouse)) return new ScrollPlan(ScrollOutcome.Pass);
        var excepted = ScrollSupport.IsExcepted(program, c.InvertExceptions);

        // Zoom: the chosen modifier and the wheel become Ctrl and the wheel, which is how Windows zooms.
        if (vertical && c.Zoom && !excepted && ScrollSupport.ZoomNeedsCtrl(c.ZoomModifier) && ScrollSupport.HeldAlone(held, c.ZoomModifier))
            return new ScrollPlan(ScrollOutcome.Zoom, c.InvertVertical ? -delta : delta, false, c.ZoomModifier);
        // Ctrl held means the app is zooming by itself: leave the tick alone.
        if (held.Contains("control") && !(c.Sideways && c.SidewaysModifier == "control")) return new ScrollPlan(ScrollOutcome.Pass);

        var value = delta;
        var horizontal = !vertical;
        var changed = false;
        var shiftTurnedSideways = false;
        if (vertical && c.Sideways && !excepted && ScrollSupport.HeldAlone(held, c.SidewaysModifier))
        {
            value = ScrollSupport.SidewaysDelta(value);
            horizontal = true;
            vertical = false;
            changed = true;
        }
        else if (vertical && held.Count == 1 && held.Contains("shift")) shiftTurnedSideways = true;

        var linearOn = c.Linear && vertical && !ScrollSupport.IsExcepted(program, c.LinearExceptions);
        if (vertical && c.Speed && !ScrollSupport.IsExcepted(program, c.SpeedExceptions) && ScrollSupport.HeldAlone(held, c.SpeedModifier))
        {
            if (linearOn) value = ScrollSupport.LinearDelta(value, 1, 1, ref _carry);
            value = ScrollSupport.SpeedDelta(value, c.Multiplier);
            changed = true;
        }
        else if (linearOn)
        {
            value = ScrollSupport.LinearDelta(value, c.Lines, systemLines, ref _carry);
            // An ordinary notch comes out as it went in, and then there is nothing to replace.
            changed |= value != delta;
        }

        if (!excepted && ScrollSupport.Invert(c.InvertVertical, c.InvertHorizontal, horizontal, shiftTurnedSideways).Negate)
        {
            value = -value;
            changed = true;
        }

        if (c.Smooth && !ScrollSupport.IsExcepted(program, c.SmoothExceptions))
            return new ScrollPlan(ScrollOutcome.Glide, value, horizontal, null, ScrollSupport.GlideDistance(value, c.Step));
        if (!changed) return new ScrollPlan(ScrollOutcome.Pass);
        // A tick that rounding turned into nothing is swallowed; its fraction is carried to the next.
        return value == 0 ? new ScrollPlan(ScrollOutcome.Swallow) : new ScrollPlan(ScrollOutcome.Send, value, horizontal);
    }
}
