// SPDX-License-Identifier: GPL-3.0-or-later
// Windows additions to the companion (upstream has none of these): its eyes following the cursor anywhere on the
// screen, the props that float beside it, the reactions made distinct from one another, and the new moments that
// bring them. On a controlled clock, as MascotRigTests.
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;

namespace Notchly.Core.Tests;

public class MascotReactionsTests
{
    private sealed class Clock
    {
        public double Now = 100;
    }

    private static MascotRig Rig(Clock clock, double outward = -1)
    {
        var rig = new MascotRig(() => clock.Now, () => 0.5, () => 14) { Outward = outward };
        rig.Configure(MascotLook.Standard, 20);
        return rig;
    }

    private static MascotTrack Notch() => MascotSupport.Track(268, 32, 44, 180, floats: false, bodyHeight: 32);

    private static IEnumerable<MascotReaction> AllReactions => Enum.GetValues<MascotReaction>();

    // MARK: Eyes on the cursor

    [Fact]
    public void ACursorFarOffGivesTheFullGazeAndNothingBeyondIt()
    {
        var center = new PointD(100, 16);
        var far = MascotSupport.PointerGaze(center, new PointD(5000, 4000), 20);
        Assert.Equal(MascotSupport.GazeAcross, far.X, 3);
        Assert.Equal(MascotSupport.GazeDown, far.Y, 3);
        var farther = MascotSupport.PointerGaze(center, new PointD(50000, 40000), 20);
        Assert.Equal(far.X, farther.X, 3);
        var up = MascotSupport.PointerGaze(center, new PointD(100, -3000), 20);
        Assert.True(up.Y < 0 && Math.Abs(up.Y) <= MascotSupport.GazeDown);
        // Closer, a little less; mirrored, the other way.
        var near = MascotSupport.PointerGaze(center, new PointD(130, 16), 20);
        Assert.InRange(near.X, 0.01, far.X);
        Assert.Equal(-near.X, MascotSupport.PointerGaze(center, new PointD(70, 16), 20).X, 9);
        Assert.Equal(new PointD(0, 0), MascotSupport.PointerGaze(center, new PointD(900, 900), 0));
    }

    [Fact]
    public void TheCursorMovingMakesItFollowAndKeepingStillMakesItLoseInterest()
    {
        var tracker = new MascotGazeTracker();
        Assert.Equal(MascotGazeStep.Follow, tracker.Update(new PointD(500, 400), 0));
        // A twitch under the threshold changes nothing.
        Assert.Equal(MascotGazeStep.None, tracker.Update(new PointD(501, 400), 1));
        Assert.Equal(MascotGazeStep.Follow, tracker.Update(new PointD(520, 400), 2));
        Assert.Equal(MascotGazeStep.None, tracker.Update(new PointD(520, 400), 2 + MascotSupport.GazeStill - 0.1));
        Assert.Equal(MascotGazeStep.Lose, tracker.Update(new PointD(520, 400), 2 + MascotSupport.GazeStill));
        // Once lost, still reports nothing more; moving again follows again.
        Assert.Equal(MascotGazeStep.None, tracker.Update(new PointD(520, 400), 60));
        Assert.Equal(MascotGazeStep.Follow, tracker.Update(new PointD(900, 100), 61));
    }

    [Fact]
    public void ResettingTheTrackerStartsItAfresh()
    {
        var tracker = new MascotGazeTracker();
        tracker.Update(new PointD(10, 10), 0);
        tracker.Reset();
        Assert.Equal(MascotGazeStep.Follow, tracker.Update(new PointD(10, 10), 1));
    }

    [Fact]
    public void FollowingAStillCursorLetsTheViewRestAndAMovingOneTurnsAndLeansTheHead()
    {
        var clock = new Clock();
        var rig = Rig(clock);
        var target = new PointD(0.15, 0.1);
        rig.Follow(target);
        clock.Now += 0.3;
        var frame = rig.Frame()!;
        Assert.Equal(20 * 0.15, frame.EyeShift.X, 6);
        Assert.Equal(20 * 0.1, frame.EyeShift.Y, 6);
        // The head turns toward it a little: leaning, and shifted.
        Assert.True(Math.Abs(frame.Transform.M12) > 0.01);
        Assert.True(frame.Transform.OffsetX - Rig(clock).Frame()!.Transform.OffsetX > 0.4);
        // Told the same again, nothing starts moving, so there is nothing to draw.
        Assert.Null(rig.NextChange());
        rig.Follow(new PointD(0.15 + 0.005, 0.1));
        Assert.Null(rig.NextChange());
        // A real change starts it moving again.
        rig.Follow(new PointD(-0.1, 0));
        Assert.NotNull(rig.NextChange());
        // Looking ahead brings the eyes and the head back.
        rig.LookAhead();
        clock.Now += 0.5;
        var back = rig.Frame()!;
        Assert.Equal(0, back.EyeShift.X, 6);
        Assert.Equal(0, back.Transform.M12, 6);
    }

    [Fact]
    public void ItDoesNotFollowWithoutMotionAndTheStageOnlyFollowsAnAwakeRestingCompanion()
    {
        var clock = new Clock();
        var still = Rig(clock);
        still.ReduceMotion = true;
        still.Follow(new PointD(0.15, 0.1));
        Assert.Equal(0, still.Frame()!.EyeShift.X, 6);

        var stage = new MascotStage(() => clock.Now, () => 0.5, () => 14) { Attached = true };
        stage.ShowOnTrack(MascotLook.Standard, Notch(), rests: true, visit: null, MascotMood.Idle, null, reduceMotion: false);
        stage.GazeAt(new PointD(2000, 16));
        clock.Now += 0.5;
        Assert.True(stage.Frame()!.EyeShift.X > 1);
        stage.GazeLost();
        clock.Now += 0.8;
        Assert.Equal(0, stage.Frame()!.EyeShift.X, 6);

        // Switched off (asleep), it looks at nothing.
        var asleep = new MascotStage(() => clock.Now, () => 0.5, () => 14) { Attached = true };
        asleep.ShowOnTrack(MascotLook.Standard, Notch(), rests: true, visit: null, MascotMood.Idle, null, reduceMotion: false, awake: false);
        asleep.GazeAt(new PointD(2000, 16));
        clock.Now += 0.5;
        Assert.Equal(0, asleep.Frame()!.EyeShift.X, 6);
    }

    [Fact]
    public void LeavingTheIslandDropsTheGazeUnlessTheCursorIsFollowedEverywhere()
    {
        var clock = new Clock();
        MascotStage Stage(bool global)
        {
            var stage = new MascotStage(() => clock.Now, () => 0.5, () => 14) { Attached = true, GlobalGaze = global };
            stage.ShowOnTrack(MascotLook.Standard, Notch(), rests: true, visit: null, MascotMood.Idle, null, reduceMotion: false);
            stage.PointerEntered(new PointD(2000, 16));
            clock.Now += 0.5;
            Assert.True(stage.Frame()!.EyeShift.X > 1);
            stage.PointerExited();
            return stage;
        }
        var local = Stage(false);
        var everywhere = Stage(true);
        clock.Now += 1;
        Assert.Equal(0, local.Frame()!.EyeShift.X, 6);
        Assert.True(everywhere.Frame()!.EyeShift.X > 1);
    }

    // MARK: Props

    [Fact]
    public void APropPopsInDriftsAndFadesOverItsLife()
    {
        var prop = new MascotProp(MascotPropKind.Heart, 10, 1, new PointD(0.5, 0), new PointD(0.9, -0.4), 0.4);
        Assert.Null(prop.Sample(9.99, 20, -1));
        Assert.Null(prop.Sample(11, 20, -1));
        var start = prop.Sample(10.001, 20, -1)!.Value;
        Assert.True(start.Size < 1 && start.Opacity < 0.1);
        var middle = prop.Sample(10.4, 20, -1)!.Value;
        Assert.Equal(1, middle.Opacity, 6);
        Assert.Equal(0.4 * 20, middle.Size, 6);
        // Away from the camera: the left of a left-resting companion, the right of a mirrored one.
        Assert.True(middle.Offset.X < 0);
        Assert.Equal(-middle.Offset.X, prop.Sample(10.4, 20, 1)!.Value.Offset.X, 9);
        Assert.True(middle.Offset.Y < 0);
        var late = prop.Sample(10.95, 20, -1)!.Value;
        Assert.True(late.Opacity < 0.2);
    }

    [Fact]
    public void ARingSpreadsAndASparkTurns()
    {
        var ring = new MascotProp(MascotPropKind.Ring, 0, 1, new PointD(0, 0), new PointD(0, 0), 0.7, 1.6);
        Assert.True(ring.Sample(0.6, 20, -1)!.Value.Size > ring.Sample(0.3, 20, -1)!.Value.Size);
        var spark = new MascotProp(MascotPropKind.Spark, 0, 1, new PointD(0, 0), new PointD(0, 0), 0.4, 1, 1);
        Assert.Equal(0.5, spark.Sample(0.5, 20, -1)!.Value.Rotation, 9);
    }

    [Fact]
    public void EveryPropHasAnOutlineInsideItsSquareAndItsOwnColour()
    {
        foreach (var kind in Enum.GetValues<MascotPropKind>())
        {
            var path = MascotPropShapes.Unit(kind);
            Assert.NotEmpty(path.Figures);
            var bounds = path.Bounds;
            Assert.True(bounds.Width > 0.1 && bounds.Height > 0.1, kind.ToString());
            Assert.InRange(bounds.X, -0.75, 0.75);
            Assert.InRange(bounds.Right, -0.75, 0.75);
            Assert.InRange(bounds.Y, -0.75, 0.75);
            Assert.InRange(bounds.Bottom, -0.75, 0.75);
            Assert.True(MascotPropShapes.Color(kind).Alpha > 0.5);
        }
        // A bang is filled where its stem is and open between the stem and the dot.
        var bang = MascotPropShapes.Unit(MascotPropKind.Bang);
        Assert.True(bang.Contains(new PointD(0, -0.2)));
        Assert.False(bang.Contains(new PointD(0.4, 0)));
    }

    // MARK: Reactions

    private static string Signature(MascotReaction reaction)
    {
        var clock = new Clock();
        var rig = Rig(clock);
        rig.React(reaction, 5);
        var parts = new List<string>();
        for (var step = 0; step <= 28; step++)
        {
            var frame = rig.Frame()!;
            parts.Add(string.Join(',',
                Math.Round(frame.Hop, 1), Math.Round(frame.Squash, 2), Math.Round(frame.Transform.M12, 2),
                Math.Round(frame.Transform.OffsetX, 1), Math.Round(frame.LeftEyeScale.Y, 1),
                Math.Round(frame.LeftEye.Max(p => p.Y) - frame.LeftEye.Min(p => p.Y), 1),
                Math.Round(frame.LeftEye.Max(p => p.X) - frame.LeftEye.Min(p => p.X), 1),
                string.Join('+', frame.Props.Select(p => p.Kind))));
            clock.Now += 0.05;
        }
        return string.Join('|', parts);
    }

    [Fact]
    public void EveryReactionLooksDifferentFromEveryOther()
    {
        var seen = new Dictionary<string, MascotReaction>();
        foreach (var reaction in AllReactions)
        {
            var signature = Signature(reaction);
            Assert.False(seen.TryGetValue(signature, out var twin), $"{reaction} plays exactly as {twin}");
            seen[signature] = reaction;
        }
    }

    [Fact]
    public void EveryReactionButTheFlashAndTheDimShowsItsOwnProp()
    {
        var kinds = new Dictionary<MascotReaction, HashSet<MascotPropKind>>();
        foreach (var reaction in AllReactions)
        {
            var clock = new Clock();
            var rig = Rig(clock);
            rig.React(reaction, 5);
            kinds[reaction] = rig.Props.Select(p => p.Kind).ToHashSet();
        }
        foreach (var (reaction, set) in kinds)
        {
            if (reaction is MascotReaction.Flash or MascotReaction.Dim) Assert.Empty(set);
            else Assert.NotEmpty(set);
        }
        // Two reactions never share the whole set of props they bring.
        var withProps = kinds.Where(k => k.Value.Count > 0).ToList();
        foreach (var a in withProps)
            foreach (var b in withProps.Where(b => a.Key < b.Key))
                Assert.False(a.Value.SetEquals(b.Value), $"{a.Key} and {b.Key} bring the same props");
    }

    [Fact]
    public void NoPropOutlastsItsReactionAndNothingStaysOnScreen()
    {
        foreach (var reaction in AllReactions)
        {
            var clock = new Clock();
            var rig = Rig(clock);
            rig.React(reaction, 5);
            foreach (var prop in rig.Props)
                Assert.True(prop.End <= clock.Now + reaction.Length() + 1e-9, $"{reaction}: {prop.Kind} ends at {prop.End - clock.Now}");
            clock.Now += reaction.Length() + 0.01;
            Assert.Empty(rig.Frame()!.Props);
            Assert.Empty(rig.Props);
        }
    }

    [Fact]
    public void WithoutMotionItOnlyChangesFaceAndDrawsNoProps()
    {
        foreach (var reaction in AllReactions)
        {
            var clock = new Clock();
            var rig = Rig(clock);
            rig.ReduceMotion = true;
            rig.React(reaction, 5);
            Assert.Empty(rig.Props);
            var frame = rig.Frame()!;
            Assert.Empty(frame.Props);
            Assert.Equal(0, frame.Hop, 9);
        }
    }

    [Fact]
    public void ThePropsStandOnTheSideAwayFromTheCamera()
    {
        foreach (var outward in new[] { -1.0, 1.0 })
        {
            var clock = new Clock();
            var rig = Rig(clock, outward);
            rig.React(MascotReaction.Love, 5);
            clock.Now += 0.9;
            var props = rig.Frame()!.Props;
            Assert.NotEmpty(props);
            Assert.All(props, p => Assert.True(p.Offset.X * outward > 0));
        }
        // On a track the stage takes the side from the track itself.
        var left = new Clock();
        var stage = new MascotStage(() => left.Now, () => 0.5, () => 14) { Attached = true };
        var mirrored = MascotSupport.Track(268, 32, 44, 180, floats: false, bodyHeight: 32, MascotSide.Right);
        stage.ShowOnTrack(MascotLook.Standard, mirrored, rests: true, visit: null, MascotMood.Idle, null, reduceMotion: false);
        Assert.Equal(1, stage.Mascot.Outward);
        stage.ShowOnTrack(MascotLook.Standard, Notch(), rests: true, visit: null, MascotMood.Idle, null, reduceMotion: false);
        Assert.Equal(-1, stage.Mascot.Outward);
    }

    [Fact]
    public void ABigHopTurnsOverAndTheWinceSqueezesTheEyes()
    {
        var clock = new Clock();
        var spin = Rig(clock);
        spin.React(MascotReaction.Celebrate, 5);
        double most = 0;
        for (var step = 0; step < 20; step++)
        {
            clock.Now += 0.04;
            most = Math.Max(most, Math.Abs(spin.Frame()!.Transform.M12));
        }
        // Somewhere in the turn it is on its side.
        Assert.True(most > 0.9);

        var wince = Rig(clock);
        wince.React(MascotReaction.Loud, 5);
        clock.Now += 0.4;
        var frame = wince.Frame()!;
        var open = Rig(clock).Frame()!;
        Assert.True(frame.LeftEye.Max(p => p.Y) - frame.LeftEye.Min(p => p.Y) < open.LeftEye.Max(p => p.Y) - open.LeftEye.Min(p => p.Y));
    }

    [Fact]
    public void EveryMomentBringsAReactionExceptAVisit()
    {
        foreach (var moment in Enum.GetValues<MascotMoment>())
        {
            if (moment == MascotMoment.Visit) Assert.Null(moment.Reaction());
            else Assert.NotNull(moment.Reaction());
        }
        Assert.Equal(MascotReaction.Loud, MascotMoment.LoudVolume.Reaction());
        Assert.Equal(MascotReaction.HeadphonesIn, MascotMoment.HeadphonesIn.Reaction());
        Assert.Equal(MascotReaction.WakeUp, MascotMoment.Unlocked.Reaction());
        foreach (var reaction in AllReactions) Assert.True(reaction.Length() > 0);
    }

    // MARK: What brings them

    [Fact]
    public void TheVolumeFlinchesOnlyAsItReachesTheTop()
    {
        Assert.Equal(MascotReaction.Loud, MascotSupport.VolumeReaction(0.5, 0.95, muted: false));
        Assert.Equal(MascotReaction.Loud, MascotSupport.VolumeReaction(0.89, 0.9, muted: false));
        Assert.Null(MascotSupport.VolumeReaction(0.92, 0.96, muted: false)); // Already up there.
        Assert.Null(MascotSupport.VolumeReaction(0.3, 0.6, muted: false));
        Assert.Null(MascotSupport.VolumeReaction(0.5, 0.95, muted: true));
    }

    [Fact]
    public void TheScreenDazzlesAtFullBrightnessAndDimsNearlyDark()
    {
        Assert.Equal(MascotReaction.Bright, MascotSupport.BrightnessReaction(70, 100));
        Assert.Null(MascotSupport.BrightnessReaction(95, 100));
        Assert.Equal(MascotReaction.Dim, MascotSupport.BrightnessReaction(40, 5));
        Assert.Null(MascotSupport.BrightnessReaction(5, 2));
        Assert.Null(MascotSupport.BrightnessReaction(40, 60));
    }

    [Fact]
    public void MusicStoppingOrSkippingIsNoticedButStartingIsNotThisOnesJob()
    {
        Assert.Equal(MascotSupport.MusicPaused, MascotSupport.MusicChange(true, false, "a", "a"));
        Assert.Equal(MascotSupport.MusicPaused, MascotSupport.MusicChange(true, false, "a", null));
        Assert.Equal(MascotSupport.MusicSkipped, MascotSupport.MusicChange(true, true, "a", "b"));
        Assert.Null(MascotSupport.MusicChange(true, true, "a", "a"));
        Assert.Null(MascotSupport.MusicChange(false, true, null, "a"));
        Assert.Null(MascotSupport.MusicChange(null, true, null, "a")); // Playing when the app opened.
        Assert.Null(MascotSupport.MusicChange(true, true, null, "b"));
    }

    [Fact]
    public void HeadphonesGetTheirOwnReactionAndNoOtherAccessoryLeavingDoes()
    {
        foreach (var kind in new[] { AccessoryKind.Headphones, AccessoryKind.AirPods, AccessoryKind.AirPodsPro, AccessoryKind.AirPodsMax })
        {
            Assert.Equal(MascotSupport.HeadphonesIn, MascotSupport.AccessoryConnectedFor(kind));
            Assert.Equal(MascotSupport.HeadphonesOut, MascotSupport.AccessoryDisconnectedFor(kind));
        }
        Assert.Equal(MascotSupport.AccessoryConnected, MascotSupport.AccessoryConnectedFor(AccessoryKind.Keyboard));
        Assert.Null(MascotSupport.AccessoryDisconnectedFor(AccessoryKind.Mouse));
        Assert.Null(MascotSupport.AccessoryDisconnectedFor(null));
    }

    [Fact]
    public void AMomentIsOfNoUseLate()
    {
        Assert.True(MascotSupport.Notified.Patience <= MascotReactionGate.Patience);
        Assert.True(MascotSupport.MusicPaused.Patience < MascotReactionGate.Patience);
    }
}
