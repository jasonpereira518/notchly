// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/UI/Notch/NotchMascotView.swift (NotchMascotRig): what the companion does and
// when — faces and their morphs, the eyes following something, cues, reactions with their keyframes, visits,
// blinks, idle glances and dozing off — as properties animated by key on a clock, sampled into a MascotFrame each
// frame. Windows-specific: upstream hands Core Animation the animations; here the frame loop samples them.
namespace Notchly.Core.Notch.Mascot;

/// <summary>A one-off motion the companion plays over its face.</summary>
public abstract record MascotCue
{
    /// <summary>Results turned up: a hop with smiling eyes.</summary>
    public sealed record Celebrate : MascotCue;
    /// <summary>The eyes dart about, searching.</summary>
    public sealed record Glance : MascotCue;
    /// <summary>The eyes go to <paramref name="Target"/> for a moment, in shares of its size, then come back. Null
    /// looks ahead again at once.</summary>
    public sealed record Look(PointD? Target) : MascotCue;
}

/// <summary>What never changes for a look at a size: the outlines and the body's gradient.</summary>
/// <param name="Body">The minimal body, or the robot's head; filled with the gradient from <c>FillTop</c> at its
/// box's top to <c>FillBottom</c> at its bottom.</param>
/// <param name="Robot">The robot's parts, or null for the minimal style.</param>
/// <param name="Shine">The highlight, filled with <see cref="MascotColor.Shine"/>; the robot has none.</param>
public sealed record MascotParts(MascotLook Look, double Size, MascotPath Body, MascotRobotParts? Robot, MascotPath? Shine,
                                 MascotColor FillTop, MascotColor FillBottom)
{
    public static MascotParts For(MascotLook look, double size)
    {
        var robot = look.Style == MascotStyle.Robot ? MascotGeometry.Robot(size) : null;
        return new MascotParts(look, size, robot?.Head ?? MascotGeometry.Body(look.Shape, size), robot,
                               robot is null ? MascotGeometry.Shine(look.Face, size) : null,
                               look.Palette.Light(), look.Palette.Shade());
    }
}

/// <summary>
/// The companion at one moment, ready to draw. Draw in this order, all through <see cref="Transform"/> (box points
/// to the stage's): the robot's ears, antenna (both <c>Parts.FillBottom</c>) and bulb (<see cref="BulbColor"/>,
/// glowing in its own light by <see cref="BulbGlowOpacity"/>); the body with its gradient; the visor
/// (<see cref="MascotColor.Visor"/>); the shine; then each eye, a smooth closed curve through its 24 points
/// (<see cref="MascotGeometry.SmoothClosedPath"/>) in <see cref="EyeColor"/>, glowing by <see cref="EyeGlowOpacity"/>.
/// </summary>
/// <param name="Transform">The box, from its top left, to the stage: where it stands, its hop, squash and lean.</param>
/// <param name="LeftEye">The left eye's outline in box points, gaze and blink applied.</param>
/// <param name="PreviousLook">The look being crossfaded from after a change in Settings, under this one.</param>
/// <param name="LookFade">How far the new look has faded in, 0 to 1.</param>
public sealed record MascotFrame(MascotParts Parts, MascotMatrix Transform, double Opacity,
                                 PointD[] LeftEye, PointD[] RightEye, MascotColor EyeColor,
                                 double EyeGlowOpacity, double EyeGlowRadius,
                                 MascotColor BulbColor, double BulbGlowOpacity, double BulbGlowRadius,
                                 MascotParts? PreviousLook, double LookFade)
{
    /// <summary>Where the box's centre stands in the stage, before its hop.</summary>
    public PointD Position { get; init; }
    public double Hop { get; init; }
    public double Squash { get; init; }
    public double Tilt { get; init; }
    /// <summary>The eyes' shift in points: the face's gaze plus what they attend to.</summary>
    public PointD EyeShift { get; init; }
    /// <summary>The left eye's scale (x, y), as a blink, flash or heartbeat has it.</summary>
    public PointD LeftEyeScale { get; init; }
    public PointD RightEyeScale { get; init; }
    /// <summary>The stage's own transform, about the host view's centre: the stage grows between sizes.</summary>
    public MascotMatrix StageTransform { get; init; } = MascotMatrix.Identity;
    /// <summary>Where it can be seen, in stage points; null everywhere. The camera range is left out.</summary>
    public IReadOnlyList<RectD>? VisibleRects { get; init; }
    /// <summary>The props floating beside it, with their offsets from the figure's middle in stage points; draw them
    /// over the figure, after its eyes. A Windows addition.</summary>
    public IReadOnlyList<MascotPropFrame> Props { get; init; } = [];
    /// <summary>The figure's middle in the stage, which the props' offsets are from.</summary>
    public PointD FigureCenter { get; init; }
}

public sealed class MascotRig
{
    private const double MorphDuration = 0.22;
    /// <summary>A pointer's eye target within this many points of the last one is the same target.</summary>
    private const double FollowThreshold = 0.2;
    /// <summary>The head leans (radians per point of eye shift over the size) and the body moves (share of the eye
    /// shift) a little toward what the eyes follow, so it looks turned and not only cross-eyed.</summary>
    private const double LeanPerGaze = 0.3, BodyShiftShare = 0.2;
    private const double RestyleFade = 0.24;
    private static readonly MascotSpring GazeSpring = new(260, 16);
    private static readonly MascotSpring TiltSpring = new(200, 12);

    private readonly Func<double> _clock;
    private readonly Func<double> _random;
    private readonly Func<int> _hour;

    private readonly MascotChannel<PointD> _position = new(new PointD(0, 0));
    private readonly MascotChannel<double> _opacity = new(1);
    private readonly MascotChannel<double> _hop = new(0);
    private readonly MascotChannel<double> _squash = new(0);
    private readonly MascotChannel<double> _tilt = new(0);
    private readonly MascotChannel<PointD> _attention = new(new PointD(0, 0));
    private readonly MascotChannel<PointD> _face = new(new PointD(0, 0));
    private readonly MascotChannel<PointD[]> _leftEye = new([]);
    private readonly MascotChannel<PointD[]> _rightEye = new([]);
    private readonly MascotChannel<PointD> _leftScale = new(new PointD(1, 1));
    private readonly MascotChannel<PointD> _rightScale = new(new PointD(1, 1));
    private readonly MascotChannel<double> _bulbLit = new(1);
    private readonly MascotChannel<double> _bulbGlow = new(0);
    /// <summary>A sideways nudge in points, added to where it stands.</summary>
    private readonly MascotChannel<double> _sway = new(0);
    private readonly List<MascotProp> _props = [];

    private MascotParts? _parts;
    private MascotParts? _previousParts;
    private double _restyled = double.NegativeInfinity;
    private int _blinks;
    private bool _dozed;
    /// <summary>When its eyes last went after something: a pointer, a drag or typing.</summary>
    private double _attended = double.NegativeInfinity;
    /// <summary>Where <see cref="Follow"/> last sent the eyes, in points.</summary>
    private PointD? _following;
    /// <summary>When the visit it plays is over.</summary>
    private double _visitEnds;
    /// <summary>A blink is on its way and ends then; its end schedules the next one.</summary>
    private double? _blinkEnds;
    /// <summary>Blinks wait until a visit or a face shown for a moment has gone by.</summary>
    private double _blinksResume;
    private bool _configured;
    private bool _advancing;

    /// <param name="clock">Seconds on a steady clock, as CACurrentMediaTime.</param>
    /// <param name="random">A share of 1, at least 0 and under 1.</param>
    /// <param name="hour">The hour of the day now, 0–23, for dozing off sooner late at night.</param>
    public MascotRig(Func<double> clock, Func<double>? random = null, Func<int>? hour = null)
    {
        _clock = clock;
        _random = random ?? Random.Shared.NextDouble;
        _hour = hour ?? (() => DateTime.Now.Hour);
    }

    public MascotLook Look { get; private set; } = MascotLook.Standard;
    public double Size { get; private set; }
    public MascotMood Mood { get; private set; } = MascotMood.Idle;
    /// <summary>Whether it blinks now and then and, after a long rest, grows sleepy.</summary>
    public bool Idles { get; private set; }
    /// <summary>Windows' animation effects are off: it changes face but does not move.</summary>
    public bool ReduceMotion { get; set; }
    /// <summary>Battery Saver is on: it blinks half as often.</summary>
    public bool LowPower { get; set; }
    /// <summary>-1 when the side away from the camera is the left, +1 when it is the right: where props appear, and
    /// which way it leans toward or away from things. Set from its track.</summary>
    public double Outward { get; set; } = -1;
    /// <summary>The props on show or waiting, for tests and tools.</summary>
    public IReadOnlyList<MascotProp> Props => _props;
    /// <summary>Where its box's centre stands in its stage while nothing moves it.</summary>
    public PointD Position { get => _position.Model; set => _position.Model = value; }
    /// <summary>It grew sleepy on its own after a long rest.</summary>
    public bool IsDozing => _dozed;
    /// <summary>Blinks at rest since it last woke or changed face.</summary>
    public int Blinks => _blinks;
    /// <summary>When the next blink ends, while one is on its way.</summary>
    public double? NextBlinkEnds => _blinkEnds;

    /// <summary>Lets time pass: a blink that ended schedules the next one, or has it doze off. The frame loop calls
    /// it through <see cref="Frame"/>; call it on its own while nothing is drawn.</summary>
    public void Advance() => Now();

    private double Now()
    {
        var now = _clock();
        if (_advancing) return now;
        _advancing = true;
        try
        {
            if (_blinkEnds is { } ends && now >= ends)
            {
                _blinkEnds = null;
                BlinkDidStop();
            }
        }
        finally { _advancing = false; }
        return now;
    }

    // MARK: Look

    /// <summary>Draws the companion at <paramref name="size"/> points with <paramref name="look"/>. Nothing moves,
    /// and a new look at the same size crossfades in, as a choice in Settings does.</summary>
    public void Configure(MascotLook look, double size)
    {
        if (_configured && look == Look && size == Size) return;
        var now = Now();
        var restyled = _configured && look != Look && size == Size && !ReduceMotion;
        _previousParts = restyled ? _parts : null;
        _restyled = restyled ? now : double.NegativeInfinity;
        _configured = true;
        Look = look;
        Size = size;
        _parts = MascotParts.For(look, size);
        _bulbLit.Model = Mood == MascotMood.Sleepy ? 0 : 1;
        Apply(Mood.Expression(), animated: false);
    }

    // MARK: Faces

    public void SetMood(MascotMood mood, bool animated)
    {
        if (mood == Mood) return;
        var previous = Mood;
        Mood = mood;
        _dozed = false;
        if (mood != MascotMood.Sleepy) _blinks = 0;
        Apply(mood.Expression(), animated && !ReduceMotion);
        LightBulb(mood != MascotMood.Sleepy, animated && !ReduceMotion);
        if (!animated || ReduceMotion || Size <= 0) { SyncBlinking(); return; }
        switch (mood)
        {
            case MascotMood.Surprised: Pop(); break;
            case MascotMood.Confused: Wobble(); break;
            case MascotMood.Love: Heartbeat(); break;
            case MascotMood.Idle when previous == MascotMood.Sleepy: Pop(); break;
        }
        SyncBlinking();
    }

    /// <summary>An eye's 24 points around its centre, at this size.</summary>
    public PointD[] EyePoints(MascotEye eye, bool right)
    {
        var face = Look.Face;
        return MascotGeometry.EyePoints(eye, new SizeD(face.EyeSize.Width * Size, face.EyeSize.Height * Size), right);
    }

    private PointD GazeShift(PointD gaze) => new(gaze.X * Size, gaze.Y * Size);

    private void Apply(MascotExpression expression, bool animated)
    {
        if (Size <= 0) return;
        var left = EyePoints(expression.Left, false);
        var right = EyePoints(expression.Right, true);
        var gaze = GazeShift(expression.Gaze);
        var now = _clock();
        if (animated)
        {
            foreach (var (eye, points) in new[] { (_leftEye, left), (_rightEye, right) })
            {
                var from = eye.Value(now);
                eye.Add("morph", Basic(now, MorphDuration, from, points, MascotTiming.Settle));
            }
            var gazeFrom = _face.Value(now);
            _face.Add("gaze", Spring(now, GazeSpring, gazeFrom, gaze));
            var tiltFrom = _tilt.Value(now);
            var tiltTo = expression.Tilt;
            _tilt.Add("tilt", new MascotAnimation<double>
            {
                Begin = now, Duration = TiltSpring.SettlingDuration,
                Apply = (_, share) => tiltFrom + (tiltTo - tiltFrom) * TiltSpring.Progress(share * TiltSpring.SettlingDuration),
            });
        }
        _leftEye.Model = left;
        _rightEye.Model = right;
        _face.Model = gaze;
        _tilt.Model = expression.Tilt;
    }

    /// <summary>Turns to <paramref name="mood"/> at <paramref name="time"/> and keeps it there, for a face set ahead
    /// of a moment, such as landing. The face it keeps on its own stays as it was.</summary>
    public void Turn(MascotMood mood, double time)
    {
        if (Size <= 0) return;
        var from = Mood.Expression();
        var to = mood.Expression();
        var duration = ReduceMotion ? 0.01 : MorphDuration;
        foreach (var (eye, a, b, right) in new[] { (_leftEye, from.Left, to.Left, false), (_rightEye, from.Right, to.Right, true) })
            eye.Add("turn", Basic(time, duration, EyePoints(a, right), EyePoints(b, right), MascotTiming.Linear, holds: true));
        _face.Add("turn", Basic(time, duration, GazeShift(from.Gaze), GazeShift(to.Gaze), MascotTiming.Linear, holds: true));
        _tilt.Add("turn", Basic(time, duration, from.Tilt, to.Tilt, MascotTiming.Linear, holds: true));
    }

    /// <summary>Back to its own face at rest, with every motion taken off.</summary>
    public void Reset(MascotMood mood)
    {
        StopBlinking();
        _position.RemoveAll(); _opacity.RemoveAll(); _hop.RemoveAll(); _squash.RemoveAll(); _tilt.RemoveAll();
        _attention.RemoveAll(); _face.RemoveAll(); _leftEye.RemoveAll(); _rightEye.RemoveAll();
        _leftScale.RemoveAll(); _rightScale.RemoveAll(); _sway.RemoveAll();
        _props.Clear();
        _attention.Model = new PointD(0, 0);
        _following = null;
        Mood = mood;
        _blinks = 0;
        _dozed = false;
        Apply(mood.Expression(), animated: false);
    }

    // MARK: Attention

    /// <summary>Whether a stroll is moving it, with eyes for the way ahead.</summary>
    public bool IsVisiting
    {
        get
        {
            var now = Now();
            return _position.Get("visit", now) is not null && now < _visitEnds;
        }
    }

    /// <summary>Whether a visit under way has it standing at <paramref name="stand"/> just now, where ending the
    /// visit leaves it without a jump.</summary>
    public bool Stands(PointD stand)
    {
        var position = _position.Value(Now());
        return Math.Abs(position.X - stand.X) < 0.5 && Math.Abs(position.Y - stand.Y) < 0.5;
    }

    /// <summary>Ends a visit under way where it stands, and it blinks again.</summary>
    public void EndVisit()
    {
        var now = Now();
        RemoveVisit();
        _blinksResume = now;
        StopBlinking();
        SyncBlinking();
    }

    /// <summary>Lets go of the last frame a visit that ended out of sight held, once the island has nothing more for it.</summary>
    public void ClearFinishedVisit()
    {
        var now = Now();
        if (_position.Get("visit", now) is null || now < _visitEnds) return;
        RemoveVisit();
    }

    private void RemoveVisit()
    {
        _position.Remove("visit");
        _squash.Remove("visit");
        _face.Remove("visit");
        _opacity.Remove("visitFade");
    }

    /// <summary>Turns its eyes toward <paramref name="target"/>, in shares of its size, on top of the face it keeps.
    /// With <paramref name="hold"/>, they stay that long and come back. Without it, they stay. Each turn starts where
    /// the eyes are, never where the last one meant to go.</summary>
    public void Attend(PointD target, double? hold = null)
    {
        if (Size <= 0 || ReduceMotion) return;
        var now = Now();
        _attended = now;
        var from = _attention.Value(now);
        var to = new PointD(target.X * Size, target.Y * Size);
        var zero = new PointD(0, 0);
        if (hold is { } held)
        {
            const double move = 0.16, back = 0.34;
            var total = move + Math.Max(0, held) + back;
            _attention.Add("attention", Keyframes(now, total, [from, to, to, zero], [0, move / total, (total - back) / total, 1],
                                                  [MascotTiming.Settle, MascotTiming.Linear, MascotTiming.EaseInEaseOut]));
        }
        else
            _attention.Add("attention", Keyframes(now, 0.2, [from, to], overall: MascotTiming.Settle));
        _attention.Model = hold is null ? to : zero;
    }

    /// <summary>
    /// Keeps its eyes on a pointer that moves about, as the cursor does across the screen: like <see cref="Attend"/>,
    /// but a target within a fraction of a point of the last one changes nothing, so a still pointer sets no
    /// animation going and the view can rest.
    /// </summary>
    public void Follow(PointD target)
    {
        if (Size <= 0 || ReduceMotion) return;
        var to = new PointD(target.X * Size, target.Y * Size);
        if (_following is { } last && Math.Abs(last.X - to.X) < FollowThreshold && Math.Abs(last.Y - to.Y) < FollowThreshold)
        {
            _attended = Now();
            return;
        }
        _following = to;
        Attend(target);
    }

    /// <summary>Its eyes come back to the face it keeps, after <paramref name="delay"/>.</summary>
    public void LookAhead(double delay = 0)
    {
        if (Size <= 0) return;
        _following = null;
        var now = Now();
        var from = _attention.Value(now);
        var zero = new PointD(0, 0);
        _attention.Model = zero;
        if (!ReduceMotion && from != zero)
        {
            var total = Math.Max(0, delay) + 0.34;
            _attention.Add("attention", Keyframes(now, total, [from, from, zero], [0, Math.Max(0, delay) / total, 1],
                                                  [MascotTiming.Linear, MascotTiming.EaseInEaseOut]));
        }
        else _attention.Remove("attention");
    }

    // MARK: Cues

    public void Play(MascotCue cue)
    {
        if (Size <= 0 || ReduceMotion)
        {
            if (cue is MascotCue.Celebrate && ReduceMotion) FlashFace(MascotMood.Happy, 1);
            return;
        }
        var now = Now();
        switch (cue)
        {
            case MascotCue.Celebrate:
                Hop(Size * 0.28);
                FlashFace(MascotMood.Happy, 1);
                break;
            case MascotCue.Glance:
                _face.Add("glance", AdditiveX(now, 0.42, [0, Size * 0.07, -Size * 0.06, 0], [0, 0.3, 0.7, 1],
                                              overall: MascotTiming.EaseInEaseOut));
                break;
            case MascotCue.Look look:
                if (look.Target is { } target) Attend(target, 0.9); else LookAhead();
                break;
        }
    }

    private void Hop(double height)
    {
        var now = _clock();
        _hop.Add("hop", Additive(now, 0.62, [0, -height, 0, -height * 0.3, 0], [0, 0.32, 0.6, 0.78, 1],
                                 [MascotTiming.EaseOut, MascotTiming.EaseIn, MascotTiming.EaseOut, MascotTiming.EaseIn]));
        _squash.Add("squash", Scalar(now, 0.62, [0, -0.12, 0, 0.14, 0, 0.06, 0], [0, 0.12, 0.3, 0.62, 0.72, 0.84, 1]));
    }

    /// <summary>The robot's antenna bulb goes dark while it sleeps and lights again as it wakes.</summary>
    private void LightBulb(bool lit, bool animated)
    {
        if (Look.Style != MascotStyle.Robot) return;
        double target = lit ? 1 : 0;
        if (animated)
        {
            var now = _clock();
            _bulbLit.Add("light", Basic(now, lit ? 0.25 : 0.6, _bulbLit.Value(now), target, MascotTiming.Linear));
        }
        _bulbLit.Model = target;
    }

    /// <summary>Lit already, the bulb shows dark a moment longer, then lights.</summary>
    private void RelightBulb(double delay)
    {
        if (Look.Style != MascotStyle.Robot) return;
        _bulbLit.Add("light", Scalar(_clock(), delay + 0.3, [0, 0, 1], [0, delay / (delay + 0.3), 1]));
    }

    /// <summary>A reaction lights the robot's bulb up with a soft glow for as long as it plays.</summary>
    private void GlowBulb(double duration)
    {
        if (Look.Style != MascotStyle.Robot) return;
        _bulbGlow.Add("glow", Scalar(_clock(), duration, [0, 1, 1, 0], [0, 0.12, 0.7, 1]));
    }

    private void Pop() =>
        _squash.Add("pop", Scalar(_clock(), 0.34, [0, -0.14, 0.06, 0], [0, 0.35, 0.7, 1]));

    private void Wobble() =>
        _tilt.Add("wobble", Additive(_clock(), 0.5, [0, 0.12, -0.1, 0.06, 0], [0, 0.25, 0.5, 0.75, 1]));

    private void Heartbeat(double? begin = null)
    {
        var at = begin ?? _clock();
        foreach (var scale in new[] { _leftScale, _rightScale })
            scale.Add("heartbeat", Scale(at, 0.7, [1, 1.3, 1, 1.22, 1], [0, 0.2, 0.45, 0.65, 1], uniform: true));
    }

    /// <summary>A slow stretch up and back, as a yawn or waking up.</summary>
    private void Stretch(double begin, double duration) =>
        _squash.Add("stretch", Scalar(begin, duration, [0, -0.12, -0.12, 0.05, 0], [0, 0.35, 0.6, 0.82, 1],
                                      [MascotTiming.EaseOut, MascotTiming.Linear, MascotTiming.EaseIn, MascotTiming.EaseOut]));

    // MARK: Reactions

    /// <summary>A short reaction to something the island saw happen, over the face it keeps. <paramref name="lift"/>
    /// is how high a hop may take it where it stands. With reduced motion it only changes face.</summary>
    public void React(MascotReaction reaction, double lift)
    {
        if (Size <= 0) return;
        // Whatever it is, it wakes up for it.
        if (_dozed && reaction != MascotReaction.WakeUp) SetMood(MascotMood.Idle, !ReduceMotion);
        _blinks = 0;
        var now = Now();
        GlowBulb(reaction.Length());
        if (ReduceMotion)
        {
            switch (reaction)
            {
                case MascotReaction.Celebrate or MascotReaction.WakeUp: FlashFace(MascotMood.Happy, 1); break;
                case MascotReaction.Love: FlashFace(MascotMood.Love, 1.2); break;
                case MascotReaction.Surprised or MascotReaction.Flash: FlashFace(MascotMood.Surprised, 0.8); break;
                case MascotReaction.Confused: FlashFace(MascotMood.Confused, 1.1); break;
                case MascotReaction.Yawn or MascotReaction.Hush: FlashFace(MascotMood.Sleepy, 1.1); break;
                case MascotReaction.Perk or MascotReaction.Bounce: FlashFace(MascotMood.Alert, 1); break;
                case MascotReaction.Ready: FlashFace(MascotMood.Determined, 1); break;
                case MascotReaction.Groove: FlashFace(MascotMood.Happy, 1.4); break;
                case MascotReaction.MusicPaused or MascotReaction.Dim: FlashFace(MascotMood.Sleepy, 1.1); break;
                case MascotReaction.MusicSkipped or MascotReaction.Notify: FlashFace(MascotMood.Alert, 0.8); break;
                case MascotReaction.Loud: FlashFace(MascotMood.Surprised, 0.9); break;
                case MascotReaction.Bright: FlashFace(MascotMood.Wince, 1.1); break;
                case MascotReaction.HeadphonesIn: FlashFace(MascotMood.Happy, 1.2); break;
                case MascotReaction.HeadphonesOut: FlashFace(MascotMood.Searching, 1); break;
            }
            return;
        }
        switch (reaction)
        {
            case MascotReaction.Celebrate:
                // A big hop with a full turn in the air, and sparkles thrown off.
                Hop(lift);
                FlashFace(MascotMood.Happy, 1);
                _tilt.Add("spin", Additive(now + 0.05, 0.62, [0, Math.Tau], [0, 1], overall: MascotTiming.EaseInEaseOut));
                AddProp(MascotPropKind.Spark, now + 0.10, 0.8, (0.55, -0.1), (0.9, -0.38), 0.4, 1.2, 0.5);
                AddProp(MascotPropKind.Spark, now + 0.22, 0.75, (0.7, 0.12), (1.05, 0.05), 0.3, 1.2, -0.5);
                AddProp(MascotPropKind.Spark, now + 0.34, 0.64, (0.5, -0.3), (0.65, -0.58), 0.25, 1.2, 0.8);
                break;
            case MascotReaction.Love:
                // A slow heartbeat, swaying side to side as hearts float up.
                FlashFace(MascotMood.Love, 1.4);
                Heartbeat(now + 0.22);
                Sway(now, 1.4, [0, 0.07, -0.07, 0.06, -0.06, 0], [0, 0.2, 0.4, 0.6, 0.8, 1]);
                AddProp(MascotPropKind.Heart, now + 0.2, 1.2, (0.6, -0.05), (0.78, -0.5), 0.4);
                AddProp(MascotPropKind.Heart, now + 0.5, 0.9, (0.78, 0.1), (0.98, -0.4), 0.3);
                AddProp(MascotPropKind.Heart, now + 0.8, 0.6, (0.55, -0.1), (0.62, -0.5), 0.25);
                break;
            case MascotReaction.Surprised:
                // Jumps back, stretched tall and leaning away, with an exclamation mark.
                FlashFace(MascotMood.Surprised, 0.9);
                Hop(lift * 0.9);
                _squash.Add("startle", Scalar(now, 0.5, [0, -0.25, -0.1, 0.06, 0], [0, 0.18, 0.45, 0.7, 1]));
                Lean("startle", now, 0.9, [0, -0.3, -0.3, -0.05, 0], [0, 0.12, 0.5, 0.8, 1], -Outward);
                AddProp(MascotPropKind.Bang, now + 0.05, 0.85, (0.72, -0.2), (0.72, -0.28), 0.5);
                break;
            case MascotReaction.Flash:
                // Squeezed shut, then wide open, as a camera's flash goes off.
                foreach (var scale in new[] { _leftScale, _rightScale })
                    scale.Add("flashBlink", Scale(now, 0.45, [1, 0.06, 0.06, 1.18, 1], [0, 0.18, 0.42, 0.7, 1]));
                Pop();
                PauseBlinking(0.5, null);
                break;
            case MascotReaction.Confused:
                // A puzzled head-shake, "no", with a question mark rising.
                FlashFace(MascotMood.Confused, 1.2);
                Lean("shake", now, 1.1, [0, 0.22, -0.22, 0.18, -0.18, 0.1, 0], [0, 0.16, 0.34, 0.52, 0.7, 0.86, 1], 1);
                AddProp(MascotPropKind.Question, now + 0.1, 1.1, (0.68, -0.2), (0.82, -0.5), 0.5);
                break;
            case MascotReaction.WakeUp:
                // Heavy eyes, a stretch, and a glad face. A robot's bulb lights up again as it stretches.
                if (_dozed)
                {
                    SetMood(MascotMood.Idle, false);
                    RelightBulb(0.35);
                }
                FlashFace(MascotMood.Sleepy, 0.7);
                Stretch(now + 0.35, 0.6);
                FlashFace(MascotMood.Happy, 0.9, now + 0.7, "flashAfter");
                AddProp(MascotPropKind.Spark, now + 0.75, 0.7, (0.6, -0.15), (0.8, -0.4), 0.3, 1, 0.4);
                AddProp(MascotPropKind.Spark, now + 0.9, 0.6, (0.75, 0.05), (0.95, -0.05), 0.22, 1, -0.4);
                // The sun is up.
                AddProp(MascotPropKind.Rays, now + 0.7, 0.8, (0.55, -0.3), (0.6, -0.34), 0.45, 1.1, 0.2);
                break;
            case MascotReaction.Yawn:
                // A slow stretch, eyes heavy, and a "z" drifting up.
                FlashFace(MascotMood.Sleepy, 1.2);
                Stretch(now + 0.1, 0.9);
                AddProp(MascotPropKind.Zee, now + 0.3, 0.9, (0.6, -0.1), (0.8, -0.45), 0.34);
                AddProp(MascotPropKind.Zee, now + 0.6, 0.6, (0.75, -0.2), (0.95, -0.6), 0.26);
                break;
            case MascotReaction.Perk:
                // Ears up: a quick stretch tall, a small hop, and a ring spreading out.
                FlashFace(MascotMood.Alert, 1);
                Hop(lift * 0.4);
                _squash.Add("perk", Scalar(now, 0.5, [0, -0.28, -0.05, 0.06, 0], [0, 0.18, 0.45, 0.7, 1]));
                AddProp(MascotPropKind.Ring, now, 0.6, (0, 0), (0, 0), 0.7, 1.6);
                break;
            case MascotReaction.Bounce:
            {
                // Time to go: two quick hops, the second lower, and a small rebound, squashing as it lands and
                // stretching to leave again.
                FlashFace(MascotMood.Alert, reaction.Length());
                var height = Math.Max(1, lift * 0.6);
                _hop.Add("hop", Additive(now, 0.95, [0, -height, 0, -height * 0.7, 0, -height * 0.15, 0],
                                         [0, 0.14, 0.32, 0.46, 0.63, 0.75, 0.88], Alternating(6)));
                _squash.Add("squash", Scalar(now, 0.95, [0, -0.12, 0, 0.14, -0.1, 0, 0.12, 0, 0.04, 0],
                                             [0, 0.04, 0.14, 0.32, 0.36, 0.46, 0.63, 0.7, 0.8, 0.88]));
                // A clock face ticks beside it: time to go.
                AddProp(MascotPropKind.Clock, now, 0.95, (0.7, -0.15), (0.7, -0.22), 0.5, 1, 0.6);
                break;
            }
            case MascotReaction.Hush:
                // Eyes shut for a moment as it ducks, then back.
                foreach (var scale in new[] { _leftScale, _rightScale })
                    scale.Add("hush", Scale(now, 0.95, [1, 0.1, 0.1, 1], [0, 0.22, 0.78, 1]));
                _squash.Add("duck", Scalar(now, 0.95, [0, 0.1, 0.1, -0.03, 0], [0, 0.25, 0.7, 0.88, 1]));
                PauseBlinking(1, null);
                AddProp(MascotPropKind.Dots, now + 0.1, 0.9, (0.7, -0.2), (0.7, -0.2), 0.45);
                break;
            case MascotReaction.Ready:
                FlashFace(MascotMood.Determined, 1);
                // A nod: down a little, up past where it was, and settled, leaning into it, with go-lines behind.
                _squash.Add("nod", Scalar(now + 0.12, 0.5, [0, 0.1, -0.06, 0], [0, 0.3, 0.65, 1]));
                Lean("lean", now + 0.05, 0.6, [0, 0.14, 0.14, 0], [0, 0.3, 0.7, 1], Outward);
                Sway(now + 0.05, 0.6, [0, 0.08, 0.08, 0], [0, 0.3, 0.7, 1]);
                AddProp(MascotPropKind.Lines, now + 0.15, 0.6, (0.5, 0.05), (0.75, 0.05), 0.4);
                break;
            case MascotReaction.Groove:
            {
                // It hears the music: smiling eyes, a bob on every beat and a lean one way and the other, landing a
                // little squashed.
                var length = reaction.Length();
                FlashFace(MascotMood.Happy, length);
                const int beats = 4;
                var height = Math.Max(1, lift * 0.45);
                var heights = new List<double>();
                var leans = new List<double>();
                var squashes = new List<double>();
                var times = new List<double>();
                for (var step = 0; step <= beats * 2; step++)
                {
                    times.Add((double)step / (beats * 2));
                    var up = step % 2 == 1;
                    heights.Add(up ? -height : 0);
                    leans.Add(up ? (step % 4 == 1 ? 0.11 : -0.11) : 0);
                    squashes.Add(up ? -0.05 : step == 0 || step == beats * 2 ? 0 : 0.07);
                }
                _hop.Add("hop", Additive(now, length, heights, times, Alternating(beats * 2)));
                _tilt.Add("wobble", Additive(now, length, leans, times, overall: MascotTiming.EaseInEaseOut));
                _squash.Add("squash", Scalar(now, length, squashes, times));
                // Notes float off to the beat.
                AddProp(MascotPropKind.Note, now + 0.1, 0.9, (0.6, -0.05), (0.8, -0.5), 0.4, 1, 0.2);
                AddProp(MascotPropKind.Note, now + 0.5, 0.9, (0.75, 0.1), (0.95, -0.35), 0.32, 1, -0.2);
                AddProp(MascotPropKind.Note, now + 0.9, 0.7, (0.55, -0.1), (0.7, -0.55), 0.28, 1, 0.2);
                break;
            }
            case MascotReaction.MusicPaused:
                // The music stops: it sags, eyes heavy, with a pause mark.
                FlashFace(MascotMood.Sleepy, 1.1);
                _squash.Add("sag", Scalar(now, 1.1, [0, 0.14, 0.14, 0], [0, 0.3, 0.75, 1]));
                Lean("droop", now, 1.1, [0, 0.1, 0.1, 0], [0, 0.3, 0.75, 1], -Outward);
                AddProp(MascotPropKind.Pause, now + 0.1, 1.0, (0.68, -0.15), (0.68, -0.15), 0.42);
                break;
            case MascotReaction.MusicSkipped:
                // A quick flick of the head the way the song went, with speed lines.
                FlashFace(MascotMood.Searching, 0.5);
                Sway(now, 0.45, [0, 0.14, 0], [0, 0.3, 1], MascotTiming.EaseOut);
                Lean("flick", now, 0.45, [0, 0.25, 0], [0, 0.3, 1], Outward);
                AddProp(MascotPropKind.Skip, now, 0.55, (0.6, -0.05), (1.0, -0.05), 0.42);
                break;
            case MascotReaction.Notify:
                // A glance toward the notice and a perk-up, with a badge popping on.
                Play(new MascotCue.Glance());
                FlashFace(MascotMood.Alert, 0.9);
                Hop(lift * 0.35);
                AddProp(MascotPropKind.Badge, now + 0.1, 0.8, (0.55, -0.3), (0.55, -0.3), 0.3, 1.15);
                break;
            case MascotReaction.Loud:
                // Too loud: it flinches away, squashed, eyes squeezed, sound arcs washing over it.
                FlashFace(MascotMood.Wince, 1.0);
                _squash.Add("flinch", Scalar(now, 1.0, [0, 0.2, 0.2, 0.05, 0], [0, 0.12, 0.5, 0.8, 1]));
                Lean("flinch", now, 1.0, [0, -0.25, -0.25, -0.05, 0], [0, 0.12, 0.5, 0.8, 1], -Outward);
                Sway(now, 1.0, [0, -0.1, -0.1, -0.02, 0], [0, 0.12, 0.5, 0.8, 1]);
                AddProp(MascotPropKind.Arcs, now, 0.8, (0.62, 0), (0.78, 0), 0.55, 1.3);
                break;
            case MascotReaction.Bright:
                // Too bright: it squints and draws back, with glare rays.
                FlashFace(MascotMood.Wince, 1.1);
                _squash.Add("glare", Scalar(now, 1.1, [0, 0.1, 0.1, 0], [0, 0.2, 0.7, 1]));
                Lean("glare", now, 1.1, [0, 0.12, 0.12, 0], [0, 0.2, 0.7, 1], Outward);
                AddProp(MascotPropKind.Rays, now, 1.0, (0.55, -0.3), (0.6, -0.34), 0.5, 1.1, 0.3);
                break;
            case MascotReaction.Dim:
                // Nearly dark: one slow, sleepy blink and a gentle settle.
                FlashFace(MascotMood.Sleepy, 0.9);
                foreach (var scale in new[] { _leftScale, _rightScale })
                    scale.Add("dim", Scale(now, 0.9, [1, 0.12, 0.12, 1], [0, 0.35, 0.65, 1]));
                _squash.Add("settle", Scalar(now, 0.9, [0, 0.06, 0.06, 0], [0, 0.35, 0.65, 1]));
                PauseBlinking(0.9, null);
                break;
            case MascotReaction.HeadphonesIn:
                // Headphones on: two happy bobs, with the band over its head.
                FlashFace(MascotMood.Happy, 1.2);
                var bob = Math.Max(1, lift * 0.3);
                _hop.Add("hop", Additive(now, 1.2, [0, -bob, 0, -bob, 0], [0, 0.25, 0.5, 0.75, 1], Alternating(4)));
                Lean("bob", now, 1.2, [0, 0.1, -0.1, 0.1, 0], [0, 0.25, 0.5, 0.75, 1], 1);
                AddProp(MascotPropKind.Band, now + 0.1, 1.3, (0, -0.1), (0, -0.1), 1.1);
                break;
            case MascotReaction.HeadphonesOut:
                // Headphones off: a puzzled shrug and a bead of sweat.
                FlashFace(MascotMood.Searching, 1.0);
                _squash.Add("shrug", Scalar(now, 1.0, [0, -0.1, 0.1, -0.1, 0], [0, 0.2, 0.45, 0.7, 1]));
                Lean("shrug", now, 1.0, [0, 0.1, -0.1, 0], [0, 0.3, 0.65, 1], 1);
                AddProp(MascotPropKind.Drop, now + 0.1, 0.9, (0.62, -0.35), (0.62, 0.05), 0.35);
                break;
        }
    }

    // MARK: Motion helpers for reactions

    /// <summary>A nudge sideways of the given shares of its size (positive away from the camera), additive.</summary>
    private void Sway(double begin, double duration, IReadOnlyList<double> shares, IReadOnlyList<double> keyTimes,
                      MascotTiming? overall = null) =>
        _sway.Add("sway", Additive(begin, duration, shares.Select(share => share * Size * Outward).ToArray(), keyTimes, overall: overall));

    /// <summary>A lean of the given radians (positive clockwise) times <paramref name="direction"/>, additive.</summary>
    private void Lean(string key, double begin, double duration, IReadOnlyList<double> radians, IReadOnlyList<double> keyTimes,
                      double direction) =>
        _tilt.Add(key, Additive(begin, duration, radians.Select(value => value * direction).ToArray(), keyTimes));

    private void AddProp(MascotPropKind kind, double begin, double duration, (double X, double Y) from, (double X, double Y) to,
                         double size, double growTo = 1, double spin = 0) =>
        _props.Add(new MascotProp(kind, begin, duration, new PointD(from.X, from.Y), new PointD(to.X, to.Y), size, growTo, spin));

    private static MascotTiming[] Alternating(int count) =>
        Enumerable.Range(0, count).Select(i => i % 2 == 0 ? MascotTiming.EaseOut : MascotTiming.EaseIn).ToArray();

    /// <summary>Shows another face for a moment and comes back to its own, without changing the face it keeps.</summary>
    private void FlashFace(MascotMood mood, double duration, double? begin = null, string key = "flash")
    {
        var expression = mood.Expression();
        var own = Mood.Expression();
        var now = _clock();
        foreach (var (channel, eye, @base, right) in new[]
                 {
                     (_leftEye, expression.Left, own.Left, false), (_rightEye, expression.Right, own.Right, true),
                 })
        {
            var back = EyePoints(@base, right);
            var to = EyePoints(eye, right);
            // One shown now leaves from the eyes on screen, heavy from a doze or shut mid-blink, rather than from
            // its own face for a frame.
            var from = begin is null ? channel.Value(now) : back;
            if (from.Length != to.Length) from = back;
            channel.Add(key, Keyframes(begin ?? now, duration, [from, to, to, back], [0, 0.15, 0.85, 1],
                                       discrete: ReduceMotion));
        }
        PauseBlinking(duration, begin);
    }

    // MARK: Visits

    /// <summary>
    /// Plays a stroll that began at <paramref name="start"/> from where it should be now. <paramref name="baseline"/>
    /// is the height of its box's centre in the stage. With reduced motion it does not walk, and greets at
    /// <paramref name="stand"/> instead. <paramref name="holdsEnd"/> keeps it where the visit ends, out of sight,
    /// until what comes next takes over.
    /// </summary>
    public void PlayVisit(MascotVisitPath path, MascotMood greeting, double baseline, PointD stand, double start,
                          bool holdsEnd = false)
    {
        if (Size <= 0 || path.Duration <= 0 || path.X.Count != path.KeyTimes.Count) return;
        var now = Now();
        // One that already ended out of sight still holds its last frame for a strip drawn after it.
        if (!(now < start + path.Duration || (holdsEnd && !ReduceMotion))) return;
        _visitEnds = start + path.Duration;
        var greets = path.Greeting.Upper > path.Greeting.Lower;
        if (ReduceMotion)
        {
            // No stroll: it fades in where it would greet, says hello and fades out.
            _position.Add("visit", new MascotAnimation<PointD> { Begin = start, Duration = path.Duration, Apply = (_, _) => stand });
            if (_position.Model != stand)
            {
                // Staying on from where it stood, as to react over an activity, it only fades out at the end.
                var inPlace = path.X.Count > 0 && Math.Abs(path.X[0] - stand.X) < 0.5;
                _opacity.Add("visitFade", Scalar(start, path.Duration, [inPlace ? 1 : 0, 1, 1, 0], [0, 0.12, 0.88, 1]));
            }
            if (greets) FlashFace(greeting, path.Greeting.Upper - path.Greeting.Lower, start + path.Greeting.Lower);
            return;
        }
        var duration = path.Duration;
        _position.Add("visit", new MascotAnimation<PointD>
        {
            Begin = start, Duration = duration, FillsForwards = holdsEnd, RemovedOnCompletion = !holdsEnd,
            Apply = (_, share) =>
            {
                var sample = path.Sample(share * duration);
                return new PointD(sample.X, baseline - sample.Lift);
            },
        });
        _squash.Add("visit", new MascotAnimation<double>
        {
            Begin = start, Duration = duration, Apply = (_, share) => path.Sample(share * duration).Squash,
        });
        var size = Size;
        _face.Add("visit", new MascotAnimation<PointD>
        {
            Begin = start, Duration = duration,
            Apply = (value, share) => new PointD(value.X + path.Sample(share * duration).Gaze * size, value.Y),
        });
        // Its eyes are for the way ahead while it walks.
        LookAhead();
        if (greets) FlashFace(greeting, path.Greeting.Upper - path.Greeting.Lower, start + path.Greeting.Lower);
        // No blink while it walks, as the face it shows is the greeting's.
        PauseBlinking(start + duration - now, null);
    }

    // MARK: Blinking

    /// <summary>Blinks wait while it is out of sight, and <paramref name="visible"/> resumes them.</summary>
    public void SetIdles(bool idles, bool visible)
    {
        var wanted = idles && visible;
        if (wanted == Idles) return;
        Idles = wanted;
        if (!wanted)
        {
            StopBlinking();
            if (_dozed && !idles) SetMood(MascotMood.Idle, false);
        }
        else SyncBlinking();
    }

    /// <summary>Wakes it from a long rest. A face shown sleepy on purpose stays.</summary>
    public void Wake(bool animated)
    {
        _blinks = 0;
        if (_dozed) SetMood(MascotMood.Idle, animated);
    }

    private void SyncBlinking()
    {
        if (!Idles || !Mood.Expression().Blinks || Size <= 0) { StopBlinking(); return; }
        if (_leftScale.Get("blink", _clock()) is not null) return;
        ScheduleBlink();
    }

    private void StopBlinking()
    {
        _blinkEnds = null;
        _leftScale.Remove("blink");
        _rightScale.Remove("blink");
        // A glance on its way goes with the blink it led to; one under way finishes, so the eyes never jump.
        if (_attention.Get("idleGlance", _clock()) is { } glance && glance.Begin > _clock()) _attention.Remove("idleGlance");
    }

    private void PauseBlinking(double duration, double? begin)
    {
        var end = (begin ?? _clock()) + Math.Max(0, duration);
        _blinksResume = Math.Max(_blinksResume, end);
        if (!Idles) return;
        StopBlinking();
        SyncBlinking();
    }

    /// <summary>One blink, a few seconds from now. Its end schedules the next one.</summary>
    private void ScheduleBlink()
    {
        var now = _clock();
        var (min, max) = MascotSupport.BlinkInterval(LowPower);
        var delay = Math.Max(0, _blinksResume - now) + min + (max - min) * _random();
        var twice = _random() < MascotSupport.DoubleBlinkChance;
        double[] values = twice ? [1, 0.1, 1, 1, 0.1, 1] : [1, 0.1, 1];
        double[] keyTimes = twice ? [0, 0.2, 0.4, 0.58, 0.78, 1] : [0, 0.45, 1];
        var duration = twice ? 0.44 : 0.17;
        var begin = now + delay;
        // Now and then its eyes wander to one side, and the blink comes as they find their way back, as eyes do.
        // Not while they follow something, which would pull them apart.
        if (!twice && !ReduceMotion && now - _attended > MascotSupport.GlanceAfterAttention
            && _random() < MascotSupport.GlanceChance)
        {
            var away = (_random() < 0.5 ? 1 : -1) * Size * 0.12;
            _attention.Add("idleGlance", AdditiveX(begin - 1.1, 1.3, [0, away, away, 0], [0, 0.2, 0.8, 1],
                                                   [MascotTiming.Settle, MascotTiming.Linear, MascotTiming.EaseInEaseOut]));
        }
        _blinkEnds = begin + duration;
        foreach (var scale in new[] { _leftScale, _rightScale })
            scale.Add("blink", Scale(begin, duration, values, keyTimes, discrete: ReduceMotion));
    }

    private void BlinkDidStop()
    {
        if (!Idles) return;
        _blinks++;
        // A long rest makes its eyes heavy, and it stops blinking then.
        if (Mood == MascotMood.Idle && _blinks >= MascotSupport.BlinksBeforeSleep(_hour()))
        {
            SetMood(MascotMood.Sleepy, true);
            _dozed = true;
            return;
        }
        SyncBlinking();
    }

    // MARK: Frames

    /// <summary>
    /// When its frame next changes on its own: now while anything moves, when the next blink or motion begins, or
    /// null while it keeps still until told otherwise. Windows-specific: the view draws only until then.
    /// </summary>
    public double? NextChange()
    {
        var now = Now();
        if (_previousParts is not null) return now;
        double? next = _blinkEnds;
        foreach (var change in new[]
                 {
                     _position.NextChange(now), _opacity.NextChange(now), _hop.NextChange(now), _squash.NextChange(now),
                     _tilt.NextChange(now), _attention.NextChange(now), _face.NextChange(now), _leftEye.NextChange(now),
                     _rightEye.NextChange(now), _leftScale.NextChange(now), _rightScale.NextChange(now),
                     _bulbLit.NextChange(now), _bulbGlow.NextChange(now), _sway.NextChange(now), PropsNextChange(now),
                 })
            next = MascotTime.Earliest(next, change);
        return next;
    }

    /// <summary>Now while a prop shows, the start of the next one waiting, or null.</summary>
    private double? PropsNextChange(double now)
    {
        double? next = null;
        foreach (var prop in _props)
        {
            if (now >= prop.End) continue;
            next = MascotTime.Earliest(next, Math.Max(now, prop.Begin));
        }
        return next;
    }

    /// <summary>The companion now, ready to draw. Null until it is configured with a size.</summary>
    public MascotFrame? Frame()
    {
        var now = Now();
        if (_parts is null || Size <= 0) return null;
        foreach (var prune in new Action<double>[]
                 {
                     _position.Prune, _opacity.Prune, _hop.Prune, _squash.Prune, _tilt.Prune, _attention.Prune,
                     _face.Prune, _leftEye.Prune, _rightEye.Prune, _leftScale.Prune, _rightScale.Prune,
                     _bulbLit.Prune, _bulbGlow.Prune, _sway.Prune,
                 })
            prune(now);
        _props.RemoveAll(prop => now >= prop.End);
        var size = Size;
        var center = new PointD(size / 2, size / 2);
        var position = _position.Value(now);
        var hop = _hop.Value(now);
        var squash = _squash.Value(now);
        var tilt = _tilt.Value(now);
        var face = _face.Value(now);
        var attention = _attention.Value(now);
        // What the eyes follow turns the head and the body toward it a little.
        var lean = attention.X / size * LeanPerGaze;
        var transform = MascotMatrix.About(center, MascotMatrix.Rotation(tilt + lean))
            .Then(Squashed(squash, size))
            .Then(MascotMatrix.Translation(0, hop))
            .Then(MascotMatrix.Translation(position.X - center.X + attention.X * BodyShiftShare + _sway.Value(now),
                                           position.Y - center.Y + attention.Y * BodyShiftShare));
        var shift = new PointD(face.X + attention.X, face.Y + attention.Y);
        var layout = Look.Face;
        var leftScale = _leftScale.Value(now);
        var rightScale = _rightScale.Value(now);
        PointD[] Place(PointD[] points, PointD eye, PointD scale) => points
            .Select(p => new PointD(p.X * scale.X + eye.X * size + shift.X, p.Y * scale.Y + eye.Y * size + shift.Y)).ToArray();
        var robot = Look.Style == MascotStyle.Robot;
        var palette = Look.Palette;
        var fade = _previousParts is null ? 1 : MascotTiming.EaseInEaseOut.Ease((now - _restyled) / RestyleFade);
        if (fade >= 1) _previousParts = null;
        // The props float in the stage, from the figure's middle: not hopping or squashing with it.
        var figureCenter = new PointD(position.X, position.Y + MascotGeometry.FigureOffset(Look) * size);
        var props = ReduceMotion ? Array.Empty<MascotPropFrame>()
            : _props.Select(prop => prop.Sample(now, size, Outward)).OfType<MascotPropFrame>().ToArray();
        return new MascotFrame(
            _parts, transform, _opacity.Value(now),
            Place(_leftEye.Value(now), layout.LeftEye, leftScale), Place(_rightEye.Value(now), layout.RightEye, rightScale),
            robot ? palette.Light() : MascotColor.Ink, robot ? 0.9 : 0, Math.Max(0.6, size * 0.04),
            MascotColor.Lerp(palette.Shade(), palette.Light(), _bulbLit.Value(now)),
            robot ? _bulbGlow.Value(now) : 0, Math.Max(0.8, size * 0.09),
            _previousParts, fade)
        {
            Position = position, Hop = hop, Squash = squash, Tilt = tilt, EyeShift = shift,
            LeftEyeScale = leftScale, RightEyeScale = rightScale, Props = props, FigureCenter = figureCenter,
        };
    }

    /// <summary>Positive flattens it on the ground, negative stretches it up, and its bottom stays where it was.
    /// About the box's centre.</summary>
    public static MascotMatrix Squashed(double amount, double size)
    {
        var scaleX = 1 + amount * 0.8;
        var scaleY = 1 - amount;
        var bottom = size * 0.42;
        return MascotMatrix.About(new PointD(size / 2, size / 2),
                                  MascotMatrix.Scale(scaleX, scaleY).Then(MascotMatrix.Translation(0, bottom * (1 - scaleY))));
    }

    // MARK: Animation builders

    private static MascotAnimation<double> Scalar(double begin, double duration, IReadOnlyList<double> values,
                                                  IReadOnlyList<double>? keyTimes = null,
                                                  IReadOnlyList<MascotTiming>? timings = null,
                                                  MascotTiming? overall = null) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (_, share) => MascotKeyframes.Value(share, values, keyTimes, timings, overall),
    };

    private static MascotAnimation<double> Additive(double begin, double duration, IReadOnlyList<double> values,
                                                    IReadOnlyList<double>? keyTimes = null,
                                                    IReadOnlyList<MascotTiming>? timings = null,
                                                    MascotTiming? overall = null) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (value, share) => value + MascotKeyframes.Value(share, values, keyTimes, timings, overall),
    };

    private static MascotAnimation<PointD> AdditiveX(double begin, double duration, IReadOnlyList<double> values,
                                                     IReadOnlyList<double>? keyTimes = null,
                                                     IReadOnlyList<MascotTiming>? timings = null,
                                                     MascotTiming? overall = null) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (value, share) => new PointD(value.X + MascotKeyframes.Value(share, values, keyTimes, timings, overall), value.Y),
    };

    /// <summary>transform.scale.y, or transform.scale for both axes.</summary>
    private static MascotAnimation<PointD> Scale(double begin, double duration, IReadOnlyList<double> values,
                                                 IReadOnlyList<double> keyTimes, bool uniform = false,
                                                 bool discrete = false) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (value, share) =>
        {
            var scale = MascotKeyframes.Value(share, values, keyTimes, discrete: discrete);
            return uniform ? new PointD(scale, scale) : new PointD(value.X, scale);
        },
    };

    private static MascotAnimation<PointD> Keyframes(double begin, double duration, IReadOnlyList<PointD> values,
                                                     IReadOnlyList<double>? keyTimes = null,
                                                     IReadOnlyList<MascotTiming>? timings = null,
                                                     MascotTiming? overall = null) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (_, share) =>
        {
            var (index, fraction) = MascotKeyframes.Segment(share, values.Count, keyTimes, timings, overall, false);
            if (index + 1 >= values.Count) return values[^1];
            return new PointD(values[index].X + (values[index + 1].X - values[index].X) * fraction,
                              values[index].Y + (values[index + 1].Y - values[index].Y) * fraction);
        },
    };

    private static MascotAnimation<PointD[]> Keyframes(double begin, double duration, IReadOnlyList<PointD[]> values,
                                                       IReadOnlyList<double> keyTimes, bool discrete) => new()
    {
        Begin = begin, Duration = duration,
        Apply = (_, share) =>
        {
            var (index, fraction) = MascotKeyframes.Segment(share, values.Count, keyTimes, null, null, discrete);
            return index + 1 >= values.Count ? values[^1] : MascotGeometry.Morph(values[index], values[index + 1], fraction);
        },
    };

    private static MascotAnimation<PointD[]> Basic(double begin, double duration, PointD[] from, PointD[] to,
                                                   MascotTiming timing, bool holds = false)
    {
        if (from.Length != to.Length) from = to;
        return new()
        {
            Begin = begin, Duration = duration, FillsForwards = holds, FillsBackwards = holds, RemovedOnCompletion = !holds,
            Apply = (_, share) => MascotGeometry.Morph(from, to, timing.Ease(share)),
        };
    }

    private static MascotAnimation<PointD> Basic(double begin, double duration, PointD from, PointD to, MascotTiming timing,
                                                 bool holds = false) => new()
    {
        Begin = begin, Duration = duration, FillsForwards = holds, FillsBackwards = holds, RemovedOnCompletion = !holds,
        Apply = (_, share) =>
        {
            var eased = timing.Ease(share);
            return new PointD(from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased);
        },
    };

    private static MascotAnimation<double> Basic(double begin, double duration, double from, double to, MascotTiming timing,
                                                 bool holds = false) => new()
    {
        Begin = begin, Duration = duration, FillsForwards = holds, FillsBackwards = holds, RemovedOnCompletion = !holds,
        Apply = (_, share) => from + (to - from) * timing.Ease(share),
    };

    private static MascotAnimation<PointD> Spring(double begin, MascotSpring spring, PointD from, PointD to)
    {
        var duration = spring.SettlingDuration;
        return new()
        {
            Begin = begin, Duration = duration,
            Apply = (_, share) =>
            {
                var progress = spring.Progress(share * duration);
                return new PointD(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);
            },
        };
    }
}
