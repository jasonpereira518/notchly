// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchMascotSupport.swift (enum NotchMascotSupport): its
// preferences, timings, blink and doze rules, pointer gaze, greetings, size and tracks in a closed strip, and which
// reaction each moment the island sees brings. Not ported: the Command Bar's style, face and reading gaze.
using Notchly.Core.Calendar;
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Notch.Mascot;

/// <summary>A reaction a source asks for: how long it may wait to be shown, and how long before it may show.</summary>
public readonly record struct MascotReactionRequest(MascotReaction Reaction, double Patience = MascotReactionGate.Patience,
                                                    double Delay = 0);

public static class MascotSupport
{
    /// <summary>Turned on in Settings, the companion says hello almost at once.</summary>
    public const double WelcomeDelay = 1.5;
    /// <summary>Music starting brings it out to bob along at most this often.</summary>
    public const double GrooveInterval = 600;
    /// <summary>It waits this long after the music starts, until the song's strip has settled.</summary>
    public const double GrooveDelay = 1.2;
    /// <summary>An AI agent getting to work has it hand the island over with a ready face at most this often.</summary>
    public const double AgentStartInterval = 600;

    /// <summary>Hiding when idle, it goes into the island this long after it last did anything, before it would
    /// doze off where it rests, even late at night.</summary>
    public const double HideDelay = 30;
    /// <summary>Busy just then, or under the pointer, it tries again this much later.</summary>
    public const double HideRetry = 3;
    /// <summary>How it goes into the island to hide: a yawn where it rests, then in behind the camera, or out at a
    /// capsule's far end.</summary>
    public static readonly MascotVisitKind HideAway = MascotVisitKind.Linger(MascotReaction.Yawn);

    public static readonly IReadOnlyList<MascotMood> Greetings = [MascotMood.Happy, MascotMood.Wink, MascotMood.Love];

    /// <summary>The share of blinks at rest its eyes wander to one side before: about once in twenty seconds.</summary>
    public const double GlanceChance = 0.25;
    /// <summary>The share of blinks that come twice.</summary>
    public const double DoubleBlinkChance = 0.18;
    /// <summary>An idle glance waits until the eyes have not followed anything for this long.</summary>
    public const double GlanceAfterAttention = 3;

    /// <summary>The pointer resting on it this long is a pat on the head.</summary>
    public const double PettingDelay = 1;
    /// <summary>At most this often, as the gate keeps a reaction from repeating.</summary>
    public const double PettingInterval = MascotReactionGate.RepeatInterval;

    /// <summary>Room a wing keeps beyond the companion for it to come out in, clear of the strip's rounded end.</summary>
    public const double WingRoom = 10;
    /// <summary>Its size in a notice it stands in, in place of the notice's symbol.</summary>
    public const double NoticeSize = 18;

    // MARK: Preferences

    /// <summary>Installed on the Features page, switched on, and in an island that is on.</summary>
    public static bool IsEnabled(SettingsStore store) =>
        store.IsAvailable(AppFeature.NotchMascot) && NotchSupport.IsEnabled(store) && store.GetBool(DefaultsKey.NotchMascotEnabled);

    public static bool Visits(SettingsStore store) => IsEnabled(store) && store.GetBool(DefaultsKey.NotchMascotVisits);

    /// <summary>Whether it comes out to react to what the island sees, and watches a countdown run out.</summary>
    public static bool Reacts(SettingsStore store) => IsEnabled(store) && store.GetBool(DefaultsKey.NotchMascotReactions);

    /// <summary>Whether it hides in the island once nothing has happened for a while, and comes out only to visit
    /// or to react.</summary>
    public static bool HidesWhenIdle(SettingsStore store) =>
        IsEnabled(store) && store.GetBool(DefaultsKey.NotchMascotHidesWhenIdle);

    public static MascotVisitFrequency VisitFrequency(SettingsStore store) =>
        Parse(store.GetString(DefaultsKey.NotchMascotVisitFrequency), MascotVisitFrequency.Normal);

    public static MascotSide Side(SettingsStore store) => Parse(store.GetString(DefaultsKey.NotchMascotSide), MascotSide.Left);

    /// <summary>The look as chosen; unknown values, as from a newer backup, fall back one by one.</summary>
    public static MascotLook Look(SettingsStore store) => new(
        Parse(store.GetString(DefaultsKey.NotchMascotStyle), MascotStyle.Minimal),
        Parse(store.GetString(DefaultsKey.NotchMascotShape), MascotShape.Ball),
        Parse(store.GetString(DefaultsKey.NotchMascotPalette), MascotPalette.Pearl));

    /// <summary>Upstream's raw value: "squircle", "frequent", "wakeUp".</summary>
    public static string Raw<T>(T value) where T : struct, Enum => NotchEnum.Raw(value);

    /// <summary>The value whose raw value is exactly <paramref name="raw"/>, or <paramref name="fallback"/>.</summary>
    public static T Parse<T>(string? raw, T fallback) where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
            if (NotchEnum.Raw(value) == raw) return value;
        return fallback;
    }

    // MARK: Life at rest

    /// <summary>A blink comes this long after the previous one, twice as long with Battery Saver on.</summary>
    public static (double Min, double Max) BlinkInterval(bool lowPower) => lowPower ? (5.2, 12.8) : (2.6, 6.4);

    /// <summary>After this many blinks at rest its eyes grow heavy: about three minutes by day, under a minute late
    /// at night.</summary>
    public static int BlinksBeforeSleep(int hour) => hour >= 22 || hour < 6 ? 12 : 36;

    /// <summary>Where its eyes go for a pointer at <paramref name="pointer"/>, with its centre at
    /// <paramref name="center"/>: toward it, more the farther it is, within its face.</summary>
    public static PointD PointerGaze(PointD center, PointD pointer, double size)
    {
        if (size <= 0) return new PointD(0, 0);
        double dx = (pointer.X - center.X) / (size * 3), dy = (pointer.Y - center.Y) / (size * 2);
        return new PointD(Math.Clamp(dx, -1, 1) * 0.08, Math.Clamp(dy, -1, 1) * 0.05);
    }

    /// <summary>Whether a pointer at <paramref name="pointer"/> is on the companion centred at
    /// <paramref name="center"/>, for a pat on the head.</summary>
    public static bool IsPetting(PointD center, PointD pointer, double size)
    {
        var reach = size / 2 + 3;
        return Math.Abs(pointer.X - center.X) <= reach && Math.Abs(pointer.Y - center.Y) <= reach;
    }

    // MARK: Visits

    /// <summary>Visits come every few minutes, never on a fixed beat. <paramref name="random"/> is a share of 1.</summary>
    public static double NextVisitDelay(MascotVisitFrequency frequency, double random)
    {
        var (min, max) = frequency.Delay();
        return min + (max - min) * Math.Min(1, Math.Max(0, random));
    }

    public static MascotMood Greeting(double random) =>
        Greetings[Math.Min(Greetings.Count - 1, Math.Max(0, (int)(random * Greetings.Count)))];

    // MARK: Placement

    /// <summary>The companion's square in a closed strip of <paramref name="stripHeight"/>, with or without a
    /// capsule's margins around its body.</summary>
    public static double Size(double stripHeight, bool floats) =>
        floats ? Math.Max(12, Math.Min(22, stripHeight - 4)) : Math.Max(16, Math.Min(26, stripHeight - 4));

    /// <summary>
    /// Where it rests in a strip with a camera: inside a wing, a little way from the camera, so it sits beside it
    /// and never under it. A capsule has no camera, so it rests in the middle whatever the side.
    /// </summary>
    public static MascotTrack Track(double stripWidth, double stripHeight, double wing, double cameraWidth, bool floats,
                                    double bodyHeight, MascotSide side = MascotSide.Left)
    {
        if (floats)
            return new MascotTrack(stripWidth, stripHeight, Size(bodyHeight, true), stripWidth / 2, null, stripHeight / 2,
                                   Ceiling: Math.Max(0, (stripHeight - bodyHeight) / 2));
        var size = Size(stripHeight, false);
        var gap = Math.Max(4, Math.Min(8, wing - size - 6));
        var rest = Math.Max(size / 2, wing - gap - size / 2);
        return new MascotTrack(stripWidth, stripHeight, size, side == MascotSide.Right ? stripWidth - rest : rest,
                               new MascotRange(wing, wing + cameraWidth), stripHeight / 2 + 0.5,
                               Mirrored: side == MascotSide.Right);
    }

    /// <summary>
    /// Where it comes out over an activity's closed strip: through a capsule, or in the wing on its side of the
    /// camera. Null when that wing has no room for it, as when the strip keeps to the cutout and the activity hangs
    /// below it instead. Upstream reads these from the strip's NotchGeometry.
    /// </summary>
    public static MascotTrack? TrackOverActivity(bool floats, double stripWidth, double stripHeight, double bodyHeight,
                                                 double wing, double cameraWidth, bool usesFooter = false,
                                                 MascotSide side = MascotSide.Left)
    {
        if (floats) return Track(stripWidth, stripHeight, 0, 0, true, bodyHeight);
        if (usesFooter || wing < Size(stripHeight, false) + WingRoom) return null;
        return Track(stripWidth, stripHeight, wing, cameraWidth, false, bodyHeight, side);
    }

    // MARK: Reactions to what the island sees

    /// <summary>How it takes a countdown running out: startled by a plain timer's ring, glad at the end of a focus
    /// session, and ready to go again when a break is over.</summary>
    public static MascotReaction TimerReaction(NotchTimerPhase finishing) => finishing switch
    {
        NotchTimerPhase.Focus => MascotReaction.Celebrate,
        NotchTimerPhase.ShortBreak or NotchTimerPhase.LongBreak => MascotReaction.Ready,
        _ => MascotReaction.Surprised,
    };

    /// <summary>How it takes the power changing: glad when the charger goes in, cheering once the battery is full,
    /// and tired when it runs low. Unplugging is no news.</summary>
    public static MascotReaction? PowerReaction(bool pluggedIn, bool charged, bool low) =>
        pluggedIn ? MascotReaction.Love : charged ? MascotReaction.Celebrate : low ? MascotReaction.Yawn : null;

    /// <summary>How it takes a watch's news in its notice: wide-eyed at a change, glad when the wait is over,
    /// puzzled when the window went.</summary>
    public static MascotReaction WatchReaction(NotchWatchOutcome outcome) => outcome switch
    {
        NotchWatchOutcome.Changed => MascotReaction.Perk,
        NotchWatchOutcome.Closed => MascotReaction.Confused,
        _ => MascotReaction.Celebrate,
    };

    /// <summary>The microphone going quiet has it duck; opening, it perks up.</summary>
    public static MascotReaction MicrophoneReaction(bool muted) => muted ? MascotReaction.Hush : MascotReaction.Perk;

    public static MascotReaction DownloadReaction(bool succeeded) =>
        succeeded ? MascotReaction.Celebrate : MascotReaction.Confused;

    /// <summary>Keep Awake taking hold perks it up; letting go, it yawns.</summary>
    public static MascotReaction KeepAwakeReaction(bool active) => active ? MascotReaction.Perk : MascotReaction.Yawn;

    /// <summary>Music started: it comes out to bob along once the song's strip has settled.</summary>
    public static readonly MascotReactionRequest MusicStarted = new(MascotReaction.Groove, Patience: 3, Delay: GrooveDelay);
    /// <summary>A timer started, or went on to its next phase.</summary>
    public static readonly MascotReactionRequest TimerStarted = new(MascotReaction.Ready);
    /// <summary>An AI agent got to work where it rests: it hands the island over with a ready face.</summary>
    public static readonly MascotReactionRequest AgentStarted = new(MascotReaction.Ready, Patience: 1);
    /// <summary>An agent finished its task, or a usage limit renewed.</summary>
    public static readonly MascotReactionRequest AgentFinished = new(MascotReaction.Celebrate);
    public static readonly MascotReactionRequest Unlocked = new(MascotReaction.WakeUp);
    public static readonly MascotReactionRequest EventStarts = new(MascotReaction.Bounce);
    /// <summary>A file dropped on the island: a hop for it once the island rests again.</summary>
    public static readonly MascotReactionRequest FileDropped = new(MascotReaction.Celebrate, Patience: 30);
    /// <summary>A Bluetooth accessory connected.</summary>
    public static readonly MascotReactionRequest AccessoryConnected = new(MascotReaction.Perk);

    /// <summary>
    /// Whether the event the island counted down to, as <paramref name="before"/>, began as the countdown became
    /// <paramref name="after"/> at <paramref name="now"/>: it bounces then. Only just begun, so a PC that slept
    /// through the start, or an event moved or removed before it, is no news.
    /// </summary>
    public static bool EventBegan(CalendarCountdown? before, CalendarCountdown? after, DateTime now)
    {
        if (before is null || before.Ongoing) return false;
        var since = (now - before.Event.Start).TotalSeconds;
        // Its refresh may come a second early.
        if (since < -2 || since >= 60) return false;
        if (after is null) return true;
        return after.Event.Id != before.Event.Id || after.Ongoing;
    }
}
