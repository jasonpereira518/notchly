// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CleaningMode/CleaningModeManager.swift and UI/CleaningMode/CleaningOverlayView.swift:
// locks the keyboard (every key, the media and volume keys included) and the wheel while the keyboard is wiped, with a full-screen
// shield or, when asked, a small card in the corner. The mouse keeps working. Five Escapes within six seconds, or the Unlock button,
// give the keyboard back; locking the PC ends it too. Windows differences: the lock is the low-level keyboard hook at the highest
// priority, and Ctrl+Alt+Del and the secure attention sequence cannot be blocked from a program, which the settings page says.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class CleaningModeService(SettingsStore settings, InputHookService input, Dispatcher dispatcher) : IFeatureService
{
    public const string Title = "Keyboard locked for cleaning", Subtitle = "Press Escape 5 times to unlock", Unlock = "Unlock",
                        MouseHint = "Your mouse still works", StartNow = "Lock keyboard now";
    private const uint WmMouseWheel = 0x020A;

    private readonly CleaningUnlockCounter _counter = CleaningUnlockCounter.Standard();
    private readonly HashSet<int> _down = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<CleaningOverlayWindow> _overlays = [];
    private IDisposable? _keys, _wheel;
    private volatile bool _active, _unlockRequested;

    public bool Active => _active;

    /// <summary>Raised on the UI thread when the keyboard is locked or given back.</summary>
    public event Action? Changed;

    public void Sync() { }

    public void Start()
    {
        if (_active) return;
        _active = true;
        _unlockRequested = false;
        _counter.Reset();
        lock (_down) _down.Clear();
        _keys = input.SubscribeKeyboard(OnKey, InputHookService.Priority.Lock);
        _wheel = input.SubscribeMouse(OnMouse, InputHookService.Priority.Lock);
        SystemEvents.SessionSwitch += OnSession;
        var keepVisible = settings.GetBool(DefaultsKey.CleaningModeKeepScreenVisible);
        foreach (var display in keepVisible ? Displays.All().Where(d => d.IsPrimary).Take(1) : Displays.All())
        {
            var overlay = new CleaningOverlayWindow(display, keepVisible, Stop);
            _overlays.Add(overlay);
            overlay.Show();
        }
        Log.Information("Cleaning mode on");
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _keys?.Dispose();
        _wheel?.Dispose();
        _keys = _wheel = null;
        SystemEvents.SessionSwitch -= OnSession;
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
        Log.Information("Cleaning mode off");
        Changed?.Invoke();
    }

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            dispatcher.BeginInvoke(Stop);
    }

    /// <summary>Runs on the hook thread: it must stay quick. Every key is swallowed; Escapes are counted.</summary>
    private bool OnKey(in KeyEvent e)
    {
        if (!_active) return false;
        var time = _clock.Elapsed.TotalSeconds;
        bool isRepeat;
        lock (_down)
        {
            if (e.IsDown) isRepeat = !_down.Add(e.VirtualKey);
            else { _down.Remove(e.VirtualKey); isRepeat = false; }
        }
        if (!e.IsDown) return true;
        if (_counter.RegisterKeyDown(e.VirtualKey, time, isRepeat) && !_unlockRequested)
        {
            _unlockRequested = true;
            dispatcher.BeginInvoke(Stop);
        }
        var progress = _counter.Progress;
        dispatcher.BeginInvoke(() => { foreach (var overlay in _overlays) overlay.SetProgress(progress); });
        return true;
    }

    private bool OnMouse(in MouseEvent e) => _active && (e.Message == WmMouseWheel || e.Message == Native.WM_MOUSEHWHEEL);

    public void Dispose() => Stop();
}

/// <summary>The shield over one display (or the corner card): the message, five dots and the Unlock button.</summary>
public sealed class CleaningOverlayWindow : Window
{
    private readonly Dot[] _dots = new Dot[CleaningUnlockCounter.UnlockThreshold];
    private readonly DisplayInfo _display;
    private readonly bool _card;

    private sealed class Dot : Border { }

    public CleaningOverlayWindow(DisplayInfo display, bool card, Action unlock)
    {
        _display = display;
        _card = card;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        Left = -32000;
        Top = -32000;
        Title = CleaningModeService.Title;
        if (card)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else Background = Brushes.Black;

        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) };
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Dot { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(5, 0, 5, 0), Background = Dim };
            dots.Children.Add(_dots[i]);
        }
        var button = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 8, 22, 8), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = CleaningModeService.Unlock, FontSize = 14, Foreground = Brushes.White },
        };
        button.MouseLeftButtonUp += (_, _) => unlock();
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = CleaningModeService.Title, FontSize = card ? 16 : 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = CleaningModeService.Subtitle, FontSize = card ? 12 : 16, Foreground = Soft, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(dots);
        content.Children.Add(button);
        content.Children.Add(new TextBlock { Text = CleaningModeService.MouseHint, FontSize = 12, Foreground = Soft, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        Content = card
            ? new Border
            {
                Margin = new Thickness(10), Padding = new Thickness(22, 16, 22, 16), CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(1), Child = content,
            }
            : content;
        SourceInitialized += (_, _) =>
        {
            if (!card)
            {
                var b = display.Bounds;
                Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0x0010);
            }
            else Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
        };
        if (card) Loaded += (_, _) => PlaceCard();
    }

    private static readonly SolidColorBrush Dim = new(Color.FromArgb(0x44, 255, 255, 255)), Lit = new(Color.FromRgb(0x30, 0xD1, 0x58)),
                                           Soft = new(Color.FromArgb(0xB3, 255, 255, 255));

    public void SetProgress(int progress)
    {
        for (var i = 0; i < _dots.Length; i++) _dots[i].Background = i < progress ? Lit : Dim;
    }

    private void PlaceCard()
    {
        var own = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * own.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * own.DpiScaleY);
        var work = _display.WorkArea;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, work.Right - width - 16, work.Bottom - height - 16, 0, 0, 0x0001 | 0x0010);
    }
}
