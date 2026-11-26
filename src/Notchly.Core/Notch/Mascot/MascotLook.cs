// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchMascotSupport.swift: the companion's look (styles,
// shapes, palettes, fixed colours, face layouts), its eyes, moods and their expressions, the reactions with their
// lengths, the Settings moments and the reaction gate. Colours are sRGB shares, as upstream's NotchMascotColor.
namespace Notchly.Core.Notch.Mascot;

/// <summary>A coloured shape with two ink eyes and no mouth, or a little robot.</summary>
public enum MascotStyle { Minimal, Robot }

/// <summary>The minimal style's body. Squircle is the "Rounded square" in Settings.</summary>
public enum MascotShape { Ball, Egg, Squircle, Pill }

/// <summary>The colours it comes in, in the order of the rainbow after white, so the swatches read as one row.</summary>
public enum MascotPalette { Pearl, Lemon, Peach, Rose, Lilac, Sky, Mint }

public enum MascotMood
{
    Idle, Happy, Wink, Love, Sleepy, Determined, Surprised, Searching, Thinking, Confused,
    /// <summary>Wide awake, while Keep Awake holds the PC up.</summary>
    Alert,
    /// <summary>Eyes squeezed against something too loud or too bright. A Windows addition.</summary>
    Wince,
}

/// <summary>Which side of the camera the companion rests on.</summary>
public enum MascotSide { Left, Right }

/// <summary>How often the companion strolls through the island.</summary>
public enum MascotVisitFrequency { Rare, Normal, Frequent }

/// <summary>A short reaction to something the island saw happen.</summary>
public enum MascotReaction
{
    /// <summary>Something finished well: an agent's task, a download, a file dropped in.</summary>
    Celebrate,
    /// <summary>Plugged in to charge, or petted.</summary>
    Love,
    /// <summary>A timer ran out.</summary>
    Surprised,
    /// <summary>A screenshot: a hard blink, as a camera's flash.</summary>
    Flash,
    /// <summary>Something failed.</summary>
    Confused,
    /// <summary>The PC was unlocked: it wakes up glad to see you.</summary>
    WakeUp,
    /// <summary>Keep Awake let go, or the battery ran low: a yawn.</summary>
    Yawn,
    /// <summary>Keep Awake took hold, or the microphone opened: wide eyes and a hop.</summary>
    Perk,
    /// <summary>An event the island counted down to began: wide eyes and two quick hops, time to go.</summary>
    Bounce,
    /// <summary>The microphone went quiet: eyes shut a moment as it ducks.</summary>
    Hush,
    /// <summary>A timer started: a determined look and a little nod.</summary>
    Ready,
    /// <summary>Music started: smiling eyes and a bob to the beat.</summary>
    Groove,
    // Windows additions, for what upstream's island does not see.
    /// <summary>The music stopped: it sags, eyes heavy.</summary>
    MusicPaused,
    /// <summary>A song skipped: a quick flick of the head after it.</summary>
    MusicSkipped,
    /// <summary>A notification arrived: a glance and a perk-up.</summary>
    Notify,
    /// <summary>The volume went to the top: a flinch.</summary>
    Loud,
    /// <summary>The screen went to full brightness: it squints.</summary>
    Bright,
    /// <summary>The screen went nearly dark: a slow, sleepy blink.</summary>
    Dim,
    /// <summary>Headphones plugged in: a happy bob with the band over its head.</summary>
    HeadphonesIn,
    /// <summary>Headphones unplugged: a puzzled shrug.</summary>
    HeadphonesOut,
}

/// <summary>What the Settings preview can act out: a visit, and the moments the island reacts to.</summary>
public enum MascotMoment
{
    Visit, TimerStarted, TimeIsUp, Music, Agents, EventStarts, DownloadFinished, DownloadFailed, Screenshot,
    MicMuted, KeepAwake, Charging, LowBattery, Unlocked,
    MusicPaused, MusicSkipped, Notification, LoudVolume, BrightScreen, DimScreen, HeadphonesIn, HeadphonesOut,
}

/// <summary>sRGB components in shares of 1, so the model needs no WPF.</summary>
public readonly record struct MascotColor(double Red, double Green, double Blue, double Alpha = 1)
{
    /// <summary>The eyes, a soft black that still reads on the black island's edge.</summary>
    public static readonly MascotColor Ink = new(0.07, 0.07, 0.08);
    /// <summary>The robot's face plate, where its lit eyes sit.</summary>
    public static readonly MascotColor Visor = new(0.09, 0.09, 0.11);
    public static readonly MascotColor Shine = new(1, 1, 1, 0.55);

    /// <summary>Component by component, as Core Animation blends colours.</summary>
    public static MascotColor Lerp(MascotColor from, MascotColor to, double share) =>
        new(from.Red + (to.Red - from.Red) * share, from.Green + (to.Green - from.Green) * share,
            from.Blue + (to.Blue - from.Blue) * share, from.Alpha + (to.Alpha - from.Alpha) * share);

    /// <summary>0–255 channels, rounded, for a WPF Color.</summary>
    public (byte A, byte R, byte G, byte B) Bytes =>
        (ToByte(Alpha), ToByte(Red), ToByte(Green), ToByte(Blue));

    private static byte ToByte(double share) =>
        (byte)Math.Round(Math.Clamp(share, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

/// <summary>A style, a shape and a colour.</summary>
public sealed record MascotLook(MascotStyle Style, MascotShape Shape, MascotPalette Palette)
{
    public static readonly MascotLook Standard = new(MascotStyle.Minimal, MascotShape.Ball, MascotPalette.Pearl);

    /// <summary>Where the face sits on this body.</summary>
    public MascotFaceLayout Face => MascotFaceLayout.For(this);
}

/// <summary>
/// Where the face sits on each body, in shares of the companion's size from its top left corner (y down).
/// </summary>
public sealed record MascotFaceLayout(PointD LeftEye, PointD RightEye, SizeD EyeSize,
                                      PointD ShineCenter, SizeD ShineRadii, double ShineAngle)
{
    public static MascotFaceLayout For(MascotLook look)
    {
        if (look.Style == MascotStyle.Robot)
            return new(new(0.36, 0.63), new(0.64, 0.63), new(0.085, 0.16), new(0.27, 0.34), new(0.07, 0.04), -0.3);
        return look.Shape switch
        {
            MascotShape.Egg => new(new(0.375, 0.55), new(0.625, 0.55), new(0.095, 0.19),
                                   new(0.33, 0.31), new(0.075, 0.05), -0.6),
            MascotShape.Squircle => new(new(0.35, 0.53), new(0.65, 0.53), new(0.10, 0.20),
                                        new(0.24, 0.28), new(0.08, 0.05), -0.55),
            MascotShape.Pill => new(new(0.37, 0.57), new(0.63, 0.57), new(0.09, 0.19),
                                    new(0.20, 0.42), new(0.07, 0.045), -0.45),
            _ => new(new(0.36, 0.50), new(0.64, 0.50), new(0.10, 0.20), new(0.31, 0.29), new(0.085, 0.055), -0.5),
        };
    }
}

public enum MascotEye
{
    Open, Closed, Happy, Heart, Sleepy, Wide, Squint,
    /// <summary>The lid slants down toward the other eye.</summary>
    Determined,
}

/// <summary>A face: each eye, where they look and how the head leans.</summary>
/// <param name="Gaze">Where the eyes look, in shares of the companion's size. Y grows downward.</param>
/// <param name="Tilt">The head's lean in radians, clockwise on screen.</param>
public readonly record struct MascotExpression(MascotEye Left, MascotEye Right, PointD Gaze = default, double Tilt = 0)
{
    /// <summary>Open eyes blink now and then. Closed, curved or heart eyes do not.</summary>
    public bool Blinks => Left.Blinks() && Right.Blinks();
}

public static class MascotLookExtensions
{
    public static bool Blinks(this MascotEye eye) => eye is MascotEye.Open or MascotEye.Wide or MascotEye.Squint;

    /// <summary>The body's lit top.</summary>
    public static MascotColor Light(this MascotPalette palette) => palette switch
    {
        MascotPalette.Lemon => new(1.00, 0.96, 0.64),
        MascotPalette.Peach => new(1.00, 0.86, 0.74),
        MascotPalette.Rose => new(1.00, 0.80, 0.87),
        MascotPalette.Lilac => new(0.88, 0.83, 1.00),
        MascotPalette.Sky => new(0.77, 0.90, 1.00),
        MascotPalette.Mint => new(0.72, 0.97, 0.86),
        _ => new(0.99, 0.98, 0.96),
    };

    /// <summary>The body's shaded bottom, and the robot's antenna, ears and dark bulb.</summary>
    public static MascotColor Shade(this MascotPalette palette) => palette switch
    {
        MascotPalette.Lemon => new(0.97, 0.83, 0.30),
        MascotPalette.Peach => new(0.98, 0.64, 0.50),
        MascotPalette.Rose => new(0.95, 0.54, 0.68),
        MascotPalette.Lilac => new(0.69, 0.59, 0.96),
        MascotPalette.Sky => new(0.43, 0.67, 0.97),
        MascotPalette.Mint => new(0.40, 0.83, 0.66),
        _ => new(0.84, 0.85, 0.90),
    };

    public static MascotExpression Expression(this MascotMood mood) => mood switch
    {
        MascotMood.Happy => new(MascotEye.Happy, MascotEye.Happy, new(0, -0.02)),
        MascotMood.Wink => new(MascotEye.Open, MascotEye.Happy, Tilt: -0.08),
        MascotMood.Love => new(MascotEye.Heart, MascotEye.Heart),
        MascotMood.Sleepy => new(MascotEye.Sleepy, MascotEye.Sleepy, new(0, 0.03), 0.05),
        MascotMood.Determined => new(MascotEye.Determined, MascotEye.Determined),
        MascotMood.Surprised => new(MascotEye.Wide, MascotEye.Wide, new(0, -0.02)),
        MascotMood.Searching => new(MascotEye.Open, MascotEye.Open, new(0.05, 0)),
        MascotMood.Thinking => new(MascotEye.Squint, MascotEye.Open, new(0.06, -0.06), -0.1),
        MascotMood.Confused => new(MascotEye.Open, MascotEye.Squint, new(-0.03, 0.01), 0.14),
        MascotMood.Alert => new(MascotEye.Wide, MascotEye.Wide),
        MascotMood.Wince => new(MascotEye.Squint, MascotEye.Squint, new(0, 0.02), -0.04),
        _ => new(MascotEye.Open, MascotEye.Open),
    };

    /// <summary>How long to wait between visits, in seconds.</summary>
    public static (double Min, double Max) Delay(this MascotVisitFrequency frequency) => frequency switch
    {
        MascotVisitFrequency.Rare => (600, 1200),
        MascotVisitFrequency.Frequent => (90, 180),
        _ => (240, 540),
    };

    /// <summary>About how long it plays, so the companion stays out for all of it over an activity.</summary>
    public static double Length(this MascotReaction reaction) => reaction switch
    {
        MascotReaction.Celebrate => 1.0,
        MascotReaction.Love => 1.4,
        MascotReaction.Surprised => 0.9,
        MascotReaction.Flash => 0.5,
        MascotReaction.Confused => 1.2,
        MascotReaction.WakeUp => 1.6,
        MascotReaction.Yawn => 1.2,
        MascotReaction.Perk => 1.0,
        MascotReaction.Bounce => 1.0,
        MascotReaction.Hush => 1.0,
        MascotReaction.Ready => 1.0,
        MascotReaction.MusicPaused => 1.1,
        MascotReaction.MusicSkipped => 0.6,
        MascotReaction.Notify => 0.9,
        MascotReaction.Loud => 1.0,
        MascotReaction.Bright => 1.1,
        MascotReaction.Dim => 0.9,
        MascotReaction.HeadphonesIn => 1.4,
        MascotReaction.HeadphonesOut => 1.0,
        _ => 1.6, // Groove
    };

    /// <summary>The reaction a moment brings; null for a visit, which is a stroll rather than a reaction.</summary>
    public static MascotReaction? Reaction(this MascotMoment moment) => moment switch
    {
        MascotMoment.Visit => null,
        MascotMoment.TimerStarted => MascotReaction.Ready,
        MascotMoment.TimeIsUp => MascotReaction.Surprised,
        MascotMoment.Music => MascotReaction.Groove,
        MascotMoment.Agents => MascotReaction.Ready,
        MascotMoment.EventStarts => MascotReaction.Bounce,
        MascotMoment.DownloadFinished => MascotReaction.Celebrate,
        MascotMoment.DownloadFailed => MascotReaction.Confused,
        MascotMoment.Screenshot => MascotReaction.Flash,
        MascotMoment.MicMuted => MascotReaction.Hush,
        MascotMoment.KeepAwake => MascotReaction.Perk,
        MascotMoment.Charging => MascotReaction.Love,
        MascotMoment.LowBattery => MascotReaction.Yawn,
        MascotMoment.MusicPaused => MascotReaction.MusicPaused,
        MascotMoment.MusicSkipped => MascotReaction.MusicSkipped,
        MascotMoment.Notification => MascotReaction.Notify,
        MascotMoment.LoudVolume => MascotReaction.Loud,
        MascotMoment.BrightScreen => MascotReaction.Bright,
        MascotMoment.DimScreen => MascotReaction.Dim,
        MascotMoment.HeadphonesIn => MascotReaction.HeadphonesIn,
        MascotMoment.HeadphonesOut => MascotReaction.HeadphonesOut,
        _ => MascotReaction.WakeUp, // Unlocked
    };

    /// <summary>What it plays once the first reaction is over, for a moment told in two beats: an AI agent
    /// getting to work, then done.</summary>
    public static MascotReaction? FollowUp(this MascotMoment moment) =>
        moment == MascotMoment.Agents ? MascotReaction.Celebrate : null;
}

/// <summary>One reaction, as the island publishes it for the companion to play.</summary>
/// <param name="Start">When it was published, on the companion's clock, in seconds.</param>
public sealed record MascotReactionEvent(Guid Id, MascotReaction Reaction, double Start)
{
    public static MascotReactionEvent Now(MascotReaction reaction, double now) => new(Guid.NewGuid(), reaction, now);
}

/// <summary>
/// Keeps reactions short and rare. None comes within <see cref="Spacing"/> of the last, the same one does not come
/// back within <see cref="RepeatInterval"/>, and one the companion cannot show waits for it at most
/// <see cref="Patience"/>.
/// </summary>
public sealed class MascotReactionGate
{
    public const double Spacing = 2.5;
    public const double RepeatInterval = 30;
    public const double Patience = 8;
    /// <summary>A reaction published longer ago than this is not played: a strip drawn after its moment keeps still.</summary>
    public const double Freshness = 1.5;

    public (MascotReaction Reaction, double Time)? Last { get; private set; }

    /// <summary>Whether <paramref name="reaction"/> may play at <paramref name="time"/>, remembering it when it may.</summary>
    public bool Admits(MascotReaction reaction, double time)
    {
        if (Last is { } last)
        {
            if (time - last.Time < Spacing) return false;
            if (last.Reaction == reaction && time - last.Time < RepeatInterval) return false;
        }
        Last = (reaction, time);
        return true;
    }
}
