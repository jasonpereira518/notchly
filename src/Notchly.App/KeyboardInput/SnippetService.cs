// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Snippets/TextSnippetService.swift and SnippetLibraryService.swift. A keyboard hook keeps the
// last 64 characters typed; when they end with a snippet's trigger, the key that completed it is swallowed, the trigger is
// erased with backspaces and the replacement is typed as Unicode (or pasted when it has line breaks). The buffer is not kept in
// password fields, after a click, or after a navigation key or a Ctrl, Alt or Win chord. The library is a window you search
// and pick from; it sends the snippet to the window you came from.
using System.Windows;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;

namespace Notchly.App.KeyboardInput;

public sealed class SnippetService(App app) : IFeatureService
{
    private IDisposable? _key, _mouse, _library;
    private readonly SnippetSupport.TypedBuffer _buffer = new();
    private volatile IReadOnlyList<TextSnippet> _snippets = [];
    private SnippetLibraryWindow? _window;
    private string? _registeredShortcut;

    public void Sync()
    {
        var s = app.Settings;
        _snippets = SnippetSupport.Decode(s.GetString(DefaultsKey.TextSnippets));
        var on = s.GetBool(DefaultsKey.TextSnippetsEnabled);
        if (on && _key is null)
        {
            _key = app.Input.SubscribeKeyboard(OnKey, Platform.Input.InputHookService.Priority.Expand);
            _mouse = app.Input.SubscribeMouse(OnMouse, Platform.Input.InputHookService.Priority.Observe);
            app.Passwords.Start();
        }
        else if (!on && _key is not null)
        {
            _key.Dispose(); _mouse?.Dispose(); _key = _mouse = null;
            app.Passwords.Stop();
            _buffer.Clear();
        }
        SyncLibrary();
    }

    private void SyncLibrary()
    {
        var storage = app.Settings.GetBool(DefaultsKey.SnippetLibraryEnabled) ? app.Settings.GetString(DefaultsKey.SnippetLibraryShortcut) : null;
        if (storage == _registeredShortcut && (storage is null || _library is not null)) return;
        _library?.Dispose();
        _library = null;
        _registeredShortcut = storage;
        if (storage is not null && Shortcut.Parse(storage) is { } shortcut) _library = app.Hotkeys.Register(shortcut, ShowLibrary);
    }

    // MARK: Typing

    private bool OnMouse(in MouseEvent e)
    {
        if (e.Message is 0x0201 or 0x0204 or 0x0207) _buffer.Clear();
        return false;
    }

    private bool OnKey(in KeyEvent e)
    {
        if (e.IsOwn) return false;
        var vk = e.VirtualKey;
        if (vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) return false;
        if (!e.IsDown) return false;
        if (app.InputGate.ShouldPassThrough || app.Passwords.InPasswordField) { _buffer.Clear(); return false; }
        var held = KeyState.HeldModifiers();
        if (SnippetSupport.ResetsBuffer(vk, held.Contains("control"), held.Contains("alt"), held.Contains("win"))) { _buffer.Clear(); return false; }
        if (vk == 0x08) { _buffer.Backspace(); return false; }
        var snippets = _snippets;
        if (snippets.Count == 0) { return false; }

        if (SnippetSupport.Delimiter(vk) is { } delimiter)
        {
            var match = SnippetSupport.Find(_buffer.Text, snippets, SnippetExpansion.AfterDelimiter);
            _buffer.Clear();
            if (match is null) { if (delimiter == ' ') _buffer.Append(' '); return false; }
            Task.Run(() => Expand(match, delimiter));
            return true;
        }
        var text = KeyText.Of(vk, e.ScanCode, held.Contains("shift"));
        if (text is not { Length: 1 }) { _buffer.Clear(); return false; }
        _buffer.Append(text[0]);
        var immediate = SnippetSupport.Find(_buffer.Text, snippets, SnippetExpansion.Immediate);
        if (immediate is null) return false;
        _buffer.Clear();
        Task.Run(() => Expand(immediate, null));
        return true;
    }

    // MARK: Sending

    private void Expand(TextSnippet snippet, char? delimiter)
    {
        try
        {
            var text = Filled(snippet);
            var injector = app.Injector;
            for (var i = 0; i < SnippetSupport.DeleteCount(snippet); i++)
            {
                injector.Key(0x08, true, InputSource.Snippet);
                injector.Key(0x08, false, InputSource.Snippet);
            }
            if (SnippetSupport.ShouldPaste(text)) { Paste(SnippetSupport.PastePayload(text, delimiter)); return; }
            Type(text);
            switch (delimiter)
            {
                case ' ': injector.Text(" ", InputSource.Snippet); break;
                case '\t': injector.Key(0x09, true, InputSource.Snippet); injector.Key(0x09, false, InputSource.Snippet); break;
                case '\r': injector.Key(0x0D, true, InputSource.Snippet); injector.Key(0x0D, false, InputSource.Snippet); break;
            }
        }
        catch (Exception e) { Log.Warning(e, "A snippet could not be sent"); }
    }

    private string Filled(TextSnippet snippet)
    {
        string? clipboard = null;
        if (SnippetSupport.NeedsClipboard(snippet.Replacement)) clipboard = app.Dispatcher.Invoke(ReadClipboard);
        return SnippetSupport.Expand(snippet.Replacement, DateTimeOffset.Now, clipboard);
    }

    private void Type(string text)
    {
        foreach (var chunk in SnippetSupport.Chunks(text))
        {
            app.Injector.Text(chunk, InputSource.Snippet);
            Thread.Sleep(2);
        }
    }

    private static string? ReadClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(20); }
        }
        return null;
    }

    /// <summary>A replacement with line breaks is pasted; the clipboard's text is put back a moment later.</summary>
    private void Paste(string text)
    {
        string? saved = null;
        app.Dispatcher.Invoke(() =>
        {
            saved = ReadClipboard();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var from = Platform.Native.GetClipboardSequenceNumber();
                    Clipboard.SetText(text);
                    app.ClipboardWatch.RememberOwnWrite(new(true, from, Platform.Native.GetClipboardSequenceNumber()));
                    return;
                }
                catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(20); }
            }
        });
        var injector = app.Injector;
        injector.Key(0x11, true, InputSource.Snippet);
        injector.Key(0x56, true, InputSource.Snippet);
        injector.Key(0x56, false, InputSource.Snippet);
        injector.Key(0x11, false, InputSource.Snippet);
        Thread.Sleep(250);
        if (saved is not null)
            app.Dispatcher.Invoke(() =>
            {
                try
                {
                    var from = Platform.Native.GetClipboardSequenceNumber();
                    Clipboard.SetText(saved);
                    app.ClipboardWatch.RememberOwnWrite(new(true, from, Platform.Native.GetClipboardSequenceNumber()));
                }
                catch (System.Runtime.InteropServices.COMException) { /* in use */ }
            });
    }

    // MARK: The library

    public void ShowLibrary()
    {
        if (_window is { IsVisible: true } open) { open.Activate(); return; }
        var previous = WindowInventory.Foreground();
        _window = new SnippetLibraryWindow(_snippets);
        _window.Picked += snippet =>
        {
            _window?.Close();
            Task.Run(() => InsertFromLibrary(snippet, previous));
        };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    /// <summary>Goes back to the window you came from, waits for the shortcut's keys to be let go, and types the snippet there.</summary>
    private void InsertFromLibrary(TextSnippet snippet, IntPtr target)
    {
        try
        {
            if (target != IntPtr.Zero) app.Dispatcher.Invoke(() => WindowInventory.Activate(target));
            // Typing while modifiers are down would turn the text into shortcuts.
            for (var i = 0; i < 100 && KeyState.HeldModifiers().Count > 0; i++) Thread.Sleep(15);
            Thread.Sleep(150);
            if (app.Passwords.InPasswordField) return;
            var text = Filled(snippet);
            if (SnippetSupport.ShouldPaste(text)) Paste(text); else Type(text);
        }
        catch (Exception e) { Log.Warning(e, "A snippet could not be inserted"); }
    }

    public void Dispose()
    {
        _key?.Dispose();
        _mouse?.Dispose();
        _library?.Dispose();
        _key = _mouse = _library = null;
        app.Passwords.Stop();
        app.Dispatcher.BeginInvoke(() => _window?.Close());
    }
}
