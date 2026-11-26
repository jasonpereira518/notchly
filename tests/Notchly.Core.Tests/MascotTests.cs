// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/NotchMascotTests.swift (preferences, faces, outlines, tracks, strolls, homecoming, cameo,
// farewell, cross, arrive, linger, hiding paths, preview, countdown, activity tracks, reactions, sides, calendar and
// centring) with the same numbers. Not ported: the Command Bar's face and droplet, and the localized strings (they
// come with the Settings page). Added: the frame-by-frame rig and stage that stand in for Core Animation.
using Notchly.Core.Calendar;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class MascotTests
{
    private static MascotTrack Notch(MascotSide side = MascotSide.Left) =>
        MascotSupport.Track(268, 32, 44, 180, floats: false, bodyHeight: 32, side);

    private static MascotTrack Capsule(MascotSide side = MascotSide.Left) =>
        MascotSupport.Track(76, 24, 0, 0, floats: true, bodyHeight: 20, side);

    private static bool Behind(MascotTrack track, double x) =>
        track.Hidden is { } hidden && x - track.Size / 2 >= hidden.Lower && x + track.Size / 2 <= hidden.Upper;

    private static void AssertEvenRun(MascotVisitPath path)
    {
        Assert.Single(new[] { path.KeyTimes.Count, path.X.Count, path.Lift.Count, path.Squash.Count, path.Gaze.Count }.Distinct());
        Assert.Equal(0, path.KeyTimes[0]);
        Assert.Equal(1, path.KeyTimes[^1]);
        Assert.True(path.KeyTimes.Zip(path.KeyTimes.Skip(1)).All(pair => pair.First <= pair.Second));
    }

    private static void AssertClearOfTheTop(MascotVisitPath path, MascotTrack track) =>
        Assert.True(path.Lift.All(lift => track.Baseline - lift - track.Size / 2 >= track.Ceiling + 1));

    private static IEnumerable<int> Frames(MascotVisitPath path, Func<double, bool> at) =>
        Enumerable.Range(0, path.KeyTimes.Count).Where(i => at(path.KeyTimes[i] * path.Duration));

    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    private static bool CanInstall => FeatureCatalog.Info(AppFeature.NotchMascot).CanInstall;

    // MARK: Preferences

    [Fact]
    public void TheCompanionIsOptInAndInstalledFromTheFeaturesPage()
    {
        var store = NewStore();
        store.Set(FeatureCatalog.Info(AppFeature.Notch).AvailabilityKey, true);
        store.Set(DefaultsKey.NotchEnabled, true);
        Assert.False(MascotSupport.IsEnabled(store));
        Assert.False(MascotSupport.Visits(store));
        store.Set(DefaultsKey.NotchMascotEnabled, true);
        // Switched on but not installed on the Features page, it stays away.
        Assert.False(MascotSupport.IsEnabled(store));
        Assert.False(MascotSupport.Reacts(store));
        store.Set(DefaultsKey.NotchMascotEnabled, false);
        store.Set(FeatureCatalog.Info(AppFeature.NotchMascot).AvailabilityKey, true);
        Assert.False(MascotSupport.IsEnabled(store));
        Assert.Equal(MascotLook.Standard, MascotSupport.Look(store));
        Assert.Equal(new MascotLook(MascotStyle.Minimal, MascotShape.Ball, MascotPalette.Pearl), MascotLook.Standard);

        // Installed and on, it visits and reacts — once the catalog lets it install.
        store.Set(DefaultsKey.NotchMascotEnabled, true);
        Assert.Equal(CanInstall, MascotSupport.IsEnabled(store));
        Assert.Equal(CanInstall, MascotSupport.Visits(store));
        Assert.Equal(CanInstall, MascotSupport.Reacts(store));
        Assert.False(MascotSupport.HidesWhenIdle(store)); // on Windows it shows at rest by default
        store.Set(DefaultsKey.NotchMascotReactions, false);
        Assert.False(MascotSupport.Reacts(store));
        Assert.Equal(CanInstall, MascotSupport.Visits(store));
        store.Set(DefaultsKey.NotchMascotReactions, true);
        store.Set(DefaultsKey.NotchMascotVisits, false);
        Assert.False(MascotSupport.Visits(store));
        Assert.Equal(CanInstall, MascotSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchMascotVisits, true);
        store.Set(DefaultsKey.NotchEnabled, false);
        Assert.False(MascotSupport.IsEnabled(store));
        Assert.False(MascotSupport.Visits(store));
        store.Set(DefaultsKey.NotchEnabled, true);
        store.Set(FeatureCatalog.Info(AppFeature.NotchMascot).AvailabilityKey, false);
        Assert.False(MascotSupport.IsEnabled(store));
        Assert.False(MascotSupport.HidesWhenIdle(store));
    }

    [Fact]
    public void StyleShapeAndColourAreReadAsChosenAndUnknownOnesFallBack()
    {
        var store = NewStore();
        store.Set(DefaultsKey.NotchMascotStyle, "robot");
        store.Set(DefaultsKey.NotchMascotShape, "pill");
        store.Set(DefaultsKey.NotchMascotPalette, "lilac");
        Assert.Equal(new MascotLook(MascotStyle.Robot, MascotShape.Pill, MascotPalette.Lilac), MascotSupport.Look(store));
        store.Set(DefaultsKey.NotchMascotShape, "squircle");
        Assert.Equal(MascotShape.Squircle, MascotSupport.Look(store).Shape);
        store.Set(DefaultsKey.NotchMascotShape, "cube");
        store.Set(DefaultsKey.NotchMascotPalette, "neon");
        store.Set(DefaultsKey.NotchMascotStyle, "dragon");
        Assert.Equal(MascotLook.Standard, MascotSupport.Look(store));
        store.Set(DefaultsKey.NotchMascotShape, "1");
        Assert.Equal(MascotShape.Ball, MascotSupport.Look(store).Shape);

        Assert.Equal(MascotVisitFrequency.Frequent, MascotSupport.VisitFrequency(store));
        Assert.Equal(MascotSide.Left, MascotSupport.Side(store));
        store.Set(DefaultsKey.NotchMascotVisitFrequency, "rare");
        store.Set(DefaultsKey.NotchMascotSide, "right");
        Assert.Equal(MascotVisitFrequency.Rare, MascotSupport.VisitFrequency(store));
        Assert.Equal(MascotSide.Right, MascotSupport.Side(store));
        store.Set(DefaultsKey.NotchMascotVisitFrequency, "hourly");
        Assert.Equal(MascotVisitFrequency.Normal, MascotSupport.VisitFrequency(store));
    }

    [Fact]
    public void EveryCompanionPreferenceIsRegisteredAndTravelsInABackup()
    {
        string[] keys =
        [
            DefaultsKey.NotchMascotEnabled, DefaultsKey.NotchMascotVisits, DefaultsKey.NotchMascotReactions,
            DefaultsKey.NotchMascotStyle, DefaultsKey.NotchMascotShape, DefaultsKey.NotchMascotPalette,
            DefaultsKey.NotchMascotSide, DefaultsKey.NotchMascotVisitFrequency, DefaultsKey.NotchMascotHidesWhenIdle,
        ];
        var store = NewStore();
        Assert.All(keys, key =>
        {
            Assert.StartsWith("notch", key);
            Assert.True(store.IsRegistered(key));
            Assert.DoesNotContain(key, SettingsBackup.MachineStateKeys);
        });
        Assert.False(store.GetBool(DefaultsKey.NotchMascotEnabled));
        Assert.True(store.GetBool(DefaultsKey.NotchMascotVisits));
        Assert.True(store.GetBool(DefaultsKey.NotchMascotReactions));
        Assert.False(store.GetBool(DefaultsKey.NotchMascotHidesWhenIdle));
        Assert.Equal("minimal", store.GetString(DefaultsKey.NotchMascotStyle));
        Assert.Equal("ball", store.GetString(DefaultsKey.NotchMascotShape));
        Assert.Equal("pearl", store.GetString(DefaultsKey.NotchMascotPalette));
        Assert.Equal("left", store.GetString(DefaultsKey.NotchMascotSide));
        Assert.Equal("frequent", store.GetString(DefaultsKey.NotchMascotVisitFrequency));
        // A saved choice to stay visible survives registering the defaults again.
        store.Set(DefaultsKey.NotchMascotHidesWhenIdle, false);
        store.RegisterDefaults(Defaults.Registered);
        Assert.False(store.GetBool(DefaultsKey.NotchMascotHidesWhenIdle));
    }

    [Fact]
    public void FourShapesAndSevenDistinctColoursWhiteFirst()
    {
        Assert.Equal(4, Enum.GetValues<MascotShape>().Select(MascotSupport.Raw).Distinct().Count());
        var palettes = Enum.GetValues<MascotPalette>();
        Assert.Equal(7, palettes.Select(MascotSupport.Raw).Distinct().Count());
        Assert.Equal(7, palettes.Select(p => p.Light()).Distinct().Count());
        Assert.Equal(7, palettes.Select(p => p.Shade()).Distinct().Count());
        Assert.Equal(MascotPalette.Pearl, palettes[0]);
        Assert.All(new[] { "pearl", "mint", "peach", "lilac", "lemon", "rose", "sky" },
                   name => Assert.NotEqual((MascotPalette)(-1), MascotSupport.Parse(name, (MascotPalette)(-1))));
        Assert.Equal(new MascotColor(0.99, 0.98, 0.96), MascotPalette.Pearl.Light());
        Assert.Equal(new MascotColor(0.40, 0.83, 0.66), MascotPalette.Mint.Shade());
        Assert.Equal(new MascotColor(1, 1, 1, 0.55), MascotColor.Shine);
        Assert.Equal("squircle", MascotSupport.Raw(MascotShape.Squircle));
        Assert.Equal("wakeUp", MascotSupport.Raw(MascotReaction.WakeUp));
    }

    [Fact]
    public void VisitsComeMinutesApartAtTheChosenPace()
    {
        var normal = MascotVisitFrequency.Normal.Delay();
        Assert.Equal(normal.Min, MascotSupport.NextVisitDelay(MascotVisitFrequency.Normal, 0));
        Assert.Equal(normal.Max, MascotSupport.NextVisitDelay(MascotVisitFrequency.Normal, 1));
        Assert.Equal(normal.Max, MascotSupport.NextVisitDelay(MascotVisitFrequency.Normal, 7));
        Assert.All(Enum.GetValues<MascotVisitFrequency>(), f => Assert.True(f.Delay().Min >= 60));
        Assert.True(MascotVisitFrequency.Rare.Delay().Min > normal.Max);
        Assert.True(MascotVisitFrequency.Frequent.Delay().Max < normal.Min);
        Assert.Equal((600.0, 1200.0), MascotVisitFrequency.Rare.Delay());
        Assert.Equal((90.0, 180.0), MascotVisitFrequency.Frequent.Delay());
    }

    [Fact]
    public void ItBlinksLessOnBatterySaverAndDozesSoonerAtNight()
    {
        var saver = MascotSupport.BlinkInterval(lowPower: true);
        var awake = MascotSupport.BlinkInterval(lowPower: false);
        Assert.True(saver.Min >= awake.Min * 2 && saver.Max >= awake.Max * 2);
        Assert.True(MascotSupport.BlinksBeforeSleep(23) < MascotSupport.BlinksBeforeSleep(14));
        Assert.True(MascotSupport.BlinksBeforeSleep(3) < MascotSupport.BlinksBeforeSleep(9));
        Assert.Equal(MascotSupport.BlinksBeforeSleep(6), MascotSupport.BlinksBeforeSleep(21));
        Assert.Equal(36, MascotSupport.BlinksBeforeSleep(12));
        Assert.Equal(12, MascotSupport.BlinksBeforeSleep(22));
    }

    [Fact]
    public void AVisitSaysHelloHappyWithAWinkOrInLove()
    {
        Assert.Equal(MascotMood.Happy, MascotSupport.Greeting(0));
        Assert.Equal(MascotMood.Wink, MascotSupport.Greeting(0.5));
        Assert.Equal(MascotMood.Love, MascotSupport.Greeting(0.99));
        Assert.Equal(MascotMood.Love, MascotSupport.Greeting(3));
    }

    // MARK: Faces

    [Fact]
    public void EveryFaceIsItsOwn()
    {
        Assert.True(MascotMood.Idle.Expression().Blinks && MascotMood.Searching.Expression().Blinks);
        Assert.False(MascotMood.Happy.Expression().Blinks || MascotMood.Love.Expression().Blinks
                     || MascotMood.Sleepy.Expression().Blinks || MascotMood.Wink.Expression().Blinks);
        Assert.NotEqual(MascotMood.Wink.Expression().Left, MascotMood.Wink.Expression().Right);
        Assert.NotEqual(0, MascotMood.Confused.Expression().Tilt);
        Assert.True(MascotMood.Thinking.Expression().Gaze.Y < 0);
        var moods = Enum.GetValues<MascotMood>();
        Assert.Equal(moods.Length, moods.Select(m => m.Expression()).Distinct().Count());
        // Wide awake keeps its eyes open and blinking, looking ahead.
        Assert.Equal(MascotEye.Wide, MascotMood.Alert.Expression().Left);
        Assert.True(MascotMood.Alert.Expression().Blinks);
        Assert.Equal(new PointD(0, 0), MascotMood.Alert.Expression().Gaze);
    }

    // MARK: Outlines

    [Fact]
    public void EveryEyeIsTwentyFourCurvesFromTheTopOfItsMiddleAndTheRightOneMirrors()
    {
        var size = new SizeD(2, 4);
        foreach (var eye in Enum.GetValues<MascotEye>())
        foreach (var right in new[] { false, true })
        {
            var path = MascotGeometry.Eye(eye, size, new PointD(10, 10), right);
            var figure = Assert.Single(path.Figures);
            Assert.True(figure.Closed);
            Assert.Equal(MascotGeometry.EyeCurveCount, figure.Segments.Count);
            Assert.All(figure.Segments, segment => Assert.IsType<MascotCurve>(segment));
            var box = path.Bounds;
            Assert.True(box.X > 10 - size.Width * 1.4 && box.Right < 10 + size.Width * 1.4, $"{eye} stays in its place across");
            Assert.True(box.Y > 10 - size.Height && box.Bottom < 10 + size.Height, $"{eye} stays in its place down");
            var points = MascotGeometry.EyePoints(eye, size, right);
            Assert.Equal(MascotGeometry.EyeCurveCount, points.Length);
            Assert.True(Math.Abs(points[0].X) < 0.05, $"{eye} starts on its middle line");
            Assert.True(points[0].Y <= points.Min(p => p.Y) + size.Height * 0.35, $"{eye} starts at its top");
        }
        var open = MascotGeometry.EyePoints(MascotEye.Open, size, false);
        var left = MascotGeometry.EyePoints(MascotEye.Determined, size, false);
        var mirrored = MascotGeometry.EyePoints(MascotEye.Determined, size, true);
        Assert.True(Math.Abs(open.Max(p => p.X) + open.Min(p => p.X)) < 0.05);
        Assert.True(Math.Abs(left.Max(p => p.X) + mirrored.Min(p => p.X)) < 0.05);
    }

    [Fact]
    public void AnEyeMorphsIntoAnotherPointByPoint()
    {
        var size = new SizeD(2, 4);
        var open = MascotGeometry.EyePoints(MascotEye.Open, size, false);
        var heart = MascotGeometry.EyePoints(MascotEye.Heart, size, false);
        Assert.Equal(open, MascotGeometry.Morph(open, heart, 0));
        Assert.True(MascotGeometry.Morph(open, heart, 1).Zip(heart).All(p =>
            Math.Abs(p.First.X - p.Second.X) < 1e-12 && Math.Abs(p.First.Y - p.Second.Y) < 1e-12));
        var half = MascotGeometry.Morph(open, heart, 0.5);
        Assert.Equal((open[3].X + heart[3].X) / 2, half[3].X, 9);
        // The smooth closed curve passes through every point, with tangents a sixth of the way to its neighbours.
        var curve = (MascotCurve)MascotGeometry.SmoothClosedPath(open).Figures[0].Segments[0];
        Assert.Equal(open[1], curve.To);
        Assert.Equal(open[0].X + (open[1].X - open[^1].X) / 6, curve.Control1.X, 12);
    }

    [Fact]
    public void EveryBodyFitsItsSquareWithItsFaceOnIt()
    {
        foreach (var shape in Enum.GetValues<MascotShape>())
        {
            var body = MascotGeometry.Body(shape, 20);
            var box = body.Bounds;
            Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= 20 && box.Bottom <= 20 && box.Width > 8 && box.Height > 8,
                        $"the {shape} body fits its square");
            var face = new MascotLook(MascotStyle.Minimal, shape, MascotPalette.Pearl).Face;
            Assert.True(body.Contains(new PointD(face.LeftEye.X * 20, face.LeftEye.Y * 20))
                        && body.Contains(new PointD(face.RightEye.X * 20, face.RightEye.Y * 20))
                        && body.Contains(new PointD(face.ShineCenter.X * 20, face.ShineCenter.Y * 20)),
                        $"the {shape} face and its highlight sit on the body");
        }
        Assert.Equal(72, MascotGeometry.Body(MascotShape.Squircle, 1).Figures[0].Segments.Count + 1);
        var ball = MascotGeometry.Body(MascotShape.Ball, 1).Bounds;
        Assert.Equal(0.08, ball.X, 9);
        Assert.Equal(0.96, ball.Bottom, 9);
        var egg = MascotGeometry.Body(MascotShape.Egg, 1).Bounds;
        Assert.Equal(0.14, egg.X, 9);
        Assert.Equal(0.10, egg.Y, 9);
        var robot = MascotGeometry.Robot(20);
        var robotFace = new MascotLook(MascotStyle.Robot, MascotShape.Ball, MascotPalette.Mint).Face;
        Assert.True(robot.Visor.Contains(new PointD(robotFace.LeftEye.X * 20, robotFace.LeftEye.Y * 20))
                    && robot.Visor.Contains(new PointD(robotFace.RightEye.X * 20, robotFace.RightEye.Y * 20)));
        Assert.All(new[] { robot.Head, robot.Ears, robot.Antenna, robot.Bulb }, part =>
        {
            var box = part.Bounds;
            Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= 20 && box.Bottom <= 20);
        });
        Assert.Equal(2, robot.Ears.Figures.Count);
        // The shine leans with the body's curve.
        var shine = MascotGeometry.Shine(MascotLook.Standard.Face, 20).Bounds;
        Assert.Equal(0.31 * 20, shine.X + shine.Width / 2, 6);
    }

    [Fact]
    public void EveryLookIsCentredOnItsLine()
    {
        foreach (var style in Enum.GetValues<MascotStyle>())
        foreach (var shape in Enum.GetValues<MascotShape>())
        {
            var look = new MascotLook(style, shape, MascotPalette.Pearl);
            foreach (var size in new double[] { 10, 16, 18, 20 })
            {
                const double middle = 15;
                var boxTop = middle - MascotGeometry.FigureOffset(look) * size - size / 2;
                var figure = style == MascotStyle.Robot ? MascotGeometry.Robot(size).FigureBounds
                    : MascotGeometry.Body(shape, size).Bounds;
                Assert.True(Math.Abs(boxTop + figure.Y + figure.Height / 2 - middle) < 0.001, $"{style} {shape} at {size}");
            }
        }
        Assert.True(MascotGeometry.FigureOffset(MascotLook.Standard) > 0.03);
    }

    // MARK: Tracks

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(38)]
    public void ItRestsWholeInsideTheWingBesideTheCamera(double height)
    {
        var track = MascotSupport.Track(180 + 88, height, 44, 180, floats: false, bodyHeight: height);
        Assert.True(track.Rest - track.Size / 2 >= 0 && track.Rest + track.Size / 2 <= 44 - 4);
        Assert.Equal(new MascotRange(44, 224), track.Hidden);
        Assert.True(Math.Abs(track.FarSpot - (224 + 44 - track.Rest)) < 0.01);
        Assert.True(track.Size <= height - 4);
        Assert.Equal(Math.Clamp(height - 4, 16, 26), track.Size);
    }

    [Fact]
    public void InACapsuleItRestsInTheMiddle()
    {
        var capsule = Capsule();
        Assert.Null(capsule.Hidden);
        Assert.Equal(38, capsule.Rest);
        Assert.True(capsule.Size <= 18);
        Assert.Equal(12, capsule.Baseline);
        Assert.True(capsule.FarSpot > capsule.Rest && capsule.FarSpot + capsule.Size / 2 <= 76);
        Assert.Equal(Math.Clamp(20 - 4.0, 12, 22), capsule.Size);
        Assert.Null(capsule.VisibleRects);
        Assert.Equal(2, Notch().VisibleRects!.Count);
    }

    // MARK: Strolls

    public static TheoryData<string, bool> Strolls => new() { { "notch", true }, { "notch", false }, { "capsule", true }, { "capsule", false } };

    [Theory, MemberData(nameof(Strolls))]
    public void LapsAndPassesAreShortLowAndSeen(string name, bool lap)
    {
        var track = name == "notch" ? Notch() : Capsule();
        var kind = lap ? MascotVisitKind.Lap : MascotVisitKind.Pass;
        var path = MascotMotion.Path(kind, track);
        AssertEvenRun(path);
        Assert.Equal(MascotMotion.Duration(kind), path.Duration);
        Assert.True(path.Duration <= 4 && path.Greeting.Lower > 0 && path.Greeting.Upper < path.Duration);
        Assert.True(path.Lift.All(lift => lift >= 0 && lift <= track.Size * 0.4));
        Assert.True(path.Squash.All(squash => Math.Abs(squash) <= 0.15));
        AssertClearOfTheTop(path, track);
        if (lap)
        {
            Assert.True(Math.Abs(path.X[0] - track.Rest) < 0.01 && Math.Abs(path.X[^1] - track.Rest) < 0.01);
            // Between two frames it never crosses the strip: a frame drawn in between would show it in the middle.
            var crossings = Enumerable.Range(1, path.X.Count - 1)
                .Where(i => Math.Abs(path.X[i] - path.X[i - 1]) > track.Width / 2).ToList();
            Assert.NotEmpty(crossings);
            Assert.All(crossings, i => Assert.Equal(path.KeyTimes[i - 1], path.KeyTimes[i]));
        }
        else Assert.True(path.X[0] <= -track.Size / 2 && path.X[^1] >= track.Width + track.Size / 2);
        if (track.Hidden is { } hidden)
            for (var i = 1; i < path.X.Count; i++)
                if (path.X[i] == path.X[i - 1])
                    Assert.False(path.X[i] > hidden.Lower + 1 && path.X[i] < hidden.Upper - 1, "it never stops behind the camera");
        var hello = Frames(path, t => t >= path.Greeting.Lower + 0.2 && t <= path.Greeting.Upper - 0.2).ToList();
        Assert.NotEmpty(hello);
        Assert.All(hello, i =>
        {
            var x = path.X[i];
            Assert.True(x - track.Size / 2 >= 0 && x + track.Size / 2 <= track.Width);
            if (track.Hidden is { } h) Assert.True(x + track.Size / 2 <= h.Lower || x - track.Size / 2 >= h.Upper);
        });
    }

    [Fact]
    public void ComingHomeIsOneShortRunEndingWhereItRests()
    {
        foreach (var track in new[] { Notch(), Capsule() })
        {
            var path = MascotMotion.Path(MascotVisitKind.Home, track);
            AssertEvenRun(path);
            Assert.Equal(MascotMotion.HomeDuration, path.Duration);
            Assert.True(path.Duration < 0.8);
            Assert.True(Math.Abs(path.X[^1] - track.Rest) < 0.01 && path.Lift[^1] == 0);
            Assert.True(path.Greeting.Lower == 0 && path.Greeting.Upper < path.Duration);
            if (track.Hidden is { } hidden)
                Assert.True(path.X[0] - track.Size / 2 > hidden.Lower && path.X.All(x => x >= track.Rest - 0.01));
            else Assert.True(path.X.All(x => Math.Abs(x - track.Rest) < 0.01));
        }
    }

    [Fact]
    public void AFarewellIsOverInUnderASecondAndEndsOutOfSight()
    {
        Assert.True(MascotVisitKind.Cameo(MascotReaction.Groove).TakesOnlyItsWing && MascotVisitKind.Countdown(5).TakesOnlyItsWing
                    && MascotVisitKind.Retreat.TakesOnlyItsWing && MascotVisitKind.Linger(MascotReaction.Perk).TakesOnlyItsWing
                    && MascotVisitKind.Cameo(MascotReaction.Celebrate).TakesOnlyItsWing && !MascotVisitKind.Pass.TakesOnlyItsWing);
        Assert.True(MascotVisitKind.Farewell.EndsOutOfSight && !MascotVisitKind.Farewell.WatchesTimer);
        Assert.True(MascotMotion.Duration(MascotVisitKind.Farewell) < 1);
        foreach (var track in new[] { Notch(), Notch(MascotSide.Right), Capsule() })
        {
            var path = MascotMotion.Path(MascotVisitKind.Farewell, track);
            AssertEvenRun(path);
            Assert.True(path.Greeting.Lower == 0 && path.Greeting.Upper > 0.3);
            Assert.True(Math.Abs(path.X[0] - track.Rest) < 0.01 && path.Lift[0] == 0);
            AssertClearOfTheTop(path, track);
            if (track.Hidden is not null) Assert.True(Behind(track, path.X[^1]));
            else Assert.True(path.X[^1] >= track.Width + track.Size / 2);
        }
    }

    [Fact]
    public void HidingItYawnsWhereItRestsAndGoesOutOfSight()
    {
        var away = MascotSupport.HideAway;
        Assert.True(away.EndsOutOfSight && away.Reaction == MascotReaction.Yawn && MascotMotion.Duration(away) < 2.5);
        foreach (var track in new[] { Notch(), Notch(MascotSide.Right), Capsule() })
        {
            var path = MascotMotion.Path(away, track);
            Assert.True(Math.Abs(path.X[0] - track.Rest) < 0.01 && path.Lift[0] == 0);
            if (track.Hidden is not null) Assert.True(Behind(track, path.X[^1]));
            else Assert.True(path.X[^1] >= track.Width + track.Size / 2);
        }
        // Late at night its eyes grow heavy after its fewest blinks, each at least the shortest wait apart.
        var doze = MascotSupport.BlinksBeforeSleep(23) * MascotSupport.BlinkInterval(false).Min;
        Assert.True(MascotSupport.HideDelay < doze && MascotSupport.HideRetry < MascotSupport.HideDelay);
    }

    [Fact]
    public void LingeringSkipsTheWayOutAndHandsBackAsItSetsOff()
    {
        Assert.True(MascotVisitKind.Linger(MascotReaction.Ready).EndsOutOfSight);
        Assert.Equal(MascotReaction.Ready, MascotVisitKind.Linger(MascotReaction.Ready).Reaction);
        Assert.Equal(MascotReaction.Love, MascotVisitKind.Cameo(MascotReaction.Love).Reaction);
        Assert.Null(MascotVisitKind.Lap.Reaction);
        Assert.Equal(MascotMotion.Duration(MascotVisitKind.Cameo(MascotReaction.Ready)) - MascotMotion.CameoArrival,
                     MascotMotion.Duration(MascotVisitKind.Linger(MascotReaction.Ready)), 12);
        foreach (var track in new[] { Notch(MascotSide.Right), Capsule() })
        {
            var path = MascotMotion.Path(MascotVisitKind.Linger(MascotReaction.Ready), track);
            var hold = MascotMotion.CameoHold(MascotReaction.Ready);
            var held = Frames(path, t => t <= hold).ToList();
            Assert.NotEmpty(held);
            Assert.All(held, i => Assert.True(Math.Abs(path.X[i] - track.Rest) < 0.01 && path.Lift[i] == 0));
            if (track.Hidden is not null) Assert.True(Behind(track, path.X[^1]));
            else Assert.True(path.X[^1] >= track.Width + track.Size / 2);
        }
        var right = Notch(MascotSide.Right);
        foreach (var kind in new[]
                 {
                     MascotVisitKind.Cameo(MascotReaction.Love), MascotVisitKind.Linger(MascotReaction.Perk),
                     MascotVisitKind.Cameo(MascotReaction.Groove), MascotVisitKind.Linger(MascotReaction.Groove),
                 })
        {
            var duration = MascotMotion.Duration(kind);
            var camera = MascotMotion.HandBack(kind, floats: false) ?? 0;
            var capsule = MascotMotion.HandBack(kind, floats: true) ?? 0;
            Assert.True(Math.Abs(camera - (duration - MascotMotion.CameoExit)) < 1e-9);
            Assert.True(Math.Abs(capsule - (duration - MascotMotion.CameoExit * 3 / 4)) < 1e-9);
            var path = MascotMotion.Path(kind, right);
            var before = Frames(path, t => t <= camera - 0.01).ToList();
            Assert.NotEmpty(before);
            Assert.True(Math.Abs(path.X[before[^1]] - right.Rest) < 0.01, $"{kind} still stands where it reacted");
        }
        Assert.Null(MascotMotion.HandBack(MascotVisitKind.Lap, floats: false));
        Assert.Null(MascotMotion.HandBack(MascotVisitKind.Countdown(5), floats: false));
    }

    [Fact]
    public void SwitchedOnItArrivesFromBehindTheCameraOrHopsIntoACapsule()
    {
        var left = Notch();
        Assert.Equal(MascotMotion.Path(MascotVisitKind.Home, left), MascotMotion.Path(MascotVisitKind.Arrive, left));
        Assert.False(MascotVisitKind.Arrive.EndsOutOfSight);
        var capsule = Capsule();
        var path = MascotMotion.Path(MascotVisitKind.Arrive, capsule);
        Assert.True(path.X[0] <= -capsule.Size / 2 && Math.Abs(path.X[^1] - capsule.Rest) < 0.01 && path.Greeting.Lower == 0);
        AssertClearOfTheTop(path, capsule);
    }

    [Fact]
    public void CrossingTheCameraEndsWhereItNowRests()
    {
        var left = Notch();
        var right = Notch(MascotSide.Right);
        Assert.False(MascotVisitKind.Cross.EndsOutOfSight);
        Assert.True(MascotMotion.Duration(MascotVisitKind.Cross) < 1.6);
        foreach (var (track, from) in new[] { (left, right.Rest), (right, left.Rest) })
        {
            var path = MascotMotion.Path(MascotVisitKind.Cross, track);
            AssertEvenRun(path);
            Assert.True(Math.Abs(path.X[0] - from) < 0.01 && Math.Abs(path.X[^1] - track.Rest) < 0.01);
            Assert.Contains(path.X, x => Behind(track, x));
            AssertClearOfTheTop(path, track);
        }
    }

    [Fact]
    public void TheSettingsPreviewIsTheRealIslandScaled()
    {
        var track = Notch(MascotSide.Right);
        var big = track.Scaled(1.5);
        Assert.True(big.Width == track.Width * 1.5 && big.Size == track.Size * 1.5 && big.Rest == track.Rest * 1.5
                    && big.Baseline == track.Baseline * 1.5 && big.Mirrored == track.Mirrored
                    && big.Hidden!.Value.Lower == track.Hidden!.Value.Lower * 1.5);
        Assert.True(Math.Abs(big.Hop(0.22) - track.Hop(0.22) * 1.5) < 0.001 && Math.Abs(big.Hop(0.9) - track.Hop(0.9) * 1.5) < 0.001);
        var lap = MascotMotion.Path(MascotVisitKind.Lap, track);
        var bigLap = MascotMotion.Path(MascotVisitKind.Lap, big);
        Assert.True(lap.X.Zip(bigLap.X).All(p => Math.Abs(p.First * 1.5 - p.Second) < 0.001));
        Assert.True(lap.Lift.Zip(bigLap.Lift).All(p => Math.Abs(p.First * 1.5 - p.Second) < 0.001));
        Assert.Equal(lap.KeyTimes, bigLap.KeyTimes);
    }

    [Fact]
    public void ThePreviewActsOutAVisitAndAMomentForEveryReaction()
    {
        var moments = Enum.GetValues<MascotMoment>();
        Assert.Equal(MascotMoment.Visit, moments[0]);
        Assert.Null(MascotMoment.Visit.Reaction());
        Assert.All(moments.Skip(1), m => Assert.NotNull(m.Reaction()));
        var beats = moments.Skip(1).Select(m => $"{m.Reaction()}/{m.FollowUp()}").ToList();
        Assert.Equal(beats.Count, beats.Distinct().Count());
        Assert.Equal(MascotReaction.Ready, MascotMoment.Agents.Reaction());
        Assert.Equal(MascotReaction.Celebrate, MascotMoment.Agents.FollowUp());
        Assert.Equal([MascotMoment.Agents], moments.Where(m => m.FollowUp() is not null));
        Assert.Equal(Enum.GetValues<MascotReaction>().ToHashSet(), moments.Select(m => m.Reaction()).OfType<MascotReaction>().ToHashSet());
        Assert.Equal(MascotReaction.Bounce, MascotMoment.EventStarts.Reaction());
        Assert.Equal(MascotReaction.Yawn, MascotMoment.LowBattery.Reaction());
    }

    [Fact]
    public void ACameoPlaysWhereItWouldRestAndIsSoonGone()
    {
        Assert.True(MascotVisitKind.Pass.EndsOutOfSight && MascotVisitKind.Cameo(MascotReaction.Love).EndsOutOfSight
                    && !MascotVisitKind.Lap.EndsOutOfSight && !MascotVisitKind.Home.EndsOutOfSight);
        foreach (var reaction in Enum.GetValues<MascotReaction>())
        {
            var kind = MascotVisitKind.Cameo(reaction);
            Assert.True(MascotMotion.CameoHold(reaction) > reaction.Length() && MascotMotion.Duration(kind) <= 3);
            foreach (var track in new[] { Notch(), Notch(MascotSide.Right), Capsule() })
            {
                var path = MascotMotion.Path(kind, track);
                AssertEvenRun(path);
                Assert.Equal(MascotMotion.Duration(kind), path.Duration);
                Assert.Equal(path.Greeting.Lower, path.Greeting.Upper);
                var landed = Frames(path, t => t >= MascotMotion.CameoArrival && t <= MascotMotion.CameoArrival + reaction.Length()).ToList();
                Assert.NotEmpty(landed);
                Assert.All(landed, i => Assert.True(Math.Abs(path.X[i] - track.Rest) < 0.01 && path.Lift[i] == 0));
                AssertClearOfTheTop(path, track);
                if (track.Hidden is not null)
                {
                    var start = path.X[0];
                    Assert.True(Behind(track, start) && Behind(track, path.X[^1]));
                    Assert.True(path.X.All(x => x >= Math.Min(start, track.Rest) - 0.01 && x <= Math.Max(start, track.Rest) + 0.01));
                }
                else Assert.True(path.X[0] <= -track.Size / 2 && path.X[^1] >= track.Width + track.Size / 2);
            }
        }
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(4.6)]
    public void ItWatchesACountdownRunOutHoppingAsEachSecondGoes(double total)
    {
        var left = Notch();
        Assert.True(MascotVisitKind.Countdown(5).EndsOutOfSight && MascotVisitKind.Retreat.EndsOutOfSight
                    && MascotVisitKind.Countdown(5).WatchesTimer && MascotVisitKind.Retreat.WatchesTimer
                    && !MascotVisitKind.Cameo(MascotReaction.Perk).WatchesTimer);
        var kind = MascotVisitKind.Countdown(total);
        var path = MascotMotion.Path(kind, left);
        var duration = MascotMotion.Duration(kind);
        Assert.Equal(duration, path.Duration);
        Assert.Equal(total + MascotMotion.CountdownLinger + MascotMotion.CameoExit, duration, 12);
        Assert.True(Behind(left, path.X[0]) && Behind(left, path.X[^1]));
        Assert.True(path.X.All(x => x >= left.Rest - 0.01 && x <= path.X[0] + 0.01));
        var watching = Frames(path, t => t >= MascotMotion.CameoArrival && t <= total).ToList();
        Assert.NotEmpty(watching);
        Assert.All(watching, i => Assert.True(Math.Abs(path.X[i] - left.Rest) < 0.01 && path.Gaze[i] > 0.05));
        var ticks = new List<double>();
        for (var leftSeconds = Math.Ceiling(total) - 1; leftSeconds >= 1; leftSeconds--) ticks.Add(total - leftSeconds);
        ticks = ticks.Where(t => t > MascotMotion.CameoArrival + 0.25).ToList();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, tick => Assert.Contains(Frames(path, t => Math.Abs(t - tick) < 0.04), i => path.Lift[i] > 0.5));
        Assert.All(ticks.SkipLast(1), tick => Assert.Contains(Frames(path, t => Math.Abs(t - (tick + 0.5)) < 0.04), i => path.Lift[i] == 0));
        // Resting on the right, it still watches from the left, where the timer's mark is.
        Assert.Equal(MascotMotion.Path(MascotVisitKind.Countdown(5), left).X,
                     MascotMotion.Path(MascotVisitKind.Countdown(5), Notch(MascotSide.Right)).X);
        var retreat = MascotMotion.Path(MascotVisitKind.Retreat, Notch(MascotSide.Right));
        Assert.True(Math.Abs(retreat.X[0] - left.Rest) < 0.01 && Behind(left, retreat.X[^1]));
        Assert.Equal(MascotMotion.Duration(MascotVisitKind.Retreat), retreat.Duration);
    }

    [Fact]
    public void OverAnActivityItComesOutInTheWingOnItsSide()
    {
        const double wing = 57.2, camera = 185;
        var width = camera + wing * 2;
        var left = MascotSupport.TrackOverActivity(false, width, 32, 32, wing, camera);
        var right = MascotSupport.TrackOverActivity(false, width, 32, 32, wing, camera, side: MascotSide.Right);
        Assert.NotNull(left);
        Assert.True(left.Width == width && left.Hidden == new MascotRange(wing, wing + camera)
                    && left.Rest - left.Size / 2 >= 4 && left.Rest + left.Size / 2 <= wing - 4);
        Assert.True(right!.Mirrored && Math.Abs(right.Rest - (right.Width - left.Rest)) < 0.01);
        // A strip that keeps to the cutout has no wing for it, so it stays behind the camera.
        Assert.Null(MascotSupport.TrackOverActivity(false, 185, 32, 32, 0, camera, usesFooter: true));
        Assert.Null(MascotSupport.TrackOverActivity(false, 185 + 50, 32, 32, 25, camera));
        var through = MascotSupport.TrackOverActivity(true, 140, 28, 20, 0, 0);
        Assert.True(through!.Hidden is null && through.Rest == 70 && !through.Mirrored);
    }

    [Fact]
    public void ReactionsAreKeptShortAndRare()
    {
        var gate = new MascotReactionGate();
        Assert.True(gate.Admits(MascotReaction.Celebrate, 100));
        Assert.False(gate.Admits(MascotReaction.Love, 100 + MascotReactionGate.Spacing / 2));
        Assert.True(gate.Admits(MascotReaction.Love, 100 + MascotReactionGate.Spacing + 0.1));
        var lovedAt = 100 + MascotReactionGate.Spacing + 0.1;
        Assert.False(gate.Admits(MascotReaction.Love, lovedAt + MascotReactionGate.Spacing + 1));
        Assert.True(gate.Admits(MascotReaction.Love, lovedAt + MascotReactionGate.RepeatInterval + 0.1));
        Assert.True(MascotReactionGate.Patience <= 10 && MascotReactionGate.Spacing >= 1);
        Assert.Equal(8, MascotReactionGate.Patience);
        Assert.Equal(1.5, MascotReactionGate.Freshness);
    }

    [Fact]
    public void OnTheRightEveryStrollIsTheLeftOneInAMirror()
    {
        var left = Notch();
        var right = Notch(MascotSide.Right);
        Assert.True(right.Mirrored && Math.Abs(right.Rest - (268 - left.Rest)) < 0.01 && right.Hidden == left.Hidden
                    && right.Rest - right.Size / 2 >= 224 + 4 && right.Rest + right.Size / 2 <= 268);
        var capsule = Capsule(MascotSide.Right);
        Assert.True(!capsule.Mirrored && capsule.Rest == 38);
        foreach (var kind in new[] { MascotVisitKind.Lap, MascotVisitKind.Pass, MascotVisitKind.Home, MascotVisitKind.Cameo(MascotReaction.Love) })
        {
            var plain = MascotMotion.Path(kind, left);
            var mirrored = MascotMotion.Path(kind, right);
            Assert.Equal(plain.KeyTimes, mirrored.KeyTimes);
            Assert.Equal(plain.Lift, mirrored.Lift);
            Assert.True(mirrored.X.Zip(plain.X).All(p => Math.Abs(p.First + p.Second - 268) < 0.01));
            Assert.True(mirrored.Gaze.Zip(plain.Gaze).All(p => Math.Abs(p.First + p.Second) < 0.0001));
        }
        var lap = MascotMotion.Path(MascotVisitKind.Lap, right);
        Assert.True(Math.Abs(lap.X[0] - right.Rest) < 0.01 && Math.Abs(lap.X[^1] - right.Rest) < 0.01);
    }

    // MARK: What the island sees

    [Fact]
    public void ItBouncesAsTheEventItCountedDownToBegins()
    {
        var start = new DateTime(2027, 1, 15, 9, 0, 0, DateTimeKind.Local);
        CalendarEvent Event(string id, double offset) =>
            new(id, id, "personal", "Personal", start.AddSeconds(offset), start.AddSeconds(offset + 1800), false);
        var meeting = Event("meeting", 0);
        var next = Event("next", 900);
        var counting = new CalendarCountdown(meeting, false);
        bool Began(CalendarCountdown? after, double at) => MascotSupport.EventBegan(counting, after, start.AddSeconds(at));
        Assert.True(Began(new CalendarCountdown(meeting, true), 0.5) && Began(null, 1)
                    && Began(new CalendarCountdown(next, false), -0.8));
        Assert.False(Began(null, -120) || Began(new CalendarCountdown(next, false), -300) || Began(null, 600)
                     || Began(new CalendarCountdown(meeting, false), 1)
                     || MascotSupport.EventBegan(new CalendarCountdown(meeting, true), null, start.AddSeconds(1800)));
    }

    [Fact]
    public void EachMomentBringsItsReaction()
    {
        Assert.Equal(MascotReaction.Surprised, MascotSupport.TimerReaction(NotchTimerPhase.Timer));
        Assert.Equal(MascotReaction.Celebrate, MascotSupport.TimerReaction(NotchTimerPhase.Focus));
        Assert.Equal(MascotReaction.Ready, MascotSupport.TimerReaction(NotchTimerPhase.ShortBreak));
        Assert.Equal(MascotReaction.Ready, MascotSupport.TimerReaction(NotchTimerPhase.LongBreak));
        Assert.Equal(MascotReaction.Love, MascotSupport.PowerReaction(pluggedIn: true, charged: false, low: false));
        Assert.Equal(MascotReaction.Celebrate, MascotSupport.PowerReaction(false, true, false));
        Assert.Equal(MascotReaction.Yawn, MascotSupport.PowerReaction(false, false, true));
        Assert.Null(MascotSupport.PowerReaction(false, false, false));
        Assert.Equal(MascotReaction.Perk, MascotSupport.WatchReaction(new NotchWatchOutcome.Changed()));
        Assert.Equal(MascotReaction.Celebrate, MascotSupport.WatchReaction(new NotchWatchOutcome.Settled()));
        Assert.Equal(MascotReaction.Celebrate, MascotSupport.WatchReaction(new NotchWatchOutcome.Shows("Done")));
        Assert.Equal(MascotReaction.Celebrate, MascotSupport.WatchReaction(new NotchWatchOutcome.Reached("100")));
        Assert.Equal(MascotReaction.Confused, MascotSupport.WatchReaction(new NotchWatchOutcome.Closed()));
        Assert.Equal(MascotReaction.Hush, MascotSupport.MicrophoneReaction(muted: true));
        Assert.Equal(MascotReaction.Perk, MascotSupport.MicrophoneReaction(muted: false));
        Assert.Equal(MascotReaction.Confused, MascotSupport.DownloadReaction(succeeded: false));
        Assert.Equal(new MascotReactionRequest(MascotReaction.Groove, 3, 1.2), MascotSupport.MusicStarted);
        Assert.Equal(1, MascotSupport.AgentStarted.Patience);
        Assert.Equal(30, MascotSupport.FileDropped.Patience);
        Assert.Equal(MascotReaction.WakeUp, MascotSupport.Unlocked.Reaction);
    }

    [Fact]
    public void ItsEyesTurnTowardThePointerWithinItsFace()
    {
        var center = new PointD(26, 16);
        Assert.Equal(new PointD(0, 0), MascotSupport.PointerGaze(center, center, 20));
        var right = MascotSupport.PointerGaze(center, new PointD(400, 16), 20);
        var left = MascotSupport.PointerGaze(center, new PointD(0, 30), 20);
        Assert.True(right.X > 0 && Math.Abs(right.X) <= 0.08 && left.X < 0 && left.Y > 0 && Math.Abs(left.Y) <= 0.05);
        Assert.True(MascotSupport.IsPetting(center, new PointD(26 + 13, 16), 20));
        Assert.False(MascotSupport.IsPetting(center, new PointD(26 + 13.5, 16), 20));
    }

    // MARK: Animation maths

    [Fact]
    public void TimingCurvesAndSpringsBehaveAsCoreAnimations()
    {
        Assert.Equal(0.5, MascotTiming.EaseInEaseOut.Ease(0.5), 6);
        Assert.Equal(0.3, MascotTiming.Linear.Ease(0.3), 12);
        Assert.True(MascotTiming.EaseIn.Ease(0.3) < 0.3 && MascotTiming.EaseOut.Ease(0.3) > 0.3);
        var previous = 0.0;
        for (var t = 0.05; t <= 1; t += 0.05)
        {
            var eased = MascotTiming.Settle.Ease(t);
            Assert.True(eased >= previous);
            previous = eased;
        }
        var spring = new MascotSpring(260, 16);
        Assert.True(spring.SettlingDuration is > 0.5 and < 1.5);
        Assert.True(Math.Abs(spring.Progress(spring.SettlingDuration) - 1) < 0.002);
        Assert.True(Enumerable.Range(1, 50).Any(i => spring.Progress(i * 0.01) > 1), "an underdamped spring overshoots");
        Assert.Equal(2.0 / 3, MascotKeyframes.Value(0.5, [0, 1, 0], [0, 0.25, 1]), 9);
        Assert.Equal(0, MascotKeyframes.Value(0.3, [0, 1, 2], discrete: true));
        Assert.Equal(1, MascotKeyframes.Value(0.6, [0, 1, 2], discrete: true));
        // Two keys at one time are crossed in no time.
        Assert.Equal((2, 0.5), MascotKeyframes.Locate(0.75, [0, 0.5, 0.5, 1]));
    }

    [Fact]
    public void MatricesComposeInWpfOrder()
    {
        var moved = MascotMatrix.Scale(2, 2).Then(MascotMatrix.Translation(10, 0));
        Assert.Equal(new PointD(12, 2), moved.Transform(new PointD(1, 1)));
        var turned = MascotMatrix.About(new PointD(1, 1), MascotMatrix.Rotation(Math.PI / 2));
        var p = turned.Transform(new PointD(2, 1));
        // Clockwise on screen, y down: right of the centre turns to below it.
        Assert.Equal(1, p.X, 9);
        Assert.Equal(2, p.Y, 9);
        var squashed = MascotRig.Squashed(0.1, 20);
        // Its bottom, 0.42 of its size below its centre, stays where it was.
        Assert.Equal(10 + 8.4, squashed.Transform(new PointD(10, 18.4)).Y, 9);
        Assert.Equal(1.08, squashed.M11, 9);
    }
}
