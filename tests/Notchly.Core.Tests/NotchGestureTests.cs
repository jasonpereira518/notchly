// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Tests/NotchGestureTests.swift (the scroll-sequence contracts).
using Notchly.Core.Notch;
using A = Notchly.Core.Notch.NotchGestureSupport.Action;

namespace Notchly.Core.Tests;

public class NotchGestureTests
{
    private readonly NotchGestureSupport _gesture = new();
    private double _time = 1.0;

    private A? Feed(double x = 0, double y = 0, bool began = false, bool ended = false, bool momentum = false,
                    bool precise = true, bool phased = true, bool vertical = true, bool horizontal = true, bool expanded = false)
    {
        _time += 0.01;
        return _gesture.Handle(x, y, _time, began, ended, momentum, precise, phased, vertical, horizontal, expanded);
    }

    [Fact]
    public void ScrollSequenceContracts()
    {
        Assert.True(Feed(0, 5, began: true) == null && Feed(0, 10) == null && Feed(0, 10) == A.Open,
            "small vertical movement accumulates into one intentional opening");
        Assert.Null(Feed(0, -100, expanded: true));
        Assert.True(Feed(0, 0, ended: true) == null && Feed(0, -80, momentum: true, expanded: true) == null,
            "momentum after a swipe that already acted never changes presentation");
        Assert.Equal(A.Close, Feed(0, -30, began: true, expanded: true));
        Assert.True(Feed(-10, 0, began: true) == null && Feed(-15, 0) == null && Feed(-20, 0) == A.NextTrack,
            "a left swipe changes one track after its threshold");
        for (var i = 0; i < 100; i++) Assert.Null(Feed(-50, 0));
        Assert.Equal(A.PreviousTrack, Feed(45, 0, began: true));
        Assert.Null(Feed(40, 40, began: true));
        Assert.True(Feed(1, 0, began: true, expanded: true) == null && Feed(0, -24, expanded: true) == A.Close,
            "small horizontal touchdown noise cannot lock out a vertical closing swipe");
        Assert.True(Feed(0, 1, began: true) == null && Feed(-40, 0) == A.NextTrack,
            "small vertical touchdown noise cannot lock out a track swipe");
        Assert.Null(Feed(0, 0.1, began: true));
        for (var i = 0; i < 7; i++) Assert.Null(Feed(0, 0.5));
        Assert.Equal(A.Open, Feed(0, 21));
        Assert.True(Feed(0, 15, began: true) == null && Feed(0, 1) == A.Open, "a short downward swipe opens the island");
        Assert.True(Feed(0, 6, began: true) == null && Feed(0, 4) == null && Feed(0, 0, ended: true) == null
                    && Feed(0, 8, momentum: true, phased: false) == A.Open,
            "the momentum of a quick flick finishes the opening it began");
        Assert.True(Feed(0, 30, momentum: true, phased: false) == null && Feed(0, 0, momentum: true, phased: false) == null,
            "a flick opens the island once");
        Assert.True(Feed(0, -6, began: true, expanded: true) == null && Feed(0, 0, ended: true, expanded: true) == null
                    && Feed(0, -12, momentum: true, phased: false, expanded: true) == A.Close,
            "the momentum of a quick upward flick closes the island");
        Assert.True(Feed(0, 2, began: true) == null && Feed(0, 0, ended: true) == null
                    && Feed(0, 40, momentum: true, phased: false) == null,
            "momentum cannot open the island for a touch that never chose a direction");
        Assert.True(Feed(-15, 0, began: true) == null && Feed(0, 0, ended: true) == null
                    && Feed(-80, 0, momentum: true, phased: false) == null,
            "momentum never finishes a track change");
    }

    [Fact]
    public void PhaselessWheelNotchesOpenAndClose()
    {
        // A Windows wheel notch: no phases, not precise, 1 line = 24 points after Movement().
        var down = NotchGestureSupport.Movement(-1, precise: false, inverted: false);
        Assert.Equal(24, down);
        Assert.Equal(A.Open, Feed(0, down, precise: false, phased: false));
        _time += 1;
        Assert.Equal(A.Close, Feed(0, -down, precise: false, phased: false, expanded: true));
    }

    [Fact]
    public void WheelNeverChangesTracks()
    {
        Assert.Null(Feed(-80, 0, precise: false, phased: false));
    }
}
