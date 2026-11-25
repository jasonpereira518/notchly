// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchService.swift's hiding when idle (syncMascotHiding,
// noteMascotStirred, scheduleMascotTuck, tuckMascotIfQuiet, stageMascotTuck, untuckMascot), with what it reads of
// the island behind IMascotHidingHost and its work items on a MascotScheduler, so it runs on a controlled clock.
namespace Notchly.Core.Notch.Mascot;

/// <summary>A piece of work planned for later, as a DispatchWorkItem.</summary>
public sealed class MascotWork
{
    internal MascotWork(double due, Action action, long order)
    {
        Due = due;
        Action = action;
        Order = order;
    }

    public double Due { get; }
    internal Action Action { get; }
    internal long Order { get; }
    public bool IsCancelled { get; private set; }

    public void Cancel() => IsCancelled = true;
}

/// <summary>
/// Work planned on the companion's clock, run in the order it falls due, as DispatchQueue.main.asyncAfter. The app
/// runs it from a timer at <see cref="NextDue"/>; tests step a manual clock through it.
/// </summary>
public sealed class MascotScheduler
{
    private readonly Func<double> _clock;
    private readonly List<MascotWork> _work = [];
    private long _order;

    public MascotScheduler(Func<double> clock) => _clock = clock;

    public double Now => _clock();

    public MascotWork After(double delay, Action action)
    {
        var work = new MascotWork(_clock() + Math.Max(0, delay), action, _order++);
        _work.Add(work);
        return work;
    }

    /// <summary>When the next piece of work falls due, or null with none planned.</summary>
    public double? NextDue
    {
        get
        {
            _work.RemoveAll(w => w.IsCancelled);
            return _work.Count == 0 ? null : _work.Min(w => w.Due);
        }
    }

    /// <summary>Runs the earliest piece of work due by <paramref name="time"/>; false when there is none.</summary>
    public bool RunNext(double time)
    {
        _work.RemoveAll(w => w.IsCancelled);
        MascotWork? next = null;
        foreach (var work in _work)
            if (work.Due <= time + 1e-9 && (next is null || work.Due < next.Due || (work.Due == next.Due && work.Order < next.Order)))
                next = work;
        if (next is null) return false;
        _work.Remove(next);
        next.Action();
        return true;
    }

    /// <summary>Runs everything due now, including work it plans for now.</summary>
    public void RunDue()
    {
        while (RunNext(_clock())) { }
    }
}

/// <summary>What hiding when idle reads of the island, and asks it to do.</summary>
public interface IMascotHidingHost
{
    /// <summary>The preference as Settings has it, with the companion on: <see cref="MascotSupport.HidesWhenIdle"/>.</summary>
    bool HidesWhenIdle { get; }
    /// <summary>The island is up and running.</summary>
    bool Running { get; }
    /// <summary>The companion is switched on.</summary>
    bool MascotOn { get; }
    bool Expanded { get; }
    /// <summary>The closed island would show the companion at rest were it not hidden: on, with nothing else to show,
    /// and not out in the Command Bar.</summary>
    bool ShowsRest { get; }
    /// <summary>The closed island is on screen with room for a visit.</summary>
    bool CanHostVisit { get; }
    /// <summary>The pointer is over the island.</summary>
    bool PointerInside { get; }
    /// <summary>The visit under way, if any.</summary>
    MascotVisit? Visit { get; }
    /// <summary>A reaction waiting to be shown, until this time; null with none waiting.</summary>
    double? PendingReactionDeadline { get; }
    /// <summary>It stands in the window's own layer, stepping into a notice or back out.</summary>
    bool Bridging { get; }
    /// <summary>It is out in the Command Bar. Always false until the Command Bar comes to the island.</summary>
    bool InBar { get; }
    bool ReduceMotion { get; }
    /// <summary>The island is a capsule.</summary>
    bool Floats { get; }

    /// <summary>Plays the visit that hides it: the island steps aside under its wing until
    /// <paramref name="handBack"/> seconds in, and the visit ends after its duration, folding the wings.</summary>
    void StartHidingVisit(MascotVisit visit, double? handBack);
    /// <summary>What the closed island holds changed: draw it again.</summary>
    void RefreshPresentation();
    /// <summary>Out from behind the camera to rest (arriving), or a farewell.</summary>
    void StageEntrance(bool arriving);
}

/// <summary>
/// Hiding in the island when idle. At launch it starts hidden, and switched on it comes out to say hello and hides
/// a quiet while later. Turned on, it goes in at once; turned off, it comes back out to rest. One check is planned at
/// a time, never a tick.
/// </summary>
public sealed class MascotHiding
{
    private readonly IMascotHidingHost _host;
    private readonly MascotScheduler _scheduler;

    public MascotHiding(IMascotHidingHost host, MascotScheduler scheduler)
    {
        _host = host;
        _scheduler = scheduler;
    }

    /// <summary>Whether it hid in the island when idle at the last preference sync; null before the first.</summary>
    public bool? HidesAtSync { get; private set; }
    /// <summary>Hidden in the island: the closed island keeps its own size without it, and it comes out only to visit
    /// or to react.</summary>
    public bool Tucked { get; set; }
    /// <summary>When it last did something, or came to rest.</summary>
    public double Stirred { get; private set; } = double.NegativeInfinity;
    /// <summary>The check planned, while a quiet while counts.</summary>
    public MascotWork? TuckWork { get; private set; }
    /// <summary>The companion at rest shows in the closed island just now.</summary>
    public bool RestsInView => _host.ShowsRest && !Tucked;

    /// <summary>
    /// Brings hiding in line with Settings at a preference sync. <paramref name="switchedOn"/>: the companion was just
    /// switched on, and comes out to say hello. True when that changes what the closed island holds.
    /// </summary>
    public bool Sync(bool switchedOn)
    {
        var hides = _host.HidesWhenIdle;
        var previous = HidesAtSync;
        HidesAtSync = hides;
        var toggled = false;
        if (previous is null) Tucked = hides;
        else if (switchedOn) Tucked = false;
        else if (previous != hides && _host.MascotOn)
        {
            toggled = true;
            if (!hides) Untuck();
            else if (!StageTuck()) Schedule(MascotSupport.HideRetry, now: true);
        }
        if (!hides) CancelCheck();
        // Out of the island, a quiet while is always counting.
        else if (!Tucked && TuckWork is null) Schedule(MascotSupport.HideDelay);
        return toggled;
    }

    /// <summary>Something happened where it is, or it came to rest: a visit or a reaction began or ended. Hiding when
    /// idle, it waits a whole quiet while from now before it goes into the island.</summary>
    public void NoteStirred() => Stirred = _scheduler.Now;

    /// <summary>The island went away: the quiet while counts again once it is back.</summary>
    public void CancelCheck()
    {
        TuckWork?.Cancel();
        TuckWork = null;
    }

    /// <summary><paramref name="now"/> hides it as soon as it is free, without waiting for quiet.</summary>
    private void Schedule(double delay, bool now = false)
    {
        TuckWork?.Cancel();
        TuckWork = _scheduler.After(delay, () => TuckIfQuiet(now));
    }

    /// <summary>A quiet while after it last did anything, it goes into the island. Anything it does meanwhile starts
    /// the while over.</summary>
    private void TuckIfQuiet(bool now)
    {
        TuckWork = null;
        if (!_host.Running || HidesAtSync != true || Tucked) return;
        var quiet = _scheduler.Now - Stirred;
        if (!now && quiet < MascotSupport.HideDelay - 0.05)
        {
            Schedule(MascotSupport.HideDelay - quiet);
            return;
        }
        // The pointer on it looks at it or pets it, and it stays meanwhile.
        if (!now && RestsInView && _host.PointerInside)
        {
            Schedule(MascotSupport.HideRetry);
            return;
        }
        if (!StageTuck())
        {
            Schedule(MascotSupport.HideRetry, now);
            return;
        }
        _host.RefreshPresentation();
    }

    /// <summary>
    /// Hides it in the island. Where the closed island shows it at rest, it yawns and hops in behind the camera, or
    /// walks out at a capsule's far end, and the wings fold once it is gone. Anywhere else it is simply in there when
    /// the island rests again. False while it is busy with a visit, a reaction, the Command Bar or the island
    /// opening around it.
    /// </summary>
    private bool StageTuck()
    {
        // A reaction still waiting to be seen comes first. One past its time never will be.
        var reacting = _host.PendingReactionDeadline is { } deadline && _scheduler.Now <= deadline;
        if (_host.Visit is not null || reacting || _host.Bridging || _host.InBar) return false;
        var shown = RestsInView && _host.CanHostVisit;
        Tucked = true;
        CancelCheck();
        if (!shown || _host.ReduceMotion) return true;
        var tuck = MascotVisit.Now(MascotSupport.HideAway, MascotMood.Idle, _scheduler.Now);
        _host.StartHidingVisit(tuck, MascotMotion.HandBack(tuck.Kind, _host.Floats));
        return true;
    }

    /// <summary>Hiding turned off: it comes back out to rest, as when switched on.</summary>
    private void Untuck()
    {
        if (!Tucked) return;
        Tucked = false;
        if (!_host.Expanded) _host.StageEntrance(arriving: true);
    }
}
