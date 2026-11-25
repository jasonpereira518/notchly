// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/WindowLayout/WindowLayoutService.swift (shortcuts and applying), with
// PointerDisplayService. A shortcut places the window in front by one of the layouts in Core; the frames it had before are kept
// for Restore; a window an app will not size as asked stays against the edge the layout meant; and pressing a direction again goes
// on to the next display or the next width. The pointer handlers (gesture, edge snap, directional ring) are in WindowPointerHandlers.cs.
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;
using Serilog;
using static Notchly.App.Platform.Native;
using static Notchly.Core.Windowing.WindowLayoutAction;

namespace Notchly.App.Layout;

public sealed partial class WindowLayoutService : IFeatureService
{
    private readonly App _app;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _hotkeys = [];
    private readonly WindowLayoutSupport.History _history = new();
    private readonly Dictionary<string, (WindowLayoutAction Action, WindowRect Settled)> _last = [];
    private readonly Dictionary<string, SizeD> _restoreSizes = [];
    private readonly PlacementWorker _worker = new();
    private string? _registered;
    private bool _disposed;

    /// <summary>Shortcuts that another program already owns, for the settings page to say so.</summary>
    public IReadOnlyList<string> TakenShortcuts { get; private set; } = [];

    public WindowLayoutService(App app)
    {
        _app = app;
        _settings = app.Settings;
        _dispatcher = app.Dispatcher;
        SystemEvents.DisplaySettingsChanged += OnDisplays;
        RefreshScreens();
    }

    public void Sync()
    {
        RegisterShortcuts();
        SyncPointerHandlers();
    }

    // MARK: Shortcuts

    private string Signature() => string.Join("|", Enum.GetValues<WindowLayoutAction>().Select(a => _settings.GetString(WindowLayoutSupport.ShortcutKey(a))))
        + "|" + _settings.GetBool(DefaultsKey.WindowLayoutShortcutsEnabled) + "|" + _settings.GetString(DefaultsKey.WindowLayoutHiddenActions)
        + "|" + _settings.GetBool(DefaultsKey.PointerDisplayEnabled) + "|" + _settings.GetString(DefaultsKey.PointerDisplayShortcut);

    private void RegisterShortcuts()
    {
        var signature = Signature();
        if (signature == _registered) return;
        foreach (var registration in _hotkeys) registration.Dispose();
        _hotkeys.Clear();
        _registered = signature;
        var taken = new List<string>();
        if (_settings.GetBool(DefaultsKey.WindowLayoutShortcutsEnabled))
        {
            var hidden = _settings.GetList(DefaultsKey.WindowLayoutHiddenActions).ToHashSet();
            foreach (var action in Enum.GetValues<WindowLayoutAction>())
            {
                if (hidden.Contains(WindowLayoutSupport.RawName(action)) || Shortcut.Parse(_settings.GetString(WindowLayoutSupport.ShortcutKey(action))) is not { } shortcut) continue;
                var captured = action;
                var handle = _app.Hotkeys.Register(shortcut, () => _ = ApplyAsync(captured));
                if (handle is null) taken.Add(WindowLayoutSupport.RawName(action)); else _hotkeys.Add(handle);
            }
        }
        if (_settings.GetBool(DefaultsKey.PointerDisplayEnabled) && Shortcut.Parse(_settings.GetString(DefaultsKey.PointerDisplayShortcut)) is { } pointer)
        {
            var handle = _app.Hotkeys.Register(pointer, MovePointerToNextDisplay);
            if (handle is null) taken.Add("pointerDisplay"); else _hotkeys.Add(handle);
        }
        TakenShortcuts = taken;
    }

    // MARK: Applying a layout

    private bool Ignored(IntPtr hwnd) => ScrollSupport.IsExcepted(WindowFrame.ProgramOf(hwnd), _settings.GetList(DefaultsKey.WindowLayoutIgnoredApps));

    private (double WindowGap, double ScreenGap, double Margin) Gaps() =>
        (WindowLayoutSupport.SanitizedGap(_settings.GetInt(DefaultsKey.WindowLayoutWindowGap)),
         WindowLayoutSupport.SanitizedGap(_settings.GetInt(DefaultsKey.WindowLayoutScreenGap)),
         WindowLayoutSupport.SanitizedMargin(_settings.GetDouble(DefaultsKey.WindowLayoutMarginPercent, 5)));

    private static DisplayDirection? DirectionOf(WindowLayoutAction a) => a switch
    {
        LeftHalf => DisplayDirection.Left, RightHalf => DisplayDirection.Right, TopHalf => DisplayDirection.Up, BottomHalf => DisplayDirection.Down, _ => null,
    };

    /// <summary>Applies a layout to the window in front. Fire and forget: it settles the window a moment after placing it.</summary>
    public async Task ApplyAsync(WindowLayoutAction action)
    {
        try { await ApplyCoreAsync(FrontWindow(), action); }
        catch (Exception e) { Log.Warning(e, "Layout {Action} failed", action); }
    }

    private IntPtr FrontWindow() => WindowFrame.Front();

    private async Task ApplyCoreAsync(IntPtr hwnd, WindowLayoutAction action, bool fromPointer = false, WindowRect? work = null)
    {
        if (hwnd == IntPtr.Zero || Ignored(hwnd)) return;
        var key = WindowFrame.Key(hwnd);
        if (WindowFrame.Visible(hwnd) is not { } current) return;
        var displays = Displays.All();
        var works = displays.Select(WindowFrame.WorkArea).ToList();
        var here = WindowFrame.DisplayOf(hwnd);
        var index = here is null ? 0 : Math.Max(0, displays.ToList().FindIndex(d => d.Handle == here.Handle));
        if (index >= works.Count) return;
        var (windowGap, screenGap, margin) = Gaps();

        if (action == Restore)
        {
            if (_history.PopPrevious(key, current) is { } previous) WindowFrame.Place(hwnd, previous);
            return;
        }
        if (action == FullScreen) { WindowFrame.Maximize(hwnd); return; }

        WindowLayoutAction applied = action;
        WindowRect target;
        var from = work ?? works[index];
        if (action is NextDisplay or PreviousDisplay)
        {
            if (WindowLayoutSupport.AdjacentIndex(works, index, action == NextDisplay) is not { } other) return;
            target = WindowLayoutSupport.RectForDisplay(current, from, works[other]);
            from = works[other];
        }
        else if (DirectionOf(action) is { } direction && !fromPointer)
        {
            var step = DirectionalStep(key, direction, current, works, index);
            if (step.CrossTo is { } cross)
            {
                if (WindowLayoutSupport.NeighbourIndex(works, index, cross) is not { } neighbour) return;
                applied = WindowLayoutSupport.OppositeHalf(cross);
                from = works[neighbour];
                target = WindowLayoutSupport.Rect(applied, current, from, windowGap, screenGap, margin);
            }
            else
            {
                applied = step.Action ?? action;
                target = WindowLayoutSupport.Rect(applied, current, from, windowGap, screenGap, margin);
            }
        }
        else target = WindowLayoutSupport.Rect(action, current, from, windowGap, screenGap, margin);

        // What the window looked like before this run of layouts is what dragging it away gives back.
        if (!_last.TryGetValue(key, out var last) || !current.NearlyEquals(last.Settled, WindowLayoutSupport.FrameTolerance))
            _restoreSizes[key] = new SizeD(current.Width, current.Height);
        _history.Push(key, current);
        WindowFrame.Place(hwnd, target);
        await SettleAsync(hwnd, key, applied, target, from);
    }

    private WindowLayoutSupport.DirectionalStep DirectionalStep(string key, DisplayDirection direction, WindowRect current, IReadOnlyList<WindowRect> works, int index)
    {
        _last.TryGetValue(key, out var last);
        var stillThere = _last.ContainsKey(key) && current.NearlyEquals(last.Settled, WindowLayoutSupport.FrameTolerance);
        var neighbour = WindowLayoutSupport.NeighbourIndex(works, index, direction) is not null;
        return WindowLayoutSupport.NextDirectional(direction, _last.ContainsKey(key) ? last.Action : null, stillThere,
            _settings.GetBool(DefaultsKey.WindowLayoutSideRepeatCyclesThirds), neighbour);
    }

    /// <summary>
    /// A moment after placing, looks at what the app made of it. An app that moved to a display with another scale is told again; one
    /// that keeps a larger size stays against the edge the layout meant; one that ended up somewhere else forgets the saved frame.
    /// </summary>
    private async Task SettleAsync(IntPtr hwnd, string key, WindowLayoutAction action, WindowRect target, WindowRect work)
    {
        await Task.Delay(90);
        if (WindowFrame.Visible(hwnd) is not { } actual) return;
        if (!actual.NearlyEquals(target, WindowLayoutSupport.FrameTolerance))
        {
            WindowFrame.Place(hwnd, target);
            await Task.Delay(90);
            actual = WindowFrame.Visible(hwnd) ?? actual;
        }
        if (!actual.NearlyEquals(target, WindowLayoutSupport.FrameTolerance))
        {
            var anchored = WindowLayoutSupport.Anchored(action, target, work, actual.Width, actual.Height);
            WindowFrame.Place(hwnd, anchored, moveOnly: true);
            await Task.Delay(40);
            actual = WindowFrame.Visible(hwnd) ?? actual;
            if (!WindowLayoutSupport.Accepts(action, target, actual)) { _history.DiscardLatest(key); return; }
        }
        _last[key] = (action, actual);
        if (_last.Count > 64) foreach (var old in _last.Keys.Take(_last.Count - 64).ToList()) _last.Remove(old);
    }

    // MARK: Another display's pointer

    private void MovePointerToNextDisplay()
    {
        var front = GetForegroundWindow();
        if (front != IntPtr.Zero && Ignored(front)) return;
        var displays = Displays.All();
        if (displays.Count < 2 || !GetCursorPos(out var p)) return;
        var bounds = displays.Select(WindowFrame.Bounds).ToList();
        var here = displays.ToList().FindIndex(d => d.Bounds.Left <= p.X && p.X < d.Bounds.Right && d.Bounds.Top <= p.Y && p.Y < d.Bounds.Bottom);
        if (here < 0 || WindowLayoutSupport.AdjacentIndex(bounds, here, forward: true) is not { } next) return;
        WindowFrame.MoveCursorTo((int)bounds[next].MidX, (int)bounds[next].MidY);
    }

    // MARK: Displays

    private void OnDisplays(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RefreshScreens);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplays;
        foreach (var registration in _hotkeys) registration.Dispose();
        _hotkeys.Clear();
        DisposePointerHandlers();
        _worker.Dispose();
    }
}
