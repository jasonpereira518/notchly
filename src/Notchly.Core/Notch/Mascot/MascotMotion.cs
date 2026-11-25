// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchMascotSupport.swift (NotchMascotVisit, NotchMascotTrack,
// NotchMascotPath, NotchMascotMotion): the visits, where the companion can walk in a closed strip, and every stroll
// sampled at 60 frames a second with its hops, steps, squashes and gaze. Points from the strip's top left, y down.
namespace Notchly.Core.Notch.Mascot;

public enum MascotVisitType
{
    /// <summary>From its resting place, around the island and back.</summary>
    Lap,
    /// <summary>In at one end and out at the other, over what the island shows.</summary>
    Pass,
    /// <summary>Back from the Command Bar's drop: out from behind the camera to its place.</summary>
    Home,
    /// <summary>Out from behind the camera over what the island shows, to play a reaction where it would rest, and
    /// back behind the camera.</summary>
    Cameo,
    /// <summary>The last seconds of a countdown: out over the timer's mark to watch the reading run out, and back.</summary>
    Countdown,
    /// <summary>A countdown it watched was paused: back behind the camera.</summary>
    Retreat,
    /// <summary>Switched off: a glad hop where it rests and away behind the camera, before the wings fold.</summary>
    Farewell,
    /// <summary>Moved to the camera's other side: from where it rested, behind the camera, across, and out.</summary>
    Cross,
    /// <summary>Switched on: out from behind the camera to its place as the wings open, or in at a capsule's near end.</summary>
    Arrive,
    /// <summary>Still where it rested as an activity took its place: it plays a reaction right there and goes behind
    /// the camera, rather than leaving its place only to come back out for it.</summary>
    Linger,
}

/// <summary>What a visit does: its type, with the reaction a cameo or linger plays and a countdown's length.</summary>
public readonly record struct MascotVisitKind
{
    public MascotVisitType Type { get; }
    /// <summary>The reaction it comes out for, played as it lands or at once where it already stands.</summary>
    public MascotReaction? Reaction { get; }
    /// <summary>A countdown's seconds; 0 for the others.</summary>
    public double Total { get; }

    private MascotVisitKind(MascotVisitType type, MascotReaction? reaction = null, double total = 0)
    {
        Type = type;
        Reaction = reaction;
        Total = total;
    }

    public static MascotVisitKind Lap => new(MascotVisitType.Lap);
    public static MascotVisitKind Pass => new(MascotVisitType.Pass);
    public static MascotVisitKind Home => new(MascotVisitType.Home);
    public static MascotVisitKind Retreat => new(MascotVisitType.Retreat);
    public static MascotVisitKind Farewell => new(MascotVisitType.Farewell);
    public static MascotVisitKind Cross => new(MascotVisitType.Cross);
    public static MascotVisitKind Arrive => new(MascotVisitType.Arrive);
    public static MascotVisitKind Cameo(MascotReaction reaction) => new(MascotVisitType.Cameo, reaction);
    public static MascotVisitKind Linger(MascotReaction reaction) => new(MascotVisitType.Linger, reaction);
    public static MascotVisitKind Countdown(double total) => new(MascotVisitType.Countdown, total: total);

    /// <summary>It ends out of sight, past the strip's end or behind the camera, so it can cross an activity's
    /// strip and leave nothing behind.</summary>
    public bool EndsOutOfSight => Type is MascotVisitType.Pass or MascotVisitType.Cameo or MascotVisitType.Countdown
        or MascotVisitType.Retreat or MascotVisitType.Farewell or MascotVisitType.Linger;

    /// <summary>It stands over the timer's mark only, and the reading stays in view.</summary>
    public bool WatchesTimer => Type is MascotVisitType.Countdown or MascotVisitType.Retreat;

    /// <summary>Over an activity beside a camera it takes only the wing it stands in, and the other side reads on.
    /// Only a stroll from end to end takes the whole strip.</summary>
    public bool TakesOnlyItsWing => Type is MascotVisitType.Countdown or MascotVisitType.Retreat
        or MascotVisitType.Cameo or MascotVisitType.Linger;

    public override string ToString() => Type switch
    {
        MascotVisitType.Cameo or MascotVisitType.Linger => $"{Type}({Reaction})",
        MascotVisitType.Countdown => $"{Type}({Total})",
        _ => Type.ToString(),
    };
}

/// <summary>One stroll of the companion through the closed island.</summary>
/// <param name="Start">When it began, on the companion's clock, so a strip drawn again meanwhile picks it up where it is.</param>
public sealed record MascotVisit(Guid Id, MascotVisitKind Kind, MascotMood Greeting, double Start)
{
    public double Duration => MascotMotion.Duration(Kind);

    public static MascotVisit Now(MascotVisitKind kind, MascotMood greeting, double now) => new(Guid.NewGuid(), kind, greeting, now);
}

/// <summary>A closed range along the strip.</summary>
public readonly record struct MascotRange(double Lower, double Upper)
{
    public bool Contains(double value) => value >= Lower && value <= Upper;
}

/// <summary>Where the companion can walk in a closed strip, in points from the strip's top left corner.</summary>
/// <param name="Size">The companion's square.</param>
/// <param name="Rest">Its centre while resting.</param>
/// <param name="Hidden">The camera: walking there, it is behind it.</param>
/// <param name="Baseline">The height of its centre.</param>
/// <param name="Ceiling">The top of the island over it: the screen's edge, or a capsule's.</param>
/// <param name="Mirrored">It rests right of the camera, and every stroll runs the other way.</param>
/// <param name="Scale">How many times larger than the island it is drawn, as in the Settings preview.</param>
public sealed record MascotTrack(double Width, double Height, double Size, double Rest, MascotRange? Hidden,
                                 double Baseline, double Ceiling = 0, bool Mirrored = false, double Scale = 1)
{
    /// <summary>How high a hop of <paramref name="share"/> of its size lifts it, kept clear of the island's top so
    /// it never brushes the screen's edge or a capsule's.</summary>
    public double Hop(double share) =>
        Math.Min(Size * share, Math.Max(Scale, Baseline - Size / 2 - Ceiling - (Hidden is null ? 1 : 2) * Scale));

    /// <summary>Where it stands just out of sight behind the camera, <paramref name="margin"/> points past the
    /// camera's edge on its side.</summary>
    public double BehindCamera(MascotRange hidden, double margin) => hidden.Lower + Size / 2 + margin * Scale;

    /// <summary>The same track <paramref name="scale"/> times larger, for the Settings preview.</summary>
    public MascotTrack Scaled(double scale) => new(Width * scale, Height * scale, Size * scale, Rest * scale,
        Hidden is { } hidden ? new MascotRange(hidden.Lower * scale, hidden.Upper * scale) : null,
        Baseline * scale, Ceiling * scale, Mirrored, Scale * scale);

    /// <summary>The same track with its resting place on the camera's left.</summary>
    public MascotTrack LeftSide => Mirrored ? this with { Mirrored = false, Rest = Width - Rest } : this;

    /// <summary>Where it says hello across the strip: past the camera, as far from it as it rests, or toward a
    /// capsule's far end.</summary>
    public double FarSpot => Hidden is { } hidden
        ? hidden.Upper + (hidden.Lower - Rest)
        : Math.Min(Width - Size * 0.8, Rest + Width * 0.22);

    /// <summary>
    /// The rectangles it can be seen in, in the strip's points: everything but the camera, reaching well past the
    /// strip's ends and above and below it so a hop or a walk off stage is never cut. Null where it is seen
    /// everywhere, as in a capsule. Upstream's track view masks its stage with these.
    /// </summary>
    public IReadOnlyList<RectD>? VisibleRects => Hidden is { } h
        ? [new RectD(-Size * 3, -Height, h.Lower + Size * 3, Height * 3),
           new RectD(h.Upper, -Height, Width - h.Upper + Size * 3, Height * 3)]
        : null;

    /// <summary>Where it stands when the track view shows it: at rest, or off stage at the far end once a visit is
    /// over, so nothing jumps when its last frame hands back to where it stands.</summary>
    public double StandX(bool rests) => rests ? Rest : Mirrored ? -Size * 2 : Width + Size * 2;
}

/// <summary>A visit's position at one moment, from <see cref="MascotVisitPath.Sample"/>.</summary>
/// <param name="Lift">How high a hop lifts it, upward.</param>
/// <param name="Squash">Positive squashes it flat, negative stretches it tall.</param>
/// <param name="Gaze">Where its eyes look across, in shares of its size.</param>
public readonly record struct MascotPathSample(double X, double Lift, double Squash, double Gaze);

/// <summary>
/// The companion's walk, sampled densely enough to play as given: where it is, how high a hop lifts it, how much a
/// landing squashes it and where it looks. Key times are shares of <see cref="Duration"/>; two frames may share one,
/// so it crosses the strip off stage in no time.
/// </summary>
public sealed class MascotVisitPath : IEquatable<MascotVisitPath>
{
    public List<double> KeyTimes { get; } = [];
    public List<double> X { get; set; } = [];
    public List<double> Lift { get; } = [];
    public List<double> Squash { get; } = [];
    public List<double> Gaze { get; set; } = [];
    public double Duration { get; set; }
    /// <summary>When its greeting face shows, in seconds from the start.</summary>
    public MascotRange Greeting { get; set; } = new(0, 0);

    /// <summary>Linear between frames, as upstream's keyframe animations play it. Before the start it is at the
    /// first frame and after the end at the last.</summary>
    public MascotPathSample Sample(double seconds)
    {
        if (KeyTimes.Count == 0) return default;
        var share = Duration > 0 ? seconds / Duration : 1;
        var (index, fraction) = MascotKeyframes.Locate(share, KeyTimes);
        double Lerp(List<double> values) => index + 1 < values.Count
            ? values[index] + (values[index + 1] - values[index]) * fraction
            : values[index];
        return new MascotPathSample(Lerp(X), Lerp(Lift), Lerp(Squash), Lerp(Gaze));
    }

    public bool Equals(MascotVisitPath? other) => other is not null && Duration == other.Duration
        && Greeting == other.Greeting && KeyTimes.SequenceEqual(other.KeyTimes) && X.SequenceEqual(other.X)
        && Lift.SequenceEqual(other.Lift) && Squash.SequenceEqual(other.Squash) && Gaze.SequenceEqual(other.Gaze);

    public override bool Equals(object? obj) => Equals(obj as MascotVisitPath);

    public override int GetHashCode() => HashCode.Combine(Duration, KeyTimes.Count);
}

public static class MascotMotion
{
    public const double LapDuration = 3.6;
    public const double PassDuration = 3.4;
    public const double HomeDuration = 0.62;
    /// <summary>A cameo lands where it would rest this long after it starts, and its reaction plays from there.</summary>
    public const double CameoArrival = 0.5;
    /// <summary>It is back behind the camera as its exit ends, so the visit ends there.</summary>
    public const double CameoExit = 0.32;
    public const double FarewellDuration = 0.78;
    public const double CrossDuration = 1.24;
    /// <summary>The closed island crossfades an activity arriving out of the companion at rest, and one leaving
    /// back into it, over this long.</summary>
    public const double RestCrossfade = 0.2;
    /// <summary>How long before a countdown runs out the companion comes to watch it.</summary>
    public const double CountdownLead = 5;
    /// <summary>It stays past the end for a beat, under the notice that takes over.</summary>
    public const double CountdownLinger = 0.1;
    /// <summary>The frames a stroll is sampled at.</summary>
    public const double SampleRate = 60;

    public static double Duration(MascotVisitKind kind) => kind.Type switch
    {
        MascotVisitType.Lap => LapDuration,
        MascotVisitType.Pass => PassDuration,
        MascotVisitType.Home or MascotVisitType.Arrive => HomeDuration,
        MascotVisitType.Cameo => CameoArrival + CameoHold(kind.Reaction!.Value) + CameoExit,
        MascotVisitType.Linger => CameoHold(kind.Reaction!.Value) + CameoExit,
        MascotVisitType.Countdown => kind.Total + CountdownLinger + CameoExit,
        MascotVisitType.Retreat => CameoExit,
        MascotVisitType.Farewell => FarewellDuration,
        _ => CrossDuration,
    };

    /// <summary>How long a cameo stays where it landed: its reaction and a beat after it.</summary>
    public static double CameoHold(MascotReaction reaction) => reaction.Length() + 0.3;

    /// <summary>
    /// When a reaction's visit gives back what it covered, from its start. Beside a camera it is behind the camera
    /// before it is halfway home, so the strip starts back as it sets off. In a capsule it walks out past the end,
    /// so what it covered starts back a quarter of the way out. Null for a visit that comes out for no reaction.
    /// </summary>
    public static double? HandBack(MascotVisitKind kind, bool floats)
    {
        if (kind.Reaction is not { } reaction || kind.Type is not (MascotVisitType.Cameo or MascotVisitType.Linger)) return null;
        var arrival = kind.Type == MascotVisitType.Linger ? 0 : CameoArrival;
        return arrival + CameoHold(reaction) + (floats ? CameoExit / 4 : 0);
    }

    /// <summary>A stroll of <paramref name="kind"/>. Resting right of the camera, it walks the left side's stroll
    /// seen in a mirror.</summary>
    public static MascotVisitPath Path(MascotVisitKind kind, MascotTrack track)
    {
        // A countdown is watched from the camera's left, over the timer's mark, whichever side it rests on.
        switch (kind.Type)
        {
            case MascotVisitType.Countdown: return Countdown(track.LeftSide, kind.Total);
            case MascotVisitType.Retreat: return Retreat(track.LeftSide);
        }
        if (!track.Mirrored)
            return kind.Type switch
            {
                MascotVisitType.Lap => Lap(track),
                MascotVisitType.Pass => Pass(track),
                MascotVisitType.Home => Home(track),
                MascotVisitType.Cameo => Cameo(track, CameoHold(kind.Reaction!.Value)),
                MascotVisitType.Linger => Linger(track, CameoHold(kind.Reaction!.Value)),
                MascotVisitType.Farewell => Farewell(track),
                MascotVisitType.Cross => Cross(track),
                MascotVisitType.Arrive => Arrive(track),
                _ => new MascotVisitPath(),
            };
        var left = track with { Mirrored = false, Rest = track.Width - track.Rest };
        var path = Path(kind, left);
        path.X = path.X.Select(x => track.Width - x).ToList();
        path.Gaze = path.Gaze.Select(g => -g).ToList();
        return path;
    }

    private abstract record Step;
    private sealed record Stay(double Length, int Bounces = 0) : Step;
    private sealed record HopTo(double Target, double Length, double Height) : Step;
    private sealed record WalkTo(double Target, double Length) : Step;
    private sealed record JumpTo(double Target) : Step;

    /// <summary>From its resting place behind the camera to the other side, a hello, out at the far end and back
    /// in at the near one to rest again.</summary>
    public static MascotVisitPath Lap(MascotTrack track)
    {
        var size = track.Size;
        var steps = new List<Step> { new Stay(0.18) };
        if (track.Hidden is { } hidden)
            steps.AddRange([new HopTo(track.BehindCamera(hidden, 2), 0.34, track.Hop(0.24)),
                            new WalkTo(hidden.Upper - size / 2 - 2 * track.Scale, 0.50),
                            new HopTo(track.FarSpot, 0.34, track.Hop(0.24))]);
        else
            steps.AddRange([new HopTo(track.FarSpot, 0.60, track.Hop(0.3)), new Stay(0.58)]);
        steps.AddRange([new Stay(0.94, 2), new WalkTo(track.Width + size, 0.40), new JumpTo(-size),
                        new HopTo(track.Rest, 0.52, track.Hop(0.26))]);
        var path = Sample(steps, track.Rest, track, LapDuration);
        var greetStart = track.Hidden is null ? 0.78 : 1.30;
        path.Greeting = new MascotRange(greetStart, greetStart + 1.0);
        return path;
    }

    /// <summary>In at the near end, a hello on that side, behind the camera, out at the far end.</summary>
    public static MascotVisitPath Pass(MascotTrack track)
    {
        var size = track.Size;
        var steps = new List<Step> { new Stay(0.20), new HopTo(track.Rest, 0.42, track.Hop(0.26)), new Stay(0.88, 2) };
        if (track.Hidden is { } hidden)
            steps.AddRange([new HopTo(track.BehindCamera(hidden, 2), 0.34, track.Hop(0.24)),
                            new WalkTo(hidden.Upper - size / 2 - 2 * track.Scale, 0.50),
                            new HopTo(track.FarSpot, 0.34, track.Hop(0.24)), new Stay(0.22)]);
        else
            steps.AddRange([new HopTo(track.FarSpot, 0.60, track.Hop(0.3)), new Stay(0.80)]);
        steps.Add(new WalkTo(track.Width + size, 0.40));
        var path = Sample(steps, -size, track, PassDuration);
        path.Greeting = new MascotRange(0.55, 1.55);
        return path;
    }

    /// <summary>Back from behind the camera with a little hop to its place, still wearing the face it left with
    /// until it lands. A capsule has no camera, so it lands where it rests.</summary>
    public static MascotVisitPath Home(MascotTrack track)
    {
        if (track.Hidden is not { } hidden)
        {
            var capsule = Sample([new Stay(0.08), new HopTo(track.Rest, 0.36, track.Hop(0.2))], track.Rest, track, HomeDuration);
            capsule.Greeting = new MascotRange(0, 0.4);
            return capsule;
        }
        var path = Sample([new Stay(0.06), new HopTo(track.Rest, 0.42, track.Hop(0.2))],
                          track.BehindCamera(hidden, 2), track, HomeDuration);
        path.Greeting = new MascotRange(0, 0.44);
        return path;
    }

    /// <summary>Out from behind the camera to where it would rest, <paramref name="hold"/> there for its reaction,
    /// and back behind the camera. A capsule has no camera, so it comes in at the near end and leaves at the far one.</summary>
    public static MascotVisitPath Cameo(MascotTrack track, double hold)
    {
        var size = track.Size;
        var duration = CameoArrival + hold + CameoExit;
        if (track.Hidden is not { } hidden)
            return Sample([new Stay(0.06), new HopTo(track.Rest, CameoArrival - 0.06, track.Hop(0.3)), new Stay(hold),
                           new WalkTo(track.Width + size, CameoExit)], -size, track, duration);
        // Just out of sight, so it shows the moment it moves and is gone as it lands.
        var behind = track.BehindCamera(hidden, 1);
        return Sample([new Stay(0.06), new HopTo(track.Rest, CameoArrival - 0.06, track.Hop(0.2)), new Stay(hold),
                       new HopTo(behind, CameoExit, track.Hop(0.16))], behind, track, duration);
    }

    /// <summary>Where it rests, <paramref name="hold"/> for its reaction, and then behind the camera as a cameo
    /// leaves, or out at a capsule's far end.</summary>
    public static MascotVisitPath Linger(MascotTrack track, double hold)
    {
        var duration = hold + CameoExit;
        if (track.Hidden is not { } hidden)
            return Sample([new Stay(hold), new WalkTo(track.Width + track.Size, CameoExit)], track.Rest, track, duration);
        return Sample([new Stay(hold), new HopTo(track.BehindCamera(hidden, 1), CameoExit, track.Hop(0.16))],
                      track.Rest, track, duration);
    }

    /// <summary>The last <paramref name="total"/> seconds of a countdown: out from behind the camera over the
    /// timer's mark, eyes on the reading, a small hop with every second that goes, and back behind the camera just
    /// after it runs out.</summary>
    public static MascotVisitPath Countdown(MascotTrack track, double total)
    {
        var size = track.Size;
        var duration = total + CountdownLinger + CameoExit;
        if (track.Hidden is not { } hidden) return Sample([new Stay(duration)], -size, track, duration);
        var behind = track.BehindCamera(hidden, 1);
        var steps = new List<Step> { new Stay(0.06), new HopTo(track.Rest, CameoArrival - 0.06, track.Hop(0.2)) };
        var time = CameoArrival;
        // The reading changes on every whole second left; it hops just as it does.
        var ticks = new List<double>();
        for (var left = Math.Ceiling(total) - 1; left >= 1; left -= 1) ticks.Add(total - left);
        foreach (var tick in ticks.Where(tick => tick - 0.15 > CameoArrival + 0.1))
        {
            steps.Add(new Stay(tick - 0.15 - time));
            steps.Add(new HopTo(track.Rest, 0.3, track.Hop(0.1)));
            time = tick + 0.15;
        }
        steps.Add(new Stay(Math.Max(0, total + CountdownLinger - time)));
        steps.Add(new HopTo(behind, CameoExit, track.Hop(0.16)));
        var path = Sample(steps, behind, track, duration);
        // Its eyes stay on the reading while it watches.
        path.Gaze = path.KeyTimes.Zip(path.Gaze, (keyTime, gaze) =>
        {
            var at = keyTime * duration;
            return at >= CameoArrival && at <= total + CountdownLinger ? 0.07 : gaze;
        }).ToList();
        return path;
    }

    /// <summary>Switched on: as it comes back home beside a camera. A capsule has no camera to come out from, so
    /// it hops in at the near end instead of appearing in place.</summary>
    public static MascotVisitPath Arrive(MascotTrack track)
    {
        if (track.Hidden is not null) return Home(track);
        var path = Sample([new Stay(0.06), new HopTo(track.Rest, 0.46, track.Hop(0.3))], -track.Size, track, HomeDuration);
        path.Greeting = new MascotRange(0, 0.52);
        return path;
    }

    /// <summary>To this side of the camera from the other: into the camera's far edge, across behind it, and out
    /// with a wink to its place on this side. A capsule has no sides, so it only hops where it rests.</summary>
    public static MascotVisitPath Cross(MascotTrack track)
    {
        if (track.Hidden is not { } hidden) return Home(track);
        var size = track.Size;
        var path = Sample([new Stay(0.06),
                           new HopTo(hidden.Upper - size / 2 - 2 * track.Scale, 0.34, track.Hop(0.24)),
                           new WalkTo(track.BehindCamera(hidden, 2), 0.38),
                           new HopTo(track.Rest, 0.36, track.Hop(0.24)), new Stay(0.1)],
                          track.FarSpot, track, CrossDuration);
        path.Greeting = new MascotRange(0.72, CrossDuration);
        return path;
    }

    /// <summary>Switched off: a glad little hop where it rests, then away behind the camera, out of sight before
    /// the wings fold. A capsule has no camera, so it walks out past the far end.</summary>
    public static MascotVisitPath Farewell(MascotTrack track)
    {
        var size = track.Size;
        if (track.Hidden is not { } hidden)
        {
            var capsule = Sample([new Stay(0.06), new HopTo(track.Rest, 0.3, track.Hop(0.22)), new Stay(0.02),
                                  new WalkTo(track.Width + size, 0.4)], track.Rest, track, FarewellDuration);
            capsule.Greeting = new MascotRange(0, 0.44);
            return capsule;
        }
        var path = Sample([new Stay(0.06), new HopTo(track.Rest, 0.3, track.Hop(0.22)), new Stay(0.08),
                           new HopTo(track.BehindCamera(hidden, 1), CameoExit, track.Hop(0.16))],
                          track.Rest, track, FarewellDuration);
        path.Greeting = new MascotRange(0, 0.48);
        return path;
    }

    /// <summary>Back behind the camera from where it watched a countdown.</summary>
    public static MascotVisitPath Retreat(MascotTrack track)
    {
        if (track.Hidden is not { } hidden) return Sample([new Stay(CameoExit)], -track.Size, track, CameoExit);
        return Sample([new HopTo(track.BehindCamera(hidden, 1), CameoExit, track.Hop(0.16))], track.Rest, track, CameoExit);
    }

    private static int Frames(double length, int minimum) =>
        Math.Max(minimum, (int)Math.Round(length * SampleRate, MidpointRounding.AwayFromZero));

    private static MascotVisitPath Sample(IReadOnlyList<Step> steps, double start, MascotTrack track, double duration)
    {
        var path = new MascotVisitPath();
        double time = 0;
        var position = start;
        // A landing's squash eases out over the start of the next step.
        (double Squash, double Time) landing = (0, 0);
        void Add(double x, double lift, double squash, double gaze)
        {
            var recovery = landing.Squash * Math.Max(0, 1 - (time - landing.Time) / 0.14);
            path.KeyTimes.Add(Math.Min(1, time / duration));
            path.X.Add(x);
            path.Lift.Add(lift);
            path.Squash.Add(squash != 0 ? squash : recovery);
            path.Gaze.Add(gaze);
        }
        Add(position, 0, 0, 0);
        foreach (var step in steps)
        {
            switch (step)
            {
                case JumpTo jump:
                    // Off stage at both ends, and in no time: two frames at the same moment, so no frame ever draws
                    // it on the way across.
                    position = jump.Target;
                    Add(position, 0, 0, 0.05);
                    break;
                case Stay stay:
                {
                    var frames = Frames(stay.Length, 1);
                    for (var frame = 1; frame <= frames; frame++)
                    {
                        var share = (double)frame / frames;
                        time += stay.Length / frames;
                        var wave = stay.Bounces > 0 ? Math.Abs(Math.Sin(share * Math.PI * stay.Bounces)) : 0;
                        Add(position, wave * track.Hop(0.16), 0, 0);
                    }
                    break;
                }
                case HopTo hop:
                {
                    var from = position;
                    var frames = Frames(hop.Length, 2);
                    double direction = hop.Target >= from ? 1 : -1;
                    for (var frame = 1; frame <= frames; frame++)
                    {
                        var share = (double)frame / frames;
                        time += hop.Length / frames;
                        // Eased along the ground, a parabola above it.
                        var eased = share * share * (3 - 2 * share);
                        var squash = share < 0.2 ? -0.12 * (1 - share / 0.2) : share > 0.88 ? 0.14 * (share - 0.88) / 0.12 : 0;
                        Add(from + (hop.Target - from) * eased, hop.Height * 4 * share * (1 - share), squash, 0.05 * direction);
                    }
                    position = hop.Target;
                    landing = (0.14, time);
                    break;
                }
                case WalkTo walk:
                {
                    var from = position;
                    var frames = Frames(walk.Length, 2);
                    double direction = walk.Target >= from ? 1 : -1;
                    for (var frame = 1; frame <= frames; frame++)
                    {
                        var share = (double)frame / frames;
                        time += walk.Length / frames;
                        // Little steps: a small bob on each.
                        var bob = Math.Abs(Math.Sin(share * Math.PI * 3)) * track.Hop(0.06);
                        Add(from + (walk.Target - from) * share, bob, 0, 0.05 * direction);
                    }
                    position = walk.Target;
                    break;
                }
            }
        }
        // The last step lands, and it stays for whatever time is left.
        if (time < duration)
        {
            time = duration;
            Add(position, 0, 0, 0);
        }
        else if (path.KeyTimes.Count > 0) path.KeyTimes[^1] = 1;
        path.Duration = duration;
        return path;
    }
}
