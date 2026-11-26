// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchService.swift's companion (its "Companion" extension):
// visits on a schedule, entrances and farewells, the side changing, hiding when idle, countdowns watched and
// reactions handed out through the gate. Windows-specific: Reduce Motion follows Windows' animation effects, Low
// Power follows Battery Saver, and every work item runs on a MascotScheduler pumped by one dispatcher timer.
using System.Windows;
using System.Windows.Threading;
using Notchly.Core.Calendar;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;
using Notchly.Core.Settings;
using Windows.System.Power;

namespace Notchly.App.Island.Mascot;

/// <summary>What the companion reads of the island, and asks of it.</summary>
internal interface IMascotIsland
{
    bool Running { get; }
    bool Expanded { get; }
    bool PointerInside { get; }
    /// <summary>The closed island is on screen and free for the companion: not hidden in full screen or until hover,
    /// with no notice, drop target or open island over it.</summary>
    bool ClosedFree { get; }
    /// <summary>The closed island shows at all: not hidden in full screen or until hover.</summary>
    bool ClosedVisible { get; }
    /// <summary>The reaction the notice on screen plays with the companion in it, if any.</summary>
    MascotReaction? NoticeReaction { get; }
    NotchCompactActivity? Activity { get; }
    /// <summary>The closed strip the activity on show takes, or null with none.</summary>
    SizeD? ActivityStrip { get; }
    NotchGeometry? Geometry { get; }
    bool MusicPlaying { get; }
    /// <summary>Draws what the closed island holds again.</summary>
    void Refresh();
}

internal sealed class MascotService : IMascotHidingHost, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly IMascotIsland _island;
    private readonly Func<double> _clock;
    private readonly MascotScheduler _scheduler;
    private readonly MascotHiding _hiding;
    private readonly MascotReactionGate _gate = new();
    private readonly DispatcherTimer _pump;

    private MascotVisit? _visit;
    private MascotWork? _stepBackWork, _visitWork, _nextVisitWork, _flushWork;
    private bool _visitsWereOn;
    private MascotVisitFrequency? _frequencyAtSync;
    private bool? _wasEnabled;
    private MascotSide? _sideAtSync;
    private MascotReactionEvent? _reaction;
    private (MascotReaction Reaction, double Deadline, double NotBefore)? _pending;
    private double _lastGroove = double.NegativeInfinity, _lastAgentStart = double.NegativeInfinity;
    private bool? _sawAgents;
    private CalendarCountdown? _sawCountdown;
    private bool? _sawMusic;
    private string? _sawTrack;
    private bool _restedInView;
    private double _leftRest = double.NegativeInfinity, _backAtRest = double.NegativeInfinity;
    private bool _flushing;
    private bool _disposed;

    public MascotService(SettingsStore settings, IMascotIsland island, Func<double> clock, Dispatcher dispatcher)
    {
        _settings = settings;
        _island = island;
        _clock = clock;
        _scheduler = new MascotScheduler(clock);
        _hiding = new MascotHiding(this, _scheduler);
        _pump = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _pump.Tick += (_, _) => { _pump.Stop(); _scheduler.RunDue(); Pump(); };
        try
        {
            PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
            LowPower = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (Exception) { LowPower = false; }
    }

    private double Now => _clock();

    /// <summary>Raised when the island should draw the companion again: Battery Saver changed.</summary>
    public event Action? Changed;

    // MARK: State the island draws

    /// <summary>The visit under way, if any.</summary>
    public MascotVisit? Visit
    {
        get => _visit;
        private set { _visit = value; _hiding.NoteStirred(); }
    }

    /// <summary>The reaction it plays where it rests, published once.</summary>
    public MascotReactionEvent? ReactionEvent
    {
        get => _reaction;
        private set { _reaction = value; _hiding.NoteStirred(); }
    }

    /// <summary>What a visit walks over steps aside meanwhile.</summary>
    public bool StepsAside { get; private set; }

    /// <summary>Windows' animation effects are off: it changes face but does not move.</summary>
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    /// <summary>Battery Saver is on: it blinks half as often and visits wait.</summary>
    public bool LowPower { get; private set; }

    /// <summary>Switched on at the last preference sync.</summary>
    public bool MascotOn => _wasEnabled ?? false;

    public MascotSide Side => _sideAtSync ?? MascotSupport.Side(_settings);

    /// <summary>On, and not hidden in the island.</summary>
    public bool AtRest => MascotOn && !_hiding.Tucked;

    /// <summary>It rests in the closed island now, rather than only visiting it.</summary>
    public bool RestsInView => AtRest && _island.Activity is null;

    /// <summary>It was drawn at rest in the closed island at the last refresh.</summary>
    public bool RestedInView
    {
        get => _restedInView;
        private set
        {
            var was = _restedInView;
            _restedInView = value;
            if (was && !value) _leftRest = Now;
            if (!was && value)
            {
                _backAtRest = Now;
                _hiding.NoteStirred();
                ReturnedToRest();
                if (_pending is not null) Flush();
            }
        }
    }

    public bool JustRested => _restedInView || Now - _leftRest < 0.35;

    public bool Lingers => _visit?.Kind.Type == MascotVisitType.Linger;

    /// <summary>The closed island at rest draws the companion, resting or visiting, and opens its wings for it.</summary>
    public bool Shows(NotchGeometry g) => (RestsInView || _visit is not null) && (g.Floats || g.RestingWingWidth > 0);

    /// <summary>Where it rests in the closed island, or strolls in a capsule.</summary>
    public MascotTrack ClosedTrack(NotchGeometry g) => g.Floats
        ? MascotSupport.Track(g.Collapsed.Width, g.StripHeight, 0, 0, true, g.StripBodyHeight)
        : MascotSupport.Track(g.Collapsed.Width, g.StripHeight, g.RestingWingWidth, g.CameraWidth, false, g.StripBodyHeight, Side);

    /// <summary>Where it comes out over an activity's strip of <paramref name="strip"/>, or null with no room.</summary>
    public MascotTrack? TrackOverActivity(NotchGeometry g, SizeD strip)
    {
        // Switched off, a visit under way still plays out over the strip.
        if (!MascotOn && _visit is null) return null;
        return MascotSupport.TrackOverActivity(g.Floats, strip.Width, g.StripHeight, g.StripBodyHeight,
                                               Math.Max(0, (strip.Width - g.CameraWidth) / 2), g.CameraWidth, false, Side);
    }

    /// <summary>A visit needs the closed island on screen with room for the companion, whatever it shows.</summary>
    private bool CanHostVisit()
    {
        if (!_island.Running || _island.Expanded || !_island.ClosedFree || _island.Geometry is not { } g) return false;
        if (_island.ActivityStrip is not { } strip || _island.Activity is null) return g.Floats || g.RestingWingWidth > 0;
        return TrackOverActivity(g, strip) is not null;
    }

    private bool Floats => _island.Geometry?.Floats == true;

    // MARK: Preferences

    /// <summary>Brings the companion in line with Settings: switched on it arrives, off it says goodbye.</summary>
    public void Sync()
    {
        var enabled = MascotSupport.IsEnabled(_settings);
        var toggled = _wasEnabled is { } was && was != enabled;
        _wasEnabled = enabled;
        _hiding.Sync(switchedOn: toggled && enabled);
        if (toggled && !_island.Expanded) StageEntrance(arriving: enabled);
        SyncVisits();
        SyncSide();
        Pump();
    }

    /// <summary>The island stopped: everything under way ends.</summary>
    public void Stop()
    {
        foreach (var work in new[] { _nextVisitWork, _visitWork, _stepBackWork, _flushWork }) work?.Cancel();
        _nextVisitWork = _visitWork = _stepBackWork = _flushWork = null;
        _visit = null;
        _pending = null;
        StepsAside = false;
        _hiding.CancelCheck();
        _restedInView = false;
        _visitsWereOn = false;
        _pump.Stop();
    }

    private void SyncVisits()
    {
        var visits = _island.Running && MascotSupport.Visits(_settings);
        var frequency = MascotSupport.VisitFrequency(_settings);
        try
        {
            if (!visits)
            {
                _nextVisitWork?.Cancel();
                _nextVisitWork = null;
                // Visits turned off only stop coming; a stopped island ends everything.
                if (_visit is { } visit && visit.Kind.Type != MascotVisitType.Farewell && !_island.Running) EndVisit();
                return;
            }
            // Turned on just now, it says hello almost at once. Coming out as it is switched on is that hello already.
            if (!_visitsWereOn)
            {
                if (_visit?.Kind.Type != MascotVisitType.Arrive) ScheduleVisit(MascotSupport.WelcomeDelay, MascotMood.Wink);
            }
            else if (_visit is null && (_nextVisitWork is null || _frequencyAtSync != frequency))
                ScheduleVisit(NextVisitDelay(frequency));
        }
        finally
        {
            _visitsWereOn = visits;
            _frequencyAtSync = frequency;
        }
    }

    private double NextVisitDelay(MascotVisitFrequency? frequency = null) =>
        MascotSupport.NextVisitDelay(frequency ?? MascotSupport.VisitFrequency(_settings), Random.Shared.NextDouble());

    private void ScheduleVisit(double delay, MascotMood? greeting = null)
    {
        _nextVisitWork?.Cancel();
        _nextVisitWork = _scheduler.After(delay, () => BeginVisit(greeting));
    }

    /// <summary><paramref name="asked"/> is a hello Settings asked for, which comes whatever the visits.</summary>
    private void BeginVisit(MascotMood? greeting, bool asked = false)
    {
        _nextVisitWork = null;
        if (!asked && !MascotSupport.Visits(_settings)) return;
        // Busy, hidden or out of room: it tries again on its next visit. Battery Saver keeps it at rest.
        if (!CanHostVisit() || _visit is not null || (!asked && LowPower))
        {
            ScheduleVisit(NextVisitDelay());
            return;
        }
        // Without motion a stroll over an activity hides its strip for seconds: it waits for the island to rest,
        // and a hello asked for plays in its own wing.
        var coversNothing = RestsInView || (_hiding.Tucked && _island.Activity is null);
        if (ReduceMotion && !coversNothing)
        {
            if (asked) BeginCameo(MascotReaction.Celebrate);
            else ScheduleVisit(NextVisitDelay());
            return;
        }
        var visit = MascotVisit.Now(RestsInView ? MascotVisitKind.Lap : MascotVisitKind.Pass,
                                    greeting ?? MascotSupport.Greeting(Random.Shared.NextDouble()), Now);
        SetVisit(visit);
        _visitWork = _scheduler.After(visit.Duration, EndVisit);
    }

    private void EndVisit()
    {
        _visitWork?.Cancel();
        _visitWork = null;
        if (_visit is not { } ended) return;
        var now = Now;
        // Gone behind the camera from a reaction over an activity that left meanwhile, it comes back out to its place.
        if (ended.Kind.Reaction is not null && RestsInView && now >= ended.Start + ended.Duration - 0.05 && !ReduceMotion)
        {
            var back = new MascotVisit(Guid.NewGuid(), MascotVisitKind.Arrive, MascotMood.Idle,
                                       Math.Max(ended.Start + ended.Duration, now - 0.1));
            SetVisit(back);
            _visitWork = _scheduler.After(back.Duration, EndVisit);
            return;
        }
        // Switched off while it strolled to its place: it says goodbye from there rather than vanish.
        if (_island.Running && !MascotSupport.IsEnabled(_settings) && !ended.Kind.EndsOutOfSight
            && ended.Kind.Type != MascotVisitType.Farewell && !ReduceMotion && _island.Activity is null && CanHostVisit())
        {
            var farewell = MascotVisit.Now(MascotVisitKind.Farewell, MascotMood.Happy, now);
            SetVisit(farewell);
            _visitWork = _scheduler.After(farewell.Duration, EndVisit);
            return;
        }
        SetVisit(null);
        // A side chosen while it was out takes effect now.
        if (_sideAtSync != MascotSupport.Side(_settings))
        {
            SyncSide();
            if (_visit is not null) return;
        }
        if (_island.Running && MascotSupport.Visits(_settings)) ScheduleVisit(NextVisitDelay());
    }

    /// <summary>Back at rest in view while it reacted over an activity that has gone: it stays once its reaction is
    /// over instead of going behind the camera.</summary>
    private void ReturnedToRest()
    {
        if (_visit is not { } visit || visit.Kind.Reaction is not { } reaction) return;
        var landed = visit.Start + (visit.Kind.Type == MascotVisitType.Linger ? 0 : MascotMotion.CameoArrival);
        var now = Now;
        if (now >= landed + MascotMotion.CameoHold(reaction) - 0.05) return;
        _visitWork?.Cancel();
        _visitWork = _scheduler.After(Math.Max(0, landed + 0.05 - now), () =>
        {
            if (_visit?.Id != visit.Id) return;
            if (RestsInView) { EndVisit(); return; }
            // Something took its place again: it leaves as it came for.
            _visitWork = _scheduler.After(Math.Max(0, visit.Start + visit.Duration - Now), EndVisit);
        });
    }

    /// <summary>Turned on while the closed island has nothing else to show, it hops out from behind the camera;
    /// turned off there, a glad hop and away behind it. Hidden in the island, it is gone already.</summary>
    public void StageEntrance(bool arriving)
    {
        if (ReduceMotion || _island.Activity is not null || !(arriving || !_hiding.Tucked) || !CanHostVisit()) return;
        if (arriving ? _visit is not null && _visit.Kind.Type != MascotVisitType.Farewell : _visit is not null) return;
        var visit = MascotVisit.Now(arriving ? MascotVisitKind.Arrive : MascotVisitKind.Farewell,
                                    arriving ? MascotMood.Wink : MascotMood.Happy, Now);
        _visitWork?.Cancel();
        _stepBackWork?.Cancel();
        _stepBackWork = null;
        Visit = visit;
        StepsAside = false;
        _visitWork = _scheduler.After(visit.Duration, EndVisit);
        _island.Refresh();
        Pump();
    }

    /// <summary>Settings asks it to say hello now. False when the island has no room for it at the moment.</summary>
    public bool Greet()
    {
        if (!MascotSupport.IsEnabled(_settings) || _visit is not null || !CanHostVisit()) return false;
        _nextVisitWork?.Cancel();
        _nextVisitWork = null;
        BeginVisit(MascotMood.Wink, asked: true);
        Pump();
        return _visit is not null;
    }

    /// <summary>Moved to the camera's other side: behind the camera, across, and out on the new side.</summary>
    private void SyncSide()
    {
        var side = MascotSupport.Side(_settings);
        var previous = _sideAtSync;
        // A visit under way finishes on the side it set out on.
        if (previous is not null && _visit is not null) return;
        _sideAtSync = side;
        if (previous is not { } before || before == side || Floats || ReduceMotion || !RestsInView || !CanHostVisit()) return;
        var cross = MascotVisit.Now(MascotVisitKind.Cross, MascotMood.Wink, Now);
        Visit = cross;
        _visitWork?.Cancel();
        _visitWork = _scheduler.After(cross.Duration + 0.1, EndVisit);
        _island.Refresh();
    }

    // MARK: The island refreshing

    /// <summary>Before the island draws: an AI agent getting to work where the companion rests hands the island over
    /// with a ready face, at most once in a while. Agents at work when the app opens are no news.</summary>
    public void BeforeRefresh(bool agentsLoaded, bool agentsWorking)
    {
        if (!agentsLoaded) return;
        var saw = _sawAgents;
        _sawAgents = agentsWorking;
        if (saw != false || !agentsWorking || !_restedInView || !MascotSupport.Reacts(_settings)) return;
        var now = Now;
        if (now - _lastAgentStart <= MascotSupport.AgentStartInterval) return;
        _lastAgentStart = now;
        React(MascotSupport.AgentStarted);
    }

    /// <summary>After the island drew: a waiting reaction goes out if it can, and whether it rests in view is noted.</summary>
    public void AfterRefresh()
    {
        Flush();
        RestedInView = RestsInView && !_island.Expanded && _island.ClosedVisible;
        Pump();
    }

    // MARK: Reactions

    /// <summary>Something happened the companion can react to: it plays where it rests, or out from behind the
    /// camera over what the closed island shows, now or as soon as it can, and never twice in a row.</summary>
    public void React(MascotReactionRequest request)
    {
        if (!MascotSupport.Reacts(_settings)) return;
        var now = Now;
        // A notice on screen that shows the companion plays it there already.
        if (_island.NoticeReaction == request.Reaction)
        {
            _gate.Admits(request.Reaction, now);
            return;
        }
        _pending = (request.Reaction, now + request.Delay + request.Patience, now + request.Delay);
        Flush();
        Pump();
    }

    public void React(MascotReaction reaction) => React(new MascotReactionRequest(reaction));

    /// <summary>Music started: once the song's strip has settled, it bobs along if the music still plays.</summary>
    public void OnMusic(bool playing, string? track = null)
    {
        var saw = _sawMusic;
        var sawTrack = _sawTrack;
        _sawMusic = playing;
        _sawTrack = track;
        // The music stopping, or another song following one that played, is noticed too (a Windows addition).
        if (MascotSupport.MusicChange(saw, playing, sawTrack, track) is { } change) React(change);
        if (saw != false || !playing) return;
        if (!MascotSupport.Reacts(_settings) || Now - _lastGroove <= MascotSupport.GrooveInterval) return;
        _scheduler.After(MascotSupport.GrooveDelay, () =>
        {
            if (_island.MusicPlaying) React(MascotSupport.MusicStarted with { Delay = 0 });
        });
        Pump();
    }

    /// <summary>The event the island counted down to begins: it bounces.</summary>
    public void OnCalendar(CalendarCountdown? countdown)
    {
        var before = _sawCountdown;
        _sawCountdown = countdown;
        if (MascotSupport.EventBegan(before, countdown, DateTime.Now)) React(MascotSupport.EventStarts);
    }

    private void Flush()
    {
        if (_flushing) return;
        _flushing = true;
        try { FlushNow(); }
        finally { _flushing = false; }
    }

    /// <summary>Hands a waiting reaction to the companion once the closed island shows with room for it and no
    /// visit is under way. A countdown it watches ends once the timer's strip is gone.</summary>
    private void FlushNow()
    {
        if (_visit?.Kind.WatchesTimer == true && _island.Activity != NotchCompactActivity.Timer) EndCountdown(retreating: false);
        if (_pending is not { } pending) return;
        var now = Now;
        if (now > pending.Deadline || !MascotSupport.Reacts(_settings)) { _pending = null; return; }
        if (now < pending.NotBefore)
        {
            _flushWork?.Cancel();
            _flushWork = _scheduler.After(pending.NotBefore - now, () => { _flushWork = null; Flush(); });
            return;
        }
        if (_visit is not null || !CanHostVisit()) return;
        // Back at rest as an activity leaves, it crossfades in first: the reaction waits for that.
        if (RestsInView)
        {
            if (!_restedInView) return;
            var shows = _backAtRest + MascotMotion.RestCrossfade;
            if (now < shows)
            {
                _pending = (pending.Reaction, Math.Max(pending.Deadline, shows + 0.5), shows);
                FlushNow();
                return;
            }
        }
        _pending = null;
        if (!_gate.Admits(pending.Reaction, now)) return;
        if (pending.Reaction == MascotReaction.Groove) _lastGroove = now;
        if (RestsInView)
        {
            ReactionEvent = MascotReactionEvent.Now(pending.Reaction, now);
            _island.Refresh();
        }
        else BeginCameo(pending.Reaction, lingering: JustRested && _island.Activity is not null);
    }

    /// <summary>The last seconds of a countdown the closed island shows: it comes out over the timer's mark and
    /// watches the reading run out.</summary>
    public void WatchCountdown(double remaining)
    {
        if (!MascotSupport.Reacts(_settings) || remaining <= 1 || _visit is not null
            || _island.Activity != NotchCompactActivity.Timer || Floats || !CanHostVisit()) return;
        var watch = MascotVisit.Now(MascotVisitKind.Countdown(remaining), MascotMood.Idle, Now);
        SetVisit(watch);
        _visitWork?.Cancel();
        _visitWork = _scheduler.After(watch.Duration, EndVisit);
        Pump();
    }

    /// <summary>The countdown it watches ran out, stopped or went away. Paused, it goes back behind the camera.</summary>
    public void EndCountdown(bool retreating)
    {
        if (_visit is not { Kind.Type: MascotVisitType.Countdown }) return;
        if (!retreating || _island.Activity != NotchCompactActivity.Timer || Floats) { EndVisit(); Pump(); return; }
        var retreat = MascotVisit.Now(MascotVisitKind.Retreat, MascotMood.Idle, Now);
        SetVisit(retreat);
        _visitWork?.Cancel();
        _visitWork = _scheduler.After(retreat.Duration, EndVisit);
        Pump();
    }

    private void BeginCameo(MascotReaction reaction, bool lingering = false)
    {
        var cameo = MascotVisit.Now(lingering ? MascotVisitKind.Linger(reaction) : MascotVisitKind.Cameo(reaction),
                                    MascotMood.Idle, Now);
        SetVisit(cameo);
        _visitWork?.Cancel();
        _visitWork = _scheduler.After(cameo.Duration, EndVisit);
    }

    /// <summary>A visit starting or ending: what it walks over steps aside, and a reaction's visit hands it back on
    /// its way home.</summary>
    private void SetVisit(MascotVisit? visit)
    {
        _stepBackWork?.Cancel();
        _stepBackWork = null;
        Visit = visit;
        StepsAside = visit is not null;
        _island.Refresh();
        if (visit is null || MascotMotion.HandBack(visit.Kind, Floats) is not { } handBack) return;
        _stepBackWork = _scheduler.After(Math.Max(0, visit.Start + handBack - Now), () =>
        {
            _stepBackWork = null;
            StepsAside = false;
            _island.Refresh();
        });
    }

    // MARK: Hiding when idle (IMascotHidingHost)

    bool IMascotHidingHost.HidesWhenIdle => MascotSupport.HidesWhenIdle(_settings);
    bool IMascotHidingHost.Running => _island.Running;
    bool IMascotHidingHost.MascotOn => MascotOn;
    bool IMascotHidingHost.Expanded => _island.Expanded;
    bool IMascotHidingHost.ShowsRest => MascotOn && _island.Activity is null;
    bool IMascotHidingHost.CanHostVisit => CanHostVisit();
    bool IMascotHidingHost.PointerInside => _island.PointerInside;
    MascotVisit? IMascotHidingHost.Visit => _visit;
    double? IMascotHidingHost.PendingReactionDeadline => _pending?.Deadline;
    bool IMascotHidingHost.Bridging => false;
    bool IMascotHidingHost.InBar => false;
    bool IMascotHidingHost.ReduceMotion => ReduceMotion;
    bool IMascotHidingHost.Floats => Floats;

    void IMascotHidingHost.StartHidingVisit(MascotVisit visit, double? handBack)
    {
        _visitWork?.Cancel();
        _stepBackWork?.Cancel();
        _stepBackWork = null;
        Visit = visit;
        // An activity arriving as it yawns finds its wing covered, and has it back as it goes behind the camera.
        StepsAside = true;
        if (handBack is { } back)
            _stepBackWork = _scheduler.After(back, () => { _stepBackWork = null; StepsAside = false; _island.Refresh(); });
        _visitWork = _scheduler.After(visit.Duration, EndVisit);
        _island.Refresh();
    }

    void IMascotHidingHost.RefreshPresentation() => _island.Refresh();

    void IMascotHidingHost.StageEntrance(bool arriving) => StageEntrance(arriving);

    // MARK: Timing

    /// <summary>Runs what falls due, then waits for the next piece of work.</summary>
    public void Pump()
    {
        if (_disposed) return;
        _scheduler.RunDue();
        _pump.Stop();
        if (_scheduler.NextDue is not { } due) return;
        _pump.Interval = TimeSpan.FromSeconds(Math.Clamp(due - Now, 0.001, 3600));
        _pump.Start();
    }

    private void OnEnergySaverChanged(object? sender, object e)
    {
        bool on;
        try { on = PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
        catch (Exception) { return; }
        _pump.Dispatcher.BeginInvoke(() =>
        {
            if (on == LowPower) return;
            LowPower = on;
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        try { PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; }
        catch (Exception) { }
    }
}
