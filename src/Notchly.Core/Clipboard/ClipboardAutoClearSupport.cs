// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Clipboard/ClipboardAutoClearSupport.swift: when the clipboard is emptied by itself.
namespace Notchly.Core.Clipboard;

public enum AutoClearDecision { NoteChange, Wait, Clear }

public static class ClipboardAutoClearSupport
{
    public const int DefaultDelaySeconds = 20, MinDelaySeconds = 5, MaxDelaySeconds = 3600;

    public static int SanitizedDelay(int seconds) => Math.Clamp(seconds, MinDelaySeconds, MaxDelaySeconds);

    /// <summary>
    /// What the one-second timer does. A clipboard Notchly just emptied itself waits; a new sequence number restarts the clock; and once
    /// the clipboard has been the same for the delay (the edge included, measured on the wall clock so a wake from sleep clears at once)
    /// it is emptied.
    /// </summary>
    public static AutoClearDecision Decide(long count, long? lastSeenCount, long? ownClearCount, DateTimeOffset lastChange, DateTimeOffset now, int delaySeconds)
    {
        if (ownClearCount is { } own && own == count) return AutoClearDecision.Wait;
        if (lastSeenCount is not { } seen || seen != count) return AutoClearDecision.NoteChange;
        return (now - lastChange).TotalSeconds >= SanitizedDelay(delaySeconds) ? AutoClearDecision.Clear : AutoClearDecision.Wait;
    }

    /// <summary>An event (sleep, display off, lock) empties the clipboard unless it was already emptied at this very count.</summary>
    public static bool EventShouldClear(long count, long? lastClearedCount) => lastClearedCount is not { } cleared || cleared != count;
}
