// SPDX-License-Identifier: GPL-3.0-or-later
// The pointer-driven parts of window layout (upstream's WindowLayoutService taps): the modifier-drag gesture that moves or resizes
// the window under the pointer, the edge snap that previews and applies a layout while a title bar is dragged to a screen edge, and
// the directional ring (hold a chord, move the pointer toward a layout). They run on the hook thread, so each decision is a few
// comparisons; placing windows is handed to a worker, and previews and layouts to the UI thread.
using System.Windows.Threading;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Input;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Layout;

internal sealed class PlacementWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private (IntPtr Hwnd, WindowRect Rect, bool MoveOnly)? _next;
    private volatile bool _stop;

    public PlacementWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Notchly window placement" };
        _thread.Start();
    }

    /// <summary>The newest rectangle wins; older ones that were not placed yet are dropped.</summary>
    public void Post(IntPtr hwnd, WindowRect rect, bool moveOnly)
    {
        lock (_gate) _next = (hwnd, rect, moveOnly);
        _signal.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            _signal.WaitOne();
            if (_stop) return;
            (IntPtr Hwnd, WindowRect Rect, bool MoveOnly)? job;
            lock (_gate) { job = _next; _next = null; }
            if (job is { } j) WindowFrame.Place(j.Hwnd, j.Rect, async: true, moveOnly: j.MoveOnly);
            // About 120 placements a second is more than any display shows.
            Thread.Sleep(8);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
    }
}

public sealed partial class WindowLayoutService
{
    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;

    private IDisposable? _mouseSub, _keySub;
    private SnapPreview? _preview;
    private volatile List<SnapScreen> _screens = [];

    // Settings the hook thread reads, copied on the UI thread.
    private volatile bool _gestureOn, _edgeOn, _ringOn, _raise, _restoreOnDrag;
    private IReadOnlyList<string> _chord = ["control", "win"];
    private HashSet<EdgeSnapZone> _disabledZones = [];
    private EdgeSnapSupport.ZoneActions _zoneActions = new();
    private Shortcut? _ringShortcut;

    // The gesture.
    private WindowGestureSupport.Phase _phase;
    private IntPtr _gTarget;
    private int _gStartX, _gStartY;
    private WindowRect _gFrame;
    private bool _gResize;
    private WindowGestureSupport.Edges _gEdges;

    // The edge snap.
    private bool _eDown, _eRestored;
    private IntPtr _eTarget;
    private WindowRect _eOriginal;
    private int _eStartX, _eStartY;
    private SnapHit? _eHit;
    private readonly EdgeSnapSupport.PointerTrail _trail = new();

    // The ring.
    private bool _ringActive;
    private PointD _ringOrigin;
    private IntPtr _ringTarget;
    private WindowLayoutAction? _ringAction;

    private void RefreshScreens() =>
        _screens = Displays.All().Select(d => new SnapScreen(WindowFrame.Bounds(d), WindowFrame.WorkArea(d))).ToList();

    private void SyncPointerHandlers()
    {
        _gestureOn = _settings.GetBool(DefaultsKey.WindowGestureEnabled);
        _edgeOn = _settings.GetBool(DefaultsKey.WindowEdgeSnapEnabled);
        _raise = _settings.GetBool(DefaultsKey.WindowGestureRaiseWindow);
        _restoreOnDrag = _settings.GetBool(DefaultsKey.WindowEdgeSnapRestoreOnDrag);
        _chord = WindowGestureSupport.Modifiers(_settings.GetString(DefaultsKey.WindowGestureModifiers));
        _disabledZones = _settings.GetList(DefaultsKey.WindowEdgeSnapDisabledZones).Select(z => Enum.TryParse<EdgeSnapZone>(z, true, out var zone) ? zone : (EdgeSnapZone?)null)
            .Where(z => z is not null).Select(z => z!.Value).ToHashSet();
        _zoneActions = EdgeSnapSupport.ZoneActions.Parse(_settings.GetString(DefaultsKey.WindowEdgeSnapZoneActions));
        _ringShortcut = _settings.GetBool(DefaultsKey.WindowDirectionalEnabled) ? Shortcut.Parse(_settings.GetString(DefaultsKey.WindowDirectionalShortcut)) : null;
        _ringOn = _ringShortcut is not null;

        var wantMouse = _gestureOn || _edgeOn || _ringOn;
        if (wantMouse && _mouseSub is null) _mouseSub = _app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Transform);
        else if (!wantMouse && _mouseSub is not null) { _mouseSub.Dispose(); _mouseSub = null; ResetPointerState(); }
        if (_ringOn && _keySub is null) _keySub = _app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Transform);
        else if (!_ringOn && _keySub is not null) { _keySub.Dispose(); _keySub = null; _ringActive = false; }
        if (!_edgeOn && !_ringOn) HidePreview();
    }

    private void ResetPointerState()
    {
        _phase = WindowGestureSupport.Phase.Idle;
        _eDown = false;
        _ringActive = false;
        HidePreview();
    }

    private void DisposePointerHandlers()
    {
        _mouseSub?.Dispose();
        _keySub?.Dispose();
        _mouseSub = null;
        _keySub = null;
        _dispatcher.BeginInvoke(() => { _preview?.Dispose(); _preview = null; });
    }

    // MARK: The hook callbacks

    private bool OnMouse(in MouseEvent e)
    {
        if (e.IsOwn) return false;
        if (_app.InputGate.ShouldPassThrough && _phase == WindowGestureSupport.Phase.Idle && !_eDown && !_ringActive) return false;
        var swallow = _gestureOn && Gesture(e);
        if (!swallow && _edgeOn) Edge(e);
        if (_ringActive && e.Message == WmMouseMove) Ring(e);
        return swallow;
    }

    // MARK: The gesture

    private bool Gesture(in MouseEvent e)
    {
        switch (e.Message)
        {
            case WmLButtonDown:
            {
                if (_phase != WindowGestureSupport.Phase.Idle) { _phase = WindowGestureSupport.Phase.Idle; return false; }
                var resize = KeyState.HeldExactly(WindowGestureSupport.ResizeChord(_chord));
                if (!resize && !KeyState.HeldExactly(_chord)) return false;
                var target = WindowFrame.TopLevelAt(e.X, e.Y);
                if (!WindowFrame.IsManageable(target) || IsZoomed(target) || Ignored(target) || ForegroundApps.Describe(target) is { Elevated: true }) return false;
                _gTarget = target;
                _gStartX = e.X;
                _gStartY = e.Y;
                _gResize = resize;
                _phase = WindowGestureSupport.Phase.Pending;
                return true;
            }
            case WmMouseMove:
            {
                if (_phase == WindowGestureSupport.Phase.Pending)
                {
                    if (!WindowGestureSupport.ExceedsSlop(e.X - _gStartX, e.Y - _gStartY)) return false;
                    if (WindowFrame.Visible(_gTarget) is not { } frame) { _phase = WindowGestureSupport.Phase.Idle; return false; }
                    _gFrame = frame;
                    _gEdges = _gResize ? WindowGestureSupport.RegionFor(frame, new PointD(_gStartX, _gStartY)) : WindowGestureSupport.Edges.None;
                    _phase = WindowGestureSupport.Phase.Active;
                    if (_raise) { var target = _gTarget; _dispatcher.BeginInvoke(() => SetForegroundWindow(target)); }
                }
                if (_phase == WindowGestureSupport.Phase.Active)
                {
                    var rect = _gResize
                        ? WindowGestureSupport.Resized(_gFrame, _gEdges, new PointD(_gStartX, _gStartY), new PointD(e.X, e.Y))
                        : _gFrame with { X = _gFrame.X + e.X - _gStartX, Y = _gFrame.Y + e.Y - _gStartY };
                    _worker.Post(_gTarget, rect, moveOnly: !_gResize);
                }
                return false;
            }
            case WmLButtonUp:
            {
                if (_phase == WindowGestureSupport.Phase.Pending)
                {
                    // It was only a click: it goes through as one.
                    _phase = WindowGestureSupport.Phase.Idle;
                    _app.Injector.Button(0, true, InputSource.Layout);
                    _app.Injector.Button(0, false, InputSource.Layout);
                    return true;
                }
                if (_phase == WindowGestureSupport.Phase.Active)
                {
                    _phase = WindowGestureSupport.Phase.Idle;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    // MARK: Edge snap

    private bool Edge(in MouseEvent e)
    {
        switch (e.Message)
        {
            case WmLButtonDown:
            {
                _eDown = false;
                var target = WindowFrame.TopLevelAt(e.X, e.Y);
                if (!WindowFrame.IsManageable(target) || IsZoomed(target) || !GetWindowRect(target, out var rect) || e.Y - rect.Top > 48) return false;
                if (WindowFrame.Visible(target) is not { } frame || Ignored(target)) return false;
                _eTarget = target;
                _eOriginal = frame;
                _eStartX = e.X;
                _eStartY = e.Y;
                _eDown = true;
                _eRestored = false;
                _eHit = null;
                _trail.Clear();
                return false;
            }
            case WmMouseMove when _eDown:
            {
                _trail.Add(e.X, e.Y, e.Time / 1000.0);
                if (WindowFrame.Visible(_eTarget) is not { } now) return false;
                if (EdgeSnapSupport.Classify(_eOriginal, now, e.X - _eStartX, e.Y - _eStartY) != EdgeSnapSupport.DragKind.Moving) return false;
                if (_restoreOnDrag && !_eRestored)
                {
                    _eRestored = true;
                    RestoreSizeOnDrag(now, e);
                }
                var screens = _screens;
                var hit = EdgeSnapSupport.ZoneAt(new PointD(e.X, e.Y), screens, _disabledZones);
                if (hit is { } h && !EdgeSnapSupport.AllowsSnap(HasNeighbour(h, screens, e), SpeedAcross(h))) hit = null;
                if (hit != _eHit)
                {
                    _eHit = hit;
                    UpdatePreview(hit, screens);
                }
                return false;
            }
            case WmLButtonUp when _eDown:
            {
                _eDown = false;
                var hit = _eHit;
                _eHit = null;
                if (hit is { } h)
                {
                    var target = _eTarget;
                    var original = _eOriginal;
                    var screens = _screens;
                    var action = EdgeSnapSupport.ActionFor(h, _zoneActions);
                    var work = screens[h.Screen].Visible;
                    _dispatcher.BeginInvoke(async () =>
                    {
                        HidePreview();
                        // The system's own drag ends just after this event; the layout goes on top of it.
                        await Task.Delay(40);
                        await ApplyCoreAsync(target, action, fromPointer: true, work: work);
                    });
                }
                else HidePreviewSoon();
                return false;
            }
        }
        return false;
    }

    private double SpeedAcross(SnapHit hit)
    {
        var v = _trail.Velocity(Environment.TickCount64 / 1000.0);
        return hit.Zone is EdgeSnapZone.Left or EdgeSnapZone.Right ? Math.Abs(v.X) : hit.Zone is EdgeSnapZone.Top or EdgeSnapZone.Bottom ? Math.Abs(v.Y) : Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
    }

    /// <summary>Whether another display lies just beyond the edge the pointer is at.</summary>
    private static bool HasNeighbour(SnapHit hit, IReadOnlyList<SnapScreen> screens, in MouseEvent e)
    {
        var f = screens[hit.Screen].Frame;
        var probe = hit.Zone switch
        {
            EdgeSnapZone.Left or EdgeSnapZone.TopLeft or EdgeSnapZone.BottomLeft => new PointD(f.X - 6, e.Y),
            EdgeSnapZone.Right or EdgeSnapZone.TopRight or EdgeSnapZone.BottomRight => new PointD(f.Right + 6, e.Y),
            EdgeSnapZone.Top => new PointD(e.X, f.Y - 6),
            _ => new PointD(e.X, f.Bottom + 6),
        };
        return screens.Where((_, i) => i != hit.Screen).Any(s => s.Frame.Contains(probe));
    }

    private void RestoreSizeOnDrag(WindowRect now, in MouseEvent e)
    {
        var key = WindowFrame.Key(_eTarget);
        if (!_restoreSizes.TryGetValue(key, out var size) || !EdgeSnapSupport.NeedsSizeRestore(now, size)) return;
        var screens = _screens;
        var point = new PointD(e.X, e.Y);
        var screen = screens.Select(s => (WindowRect?)s.Frame).FirstOrDefault(f => f!.Value.Contains(point));
        var restored = EdgeSnapSupport.RestoredFrame(now, size, new PointD(_eStartX, _eStartY), point, screen);
        _worker.Post(_eTarget, restored, moveOnly: false);
    }

    private void UpdatePreview(SnapHit? hit, IReadOnlyList<SnapScreen> screens)
    {
        if (hit is not { } h) { HidePreview(); return; }
        var action = EdgeSnapSupport.ActionFor(h, _zoneActions);
        var v = screens[h.Screen].Visible;
        var f = EdgeSnapSupport.PreviewFractions(action);
        var rect = new WindowRect(v.X + f.X * v.Width, v.Y + f.Y * v.Height, f.Width * v.Width, f.Height * v.Height);
        _dispatcher.BeginInvoke(() => (_preview ??= new SnapPreview()).Show(rect));
    }

    private void HidePreview() => _dispatcher.BeginInvoke(() => _preview?.Hide());
    private void HidePreviewSoon() => _dispatcher.BeginInvoke(() => _preview?.Hide());

    // MARK: The directional ring

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn || _ringShortcut is not { } shortcut || _app.InputGate.ShouldPassThrough && !_ringActive) return false;
        if (e.VirtualKey == shortcut.VirtualKey)
        {
            if (e.IsDown)
            {
                if (_ringActive) return true;
                if (!KeyState.HeldExactly(ModifierNames(shortcut))) return false;
                GetCursorPos(out var p);
                _ringOrigin = new PointD(p.X, p.Y);
                _ringTarget = WindowFrame.Front();
                _ringAction = null;
                _ringActive = _ringTarget != IntPtr.Zero;
                return _ringActive;
            }
            if (_ringActive) { FinishRing(apply: true); return true; }
        }
        if (_ringActive && e.IsDown && e.VirtualKey == 0x1B) { FinishRing(apply: false); return true; }
        // Letting go of a modifier of the chord ends the ring too.
        if (_ringActive && !e.IsDown && e.VirtualKey is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C)
            FinishRing(apply: true);
        return false;
    }

    private static IEnumerable<string> ModifierNames(Shortcut s)
    {
        if (s.Modifiers.HasFlag(ShortcutModifiers.Control)) yield return "control";
        if (s.Modifiers.HasFlag(ShortcutModifiers.Alt)) yield return "alt";
        if (s.Modifiers.HasFlag(ShortcutModifiers.Shift)) yield return "shift";
        if (s.Modifiers.HasFlag(ShortcutModifiers.Win)) yield return "win";
    }

    private void Ring(in MouseEvent e)
    {
        var point = new PointD(e.X, e.Y);
        var action = WindowGestureSupport.RingAction(_ringOrigin, point);
        if (action == _ringAction) return;
        _ringAction = action;
        if (action is not { } a) { HidePreview(); return; }
        var screens = _screens;
        var at = screens.FirstOrDefault(s => s.Frame.Contains(point));
        var work = at.Visible.Width > 0 ? at.Visible : screens.FirstOrDefault().Visible;
        var (windowGap, screenGap, margin) = Gaps();
        var rect = WindowLayoutSupport.Rect(a, new WindowRect(0, 0, 0, 0), work, windowGap, screenGap, margin);
        _dispatcher.BeginInvoke(() => (_preview ??= new SnapPreview()).Show(rect));
    }

    private void FinishRing(bool apply)
    {
        _ringActive = false;
        var action = _ringAction;
        var target = _ringTarget;
        _ringAction = null;
        GetCursorPos(out var p);
        HidePreview();
        if (!apply || action is not { } a || target == IntPtr.Zero) return;
        var screens = _screens;
        var at = screens.FirstOrDefault(s => s.Frame.Contains(new PointD(p.X, p.Y)));
        var work = at.Visible.Width > 0 ? at.Visible : (WindowRect?)null;
        _dispatcher.BeginInvoke(() => _ = ApplyCoreAsync(target, a, fromPointer: true, work: work));
    }
}
