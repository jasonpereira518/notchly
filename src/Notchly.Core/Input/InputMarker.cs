// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's synthetic-event tags (0x564F5253 and friends on CGEvents): every key, wheel tick and click
// Notchly sends carries a marker in its extra-info field, so the input handlers can tell their own events from the user's
// and never act on them twice. The marker is a tag in the high half and a purpose in the low half.
namespace Notchly.Core.Input;

/// <summary>Why an event was sent; the low half of the marker.</summary>
public enum InputSource : uint
{
    None = 0,
    Scroll = 1,
    SmoothScroll = 2,
    Snippet = 3,
    SuperKey = 4,
    QuitProtection = 5,
    MouseButton = 6,
    Switcher = 7,
    Layout = 8,
    Other = 9,
}

public static class InputMarker
{
    /// <summary>"NTCH": the tag every Notchly event carries.</summary>
    public const uint Tag = 0x4E544348;

    /// <summary>The extra-info value for an event sent for a purpose.</summary>
    public static long Make(InputSource source) => ((long)Tag << 32) | (uint)source;

    /// <summary>Whether an event came from Notchly itself.</summary>
    public static bool IsOwn(long extraInfo) => (ulong)extraInfo >> 32 == Tag;

    /// <summary>The purpose of an event of ours, or <see cref="InputSource.None"/> when it is not ours.</summary>
    public static InputSource SourceOf(long extraInfo) => IsOwn(extraInfo) ? (InputSource)(uint)(extraInfo & 0xFFFFFFFF) : InputSource.None;
}

/// <summary>
/// How handlers send input. The real one wraps <c>SendInput</c>; tests and harnesses use a recording fake, so a handler can be
/// exercised without a single key reaching the desktop.
/// </summary>
public interface IInputInjector
{
    /// <summary>A key press or release. <paramref name="extended"/> marks the extended key set (arrows, right-hand Ctrl and Alt).</summary>
    void Key(int virtualKey, bool down, InputSource source, bool extended = false);

    /// <summary>Text typed as Unicode, one UTF-16 unit at a time, so it does not depend on the keyboard layout.</summary>
    void Text(string text, InputSource source);

    /// <summary>A wheel tick: <paramref name="delta"/> in wheel units (120 is one notch), vertical or horizontal.</summary>
    void Wheel(int delta, bool horizontal, InputSource source);

    /// <summary>A button press or release (0 left, 1 right, 2 middle, 3 back, 4 forward).</summary>
    void Button(int button, bool down, InputSource source);

    /// <summary>A relative pointer move.</summary>
    void Move(int dx, int dy, InputSource source);
}
