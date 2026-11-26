// SPDX-License-Identifier: GPL-3.0-or-later
// When the input features must step aside. A full-screen game, video or presentation is left alone (anti-cheat does not
// like another program rewriting its keys, and a game gains nothing from a smoothed wheel), and an app running as
// administrator cannot be reached by an app running without rights (Windows' UIPI), so there is nothing to rewrite
// there. Handlers read two volatile flags; the work of finding them out happens off the hook thread, when the foreground
// window changes and twice a second while a full-screen window covers a display.
using System.Windows.Threading;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Settings;

namespace Notchly.App.Platform.Input;

public sealed class InputGate : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private ForegroundWatcher? _watcher;
    private DispatcherTimer? _timer;
    private volatile bool _fullscreen, _elevatedTarget;
    private int _started;
    private readonly bool _selfElevated = Environment.IsPrivilegedProcess;

    public InputGate(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Nothing runs until a handler first asks: an app with every input feature off watches no foreground changes and runs no timer.
    /// </summary>
    private void Ensure()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _dispatcher.BeginInvoke(() =>
        {
            _watcher = new ForegroundWatcher();
            _watcher.ForegroundChanged += _ => Refresh();
            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
        });
    }

    /// <summary>A full-screen window covers a display and the user asked to leave those alone.</summary>
    public bool GamePaused { get { Ensure(); return _fullscreen && _settings.GetBool(DefaultsKey.InputPauseInGames); } }

    /// <summary>The foreground app runs as administrator and Notchly does not, so its input cannot be reached.</summary>
    public bool ForegroundOutOfReach { get { Ensure(); return _elevatedTarget && !_selfElevated; } }

    /// <summary>Whether input features should pass events through untouched right now.</summary>
    public bool ShouldPassThrough => GamePaused || ForegroundOutOfReach;

    public void Refresh()
    {
        var window = Native.GetForegroundWindow();
        _elevatedTarget = ForegroundApps.Describe(window) is { Elevated: true };
        var display = Displays.All().FirstOrDefault(d => d.Handle == Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST));
        _fullscreen = display is not null && FullscreenDetector.IsFullscreen(display);
    }

    public void Dispose()
    {
        _timer?.Stop();
        _watcher?.Dispose();
    }
}
