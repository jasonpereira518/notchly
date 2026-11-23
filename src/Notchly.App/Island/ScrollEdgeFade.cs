// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream NotchScrollEdgeFade.swift: a scrolling page fades where more of it lies beyond
// an edge, so a card is never sliced by a hard line. At rest at the top there is nothing to fade,
// and the last row at the end stays clear.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchly.App.Island;

public static class ScrollEdgeFade
{
    /// <summary>Fades `scroller`'s top and bottom over `length` points whenever content lies beyond them.</summary>
    public static void Attach(ScrollViewer scroller, double length = 16)
    {
        void Update(object? sender, EventArgs e) => scroller.OpacityMask = Mask(scroller, length);
        scroller.ScrollChanged += Update;
        scroller.SizeChanged += Update;
    }

    private static Brush? Mask(ScrollViewer scroller, double length)
    {
        var height = scroller.ActualHeight;
        var beyondStart = scroller.VerticalOffset > 1;
        var beyondEnd = scroller.VerticalOffset + scroller.ViewportHeight < scroller.ExtentHeight - 1;
        if (height <= 0 || (!beyondStart && !beyondEnd)) return null;
        var fade = Math.Min(length, height / 3) / height;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(beyondStart ? Colors.Transparent : Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, fade));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
        brush.GradientStops.Add(new GradientStop(beyondEnd ? Colors.Transparent : Colors.Black, 1));
        brush.Freeze();
        return brush;
    }
}
