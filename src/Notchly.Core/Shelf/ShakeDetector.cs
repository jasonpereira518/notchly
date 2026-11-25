// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the shake test inline in upstream Sources/Vorssaint/Services/Shelf/ShelfService.swift (handleDrag, about lines 520-548),
// made a pure object: horizontal samples of a drag in, "this was a shake" out. Upstream has no test of it; ShelfSummonTests has.
namespace Notchly.Core.Shelf;

public sealed class ShakeDetector
{
    public const double WindowSeconds = 0.5;
    /// <summary>A step smaller than this does not count as a direction.</summary>
    public const double DeadZone = 6;
    public const int MinSamples = 5, MinReversals = 3;
    public const double MinTravel = 220;
    /// <summary>A second shake within this long of a summon is ignored.</summary>
    public const double CooldownSeconds = 1.0;

    private readonly List<(double Time, double X)> _samples = [];
    private double _lastSummon = double.NegativeInfinity;

    /// <summary>A new press starts over.</summary>
    public void Reset() => _samples.Clear();

    /// <summary>
    /// A sample of the drag. True when the last half second holds at least three direction changes and over 220 pixels of travel, and
    /// the last summon is over a second ago. The caller decides whether to act, and tells <see cref="Summoned"/> when it did.
    /// </summary>
    public bool Add(double timeSeconds, double x)
    {
        _samples.Add((timeSeconds, x));
        _samples.RemoveAll(s => timeSeconds - s.Time > WindowSeconds);
        if (_samples.Count < MinSamples) return false;

        double travel = 0;
        var reversals = 0;
        var last = 0;
        for (var i = 1; i < _samples.Count; i++)
        {
            var dx = _samples[i].X - _samples[i - 1].X;
            travel += Math.Abs(dx);
            var direction = dx > DeadZone ? 1 : dx < -DeadZone ? -1 : 0;
            if (direction == 0) continue;
            if (last != 0 && direction != last) reversals++;
            last = direction;
        }
        return reversals >= MinReversals && travel > MinTravel && timeSeconds - _lastSummon > CooldownSeconds;
    }

    /// <summary>The shelf was shown for the shake: the samples go and the cooldown starts.</summary>
    public void Summoned(double timeSeconds)
    {
        _lastSummon = timeSeconds;
        _samples.Clear();
    }
}
