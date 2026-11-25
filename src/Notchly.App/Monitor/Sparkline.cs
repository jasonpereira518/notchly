// SPDX-License-Identifier: GPL-3.0-or-later
// The history graph (upstream UI/MenuPanel/Sparkline.swift): a line over a soft area fill, drawn straight from the
// history ring. With no ceiling set it scales to the peak, rounded up to a round number so it only moves when the
// traffic crosses a step.
using System.Windows;
using System.Windows.Media;

namespace Notchly.App.Monitor;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<double> _values = [];
    private double _ceiling = 1;

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    /// <summary>The samples (oldest first) and the value the top edge stands for.</summary>
    public void Set(IReadOnlyList<double> values, double ceiling)
    {
        _values = values;
        _ceiling = ceiling > 0 ? ceiling : 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (_values.Count < 2 || width < 2 || height < 2) return;
        var step = width / Math.Max(1, _values.Count - 1);
        Point At(int i) => new(i * step, height - Math.Clamp(_values[i] / _ceiling, 0, 1) * (height - 2) - 1);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, height), true, true);
            for (var i = 0; i < _values.Count; i++) ctx.LineTo(At(i), true, false);
            ctx.LineTo(new Point((_values.Count - 1) * step, height), true, false);
        }
        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, Color.R, Color.G, Color.B), Color.FromArgb(0x05, Color.R, Color.G, Color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (var i = 1; i < _values.Count; i++) ctx.LineTo(At(i), true, true);
        }
        line.Freeze();
        var pen = new Pen(new SolidColorBrush(Color), 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
