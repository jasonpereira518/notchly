// SPDX-License-Identifier: GPL-3.0-or-later
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class NotchTimerTests
{
    [Fact]
    public void CountdownRoundsUpAndStopwatchRoundsDown()
    {
        Assert.Equal("05:00", NotchTimerSupport.ClockText(299.2));
        Assert.Equal("00:01", NotchTimerSupport.ClockText(0.01));
        Assert.Equal("00:00", NotchTimerSupport.StopwatchText(0.99));
        Assert.Equal("1:00:00", NotchTimerSupport.ClockText(3600));
        Assert.Equal("1h35", NotchTimerSupport.CompactStopwatchText(3600 + 35 * 60 + 10));
        Assert.Equal("99:59:59", NotchTimerSupport.StopwatchText(1e9));
    }

    [Fact]
    public void TimerPausesResumesAndFinishes()
    {
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Timer, minutes: 1, now: 100);
        Assert.True(session.IsRunning);
        Assert.Equal(60, session.Reading(100));
        session.Pause(130);
        Assert.Equal(30, session.Reading(500));
        session.Resume(500);
        Assert.False(session.FinishIfDue(529));
        Assert.True(session.FinishIfDue(530));
        Assert.True(session.Completed);
        Assert.Equal(0, session.Reading(600));
    }

    [Fact]
    public void TimerMinutesAreClamped()
    {
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Timer, minutes: 999, now: 0);
        Assert.Equal(180 * 60, session.Duration);
    }

    [Fact]
    public void PomodoroAlternatesAndTakesALongBreakOnItsInterval()
    {
        var config = new NotchPomodoroConfiguration(focusMinutes: 1, shortBreakMinutes: 1, longBreakMinutes: 2,
                                                    longBreakInterval: 2, totalSessions: 3);
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Pomodoro, 0, now: 0, config);
        Assert.Equal(1, session.SessionNumber);
        Assert.True(session.FinishIfDue(60));
        Assert.Equal(NotchTimerPhase.ShortBreak, session.NextPhase);
        session.StartNext(60);
        Assert.True(session.FinishIfDue(120));
        session.StartNext(120);
        Assert.Equal(NotchTimerPhase.Focus, session.Phase);
        Assert.True(session.FinishIfDue(180));
        Assert.Equal(NotchTimerPhase.LongBreak, session.NextPhase);
        session.StartNext(180);
        Assert.Equal(120, session.Duration);
    }

    [Fact]
    public void ANewPhaseNeverStartsByItself()
    {
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Pomodoro, 0, now: 0);
        session.FinishIfDue(1e6);
        Assert.True(session.Completed);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void StopwatchCountsUp()
    {
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Stopwatch, 0, now: 10);
        Assert.Equal(5, session.Reading(15));
        Assert.Null(session.Deadline);
        Assert.Equal("00:05", NotchTimerSupport.ClockText(session, 15.7));
    }

    [Fact]
    public void CompactTextShortensReadings()
    {
        var session = new NotchTimerSession();
        session.Start(NotchTimerMode.Timer, 15, now: 0);
        Assert.Equal("15m", NotchTimerSupport.CompactText(session, 0));
        Assert.Equal("30s", NotchTimerSupport.CompactText(session, 15 * 60 - 30));
    }
}
