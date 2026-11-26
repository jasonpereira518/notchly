// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/UI/Notch/NotchMascotView.swift (NotchMascotHostView, NotchMascotView and
// NotchMascotTrackView): the companion in a view — where it stands and can be seen, its eyes on the pointer, a pat
// on the head, reactions played once while fresh, visits with a cameo's reaction as it lands, the stage growing
// between sizes and fading as an activity takes its place. Windows-specific: the view reports the pointer and its
// own visibility, and samples Frame() on each render.
namespace Notchly.Core.Notch.Mascot;

public sealed class MascotStage
{
    private readonly Func<double> _clock;
    private readonly MascotChannel<double> _scale = new(1);
    private readonly MascotChannel<double> _yield = new(1);

    private Guid? _playedVisit;
    private int _lastCueId;
    /// <summary>The face last asked for. Asked again, it changes nothing: a companion grown sleepy on its own stays
    /// so until something new happens.</summary>
    private MascotMood? _requestedMood;
    private PointD? _center;
    private IReadOnlyList<RectD>? _visible;
    private bool _idlesWhenVisible;
    private bool _followsPointer;
    private bool _attached;
    private bool _onScreen = true;
    private Guid? _playedReaction;
    /// <summary>A fresh reaction for a stage not yet on screen.</summary>
    private (MascotReactionEvent Event, double Lift)? _waitingReaction;
    /// <summary>The pointer resting on it this long brings the hearts.</summary>
    private double? _pettingDue;
    private double _lastPetting = double.NegativeInfinity;
    /// <summary>A cameo's reaction, waiting for it to land.</summary>
    private (double Due, MascotReaction Reaction, double Lift)? _cameoDue;
    private double? _yieldEnds;

    /// <param name="clock">Seconds on a steady clock; the rig and every visit and reaction share it.</param>
    public MascotStage(Func<double> clock, Func<double>? random = null, Func<int>? hour = null)
    {
        _clock = clock;
        Mascot = new MascotRig(clock, random, hour);
    }

    public MascotRig Mascot { get; }

    /// <summary>The host view's size; with no place given, it stands in its middle.</summary>
    public SizeD ViewSize { get; set; }

    /// <summary>The view is in a window. Reactions published before it was wait for it.</summary>
    public bool Attached
    {
        get => _attached;
        set
        {
            if (_attached == value) return;
            _attached = value;
            SyncIdling();
            if (value && _waitingReaction is { } waiting)
            {
                _waitingReaction = null;
                PlayReaction(waiting.Event, waiting.Lift);
            }
        }
    }

    /// <summary>The view is visible on screen: blinks wait while it is not.</summary>
    public bool OnScreen
    {
        get => _onScreen;
        set { if (_onScreen != value) { _onScreen = value; SyncIdling(); } }
    }

    /// <summary>A resting companion's eyes follow the pointer over the island, and the pointer wakes it.</summary>
    public bool FollowsPointer
    {
        get => _followsPointer;
        set
        {
            if (_followsPointer == value) return;
            _followsPointer = value;
            if (!value)
            {
                _pettingDue = null;
                Mascot.LookAhead();
            }
        }
    }

    /// <summary>Its box's offset from the figure's own middle, so that what a caller centres is the body that shows.</summary>
    public double FigureShift => MascotGeometry.FigureOffset(Mascot.Look) * Mascot.Size;

    // MARK: The two views

    /// <summary>
    /// The companion on its own, centred in its view: the Settings preview and a notice's icon. Call on every
    /// layout update; <paramref name="cue"/> plays whenever <paramref name="cueId"/> changes. A reaction given the
    /// first time plays once, as it first shows, a little higher for its size to read.
    /// </summary>
    public void ShowAlone(MascotLook look, double size, MascotMood mood, bool idles, bool reduceMotion,
                          MascotCue? cue = null, int cueId = 0, MascotReaction? reaction = null, bool first = false)
    {
        Configure(look, size, mood, idles, reduceMotion, animated: !first);
        if (first && reaction is { } played) React(MascotReactionEvent.Now(played, _clock()), size * 0.32);
        Place(null, null);
        if (cue is not null && !first) Play(cue, cueId);
    }

    /// <summary>
    /// The companion in a closed strip: resting beside the camera, or strolling through on a visit, hidden behind
    /// the camera as it passes. <paramref name="awake"/> false shows it asleep, as the Settings preview does while
    /// it is switched off. Call on every layout update.
    /// </summary>
    public void ShowOnTrack(MascotLook look, MascotTrack track, bool rests, MascotVisit? visit, MascotMood mood,
                            MascotReactionEvent? reaction, bool reduceMotion, bool awake = true)
    {
        ViewSize = new SizeD(track.Width, track.Height);
        // Props and leans go to the side away from the camera: the right of a mirrored track, else the left.
        Mascot.Outward = track.Mirrored ? 1 : -1;
        Configure(look, track.Size, awake ? mood : MascotMood.Sleepy, rests && awake, reduceMotion, animated: true);
        Place(new PointD(track.StandX(rests), track.Baseline), track.VisibleRects);
        FollowsPointer = rests && awake;
        // A countdown is watched from the camera's left whatever the side.
        var stand = visit?.Kind.WatchesTimer == true ? track.LeftSide.Rest : track.Rest;
        PlayVisit(visit, () => MascotMotion.Path(visit?.Kind ?? MascotVisitKind.Pass, track), track.Baseline,
                  new PointD(stand, track.Baseline), track.Hop(0.22));
        if (rests) React(reaction, track.Hop(0.22));
    }

    // MARK: Configuration and placement

    public void Configure(MascotLook look, double size, MascotMood mood, bool idles, bool reduceMotion, bool animated)
    {
        Mascot.ReduceMotion = reduceMotion;
        Mascot.Configure(look, size);
        if (mood != _requestedMood)
        {
            _requestedMood = mood;
            Mascot.SetMood(mood, animated && _attached);
        }
        _idlesWhenVisible = idles;
        SyncIdling();
        ApplyPlacement();
    }

    /// <summary>Where it stands, its centre in the view or null for the middle, and where it can be seen: null
    /// everywhere, or only inside <paramref name="visible"/>.</summary>
    public void Place(PointD? center, IReadOnlyList<RectD>? visible)
    {
        _center = center;
        _visible = visible;
        ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        var placed = _center ?? new PointD(ViewSize.Width / 2, ViewSize.Height / 2);
        Mascot.Position = new PointD(placed.X, placed.Y - FigureShift);
    }

    private void SyncIdling() => Mascot.SetIdles(_idlesWhenVisible, _attached && _onScreen);

    // MARK: The pointer

    /// <summary>The pointer came over the island: it wakes, with a stretch from a doze, and looks at it.</summary>
    public void PointerEntered(PointD point)
    {
        if (!_followsPointer) return;
        if (Mascot.IsDozing && !Mascot.IsVisiting) Mascot.React(MascotReaction.WakeUp, 0);
        else Mascot.Wake(animated: true);
        Follow(point);
    }

    /// <summary>Moves come only while the pointer moves over it, each one short look from where the eyes are.</summary>
    public void PointerMoved(PointD point)
    {
        if (!_followsPointer) return;
        Follow(point);
        NotePetting(point);
    }

    public void PointerExited()
    {
        if (!_followsPointer) return;
        _pettingDue = null;
        // The cursor goes on being followed off the island when the island reads it everywhere.
        if (!GlobalGaze) Mascot.LookAhead(0.25);
    }

    /// <summary>In the island's drop hint, its eyes follow a file dragged anywhere on screen.</summary>
    public void DragMoved(PointD point) =>
        Mascot.Attend(MascotSupport.PointerGaze(Mascot.Position, point, Mascot.Size));

    private void Follow(PointD point)
    {
        if (Mascot.IsVisiting) return;
        Mascot.Follow(MascotSupport.PointerGaze(Mascot.Position, point, Mascot.Size));
    }

    /// <summary>The island reads the cursor anywhere on the screen and tells it through <see cref="GazeAt"/>.</summary>
    public bool GlobalGaze { get; set; }

    /// <summary>The cursor, at <paramref name="point"/> in this view's points, wherever it is on the screen. A
    /// resting, awake companion looks at it; a sleeping one, or one on a stroll, does not.</summary>
    public void GazeAt(PointD point)
    {
        if (!_followsPointer || Mascot.IsDozing) return;
        Follow(point);
    }

    /// <summary>The cursor kept still: its eyes look ahead again.</summary>
    public void GazeLost()
    {
        if (!_followsPointer) return;
        Mascot.LookAhead(0.25);
    }

    /// <summary>A second of the pointer resting on it brings out the hearts, at most once in a while.</summary>
    private void NotePetting(PointD point)
    {
        if (!MascotSupport.IsPetting(Mascot.Position, point, Mascot.Size) || Mascot.IsVisiting)
        {
            _pettingDue = null;
            return;
        }
        var now = _clock();
        if (_pettingDue is not null || now - _lastPetting <= MascotSupport.PettingInterval) return;
        _pettingDue = now + MascotSupport.PettingDelay;
    }

    /// <summary>Whether a pat on the head is under way, the pointer resting on it.</summary>
    public bool IsBeingPetted => _pettingDue is not null;

    // MARK: Reactions, cues and visits

    /// <summary>Plays a reaction the island published, once, and only while it is fresh: a strip drawn after its
    /// moment, as the island closes, keeps still.</summary>
    public void React(MascotReactionEvent? reaction, double lift)
    {
        if (reaction is null || reaction.Id == _playedReaction) return;
        _playedReaction = reaction.Id;
        if (!_attached) { _waitingReaction = (reaction, lift); return; }
        PlayReaction(reaction, lift);
    }

    private void PlayReaction(MascotReactionEvent reaction, double lift)
    {
        if (_clock() - reaction.Start >= MascotReactionGate.Freshness || Mascot.IsVisiting) return;
        Mascot.React(reaction.Reaction, lift);
    }

    public void Play(MascotCue cue, int id)
    {
        if (id == _lastCueId) return;
        _lastCueId = id;
        if (!_attached) return;
        Mascot.Play(cue);
    }

    /// <summary>
    /// Plays <paramref name="visit"/> once. A cameo's reaction plays as it lands, <paramref name="lift"/> being how
    /// high a hop may take it there. Null ends a visit early where it stands in its place, as when what it reacted
    /// over went away, and lets go of one that ended out of sight.
    /// </summary>
    public void PlayVisit(MascotVisit? visit, Func<MascotVisitPath> path, double baseline, PointD stand, double lift)
    {
        var shift = FigureShift;
        baseline -= shift;
        stand = new PointD(stand.X, stand.Y - shift);
        if (visit is null)
        {
            if (Mascot.IsVisiting && Mascot.Stands(stand)) Mascot.EndVisit();
            else if (!Mascot.IsVisiting) Mascot.ClearFinishedVisit();
            return;
        }
        if (visit.Id == _playedVisit) return;
        _playedVisit = visit.Id;
        _cameoDue = null;
        Mascot.Wake(animated: false);
        Mascot.PlayVisit(path(), visit.Greeting, baseline, stand, visit.Start, visit.Kind.EndsOutOfSight);
        if (visit.Kind.Reaction is not { } reaction) return;
        // A strip drawn again after it landed picks the visit up there, and does not play the reaction late.
        // Lingering, it is already there.
        var arrival = visit.Kind.Type == MascotVisitType.Linger ? 0 : MascotMotion.CameoArrival;
        var wait = visit.Start + arrival - _clock();
        if (wait <= -0.2) return;
        _cameoDue = (_clock() + Math.Max(0, wait), reaction, lift);
    }

    /// <summary>Grows or shrinks it about the middle of the view, as from one size of it to another.</summary>
    public void Scale(double from, double to, double duration, MascotTiming? timing = null)
    {
        var now = _clock();
        _scale.RemoveAll();
        _scale.Model = to;
        if (duration > 0 && from != to && !Mascot.ReduceMotion)
        {
            var curve = timing ?? MascotTiming.Grow;
            _scale.Add("scale", new MascotAnimation<double>
            {
                Begin = now, Duration = duration, Apply = (_, share) => from + (to - from) * curve.Ease(share),
            });
        }
    }

    /// <summary>
    /// Resting in the closed island, it fades out over the first half of the crossfade an arriving activity brings,
    /// and the strip comes in over the second. A view still on screen after the crossfade shows it again.
    /// </summary>
    public void YieldToActivity()
    {
        if (!_attached) return;
        var now = _clock();
        _yield.Add("yield", new MascotAnimation<double>
        {
            Begin = now, Duration = MascotMotion.RestCrossfade / 2, FillsForwards = true, RemovedOnCompletion = false,
            Apply = (_, share) => 1 - MascotTiming.EaseIn.Ease(share),
        });
        _yieldEnds = now + MascotMotion.RestCrossfade * 2;
    }

    // MARK: Frames

    /// <summary>Lets time pass: a pat on the head comes due, a cameo lands. <see cref="Frame"/> calls it.</summary>
    public void Advance()
    {
        var now = _clock();
        Mascot.Advance();
        if (_pettingDue is { } due && now >= due)
        {
            _pettingDue = null;
            _lastPetting = now;
            Mascot.React(MascotReaction.Love, 0);
        }
        if (_cameoDue is { } cameo && now >= cameo.Due)
        {
            _cameoDue = null;
            Mascot.React(cameo.Reaction, cameo.Lift);
        }
        if (_yieldEnds is { } ends && now >= ends)
        {
            _yieldEnds = null;
            _yield.Remove("yield");
        }
    }

    /// <summary>The stage's clock now.</summary>
    public double Now => _clock();

    /// <summary>
    /// When the frame next changes on its own: now while anything moves, when a blink, a pat on the head, a cameo
    /// landing or the end of a yield is due, or null while it keeps still. Windows-specific: the view draws only
    /// until then, and is woken by whatever its owner tells the stage.
    /// </summary>
    public double? NextChange()
    {
        var now = _clock();
        var next = Mascot.NextChange();
        next = MascotTime.Earliest(next, _scale.NextChange(now));
        next = MascotTime.Earliest(next, _yield.NextChange(now));
        next = MascotTime.Earliest(next, _pettingDue);
        next = MascotTime.Earliest(next, _cameoDue?.Due);
        return MascotTime.Earliest(next, _yieldEnds);
    }

    /// <summary>The companion now, in the view: its frame with the stage's scale and where it can be seen.</summary>
    public MascotFrame? Frame()
    {
        Advance();
        var now = _clock();
        _scale.Prune(now);
        if (Mascot.Frame() is not { } frame) return null;
        var scale = _scale.Value(now);
        var center = new PointD(ViewSize.Width / 2, ViewSize.Height / 2);
        return frame with
        {
            Opacity = frame.Opacity * _yield.Value(now),
            StageTransform = MascotMatrix.About(center, MascotMatrix.Scale(scale, scale)),
            VisibleRects = _visible,
        };
    }
}
