// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchTimerSupport.swift: modes, Pomodoro
// configuration, the session state machine anchored to a monotonic clock, and clock text.
namespace Notchly.Core.Notch;

public enum NotchTimerMode { Timer, Pomodoro, Stopwatch }
public enum NotchTimerPhase { Timer, Focus, ShortBreak, LongBreak, Stopwatch }

public readonly record struct NotchPomodoroConfiguration
{
    public int FocusMinutes { get; }
    public int ShortBreakMinutes { get; }
    public int LongBreakMinutes { get; }
    public int LongBreakInterval { get; }
    public int TotalSessions { get; }

    public NotchPomodoroConfiguration(int focusMinutes = 25, int shortBreakMinutes = 5, int longBreakMinutes = 15,
                                      int longBreakInterval = 4, int totalSessions = 4)
    {
        FocusMinutes = Math.Clamp(focusMinutes, 1, 180);
        ShortBreakMinutes = Math.Clamp(shortBreakMinutes, 1, 60);
        LongBreakMinutes = Math.Clamp(longBreakMinutes, 1, 60);
        LongBreakInterval = Math.Clamp(longBreakInterval, 1, 24);
        TotalSessions = Math.Clamp(totalSessions, 1, 24);
    }

    public NotchPomodoroConfiguration() : this(25) { }
}

/// <summary>One timer, Pomodoro or stopwatch run. Times are seconds on a monotonic clock.</summary>
public sealed class NotchTimerSession
{
    public NotchTimerMode Mode { get; private set; } = NotchTimerMode.Timer;
    public NotchTimerPhase Phase { get; private set; } = NotchTimerPhase.Timer;
    public double Duration { get; private set; } = 300;
    public double? Anchor { get; private set; }
    public double? PausedReading { get; private set; }
    public bool Completed { get; private set; }
    public int CompletedFocuses { get; private set; }
    public NotchPomodoroConfiguration Configuration { get; private set; } = new();

    public bool CountsUp => Mode == NotchTimerMode.Stopwatch;
    public bool IsRunning => Anchor is not null;
    public bool IsPaused => PausedReading is not null;
    public bool HasSession => IsRunning || IsPaused || Completed;
    public double? Deadline => CountsUp ? null : Anchor;
    public bool CycleFinished => Mode == NotchTimerMode.Pomodoro && Completed && CompletedFocuses >= Configuration.TotalSessions;
    public bool CanStartNext => Mode == NotchTimerMode.Pomodoro && Completed && !CycleFinished;
    public int SessionNumber =>
        Math.Min(Configuration.TotalSessions, CompletedFocuses + (Phase == NotchTimerPhase.Focus && !Completed ? 1 : 0));

    /// <summary>Seconds left in a countdown, or elapsed on a stopwatch.</summary>
    public double Reading(double now)
    {
        if (Anchor is { } anchor) return Math.Max(0, CountsUp ? now - anchor : anchor - now);
        return PausedReading ?? (Completed || CountsUp ? 0 : Duration);
    }

    public void Start(NotchTimerMode mode, int minutes, double now, NotchPomodoroConfiguration? configuration = null)
    {
        if (HasSession || !double.IsFinite(now)) return;
        Mode = mode;
        Configuration = configuration ?? new NotchPomodoroConfiguration();
        CompletedFocuses = 0;
        switch (mode)
        {
            case NotchTimerMode.Timer:
                Phase = NotchTimerPhase.Timer;
                Duration = NotchTimerSupport.TimerMinutes(minutes) * 60.0;
                Anchor = now + Duration;
                break;
            case NotchTimerMode.Pomodoro:
                Phase = NotchTimerPhase.Focus;
                Duration = Configuration.FocusMinutes * 60.0;
                Anchor = now + Duration;
                break;
            case NotchTimerMode.Stopwatch:
                Phase = NotchTimerPhase.Stopwatch;
                Duration = 0;
                Anchor = now;
                break;
        }
    }

    public bool FinishIfDue(double now)
    {
        if (Deadline is not { } deadline || !double.IsFinite(now) || now < deadline) return false;
        Anchor = null;
        PausedReading = null;
        Completed = true;
        if (Phase == NotchTimerPhase.Focus) CompletedFocuses++;
        return true;
    }

    public void Pause(double now)
    {
        if (!IsRunning || !double.IsFinite(now)) return;
        if (FinishIfDue(now)) return;
        PausedReading = Reading(now);
        Anchor = null;
    }

    public void Resume(double now)
    {
        if (PausedReading is not { } reading || !double.IsFinite(now)) return;
        PausedReading = null;
        Anchor = CountsUp ? now - reading : now + reading;
    }

    public NotchTimerPhase NextPhase => Phase == NotchTimerPhase.Focus
        ? (CompletedFocuses % Configuration.LongBreakInterval == 0 ? NotchTimerPhase.LongBreak : NotchTimerPhase.ShortBreak)
        : NotchTimerPhase.Focus;

    /// <summary>A new phase always starts by an explicit action, never silently after a long sleep.</summary>
    public void StartNext(double now)
    {
        if (!CanStartNext || !double.IsFinite(now)) return;
        Phase = NextPhase;
        Duration = (Phase == NotchTimerPhase.Focus ? Configuration.FocusMinutes
            : Phase == NotchTimerPhase.LongBreak ? Configuration.LongBreakMinutes : Configuration.ShortBreakMinutes) * 60.0;
        Completed = false;
        Anchor = now + Duration;
    }

    public void Cancel()
    {
        Mode = NotchTimerMode.Timer; Phase = NotchTimerPhase.Timer; Duration = 300; Anchor = null; PausedReading = null;
        Completed = false; CompletedFocuses = 0; Configuration = new NotchPomodoroConfiguration();
    }
}

public static class NotchTimerSupport
{
    public const double TimerLimit = 180 * 60;
    /// <summary>A stopwatch's clock saturates at the widest reading it fits, two hour digits.</summary>
    public const double StopwatchLimit = 100 * 3600 - 1;

    public static int TimerMinutes(int value) => Math.Min(180, Math.Max(1, value));

    private static int WholeSeconds(double value, double limit, bool roundUp)
    {
        if (!double.IsFinite(value)) return 0;
        var clamped = Math.Min(limit, Math.Max(0, value));
        return (int)(roundUp ? Math.Ceiling(clamped) : Math.Floor(clamped));
    }

    private static string ClockText(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60:00}:{seconds % 60:00}";

    /// <summary>A countdown rounds up: it never shows 00:00 while time is left.</summary>
    public static string ClockText(double remaining) => ClockText(WholeSeconds(remaining, TimerLimit, roundUp: true));

    /// <summary>Elapsed time never rounds up: a stopwatch cannot show a second that has not passed.</summary>
    public static string StopwatchText(double elapsed) => ClockText(WholeSeconds(elapsed, StopwatchLimit, roundUp: false));

    /// <summary>Hours read as "1h35", never "1:35", which would pass for minutes and seconds.</summary>
    public static string HoursText(int hours, int minutes) => $"{hours}h{minutes:00}";

    public static string CompactHoursText(double remaining)
    {
        var seconds = WholeSeconds(remaining, TimerLimit, roundUp: false);
        return HoursText(seconds / 3600, seconds / 60 % 60);
    }

    public static string CompactStopwatchText(double elapsed)
    {
        var seconds = WholeSeconds(elapsed, StopwatchLimit, roundUp: false);
        return seconds >= 3600 ? HoursText(seconds / 3600, seconds / 60 % 60) : ClockText(seconds);
    }

    public static string ClockText(NotchTimerSession session, double now) =>
        session.CountsUp ? StopwatchText(session.Reading(now)) : ClockText(session.Reading(now));

    /// <summary>The closed strip's short reading: "12m", "45s", "1h05".</summary>
    public static string CompactText(NotchTimerSession session, double now)
    {
        var reading = session.Reading(now);
        if (session.CountsUp) return CompactStopwatchText(reading);
        if (reading >= 3600) return CompactHoursText(reading);
        var seconds = double.IsFinite(reading) ? Math.Ceiling(Math.Min(TimerLimit, Math.Max(0, reading))) : 0;
        return seconds >= 60 ? $"{(int)Math.Floor(seconds / 60)}m" : $"{(int)seconds}s";
    }

    /// <summary>Seconds until the reading next crosses a whole second, so ticks land just after changes.</summary>
    public static double SecondBoundaryOffset(NotchTimerSession session, double now)
    {
        var reading = session.Reading(now);
        if (!double.IsFinite(reading)) return 0;
        var fraction = reading % 1;
        var offset = session.CountsUp ? 1 - fraction : fraction;
        return offset > 0 ? offset : 1;
    }

    public static string PhaseTitle(NotchTimerPhase phase) => phase switch
    {
        NotchTimerPhase.Focus => "Focus",
        NotchTimerPhase.ShortBreak => "Short break",
        NotchTimerPhase.LongBreak => "Long break",
        NotchTimerPhase.Stopwatch => "Stopwatch",
        _ => "Timer",
    };
}
