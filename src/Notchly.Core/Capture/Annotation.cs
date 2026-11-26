// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the annotation value types in upstream Services/QuickTools/ScreenshotSupport.swift: ImageAsset, SavedAnnotation,
// Annotation, SelectionStyle, selectionStyle(for:), LayerMove, reordering, canReorder, renumberingCounters.
using System.Security.Cryptography;
using Notchly.Core.Notch;

namespace Notchly.Core.Capture;

/// <summary>
/// Immutable pixels with an identity, shared by the editor's base image, image annotations and undo snapshots. The model
/// never touches pixels: it only needs the identity (equality is by reference, as upstream's CGImage identity), the size
/// and the byte cost for the history budget. <see cref="Payload"/> is the host's own bitmap object.
/// </summary>
public sealed class ImageAsset
{
    /// <summary>Stable identity for saved-state comparisons after the pixels have been released.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    /// <summary>What the pixels cost in memory (row stride times height); charged once per asset in the undo budget.</summary>
    public long ByteCount { get; }

    /// <summary>The host's bitmap (a WPF BitmapSource, say); opaque to the model.</summary>
    public object? Payload { get; init; }

    public ImageAsset(int pixelWidth, int pixelHeight, long? byteCount = null, object? payload = null)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ByteCount = byteCount ?? (long)pixelWidth * pixelHeight * 4;
        Payload = payload;
    }

    public SizeD PixelSize => new(PixelWidth, PixelHeight);
}

/// <summary>Which way a selected annotation moves through the drawing order.</summary>
public enum LayerMove { Forward, Backward }

/// <summary>The style values the editor controls should show for a picked mark.</summary>
public sealed record SelectionStyle(AnnotationColor? Color, StrokeWidth? Stroke, ArrowStyle? ArrowStyle,
                                    int? TextSize = null, int? BlurLevel = null, BlurStyle? BlurStyle = null,
                                    bool? BlurTextOnly = null);

/// <summary>
/// One annotation on the canvas. Geometry lives in image-point coordinates (top-left origin), so export is
/// resolution-exact and the view only scales for display. Immutable: edit with <c>with</c> and replace the list entry.
/// </summary>
public sealed record Annotation
{
    public Guid Id { get; init; }
    public ScreenshotTool Tool { get; init; }
    public RectD Rect { get; init; }
    public IReadOnlyList<PointD> Points { get; init; }
    public string Text { get; init; }
    public AnnotationColor Color { get; init; }
    public StrokeWidth Stroke { get; init; }
    public int TextSize { get; init; }
    public int BlurLevel { get; init; }
    public BlurStyle BlurStyle { get; init; }

    /// <summary>A blur area that covers only the text recognized inside it.</summary>
    public bool BlurTextOnly { get; init; }
    public ArrowStyle ArrowStyle { get; init; }
    public ulong ScribbleSeed { get; init; }
    public int Number { get; init; }
    public ImageAsset? ImageAsset { get; init; }
    public SizeD? RenderReferenceSize { get; init; }

    /// <summary>Mirrors the Swift initialiser: a scribbly arrow without an explicit seed gets its own random one.</summary>
    public Annotation(ScreenshotTool tool,
                      RectD rect = default,
                      IEnumerable<PointD>? points = null,
                      string text = "",
                      AnnotationColor color = AnnotationColor.Red,
                      StrokeWidth stroke = StrokeWidth.Medium,
                      int textSize = AnnotationSizes.DefaultTextSize,
                      int blurLevel = BlurStrength.DefaultLevel,
                      BlurStyle blurStyle = BlurStyle.Pixelate,
                      bool blurTextOnly = false,
                      ArrowStyle arrowStyle = ArrowStyle.Filled,
                      ulong? scribbleSeed = null,
                      int number = 0,
                      ImageAsset? imageAsset = null,
                      SizeD? renderReferenceSize = null,
                      Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Tool = tool;
        Rect = rect;
        Points = points?.ToArray() ?? Array.Empty<PointD>();
        Text = text;
        Color = color;
        Stroke = stroke;
        TextSize = textSize;
        BlurLevel = blurLevel;
        BlurStyle = blurStyle;
        BlurTextOnly = blurTextOnly;
        ArrowStyle = arrowStyle;
        ScribbleSeed = scribbleSeed ?? (arrowStyle == ArrowStyle.Scribbly ? AnnotationSupport.RandomScribbleSeed() : 0);
        Number = number;
        ImageAsset = imageAsset;
        RenderReferenceSize = renderReferenceSize;
    }

    /// <summary>Value equality over every field; the image is compared by reference, the points by value.</summary>
    public bool Equals(Annotation? other) =>
        other is not null
        && Id == other.Id && Tool == other.Tool && Rect == other.Rect && Text == other.Text
        && Color == other.Color && Stroke == other.Stroke && TextSize == other.TextSize
        && BlurLevel == other.BlurLevel && BlurStyle == other.BlurStyle && BlurTextOnly == other.BlurTextOnly
        && ArrowStyle == other.ArrowStyle && ScribbleSeed == other.ScribbleSeed && Number == other.Number
        && ReferenceEquals(ImageAsset, other.ImageAsset) && RenderReferenceSize == other.RenderReferenceSize
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// Dirty-state comparisons remember each image layer's identity without keeping its potentially large image alive after
/// it leaves history.
/// </summary>
public sealed class SavedAnnotation : IEquatable<SavedAnnotation>
{
    private readonly Annotation annotation;
    private readonly Guid? imageId;

    public SavedAnnotation(Annotation source)
    {
        annotation = source with { ImageAsset = null };
        imageId = source.ImageAsset?.Id;
    }

    public bool Equals(SavedAnnotation? other) =>
        other is not null && imageId == other.imageId && annotation.Equals(other.annotation);

    public override bool Equals(object? obj) => obj is SavedAnnotation other && Equals(other);
    public override int GetHashCode() => annotation.Id.GetHashCode();
}

public static class AnnotationSupport
{
    /// <summary>
    /// A fresh scribble seed. The seed belongs to the annotation, so a redraw or export keeps the same sketch (see
    /// <see cref="AnnotationGeometry.ScribblyArrow"/>), while each newly created scribbly arrow gets its own variation.
    /// </summary>
    public static ulong RandomScribbleSeed() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    public static SelectionStyle SelectionStyleFor(Annotation annotation) => annotation.Tool switch
    {
        ScreenshotTool.Arrow => new SelectionStyle(annotation.Color, annotation.Stroke, annotation.ArrowStyle),
        ScreenshotTool.Line or ScreenshotTool.Rect or ScreenshotTool.Ellipse or ScreenshotTool.Freehand =>
            new SelectionStyle(annotation.Color, annotation.Stroke, null),
        ScreenshotTool.Text => new SelectionStyle(annotation.Color, null, null, TextSize: annotation.TextSize),
        ScreenshotTool.Highlight or ScreenshotTool.Counter or ScreenshotTool.Redact =>
            new SelectionStyle(annotation.Color, null, null),
        ScreenshotTool.Pixelate => new SelectionStyle(null, null, null, BlurLevel: annotation.BlurLevel,
            BlurStyle: annotation.BlurStyle, BlurTextOnly: annotation.BlurTextOnly),
        _ => new SelectionStyle(null, null, null),
    };

    /// <summary>Whether the annotation has somewhere to go: false at the end it is heading for, and for an unknown id.</summary>
    public static bool CanReorder(IReadOnlyList<Annotation> annotations, Guid id, LayerMove move)
    {
        int index = IndexOf(annotations, id);
        if (index < 0) return false;
        int target = move == LayerMove.Forward ? index + 1 : index - 1;
        return target >= 0 && target < annotations.Count;
    }

    /// <summary>
    /// Moves one annotation a single step through the array the renderer draws in order, so a shape can go behind text
    /// that was written first. At the end it is heading for, or for an unknown id, the order is unchanged.
    /// </summary>
    public static List<Annotation> Reordering(IReadOnlyList<Annotation> annotations, Guid id, LayerMove move)
    {
        var result = annotations.ToList();
        int index = IndexOf(annotations, id);
        if (index < 0) return result;
        int target = move == LayerMove.Forward ? index + 1 : index - 1;
        if (target < 0 || target >= result.Count) return result;
        (result[index], result[target]) = (result[target], result[index]);
        return result;
    }

    /// <summary>Counters stay 1..n in creation order; deleting one renumbers the rest so a sequence never shows a hole.</summary>
    public static List<Annotation> RenumberingCounters(IEnumerable<Annotation> annotations)
    {
        int next = 1;
        return annotations.Select(a => a.Tool == ScreenshotTool.Counter ? a with { Number = next++ } : a).ToList();
    }

    private static int IndexOf(IReadOnlyList<Annotation> annotations, Guid id)
    {
        for (int i = 0; i < annotations.Count; i++) if (annotations[i].Id == id) return i;
        return -1;
    }
}
