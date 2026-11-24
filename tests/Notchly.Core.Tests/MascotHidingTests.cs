// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Tests/NotchMascotHidingTests.swift: the companion hiding in the island when idle, with the
// production bodies (MascotHiding) on a controlled clock and the island as a stand-in. Nothing is drawn and no
// preference is read. The stand-in plays the hiding visit as the island will: stepping aside until the hand-back,
// and ending the visit after its duration.
using Notchly.Core.Notch.Mascot;

namespace Notchly.Core.Tests;

public class MascotHidingTests
{
    private const double Delay = MascotSupport.HideDelay, Retry = MascotSupport.HideRetry;
    private static readonly double Away = MascotMotion.Duration(MascotSupport.HideAway);

    private sealed class Island : IMascotHidingHost
    {
        private MascotVisit? _visit;
        private MascotWork? _visitWork, _stepBackWork;

        public Island()
        {
            Scheduler = new MascotScheduler(() => Now);
            Hiding = new MascotHiding(this, Scheduler);
        }

        public double Now { get; set; } = 1000;
        public MascotScheduler Scheduler { get; }
        public MascotHiding Hiding { get; }

        public bool HidesWhenIdle { get; set; }
        public bool Running { get; set; } = true;
        public bool MascotOn { get; set; } = true;
        public bool Expanded { get; set; }
        /// <summary>The closed island has nothing else to show and room for it.</summary>
        public bool RestsInPlace { get; set; } = true;
        public bool CanHost { get; set; } = true;
        public bool PointerInside { get; set; }
        public double? PendingReactionDeadline { get; set; }
        public bool Bridging { get; set; }
        public bool InBar { get; set; }
        public bool ReduceMotion { get; set; }
        public bool Floats => false;
        public bool StepsAside { get; private set; }
        public int Refreshes { get; private set; }
        public int VisitEnds { get; private set; }
        public List<bool> Entrances { get; } = [];

        public MascotVisit? Visit
        {
            get => _visit;
            set
            {
                _visit = value;
                Hiding.NoteStirred();
            }
        }

        public bool ShowsRest => MascotOn && RestsInPlace && !InBar;
        public bool CanHostVisit => CanHost && !Expanded && !InBar;

        public void StartHidingVisit(MascotVisit visit, double? handBack)
        {
            _visitWork?.Cancel();
            _stepBackWork?.Cancel();
            _stepBackWork = null;
            Visit = visit;
            StepsAside = true;
            if (handBack is { } back) _stepBackWork = Scheduler.After(back, () => { _stepBackWork = null; StepsAside = false; });
            _visitWork = Scheduler.After(visit.Duration, EndVisit);
        }

        public void EndVisit()
        {
            VisitEnds++;
            Visit = null;
        }

        public void RefreshPresentation() => Refreshes++;
        public void StageEntrance(bool arriving) => Entrances.Add(arriving);

        /// <summary>Lets time pass, running each piece of work at the moment it falls due.</summary>
        public void Advance(double seconds)
        {
            var target = Now + seconds;
            while (Scheduler.NextDue is { } due && due <= target + 1e-9)
            {
                Now = Math.Max(Now, due);
                Scheduler.RunNext(Now);
            }
            Now = target;
        }
    }

    /// <summary>Set to hide when idle, the companion just switched on: out beside the camera in a resting island,
    /// a quiet while counting from now.</summary>
    private static Island Out()
    {
        var island = new Island { HidesWhenIdle = true };
        island.Hiding.Sync(switchedOn: false);
        island.Hiding.Sync(switchedOn: true);
        island.Hiding.NoteStirred();
        return island;
    }

    [Fact]
    public void NotSetToHideItRestsFromLaunchOnAndNothingCounts()
    {
        var plain = new Island();
        Assert.False(plain.Hiding.Sync(switchedOn: false));
        Assert.False(plain.Hiding.Tucked);
        Assert.Null(plain.Hiding.TuckWork);
        plain.Advance(Delay * 3);
        Assert.False(plain.Hiding.Tucked);
        Assert.Null(plain.Visit);
        Assert.Equal(0, plain.Refreshes);
    }

    [Fact]
    public void SetToHideItStartsInTheIslandAtLaunchWithNothingToAnimate()
    {
        var launched = new Island { HidesWhenIdle = true };
        Assert.False(launched.Hiding.Sync(switchedOn: false));
        Assert.True(launched.Hiding.Tucked);
        Assert.Null(launched.Hiding.TuckWork);
        Assert.Null(launched.Visit);
    }

    [Fact]
    public void SwitchedOnItComesOutAndHidesAQuietWhileLater()
    {
        var island = Out();
        Assert.False(island.Hiding.Tucked);
        Assert.NotNull(island.Hiding.TuckWork);
        island.Advance(Delay - 1);
        Assert.False(island.Hiding.Tucked);
        island.Advance(1);
        Assert.True(island.Hiding.Tucked);
        Assert.Equal(MascotSupport.HideAway, island.Visit?.Kind);
        Assert.Equal(1, island.Refreshes);
        // An activity arriving as it yawns has its wing covered until the companion is gone.
        Assert.True(island.StepsAside);
        var handBack = MascotMotion.HandBack(MascotSupport.HideAway, floats: false) ?? Away;
        island.Advance(handBack);
        Assert.False(island.StepsAside);
        Assert.NotNull(island.Visit);
        island.Advance(Away - handBack);
        Assert.Equal(1, island.VisitEnds);
        Assert.Null(island.Visit);
        Assert.Null(island.Hiding.TuckWork);
    }

    [Fact]
    public void AnythingItDoesStartsTheWhileOver()
    {
        var island = Out();
        island.Advance(Delay * 0.8);
        island.Hiding.NoteStirred();
        island.Advance(Delay * 0.8);
        Assert.False(island.Hiding.Tucked);
        Assert.NotNull(island.Hiding.TuckWork);
        island.Advance(Delay * 0.2);
        Assert.True(island.Hiding.Tucked);
    }

    [Fact]
    public void UnderThePointerItWaitsForThePointerToGo()
    {
        var island = Out();
        island.PointerInside = true;
        island.Advance(Delay + Retry * 2);
        Assert.False(island.Hiding.Tucked);
        Assert.NotNull(island.Hiding.TuckWork);
        island.PointerInside = false;
        island.Advance(Retry);
        Assert.True(island.Hiding.Tucked);
        Assert.Equal(MascotSupport.HideAway, island.Visit?.Kind);
    }

    [Fact]
    public void OnAStrollItStaysOutAndTheStrollsEndStartsAWholeQuietWhile()
    {
        var island = Out();
        island.Visit = MascotVisit.Now(MascotVisitKind.Lap, MascotMood.Wink, island.Now);
        island.Advance(Delay + Retry);
        var strolled = !island.Hiding.Tucked;
        island.Visit = null;
        island.Advance(Retry);
        var rested = !island.Hiding.Tucked;
        island.Advance(Delay);
        Assert.True(strolled && rested && island.Hiding.Tucked);
    }

    public static TheoryData<string> Busy => new() { "reaction", "bridging", "bar" };

    [Theory, MemberData(nameof(Busy))]
    public void BusyItHidesOnlyOnceThatIsOver(string busy)
    {
        var island = Out();
        void Set(bool on)
        {
            switch (busy)
            {
                case "reaction": island.PendingReactionDeadline = on ? island.Now + 60 : null; break;
                case "bridging": island.Bridging = on; break;
                default: island.InBar = on; break;
            }
        }
        Set(true);
        island.Advance(Delay + Retry);
        var waited = !island.Hiding.Tucked && island.Hiding.TuckWork is not null;
        Set(false);
        island.Advance(Retry);
        Assert.True(waited && island.Hiding.Tucked, $"busy with {busy}, it hides only once that is over");
    }

    [Fact]
    public void AReactionPastItsTimeKeepsNothingWaiting()
    {
        var island = Out();
        island.PendingReactionDeadline = island.Now + 1;
        island.Advance(Delay);
        Assert.True(island.Hiding.Tucked);
    }

    public static TheoryData<string> OutOfSight => new() { "activity", "open", "no room" };

    [Theory, MemberData(nameof(OutOfSight))]
    public void OutOfSightItSimplyStaysIn(string why)
    {
        var island = Out();
        switch (why)
        {
            case "activity": island.RestsInPlace = false; break;
            case "open": island.Expanded = true; break;
            default: island.CanHost = false; break;
        }
        island.Advance(Delay);
        Assert.True(island.Hiding.Tucked);
        Assert.Null(island.Visit);
    }

    [Fact]
    public void WithReducedMotionItGoesWithoutTheHop()
    {
        var island = Out();
        island.ReduceMotion = true;
        island.Advance(Delay);
        Assert.True(island.Hiding.Tucked);
        Assert.Null(island.Visit);
        Assert.Equal(1, island.Refreshes);
    }

    [Fact]
    public void AStoppedIslandHidesNothing()
    {
        var island = Out();
        island.Running = false;
        island.Advance(Delay * 3);
        Assert.False(island.Hiding.Tucked);
        Assert.Null(island.Hiding.TuckWork);
    }

    [Fact]
    public void TurnedOnItGoesInAtOnceAndTurnedOffItComesBackOut()
    {
        var island = new Island();
        island.Hiding.Sync(switchedOn: false);
        island.HidesWhenIdle = true;
        Assert.True(island.Hiding.Sync(switchedOn: false));
        Assert.True(island.Hiding.Tucked);
        Assert.Equal(MascotSupport.HideAway, island.Visit?.Kind);
        Assert.Null(island.Hiding.TuckWork);
        island.Advance(Away);
        island.HidesWhenIdle = false;
        Assert.True(island.Hiding.Sync(switchedOn: false));
        Assert.False(island.Hiding.Tucked);
        Assert.Equal([true], island.Entrances);
        Assert.Null(island.Hiding.TuckWork);
    }

    [Fact]
    public void TurnedOnMidStrollItGoesInAsSoonAsTheStrollIsOver()
    {
        var island = new Island();
        island.Hiding.Sync(switchedOn: false);
        island.Visit = MascotVisit.Now(MascotVisitKind.Lap, MascotMood.Wink, island.Now);
        island.HidesWhenIdle = true;
        var answered = island.Hiding.Sync(switchedOn: false) && !island.Hiding.Tucked;
        island.Visit = null;
        island.Advance(Retry);
        Assert.True(answered && island.Hiding.Tucked);
    }

    [Fact]
    public void TurnedOffWithTheIslandOpenItIsSimplyBackWhenItCloses()
    {
        var island = Out();
        island.Hiding.Tucked = true;
        island.Expanded = true;
        island.HidesWhenIdle = false;
        island.Hiding.Sync(switchedOn: false);
        Assert.False(island.Hiding.Tucked);
        Assert.Empty(island.Entrances);
    }

    [Fact]
    public void SwitchedOffWhileHiddenNothingIsLeftToHideOrBringOut()
    {
        var island = Out();
        island.Hiding.Tucked = true;
        island.MascotOn = false;
        island.HidesWhenIdle = false;
        Assert.False(island.Hiding.Sync(switchedOn: false));
        Assert.Empty(island.Entrances);
        Assert.Null(island.Hiding.TuckWork);
    }
}
