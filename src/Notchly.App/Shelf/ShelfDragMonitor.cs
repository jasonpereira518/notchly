// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the drag monitor of upstream Sources/Vorssaint/Services/Shelf/ShelfService.swift (the global mouse-down/dragged/up monitor
// and ShelfInteractionSupport.isContentDrag): tells the shelf when something is being dragged and when the pointer was shaken.
// Windows has no drag-pasteboard change count, so a drag counts as content when it began on the body of a window (not a title bar or a
// border), that window is not being moved or sized, and the pointer has travelled a little. A false alarm only shows a shelf that is
// empty and hides itself. One mouse-hook subscriber at the Observe priority; it never swallows anything and returns at once.
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Shelf;

namespace Notchly.App.ShelfCard;

internal sealed class ShelfDragMonitor : IDisposable
{
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200, WM_NCHITTEST = 0x0084, SMTO_ABORTIFHUNG = 0x2;
    private const int GUI_INMOVESIZE = 0x2;

    private readonly IDisposable _subscription;
    private readonly Dispatcher _dispatcher;
    private readonly ShakeDetector _shake = new();
    /// <summary>A drag counts as content only once it has run this long without the window being moved or sized (a modern title bar
    /// answers a hit test as client area, so the move can only be told from the window's move loop that starts a moment later).</summary>
    private const uint ConfirmMs = 120;

    private bool _down, _fromCaption, _fromOwnProcess, _content, _candidate, _rejected;
    private uint _candidateAt;
    private int _downX, _downY;
    private string? _sourceProgram;
    private IntPtr _downRoot;

    /// <summary>Whether to watch for a shake as well as for drags.</summary>
    public volatile bool ShakeEnabled;
    /// <summary>Programs the shelf must not open for by itself.</summary>
    public volatile IReadOnlyList<string> Exclusions = [];

    /// <summary>A drag of content began (once per press). UI thread.</summary>
    public event Action? DragStarted;
    /// <summary>The button came up. UI thread.</summary>
    public event Action? DragEnded;
    /// <summary>The pointer was shaken during a content drag, and the source is not excluded. UI thread.</summary>
    public event Action? Shaken;

    public ShelfDragMonitor(InputHookService input, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _subscription = input.SubscribeMouse(OnMouse, InputHookService.Priority.Observe);
    }

    /// <summary>The program the current drag started in, as the exclusion list names it.</summary>
    public string? SourceProgram => _sourceProgram;

    private bool OnMouse(in MouseEvent e)
    {
        if (e.IsOwn) return false;
        switch (e.Message)
        {
            case WM_LBUTTONDOWN: Down(e); break;
            case WM_LBUTTONUP:
                if (_down)
                {
                    var wasContent = _content;
                    _down = false;
                    _content = false;
                    if (wasContent) _dispatcher.BeginInvoke(() => DragEnded?.Invoke());
                }
                break;
            case WM_MOUSEMOVE: Move(e); break;
        }
        return false;
    }

    private void Down(in MouseEvent e)
    {
        _down = true;
        _content = false;
        _candidate = false;
        _rejected = false;
        _downX = e.X;
        _downY = e.Y;
        _shake.Reset();
        var window = Native.WindowFromPoint(new Native.POINT { X = e.X, Y = e.Y });
        _downRoot = window == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(window, 2);
        _fromOwnProcess = false;
        _fromCaption = true;
        _sourceProgram = null;
        if (_downRoot == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(_downRoot, out var pid);
        _fromOwnProcess = pid == (uint)Environment.ProcessId;
        // Only the client area carries content: a caption, a border, a scroll bar or a button does not.
        var lParam = new IntPtr((e.Y << 16) | (e.X & 0xFFFF));
        if (Native.SendMessageTimeout(_downRoot, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, 30, out var hit) != IntPtr.Zero)
            _fromCaption = hit.ToInt32() is not (1 or -1);
        _sourceProgram = WindowFrame.ProgramOf(_downRoot);
    }

    private bool InMoveSize()
    {
        var thread = _downRoot == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(_downRoot, out _);
        var info = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        return thread != 0 && Native.GetGUIThreadInfo(thread, ref info) && (info.flags & GUI_INMOVESIZE) != 0;
    }

    private void Move(in MouseEvent e)
    {
        if (!_down || _fromOwnProcess || _fromCaption || _rejected) return;
        if (!_content)
        {
            var moved = Math.Max(Math.Abs(e.X - _downX), Math.Abs(e.Y - _downY));
            if (!_candidate)
            {
                if (moved < ShelfSummonSupport.ContentDragMinMove) return;
                _candidate = true;
                _candidateAt = e.Time;
            }
            if (!ShelfSummonSupport.IsContentDrag(_fromCaption, InMoveSize(), moved)) { _rejected = true; return; }
            if (e.Time - _candidateAt < ConfirmMs) return;
            _content = true;
            _shake.Reset();
            _dispatcher.BeginInvoke(() => DragStarted?.Invoke());
        }
        if (!ShakeEnabled || !_shake.Add(e.Time / 1000.0, e.X)) return;
        if (!ShelfSummonSupport.AutoOpenAllowed(_sourceProgram, Exclusions)) return;
        _shake.Summoned(e.Time / 1000.0);
        _dispatcher.BeginInvoke(() => Shaken?.Invoke());
    }

    public void Dispose() => _subscription.Dispose();
}
