// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CleaningMode/CleaningUnlockCounter.swift: the unlock gesture of cleaning mode. Five
// deliberate presses of Escape within a window unlock; any other key (a modifier included) starts over; auto-repeat never counts.
// Not ported: CleaningSystemKeyEvent (macOS NX_SYSDEFINED bit packing, Windows media keys arrive as ordinary virtual keys) and
// CleaningMouseReleaseGate (Windows lets the mouse through the lock, so no click is left waiting for its release).
namespace Notchly.Core.Tools;

public sealed class CleaningUnlockCounter(int requiredKeyCode, int threshold, double pressWindow)
{
    /// <summary>Escape (VK_ESCAPE).</summary>
    public const int EscapeKey = 0x1B;
    /// <summary>Upstream's <c>unlockThreshold</c> and the window its manager builds the counter with.</summary>
    public const int UnlockThreshold = 5;
    public const double UnlockWindowSeconds = 6.0;

    private double _lastKeyTime = double.NegativeInfinity;

    /// <summary>The counter cleaning mode uses: five Escapes, each within six seconds of the last.</summary>
    public static CleaningUnlockCounter Standard() => new(EscapeKey, UnlockThreshold, UnlockWindowSeconds);

    /// <summary>Consecutive presses so far (0…threshold).</summary>
    public int Progress { get; private set; }

    /// <summary>
    /// Registers a key-down at <paramref name="time"/> (a monotonic clock, in seconds). <paramref name="isRepeat"/> is true for
    /// auto-repeat, which never counts. Returns true once the progress reaches the threshold: the caller should unlock.
    /// </summary>
    public bool RegisterKeyDown(int code, double time, bool isRepeat)
    {
        if (isRepeat) return false;
        if (code != requiredKeyCode)
        {
            Progress = 0;
            _lastKeyTime = double.NegativeInfinity;
            return false;
        }
        Progress = time - _lastKeyTime <= pressWindow ? Progress + 1 : 1;
        _lastKeyTime = time;
        return Progress >= threshold;
    }

    public void Reset()
    {
        Progress = 0;
        _lastKeyTime = double.NegativeInfinity;
    }
}
