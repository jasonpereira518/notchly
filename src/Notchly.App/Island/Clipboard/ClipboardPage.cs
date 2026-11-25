// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Clipboard page: the Windows counterpart of upstream UI/Notch/NotchClipboardView.swift. The list itself is the shared
// ClipboardView; this hosts it in the island, feeds it the keys the island window receives while the page is up, and pastes into the
// window that was in front before the island took the focus. The history service may be installed after the island starts, so the list
// is built the first time the page is shown.
using Notchly.App.Pasteboard;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Settings;

namespace Notchly.App.Island.Clipboard;

public sealed class ClipboardPage
{
    private readonly NotchWindow _window;
    private readonly Func<ClipboardHistoryService?> _history;
    private readonly SettingsStore _settings;
    private readonly ClipboardPaster _paster;
    private readonly Action _collapse, _openWindow;
    private ClipboardView? _view;
    private ClipboardHistoryService? _viewHistory;
    private IntPtr _target, _lastExternal;
    private bool _visible;

    public ClipboardPage(NotchWindow window, Func<ClipboardHistoryService?> history, SettingsStore settings, ClipboardPaster paster, Action collapse, Action openWindow)
    {
        _window = window;
        _history = history;
        _settings = settings;
        _paster = paster;
        _collapse = collapse;
        _openWindow = openWindow;
        window.PreviewKeyDown += (_, e) => { if (_visible && _view?.HandleKey(e) == true) e.Handled = true; };
        window.PreviewTextInput += (_, e) => { if (_visible && _view?.HandleText(e.Text) == true) e.Handled = true; };
    }

    /// <summary>The page fills the preset's height and scrolls inside it.</summary>
    public void Layout(double height) => _window.ClipboardPage.Height = height;

    /// <summary>The island watches the foreground: the last window that is not Notchly's is where a paste goes.</summary>
    public void NoteForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || ForegroundApps.Describe(hwnd) is not { } app || app.ProcessId == (uint)Environment.ProcessId) return;
        _lastExternal = hwnd;
    }

    private void EnsureView()
    {
        var history = _history();
        if (history is null || ReferenceEquals(history, _viewHistory)) return;
        if (_view is not null) _window.ClipboardPage.Children.Clear();
        _viewHistory = history;
        _view = new ClipboardView(history, _settings, ClipboardPalette.Dark, offerWindow: true);
        _window.ClipboardPage.Children.Add(_view.Root);
        _view.Chosen += async (entries, paste) =>
        {
            var target = _target;
            _collapse();
            await _paster.PasteAsync(history, entries, target, paste);
        };
        _view.OpenWindowRequested += () => { _collapse(); _openWindow(); };
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
            {
                EnsureView();
                var front = WindowInventory.Foreground();
                _target = front != IntPtr.Zero && ForegroundApps.Describe(front)?.ProcessId != (uint)Environment.ProcessId ? front : _lastExternal;
            }
            if (_view is not null) _view.Visible = value;
        }
    }
}
