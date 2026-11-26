// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuitProtection/QuitProtectionService.swift. Alt+F4 and Ctrl+Q (quit) and Ctrl+W (close) are held
// back by a keyboard hook until the person means them: held down for a moment, pressed twice, or pressed with an extra modifier.
// A small panel shows the progress. When a press is confirmed, the key is sent again as Notchly's own, with the modifiers the
// person is still holding. The rules and the guard's states are in Core; here they are fed from the hook and the timers.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using static Notchly.App.Platform.Native;

namespace Notchly.App.KeyboardInput;

public sealed class QuitProtectionService(App app) : IFeatureService
{
    private sealed class Side(QuitGuardKind kind)
    {
        public QuitGuardKind Kind { get; } = kind;
        public volatile bool Enabled;
        public QuitProtectionSupport.Guard Guard { get; set; } = new(QuitGuardMode.Hold, 800, 600);
        public string Extra = "shift";
        public QuitGuardScope Scope;
        public IReadOnlyList<string> Exceptions = [];
        public bool Feedback = true;
        public readonly HashSet<int> Swallowed = [];
        public int ChordKey;
    }

    private IDisposable? _key;
    private readonly Side[] _sides = [new(QuitGuardKind.Quit), new(QuitGuardKind.Close)];
    private HudWindow? _hud;
    private DispatcherTimer? _timer;
    private Side? _holding;
    private double _holdStartedMs;

    public void Sync()
    {
        var s = app.Settings;
        foreach (var side in _sides)
        {
            var k = side.Kind;
            side.Enabled = s.GetBool(k == QuitGuardKind.Quit ? DefaultsKey.QuitProtectionQuitEnabled : DefaultsKey.QuitProtectionCloseEnabled);
            var mode = QuitProtectionSupport.ParseMode(s.GetString(QuitProtectionSupport.SettingKey(k, "Mode")));
            var hold = QuitProtectionSupport.SanitizedHold(s.GetInt(QuitProtectionSupport.SettingKey(k, "HoldDurationMs"), 800));
            var dbl = QuitProtectionSupport.SanitizedDouble(s.GetInt(QuitProtectionSupport.SettingKey(k, "DoubleIntervalMs"), 600));
            side.Guard = new QuitProtectionSupport.Guard(mode, hold, dbl);
            side.Extra = QuitProtectionSupport.SanitizedExtraModifier(s.GetString(QuitProtectionSupport.SettingKey(k, "ExtraModifier")));
            side.Scope = QuitProtectionSupport.ParseScope(s.GetString(QuitProtectionSupport.SettingKey(k, "Scope")));
            side.Exceptions = s.GetList(QuitProtectionSupport.SettingKey(k, "Exceptions"));
            side.Feedback = s.GetBool(QuitProtectionSupport.SettingKey(k, "ShowFeedback"));
        }
        var any = _sides.Any(x => x.Enabled);
        // One step behind the switcher, so Alt+F4 inside it is the switcher's.
        if (any && _key is null) _key = app.Input.SubscribeKeyboard(OnKey, InputHookService.Priority.Protect - 1);
        else if (!any && _key is not null) { _key.Dispose(); _key = null; app.Dispatcher.BeginInvoke(Hide); }
    }

    private bool Applies(Side side)
    {
        var program = WindowFrame.ProgramOf(WindowInventory.Foreground());
        return QuitProtectionSupport.Applies(side.Scope, side.Exceptions, program);
    }

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn || app.InputGate.ShouldPassThrough) return false;
        var vk = e.VirtualKey;
        foreach (var side in _sides)
        {
            if (!side.Enabled) continue;
            // Esc cancels a press that is waiting.
            if (e.IsDown && vk == 0x1B && side.Guard.Pending)
            {
                side.Guard.Cancel();
                side.Swallowed.Clear();
                app.Dispatcher.BeginInvoke(Hide);
                return true;
            }
            if (!e.IsDown)
            {
                if (side.Swallowed.Remove(vk))
                {
                    var released = side.Guard.Released();
                    if (released == GuardDecision.Cancel) app.Dispatcher.BeginInvoke(Hide);
                    return true;
                }
                // The modifier was let go while a hold was going: it is over.
                if (side.Guard.Pending && vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) && side.Guard.Released() == GuardDecision.Cancel)
                {
                    side.Swallowed.Clear();
                    app.Dispatcher.BeginInvoke(Hide);
                }
                continue;
            }
            if (QuitProtectionSupport.ChordModifier(side.Kind, vk) is null) continue;
            var held = KeyState.HeldModifiers();
            if (QuitProtectionSupport.IsPlainChord(side.Kind, vk, held) && Applies(side))
            {
                var repeat = side.Swallowed.Contains(vk);
                var now = Environment.TickCount64;
                var decision = side.Guard.PlainDown(repeat, now);
                side.Swallowed.Add(vk);
                side.ChordKey = vk;
                switch (decision)
                {
                    case GuardDecision.SwallowAndStartHold: app.Dispatcher.BeginInvoke(() => StartHold(side, now)); break;
                    case GuardDecision.SwallowAndArm: app.Dispatcher.BeginInvoke(() => Show(side, $"Press again to {Verb(side)}", 0, arm: now)); break;
                    case GuardDecision.SwallowAndHint: app.Dispatcher.BeginInvoke(() => Show(side, $"Add {side.Extra} to {Verb(side)}", 0, hintOnly: true)); break;
                    case GuardDecision.Confirm: Replay(side, vk, held, strip: null); app.Dispatcher.BeginInvoke(Hide); break;
                }
                return true;
            }
            if (QuitProtectionSupport.IsExtraChord(side.Kind, vk, held, side.Extra) && side.Guard.ExtraDown() == GuardDecision.Confirm && Applies(side))
            {
                Replay(side, vk, held, strip: side.Extra);
                app.Dispatcher.BeginInvoke(Hide);
                return true;
            }
        }
        return false;
    }

    private static string Verb(Side side) => side.Kind == QuitGuardKind.Quit ? "quit" : "close";

    /// <summary>Sends the confirmed key again as Notchly's own; the extra modifier of that mode is lifted for it and put back.</summary>
    private void Replay(Side side, int vk, ISet<string> held, string? strip)
    {
        var injector = app.Injector;
        var stripKey = strip switch { "shift" => 0x10, "alt" => 0x12, "control" => 0x11, _ => 0 };
        if (stripKey != 0) injector.Key(stripKey, false, InputSource.QuitProtection);
        injector.Key(vk, true, InputSource.QuitProtection);
        injector.Key(vk, false, InputSource.QuitProtection);
        if (stripKey != 0) injector.Key(stripKey, true, InputSource.QuitProtection);
    }

    // MARK: The panel

    private void StartHold(Side side, long startedMs)
    {
        _holding = side;
        _holdStartedMs = startedMs;
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Send, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (_holding is not { } h || !h.Guard.Pending) { Hide(); return; }
            var progress = QuitProtectionSupport.HoldProgress(now - _holdStartedMs, h.Guard.HoldMs);
            if (h.Feedback) Show(h, $"Hold to {Verb(h)}", progress);
            if (h.Guard.HoldElapsed(now) == GuardDecision.Confirm)
            {
                var vk = h.ChordKey;
                Replay(h, vk, KeyState.HeldModifiers(), strip: null);
                Hide();
            }
        };
        _timer.Start();
    }

    private void Show(Side side, string text, double progress, long arm = 0, bool hintOnly = false)
    {
        if (!side.Feedback) return;
        _hud ??= new HudWindow();
        _hud.Set(text, progress);
        if (hintOnly || arm != 0)
        {
            _timer?.Stop();
            var timer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(hintOnly ? QuitProtectionSupport.HintSeconds : (side.Guard.DoubleMs + QuitProtectionSupport.ArmSlackMs) / 1000.0),
            };
            timer.Tick += (_, _) => { timer.Stop(); if (!hintOnly) side.Guard.Cancel(); Hide(); };
            timer.Start();
        }
    }

    private void Hide()
    {
        _timer?.Stop();
        _holding = null;
        _hud?.Dismiss();
        _hud = null;
    }

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
        app.Dispatcher.BeginInvoke(Hide);
    }

    /// <summary>The small panel at the bottom of the screen: a line of text and a bar.</summary>
    private sealed class HudWindow : Window
    {
        private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly ProgressBar _bar = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };

        public HudWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            var stack = new StackPanel { MinWidth = 240 };
            stack.Children.Add(_text);
            stack.Children.Add(_bar);
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x22)), CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 14, 22, 14), Child = stack };
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                SetWindowLongPtr(handle, -20, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
            };
            var work = SystemParameters.WorkArea;
            Left = work.Left + work.Width / 2 - 140;
            Top = work.Bottom - 140;
            Show();
            LayoutUpdated += (_, _) => Left = work.Left + (work.Width - ActualWidth) / 2;
        }

        public void Set(string text, double progress)
        {
            _text.Text = text;
            _bar.Value = progress;
            _bar.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Dismiss()
        {
            try { Close(); } catch (InvalidOperationException) { /* already closed */ }
        }
    }
}
