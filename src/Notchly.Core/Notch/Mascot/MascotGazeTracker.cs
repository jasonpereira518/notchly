// SPDX-License-Identifier: GPL-3.0-or-later
// Windows addition: decides what the companion's eyes do with each reading of the cursor anywhere on the screen
// (upstream's follow the pointer over the island only). Pure, so the timing is tested here and the island only polls.
namespace Notchly.Core.Notch.Mascot;

public enum MascotGazeStep
{
    /// <summary>Nothing to do: the cursor has not moved, or the eyes already lost interest.</summary>
    None,
    /// <summary>The cursor moved: the eyes go to where it is.</summary>
    Follow,
    /// <summary>The cursor kept still long enough: the eyes look ahead again.</summary>
    Lose,
}

public sealed class MascotGazeTracker
{
    private PointD? _last;
    private double _movedAt;
    private bool _lost = true;

    /// <summary>Feeds one reading of the cursor, in screen pixels, at <paramref name="now"/> seconds.</summary>
    public MascotGazeStep Update(PointD cursor, double now)
    {
        if (_last is not { } last || Distance(last, cursor) >= MascotSupport.GazeMoveThreshold)
        {
            _last = cursor;
            _movedAt = now;
            _lost = false;
            return MascotGazeStep.Follow;
        }
        if (!_lost && now - _movedAt >= MascotSupport.GazeStill)
        {
            _lost = true;
            return MascotGazeStep.Lose;
        }
        return MascotGazeStep.None;
    }

    /// <summary>The companion stopped watching (hidden, asleep, away): the next reading starts afresh.</summary>
    public void Reset()
    {
        _last = null;
        _lost = true;
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
