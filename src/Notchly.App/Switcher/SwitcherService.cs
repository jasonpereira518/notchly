// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Switcher/AppSwitcher.swift. A keyboard hook takes the switcher's shortcuts (Alt+Tab
// when the take-over option is on): the Tab is swallowed so Windows' own switcher never opens, the panel appears after a short
// delay, Tab and the arrows move, typing searches, W closes the window, Q quits its app, and letting go of the modifier switches
// to the chosen window. The hook thread only decides what to swallow and hands everything else to the UI thread; the session lives
// there. A dummy key is sent as the session starts so releasing Alt does not open an app's menu bar, as AutoHotkey does.
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Switcher;

public sealed class SwitcherService : IFeatureService
{
    private sealed record Trigger(Shortcut Shortcut, SwitcherScope Scope, IReadOnlyList<string> Modifiers);

    private readonly App _app;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _keySub, _mouseSub;
    private WindowUseTracker? _tracker;
    private volatile Trigger[] _triggers = [];

    // The hook thread's view of the session.
    private volatile bool _active, _pinned;
    private Trigger? _current;

    // The session, on the UI thread.
    private List<SwitchWindow> _all = [];
    private List<SwitchWindow> _shown = [];
    private int _selected, _pendingSteps;
    private string _query = "";
    private readonly HashSet<long> _closing = [];
    private SwitcherOverlay? _overlay;
    private DispatcherTimer? _delay;
    private bool _sessionOpen, _committedEarly;
    private RECT _display;
    private volatile int _overlayLeft, _overlayTop, _overlayRight, _overlayBottom;
    private readonly SwitcherSupport.WheelNavigator _wheel = new();
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public SwitcherService(App app)
    {
        _app = app;
        _settings = app.Settings;
        _dispatcher = app.Dispatcher;
    }

    // MARK: Lifecycle

    public void Sync()
    {
        var on = _settings.GetBool(DefaultsKey.SwitcherEnabled);
        if (!on) { Stop(); return; }
        _tracker ??= new WindowUseTracker();
        _triggers = BuildTriggers();
        _keySub ??= _app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Protect);
    }

    private void Stop()
    {
        _keySub?.Dispose();
        _keySub = null;
        _triggers = [];
        _tracker?.Dispose();
        _tracker = null;
        if (_sessionOpen) _dispatcher.BeginInvoke(() => EndSession());
        _active = false;
    }

    private Trigger[] BuildTriggers()
    {
        var list = new List<Trigger>();
        var takeOver = _settings.GetBool(DefaultsKey.SwitcherTakeOverSystemShortcuts);
        void Add(string key, SwitcherScope scope, bool enabled = true)
        {
            if (!enabled || Shortcut.Parse(_settings.GetString(key)) is not { } shortcut) return;
            // Alt+Tab belongs to Windows unless the take-over is on.
            if (!takeOver && shortcut.VirtualKey == 0x09 && shortcut.Modifiers == ShortcutModifiers.Alt) return;
            var mods = new List<string>();
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) mods.Add("control");
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) mods.Add("alt");
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) mods.Add("shift");
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Win)) mods.Add("win");
            list.Add(new Trigger(shortcut, scope, mods));
        }
        Add(DefaultsKey.SwitcherShortcut, SwitcherScope.AllApps);
        Add(DefaultsKey.SwitcherWindowShortcut, SwitcherScope.FrontmostApp);
        Add(DefaultsKey.SwitcherAllWindowsShortcut, SwitcherScope.AllWindows, _settings.GetBool(DefaultsKey.SwitcherAllWindowsShortcutEnabled));
        return [.. list];
    }

    // MARK: The keyboard hook

    private static string? ModifierName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "shift",
        0x11 or 0xA2 or 0xA3 => "control",
        0x12 or 0xA4 or 0xA5 => "alt",
        0x5B or 0x5C => "win",
        _ => null,
    };

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn) return false;
        return _active ? OnActiveKey(e) : OnIdleKey(e);
    }

    private bool OnIdleKey(in KeyEvent e)
    {
        if (!e.IsDown) return false;
        var triggers = _triggers;
        foreach (var t in triggers)
        {
            if (e.VirtualKey != t.Shortcut.VirtualKey) continue;
            var held = KeyState.HeldModifiers();
            // A Shift that is not part of the shortcut goes backwards.
            var reverse = held.Contains("shift") && !t.Modifiers.Contains("shift");
            if (reverse) held.Remove("shift");
            if (!held.SetEquals(t.Modifiers)) continue;
            if (_app.InputGate.ShouldPassThrough) return false;
            _current = t;
            _pinned = false;
            _active = true;
            // A key between the Alt press and its release keeps the foreground app from opening its menu bar.
            _app.Injector.Key(0xE8, true, InputSource.Switcher);
            _app.Injector.Key(0xE8, false, InputSource.Switcher);
            var scope = t.Scope;
            _dispatcher.BeginInvoke(() => StartSession(scope, reverse));
            return true;
        }
        return false;
    }

    private bool OnActiveKey(in KeyEvent e)
    {
        var t = _current;
        if (t is null) { _active = false; return false; }
        var vk = e.VirtualKey;
        if (ModifierName(vk) is { } modifier)
        {
            if (!e.IsDown && t.Modifiers.Contains(modifier) && !_pinned)
            {
                _active = false;
                _dispatcher.BeginInvoke(() => Commit());
            }
            return false;
        }
        // Everything else belongs to the switcher while it is up; key-ups of what it swallowed are swallowed too.
        if (!e.IsDown) return true;
        var shift = KeyState.IsDown(0x10);
        if (vk == t.Shortcut.VirtualKey || vk == 0x09) { var step = shift ? -1 : 1; _dispatcher.BeginInvoke(() => Step(step)); return true; }
        switch (vk)
        {
            case 0x1B: _active = false; _dispatcher.BeginInvoke(() => Cancel()); return true;
            case 0x0D: _active = false; _dispatcher.BeginInvoke(() => Commit()); return true;
            case 0x25: _dispatcher.BeginInvoke(() => Move(-1, 0)); return true;
            case 0x27: _dispatcher.BeginInvoke(() => Move(1, 0)); return true;
            case 0x26: _dispatcher.BeginInvoke(() => Move(0, -1)); return true;
            case 0x28: _dispatcher.BeginInvoke(() => Move(0, 1)); return true;
            case 0x2E: _dispatcher.BeginInvoke(() => CloseSelected(quitApp: false)); return true;
            case 0x08: _dispatcher.BeginInvoke(() => Search(null, vk)); return true;
        }
        var text = KeyText.Of(vk, e.ScanCode, shift);
        _dispatcher.BeginInvoke(() => Search(text, vk));
        return true;
    }

    private bool OnMouse(in MouseEvent e)
    {
        if (!_active || e.IsOwn) return false;
        if (e.Message == 0x020A)
        {
            var steps = _wheel.Add(e.WheelDelta, e.Time);
            if (steps != 0) _dispatcher.BeginInvoke(() => Step(-steps));
            return true;
        }
        if (e.Message == 0x0201 && _overlayRight > _overlayLeft && (e.X < _overlayLeft || e.X > _overlayRight || e.Y < _overlayTop || e.Y > _overlayBottom))
        {
            _active = false;
            _dispatcher.BeginInvoke(() => Cancel());
        }
        return false;
    }

    // MARK: Starting

    private void StartSession(SwitcherScope scope, bool reverse)
    {
        EndSession();
        _sessionOpen = true;
        _committedEarly = false;
        _query = "";
        _closing.Clear();
        _pendingSteps = 0;
        _mouseSub ??= _app.Input.SubscribeMouse(OnMouse, InputHookService.Priority.Protect);
        var foreground = WindowInventory.Foreground();
        var windows = WindowInventory.List(_settings.GetBool(DefaultsKey.SwitcherCurrentSpaceOnly)).ToList();
        var front = windows.FirstOrDefault(w => w.Window == foreground);
        var frontPid = front?.Pid ?? 0;
        if (scope == SwitcherScope.FrontmostApp) windows = windows.Where(w => w.Pid == frontPid).ToList();
        if (_settings.GetBool(DefaultsKey.SwitcherCurrentDisplayOnly) && front is not null) windows = windows.Where(w => w.Monitor == front.Monitor).ToList();

        // Order by recent use, then apply the filters.
        var indexed = windows.Select((w, z) => (w, z)).ToList();
        var history = (_tracker?.History ?? []).Select(h => indexed.FirstOrDefault(p => p.w.Window.ToInt64() == h) is { w: not null } p ? p.z : -1).Where(i => i >= 0).ToList();
        var order = SwitcherSupport.Order(indexed.Select(p => (p.z, p.w.Pid)).ToList(), history, _tracker?.AppRank ?? []);
        var entries = order.Select(z => indexed[z]).Select(p => new SwitcherEntry(p.z, p.w.Pid, p.w.AppId, p.w.AppName, p.w.Title, p.w.IsMinimized, false, false, true, p.w.Window == foreground)).ToList();
        var placement = _settings.GetString(DefaultsKey.SwitcherMinimizedPlacement) switch { "end" => MinimizedPlacement.End, "hidden" => MinimizedPlacement.Hidden, _ => MinimizedPlacement.Normal };
        IReadOnlyList<SwitcherEntry> filtered = SwitcherSupport.PlaceMinimized(entries, placement, _settings.GetBool(DefaultsKey.SwitcherTreatHiddenAppsLikeMinimized));
        filtered = SwitcherSupport.ApplyAppRules(filtered, ParseRules(_settings.GetString(DefaultsKey.SwitcherAppRules)));
        if (_settings.GetBool(DefaultsKey.SwitcherMergeTabs) && scope == SwitcherScope.AllApps)
            filtered = filtered.GroupBy(e => e.AppId).Select(g => g.First()).ToList();
        var keep = SwitcherSupport.VisibleSelectionIndices(filtered.Select(e => e.Pid).ToList(), SwitcherSupport.MaxEntries);
        _all = keep.Select(i => indexed[filtered[i].Id].w).ToList();
        _shown = [.. _all];
        _selected = _all.Count == 0 ? 0 : SwitcherSupport.InitialSelection(_all.Select(w => w.Pid).ToList(),
            _all.Count > 0 && _all[0].Window == foreground, frontPid, reverse);
        // Presses that came before the session was ready.
        if (_pendingSteps != 0 && _shown.Count > 0) _selected = ((_selected + _pendingSteps) % _shown.Count + _shown.Count) % _shown.Count;
        var delay = SwitcherSupport.SanitizedAppearanceDelay(_settings.GetInt(DefaultsKey.SwitcherAppearanceDelay, 100));
        if (_shown.Count == 0) { EndSession(); _active = false; return; }
        if (delay == 0) _ = ShowOverlayAsync();
        else
        {
            _delay = new DispatcherTimer(DispatcherPriority.Send, _dispatcher) { Interval = TimeSpan.FromMilliseconds(delay) };
            _delay.Tick += (_, _) => { _delay.Stop(); _ = ShowOverlayAsync(); };
            _delay.Start();
        }
    }

    private static IReadOnlyDictionary<string, AppRule> ParseRules(string stored)
    {
        var map = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length == 2 && Enum.TryParse<AppRule>(pair[1], true, out var rule)) map[pair[0]] = rule;
        }
        return map;
    }

    // MARK: Showing

    private async Task ShowOverlayAsync()
    {
        if (!_sessionOpen || _committedEarly) return;
        var cards = new List<SwitcherCard>();
        foreach (var w in _shown) cards.Add(new SwitcherCard(w, await IconFor(w)));
        if (!_sessionOpen || _committedEarly) return;
        var display = ChooseDisplay();
        _display = display.Bounds;
        var scale = display.Scale <= 0 ? 1 : display.Scale;
        var size = _settings.GetString(DefaultsKey.SwitcherPreviewSize);
        var cardWidth = 288 * (size == "small" ? 0.78 : size == "large" ? 1.22 : 1.0) + 10;
        var fit = Math.Clamp((int)Math.Floor(display.Bounds.Width / scale * 0.9 / cardWidth), 2, 10);
        var userMax = SwitcherSupport.SanitizedMaxColumns(_settings.GetInt(DefaultsKey.SwitcherMaxColumns));
        var iconRow = _settings.GetBool(DefaultsKey.SwitcherIconRowMode) || _settings.GetBool(DefaultsKey.SwitcherSimpleMode);
        var columns = SwitcherSupport.GridColumnCount(_shown.Count, iconRow ? Math.Clamp((int)(display.Bounds.Width / scale * 0.9 / 94), 4, 14) : Math.Min(fit, userMax ?? fit));
        if (_overlay is null)
        {
            _overlay = new SwitcherOverlay();
            _overlay.CardClicked += OnCardClicked;
            _overlay.CardClosed += i => { if (i >= 0 && i < _shown.Count) { _selected = i; CloseSelected(false); } };
            _overlay.CardHovered += i => { if (!_settings.GetBool(DefaultsKey.SwitcherIgnoreHoverSelection) && i >= 0 && i < _shown.Count) { _selected = i; _overlay?.Select(i); } };
            _overlay.ContentRendered += (_, _) => PublishOverlayBounds();
        }
        _overlay.SetQuery(_query);
        _overlay.ShowCards(cards, _selected, columns, iconRow, size, display.Bounds, _settings.GetBool(DefaultsKey.SwitcherShowShortcutHints));
        _overlay.Select(_selected);
    }

    private void PublishOverlayBounds()
    {
        if (_overlay is null || !GetWindowRect(new System.Windows.Interop.WindowInteropHelper(_overlay).Handle, out var r)) return;
        _overlayLeft = r.Left; _overlayTop = r.Top; _overlayRight = r.Right; _overlayBottom = r.Bottom;
    }

    private DisplayInfo ChooseDisplay()
    {
        var displays = Displays.All();
        var placement = _settings.GetString(DefaultsKey.SwitcherScreenPlacement);
        DisplayInfo? chosen = placement switch
        {
            "menuBar" => displays.FirstOrDefault(d => d.IsPrimary),
            "activeWindow" => WindowFrame.DisplayOf(WindowInventory.Foreground()),
            _ => GetCursorPos(out var p) ? Displays.AtPoint(p.X, p.Y) : null,
        };
        return chosen ?? displays.First();
    }

    private async Task<System.Windows.Media.ImageSource?> IconFor(SwitchWindow w)
    {
        if (w.ExePath is null) return null;
        if (_icons.TryGetValue(w.ExePath, out var cached)) return cached;
        return _icons[w.ExePath] = await ShellThumbnails.GetAsync(w.ExePath, 64);
    }

    // MARK: Moving

    private void Step(int direction)
    {
        if (!_sessionOpen) { _pendingSteps += direction; return; }
        if (_shown.Count == 0) return;
        _selected = ((_selected + direction) % _shown.Count + _shown.Count) % _shown.Count;
        ShowNowIfHidden();
        _overlay?.Select(_selected);
    }

    private void Move(int dx, int dy)
    {
        if (_shown.Count == 0) return;
        var columns = Math.Max(1, SwitcherSupport.GridColumnCount(_shown.Count, SwitcherSupport.SanitizedMaxColumns(_settings.GetInt(DefaultsKey.SwitcherMaxColumns)) ?? 10));
        _selected = Math.Clamp(_selected + dx + dy * columns, 0, _shown.Count - 1);
        ShowNowIfHidden();
        _overlay?.Select(_selected);
    }

    /// <summary>Using a key that needs the panel (arrows, typing) shows it without waiting for the delay.</summary>
    private void ShowNowIfHidden()
    {
        if (_overlay is null && _sessionOpen && !_committedEarly)
        {
            _delay?.Stop();
            _ = ShowOverlayAsync();
        }
    }

    // MARK: Searching and closing

    private void Search(string? text, int virtualKey)
    {
        if (!_sessionOpen) return;
        if (text is null) { if (_query.Length > 0) _query = _query[..^1]; }
        else if (_query.Length == 0 && SwitcherSupport.LetterAction(virtualKey, _settings.GetBool(DefaultsKey.SwitcherSearchPinEnabled)) is var letter and not SwitcherLetter.None)
        {
            switch (letter)
            {
                case SwitcherLetter.CloseWindow: CloseSelected(false); return;
                case SwitcherLetter.QuitApp: CloseSelected(true); return;
                case SwitcherLetter.PinSearch: _pinned = true; ShowNowIfHidden(); return;
            }
        }
        else if (text is not null) _query += text;
        var previous = _shown.Count > 0 && _selected < _shown.Count ? _shown[_selected].Window : IntPtr.Zero;
        _shown = _all.Where(w => SwitcherSupport.Matches(_query, w.Title, w.AppName)).ToList();
        var at = _shown.FindIndex(w => w.Window == previous);
        _selected = at >= 0 ? at : Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));
        ShowNowIfHidden();
        if (_overlay is not null) _ = ShowOverlayAsync();
    }

    private void CloseSelected(bool quitApp)
    {
        if (!_sessionOpen || _shown.Count == 0) return;
        var target = _shown[Math.Clamp(_selected, 0, _shown.Count - 1)];
        var windows = quitApp ? _all.Where(w => w.Pid == target.Pid).ToList() : [target];
        foreach (var w in windows) { PostMessage(w.Window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); _closing.Add(w.Window.ToInt64()); }
        ShowNowIfHidden();
        var tries = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(SwitcherSupport.CloseRemovalDelay) };
        timer.Tick += (_, _) =>
        {
            tries++;
            var gone = windows.Where(w => !IsWindow(w.Window)).ToList();
            foreach (var w in gone) Remove(w);
            windows = windows.Except(gone).ToList();
            if (windows.Count == 0 || tries >= 8)
            {
                timer.Stop();
                foreach (var w in windows) _closing.Remove(w.Window.ToInt64());
            }
        };
        timer.Start();
    }

    private void Remove(SwitchWindow window)
    {
        if (!_sessionOpen) return;
        var ids = _shown.Select(w => (int)(w.Window.ToInt64() & 0x7FFFFFFF)).ToList();
        var result = SwitcherSupport.CloseState(ids, _selected, (int)(window.Window.ToInt64() & 0x7FFFFFFF));
        _all.Remove(window);
        _closing.Remove(window.Window.ToInt64());
        if (result.SessionEnds || _all.Count == 0) { _active = false; Cancel(); return; }
        _shown = _all.Where(w => SwitcherSupport.Matches(_query, w.Title, w.AppName)).ToList();
        _selected = Math.Clamp(result.Selected, 0, Math.Max(0, _shown.Count - 1));
        if (_overlay is not null) _ = ShowOverlayAsync();
    }

    // MARK: Ending

    private void OnCardClicked(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        _selected = index;
        _active = false;
        Commit();
    }

    private void Commit()
    {
        if (!_sessionOpen) return;
        var ids = _shown.Select(w => (int)(w.Window.ToInt64() & 0x7FFFFFFF)).ToList();
        var closing = _closing.Select(c => (int)(c & 0x7FFFFFFF)).ToHashSet();
        var chosen = SwitcherSupport.CommitTarget(ids, _selected, closing);
        var target = chosen is { } id ? _shown.FirstOrDefault(w => (int)(w.Window.ToInt64() & 0x7FFFFFFF) == id) : null;
        EndSession();
        if (target is not null) WindowInventory.Activate(target.Window);
    }

    private void Cancel() => EndSession();

    private void EndSession()
    {
        _delay?.Stop();
        _delay = null;
        _committedEarly = true;
        _sessionOpen = false;
        _active = false;
        _pinned = false;
        _mouseSub?.Dispose();
        _mouseSub = null;
        _overlayLeft = _overlayRight = _overlayTop = _overlayBottom = 0;
        _overlay?.Dismiss();
        _overlay = null;
        _all = [];
        _shown = [];
        _closing.Clear();
    }

    public void Dispose() => Stop();
}
