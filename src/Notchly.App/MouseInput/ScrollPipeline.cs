// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/ScrollInverter.swift (the one shared wheel tap that serves the inverter, sideways,
// zoom, linear, speed and, through its engine, smooth scrolling). Windows lets a low-level hook swallow a wheel tick but not edit
// it, so a tick that needs changing is swallowed and one marked event is sent in its place. Only mouse wheels are touched: a
// touchpad's finer ticks pass through. All the maths is in Notchly.Core.Input.ScrollSupport.
using System.Runtime.InteropServices;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.MouseInput;

/// <summary>The shared handler; every scroll feature's service just asks it to read the settings again.</summary>
public sealed class ScrollPipeline : IDisposable
{
    private const uint WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E;

    [DllImport("user32.dll")] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct InputMessageSource { public int DeviceType; public int OriginId; }

    private readonly App _app;
    private readonly SettingsStore _settings;
    private volatile ScrollConfig _config = ScrollConfig.Off;
    private int _response = 65, _coast;
    private readonly ScrollPlanner _planner = new();
    private IDisposable? _subscription;
    private SmoothEngine? _engine;
        private readonly Dictionary<IntPtr, (string? Program, long At)> _programs = [];
    private int _systemLines = ScrollSupport.DefaultSystemLines;

    public ScrollPipeline(App app)
    {
        _app = app;
        _settings = app.Settings;
    }

    private bool On(AppFeature feature, string key) => _settings.IsAvailable(feature) && _settings.GetBool(key);

    public void Sync()
    {
        var c = new ScrollConfig(
            On(AppFeature.ScrollInverter, DefaultsKey.ScrollInverterEnabled), On(AppFeature.ScrollInverter, DefaultsKey.ScrollInverterHorizontalEnabled),
            On(AppFeature.ScrollHorizontal, DefaultsKey.ScrollHorizontalEnabled), ScrollSupport.SanitizedModifier(_settings.GetString(DefaultsKey.ScrollHorizontalModifier), ScrollSupport.SidewaysModifiers, "shift"),
            On(AppFeature.ScrollZoom, DefaultsKey.PinchZoomEnabled), ScrollSupport.SanitizedModifier(_settings.GetString(DefaultsKey.PinchZoomModifier), ScrollSupport.ZoomModifiers, "control"),
            On(AppFeature.LinearScroll, DefaultsKey.LinearScrollEnabled), ScrollSupport.SanitizedLines(_settings.GetInt(DefaultsKey.LinearScrollLines, 3)),
            On(AppFeature.ScrollSpeed, DefaultsKey.ScrollSpeedEnabled), ScrollSupport.SanitizedModifier(_settings.GetString(DefaultsKey.ScrollSpeedModifier), ScrollSupport.SpeedModifiers, "alt"),
            ScrollSupport.SanitizedMultiplier(_settings.GetInt(DefaultsKey.ScrollSpeedMultiplier, 3)),
            On(AppFeature.SmoothScroll, DefaultsKey.SmoothScrollEnabled), ScrollSupport.SanitizedStep(_settings.GetInt(DefaultsKey.SmoothScrollStep, 40)),
            _settings.GetList(DefaultsKey.ScrollInverterExceptions), _settings.GetList(DefaultsKey.LinearScrollExceptions),
            _settings.GetList(DefaultsKey.ScrollSpeedExceptions), _settings.GetList(DefaultsKey.SmoothScrollExceptions));
        _response = ScrollSupport.SanitizedResponse(_settings.GetInt(DefaultsKey.SmoothScrollResponse, 65));
        _coast = ScrollSupport.SanitizedCoast(_settings.GetInt(DefaultsKey.SmoothScrollCoast, 0));
        _planner.Reset();
        _config = c;
        uint lines = 3;
        if (SystemParametersInfo(0x0068, 0, ref lines, 0) && lines is > 0 and < 100) _systemLines = (int)lines;
        if (c.Any && _subscription is null) _subscription = _app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Transform);
        else if (!c.Any && _subscription is not null) { _subscription.Dispose(); _subscription = null; }
        if (c.Smooth) _engine ??= new SmoothEngine(_app.Injector);
        else if (_engine is not null) { _engine.Dispose(); _engine = null; }
        _engine?.Configure(_response, _coast);
    }

    /// <summary>The program of the window under the pointer, for the exception lists; remembered for a couple of seconds.</summary>
    private string? ProgramAt(int x, int y)
    {
        var hwnd = WindowFrame.TopLevelAt(x, y);
        if (hwnd == IntPtr.Zero) return null;
        var now = Environment.TickCount64;
        if (_programs.TryGetValue(hwnd, out var cached) && now - cached.At < 2000) return cached.Program;
        var program = WindowFrame.ProgramOf(hwnd);
        if (_programs.Count > 64) _programs.Clear();
        _programs[hwnd] = (program, now);
        return program;
    }

    /// <summary>Whether Windows says this tick came from a mouse; null when it does not say (the tick's size decides then).</summary>
    private static bool? SourceIsMouse()
    {
        try
        {
            if (!GetCurrentInputMessageSource(out var s)) return null;
            return s.DeviceType switch { 2 => true, 16 => false, _ => null };
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private bool OnMouse(in MouseEvent e)
    {
        if (e.Message is not (WmMouseWheel or WmMouseHWheel) || e.IsOwn) return false;
        var c = _config;
        if (!c.Any || _app.InputGate.ShouldPassThrough) return false;
        var program = ProgramAt(e.X, e.Y);
        var plan = _planner.Plan(c, e.WheelDelta, e.Message == WmMouseWheel, KeyState.HeldModifiers(), program, SourceIsMouse(), _systemLines);
        var injector = _app.Injector;
        switch (plan.Outcome)
        {
            case ScrollOutcome.Pass:
                return false;
            case ScrollOutcome.Swallow:
                return true;
            case ScrollOutcome.Send:
                injector.Wheel(plan.Delta, plan.Horizontal, InputSource.Scroll);
                return true;
            case ScrollOutcome.Zoom:
            {
                // The physical modifier is lifted while Ctrl stands in for it, then put back.
                var key = ModifierKey(plan.Modifier ?? "control");
                injector.Key(key, false, InputSource.Scroll);
                injector.Key(0x11, true, InputSource.Scroll);
                injector.Wheel(plan.Delta, false, InputSource.Scroll);
                injector.Key(0x11, false, InputSource.Scroll);
                injector.Key(key, true, InputSource.Scroll);
                return true;
            }
            default:
                if (_engine is { } engine) { engine.Add(plan.Horizontal, plan.Distance, c.Step); return true; }
                injector.Wheel(plan.Delta, plan.Horizontal, InputSource.Scroll);
                return true;
        }
    }

    private static int ModifierKey(string name) => name switch { "shift" => 0x10, "alt" => 0x12, "win" => 0x5B, _ => 0x11 };

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _engine?.Dispose();
        _engine = null;
    }

    // MARK: The glide

    /// <summary>
    /// Spreads each tick over time: a timer at about 60 frames a second sends what is left of each axis's distance in steps that
    /// shrink as the glide runs out, so a notch becomes a smooth scroll. A tick in the other direction replaces what was left.
    /// </summary>
    private sealed class SmoothEngine : IDisposable
    {
        private readonly IInputInjector _injector;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly object _gate = new();
        private readonly double[] _remaining = new double[2], _total = new double[2], _carry = new double[2];
        private volatile bool _stop;
        private int _response = 65, _coast;

        public SmoothEngine(IInputInjector injector)
        {
            _injector = injector;
            timeBeginPeriod(1);
            _thread = new Thread(Run) { IsBackground = true, Name = "Notchly smooth scroll", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Configure(int response, int coast) { _response = response; _coast = coast; }

        public void Add(bool horizontal, double distance, int step)
        {
            var axis = horizontal ? 1 : 0;
            lock (_gate)
            {
                // A reversal drops the tail; more in the same direction adds to it.
                if (_remaining[axis] != 0 && Math.Sign(_remaining[axis]) != Math.Sign(distance)) { _remaining[axis] = 0; _carry[axis] = 0; }
                _remaining[axis] += distance;
                _total[axis] = Math.Max(Math.Abs(_remaining[axis]), 1);
            }
            _wake.Set();
        }

        private void Run()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
            while (!_stop)
            {
                bool busy;
                lock (_gate) busy = _remaining[0] != 0 || _remaining[1] != 0;
                if (!busy) { _wake.WaitOne(); last = clock.Elapsed.TotalSeconds; if (_stop) return; }
                Thread.Sleep((int)(ScrollSupport.FrameInterval * 1000));
                var now = clock.Elapsed.TotalSeconds;
                var dt = now - last;
                last = now;
                for (var axis = 0; axis < 2; axis++)
                {
                    double amount;
                    lock (_gate)
                    {
                        var remaining = _remaining[axis];
                        if (remaining == 0) continue;
                        var travelled = 1 - Math.Abs(remaining) / _total[axis];
                        var stretch = ScrollSupport.CoastStretch(_coast, travelled);
                        amount = ScrollSupport.FrameDelta(remaining, dt, _response, stretch) * Math.Sign(remaining);
                        _remaining[axis] = Math.Abs(remaining) - Math.Abs(amount) < 1e-6 ? 0 : remaining - amount;
                        if (_remaining[axis] == 0) { amount += _carry[axis]; _carry[axis] = 0; }
                    }
                    double carry;
                    lock (_gate) carry = _carry[axis];
                    var units = ScrollSupport.Emit(amount, ref carry);
                    lock (_gate) _carry[axis] = carry;
                    if (units != 0) _injector.Wheel(units, horizontal: axis == 1, InputSource.SmoothScroll);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            timeEndPeriod(1);
        }
    }
}

/// <summary>The services every scroll feature binds to: they only ask the shared pipeline to read the settings again.</summary>
public sealed class ScrollFeatureService(App app) : IFeatureService
{
    public void Sync() => app.Scroll.Sync();
    public void Dispose() => app.Scroll.Sync();
}
