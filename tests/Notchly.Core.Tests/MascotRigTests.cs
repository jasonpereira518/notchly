// SPDX-License-Identifier: GPL-3.0-or-later
// The frame-by-frame rig and stage that stand in for Core Animation (upstream UI/Notch/NotchMascotView.swift has no
// tests of its own): blinks, dozing, morphs, reactions, petting, freshness, visits and cameos, the restyle crossfade
// and the robot's bulb, on a controlled clock and a controlled random source.
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;

namespace Notchly.Core.Tests;

public class MascotRigTests
{
    private sealed class Clock
    {
        public double Now = 100;
    }

    private static Func<double> Randoms(params double[] values)
    {
        var index = 0;
        return () => values[index++ % values.Length];
    }

    private static double Width(PointD[] eye) => eye.Max(p => p.X) - eye.Min(p => p.X);

    private static MascotRig Rig(Clock clock, Func<double>? random = null, int hour = 14)
    {
        var rig = new MascotRig(() => clock.Now, random ?? Randoms(0.5), () => hour);
        rig.Configure(MascotLook.Standard, 20);
        return rig;
    }

    private static MascotTrack Notch() => MascotSupport.Track(268, 32, 44, 180, floats: false, bodyHeight: 32);

    [Fact]
    public void BlinksComeEveryFewSecondsAndALongRestMakesItDoze()
    {
        var clock = new Clock();
        var rig = Rig(clock);
        rig.SetIdles(true, visible: true);
        // 2.6–6.4 s: halfway is 4.5 s, and a single blink lasts 0.17 s.
        Assert.Equal(100 + 4.5 + 0.17, rig.NextBlinkEnds!.Value, 9);
        clock.Now = 100 + 4.5 + 0.17 * 0.45;
        var frame = rig.Frame()!;
        Assert.Equal(0.1, frame.LeftEyeScale.Y, 6);
        Assert.Equal(0.1, frame.RightEyeScale.Y, 6);
        for (var blink = 1; blink <= MascotSupport.BlinksBeforeSleep(14); blink++)
        {
            Assert.Equal(MascotMood.Idle, rig.Mood);
            clock.Now = rig.NextBlinkEnds!.Value;
            rig.Advance();
        }
        Assert.Equal(MascotMood.Sleepy, rig.Mood);
        Assert.True(rig.IsDozing);
        Assert.Null(rig.NextBlinkEnds);
        rig.Wake(animated: true);
        Assert.Equal(MascotMood.Idle, rig.Mood);
        Assert.False(rig.IsDozing);
        Assert.NotNull(rig.NextBlinkEnds);
    }

    [Fact]
    public void LateAtNightItDozesAfterTwelveBlinks()
    {
        var clock = new Clock();
        var rig = Rig(clock, hour: 23);
        rig.SetIdles(true, visible: true);
        for (var blink = 1; blink <= 12; blink++)
        {
            Assert.False(rig.IsDozing);
            clock.Now = rig.NextBlinkEnds!.Value;
            rig.Advance();
        }
        Assert.True(rig.IsDozing);
    }

    [Fact]
    public void BatterySaverHalvesBlinksAndSomeBlinksComeTwiceOrWithAGlance()
    {
        var clock = new Clock();
        var saver = new MascotRig(() => clock.Now, Randoms(0.5), () => 14) { LowPower = true };
        saver.Configure(MascotLook.Standard, 20);
        saver.SetIdles(true, visible: true);
        Assert.Equal(100 + 5.2 + 7.6 * 0.5 + 0.17, saver.NextBlinkEnds!.Value, 9);

        var twice = Rig(clock, Randoms(0.1));
        twice.SetIdles(true, visible: true);
        Assert.Equal(100 + 2.6 + 3.8 * 0.1 + 0.44, twice.NextBlinkEnds!.Value, 9);

        // The delay, not twice, a glance, to the left.
        var glancing = Rig(clock, Randoms(0.5, 0.5, 0.1, 0.7));
        glancing.SetIdles(true, visible: true);
        clock.Now = 100 + 4.5 - 1.1 + 1.3 * 0.5;
        Assert.Equal(-20 * 0.12, glancing.Frame()!.EyeShift.X, 6);
        // Following the pointer, its eyes do not wander.
        var following = Rig(clock, Randoms(0.5, 0.5, 0.1, 0.7));
        following.Attend(new PointD(0.05, 0));
        following.SetIdles(true, visible: true);
        clock.Now += 4.5 - 1.1 + 1.3 * 0.5;
        Assert.Equal(20 * 0.05, following.Frame()!.EyeShift.X, 6);
    }

    [Fact]
    public void AFaceMorphsInAFifthOfASecondAndSurpriseComesWithAPop()
    {
        var clock = new Clock();
        var rig = Rig(clock);
        Assert.Equal(2, Width(rig.Frame()!.LeftEye), 1);
        rig.SetMood(MascotMood.Surprised, animated: true);
        clock.Now = 100 + 0.35 * 0.34;
        var popping = rig.Frame()!;
        Assert.Equal(-0.14, popping.Squash, 6);
        var midway = Width(popping.LeftEye);
        Assert.True(midway > 2.05 && midway < 3.15, $"midway {midway}");
        clock.Now = 100.22;
        Assert.Equal(3.2, Width(rig.Frame()!.LeftEye), 1);
    }

    [Fact]
    public void AReactionHopsAndWithReducedMotionOnlyTheFaceChanges()
    {
        var clock = new Clock();
        var rig = Rig(clock);
        rig.React(MascotReaction.Celebrate, 4.4);
        clock.Now = 100 + 0.32 * 0.62;
        Assert.Equal(-4.4, rig.Frame()!.Hop, 6);

        clock.Now = 200;
        var still = Rig(clock);
        still.ReduceMotion = true;
        still.React(MascotReaction.Celebrate, 4.4);
        for (var t = 0.0; t <= 1.2; t += 0.05)
        {
            clock.Now = 200 + t;
            var frame = still.Frame()!;
            Assert.Equal(0, frame.Hop);
            Assert.Equal(0, frame.Squash);
            // Happy eyes, a band 2.1 open eyes wide, held from a moment in until a moment before the end.
            if (t is > 0.2 and < 0.8) Assert.Equal(4.2, Width(frame.LeftEye), 0);
        }
        clock.Now = 201.5;
        Assert.Equal(2, Width(still.Frame()!.LeftEye), 1);
    }

    [Fact]
    public void RestingThePointerOnItForASecondBringsTheHeartsOnceInAWhile()
    {
        var clock = new Clock();
        var stage = new MascotStage(() => clock.Now, Randoms(0.5), () => 14) { Attached = true };
        var track = Notch();
        stage.ShowOnTrack(MascotLook.Standard, track, rests: true, visit: null, MascotMood.Idle, reaction: null,
                          reduceMotion: false);
        var on = new PointD(track.Rest, track.Baseline);
        stage.PointerEntered(on);
        stage.PointerMoved(on);
        Assert.True(stage.IsBeingPetted);
        clock.Now += 1;
        stage.Frame();
        Assert.False(stage.IsBeingPetted);
        clock.Now += 0.21;
        // Heart eyes are twice an open eye's width.
        Assert.Equal(4, Width(stage.Frame()!.LeftEye), 0);
        clock.Now += 5;
        stage.PointerMoved(on);
        Assert.False(stage.IsBeingPetted);
        clock.Now += MascotSupport.PettingInterval;
        stage.PointerMoved(on);
        Assert.True(stage.IsBeingPetted);
        stage.PointerMoved(new PointD(track.Rest + 20, track.Baseline));
        Assert.False(stage.IsBeingPetted);
    }

    [Fact]
    public void AReactionPlaysOnceAndOnlyWhileFresh()
    {
        var clock = new Clock();
        var stage = new MascotStage(() => clock.Now, Randoms(0.5)) { Attached = true };
        var track = Notch();
        var stale = MascotReactionEvent.Now(MascotReaction.Celebrate, clock.Now - 2);
        stage.ShowOnTrack(MascotLook.Standard, track, true, null, MascotMood.Idle, stale, false);
        clock.Now += 0.2;
        Assert.Equal(0, stage.Frame()!.Hop);
        var fresh = MascotReactionEvent.Now(MascotReaction.Celebrate, clock.Now);
        stage.ShowOnTrack(MascotLook.Standard, track, true, null, MascotMood.Idle, fresh, false);
        clock.Now += 0.2;
        Assert.True(stage.Frame()!.Hop < 0);
        clock.Now += 2;
        stage.ShowOnTrack(MascotLook.Standard, track, true, null, MascotMood.Idle, fresh, false);
        clock.Now += 0.2;
        Assert.Equal(0, stage.Frame()!.Hop);
    }

    [Fact]
    public void AReactionPublishedBeforeTheViewIsOnScreenWaitsForIt()
    {
        var clock = new Clock();
        var stage = new MascotStage(() => clock.Now, Randoms(0.5));
        stage.ShowAlone(MascotLook.Standard, 18, MascotMood.Idle, idles: false, reduceMotion: false,
                        reaction: MascotReaction.Celebrate, first: true);
        clock.Now += 0.2;
        Assert.Equal(0, stage.Frame()!.Hop);
        stage.Attached = true;
        clock.Now += 0.62 * 0.32;
        Assert.Equal(-18 * 0.32, stage.Frame()!.Hop, 6);
    }

    [Fact]
    public void AVisitIsPlayedFromItsPathAndACameoReactsAsItLands()
    {
        var clock = new Clock();
        var stage = new MascotStage(() => clock.Now, Randoms(0.5)) { Attached = true };
        var track = Notch();
        var shift = MascotGeometry.FigureOffset(MascotLook.Standard) * track.Size;
        var lap = MascotVisit.Now(MascotVisitKind.Lap, MascotMood.Wink, clock.Now);
        stage.ShowOnTrack(MascotLook.Standard, track, true, lap, MascotMood.Idle, null, false);
        var path = MascotMotion.Path(MascotVisitKind.Lap, track);
        clock.Now += 1.8;
        var frame = stage.Frame()!;
        var sample = path.Sample(1.8);
        Assert.Equal(sample.X, frame.Position.X, 6);
        Assert.Equal(track.Baseline - shift - sample.Lift, frame.Position.Y, 6);
        Assert.True(stage.Mascot.IsVisiting);
        clock.Now += 2;
        Assert.False(stage.Mascot.IsVisiting);
        var settled = stage.Frame()!;
        Assert.Equal(track.Rest, settled.Position.X, 6);
        // Nothing moves it now: the box's centre is where it stands.
        var centre = settled.Transform.Transform(new PointD(10, 10));
        Assert.Equal(settled.Position.X, centre.X, 9);
        Assert.Equal(settled.Position.Y, centre.Y, 9);

        var cameo = MascotVisit.Now(MascotVisitKind.Cameo(MascotReaction.Celebrate), MascotMood.Idle, clock.Now);
        stage.ShowOnTrack(MascotLook.Standard, track, false, cameo, MascotMood.Idle, null, false);
        clock.Now += MascotMotion.CameoArrival;
        stage.Frame();
        clock.Now += 0.62 * 0.32;
        Assert.Equal(-track.Hop(0.22), stage.Frame()!.Hop, 6);
    }

    [Fact]
    public void ANewLookCrossfadesInAndTheRobotsBulbDimsAsItSleeps()
    {
        var clock = new Clock();
        var robot = new MascotLook(MascotStyle.Robot, MascotShape.Ball, MascotPalette.Mint);
        var rig = Rig(clock);
        rig.Configure(robot, 20);
        var fading = rig.Frame()!;
        Assert.Equal(MascotLook.Standard, fading.PreviousLook?.Look);
        Assert.Equal(0, fading.LookFade, 6);
        Assert.NotNull(fading.Parts.Robot);
        Assert.Null(fading.Parts.Shine);
        Assert.Equal(MascotPalette.Mint.Light(), fading.EyeColor);
        Assert.Equal(0.9, fading.EyeGlowOpacity);
        clock.Now += 0.24;
        Assert.Null(rig.Frame()!.PreviousLook);
        rig.SetMood(MascotMood.Sleepy, animated: true);
        clock.Now += 0.6;
        Assert.Equal(MascotPalette.Mint.Shade(), rig.Frame()!.BulbColor);
        rig.SetMood(MascotMood.Idle, animated: true);
        clock.Now += 0.25;
        Assert.Equal(MascotPalette.Mint.Light(), rig.Frame()!.BulbColor);
        rig.React(MascotReaction.Perk, 4);
        clock.Now += 0.5;
        Assert.Equal(1, rig.Frame()!.BulbGlowOpacity, 6);
    }
}
