// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/RadialMenu/RadialMenuSupport.swift: RadialMenuLayout and RadialMenuGeometry.
// Sizes are device-independent pixels (WPF units). Screen y points down on Windows, so callers pass `dyUp` (positive
// toward the top of the screen) exactly as upstream does: use `dyUp = -(pointerY - centerY)`.
namespace Notchly.Core.Radial;

/// <summary>Shared wheel dimensions. The panel is wider than the wheel so its soft shadow fades out instead of being clipped.</summary>
public static class RadialMenuLayout
{
    public const double PanelSize = 400;
    public const double WheelDiameter = 300;
    public const double RingRadius = 112;
    public const double ChipSize = 52;
    public const double HubDiameter = 76;
    public const double DeadZoneRadius = 40;

    /// <summary>
    /// The pointer must travel this far from where the wheel opened before slices start highlighting, so a wheel opened
    /// at the center of the screen never fires on whatever direction the pointer already happened to sit in.
    /// </summary>
    public const double MoveActivationDistance = 8;
}

/// <summary>Pure slice math. Slice 0 sits at 12 o'clock and indices grow clockwise; angles run clockwise from the top in radians.</summary>
public static class RadialMenuGeometry
{
    /// <summary>Angle of the vector (dx, dyUp), where dyUp grows toward the top of the screen, in [0, 2π).</summary>
    public static double Angle(double dx, double dyUp)
    {
        var raw = Math.Atan2(dx, dyUp);
        return raw < 0 ? raw + 2 * Math.PI : raw;
    }

    /// <summary>The slice whose arc contains the angle; null for an empty wheel.</summary>
    public static int? Index(double angle, int itemCount)
    {
        if (itemCount <= 0) return null;
        var step = 2 * Math.PI / itemCount;
        var shifted = (angle + step / 2) % (2 * Math.PI);
        var index = (int)(shifted / step);
        return Math.Min(Math.Max(index, 0), itemCount - 1);
    }

    /// <summary>The slice under the pointer, null inside the dead zone around the hub.</summary>
    public static int? HighlightedIndex(double dx, double dyUp, double deadZoneRadius, int itemCount)
    {
        if (itemCount <= 0) return null;
        var distance = Math.Sqrt(dx * dx + dyUp * dyUp);
        if (distance < deadZoneRadius) return null;
        return Index(Angle(dx, dyUp), itemCount);
    }

    /// <summary>Unit-circle position of a slice center, dyUp toward the screen top.</summary>
    public static (double Dx, double DyUp) UnitPosition(int index, int itemCount)
    {
        if (itemCount <= 0) return (0, 1);
        var theta = 2 * Math.PI * index / itemCount;
        return (Math.Sin(theta), Math.Cos(theta));
    }
}
