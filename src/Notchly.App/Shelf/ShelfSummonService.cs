// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the summoning side of upstream Sources/Vorssaint/Services/Shelf/ShelfService.swift (the hotkey, the shake, the docked drop zone
// and the screen-edge peek) and NotchSupport.routesShelf. When the island is there to show Files, every way of summoning the shelf opens the
// island on its Files page, as upstream does; otherwise the floating card appears. The edge peek always uses the card, since the island
// cannot peek. The shelf itself (items, drops, drag out) is the one the island uses.
using System.Windows.Threading;
using Notchly.App.Island;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shelf;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;
using Serilog;

namespace Notchly.App.ShelfCard;

public sealed class ShelfSummonService(App app) : IFeatureService
{
    private ShelfCardWindow? _card;
    private ShelfDropZoneWindow? _pill;
    private ShelfDragMonitor? _monitor;
    private IDisposable? _hotkey;
    private string? _hotkeyStorage;
    private DispatcherTimer? _watch, _grace;
    private bool _dragActive, _dockOpened, _busy;
    private ScreenSide _peeking;
    private readonly ShelfSummonSupport.DwellTracker _dockDwell = new(ShelfSummonSupport.DockDwell);
    private readonly ShelfSummonSupport.DwellTracker _edgeDwell = new(ShelfSummonSupport.EdgeDwell);

    private IslandService? Island => app.Features.Get<IslandService>(AppFeature.Notch);
    private bool IslandShowsFiles => Island is not null && app.Settings.GetBool(DefaultsKey.NotchShelf);

    public void Sync()
    {
        var s = app.Settings;
        var enabled = s.GetBool(DefaultsKey.ShelfEnabled);

        var storage = enabled && s.GetBool(DefaultsKey.ShelfShortcutEnabled) ? s.GetString(DefaultsKey.ShelfShortcut) : null;
        if (storage != _hotkeyStorage || storage is not null && _hotkey is null)
        {
            _hotkey?.Dispose();
            _hotkey = null;
            _hotkeyStorage = storage;
            if (storage is not null && Shortcut.Parse(storage) is { } shortcut) _hotkey = app.Hotkeys.Register(shortcut, () => _ = OnHotkeyAsync());
        }

        var shake = enabled && s.GetBool(DefaultsKey.ShelfShakeToOpen);
        var wanted = shake || enabled && (s.GetBool(DefaultsKey.ShelfDropZoneEnabled) || s.GetBool(DefaultsKey.ShelfEdgeDragEnabled));
        if (wanted && _monitor is null)
        {
            _monitor = new ShelfDragMonitor(app.Input, app.Dispatcher);
            _monitor.DragStarted += OnDragStarted;
            _monitor.DragEnded += OnDragEnded;
            _monitor.Shaken += OnShaken;
        }
        else if (!wanted && _monitor is not null)
        {
            StopDrag();
            _monitor.Dispose();
            _monitor = null;
        }
        if (_monitor is not null)
        {
            _monitor.ShakeEnabled = shake;
            _monitor.Exclusions = s.GetList(DefaultsKey.ShelfAutomaticExclusions);
        }
        if (!enabled) _card?.HideCard();
    }

    // MARK: Showing the shelf

    private DisplayInfo? PointerDisplay(out Native.POINT pointer)
    {
        Native.GetCursorPos(out pointer);
        return Displays.AtPoint(pointer.X, pointer.Y) ?? Displays.All().FirstOrDefault(d => d.IsPrimary);
    }

    /// <summary>The island's Files page when the island can show it, otherwise the card at the pointer.</summary>
    private void ShowShelf()
    {
        if (IslandShowsFiles && Island!.OpenPage(NotchModule.Files, takeFocus: false)) return;
        ShowCard();
    }

    private ShelfCardWindow Card()
    {
        if (_card is not null) return _card;
        _card = new ShelfCardWindow(app.Shelf, app.Settings);
        _card.DragEntered += () => { if (_card.IsEdgePeek) _card.Settle(); };
        _card.Hidden += () => { _peeking = ScreenSide.None; _edgeDwell.Reset(); };
        return _card;
    }

    private void ShowCard()
    {
        if (PointerDisplay(out var pointer) is not { } display) return;
        Card().ShowAtPointer(display, pointer.X, pointer.Y);
    }

    // MARK: The hotkey

    private async Task OnHotkeyAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (app.Settings.GetBool(DefaultsKey.ShelfShortcutAddsSelection) && await TryAddSelectionAsync()) return;
            Toggle();
        }
        catch (Exception e) { Log.Warning(e, "The shelf shortcut failed"); }
        finally { _busy = false; }
    }

    /// <summary>With File Explorer in front and files selected that are not on the shelf yet, the shortcut shelves them.</summary>
    private async Task<bool> TryAddSelectionAsync()
    {
        var front = WindowInventory.Foreground();
        if (front == IntPtr.Zero || !PasteImageSupport_IsFolderWindow(Native.ClassName(front))) return false;
        var selected = await Task.Run(() => OnSta(() => ExplorerFolders.SelectedPaths(front)));
        var shelved = app.Shelf.Store.Items.SelectMany(i => i.Leaves()).Select(l => l.Path).OfType<string>().ToList();
        if (ShelfSummonSupport.ShortcutAction(true, true, selected, shelved) != ShelfShortcutAction.AddSelection) return false;
        app.Shelf.AddFiles(ShelfSummonSupport.NewSelection(selected, shelved));
        ShowShelf();
        return true;
    }

    private static bool PasteImageSupport_IsFolderWindow(string className) => Notchly.Core.Clipboard.PasteImageSupport.IsFolderWindowClass(className);

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = work(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return result;
    }

    private void Toggle()
    {
        if (Island is { } island && IslandShowsFiles && island.IsOpenOn(NotchModule.Files)) { island.Collapse(); return; }
        if (_card is { IsShown: true }) { _card.HideCard(); return; }
        ShowShelf();
    }

    // MARK: Dragging

    private void OnShaken()
    {
        if (_card is { IsShown: true } || Island is { } island && island.IsOpenOn(NotchModule.Files)) return;
        ShowShelf();
    }

    private void OnDragStarted()
    {
        if (_monitor is null || !app.Settings.GetBool(DefaultsKey.ShelfEnabled)) return;
        _grace?.Stop();
        _dragActive = true;
        _dockOpened = false;
        _dockDwell.Reset();
        _edgeDwell.Reset();
        // Programs on the exclusion list never bring the shelf up by themselves.
        if (!ShelfSummonSupport.AutoOpenAllowed(_monitor.SourceProgram, app.Settings.GetList(DefaultsKey.ShelfAutomaticExclusions))) return;
        _watch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => OnWatch(), app.Dispatcher);
        _watch.Start();
        if (app.Settings.GetBool(DefaultsKey.ShelfDropZoneEnabled)) ShowPill();
    }

    private WindowRect? DockFrameNow(out DisplayInfo? display)
    {
        display = Displays.All().FirstOrDefault(d => d.IsPrimary);
        if (display is null) return null;
        var placement = ShelfSummonSupport.EffectivePlacement(ShelfSummonSupport.ParsePlacement(app.Settings.GetString(DefaultsKey.ShelfDockPlacement)), IslandShowsFiles);
        var work = new WindowRect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
        return ShelfSummonSupport.DockFrame(placement, work, display.Scale);
    }

    private void ShowPill()
    {
        if (DockFrameNow(out _) is not { } frame) return;
        _pill ??= new ShelfDropZoneWindow(app.Shelf);
        _pill.ShowAt(frame);
    }

    private void OnWatch()
    {
        if (!_dragActive) return;
        Native.GetCursorPos(out var p);
        var now = Environment.TickCount64 / 1000.0;

        if (_pill is { IsShown: true } && !_dockOpened && DockFrameNow(out var primary) is { } frame)
        {
            if (_dockDwell.Update(ShelfSummonSupport.InZone(frame, p.X, p.Y, ShelfSummonSupport.DockTriggerMargin), now))
            {
                _dockOpened = true;
                _pill.HidePill();
                ShowShelf();
            }
        }

        if (app.Settings.GetBool(DefaultsKey.ShelfEdgeDragEnabled) && Displays.AtPoint(p.X, p.Y) is { } display) WatchEdge(p, display, now);
    }

    private void WatchEdge(Native.POINT p, DisplayInfo display, double now)
    {
        if (_peeking == ScreenSide.None)
        {
            var all = Displays.All();
            var others = all.Where(d => d.Handle != display.Handle).Select(d => new WindowRect(d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height)).ToList();
            var bounds = new WindowRect(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height);
            var work = new WindowRect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height);
            var side = ShelfSummonSupport.EdgeSide(p.X, p.Y, bounds, work, others, ShelfSummonSupport.EdgeTriggerDistance * display.Scale);
            if (_edgeDwell.Update(side != ScreenSide.None, now) && side != ScreenSide.None)
            {
                _peeking = side;
                Card().ShowPeek(side, display);
            }
        }
        else if (_card is { IsEdgePeek: true })
        {
            var distance = _peeking == ScreenSide.Left ? p.X - display.Bounds.Left : display.Bounds.Right - p.X;
            if (distance > ShelfSummonSupport.EdgeRetreatDistance * display.Scale)
            {
                _card.HideCard();
                _peeking = ScreenSide.None;
                _edgeDwell.Reset();
            }
        }
    }

    private void OnDragEnded()
    {
        if (!_dragActive) return;
        // A drop lands a moment after the button comes up: the pill and the peek stay for it.
        _grace ??= new DispatcherTimer(TimeSpan.FromSeconds(ShelfSummonSupport.DragEndGrace), DispatcherPriority.Background, (_, _) => { _grace!.Stop(); StopDrag(); }, app.Dispatcher);
        _grace.Stop();
        _grace.Start();
    }

    private void StopDrag()
    {
        _dragActive = false;
        _watch?.Stop();
        _pill?.HidePill();
        if (_card is { IsEdgePeek: true }) _card.HideCard();
        _peeking = ScreenSide.None;
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        _monitor?.Dispose();
        _monitor = null;
        _watch?.Stop();
        _grace?.Stop();
        _pill?.Close();
        _card?.Close();
        _pill = null;
        _card = null;
    }
}
