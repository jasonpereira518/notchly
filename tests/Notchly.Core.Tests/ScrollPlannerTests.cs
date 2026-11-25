// SPDX-License-Identifier: GPL-3.0-or-later
// The wheel tick decisions behind every scroll feature (upstream's shared tap, ScrollInverter.swift): what each combination of
// features does with a tick, the modifiers held and the program under the pointer. Ticks are in Windows' units, 120 to a notch.
using Notchly.Core.Input;

namespace Notchly.Core.Tests;

public class ScrollPlannerTests
{
    private static readonly ISet<string> None = new HashSet<string>();
    private static ISet<string> Held(params string[] names) => new HashSet<string>(names);

    private static ScrollConfig With(Func<ScrollConfig, ScrollConfig> change) => change(ScrollConfig.Off);

    private static ScrollPlan Plan(ScrollConfig c, int delta, bool vertical = true, ISet<string>? held = null, string? program = null, bool? mouse = null, ScrollPlanner? planner = null) =>
        (planner ?? new ScrollPlanner()).Plan(c, delta, vertical, held ?? None, program, mouse, 3);

    [Fact]
    public void WithNothingOnATickPasses()
    {
        Assert.Equal(ScrollOutcome.Pass, Plan(ScrollConfig.Off, 120).Outcome);
        Assert.False(ScrollConfig.Off.Any);
    }

    [Fact]
    public void TheVerticalSwitchFlipsAVerticalWheel()
    {
        var c = With(x => x with { InvertVertical = true });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, 120), Plan(c, -120));
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -240), Plan(c, 240));
        // A horizontal tick is not its business.
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, vertical: false).Outcome);
    }

    [Fact]
    public void ATouchpadIsLeftAlone()
    {
        var c = With(x => x with { InvertVertical = true, InvertHorizontal = true });
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 37).Outcome);
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, mouse: false).Outcome);
        Assert.Equal(ScrollOutcome.Send, Plan(c, 37, mouse: true).Outcome);
    }

    [Fact]
    public void TheHorizontalSwitchFlipsSidewaysTicksAndShiftedOnes()
    {
        var c = With(x => x with { InvertHorizontal = true });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -120, true), Plan(c, 120, vertical: false));
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120).Outcome);
        // Shift on a vertical tick makes apps scroll sideways: it follows the horizontal switch.
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -120, false), Plan(c, 120, held: Held("shift")));
    }

    [Fact]
    public void SidewaysMovesAVerticalTickToTheOtherAxis()
    {
        var c = With(x => x with { Sideways = true, SidewaysModifier = "shift" });
        // Scrolling down (negative) with Shift alone goes right (positive).
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, 120, true), Plan(c, -120, held: Held("shift")));
        Assert.Equal(ScrollOutcome.Pass, Plan(c, -120).Outcome);
        Assert.Equal(ScrollOutcome.Pass, Plan(c, -120, held: Held("shift", "alt")).Outcome);
        // A native sideways tick is never touched.
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, vertical: false, held: Held("shift")).Outcome);
        // With the horizontal switch on too, the moved tick is flipped like any sideways one.
        var both = c with { InvertHorizontal = true };
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -120, true), Plan(both, -120, held: Held("shift")));
    }

    [Fact]
    public void ZoomTurnsTheModifierIntoCtrl()
    {
        var c = With(x => x with { Zoom = true, ZoomModifier = "alt" });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Zoom, 120, false, "alt"), Plan(c, 120, held: Held("alt")));
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120).Outcome);
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, held: Held("alt", "shift")).Outcome);
        // Ctrl itself already zooms.
        var ctrl = With(x => x with { Zoom = true, ZoomModifier = "control" });
        Assert.Equal(ScrollOutcome.Pass, Plan(ctrl, 120, held: Held("control")).Outcome);
        // The vertical flip applies to a zoom as it does to a scroll.
        var flipped = c with { InvertVertical = true };
        Assert.Equal(-120, Plan(flipped, 120, held: Held("alt")).Delta);
    }

    [Fact]
    public void CtrlMeansTheAppIsZoomingAndTheTickPasses()
    {
        var c = With(x => x with { InvertVertical = true, Linear = true, Speed = true });
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, held: Held("control")).Outcome);
    }

    [Fact]
    public void LinearCapsAFastSpinAtOneNotch()
    {
        var c = With(x => x with { Linear = true, Lines = 3 });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, 120), Plan(c, 600));
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -120), Plan(c, -480));
        // An ordinary notch needs no replacement.
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120).Outcome);
        // More lines per notch than Windows uses.
        var more = c with { Lines = 6 };
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, 240), Plan(more, 120));
        // Linear is vertical only.
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 600, vertical: false).Outcome);
    }

    [Fact]
    public void SpeedMultipliesWithItsModifierHeldAlone()
    {
        var c = With(x => x with { Speed = true, SpeedModifier = "alt", Multiplier = 3 });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, 360), Plan(c, 120, held: Held("alt")));
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120).Outcome);
        Assert.Equal(ScrollOutcome.Pass, Plan(c, 120, held: Held("alt", "shift")).Outcome);
        // Linear comes first: a fast spin is one notch, then it is multiplied.
        var both = c with { Linear = true, Lines = 3 };
        Assert.Equal(360, Plan(both, 600, held: Held("alt")).Delta);
    }

    [Fact]
    public void SmoothHandsTheTickToTheGlide()
    {
        var c = With(x => x with { Smooth = true, Step = 40 });
        Assert.Equal(new ScrollPlan(ScrollOutcome.Glide, 120, false, null, 120), Plan(c, 120));
        Assert.Equal(240, Plan(c with { Step = 80 }, 120).Distance);
        // Flipped, the glide runs the other way.
        Assert.Equal(-120, Plan(c with { InvertVertical = true }, 120).Distance);
        // A sideways tick glides on the other axis.
        Assert.True(Plan(c, 120, vertical: false).Horizontal);
    }

    [Fact]
    public void ExceptionsSkipTheirOwnFeatureOnly()
    {
        var c = With(x => x with
        {
            InvertVertical = true, Smooth = true, InvertExceptions = ["code.exe"], SmoothExceptions = ["steam.exe"],
        });
        // An app excepted from the flip still glides, unflipped.
        var code = Plan(c, 120, program: "Code.exe");
        Assert.Equal(ScrollOutcome.Glide, code.Outcome);
        Assert.Equal(120, code.Distance);
        // An app excepted from the glide is flipped and sent.
        Assert.Equal(new ScrollPlan(ScrollOutcome.Send, -120), Plan(c, 120, program: "steam.exe"));
        Assert.Equal(ScrollOutcome.Glide, Plan(c, 120, program: "chrome.exe").Outcome);
    }

    [Fact]
    public void AFractionOfANotchIsCarriedNotLost()
    {
        var c = With(x => x with { Linear = true, Lines = 5 });
        var planner = new ScrollPlanner();
        var total = 0;
        for (var i = 0; i < 4; i++)
        {
            var plan = planner.Plan(c, 120, true, None, null, true, 3);
            total += plan.Outcome == ScrollOutcome.Pass ? 120 : plan.Delta;
        }
        // Five lines against three: each notch is 200, so four are 800.
        Assert.Equal(800, total);
    }
}
