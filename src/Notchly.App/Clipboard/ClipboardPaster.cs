// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's "paste from history": the chosen entries go onto the clipboard, the window that was in front comes back, and Ctrl+V
// is sent as Notchly's own key (upstream posts Cmd+V after 0.12 s). Remote-desktop clients and elevated windows get a copy only: a paste
// there would go to another machine, or Windows would refuse it.
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Clipboard;
using Notchly.Core.Input;

namespace Notchly.App.Pasteboard;

public enum PasteOutcome { Pasted, CopiedOnly, Failed }

public sealed class ClipboardPaster(IInputInjector injector)
{
    public const int ReturnDelayMs = 120;

    public async Task<PasteOutcome> PasteAsync(ClipboardHistoryService history, IReadOnlyList<ClipboardEntry> entries, IntPtr target, bool paste = true)
    {
        if (!history.CopyToClipboard(entries)) return PasteOutcome.Failed;
        if (!paste || target == IntPtr.Zero) return PasteOutcome.CopiedOnly;
        var app = ForegroundApps.Describe(target);
        if (app is null || app.Elevated || ClipboardHistorySupport.IsCopyOnlyApp(WindowFrame.ProgramOf(target))) return PasteOutcome.CopiedOnly;

        if (WindowInventory.Foreground() != target)
        {
            WindowInventory.Activate(target);
            for (var i = 0; i < 15 && WindowInventory.Foreground() != target; i++) await Task.Delay(20);
            if (WindowInventory.Foreground() != target) return PasteOutcome.CopiedOnly;
        }
        await Task.Delay(ReturnDelayMs);
        // Never press a key over one the user is still holding (the Return that chose the entry).
        for (var i = 0; i < 25 && (KeyState.HeldModifiers().Count > 0 || KeyState.IsDown(0x0D)); i++) await Task.Delay(20);
        injector.Key(0x11, true, InputSource.Clipboard);
        injector.Key(0x56, true, InputSource.Clipboard);
        injector.Key(0x56, false, InputSource.Clipboard);
        injector.Key(0x11, false, InputSource.Clipboard);
        return PasteOutcome.Pasted;
    }
}
