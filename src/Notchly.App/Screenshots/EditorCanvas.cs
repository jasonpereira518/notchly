// SPDX-License-Identifier: GPL-3.0-or-later
// The editor's drawing surface (the SwiftUI canvas of upstream's ScreenshotEditorView): it draws the frame with EditorRenderer, adds the
// editing chrome (selection handles, crop and canvas-expansion drafts, recognized-text selection) and turns the pointer into the Core
// model's drag calls in image pixels. The model decides everything; this only translates.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Cursors = System.Windows.Input.Cursors;

namespace Notchly.App.Screenshots;

internal sealed class EditorCanvas : FrameworkElement
{
    private readonly Func<EditorScene> _scene;
    private readonly BlurPatches _patches = new();
    private EditorDrag _drag;
    private bool _dragging;
    private ExportLayout _layout;
    private double _zoom = 0.5;

    public EditorCanvas(Func<EditorScene> scene)
    {
        _scene = scene;
        Focusable = true;
        ClipToBounds = false;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
    }

    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Max(0.01, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Where the picture starts inside the canvas (the padding of a backdrop), in image pixels.</summary>
    public PointD CardOrigin => new(_layout.Card.X, _layout.Card.Y);

    public ExportLayout Layout => _layout;

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout = _scene().Layout();
        return new Size(_layout.Canvas.Width * _zoom, _layout.Canvas.Height * _zoom);
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public PointD ToImage(Point viewPoint) => new(viewPoint.X / _zoom - _layout.Card.X, viewPoint.Y / _zoom - _layout.Card.Y);

    public Point ToView(PointD imagePoint) => new((imagePoint.X + _layout.Card.X) * _zoom, (imagePoint.Y + _layout.Card.Y) * _zoom);

    public Rect ToView(RectD r) => new((r.X + _layout.Card.X) * _zoom, (r.Y + _layout.Card.Y) * _zoom, r.Width * _zoom, r.Height * _zoom);

    // MARK: Drawing

    protected override void OnRender(DrawingContext dc)
    {
        var scene = _scene();
        _layout = scene.Layout();
        var model = scene.Model;
        if (!_layout.HasBackdrop)
            dc.DrawRectangle(CheckerBrush(), null, new Rect(0, 0, _layout.Canvas.Width * _zoom, _layout.Canvas.Height * _zoom));
        EditorRenderer.DrawFrame(dc, scene, _layout, _zoom, _patches, forExport: false);
        DrawChrome(dc, model);
    }

    private static Brush? _checker;

    private static Brush CheckerBrush()
    {
        if (_checker is not null) return _checker;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x3B)), null, new GeometryGroup
        {
            Children = [new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8))],
        }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return _checker = brush;
    }

    private static readonly Brush HandleFill = Brushes.White;
    private static readonly Pen AccentPen = new(new SolidColorBrush(Color.FromRgb(0x2E, 0x8C, 0xFF)), 1.5);
    private static readonly Pen DashedPen = new(new SolidColorBrush(Color.FromRgb(0x2E, 0x8C, 0xFF)), 1.2) { DashStyle = DashStyles.Dash };

    private void DrawChrome(DrawingContext dc, CanvasModel model)
    {
        var handleSize = 9.0;
        void Handle(Point p) => dc.DrawRectangle(HandleFill, AccentPen, new Rect(p.X - handleSize / 2, p.Y - handleSize / 2, handleSize, handleSize));
        void Handles(RectD rect) { foreach (var h in SelectionHandles.All) Handle(ToView(h.Position(rect))); }

        if (model.SelectedAnnotation is { } selected && selected.Id != model.DraftId)
        {
            switch (selected.Tool)
            {
                case ScreenshotTool.Arrow or ScreenshotTool.Line when selected.Points.Count >= 2:
                    Handle(ToView(selected.Points[0]));
                    Handle(ToView(selected.Points[1]));
                    break;
                case ScreenshotTool.Freehand:
                    break;
                case ScreenshotTool.Counter:
                    var d = AnnotationGeometry.CounterDiameter(model.ImageSize, model.Scale);
                    dc.DrawEllipse(null, DashedPen, ToView(new PointD(selected.Rect.X, selected.Rect.Y)), d / 2 * _zoom + 3, d / 2 * _zoom + 3);
                    break;
                default:
                    var view = ToView(selected.Rect);
                    dc.DrawRectangle(null, DashedPen, view);
                    if (selected.Tool.ResizesWithHandles()) Handles(selected.Rect);
                    break;
            }
        }
        if (model.Tool == ScreenshotTool.Crop && model.CropDraft is { } crop)
        {
            var full = new Rect(0, 0, _layout.Canvas.Width * _zoom, _layout.Canvas.Height * _zoom);
            var hole = ToView(crop);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(hole)));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), hole);
            Handles(crop);
        }
        if (model.Tool == ScreenshotTool.ExpandCanvas && model.ExpansionDraft is { } draft)
        {
            var view = ToView(draft);
            var fill = model.ExpansionFill;
            var color = fill.IsTransparent ? Color.FromArgb(0x30, 255, 255, 255)
                : Color.FromRgb((byte)(fill.Red * 255), (byte)(fill.Green * 255), (byte)(fill.Blue * 255));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, color.R, color.G, color.B)), model.ExpansionRejected ? new Pen(Brushes.Red, 1.5) : AccentPen, view);
            Handles(draft);
        }
        if (model.SelectedWordIndexes.Count > 0)
        {
            var highlight = new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0x8C, 0xFF));
            foreach (var index in model.SelectedWordIndexes)
                if (index >= 0 && index < model.TextWords.Count) dc.DrawRectangle(highlight, null, ToView(model.TextWords[index].Rect));
        }
    }

    // MARK: Pointer

    public event Action? PointerPressed;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        PointerPressed?.Invoke();
        var model = _scene().Model;
        var view = e.GetPosition(this);
        var point = ToImage(view);
        _drag = EditorDrag.Started(new PointD(view.X, view.Y));
        _dragging = true;
        CaptureMouse();
        model.BeginDrag(point);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var model = _scene().Model;
        var view = e.GetPosition(this);
        var point = ToImage(view);
        if (_dragging)
        {
            _drag.Update(new PointD(view.X, view.Y));
            model.ContinueDrag(point);
            InvalidateVisual();
        }
        Cursor = model.CursorAt(point) switch
        {
            CanvasCursor.Crosshair => Cursors.Cross,
            CanvasCursor.OpenHand => Cursors.SizeAll,
            CanvasCursor.IBeam => Cursors.IBeam,
            _ => Cursors.Arrow,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var model = _scene().Model;
        var view = e.GetPosition(this);
        var point = ToImage(view);
        _drag.Update(new PointD(view.X, view.Y));
        model.EndDrag(point, _drag.IsTap(model.Tool));
        InvalidateVisual();
    }

    public event Action<double>? ZoomRequested;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomRequested?.Invoke(e.Delta > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }
}
