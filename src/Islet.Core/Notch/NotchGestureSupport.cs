// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchGestureSupport.swift.
// One decision per physical scroll sequence. Windows wheel messages carry no phases, so the
// app feeds hasPhase:false; a precision touchpad's small deltas count as `precise`.
namespace Islet.Core.Notch;

public sealed class NotchGestureSupport
{
    public enum Action { Open, Close, NextTrack, PreviousTrack }
    private enum Axis { Horizontal, Vertical }
    private readonly record struct Origin(bool Vertical, bool Horizontal, bool Expanded);

    private Origin? _origin;
    private Axis? _axis;
    private double _pendingX, _pendingY, _distance;
    private bool _fired, _coasting;
    private double? _lastTimestamp;
    private const double VerticalThreshold = 16.0;

    /// <summary>Upstream scales a non-precise wheel line to 24 points.</summary>
    public static double Movement(double delta, bool precise, bool inverted)
    {
        if (!double.IsFinite(delta)) return 0;
        return delta * (inverted ? 1 : -1) * (precise ? 1 : 24);
    }

    public static bool AllowsVertical(bool expanded, bool inHeader, bool musicSurface, bool control, bool scroll) =>
        !control && (!expanded || inHeader || (musicSurface && !scroll));

    private void Reset()
    {
        _origin = null; _axis = null; _pendingX = _pendingY = _distance = 0;
        _fired = _coasting = false; _lastTimestamp = null;
    }

    public Action? Handle(double x, double y, double timestamp, bool began, bool ended, bool momentum, bool precise,
                          bool hasPhase, bool allowVertical, bool allowHorizontal, bool expanded)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(x) || !double.IsFinite(y)) { Reset(); return null; }
        // A quick flick lifts the fingers before the swipe travelled far enough; its momentum can
        // finish the opening or closing it began, never a track change.
        var transition = _coasting && !momentum && !hasPhase && precise
            && _lastTimestamp is { } t0 && timestamp >= t0 && timestamp - t0 <= 0.35;
        if (momentum || transition)
        {
            if (!_coasting || _fired || _origin is not { } origin || _lastTimestamp is not { } last
                || timestamp < last || timestamp - last > 0.35 || Math.Abs(y) < Math.Abs(x) * 1.5)
            {
                Reset(); return null;
            }
            _lastTimestamp = timestamp;
            _distance += y;
            return VerticalAction(origin.Expanded);
        }
        if (ended)
        {
            _coasting = _origin is not null && _axis == Axis.Vertical && !_fired;
            if (_coasting) _lastTimestamp = timestamp; else Reset();
            return null;
        }
        if (_coasting) Reset();
        if (hasPhase)
        {
            if (began)
            {
                Reset();
                _origin = new Origin(allowVertical, allowHorizontal, expanded);
            }
            if (_origin is null) return null;
            if (_lastTimestamp is { } t1 && timestamp < t1) { Reset(); return null; }
        }
        else if (_origin is not null || began || (_lastTimestamp is { } t2 && (timestamp < t2 || timestamp - t2 > 0.35)))
        {
            Reset();
        }
        _lastTimestamp = timestamp;
        if (_fired) return null;
        var vertical = _origin?.Vertical ?? allowVertical;
        var horizontal = _origin?.Horizontal ?? allowHorizontal;
        var isExpanded = _origin?.Expanded ?? expanded;
        if (_axis is null)
        {
            // Establish intent from a little accumulated travel, not the first fraction of a point.
            _pendingX += x;
            _pendingY += y;
            if (precise && horizontal && Math.Abs(_pendingX) >= 4 && Math.Abs(_pendingX) >= Math.Abs(_pendingY) * 1.5)
            {
                _axis = Axis.Horizontal;
                _distance = _pendingX;
            }
            else if (vertical && Math.Abs(_pendingY) >= 4 && Math.Abs(_pendingY) >= Math.Abs(_pendingX) * 1.5)
            {
                _axis = Axis.Vertical;
                _distance = _pendingY;
            }
            else return null;
        }
        else
        {
            _distance += _axis == Axis.Horizontal ? x : y;
        }
        switch (_axis)
        {
            case Axis.Horizontal when horizontal:
                if (Math.Abs(_distance) < 40) return null;
                _fired = true;
                return _distance < 0 ? Action.NextTrack : Action.PreviousTrack;
            case Axis.Vertical when vertical:
                return VerticalAction(isExpanded);
            default:
                return null;
        }
    }

    private Action? VerticalAction(bool expanded)
    {
        if (Math.Abs(_distance) < VerticalThreshold) return null;
        _fired = true;
        if (_distance > 0 && !expanded) return Action.Open;
        if (_distance < 0 && expanded) return Action.Close;
        return null;
    }
}
