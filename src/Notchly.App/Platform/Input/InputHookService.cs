// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's CGEventTaps: ONE dedicated thread owns the low-level keyboard
// and mouse hooks, installed only while someone subscribes. Handlers run on that thread and must
// return within microseconds: Windows silently removes a hook that stalls (LowLevelHooksTimeout).
using System.Runtime.InteropServices;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Input;

public readonly record struct KeyEvent(int VirtualKey, int ScanCode, bool IsDown, bool IsInjected, uint Time);

public readonly record struct MouseEvent(uint Message, int X, int Y, int WheelDelta, bool IsInjected, uint Time);

public sealed class InputHookService : IDisposable
{
    /// <summary>Return true to swallow the event so no other app sees it.</summary>
    public delegate bool KeyHandler(in KeyEvent e);
    public delegate bool MouseHandler(in MouseEvent e);

    private const uint MsgSync = WM_APP + 1;

    private readonly object _gate = new();
    private readonly List<KeyHandler> _keyHandlers = [];
    private readonly List<MouseHandler> _mouseHandlers = [];
    private KeyHandler[] _keySnapshot = [];
    private MouseHandler[] _mouseSnapshot = [];
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;

    public InputHookService()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Notchly input hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
    }

    public IDisposable SubscribeKeyboard(KeyHandler handler)
    {
        lock (_gate) { _keyHandlers.Add(handler); _keySnapshot = [.. _keyHandlers]; }
        RequestSync();
        return new Subscription(() =>
        {
            lock (_gate) { _keyHandlers.Remove(handler); _keySnapshot = [.. _keyHandlers]; }
            RequestSync();
        });
    }

    public IDisposable SubscribeMouse(MouseHandler handler)
    {
        lock (_gate) { _mouseHandlers.Add(handler); _mouseSnapshot = [.. _mouseHandlers]; }
        RequestSync();
        return new Subscription(() =>
        {
            lock (_gate) { _mouseHandlers.Remove(handler); _mouseSnapshot = [.. _mouseHandlers]; }
            RequestSync();
        });
    }

    private void RequestSync() => PostThreadMessage(_threadId, MsgSync, IntPtr.Zero, IntPtr.Zero);

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // Make sure the thread has a message queue before anyone posts to it.
        GetMessageQueueReady();
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == MsgSync) { SyncHooks(); continue; }
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
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var message = (uint)wParam;
            var e = new KeyEvent((int)data.vkCode, (int)data.scanCode,
                                 message is WM_KEYDOWN or WM_SYSKEYDOWN,
                                 (data.flags & LLKHF_INJECTED) != 0, data.time);
            foreach (var handler in _keySnapshot)
            {
                try { if (handler(in e)) return new IntPtr(1); }
                catch (Exception ex) { Log.Error(ex, "Keyboard handler failed"); }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var e = new MouseEvent((uint)wParam, data.pt.X, data.pt.Y, (short)(data.mouseData >> 16),
                                   (data.flags & LLMHF_INJECTED) != 0, data.time);
            foreach (var handler in _mouseSnapshot)
            {
                try { if (handler(in e)) return new IntPtr(1); }
                catch (Exception ex) { Log.Error(ex, "Mouse handler failed"); }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
