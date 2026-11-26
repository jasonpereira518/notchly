// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotRenderer over WPF's DrawingContext instead of CoreGraphics. One routine draws the editor's view and the
// export (the same marks, the same geometry from Core); only the factor from image pixels to drawing units differs. Pixelate, blur and
// erase come from Core's PixelOps on a copy of the base pixels, cached per mark.
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchly.App.Platform.Capture;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Notchly.Core.Tools;

namespace Notchly.App.Screenshots;

/// <summary>The pixels behind an <see cref="ImageAsset"/> of the editor (its Payload): the BGRA copy blur works on and the bitmap WPF draws.</summary>
internal sealed record EditorPicture(CapturedImage Pixels, BitmapSource Bitmap)
{
    public static EditorPicture From(CapturedImage image) => new(image, ImageCodec.ToBitmap(image));

    public static ImageAsset Asset(CapturedImage image)
    {
        var picture = From(image);
        return new ImageAsset(image.Width, image.Height, (long)image.Stride * image.Height, picture);
    }
}

/// <summary>What one frame needs besides the model: the beautify settings and their pictures.</summary>
internal sealed class EditorScene
{
    public required CanvasModel Model { get; init; }
    public BackdropStyle Backdrop { get; init; } = new();
    public WatermarkStyle Watermark { get; init; } = new();
    public BitmapSource? BackdropPicture { get; init; }
    public BitmapSource? WatermarkPicture { get; init; }
    public bool Downscale { get; init; }

    public EditorPicture BasePicture => (EditorPicture)Model.BaseImage.Payload!;

    public bool HasBackdrop => Backdrop.Kind != BackdropKind.None && (Backdrop.Kind != BackdropKind.Image || BackdropPicture is not null);

    public ExportLayout Layout()
    {
        var pixels = BasePicture.Pixels;
        var corner = BackdropGeometry.TransparentCornerRadius(pixels.Pixels, pixels.Width, pixels.Height, pixels.Stride);
        return ExportLayout.Compute(Model.ImageSize, Backdrop, Model.Scale, HasBackdrop, Downscale, corner);
    }
}

/// <summary>Caches the patch each pixelate / blur / erase mark paints (they are recomputed only when the mark or the picture changes).</summary>
internal sealed class BlurPatches
{
    private readonly Dictionary<Guid, (string Key, BitmapSource Patch)> _cache = [];

    public BitmapSource? For(Annotation mark, EditorPicture picture, IReadOnlyList<RectD>? runs)
    {
        var pixels = picture.Pixels;
        var area = Whole(mark.Rect, pixels.Width, pixels.Height);
        if (area.Width < 1 || area.Height < 1) return null;
        var key = $"{mark.Rect}|{mark.BlurLevel}|{mark.BlurStyle}|{mark.BlurTextOnly}|{picture.Bitmap.GetHashCode()}|{runs?.Count}";
        if (_cache.TryGetValue(mark.Id, out var hit) && hit.Key == key) return hit.Patch;
        var copy = new byte[pixels.Height * pixels.Stride];
        Buffer.BlockCopy(pixels.Pixels, pixels.Offset, copy, 0, copy.Length);
        var rect = new RectD(area.X, area.Y, area.Width, area.Height);
        switch (mark.BlurStyle)
        {
            case BlurStyle.Blur: PixelOps.SoftBlur(copy, pixels.Width, pixels.Height, pixels.Stride, rect, mark.BlurLevel, mark.BlurTextOnly, runs); break;
            case BlurStyle.Erase: PixelOps.Erase(copy, pixels.Width, pixels.Height, pixels.Stride, rect, mark.BlurTextOnly, runs); break;
            default: PixelOps.Pixelate(copy, pixels.Width, pixels.Height, pixels.Stride, rect, mark.BlurLevel, mark.BlurTextOnly, runs); break;
        }
        var patch = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null, copy, pixels.Stride);
        var cropped = new CroppedBitmap(patch, new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height));
        cropped.Freeze();
        _cache[mark.Id] = (key, cropped);
        return cropped;
    }

    public void Forget(IEnumerable<Guid> alive)
    {
        var keep = alive.ToHashSet();
        foreach (var id in _cache.Keys.Where(id => !keep.Contains(id)).ToList()) _cache.Remove(id);
    }

    private static RectD Whole(RectD r, int width, int height)
    {
        var x = Math.Clamp(Math.Floor(Math.Min(r.X, r.Right)), 0, width);
        var y = Math.Clamp(Math.Floor(Math.Min(r.Y, r.Bottom)), 0, height);
        var right = Math.Clamp(Math.Ceiling(Math.Max(r.X, r.Right)), 0, width);
        var bottom = Math.Clamp(Math.Ceiling(Math.Max(r.Y, r.Bottom)), 0, height);
        return new RectD(x, y, right - x, bottom - y);
    }
}

internal static class EditorRenderer
{
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface EmojiFace = new(new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static Color ColorOf(AnnotationColor c, double alpha = 1)
    {
        var (r, g, b) = c.Components();
        return Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    public static Rect R(RectD r, double k, PointD? origin = null) =>
        new((r.X + (origin?.X ?? 0)) * k, (r.Y + (origin?.Y ?? 0)) * k, Math.Max(0, r.Width * k), Math.Max(0, r.Height * k));

    /// <summary>The measure of a text mark, in image pixels (the model's host asks for this).</summary>
    public static SizeD MeasureText(string text, int textSize, double scale)
    {
        var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, textSize * scale, Brushes.Black, 1);
        return new SizeD(Math.Ceiling(f.WidthIncludingTrailingWhitespace) + 4, Math.Ceiling(f.Height));
    }

    /// <summary>
    /// Draws the whole frame at <paramref name="k"/> drawing units per canvas pixel: the backdrop, the card's soft shadow, the picture with
    /// its marks inside the rounded card, and the watermark. Without a backdrop the card is the picture.
    /// </summary>
    public static void DrawFrame(DrawingContext dc, EditorScene scene, ExportLayout layout, double k, BlurPatches patches, bool forExport)
    {
        var model = scene.Model;
        var canvas = new Rect(0, 0, layout.Canvas.Width * k, layout.Canvas.Height * k);
        if (layout.HasBackdrop) DrawBackdrop(dc, scene, layout, k, canvas);
        var card = R(layout.Card, k);
        if (layout.HasBackdrop && layout.Shadow is { } shadow)
        {
            // A soft shadow made of a few widening, fainter outlines (WPF has no blur on a drawing context).
            var corner = layout.CornerRadius * k;
            for (var i = 6; i >= 1; i--)
            {
                var grow = shadow.Blur * k * i / 12;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(shadow.Alpha * 255 / 14), 0, 0, 0)), null,
                    new Rect(card.X - grow, card.Y - grow + shadow.OffsetY * k, card.Width + grow * 2, card.Height + grow * 2), corner + grow, corner + grow);
            }
        }
        var clip = layout.OutlineCorner > 0
            ? new RectangleGeometry(card, layout.OutlineCorner * k, layout.OutlineCorner * k)
            : new RectangleGeometry(card);
        dc.PushClip(clip);
        dc.DrawImage(scene.BasePicture.Bitmap, card);
        DrawMarks(dc, scene, k, patches, layout.Card.X == 0 && layout.Card.Y == 0 ? null : new PointD(layout.Card.X, layout.Card.Y), forExport);
        dc.Pop();
        DrawWatermark(dc, scene, layout, k);
    }

    private static void DrawBackdrop(DrawingContext dc, EditorScene scene, ExportLayout layout, double k, Rect canvas)
    {
        var style = scene.Backdrop;
        Brush? fill = style.Kind switch
        {
            BackdropKind.Preset => Gradient(BackdropIds.Parse(style.PresetId) is { } id ? BackdropIds.Stops(id) : [(0.1, 0.1, 0.12)], canvas),
            BackdropKind.Solid => style.Colors is { Length: > 0 } c ? Solid(c[0]) : Brushes.Black,
            BackdropKind.Gradient => style.Colors is { Length: > 1 } g ? Gradient(g.Select(x => (x[0], x[1], x[2])).ToList(), canvas) : Brushes.Black,
            _ => null,
        };
        if (fill is not null) dc.DrawRectangle(fill, null, canvas);
        else if (style.Kind == BackdropKind.Image && scene.BackdropPicture is { } picture
                 && BackdropGeometry.AspectFill(new SizeD(picture.PixelWidth, picture.PixelHeight), layout.Canvas) is { } place)
        {
            dc.PushClip(new RectangleGeometry(canvas));
            dc.DrawImage(picture, R(place, k));
            dc.Pop();
        }
    }

    private static Brush Solid(double[] c) => new SolidColorBrush(Color.FromRgb(B(c[0]), B(c[1]), B(c[2])));

    private static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static Brush Gradient(IReadOnlyList<(double Red, double Green, double Blue)> stops, Rect canvas)
    {
        var (start, end) = BackdropGeometry.GradientEndpoints(new SizeD(canvas.Width, canvas.Height));
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(start.X, start.Y), EndPoint = new Point(end.X, end.Y) };
        for (var i = 0; i < stops.Count; i++)
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(B(stops[i].Red), B(stops[i].Green), B(stops[i].Blue)), stops.Count == 1 ? 0 : (double)i / (stops.Count - 1)));
        return brush;
    }

    private static void DrawWatermark(DrawingContext dc, EditorScene scene, ExportLayout layout, double k)
    {
        var style = scene.Watermark;
        if (style.Kind == WatermarkKind.None) return;
        var imageSize = layout.ImageSize;
        var colorValue = WatermarkColors.Value(style.Color);
        var color = Color.FromRgb(B(colorValue.Red), B(colorValue.Green), B(colorValue.Blue));
        FormattedText? text = null;
        SizeD content;
        if (style.Kind == WatermarkKind.Text && style.Text.Length > 0)
        {
            text = new FormattedText(style.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
                WatermarkGeometry.FontSize(imageSize, style.Size), new SolidColorBrush(color), 1);
            content = new SizeD(text.WidthIncludingTrailingWhitespace, text.Height);
        }
        else if (style.Kind == WatermarkKind.Image && scene.WatermarkPicture is { } picture)
            content = WatermarkGeometry.ImageContentSize(imageSize, style.Size, new SizeD(picture.PixelWidth, picture.PixelHeight));
        else return;
        if (WatermarkGeometry.Placement(content, style.Rotation, style.Anchor, imageSize, layout.OutlineCorner) is not { } placement) return;
        var rect = WatermarkGeometry.ContentRect(placement, content);
        var origin = layout.Card;
        dc.PushClip(layout.OutlineCorner > 0 ? new RectangleGeometry(R(layout.Card, k), layout.OutlineCorner * k, layout.OutlineCorner * k) : new RectangleGeometry(R(layout.Card, k)));
        dc.PushOpacity(style.Opacity);
        var center = new Point((rect.X + rect.Width / 2 + origin.X) * k, (rect.Y + rect.Height / 2 + origin.Y) * k);
        dc.PushTransform(new RotateTransform(style.Rotation, center.X, center.Y));
        if (text is not null)
        {
            dc.PushTransform(new ScaleTransform(placement.Fit * k, placement.Fit * k, center.X, center.Y));
            dc.DrawText(text, new Point(center.X - content.Width / 2, center.Y - content.Height / 2));
            dc.Pop();
        }
        else if (scene.WatermarkPicture is { } image)
            dc.DrawImage(image, new Rect(center.X - rect.Width * k / 2, center.Y - rect.Height * k / 2, rect.Width * k, rect.Height * k));
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    // MARK: Marks

    private static void DrawMarks(DrawingContext dc, EditorScene scene, double k, BlurPatches patches, PointD? origin, bool forExport)
    {
        var model = scene.Model;
        var marks = model.Annotations;
        var picture = scene.BasePicture;
        // Spotlight: everything outside its regions dims, under the other marks.
        var spotlights = marks.Where(a => a.Tool == ScreenshotTool.Spotlight && a.Rect.Width > 0 && a.Rect.Height > 0).ToList();
        if (spotlights.Count > 0 && model.SpotlightDimStrength > 0)
        {
            Geometry dim = new RectangleGeometry(R(new RectD(0, 0, model.ImageSize.Width, model.ImageSize.Height), k, origin));
            foreach (var s in spotlights)
                dim = new CombinedGeometry(GeometryCombineMode.Exclude, dim, new RectangleGeometry(R(Standard(s.Rect), k, origin), 4 * k * model.Scale, 4 * k * model.Scale));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)Math.Round(model.SpotlightDimStrength * 255), 0, 0, 0)), null, dim);
        }
        foreach (var mark in marks)
        {
            if (mark.Id == model.EditingTextId) continue;
            DrawMark(dc, mark, scene, k, patches, origin, picture);
        }
        patches.Forget(marks.Select(a => a.Id));
    }

    private static RectD Standard(RectD r) => new(Math.Min(r.X, r.Right), Math.Min(r.Y, r.Bottom), Math.Abs(r.Width), Math.Abs(r.Height));

    private static Point P(PointD p, double k, PointD? origin) => new((p.X + (origin?.X ?? 0)) * k, (p.Y + (origin?.Y ?? 0)) * k);

    private static void DrawMark(DrawingContext dc, Annotation a, EditorScene scene, double k, BlurPatches patches, PointD? origin, EditorPicture picture)
    {
        var model = scene.Model;
        var width = a.Stroke.Width() * model.Scale * k;
        var color = ColorOf(a.Color);
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var shadowPen = model.AnnotationShadowsEnabled ? new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round } : null;
        var shadowShift = 1.5 * k * model.Scale;
        var rect = R(Standard(a.Rect), k, origin);
        switch (a.Tool)
        {
            case ScreenshotTool.Rect:
                if (shadowPen is not null) dc.DrawRectangle(null, shadowPen, Shift(rect, shadowShift));
                dc.DrawRectangle(null, pen, rect);
                break;
            case ScreenshotTool.Ellipse:
                if (shadowPen is not null) { var s = Shift(rect, shadowShift); dc.DrawEllipse(null, shadowPen, new Point(s.X + s.Width / 2, s.Y + s.Height / 2), s.Width / 2, s.Height / 2); }
                dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
            case ScreenshotTool.Highlight:
                dc.DrawRectangle(new SolidColorBrush(ColorOf(a.Color, 0.38)), null, rect);
                break;
            case ScreenshotTool.Redact:
                dc.DrawRectangle(brush, null, rect);
                break;
            case ScreenshotTool.Spotlight:
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), Math.Max(1, k * model.Scale)), rect, 4 * k * model.Scale, 4 * k * model.Scale);
                break;
            case ScreenshotTool.Pixelate:
                if (picture is not null && patches.For(a, picture, model.TextRuns) is { } patch && rect.Width > 0 && rect.Height > 0)
                {
                    var area = Whole(a.Rect, picture.Pixels.Width, picture.Pixels.Height);
                    dc.DrawImage(patch, R(area, k, origin));
                }
                break;
            case ScreenshotTool.Line:
                if (a.Points.Count >= 2)
                {
                    if (shadowPen is not null) dc.DrawLine(shadowPen, Shift(P(a.Points[0], k, origin), shadowShift), Shift(P(a.Points[1], k, origin), shadowShift));
                    dc.DrawLine(pen, P(a.Points[0], k, origin), P(a.Points[1], k, origin));
                }
                break;
            case ScreenshotTool.Arrow:
                if (a.Points.Count >= 2) DrawArrow(dc, a, k, origin, width, brush, pen, shadowPen, shadowShift);
                break;
            case ScreenshotTool.Freehand:
                if (a.Points.Count >= 1)
                {
                    var geometry = FreehandGeometry(a.Points, k, origin);
                    if (shadowPen is not null) { dc.PushTransform(new TranslateTransform(0, shadowShift)); dc.DrawGeometry(null, shadowPen, geometry); dc.Pop(); }
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;
            case ScreenshotTool.Text:
                DrawText(dc, a, k, origin, brush, model.Scale, model.AnnotationShadowsEnabled);
                break;
            case ScreenshotTool.Counter:
                DrawCounter(dc, a, scene, k, origin, brush);
                break;
            case ScreenshotTool.Sticker:
                if (rect.Width > 1 && rect.Height > 1) DrawSticker(dc, Stickers.Sanitized(a.Text), rect);
                break;
            case ScreenshotTool.Image:
                if (a.ImageAsset?.Payload is EditorPicture layer) dc.DrawImage(layer.Bitmap, rect);
                break;
        }
    }

    private static RectD Whole(RectD r, int width, int height)
    {
        var x = Math.Clamp(Math.Floor(Math.Min(r.X, r.Right)), 0, width);
        var y = Math.Clamp(Math.Floor(Math.Min(r.Y, r.Bottom)), 0, height);
        return new RectD(x, y, Math.Clamp(Math.Ceiling(Math.Max(r.X, r.Right)), 0, width) - x, Math.Clamp(Math.Ceiling(Math.Max(r.Y, r.Bottom)), 0, height) - y);
    }

    private static readonly Dictionary<StickerKind, (Color Disc, bool DarkMark)> StickerLook = new()
    {
        [StickerKind.Check] = (Color.FromRgb(0x34, 0xC7, 0x59), false), [StickerKind.Cross] = (Color.FromRgb(0xEB, 0x44, 0x3A), false),
        [StickerKind.Star] = (Color.FromRgb(0xFF, 0xCC, 0x00), true), [StickerKind.Heart] = (Color.FromRgb(0xEB, 0x44, 0x6A), false),
        [StickerKind.ThumbsUp] = (Color.FromRgb(0x0A, 0x84, 0xFF), false), [StickerKind.ThumbsDown] = (Color.FromRgb(0x63, 0x63, 0x66), false),
        [StickerKind.Smile] = (Color.FromRgb(0xFF, 0xCC, 0x00), true), [StickerKind.Laugh] = (Color.FromRgb(0xFF, 0xCC, 0x00), true),
        [StickerKind.Party] = (Color.FromRgb(0xAF, 0x52, 0xDE), false), [StickerKind.Fire] = (Color.FromRgb(0xFF, 0x95, 0x00), false),
        [StickerKind.Warning] = (Color.FromRgb(0xFF, 0xCC, 0x00), true), [StickerKind.Eyes] = (Color.FromRgb(0x30, 0xB0, 0xC7), false),
    };

    /// <summary>A sticker as a badge: a coloured disc with a mark. WPF paints colour emoji in one colour, so the marks are drawn by hand or as plain glyphs.</summary>
    private static void DrawSticker(DrawingContext dc, StickerKind kind, Rect rect)
    {
        var (disc, dark) = StickerLook[kind];
        var side = Math.Min(rect.Width, rect.Height);
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var radius = side / 2;
        var ink = dark ? new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x00)) : Brushes.White;
        var line = new Pen(ink, Math.Max(1.5, side * 0.09)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawEllipse(new SolidColorBrush(disc), new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), Math.Max(1, side * 0.02)), center, radius, radius);
        Point At(double x, double y) => new(center.X + x * radius, center.Y + y * radius);
        switch (kind)
        {
            case StickerKind.Check:
                dc.DrawGeometry(null, line, new PathGeometry([new PathFigure(At(-0.45, 0.02), [new PolyLineSegment([At(-0.1, 0.38), At(0.5, -0.32)], true)], false)]));
                return;
            case StickerKind.Cross:
                dc.DrawLine(line, At(-0.38, -0.38), At(0.38, 0.38));
                dc.DrawLine(line, At(0.38, -0.38), At(-0.38, 0.38));
                return;
            case StickerKind.Smile or StickerKind.Laugh:
                var eye = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x00));
                dc.DrawEllipse(eye, null, At(-0.32, -0.25), radius * 0.1, radius * (kind == StickerKind.Laugh ? 0.06 : 0.12));
                dc.DrawEllipse(eye, null, At(0.32, -0.25), radius * 0.1, radius * (kind == StickerKind.Laugh ? 0.06 : 0.12));
                var mouth = new PathFigure(At(-0.5, 0.12), [new ArcSegment(At(0.5, 0.12), new Size(radius * 0.5, radius * (kind == StickerKind.Laugh ? 0.62 : 0.42)), 0, false, SweepDirection.Counterclockwise, true)], kind == StickerKind.Laugh);
                dc.DrawGeometry(kind == StickerKind.Laugh ? Brushes.White : null, line, new PathGeometry([mouth]));
                return;
            case StickerKind.Warning:
                var bang = new FormattedText("!", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, side * 0.7, ink, 1);
                dc.DrawText(bang, new Point(center.X - bang.Width / 2, center.Y - bang.Height / 2));
                return;
        }
        var glyph = new FormattedText(kind.Glyph(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, EmojiFace, side * 0.55, ink, 1);
        dc.DrawText(glyph, new Point(center.X - glyph.Width / 2, center.Y - glyph.Height / 2));
    }

    private static Rect Shift(Rect r, double dy) => new(r.X, r.Y + dy, r.Width, r.Height);
    private static Point Shift(Point p, double dy) => new(p.X, p.Y + dy);

    private static void DrawArrow(DrawingContext dc, Annotation a, double k, PointD? origin, double width, Brush brush, Pen pen, Pen? shadowPen, double shadowShift)
    {
        var tail = new PointD(a.Points[0].X, a.Points[0].Y);
        var tip = new PointD(a.Points[1].X, a.Points[1].Y);
        var strokeWidth = a.Stroke.Width() * 1.0;
        // The geometry is in image pixels with the stroke in pixels: width / k gives the model's value.
        var pixelWidth = width / k;
        if (a.ArrowStyle == ArrowStyle.Filled)
        {
            var silhouette = AnnotationGeometry.ArrowSilhouette(tail, tip, pixelWidth);
            var figure = new PathFigure { StartPoint = P(silhouette[0], k, origin), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new PolyLineSegment(silhouette.Skip(1).Select(p => P(p, k, origin)), true));
            var geometry = new PathGeometry([figure]) { FillRule = FillRule.Nonzero };
            if (shadowPen is not null) { dc.PushTransform(new TranslateTransform(0, shadowShift)); dc.DrawGeometry(shadowPen.Brush, null, geometry); dc.Pop(); }
            dc.DrawGeometry(brush, null, geometry);
            return;
        }
        var paths = AnnotationGeometry.ArrowStrokePath(tail, tip, pixelWidth, a.ArrowStyle, a.ScribbleSeed);
        if (paths is null) return;
        var path = new PathGeometry();
        foreach (var sub in paths)
        {
            if (sub.Points.Count < 2) continue;
            var figure = new PathFigure { StartPoint = P(sub.Points[0], k, origin), IsClosed = sub.Closed, IsFilled = false };
            figure.Segments.Add(new PolyLineSegment(sub.Points.Skip(1).Select(p => P(p, k, origin)), true));
            path.Figures.Add(figure);
        }
        if (shadowPen is not null) { dc.PushTransform(new TranslateTransform(0, shadowShift)); dc.DrawGeometry(null, shadowPen, path); dc.Pop(); }
        dc.DrawGeometry(null, pen, path);
    }

    private static Geometry FreehandGeometry(IReadOnlyList<PointD> points, double k, PointD? origin)
    {
        var figure = new PathFigure { StartPoint = P(points[0], k, origin), IsClosed = false, IsFilled = false };
        if (points.Count == 1) figure.Segments.Add(new LineSegment(new Point(figure.StartPoint.X + 0.01, figure.StartPoint.Y), true));
        else figure.Segments.Add(new PolyLineSegment(points.Skip(1).Select(p => P(p, k, origin)), true));
        return new PathGeometry([figure]);
    }

    private static void DrawText(DrawingContext dc, Annotation a, double k, PointD? origin, Brush brush, double scale, bool shadows)
    {
        if (a.Text.Length == 0) return;
        var text = new FormattedText(a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, a.TextSize * scale * k, brush, 1);
        var at = P(new PointD(a.Rect.X + 2, a.Rect.Y), k, origin);
        if (shadows)
        {
            var back = new FormattedText(a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, a.TextSize * scale * k, new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1);
            dc.DrawText(back, new Point(at.X, at.Y + 1.5 * scale * k));
        }
        dc.DrawText(text, at);
    }

    private static void DrawCounter(DrawingContext dc, Annotation a, EditorScene scene, double k, PointD? origin, Brush brush)
    {
        var diameter = AnnotationGeometry.CounterDiameter(scene.Model.ImageSize, scene.Model.Scale) * k;
        var center = P(new PointD(a.Rect.X, a.Rect.Y), k, origin);
        dc.DrawEllipse(brush, new Pen(Brushes.White, Math.Max(1, diameter * 0.06)), center, diameter / 2, diameter / 2);
        var dark = a.Color is AnnotationColor.Yellow or AnnotationColor.White or AnnotationColor.Orange;
        var text = new FormattedText(a.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
            diameter * 0.55, dark ? Brushes.Black : Brushes.White, 1);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
