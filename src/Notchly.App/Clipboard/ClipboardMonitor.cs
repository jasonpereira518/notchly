// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the one clipboard watcher the clipboard features share (upstream polls a change count every 0.8 s in each service). A
// message-only window listens for clipboard updates; a change is read once, after a short settle (many apps put their formats on one at
// a time), and handed to each consumer in order, so the URL cleaner can rewrite it before history records it. Notchly's own writes carry
// their sequence numbers and are skipped.
using System.Windows.Interop;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.Core.Clipboard;
using Serilog;

namespace Notchly.App.Pasteboard;

/// <summary>A change on its way through the consumers; one of them may replace what the later ones see.</summary>
public sealed class ClipboardChange(ClipboardSnapshot snapshot)
{
    public ClipboardSnapshot Snapshot { get; private set; } = snapshot;
    public void Replace(ClipboardSnapshot snapshot) => Snapshot = snapshot;
}

public interface IClipboardConsumer
{
    /// <summary>Consumers run in this order: lower first.</summary>
    int Order { get; }
    bool WantsImage { get; }
    void OnChange(ClipboardChange change);
}

public sealed class ClipboardMonitor : IDisposable
{
    /// <summary>The clipboard is read this long after the last update, so an app that sets its formats one by one has finished.</summary>
    public const int SettleMs = 120;

    private readonly IClipboardAccess _access;
    private readonly Dispatcher _dispatcher;
    private readonly bool _listen;
    private readonly List<IClipboardConsumer> _consumers = [];
    private HwndSource? _source;
    private DispatcherTimer? _settle;
    private long _since, _last;

    public ClipboardMonitor(IClipboardAccess access, Dispatcher dispatcher, bool listen = true)
    {
        _access = access;
        _dispatcher = dispatcher;
        _listen = listen;
    }

    public IClipboardAccess Access => _access;

    /// <summary>Starts listening with the first consumer and stops with the last. What is on the clipboard now is not recorded.</summary>
    public IDisposable Add(IClipboardConsumer consumer)
    {
        _dispatcher.VerifyAccess();
        if (_consumers.Count == 0) Start();
        _consumers.Add(consumer);
        _consumers.Sort((a, b) => a.Order.CompareTo(b.Order));
        return new Registration(this, consumer);
    }

    private void Remove(IClipboardConsumer consumer)
    {
        _consumers.Remove(consumer);
        if (_consumers.Count == 0) Stop();
    }

    private void Start()
    {
        _since = _last = _access.SequenceNumber;
        if (!_listen) return;
        _source = new HwndSource(new HwndSourceParameters("NotchlyClipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (!Native.AddClipboardFormatListener(_source.Handle)) Log.Warning("The clipboard listener could not be added");
        _settle = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(SettleMs) };
        _settle.Tick += (_, _) => { _settle.Stop(); ProcessUpdate(); };
    }

    private void Stop()
    {
        _settle?.Stop();
        _settle = null;
        if (_source is not null)
        {
            Native.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            _settle?.Stop();
            _settle?.Start();
        }
        return IntPtr.Zero;
    }

    /// <summary>The consumers learn that Notchly itself wrote or emptied the clipboard: those sequence numbers are not copies.</summary>
    public void RememberOwnWrite(ClipboardWriteResult result)
    {
        if (result.ToSequence > _last) _last = result.ToSequence;
    }

    /// <summary>Reads the clipboard if it changed since the last look and passes it on. Public so a harness can drive it without a message.</summary>
    public void ProcessUpdate()
    {
        if (_consumers.Count == 0) return;
        var sequence = _access.SequenceNumber;
        if (ClipboardHistorySupport.Accepted(sequence, _since, _last) is not { } accepted) return;
        _last = accepted;
        var snapshot = _access.Read(includeImage: _consumers.Any(c => c.WantsImage));
        if (snapshot is null)
        {
            // Another program has it open: try once more shortly.
            _last = sequence - 1;
            if (_settle is not null) { _settle.Stop(); _settle.Start(); }
            return;
        }
        _last = Math.Max(_last, snapshot.Sequence);
        var change = new ClipboardChange(snapshot);
        foreach (var consumer in _consumers.ToArray())
        {
            try { consumer.OnChange(change); }
            catch (Exception e) { Log.Warning(e, "A clipboard consumer failed"); }
        }
    }

    public void Dispose()
    {
        _consumers.Clear();
        Stop();
    }

    private sealed class Registration(ClipboardMonitor monitor, IClipboardConsumer consumer) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            monitor.Remove(consumer);
        }
    }
}
