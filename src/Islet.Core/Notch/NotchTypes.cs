// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchSupport.swift: NotchModule, NotchDisplay,
// NotchSilhouette, NotchSize, NotchCapsuleFit, NotchIdleContent, BatteryWarning,
// NotchCompactActivity, NotchActivitySelection, NotchHoverState.
namespace Islet.Core.Notch;

public readonly record struct SizeD(double Width, double Height)
{
    public static readonly SizeD Zero = new(0, 0);
    public override string ToString() => $"{Width:0.##}×{Height:0.##}";
}

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>The island's pages. Raw values are stored in notchModuleOrder / notchHiddenModules.</summary>
public enum NotchModule
{
    Controls, Mixer, Music, Clipboard, Captures, Files, System, Tools, Calendar, Notifications, Timer, Camera,
    Downloads, Scratchpad, Agents, Watch,
}

public enum NotchDisplay { Automatic, BuiltIn, Main, Pointer, All }

/// <summary>A capsule floating at the top, as on the phone, or a cutout hanging from the top edge.</summary>
public enum NotchSilhouette { Capsule, Notch }

public enum NotchSize { Compact, Spacious, Custom }

public enum NotchIdleContent { None, Battery, Music, Agents }

/// <summary>How urgently the charge is shown: amber for the early warning, red when low.</summary>
public enum BatteryWarning { None, Early, Low }

public static class NotchSizes
{
    public const double MinWidth = 360, MaxWidth = 600, MinHeight = 260, MaxHeight = 640;
    public const double DefaultWidth = 440, DefaultHeight = 480;

    public static double Clamped(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Min(max, Math.Max(min, value)) : fallback;
}

public static class NotchEnum
{
    /// <summary>Upstream raw value: lower camel case ("builtIn", "notifications").</summary>
    public static string Raw<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static T Parse<T>(string? raw, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(raw, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : fallback;

    /// <summary>The single-letter key that jumps to a page; stable across ordering and languages.</summary>
    public static char ShortcutKey(this NotchModule module) => module switch
    {
        NotchModule.Controls => 'c', NotchModule.Mixer => 'v', NotchModule.Music => 'm', NotchModule.Clipboard => 'b',
        NotchModule.Captures => 's', NotchModule.Files => 'f', NotchModule.System => 'i', NotchModule.Tools => 't',
        NotchModule.Calendar => 'a', NotchModule.Notifications => 'n', NotchModule.Timer => 'r', NotchModule.Camera => 'w',
        NotchModule.Downloads => 'd', NotchModule.Scratchpad => 'p', NotchModule.Agents => 'g', NotchModule.Watch => 'o',
        _ => '?',
    };
}

/// <summary>A capsule sized and placed by hand (port of NotchCapsuleFit).</summary>
public readonly record struct NotchCapsuleFit
{
    public const double WidthLimit = 40, HeightLimit = 4, DropLimit = 20;
    public static readonly NotchCapsuleFit Zero = new(0, 0, 0);

    public double Width { get; }
    public double Height { get; }
    public double Drop { get; }

    /// <summary>Whole points keep the capsule's edges on pixels, and even widths keep it centred.</summary>
    public NotchCapsuleFit(double width, double height, double drop)
    {
        Width = Math.Round(NotchSizes.Clamped(width, -WidthLimit, WidthLimit, 0) / 2, MidpointRounding.AwayFromZero) * 2;
        Height = Math.Round(NotchSizes.Clamped(height, -HeightLimit, HeightLimit, 0), MidpointRounding.AwayFromZero);
        Drop = Math.Round(NotchSizes.Clamped(drop, 0, DropLimit, 0), MidpointRounding.AwayFromZero);
    }
}

/// <summary>Live activities the closed island can show, in priority order.</summary>
public enum NotchCompactActivity { Timer, Watch, Downloads, Agents, Calendar, Music, KeepAwake }

public static class NotchActivities
{
    public static NotchModule Module(this NotchCompactActivity activity) => activity switch
    {
        NotchCompactActivity.Timer => NotchModule.Timer,
        NotchCompactActivity.Watch => NotchModule.Watch,
        NotchCompactActivity.Downloads => NotchModule.Downloads,
        NotchCompactActivity.Agents => NotchModule.Agents,
        NotchCompactActivity.Calendar => NotchModule.Calendar,
        NotchCompactActivity.Music => NotchModule.Music,
        _ => NotchModule.Controls,
    };

    /// <summary>Activities that can take the left wing beside a primary one.</summary>
    public static IReadOnlyList<NotchCompactActivity> Companions(this NotchCompactActivity primary) => primary switch
    {
        NotchCompactActivity.Timer => [NotchCompactActivity.Downloads, NotchCompactActivity.Agents,
                                       NotchCompactActivity.Calendar, NotchCompactActivity.Music],
        NotchCompactActivity.Calendar => [NotchCompactActivity.Downloads, NotchCompactActivity.Agents, NotchCompactActivity.Music],
        _ => [],
    };

    /// <summary>Running activities sorted by the island's priority.</summary>
    public static IReadOnlyList<NotchCompactActivity> Prioritized(IEnumerable<NotchCompactActivity> running) =>
        running.Distinct().Order().ToList();
}

/// <summary>
/// A choice outlives gaps in what was chosen (a song changing): the island shows the next
/// activity meanwhile and returns to the chosen one. Port of NotchActivitySelection.
/// </summary>
public struct NotchActivitySelection
{
    public NotchCompactActivity? Preferred { get; private set; }

    public void Select(NotchCompactActivity activity, IReadOnlyCollection<NotchCompactActivity> available)
    {
        if (available.Contains(activity)) Preferred = activity;
    }

    public void Reconcile(IReadOnlyCollection<NotchCompactActivity> available)
    {
        if (available.Count == 0) Preferred = null;
    }

    public readonly NotchCompactActivity? Current(IReadOnlyList<NotchCompactActivity> available)
    {
        if (Preferred is { } preferred && available.Contains(preferred)) return preferred;
        return available.Count > 0 ? available[0] : null;
    }
}

/// <summary>Resizing can send hover exits and entries without any pointer movement.</summary>
public struct NotchHoverState
{
    public bool Suppressed { get; private set; }
    public void Close(bool pointerInside) => Suppressed = pointerInside;
    public void Open() => Suppressed = false;
    public void Update(bool pointerInside) { if (!pointerInside) Suppressed = false; }
}
