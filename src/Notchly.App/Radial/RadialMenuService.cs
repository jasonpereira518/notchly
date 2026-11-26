// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/RadialMenu/RadialMenuService.swift: a wheel around the pointer, opened by a chord or a mouse side button.
// Hold the summoner, move toward a slice and let go to run it; tap and the wheel stays open to click (or press Esc, Return, 1-9, the
// arrows). Windows: a chord has no key-up event, so "still held" is its modifiers polled every few milliseconds; a mouse button is
// caught in the hook pipeline above the button shortcuts, swallowed on both halves so the app underneath never sees half a click.
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Notchly.App.Layout;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core.Features;
using Notchly.Core.Input;
using Notchly.Core.Radial;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Notchly.Core.Shortcuts;
using Serilog;

namespace Notchly.App.Radial;

internal sealed class RadialMenuService : IFeatureService
{
    private const uint WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    /// <summary>Above the mouse button shortcuts (60), so a button a wheel claims never reaches them.</summary>
    private const int HookPriority = 70;

    private sealed class Session
    {
        public required RadialMenuProfile Profile;
        public required RadialMenuWindow Window;
        public required DispatcherTimer Timer;
        public required Native.POINT Center;
        public required double Scale;
        public required IntPtr Previous;
        public required RadialMenuActivationMode Mode;
        public Native.POINT Origin;
        public Stack<(IReadOnlyList<RadialMenuItem> Items, string Name)> Stack = new();
        public IReadOnlyList<RadialMenuItem> Items = [];
        public bool Held, PointerActive;
        public Shortcut? HeldShortcut;
        public int? HeldButton;
        public int? Highlight;
        public Native.POINT LastPointer;
    }

    private readonly App _app;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _registrations = [];
    private IDisposable? _mouse;
    private Dictionary<int, RadialMenuProfile> _claims = [];
    private Session? _session;
    private string? _signature;
    private volatile int _swallowedDown;

    public RadialMenuService(App app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
    }

    private SettingsStore Settings => _app.Settings;
    public bool ShortcutUnavailable { get; private set; }
    public bool IsOpen => _session is not null;

    public IReadOnlyList<RadialMenuProfile> Profiles() =>
        RadialMenuSupport.DecodeProfiles(Settings.GetString(DefaultsKey.RadialMenuProfiles), new StoreRadialSettings(Settings));

    public void Sync()
    {
        var enabled = Settings.GetBool(DefaultsKey.RadialMenuEnabled);
        var profiles = enabled ? Profiles() : [];
        var signature = enabled + "|" + Settings.GetString(DefaultsKey.RadialMenuProfiles) + "|" + Settings.GetString(DefaultsKey.RadialMenuShortcut) + "|"
                        + Settings.GetString(DefaultsKey.RadialMenuMouseButton);
        if (signature == _signature) return;
        _signature = signature;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        _mouse?.Dispose();
        _mouse = null;
        _claims = [];
        ShortcutUnavailable = false;
        if (!enabled) { Close(); return; }
        foreach (var profile in profiles)
        {
            if (RadialMenuSupport.ParseShortcut(profile.Shortcut) is { } shortcut)
            {
                var captured = profile;
                var handle = _app.Hotkeys.Register(shortcut, () => _dispatcher.BeginInvoke(() => Begin(captured, shortcut, null)));
                if (handle is null) ShortcutUnavailable = true; else _registrations.Add(handle);
            }
            if (RadialMouseTrigger.Sanitized(profile.MouseButton).ButtonNumber is { } button && !_claims.ContainsKey(button)) _claims[button] = profile;
        }
        if (_claims.Count > 0) _mouse = _app.Input.SubscribeMouse(OnMouse, HookPriority);
    }

    // MARK: The mouse button (hook thread: quick)

    private bool OnMouse(in MouseEvent e)
    {
        if (e.Message is not (WmXButtonDown or WmXButtonUp) || e.IsOwn) return false;
        var button = e.XButton + 2;
        if (e.Message == WmXButtonDown)
        {
            if (!_claims.TryGetValue(button, out var profile) || _app.InputGate.ShouldPassThrough) return false;
            _swallowedDown = button;
            _dispatcher.BeginInvoke(() => Begin(profile, null, button));
            return true;
        }
        // The release belongs to whoever took the press.
        if (_swallowedDown != button) return false;
        _swallowedDown = 0;
        _dispatcher.BeginInvoke(() => EndHold(button));
        return true;
    }

    // MARK: Opening and closing

    public void Open(RadialMenuProfile profile) => Begin(profile, null, null);

    private void Begin(RadialMenuProfile profile, Shortcut? shortcut, int? button)
    {
        if (_session is { } open)
        {
            var same = open.Profile.Id == profile.Id;
            Close();
            if (same) return;
        }
        var items = Available(profile.Items);
        if (items.Count == 0) { SystemSounds.Beep.Play(); return; }
        var mode = RadialMenuActivationModes.Sanitized(Settings.GetString(DefaultsKey.RadialMenuActivationMode));
        var held = mode.StartsHeld(shortcut is not null, button is not null, shortcut is { } s && s.Modifiers != ShortcutModifiers.None);

        Native.GetCursorPos(out var pointer);
        var display = Displays.AtPoint(pointer.X, pointer.Y) ?? Displays.All().FirstOrDefault();
        var scale = display?.Scale ?? 1;
        var side = (int)Math.Round(RadialMenuLayout.PanelSize * scale);
        var center = pointer;
        if (!Settings.GetBool(DefaultsKey.RadialMenuAtPointer) && display is not null)
            center = new Native.POINT { X = display.WorkArea.Left + display.WorkArea.Width / 2, Y = display.WorkArea.Top + display.WorkArea.Height / 2 };
        if (display is not null && display.Bounds.Width >= side && display.Bounds.Height >= side)
        {
            // The panel stays on the screen.
            center.X = Math.Clamp(center.X, display.Bounds.Left + side / 2, display.Bounds.Right - side / 2);
            center.Y = Math.Clamp(center.Y, display.Bounds.Top + side / 2, display.Bounds.Bottom - side / 2);
        }
        var front = WindowInventory.Foreground();
        var previous = front != IntPtr.Zero && ForegroundApps.Describe(front)?.ProcessId != (uint)Environment.ProcessId ? front : IntPtr.Zero;

        var window = new RadialMenuWindow();
        var session = new Session
        {
            Profile = profile, Window = window, Center = center, Scale = scale, Previous = previous, Mode = mode, Items = items, Origin = pointer, LastPointer = pointer,
            Held = held, HeldShortcut = held ? shortcut : null, HeldButton = held ? button : null,
            Timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = TimeSpan.FromMilliseconds(15) },
        };
        _session = session;
        window.Present(items, profile.Color, profile.DisplayName());
        window.PlaceCentredAt(center.X, center.Y, scale);
        window.KeyPressed += key => OnKey(session, key);
        window.PointerReleased += _ => ActivatePointer(session);
        window.Dismissed += () => { if (ReferenceEquals(_session, session)) Close(); };
        session.Timer.Tick += (_, _) => Tick(session);
        window.Show();
        window.Activate();
        session.Timer.Start();
    }

    public void Close()
    {
        if (_session is not { } session) return;
        _session = null;
        session.Timer.Stop();
        session.Window.Close();
    }

    // MARK: The pointer and the hold

    private void Tick(Session session)
    {
        if (!ReferenceEquals(_session, session)) return;
        // A chord has no key-up message: its modifiers are looked at instead.
        if (session.Held && session.HeldShortcut is { } chord && !ChordHeld(chord)) { EndHold(null); return; }
        Native.GetCursorPos(out var p);
        var moved = p.X != session.LastPointer.X || p.Y != session.LastPointer.Y;
        session.LastPointer = p;
        var dx = (p.X - session.Center.X) / session.Scale;
        var dyUp = -(p.Y - session.Center.Y) / session.Scale;
        if (!session.PointerActive)
        {
            var travelled = Math.Sqrt(Math.Pow((p.X - session.Origin.X) / session.Scale, 2) + Math.Pow((p.Y - session.Origin.Y) / session.Scale, 2));
            if (travelled >= RadialMenuLayout.MoveActivationDistance) session.PointerActive = true;
            else return;
        }
        if (!moved && session.Highlight is not null) return;
        session.Highlight = RadialMenuGeometry.HighlightedIndex(dx, dyUp, RadialMenuLayout.DeadZoneRadius, session.Items.Count);
        session.Window.SetHighlight(session.Highlight);
    }

    private static bool ChordHeld(Shortcut chord)
    {
        var held = KeyState.HeldModifiers();
        var modifiersHeld = (!chord.Modifiers.HasFlag(ShortcutModifiers.Control) || held.Contains("control"))
                            && (!chord.Modifiers.HasFlag(ShortcutModifiers.Alt) || held.Contains("alt"))
                            && (!chord.Modifiers.HasFlag(ShortcutModifiers.Shift) || held.Contains("shift"))
                            && (!chord.Modifiers.HasFlag(ShortcutModifiers.Win) || held.Contains("win"));
        return RadialMenuSupport.ShortcutIsStillHeld(modifiersHeld, false);
    }

    /// <summary>The summoner came up: run the highlighted slice, or close, or leave the wheel open to click.</summary>
    private void EndHold(int? button)
    {
        if (_session is not { } session || !session.Held) return;
        if (button is not null && session.HeldButton != button) return;
        session.Held = false;
        switch (session.Mode.ReleaseAction(session.Highlight is not null))
        {
            case RadialMenuReleaseAction.Select: RunHighlight(session); break;
            case RadialMenuReleaseAction.Dismiss: Close(); break;
        }
    }

    // MARK: Clicks and keys

    private void ActivatePointer(Session session)
    {
        if (!ReferenceEquals(_session, session)) return;
        Native.GetCursorPos(out var p);
        var distance = Math.Sqrt(Math.Pow((p.X - session.Center.X) / session.Scale, 2) + Math.Pow((p.Y - session.Center.Y) / session.Scale, 2));
        if (distance > RadialMenuLayout.WheelDiameter / 2) { Close(); return; }
        if (distance < RadialMenuLayout.DeadZoneRadius) { StepBack(session); return; }
        RunHighlight(session);
    }

    private void StepBack(Session session)
    {
        if (session.Stack.Count == 0) { Close(); return; }
        var (items, name) = session.Stack.Pop();
        session.Items = items;
        session.Highlight = null;
        session.PointerActive = false;
        session.Origin = session.LastPointer;
        session.Window.SetItems(items, name);
    }

    private void OnKey(Session session, Key key)
    {
        if (!ReferenceEquals(_session, session)) return;
        var count = session.Items.Count;
        switch (key)
        {
            case Key.Escape: StepBack(session); return;
            case Key.Return or Key.Enter: RunHighlight(session); return;
            case Key.Left or Key.Up: Highlight(session, session.Highlight is { } a ? (a + count - 1) % count : count - 1); return;
            case Key.Right or Key.Down: Highlight(session, session.Highlight is { } b ? (b + 1) % count : 0); return;
        }
        var digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (digit >= 0 && digit < count) { session.Highlight = digit; RunHighlight(session); }
    }

    private static void Highlight(Session session, int index)
    {
        // The arrows take the highlight; the pointer takes it back once it moves again.
        session.Highlight = index;
        session.Held = false;
        session.Window.SetHighlight(index);
    }

    private void RunHighlight(Session session)
    {
        if (session.Highlight is not { } index || index >= session.Items.Count) return;
        var item = session.Items[index];
        if (item.Kind == RadialMenuKind.Submenu)
        {
            session.Stack.Push((session.Items, session.Profile.DisplayName()));
            session.Items = item.Children;
            session.Highlight = null;
            session.PointerActive = false;
            session.Origin = session.LastPointer;
            session.Held = false;
            session.Window.SetItems(item.Children, "‹ " + RadialLabels.Of(item));
            return;
        }
        var previous = session.Previous;
        Close();
        _dispatcher.BeginInvoke(() => Run(item, previous), DispatcherPriority.Background);
    }

    // MARK: Which slices can run, and running one

    private IReadOnlyList<RadialMenuItem> Available(IReadOnlyList<RadialMenuItem> items)
    {
        var result = new List<RadialMenuItem>();
        foreach (var item in items)
        {
            if (item.Kind == RadialMenuKind.Submenu)
            {
                var children = Available(item.Children);
                if (children.Count > 0) result.Add(item with { Children = children });
            }
            else if (CanRun(item)) result.Add(item);
        }
        return result;
    }

    private bool CanRun(RadialMenuItem item)
    {
        switch (item.Kind)
        {
            case RadialMenuKind.Tool:
                return RadialMenuTools.IsRunnable(item.Payload, Settings.IsAvailable, new StoreRadialSettings(Settings)) && ToolRuns(item.Payload);
            case RadialMenuKind.QuickToggle:
                var feature = RadialMenuToggles.Feature(item.Payload);
                if (!Settings.IsAvailable(feature)) return false;
                return feature == AppFeature.QuitApps || (_app.Features.Get<QuickTogglesService>(AppFeature.QuickToggles)?.IsSupported(item.Payload) ?? false);
            case RadialMenuKind.WindowLayout:
                return Settings.IsAvailable(AppFeature.WindowLayout);
            default:
                return true;
        }
    }

    private static bool ToolRuns(string id) => id is "colorPicker" or "screenOCR" or "micMute" or "clipboardHistory" or "quickLauncher" or "cameraPreview" or "scratchpad" or "cleaningMode" or "keepAwake";

    private T? Service<T>(AppFeature feature) where T : class, IFeatureService => _app.Features.Get<T>(feature);

    private async void Run(RadialMenuItem item, IntPtr previous)
    {
        try
        {
            switch (item.Kind)
            {
                case RadialMenuKind.App:
                case RadialMenuKind.File:
                    var path = Expand(item.Payload);
                    if (item.Kind == RadialMenuKind.File && !File.Exists(path) && !Directory.Exists(path)) { SystemSounds.Beep.Play(); return; }
                    ShellActions.Open(path);
                    break;
                case RadialMenuKind.Url:
                    if (RadialMenuSupport.NormalizedUrl(item.Payload) is { } url) ShellActions.Open(url);
                    break;
                case RadialMenuKind.Shortcut:
                    if (RadialMenuSupport.ParseShortcut(item.Payload) is { } chord) await KeySender.SendAsync(previous, chord);
                    break;
                case RadialMenuKind.Media:
                    if (RadialMenuMedia.VirtualKey(item.Payload) is { } vk)
                    {
                        _app.Injector.Key(vk, true, InputSource.Radial);
                        await Task.Delay(40);
                        _app.Injector.Key(vk, false, InputSource.Radial);
                    }
                    break;
                case RadialMenuKind.Tool:
                    await Task.Delay(150);
                    RunTool(item.Payload);
                    break;
                case RadialMenuKind.QuickToggle:
                    await Task.Delay(150);
                    if (item.Payload == "quitAllApps") Service<QuitAppsService>(AppFeature.QuitApps)?.Show(QuitAppsScope.All);
                    else Service<QuickTogglesService>(AppFeature.QuickToggles)?.Run(item.Payload);
                    break;
                case RadialMenuKind.WindowLayout:
                    await Task.Delay(150);
                    if (item.WindowLayoutAction is { } action && Service<WindowLayoutService>(AppFeature.WindowLayout) is { } layout)
                    {
                        if (previous != IntPtr.Zero) { WindowInventory.Activate(previous); await Task.Delay(140); }
                        await layout.ApplyAsync(action);
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "A radial menu slice failed: {Kind}", item.Kind);
            SystemSounds.Beep.Play();
        }
    }

    private void RunTool(string id)
    {
        switch (id)
        {
            case "colorPicker": Service<ColorPickerService>(AppFeature.ColorPicker)?.Pick(); break;
            case "screenOCR": Service<ScreenTextService>(AppFeature.ScreenOCR)?.Capture(); break;
            case "micMute": Service<Sound.MicMuteService>(AppFeature.MicMute)?.Toggle(); break;
            case "clipboardHistory": _app.ShowClipboardWindow(); break;
            case "quickLauncher": Service<Launcher.QuickLauncherService>(AppFeature.QuickLauncher)?.Toggle(); break;
            case "cameraPreview": Service<CameraWindowService>(AppFeature.CameraPreview)?.Toggle(); break;
            case "scratchpad": Service<ScratchpadService>(AppFeature.Scratchpad)?.Toggle(); break;
            case "cleaningMode": Service<CleaningModeService>(AppFeature.CleaningMode)?.Start(); break;
            case "keepAwake": Service<Energy.KeepAwakeService>(AppFeature.KeepAwake)?.Toggle(); break;
        }
    }

    /// <summary>A leading "~" is the user's profile folder; environment variables are filled in.</summary>
    private static string Expand(string payload)
    {
        var text = payload.Trim();
        if (text == "~") text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..]);
        return Environment.ExpandEnvironmentVariables(text);
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        _mouse?.Dispose();
        Close();
    }
}
