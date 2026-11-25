// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/PastePlainService.swift and TransientPaste.swift: a shortcut pastes what is on the
// clipboard as plain text. The text is put on the clipboard on its own for a moment, Ctrl+V is sent, and the clipboard is put back as it
// was (only if nobody changed it meanwhile). Files and images are pasted as they are. Windows differences: there is no "Paste and Match
// Style" menu to try first, the paste is Ctrl+V sent as Notchly's own key, and the text of HTML stands in for rich text.
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core.Clipboard;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;

namespace Notchly.App.Pasteboard;

public enum PlainPasteResult { Pasted, Forwarded, Skipped }

/// <summary>The plain paste itself, free of the shortcut so a harness can run it with a fake clipboard and a recording injector.</summary>
public sealed class PlainPaster(IClipboardAccess access, IInputInjector injector, ClipboardMonitor? monitor, Func<bool> keysHeld)
{
    /// <summary>Raised just before and after Ctrl+V is sent (a shortcut that is itself Ctrl+V must not catch it).</summary>
    public Action? BeforeSend { get; init; }
    public Action? AfterSend { get; init; }

    private void Remember(ClipboardWriteResult result) => monitor?.RememberOwnWrite(result);

    private void SendPaste()
    {
        BeforeSend?.Invoke();
        try
        {
            injector.Key(0x11, true, InputSource.Clipboard);
            injector.Key(0x56, true, InputSource.Clipboard);
            injector.Key(0x56, false, InputSource.Clipboard);
            injector.Key(0x11, false, InputSource.Clipboard);
        }
        finally { AfterSend?.Invoke(); }
    }

    /// <summary>Waits for the shortcut's keys to come up: a paste over a held Alt or Shift would be another shortcut.</summary>
    private async Task<bool> WaitForKeysUpAsync()
    {
        for (var i = 0; i < PastePlainSupport.ModifierPolls; i++)
        {
            if (!keysHeld()) return true;
            await Task.Delay(PastePlainSupport.ModifierPollMs);
        }
        return !keysHeld();
    }

    public async Task<PlainPasteResult> RunAsync()
    {
        var snapshot = access.Read(includeImage: false);
        if (snapshot is null) return PlainPasteResult.Skipped;
        var formats = snapshot.Formats;
        var plan = PastePlainSupport.Plan(ClipboardFormats.HasFiles(formats), ClipboardFormats.HasImage(formats), snapshot.Text,
            formats.Contains(ClipboardFormats.Rtf, StringComparer.OrdinalIgnoreCase), null, PastePlainSupport.TextOfHtml(snapshot.Html));

        if (!await WaitForKeysUpAsync()) return PlainPasteResult.Skipped;
        if (plan.Forward)
        {
            SendPaste();
            return PlainPasteResult.Forwarded;
        }

        // Fails open: a clipboard that cannot be kept whole is left as it is and pasted as it is.
        if (access.Capture() is not { } backup)
        {
            Log.Debug("The clipboard could not be kept whole, so the paste goes through as it is");
            SendPaste();
            return PlainPasteResult.Forwarded;
        }
        var written = access.WriteText(plan.Text!, excludeFromMonitoring: true);
        Remember(written);
        if (!written.Ok)
        {
            SendPaste();
            return PlainPasteResult.Forwarded;
        }
        await Task.Delay(PastePlainSupport.SettleMs);
        if (access.SequenceNumber != written.ToSequence)
        {
            // Something else took the clipboard: leave it be, and send nothing.
            return PlainPasteResult.Skipped;
        }
        SendPaste();
        await Task.Delay(PastePlainSupport.RestoreAfterMs);
        if (PastePlainSupport.ShouldRestore(written.ToSequence, access.SequenceNumber))
            Remember(access.Restore(backup, excludeFromMonitoring: true));
        return PlainPasteResult.Pasted;
    }
}

public sealed class PastePlainService(App app) : IFeatureService
{
    private IDisposable? _hotkey;
    private string? _registered;
    private bool _busy;
    private Shortcut? _shortcut;
    private PlainPaster? _paster;

    public void Sync()
    {
        var s = app.Settings;
        var storage = s.GetBool(DefaultsKey.PastePlainEnabled) ? s.GetString(DefaultsKey.PastePlainShortcut) : null;
        if (storage == _registered && (storage is null || _hotkey is not null)) return;
        _hotkey?.Dispose();
        _hotkey = null;
        _registered = storage;
        _shortcut = storage is null ? null : Shortcut.Parse(storage);
        if (_shortcut is { } shortcut) _hotkey = app.Hotkeys.Register(shortcut, OnShortcut);
    }

    private void OnShortcut()
    {
        if (_busy || app.InputGate.ShouldPassThrough) return;
        _busy = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            // A shortcut that is Ctrl+V itself would catch the paste it sends: it steps aside for that moment.
            var selfPaste = _shortcut is { VirtualKey: 0x56, Modifiers: ShortcutModifiers.Control };
            _paster ??= new PlainPaster(app.ClipboardWatch.Access, app.Injector, app.ClipboardWatch, () => KeyState.HeldModifiers().Count > 0 || KeyState.IsDown(0x56))
            {
                BeforeSend = () => { if (selfPaste) { _hotkey?.Dispose(); _hotkey = null; } },
                AfterSend = () => { if (selfPaste && _shortcut is { } shortcut) _hotkey = app.Hotkeys.Register(shortcut, OnShortcut); },
            };
            await _paster.RunAsync();
        }
        catch (Exception e) { Log.Warning(e, "Paste as plain text failed"); }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        _hotkey = null;
    }
}
