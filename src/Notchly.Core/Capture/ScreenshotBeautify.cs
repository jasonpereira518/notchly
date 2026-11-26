// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the backdrop (beautify) and watermark halves of upstream Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift
// (BackdropID, backdropPadding, cardCornerRadius, transparentCornerRadius, backdropBlurRadius, BackdropStyle, the preset codecs,
// WatermarkStyle, watermarkFontSize/ImageWidth/Placement) and the geometry of ScreenshotRenderer.renderExport / renderBackdrop /
// drawFill. Drawing itself is the WPF app's job; everything here is numbers, so it is testable.
// Coordinates: image pixels with a TOP-LEFT origin, y down, as upstream's annotation space already is.
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchly.Core.Notch;
using Notchly.Core.Tools;

namespace Notchly.Core.Capture;

// MARK: - Backdrop

/// <summary>The built-in gradient backdrops. Raw values are what the style JSON stores in <c>presetID</c>.</summary>
public enum BackdropId { None, Ocean, Sunset, Forest, Candy, Graphite }

public static class BackdropIds
{
    public static string Raw(BackdropId id) => id.ToString().ToLowerInvariant();

    /// <summary>The preset for a stored id, or null when it is not one of the six.</summary>
    public static BackdropId? Parse(string? raw) =>
        Enum.GetValues<BackdropId>().Cast<BackdropId?>().FirstOrDefault(id => Raw(id!.Value) == raw);

    /// <summary>Gradient stops as sRGB components, top-left to bottom-right; none has no stops.</summary>
    public static IReadOnlyList<(double Red, double Green, double Blue)> Stops(BackdropId id) => id switch
    {
        BackdropId.Ocean => [(0.20, 0.47, 0.96), (0.45, 0.83, 0.98)],
        BackdropId.Sunset => [(0.99, 0.36, 0.42), (1.00, 0.75, 0.35)],
        BackdropId.Forest => [(0.07, 0.56, 0.43), (0.62, 0.87, 0.50)],
        BackdropId.Candy => [(0.66, 0.32, 0.95), (0.99, 0.56, 0.65)],
        BackdropId.Graphite => [(0.23, 0.25, 0.31), (0.55, 0.60, 0.70)],
        _ => [],
    };
}

public enum BackdropKind { None, Preset, Solid, Gradient, Image }

/// <summary>
/// The full backdrop configuration behind a capture, persisted as JSON (one style in use, plus the saved presets). Colours are
/// sRGB components in 0...1, so the codec stays pure and testable.
/// </summary>
public sealed record BackdropStyle : IEquatable<BackdropStyle>
{
    public BackdropKind Kind { get; init; } = BackdropKind.None;
    /// <summary>A <see cref="BackdropId"/> raw value when the kind is a preset.</summary>
    public string? PresetId { get; init; }
    /// <summary>One colour (solid) or two (gradient), each [r, g, b] in 0...1.</summary>
    public double[][]? Colors { get; init; }
    /// <summary>Absolute path when the kind is an image (a user picture or a wallpaper).</summary>
    public string? ImagePath { get; init; }
    public double Padding { get; init; } = 0.5;
    public double CornerRadius { get; init; }
    public double Blur { get; init; }

    public bool Equals(BackdropStyle? other) =>
        other is not null && Kind == other.Kind && PresetId == other.PresetId && ImagePath == other.ImagePath
        && Padding.Equals(other.Padding) && CornerRadius.Equals(other.CornerRadius) && Blur.Equals(other.Blur)
        && (Colors is null ? other.Colors is null
            : other.Colors is not null && Colors.Length == other.Colors.Length
              && Colors.Zip(other.Colors).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)));

    public override int GetHashCode() => HashCode.Combine(Kind, PresetId, ImagePath, Padding, CornerRadius, Blur, Colors?.Length);

    /// <summary>
    /// Clamps sliders, validates colours and drops broken configurations back to none, so a damaged persisted value can never
    /// wedge the editor.
    /// </summary>
    public BackdropStyle Sanitized()
    {
        var style = this with
        {
            Padding = double.IsFinite(Padding) ? Math.Clamp(Padding, 0, 1) : 0.5,
            CornerRadius = double.IsFinite(CornerRadius) ? Math.Clamp(CornerRadius, 0, 1) : 0.1,
            Blur = double.IsFinite(Blur) ? Math.Clamp(Blur, 0, 1) : 0,
        };
        switch (style.Kind)
        {
            case BackdropKind.None:
                return style;
            case BackdropKind.Preset:
                return BackdropIds.Parse(style.PresetId) is { } id && id != BackdropId.None ? style : style.Demoted();
            case BackdropKind.Solid:
            case BackdropKind.Gradient:
                var wanted = style.Kind == BackdropKind.Solid ? 1 : 2;
                if (style.Colors is not { } colors || colors.Length != wanted
                    || colors.Any(c => c is null || c.Length != 3 || !c.All(double.IsFinite))) return style.Demoted();
                return style with { Colors = colors.Select(c => c.Select(v => Math.Clamp(v, 0, 1)).ToArray()).ToArray() };
            default:
                return !string.IsNullOrEmpty(style.ImagePath) ? style : style.Demoted();
        }
    }

    private BackdropStyle Demoted() => this with { Kind = BackdropKind.None, PresetId = null, Colors = null, ImagePath = null };

    /// <summary>The JSON stored in <c>screenshotBackdropStyle</c>; empty when it cannot be written.</summary>
    public string Encoded()
    {
        try { return ToNode().ToJsonString(); }
        catch (ArgumentException) { return ""; }
    }

    internal JsonObject ToNode()
    {
        var node = new JsonObject { ["kind"] = Kind.ToString().ToLowerInvariant() };
        if (PresetId is not null) node["presetID"] = PresetId;
        if (Colors is not null)
            node["colors"] = new JsonArray(Colors.Select(c => (JsonNode?)new JsonArray(c.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())).ToArray());
        if (ImagePath is not null) node["imagePath"] = ImagePath;
        node["padding"] = Padding;
        node["cornerRadius"] = CornerRadius;
        node["blur"] = Blur;
        return node;
    }

    /// <summary>A stored style, sanitised; a missing or broken text is the plain default (no backdrop).</summary>
    public static BackdropStyle Decoded(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new BackdropStyle();
        try { return TryRead(JsonNode.Parse(raw))?.Sanitized() ?? new BackdropStyle(); }
        catch (JsonException) { return new BackdropStyle(); }
    }

    /// <summary>One style from a JSON node, or null when a required part (kind, padding, cornerRadius) is missing or mistyped.</summary>
    internal static BackdropStyle? TryRead(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        if (!BeautifyJson.TryString(obj, "kind", true, out var kindText) || kindText is null) return null;
        var kind = Enum.GetValues<BackdropKind>().Cast<BackdropKind?>()
            .FirstOrDefault(k => k!.Value.ToString().ToLowerInvariant() == kindText);
        if (kind is null) return null;
        if (!BeautifyJson.TryString(obj, "presetID", false, out var preset)
            || !BeautifyJson.TryString(obj, "imagePath", false, out var path)
            || !BeautifyJson.TryNumber(obj, "padding", true, out var padding)
            || !BeautifyJson.TryNumber(obj, "cornerRadius", true, out var corner)
            || !BeautifyJson.TryNumber(obj, "blur", false, out var blur)) return null;
        double[][]? colors = null;
        if (obj["colors"] is { } colorNode)
        {
            if (colorNode is not JsonArray rows) return null;
            var list = new List<double[]>();
            foreach (var row in rows)
            {
                if (row is not JsonArray cells) return null;
                var values = new List<double>();
                foreach (var cell in cells)
                {
                    if (cell is not JsonValue value || !value.TryGetValue<double>(out var number)) return null;
                    values.Add(number);
                }
                list.Add([.. values]);
            }
            colors = [.. list];
        }
        return new BackdropStyle
        {
            Kind = kind.Value, PresetId = preset, Colors = colors, ImagePath = path,
            Padding = padding ?? 0.5, CornerRadius = corner ?? 0, Blur = blur ?? 0,
        };
    }
}

/// <summary>Reads typed members out of a JSON object, telling "absent" (fine when optional) from "mistyped" (always a failure).</summary>
internal static class BeautifyJson
{
    public static bool TryString(JsonObject obj, string key, bool required, out string? value)
    {
        value = null;
        if (obj[key] is not { } node) return !required;
        if (node is JsonValue json && json.TryGetValue<string>(out var text)) { value = text; return true; }
        return false;
    }

    public static bool TryNumber(JsonObject obj, string key, bool required, out double? value)
    {
        value = null;
        if (obj[key] is not { } node) return !required;
        if (node is JsonValue json && json.TryGetValue<double>(out var number)) { value = number; return true; }
        return false;
    }
}

/// <summary>Saved backdrop and watermark lists: capped, sanitised, lossy (an unreadable entry is skipped, not the whole list).</summary>
public static class ScreenshotPresets
{
    public const int Limit = 12;

    public static IReadOnlyList<BackdropStyle> DecodeBackdrops(string? raw) =>
        DecodeList(raw, BackdropStyle.TryRead).Select(s => s.Sanitized()).Where(s => s.Kind != BackdropKind.None).TakeLast(Limit).ToList();

    public static string EncodeBackdrops(IEnumerable<BackdropStyle> presets) =>
        EncodeList(presets.TakeLast(Limit).Select(p => p.ToNode()));

    public static IReadOnlyList<WatermarkStyle> DecodeWatermarks(string? raw) =>
        DecodeList(raw, WatermarkStyle.TryRead).Select(s => s.Sanitized()).Where(s => s.Kind != WatermarkKind.None).TakeLast(Limit).ToList();

    public static string EncodeWatermarks(IEnumerable<WatermarkStyle> presets) =>
        EncodeList(presets.TakeLast(Limit).Select(p => p.ToNode()));

    private static List<T> DecodeList<T>(string? raw, Func<JsonNode?, T?> read) where T : class
    {
        if (string.IsNullOrEmpty(raw)) return [];
        try
        {
            if (JsonNode.Parse(raw) is not JsonArray rows) return [];
            return rows.Select(read).Where(item => item is not null).Select(item => item!).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static string EncodeList(IEnumerable<JsonObject> nodes)
    {
        try { return new JsonArray(nodes.Select(n => (JsonNode?)n).ToArray()).ToJsonString(); }
        catch (ArgumentException) { return "[]"; }
    }
}

/// <summary>The numbers behind the padded, rounded, blurred backdrop export.</summary>
public static class BackdropGeometry
{
    /// <summary>Padding around the image: the margin slider (0...1), a small frame even at 0, and a floor for small captures.</summary>
    public static double Padding(SizeD imageSize, double factor)
    {
        var clamped = Math.Clamp(factor, 0, 1);
        var side = Math.Min(imageSize.Width, imageSize.Height);
        return Math.Round(Math.Max(24, side * (0.035 + 0.14 * clamped)), MidpointRounding.AwayFromZero);
    }

    /// <summary>Corner rounding of the capture card in image pixels: factor 0 is square, 1 a fifth of the short side.</summary>
    public static double CardCornerRadius(SizeD imageSize, double factor) =>
        Math.Round(Math.Clamp(factor, 0, 1) * Math.Min(imageSize.Width, imageSize.Height) * 0.2, MidpointRounding.AwayFromZero);

    /// <summary>Gaussian blur radius in output pixels, proportional to the canvas so the slider looks the same at every size.</summary>
    public static double BlurRadius(SizeD size, double factor) =>
        Math.Clamp(factor, 0, 1) * Math.Min(size.Width, size.Height) * 0.035;

    /// <summary>The gradient runs from the top-left corner to the bottom-right one.</summary>
    public static (PointD Start, PointD End) GradientEndpoints(SizeD canvas) => (new PointD(0, 0), new PointD(canvas.Width, canvas.Height));

    /// <summary>
    /// Where a backdrop picture is drawn: aspect fill, centred, like a wallpaper. The rectangle can reach past the canvas on one
    /// axis, and the canvas clips it. Null for an empty picture.
    /// </summary>
    public static RectD? AspectFill(SizeD picture, SizeD canvas)
    {
        if (picture.Width <= 0 || picture.Height <= 0) return null;
        var scale = Math.Max(canvas.Width / picture.Width, canvas.Height / picture.Height);
        var size = new SizeD(picture.Width * scale, picture.Height * scale);
        return new RectD((canvas.Width - size.Width) / 2, (canvas.Height - size.Height) / 2, size.Width, size.Height);
    }

    /// <summary>
    /// The rounding a picture already carries in its alpha, such as a window captured with its own corners, in image pixels: how
    /// far transparency reaches in along each corner's diagonal, read as the radius of the arc that meets the diagonal there. The
    /// least rounded corner wins, and an opaque picture reads 0. <paramref name="bgra"/> is 8 bit BGRA, top row first.
    /// </summary>
    public static double TransparentCornerRadius(byte[] bgra, int width, int height, int stride)
    {
        var side = Math.Min(Math.Min(width, height), 512) / 2;
        if (side < 2 || stride < width * 4 || bgra.Length < (height - 1) * stride + width * 4) return 0;
        (int X, int Y, int StepX, int StepY)[] corners =
        [
            (0, 0, 1, 1), (width - side, 0, -1, 1), (0, height - side, 1, -1), (width - side, height - side, -1, -1),
        ];
        var reach = int.MaxValue;
        foreach (var corner in corners)
        {
            var startX = corner.StepX > 0 ? 0 : side - 1;
            var startY = corner.StepY > 0 ? 0 : side - 1;
            var opaqueAt = -1;
            for (var step = 0; step < side; step++)
            {
                var x = corner.X + startX + step * corner.StepX;
                var y = corner.Y + startY + step * corner.StepY;
                if (bgra[y * stride + x * 4 + 3] > 127) { opaqueAt = step; break; }
            }
            if (opaqueAt < 0) return 0;
            reach = Math.Min(reach, opaqueAt);
        }
        // The arc crosses the diagonal at r * (1 - 1/sqrt 2) from the corner.
        return Math.Round(reach / (1 - 1 / Math.Sqrt(2)), MidpointRounding.AwayFromZero);
    }
}

/// <summary>What the exporter draws behind the card: its soft shadow (a sixth of an inch down, blurred, 40 percent black).</summary>
public readonly record struct BackdropShadow(double OffsetY, double Blur, double Alpha)
{
    public static BackdropShadow For(double scale) => new(6 * scale, 22 * scale, 0.4);
}

/// <summary>
/// The sizes of one export: the canvas with its padding, where the card sits, how far it is rounded and the file's final size.
/// This is the geometry of upstream <c>renderExport</c> without any drawing.
/// </summary>
public readonly record struct ExportLayout(
    SizeD ImageSize,
    bool HasBackdrop,
    double CornerRadius,
    double OutlineCorner,
    double Padding,
    SizeD Canvas,
    RectD Card,
    double BlurRadius,
    BackdropShadow? Shadow,
    SizeD Output,
    double OutputScale)
{
    /// <summary>
    /// Lays out an export. <paramref name="hasFill"/> is whether a backdrop fill is drawn (the style's kind may be anything; the
    /// editor resolves the fill). Without a fill the card is the whole picture and only gains transparent rounded corners.
    /// </summary>
    /// <param name="scale">Pixels per point of the capture (2 on a Retina screen).</param>
    /// <param name="downscaleTo1x">The "export at 1x" option: a scale above 1 halves the pixels and reports 1x.</param>
    /// <param name="transparentCorner">The picture's own rounding, from <see cref="BackdropGeometry.TransparentCornerRadius"/>.</param>
    public static ExportLayout Compute(SizeD imageSize, BackdropStyle style, double scale, bool hasFill, bool downscaleTo1x,
        double transparentCorner = 0)
    {
        var corner = BackdropGeometry.CardCornerRadius(imageSize, style.CornerRadius);
        SizeD canvas;
        RectD card;
        double padding = 0, blur = 0;
        BackdropShadow? shadow = null;
        if (hasFill)
        {
            padding = BackdropGeometry.Padding(imageSize, style.Padding);
            // The canvas is a whole number of pixels, cut rather than rounded.
            canvas = new SizeD(Math.Truncate(imageSize.Width + padding * 2), Math.Truncate(imageSize.Height + padding * 2));
            card = new RectD(padding, padding, imageSize.Width, imageSize.Height);
            if (style.Blur > 0) blur = BackdropGeometry.BlurRadius(canvas, style.Blur);
            shadow = BackdropShadow.For(scale);
        }
        else
        {
            canvas = imageSize;
            card = new RectD(0, 0, imageSize.Width, imageSize.Height);
        }
        var output = canvas;
        var outputScale = scale;
        if (downscaleTo1x && scale > 1)
        {
            output = ScreenshotImageRules.DownscaledSize(canvas, scale);
            outputScale = 1;
        }
        return new ExportLayout(imageSize, hasFill, corner, Math.Max(corner, transparentCorner), padding, canvas, card, blur, shadow,
            output, outputScale);
    }
}

// MARK: - Watermark

public enum WatermarkKind { None, Text, Image }

/// <summary>The nine places a mark can take, the same grid the recorder's captions use. Raw values match upstream's JSON.</summary>
public enum WatermarkAnchor { TopLeading, Top, TopTrailing, Leading, Center, Trailing, BottomLeading, Bottom, BottomTrailing }

public static class WatermarkAnchors
{
    public static string Raw(WatermarkAnchor anchor)
    {
        var name = anchor.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static WatermarkAnchor? Parse(string? raw) =>
        Enum.GetValues<WatermarkAnchor>().Cast<WatermarkAnchor?>().FirstOrDefault(a => Raw(a!.Value) == raw);

    /// <summary>Where the mark sits, 0...1 with a top-left origin, before the margin is taken off.</summary>
    public static PointD UnitPoint(this WatermarkAnchor anchor) => anchor switch
    {
        WatermarkAnchor.TopLeading => new PointD(0, 0),
        WatermarkAnchor.Top => new PointD(0.5, 0),
        WatermarkAnchor.TopTrailing => new PointD(1, 0),
        WatermarkAnchor.Leading => new PointD(0, 0.5),
        WatermarkAnchor.Center => new PointD(0.5, 0.5),
        WatermarkAnchor.Trailing => new PointD(1, 0.5),
        WatermarkAnchor.BottomLeading => new PointD(0, 1),
        WatermarkAnchor.Bottom => new PointD(0.5, 1),
        _ => new PointD(1, 1),
    };

    /// <summary>
    /// Where something of <paramref name="contentSize"/> sits on a canvas with a top-left origin (upstream returns Core Image's
    /// bottom-left space; the y here counts down from the top), with a 5 percent margin off every edge it touches.
    /// </summary>
    public static PointD Origin(this WatermarkAnchor anchor, SizeD contentSize, SizeD canvas)
    {
        var margin = Math.Min(canvas.Width, canvas.Height) * 0.05;
        var point = anchor.UnitPoint();
        return new PointD(margin + (canvas.Width - contentSize.Width - margin * 2) * point.X,
                          margin + (canvas.Height - contentSize.Height - margin * 2) * point.Y);
    }
}

/// <summary>The eight text colours a mark may use (upstream <c>ColorID</c>) with their sRGB components.</summary>
public static class WatermarkColors
{
    public static readonly IReadOnlyList<string> Names = ["red", "orange", "yellow", "green", "blue", "purple", "black", "white"];

    public static bool IsKnown(string? raw) => raw is not null && Names.Contains(raw);

    public static ColorValue Value(string? raw) => raw switch
    {
        "red" => new ColorValue(0.93, 0.26, 0.21),
        "orange" => new ColorValue(1.00, 0.58, 0.00),
        "yellow" => new ColorValue(1.00, 0.80, 0.00),
        "green" => new ColorValue(0.20, 0.78, 0.35),
        "blue" => new ColorValue(0.04, 0.52, 1.00),
        "purple" => new ColorValue(0.69, 0.32, 0.87),
        "black" => new ColorValue(0.09, 0.09, 0.11),
        _ => new ColorValue(1, 1, 1),
    };
}

/// <summary>
/// A mark of your own on every capture that leaves the editor: a line of text or a picture from disk, faded, tilted if you like
/// and set on one of nine places. Persisted as JSON like the backdrop.
/// </summary>
public sealed record WatermarkStyle
{
    public const int TextLimit = 120;
    public const double OpacityMinimum = 0.05, OpacityMaximum = 1, RotationLimit = 90;

    public WatermarkKind Kind { get; init; } = WatermarkKind.None;
    public string Text { get; init; } = "";
    /// <summary>Absolute path when the kind is an image.</summary>
    public string? ImagePath { get; init; }
    /// <summary>One of <see cref="WatermarkColors.Names"/>.</summary>
    public string Color { get; init; } = "white";
    public WatermarkAnchor Anchor { get; init; } = WatermarkAnchor.BottomTrailing;
    /// <summary>Slider 0...1; <see cref="WatermarkGeometry"/> turns it into pixels for a capture.</summary>
    public double Size { get; init; } = 0.3;
    public double Opacity { get; init; } = 0.4;
    /// <summary>Degrees, -90...90; positive tilts the mark up to the right.</summary>
    public double Rotation { get; init; }

    /// <summary>Clamps the sliders, trims the text and drops a configuration missing its content back to none.</summary>
    public WatermarkStyle Sanitized()
    {
        var trimmed = Text.Trim();
        var style = this with
        {
            Text = trimmed.Length > TextLimit ? TruncateText(trimmed) : trimmed,
            Color = WatermarkColors.IsKnown(Color) ? Color : "white",
            Size = double.IsFinite(Size) ? Math.Clamp(Size, 0, 1) : 0.3,
            Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, OpacityMinimum, OpacityMaximum) : 0.4,
            Rotation = double.IsFinite(Rotation) ? Math.Clamp(Rotation, -RotationLimit, RotationLimit) : 0,
        };
        return style.Kind switch
        {
            WatermarkKind.Text when style.Text.Length == 0 => style with { Kind = WatermarkKind.None },
            WatermarkKind.Image when string.IsNullOrEmpty(style.ImagePath) => style with { Kind = WatermarkKind.None },
            _ => style,
        };
    }

    /// <summary>The first 120 characters, counted as text elements the way Swift counts characters, so a pair is never cut apart.</summary>
    private static string TruncateText(string text)
    {
        var taken = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var end = 0;
        while (taken < TextLimit && enumerator.MoveNext())
        {
            taken++;
            end = enumerator.ElementIndex + enumerator.GetTextElement().Length;
        }
        return text[..end];
    }

    public string Encoded()
    {
        try { return ToNode().ToJsonString(); }
        catch (ArgumentException) { return ""; }
    }

    internal JsonObject ToNode()
    {
        var node = new JsonObject
        {
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["text"] = Text,
        };
        if (ImagePath is not null) node["imagePath"] = ImagePath;
        node["color"] = Color;
        node["anchor"] = WatermarkAnchors.Raw(Anchor);
        node["size"] = Size;
        node["opacity"] = Opacity;
        node["rotation"] = Rotation;
        return node;
    }

    /// <summary>A stored style, sanitised; a missing or broken text is "no watermark".</summary>
    public static WatermarkStyle Decoded(string? raw) => DecodedRaw(raw)?.Sanitized() ?? new WatermarkStyle();

    /// <summary>A stored style exactly as written (no clamping), or null when it cannot be read.</summary>
    public static WatermarkStyle? DecodedRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try { return TryRead(JsonNode.Parse(raw)); }
        catch (JsonException) { return null; }
    }

    internal static WatermarkStyle? TryRead(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        if (!BeautifyJson.TryString(obj, "kind", true, out var kindText) || kindText is null
            || !BeautifyJson.TryString(obj, "text", true, out var text) || text is null
            || !BeautifyJson.TryString(obj, "imagePath", false, out var path)
            || !BeautifyJson.TryString(obj, "color", true, out var color) || color is null
            || !BeautifyJson.TryString(obj, "anchor", true, out var anchorText)
            || !BeautifyJson.TryNumber(obj, "size", true, out var size)
            || !BeautifyJson.TryNumber(obj, "opacity", true, out var opacity)
            || !BeautifyJson.TryNumber(obj, "rotation", true, out var rotation)) return null;
        var kind = Enum.GetValues<WatermarkKind>().Cast<WatermarkKind?>().FirstOrDefault(k => k!.Value.ToString().ToLowerInvariant() == kindText);
        if (kind is null || WatermarkAnchors.Parse(anchorText) is not { } anchor) return null;
        return new WatermarkStyle
        {
            Kind = kind.Value, Text = text, ImagePath = path, Color = color, Anchor = anchor,
            Size = size ?? 0.3, Opacity = opacity ?? 0.4, Rotation = rotation ?? 0,
        };
    }
}

/// <summary>Where a mark's centre goes, and how much its content shrinks so the tilted bounds stay inside the margins.</summary>
public readonly record struct WatermarkPlacement(PointD Center, double Fit);

public static class WatermarkGeometry
{
    /// <summary>Point size of watermark text in image pixels: factor 0 is a discreet 2 percent of the short side, 1 a bold 16.</summary>
    public static double FontSize(SizeD imageSize, double factor)
    {
        var clamped = Math.Clamp(factor, 0, 1);
        return Math.Max(8, Math.Round(Math.Min(imageSize.Width, imageSize.Height) * (0.02 + 0.14 * clamped), MidpointRounding.AwayFromZero));
    }

    /// <summary>Width of a watermark picture in image pixels, from 5 percent of the capture's width up to half of it.</summary>
    public static double ImageWidth(SizeD imageSize, double factor)
    {
        var clamped = Math.Clamp(factor, 0, 1);
        return Math.Max(1, Math.Round(imageSize.Width * (0.05 + 0.45 * clamped), MidpointRounding.AwayFromZero));
    }

    /// <summary>The size of a picture mark: the width from <see cref="ImageWidth"/>, the height from the picture's own proportions.</summary>
    public static SizeD ImageContentSize(SizeD imageSize, double factor, SizeD picture) =>
        picture.Width <= 0 || picture.Height <= 0
            ? SizeD.Zero
            : new SizeD(ImageWidth(imageSize, factor), ImageWidth(imageSize, factor) * picture.Height / picture.Width);

    /// <summary>
    /// Where a mark of <paramref name="contentSize"/>, turned by <paramref name="rotation"/> degrees, sits on a capture: its tilted
    /// bounding box is what the nine places position, so a diagonal mark in a corner touches the margin instead of leaving it.
    /// The margin is 5 percent of the short side, widened to clear a rounded card's corner. Null for an empty or non-finite mark.
    /// </summary>
    public static WatermarkPlacement? Placement(SizeD contentSize, double rotation, WatermarkAnchor anchor, SizeD imageSize,
        double cornerRadius = 0)
    {
        if (!double.IsFinite(contentSize.Width) || !double.IsFinite(contentSize.Height)
            || contentSize.Width <= 0 || contentSize.Height <= 0
            || imageSize.Width <= 0 || imageSize.Height <= 0 || !double.IsFinite(rotation)) return null;
        var radians = rotation * Math.PI / 180;
        var bounds = new SizeD(
            Math.Abs(contentSize.Width * Math.Cos(radians)) + Math.Abs(contentSize.Height * Math.Sin(radians)),
            Math.Abs(contentSize.Width * Math.Sin(radians)) + Math.Abs(contentSize.Height * Math.Cos(radians)));
        var shortSide = Math.Min(imageSize.Width, imageSize.Height);
        // The inset rectangle must lie inside the rounded capture: at the diagonal of a quarter circle the inset is r * (1 - sqrt 0.5),
        // plus a pixel so antialiased edges are not clipped. Positive space stays even after cropping down to a few pixels.
        var radius = double.IsFinite(cornerRadius) ? Math.Max(0, Math.Min(shortSide / 2, cornerRadius)) : 0;
        var roundedInset = radius > 0 ? Math.Ceiling(radius * (1 - Math.Sqrt(0.5))) + 1 : 0;
        var margin = Math.Min(shortSide * 0.45, Math.Max(shortSide * 0.05, roundedInset));
        var available = new SizeD(imageSize.Width - margin * 2, imageSize.Height - margin * 2);
        var fit = Math.Min(1, Math.Min(available.Width / bounds.Width, available.Height / bounds.Height));
        var fitted = new SizeD(bounds.Width * fit, bounds.Height * fit);
        var point = anchor.UnitPoint();
        return new WatermarkPlacement(
            new PointD(margin + (available.Width - fitted.Width) * point.X + fitted.Width / 2,
                       margin + (available.Height - fitted.Height) * point.Y + fitted.Height / 2),
            fit);
    }

    /// <summary>The mark's content rectangle before it is turned: centred on the placement, scaled by its fit.</summary>
    public static RectD ContentRect(WatermarkPlacement placement, SizeD contentSize)
    {
        var width = contentSize.Width * placement.Fit;
        var height = contentSize.Height * placement.Fit;
        return new RectD(placement.Center.X - width / 2, placement.Center.Y - height / 2, width, height);
    }
}

/// <summary>
/// What of a watermark may travel in a settings backup. A picture is a private file on one PC, so a backup keeps the look and
/// never grants a path; only a choice already made locally can supply an image.
/// </summary>
public static class WatermarkPortability
{
    /// <summary>A style safe to export: the same look with no picture path (an unreadable style is the plain default).</summary>
    public static string PortableStyle(string? raw) =>
        ((WatermarkStyle.DecodedRaw(raw) ?? new WatermarkStyle()) with { ImagePath = null }).Encoded();

    /// <summary>Presets safe to export: picture presets are left out and text presets lose any hidden path.</summary>
    public static string PortablePresets(string? raw) =>
        ScreenshotPresets.EncodeWatermarks(ScreenshotPresets.DecodeWatermarks(raw)
            .Where(style => style.Kind != WatermarkKind.Image).Select(style => style with { ImagePath = null }));

    /// <summary>The restored style with the picture this PC already has chosen, if any.</summary>
    public static string RestoredStyle(string? restored, string? local)
    {
        var style = WatermarkStyle.DecodedRaw(restored) ?? new WatermarkStyle();
        return (style with { ImagePath = WatermarkStyle.Decoded(local).ImagePath }).Sanitized().Encoded();
    }

    /// <summary>Text presets from the backup, with this PC's picture presets reserved first so a restore never evicts one.</summary>
    public static string RestoredPresets(string? restored, string? local)
    {
        var pictures = ScreenshotPresets.DecodeWatermarks(local).Where(style => style.Kind == WatermarkKind.Image).ToList();
        var text = ScreenshotPresets.DecodeWatermarks(restored).Where(style => style.Kind == WatermarkKind.Text)
            .Select(style => style with { ImagePath = null }).ToList();
        return ScreenshotPresets.EncodeWatermarks(text.TakeLast(Math.Max(0, ScreenshotPresets.Limit - pictures.Count)).Concat(pictures));
    }
}

/// <summary>The backdrop counterpart of <see cref="WatermarkPortability"/>: a picture backdrop is a private file, so a backup drops it.</summary>
public static class BackdropPortability
{
    public static string PortableStyle(string? raw)
    {
        var style = BackdropStyle.Decoded(raw);
        return (style.Kind == BackdropKind.Image ? new BackdropStyle() : style).Encoded();
    }

    public static string PortablePresets(string? raw) =>
        ScreenshotPresets.EncodeBackdrops(ScreenshotPresets.DecodeBackdrops(raw).Where(style => style.Kind != BackdropKind.Image));

    /// <summary>The restored look; a picture backdrop this PC already has stays as it is.</summary>
    public static string RestoredStyle(string? restored, string? local)
    {
        var style = BackdropStyle.Decoded(restored);
        return style.Kind == BackdropKind.Image ? BackdropStyle.Decoded(local).Encoded() : style.Encoded();
    }

    /// <summary>Presets from the backup, with this PC's picture presets kept first so a restore never evicts one.</summary>
    public static string RestoredPresets(string? restored, string? local)
    {
        var pictures = ScreenshotPresets.DecodeBackdrops(local).Where(style => style.Kind == BackdropKind.Image).ToList();
        var others = ScreenshotPresets.DecodeBackdrops(restored).Where(style => style.Kind != BackdropKind.Image).ToList();
        return ScreenshotPresets.EncodeBackdrops(others.TakeLast(Math.Max(0, ScreenshotPresets.Limit - pictures.Count)).Concat(pictures));
    }
}
