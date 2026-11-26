// SPDX-License-Identifier: GPL-3.0-or-later
// Port of ScreenshotSupport.Tool (upstream Services/QuickTools/ScreenshotSupport.swift): the editor's tool list, its
// default rail order, sanitising of a saved order and the digit shortcut mapping (keys 1-9 follow the rail order).
// Remappable per-tool keys live in ToolShortcuts.cs.
namespace Notchly.Core.Capture;

public enum ScreenshotTool
{
    // Case order is the default rail order and therefore the default mapping for keys 1 through 9. Put the common
    // actions first.
    Select, Arrow, Pixelate, Crop, Text, Sticker, Rect, Highlight,
    Freehand, Line, Ellipse, Counter, Redact, Spotlight, ExpandCanvas, Image,
}

public static class ScreenshotTools
{
    public const int ShortcutLimit = 9;

    /// <summary>Upstream raw value ("expandCanvas"); this is what the saved tool order and the last tool store.</summary>
    public static string Raw(this ScreenshotTool tool) => EnumRaw.Of(tool);

    public static ScreenshotTool? FromRaw(string? raw) => EnumRaw.Parse<ScreenshotTool>(raw);

    public static IReadOnlyList<ScreenshotTool> All { get; } = Enum.GetValues<ScreenshotTool>();

    /// <summary>Content-only image annotations never appear in the tool rail.</summary>
    public static IReadOnlyList<ScreenshotTool> EditorTools { get; } =
        Enum.GetValues<ScreenshotTool>().Where(t => t != ScreenshotTool.Image).ToArray();

    /// <summary>The default value of the tool order setting: every rail tool in canonical order.</summary>
    public static string DefaultOrderStorage { get; } = string.Join(",", EditorTools.Select(Raw));

    /// <summary>Tools that create an annotation by dragging a rectangle.</summary>
    public static bool DragsRect(this ScreenshotTool tool) => tool is ScreenshotTool.Rect or ScreenshotTool.Ellipse
        or ScreenshotTool.Highlight or ScreenshotTool.Pixelate or ScreenshotTool.Redact or ScreenshotTool.Spotlight;

    /// <summary>Existing rectangular marks with visible resize grips.</summary>
    public static bool ResizesWithHandles(this ScreenshotTool tool) =>
        tool.DragsRect() || tool is ScreenshotTool.Sticker or ScreenshotTool.Image;

    /// <summary>
    /// The tool a new editor opens with. Select, crop and expand canvas act on the capture as a whole and an image is
    /// never picked by hand, so each falls back to arrow and the editor opens ready to draw.
    /// </summary>
    public static ScreenshotTool StartingTool(string? remembered)
    {
        var tool = FromRaw(remembered) ?? ScreenshotTool.Arrow;
        return tool is ScreenshotTool.Select or ScreenshotTool.Crop or ScreenshotTool.ExpandCanvas or ScreenshotTool.Image
            ? ScreenshotTool.Arrow
            : tool;
    }

    /// <summary>
    /// Saved ids first, then any tools added by a later version in the canonical order. Invalid and duplicate ids never
    /// reach the UI.
    /// </summary>
    public static List<ScreenshotTool> Ordered(string? raw)
    {
        var seen = new HashSet<ScreenshotTool>();
        var result = new List<ScreenshotTool>();
        foreach (var id in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (FromRaw(id) is { } tool && tool != ScreenshotTool.Image && seen.Add(tool)) result.Add(tool);
        }
        foreach (var tool in EditorTools)
        {
            if (seen.Add(tool)) result.Add(tool);
        }
        return result;
    }

    public static string OrderStorage(IEnumerable<ScreenshotTool> order) => string.Join(",", order.Select(Raw));

    /// <summary>The tool a digit key (1 through 9) selects, or null when shortcuts are off or the slot is empty.</summary>
    public static ScreenshotTool? ShortcutTool(int number, string? orderRaw, bool enabled)
    {
        if (!enabled || number < 1 || number > ShortcutLimit) return null;
        var order = Ordered(orderRaw);
        int index = number - 1;
        return index < order.Count ? order[index] : null;
    }

    /// <summary>The digit a tool answers to, or null past the ninth slot.</summary>
    public static int? ShortcutNumber(ScreenshotTool tool, string? orderRaw, bool enabled)
    {
        if (!enabled) return null;
        int index = Ordered(orderRaw).IndexOf(tool);
        return index >= 0 && index < ShortcutLimit ? index + 1 : null;
    }

    /// <summary>
    /// Assigning a number is the same operation as moving the tool into that numbered rail slot. Choosing no shortcut
    /// moves it just below the first nine, where it remains available without a key.
    /// </summary>
    public static List<ScreenshotTool> AssigningShortcut(int? number, ScreenshotTool tool, string? orderRaw)
    {
        var order = Ordered(orderRaw);
        int current = order.IndexOf(tool);
        if (current < 0) return order;
        if (number is null && current >= ShortcutLimit) return order;
        order.RemoveAt(current);
        int destination = number is { } n && n >= 1 && n <= ShortcutLimit
            ? Math.Min(n - 1, order.Count)
            : Math.Min(ShortcutLimit, order.Count);
        order.Insert(destination, tool);
        return order;
    }
}
