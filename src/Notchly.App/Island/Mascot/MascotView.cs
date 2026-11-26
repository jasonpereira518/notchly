// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/UI/Notch/NotchMascotView.swift (NotchMascotRig's layers and NotchMascotHostView):
// draws the companion's MascotFrame. Windows-specific: one WPF element draws what upstream's Core Animation layers
// held, sampling its MascotStage each rendered frame while anything moves, and glows drawn as soft strokes.
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;

namespace Notchly.App.Island.Mascot;

/// <summary>
/// The companion on its stage: the island's wing, a notice's icon or the Settings preview. The owner configures
/// <see cref="Stage"/> (ShowOnTrack or ShowAlone) and calls <see cref="Wake"/> after telling it anything; the view
/// then draws on every rendered frame while the companion moves, waits for the next blink, and draws nothing in
/// between. It takes no clicks.
/// </summary>
public sealed class MascotView : FrameworkElement
{
    private static readonly ConditionalWeakTable<MascotPath, Geometry> Geometries = new();
    private static readonly ConditionalWeakTable<MascotParts, Brush> BodyBrushes = new();
    private static readonly Dictionary<MascotColor, SolidColorBrush> Brushes = [];

    private readonly DispatcherTimer _timer;
    private MascotFrame? _frame;
    private bool _rendering;

    public MascotView(MascotStage stage)
    {
        Stage = stage;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => { _timer.Stop(); Wake(); };
        Loaded += (_, _) => { Stage.Attached = true; Wake(); };
        Unloaded += (_, _) => { Stage.Attached = false; Sleep(); };
        IsVisibleChanged += (_, _) =>
        {
            Stage.OnScreen = IsVisible;
            if (IsVisible) Wake(); else Sleep();
        };
    }

    public MascotStage Stage { get; }

    /// <summary>The frame last drawn, null before the stage was configured.</summary>
    public MascotFrame? Frame => _frame;

    /// <summary>Draws again from the next rendered frame, for as long as the companion moves.</summary>
    public void Wake()
    {
        _timer.Stop();
        if (_rendering || !IsVisible) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Sleep()
    {
        _timer.Stop();
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Samples the stage now and draws it if it changed. A test or a contact sheet calls it directly.</summary>
    public void Sample()
    {
        var frame = Stage.Frame();
        if (frame is null && _frame is null) return;
        if (frame is not null && _frame is not null && Same(frame, _frame)) return;
        _frame = frame;
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) { Sleep(); return; }
        Sample();
        var now = Stage.Now;
        var next = Stage.NextChange();
        // Moving, it draws again on the next frame; keeping still, it waits for what comes next, or for its owner.
        if (next is { } due && due - now < 0.03) return;
        Sleep();
        if (next is { } later)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Min(later - now, 3600));
            _timer.Start();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        if (_frame is { } frame) Draw(context, frame);
    }

    // MARK: Drawing

    /// <summary>
    /// Draws <paramref name="frame"/> in its view's points: the stage's scale and visible area, then the box moved
    /// to where it stands, with the robot's ears, antenna and bulb, the body's gradient, the visor or shine, and the
    /// eyes on top. A new look crossfades over the one it replaces.
    /// </summary>
    public static void Draw(DrawingContext context, MascotFrame frame)
    {
        if (frame.Opacity <= 0.001) return;
        var pushes = 0;
        context.PushTransform(new MatrixTransform(ToMatrix(frame.StageTransform)));
        pushes++;
        if (frame.VisibleRects is { } rects)
        {
            var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var rect in rects) clip.Children.Add(new RectangleGeometry(new Rect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height))));
            context.PushClip(clip);
            pushes++;
        }
        context.PushOpacity(Math.Clamp(frame.Opacity, 0, 1));
        pushes++;
        context.PushTransform(new MatrixTransform(ToMatrix(frame.Transform)));
        pushes++;
        if (frame.PreviousLook is { } previous && frame.LookFade < 1)
        {
            context.PushOpacity(1 - Math.Clamp(frame.LookFade, 0, 1));
            DrawFigure(context, previous, frame);
            context.Pop();
            context.PushOpacity(Math.Clamp(frame.LookFade, 0, 1));
            DrawFigure(context, frame.Parts, frame);
            context.Pop();
        }
        else DrawFigure(context, frame.Parts, frame);
        // The props float in the stage, so they leave the figure's own hop, squash and lean behind.
        context.Pop();
        pushes--;
        DrawProps(context, frame);
        for (var i = 0; i < pushes; i++) context.Pop();
    }

    private static readonly Dictionary<MascotPropKind, Geometry> PropGeometries = [];

    /// <summary>The props: each its unit outline scaled to its size, turned, and moved to the figure's middle plus its
    /// offset, in the colour of its kind.</summary>
    private static void DrawProps(DrawingContext context, MascotFrame frame)
    {
        foreach (var prop in frame.Props)
        {
            if (prop.Opacity <= 0.01 || prop.Size <= 0.1) continue;
            if (!PropGeometries.TryGetValue(prop.Kind, out var geometry))
                PropGeometries[prop.Kind] = geometry = ToGeometry(MascotPropShapes.Unit(prop.Kind));
            var matrix = new Matrix(prop.Size, 0, 0, prop.Size, 0, 0);
            matrix.Rotate(prop.Rotation * 180 / Math.PI);
            matrix.Translate(frame.FigureCenter.X + prop.Offset.X, frame.FigureCenter.Y + prop.Offset.Y);
            context.PushTransform(new MatrixTransform(matrix));
            context.PushOpacity(Math.Clamp(prop.Opacity, 0, 1));
            context.DrawGeometry(BrushFor(MascotPropShapes.Color(prop.Kind)), null, geometry);
            context.Pop();
            context.Pop();
        }
    }

    private static void DrawFigure(DrawingContext context, MascotParts parts, MascotFrame frame)
    {
        var current = ReferenceEquals(parts, frame.Parts);
        if (parts.Robot is { } robot)
        {
            var shade = BrushFor(parts.FillBottom);
            context.DrawGeometry(shade, null, GeometryFor(robot.Ears));
            context.DrawGeometry(shade, null, GeometryFor(robot.Antenna));
            var bulb = GeometryFor(robot.Bulb);
            // The bulb glows in its own light as a reaction plays (upstream's shadow on the bulb layer).
            var bulbColor = current ? frame.BulbColor : parts.FillTop;
            if (current && frame.BulbGlowOpacity > 0.01) Glow(context, bulb, parts.FillTop, frame.BulbGlowOpacity, frame.BulbGlowRadius);
            context.DrawGeometry(BrushFor(bulbColor), null, bulb);
        }
        context.DrawGeometry(BodyBrush(parts), null, GeometryFor(parts.Body));
        if (parts.Robot is { } face) context.DrawGeometry(BrushFor(MascotColor.Visor), null, GeometryFor(face.Visor));
        if (parts.Shine is { } shine) context.DrawGeometry(BrushFor(MascotColor.Shine), null, GeometryFor(shine));
        // Eyes in ink on a minimal body; lit in the palette's light, glowing, on a robot's visor.
        var robotEyes = parts.Robot is not null;
        var eyeColor = robotEyes ? parts.FillTop : MascotColor.Ink;
        var eyeBrush = BrushFor(eyeColor);
        foreach (var eye in new[] { frame.LeftEye, frame.RightEye })
        {
            if (eye.Length < 3) continue;
            var geometry = ToGeometry(MascotGeometry.SmoothClosedPath(eye));
            if (robotEyes && frame.EyeGlowRadius > 0)
                Glow(context, geometry, eyeColor, current ? frame.EyeGlowOpacity : 0.9, frame.EyeGlowRadius);
            context.DrawGeometry(eyeBrush, null, geometry);
        }
    }

    /// <summary>A soft halo of <paramref name="radius"/> around <paramref name="geometry"/>, as a shadow with no
    /// offset: a few widening strokes, fainter outward.</summary>
    private static void Glow(DrawingContext context, Geometry geometry, MascotColor color, double opacity, double radius)
    {
        const int rings = 3;
        for (var ring = rings; ring >= 1; ring--)
        {
            var width = radius * 2 * ring / rings;
            var alpha = opacity * 0.22;
            var pen = new Pen(BrushFor(color with { Alpha = color.Alpha * alpha }), width)
            {
                LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static Brush BodyBrush(MascotParts parts)
    {
        if (BodyBrushes.TryGetValue(parts, out var cached)) return cached;
        // From the box's top to its bottom, as upstream's gradient layer under the body's mask.
        var brush = new LinearGradientBrush(ToColor(parts.FillTop), ToColor(parts.FillBottom), new Point(0, 0), new Point(0, parts.Size))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        BodyBrushes.AddOrUpdate(parts, brush);
        return brush;
    }

    private static SolidColorBrush BrushFor(MascotColor color)
    {
        if (Brushes.TryGetValue(color, out var cached)) return cached;
        var brush = new SolidColorBrush(ToColor(color));
        brush.Freeze();
        if (Brushes.Count > 256) Brushes.Clear();
        Brushes[color] = brush;
        return brush;
    }

    private static Geometry GeometryFor(MascotPath path)
    {
        if (Geometries.TryGetValue(path, out var cached)) return cached;
        var geometry = ToGeometry(path);
        Geometries.AddOrUpdate(path, geometry);
        return geometry;
    }

    public static Color ToColor(MascotColor color)
    {
        var (a, r, g, b) = color.Bytes;
        return Color.FromArgb(a, r, g, b);
    }

    public static Matrix ToMatrix(MascotMatrix m) => new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);

    /// <summary>The path as a frozen WPF geometry, filled with the nonzero rule as Core Graphics fills.</summary>
    public static Geometry ToGeometry(MascotPath path)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            foreach (var figure in path.Figures)
            {
                context.BeginFigure(ToPoint(figure.Start), isFilled: true, isClosed: figure.Closed);
                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case MascotCurve curve:
                            context.BezierTo(ToPoint(curve.Control1), ToPoint(curve.Control2), ToPoint(curve.To), true, true);
                            break;
                        default:
                            context.LineTo(ToPoint(segment.To), true, true);
                            break;
                    }
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point ToPoint(PointD point) => new(point.X, point.Y);

    private static bool Same(MascotFrame a, MascotFrame b) =>
        ReferenceEquals(a.Parts, b.Parts) && ReferenceEquals(a.PreviousLook, b.PreviousLook) && a.LookFade == b.LookFade
        && a.Transform == b.Transform && a.StageTransform == b.StageTransform && a.Opacity == b.Opacity
        && a.EyeColor == b.EyeColor && a.BulbColor == b.BulbColor && a.BulbGlowOpacity == b.BulbGlowOpacity
        && a.EyeGlowOpacity == b.EyeGlowOpacity && a.LeftEye.AsSpan().SequenceEqual(b.LeftEye)
        && a.RightEye.AsSpan().SequenceEqual(b.RightEye) && SameRects(a.VisibleRects, b.VisibleRects)
        && a.Props.SequenceEqual(b.Props) && a.FigureCenter == b.FigureCenter;

    private static bool SameRects(IReadOnlyList<RectD>? a, IReadOnlyList<RectD>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}
