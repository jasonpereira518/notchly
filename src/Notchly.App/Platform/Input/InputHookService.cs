// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's CGEventTaps: ONE dedicated thread owns the low-level keyboard
// and mouse hooks, installed only while someone subscribes. Handlers run on that thread and must
// return within microseconds: Windows silently removes a hook that stalls (LowLevelHooksTimeout).
//
// Several features share the hooks, so subscribers are ordered: a higher priority runs first, and the first handler to
// return true swallows the event for everyone after it. Events Notchly sent itself carry a marker (Core/Input/InputMarker)
// in the extra-info field; handlers ignore those. A handler that takes too long, or a session unlock, makes the hooks
// install again, since Windows drops a hook that timed out without telling anyone.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Notchly.Core.Input;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public readonly record struct KeyEvent(int VirtualKey, int ScanCode, bool IsDown, bool IsInjected, uint Time, long ExtraInfo = 0, bool IsExtended = false)
{
    /// <summary>Sent by Notchly itself.</summary>
    public bool IsOwn => InputMarker.IsOwn(ExtraInfo);
}

public readonly record struct MouseEvent(uint Message, int X, int Y, int WheelDelta, bool IsInjected, uint Time, long ExtraInfo = 0, uint MouseData = 0)
{
    public bool IsOwn => InputMarker.IsOwn(ExtraInfo);
    /// <summary>Which extra button an XBUTTON message is about: 1 (back) or 2 (forward).</summary>
    public int XButton => (int)(MouseData >> 16) & 0xFFFF;
}

public sealed class InputHookService : IDisposable
{
    /// <summary>Return true to swallow the event so no other app sees it.</summary>
    public delegate bool KeyHandler(in KeyEvent e);
    public delegate bool MouseHandler(in MouseEvent e);

    /// <summary>The order of the built-in subscribers: transformers first, observers after.</summary>
    public static class Priority
    {
        public const int Lock = 200;        // cleaning mode: nothing may reach an app while the keyboard is locked
        public const int Protect = 100;     // quit protection, the switcher's own keys
        public const int Filter = 80;       // debounce: drops events before anything acts on them
        public const int Transform = 60;    // super key, scroll features, button shortcuts
        public const int Expand = 40;       // snippets
        public const int Observe = 0;       // everything that only watches
    }

    private const uint MsgSync = WM_APP + 1, MsgReinstall = WM_APP + 2;
    /// <summary>Over this long in a handler and Windows may already have dropped the hook.</summary>
    private static readonly long SlowTicks = Stopwatch.Frequency / 5;

    private readonly object _gate = new();
    private readonly List<(KeyHandler Handler, int Priority)> _keyHandlers = [];
    private readonly List<(MouseHandler Handler, int Priority)> _mouseHandlers = [];
    private KeyHandler[] _keySnapshot = [];
    private MouseHandler[] _mouseSnapshot = [];
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;
    private volatile bool _disposed;

    /// <summary>How many times the hooks were installed again after a slow handler or an unlock.</summary>
    public int Reinstalls { get; private set; }

    public InputHookService()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Notchly input hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        SystemEvents.SessionSwitch += OnSession;
    }

    public IDisposable SubscribeKeyboard(KeyHandler handler, int priority = Priority.Observe)
    {
        lock (_gate) { _keyHandlers.Add((handler, priority)); _keySnapshot = Ordered(_keyHandlers); }
        RequestSync();
        return new Subscription(() =>
        {
            lock (_gate) { _keyHandlers.RemoveAll(h => h.Handler == handler); _keySnapshot = Ordered(_keyHandlers); }
            RequestSync();
        });
    }

    public IDisposable SubscribeMouse(MouseHandler handler, int priority = Priority.Observe)
    {
        lock (_gate) { _mouseHandlers.Add((handler, priority)); _mouseSnapshot = Ordered(_mouseHandlers); }
        RequestSync();
        return new Subscription(() =>
        {
            lock (_gate) { _mouseHandlers.RemoveAll(h => h.Handler == handler); _mouseSnapshot = Ordered(_mouseHandlers); }
            RequestSync();
        });
    }

    /// <summary>The handlers in the order they run: higher priority first, and subscription order among equals.</summary>
    private static T[] Ordered<T>(List<(T Handler, int Priority)> handlers) where T : Delegate =>
        handlers.Select((h, index) => (h.Handler, h.Priority, index)).OrderByDescending(h => h.Priority).ThenBy(h => h.index).Select(h => h.Handler).ToArray();

    private void RequestSync() => PostThreadMessage(_threadId, MsgSync, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Takes the hooks down and puts them back, in case Windows dropped one after a timeout.</summary>
    public void Reinstall() => PostThreadMessage(_threadId, MsgReinstall, IntPtr.Zero, IntPtr.Zero);

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) Reinstall();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // Make sure the thread has a message queue before anyone posts to it.
        GetMessageQueueReady();
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == MsgSync) { SyncHooks(); continue; }
            if (msg.message == MsgReinstall)
            {
                Unhook(ref _keyboardHook);
                Unhook(ref _mouseHook);
                Reinstalls++;
                Log.Information("Input hooks installed again");
                SyncHooks();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        Unhook(ref _keyboardHook);
        Unhook(ref _mouseHook);
    }

    /// <summary>A thread gets its message queue on its first USER call; PeekMessage forces it.</summary>
    private static void GetMessageQueueReady() => PeekMessage(out _, IntPtr.Zero, WM_APP, WM_APP, 0);

    private void SyncHooks()
    {
        bool wantKeyboard, wantMouse;
        lock (_gate) { wantKeyboard = _keyHandlers.Count > 0; wantMouse = _mouseHandlers.Count > 0; }
        var module = GetModuleHandle(null);
        if (wantKeyboard && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            Log.Information("Keyboard hook {State}", _keyboardHook != IntPtr.Zero ? "installed" : $"failed ({Marshal.GetLastWin32Error()})");
        }
        else if (!wantKeyboard) Unhook(ref _keyboardHook);
        if (wantMouse && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            Log.Information("Mouse hook {State}", _mouseHook != IntPtr.Zero ? "installed" : $"failed ({Marshal.GetLastWin32Error()})");
        }
        else if (!wantMouse) Unhook(ref _mouseHook);
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var started = Stopwatch.GetTimestamp();
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var message = (uint)wParam;
            var e = new KeyEvent((int)data.vkCode, (int)data.scanCode,
                                 message is WM_KEYDOWN or WM_SYSKEYDOWN,
                                 (data.flags & LLKHF_INJECTED) != 0, data.time, data.dwExtraInfo.ToInt64(), (data.flags & 0x01) != 0);
            foreach (var handler in _keySnapshot)
            {
                try { if (handler(in e)) { Watch(started); return new IntPtr(1); } }
                catch (Exception ex) { Log.Error(ex, "Keyboard handler failed"); }
            }
            Watch(started);
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var started = Stopwatch.GetTimestamp();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var e = new MouseEvent((uint)wParam, data.pt.X, data.pt.Y, (short)(data.mouseData >> 16),
                                   (data.flags & LLMHF_INJECTED) != 0, data.time, data.dwExtraInfo.ToInt64(), data.mouseData);
            foreach (var handler in _mouseSnapshot)
            {
                try { if (handler(in e)) { Watch(started); return new IntPtr(1); } }
                catch (Exception ex) { Log.Error(ex, "Mouse handler failed"); }
            }
            Watch(started);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>A handler that held the hook for a long time may have cost it: put the hooks back, off this callback.</summary>
    private void Watch(long started)
    {
        if (Stopwatch.GetTimestamp() - started > SlowTicks && !_disposed)
        {
            Log.Warning("An input handler took {Ms} ms", (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency);
            Reinstall();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSession;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
