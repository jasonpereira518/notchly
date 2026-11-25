// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/MouseButtons/MouseButtonShortcutSupport.swift and MouseSpacesGestureSupport.swift, the
// click debounce (MouseClickDebounceSupport.swift) and the key debounce (KeyboardDebounceSupport.swift): which buttons can be
// mapped, how a mapping is stored, the side-wheel burst gate, the desktop drag, and the rules that drop a bouncing click or
// key. Windows differences: a mouse reports Back and Forward (buttons 3 and 4) and wheel tilt, and the Spaces drag switches
// virtual desktops.
using System.Globalization;

namespace Notchly.Core.Input;

public static class ButtonShortcutSupport
{
    public const int SideWheelLeft = -2, SideWheelRight = -1;

    /// <summary>Buttons 3 to 31 (3 is Back, 4 is Forward) and the two tilt-wheel directions; the main three never map.</summary>
    public static bool IsMappable(int button) => button is >= 3 and <= 31 or SideWheelLeft or SideWheelRight;

    public static string Name(int button) => button switch
    {
        SideWheelLeft => "Wheel tilt left",
        SideWheelRight => "Wheel tilt right",
        3 => "Back button",
        4 => "Forward button",
        _ => $"Button {button + 1}",
    };

    /// <summary>The saved map, "3=control+alt:37;4=alt:39": entries for buttons that cannot map, or with no shortcut, are dropped.</summary>
    public static IReadOnlyDictionary<int, string> Parse(string? stored)
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in (stored ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length == 2 && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var button)
                && IsMappable(button) && Shortcuts.Shortcut.Parse(pair[1]) is not null)
                map[button] = pair[1];
        }
        return map;
    }

    public static string Format(IReadOnlyDictionary<int, string> map) =>
        string.Join(";", map.Where(p => IsMappable(p.Key)).OrderBy(p => p.Key).Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + "=" + p.Value));

    /// <summary>A horizontal tick counts as a side-wheel press only when it is bigger than the vertical one; tilting right is positive.</summary>
    public static int? SideWheelButton(int horizontal, int vertical) =>
        horizontal == 0 || Math.Abs(horizontal) <= Math.Abs(vertical) ? null : horizontal > 0 ? SideWheelRight : SideWheelLeft;

    /// <summary>One tilt fires once per burst: it can fire again only after a quiet gap of 250 ms or a change of direction.</summary>
    public sealed class SideWheelGate
    {
        public const double QuietGap = 250;
        private double _lastMs = double.NegativeInfinity;
        private int _lastButton;

        public bool Fires(int button, double nowMs)
        {
            var fire = button != _lastButton || nowMs - _lastMs > QuietGap;
            _lastButton = button;
            _lastMs = nowMs;
            return fire;
        }
    }

    // MARK: The desktop drag

    public enum DesktopAction { None, Left, Right, TaskView }

    /// <summary>
    /// Holding a chosen button and dragging switches desktops: 220 pixels sideways is one desktop, 150 pixels up opens Task View, and a
    /// cooldown of 0.35 s and a bank of one step keep a long drag from racing through every desktop.
    /// </summary>
    public sealed class DesktopDragTracker
    {
        public const double HorizontalStep = 220, VerticalStep = 150, CooldownSeconds = 0.35;
        private double _x, _y, _lastFired = double.NegativeInfinity;

        public void Reset() { _x = 0; _y = 0; }

        public DesktopAction Add(double dx, double dy, double nowSeconds)
        {
            _x += dx;
            _y += dy;
            // At most one step is banked, so a long drag does not queue up desktops.
            _x = Math.Clamp(_x, -HorizontalStep * 2 + 1, HorizontalStep * 2 - 1);
            if (nowSeconds - _lastFired < CooldownSeconds) return DesktopAction.None;
            if (_y <= -VerticalStep && Math.Abs(_y) > Math.Abs(_x)) { _y = 0; _x = 0; _lastFired = nowSeconds; return DesktopAction.TaskView; }
            if (_x >= HorizontalStep) { _x -= HorizontalStep; _lastFired = nowSeconds; return DesktopAction.Right; }
            if (_x <= -HorizontalStep) { _x += HorizontalStep; _lastFired = nowSeconds; return DesktopAction.Left; }
            return DesktopAction.None;
        }
    }
}

/// <summary>What a debounce filter decided about an event.</summary>
public enum DebounceVerdict { Pass, Suppress }

public static class ClickDebounceSupport
{
    public const int DefaultWindowMs = 25;

    /// <summary>The window in milliseconds, 5 to 100; a value outside that is the default, not the nearest limit.</summary>
    public static int SanitizedWindow(int ms) => ms is >= 5 and <= 100 ? ms : DefaultWindowMs;

    /// <summary>Left, right and middle clicks only: the extra buttons are never filtered.</summary>
    public static bool Applies(int button) => button is 0 or 1 or 2;

    /// <summary>
    /// Drops a second press that follows a release too quickly (a worn button's bounce), along with its drags and its release.
    /// Healthy clicks are never delayed, and a release with no press behind it always passes.
    /// </summary>
    public sealed class Filter(int windowMs)
    {
        private sealed class State { public bool Down, Suppressing; public double LastUp = double.NegativeInfinity; }
        private readonly State[] _buttons = [new(), new(), new()];
        private double _lastSeen = double.NegativeInfinity;

        public int WindowMs { get; set; } = SanitizedWindow(windowMs);

        public void Reset()
        {
            foreach (var s in _buttons) { s.Down = false; s.Suppressing = false; s.LastUp = double.NegativeInfinity; }
        }

        private void Tick(double timeMs)
        {
            if (timeMs < _lastSeen) Reset();
            _lastSeen = timeMs;
        }

        public DebounceVerdict Down(int button, double timeMs)
        {
            if (!Applies(button)) return DebounceVerdict.Pass;
            Tick(timeMs);
            var s = _buttons[button];
            if (s.Down || s.Suppressing) return DebounceVerdict.Suppress;
            if (timeMs - s.LastUp < WindowMs) { s.Suppressing = true; return DebounceVerdict.Suppress; }
            s.Down = true;
            return DebounceVerdict.Pass;
        }

        /// <summary>Movement while a button is held.</summary>
        public DebounceVerdict Drag(int button, double timeMs)
        {
            if (!Applies(button)) return DebounceVerdict.Pass;
            Tick(timeMs);
            return _buttons[button].Suppressing ? DebounceVerdict.Suppress : DebounceVerdict.Pass;
        }

        public DebounceVerdict Up(int button, double timeMs)
        {
            if (!Applies(button)) return DebounceVerdict.Pass;
            Tick(timeMs);
            var s = _buttons[button];
            if (s.Suppressing) { s.Suppressing = false; return DebounceVerdict.Suppress; }
            if (s.Down) { s.Down = false; s.LastUp = timeMs; }
            return DebounceVerdict.Pass;
        }
    }
}

public static class KeyDebounceSupport
{
    public const int DefaultWindowMs = 5;

    /// <summary>The window in milliseconds, 0 to 500; anything else is the default.</summary>
    public static int SanitizedWindow(int ms) => ms is >= 0 and <= 500 ? ms : DefaultWindowMs;

    /// <summary>Per-key windows as "37:100,40:0" in key order; a bad entry is skipped and an out-of-range window becomes the default.</summary>
    public static IReadOnlyDictionary<int, int> Decode(string? stored)
    {
        var map = new Dictionary<int, int>();
        foreach (var entry in (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = entry.Split(':');
            if (pair.Length == 2 && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) && key is > 0 and < 256
                && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                map[key] = SanitizedWindow(ms);
        }
        return map;
    }

    public static string Encode(IReadOnlyDictionary<int, int> windows) =>
        string.Join(",", windows.OrderBy(p => p.Key).Select(p => $"{p.Key.ToString(CultureInfo.InvariantCulture)}:{p.Value.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>
    /// Drops a press of a key that was just released or is already down: same key, within its window, strictly. Auto-repeat and
    /// key-ups are never touched, a different key is never filtered, a window of 0 turns a key off, and a time that jumps back or a
    /// gap of over five seconds starts the memory afresh.
    /// </summary>
    public sealed class Filter(int windowMs, IReadOnlyDictionary<int, int>? perKey = null)
    {
        private const double ForgetAfterMs = 5000;
        private sealed class State { public bool Down; public double AcceptedAt = double.NegativeInfinity, UpAt = double.NegativeInfinity; }
        private readonly Dictionary<int, State> _keys = [];
        private int _lastAccepted = -1;
        private double _lastSeen = double.NegativeInfinity;

        public int WindowMs { get; set; } = SanitizedWindow(windowMs);
        public IReadOnlyDictionary<int, int> PerKey { get; set; } = perKey ?? new Dictionary<int, int>();

        public void Reset() { _keys.Clear(); _lastAccepted = -1; }

        private void Tick(double timeMs)
        {
            if (timeMs < _lastSeen || timeMs - _lastSeen > ForgetAfterMs) Reset();
            _lastSeen = timeMs;
        }

        private State Of(int key) => _keys.TryGetValue(key, out var s) ? s : _keys[key] = new State();

        public DebounceVerdict Down(int key, bool isRepeat, double timeMs)
        {
            Tick(timeMs);
            var window = PerKey.TryGetValue(key, out var own) ? own : WindowMs;
            var s = Of(key);
            if (isRepeat || window <= 0) { if (!isRepeat) { s.Down = true; s.AcceptedAt = timeMs; _lastAccepted = key; } return DebounceVerdict.Pass; }
            if (s.Down) return DebounceVerdict.Suppress;
            if (_lastAccepted == key && timeMs - s.UpAt < window) return DebounceVerdict.Suppress;
            s.Down = true;
            s.AcceptedAt = timeMs;
            _lastAccepted = key;
            return DebounceVerdict.Pass;
        }

        public DebounceVerdict Up(int key, double timeMs)
        {
            Tick(timeMs);
            var s = Of(key);
            if (s.Down) { s.Down = false; s.UpAt = timeMs; }
            return DebounceVerdict.Pass;
        }
    }
}
