// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the launcher's measurements in upstream Sources/Vorssaint/Services/QuickTools/QuickLauncherService.swift
// (columns, panel size), UI/QuickLauncher/QuickLauncherView.swift (padding, spacing), and the rail helpers of
// Services/Notch/NotchSupport.swift (NotchLayout.railCapacity, railRows, railHeight, railColumns, railFits, railGap,
// toolTitleInset; NotchGeometry.toolRows and toolFlow; NotchSupport.toolColumns).
using Notchly.Core.Notch;

namespace Notchly.Core.Launcher;

/// <summary>The launcher's measurements, in device-independent pixels.</summary>
public static class QuickLauncherLayout
{
    /// <summary>Columns of the floating panel's grid.</summary>
    public const int Columns = 3;
    public const double PanelWidth = 420;
    public const double Padding = 16;
    public const double Spacing = 10;
    /// <summary>A tile on the island's rail.</summary>
    public const double RailTileWidth = 76;
    public const double RailTileHeight = 72;
    public const double RailSpacing = 6;
    /// <summary>
    /// Tiles that would just miss the island's width close their gaps, down to none, before their rail scrolls,
    /// so the narrowest island holds four across (upstream toolLeastSpacing).
    /// </summary>
    public const double RailLeastSpacing = 0;
    /// <summary>Columns of the island's grid in edit mode (upstream NotchSupport.toolColumns).</summary>
    public const int EditColumns = 5;
}

/// <summary>
/// Rails: items fill the rows a height allows and continue sideways. Every page and the geometry that sizes it
/// share these functions, so the window never reserves a row the page cannot draw.
/// </summary>
public static class RailLayout
{
    public static int Capacity(double width, double itemWidth, double spacing)
    {
        if (!double.IsFinite(width) || itemWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((width + spacing) / (itemWidth + spacing)));
    }

    public static int Rows(int count, int perRow, double rowHeight, double spacing, double height)
    {
        var capacity = Math.Max(1, perRow);
        var needed = Math.Max(1, (Math.Max(0, count) + capacity - 1) / capacity);
        if (rowHeight <= 0) return 1;
        if (!double.IsFinite(height)) return height > 0 ? needed : 1;
        var fitting = Math.Max(1, (int)Math.Floor((height + spacing) / (rowHeight + spacing)));
        return Math.Min(needed, fitting);
    }

    public static double Height(int rows, double rowHeight, double spacing) =>
        Math.Max(1, rows) * rowHeight + Math.Max(0, rows - 1) * spacing;

    /// <summary>
    /// The columns a rail spreads its items over. A rail whose columns all fit lays the items out in reading order,
    /// this many per row, and centers a short last row; one that scrolls fills its columns instead.
    /// </summary>
    public static int Columns(int count, int rows) => (Math.Max(0, count) + Math.Max(1, rows) - 1) / Math.Max(1, rows);

    public static bool Fits(int columns, double itemWidth, double spacing, double width) =>
        columns * itemWidth + Math.Max(0, columns - 1) * spacing <= width;

    /// <summary>
    /// The room between the columns of a rail that fits: its own spacing, or what is left where that would just
    /// miss the width, down to <paramref name="least"/>, so the rail closes its gaps before it scrolls. Null once
    /// the columns would not fit even then.
    /// </summary>
    public static double? Gap(int columns, double itemWidth, double spacing, double least, double width)
    {
        if (Fits(columns, itemWidth, spacing, width)) return spacing;
        if (columns <= 1 || !Fits(columns, itemWidth, least, width)) return null;
        return (width - columns * itemWidth) / (columns - 1);
    }

    /// <summary>
    /// A tool's title spans its cell, so where the rail closes its gaps each title gives up half of the closed gap
    /// on either side: neighbouring titles stay as far apart as the rail's spacing keeps them elsewhere. Zero where
    /// the rail keeps its gaps or scrolls.
    /// </summary>
    public static double ToolTitleInset(int count, int rows, double width)
    {
        var gap = Gap(Columns(count, rows), QuickLauncherLayout.RailTileWidth, QuickLauncherLayout.RailSpacing,
                      QuickLauncherLayout.RailLeastSpacing, width);
        return Math.Max(0, QuickLauncherLayout.RailSpacing - (gap ?? QuickLauncherLayout.RailSpacing)) / 2;
    }

    /// <summary>The rows the launcher's rail draws in a content area of this width and height.</summary>
    public static int ToolRows(int count, double contentWidth, double contentBudget) =>
        Rows(count, Capacity(contentWidth, QuickLauncherLayout.RailTileWidth, QuickLauncherLayout.RailSpacing),
             QuickLauncherLayout.RailTileHeight, QuickLauncherLayout.RailSpacing, contentBudget);

    /// <summary>
    /// The arrows walk the tiles the way the rail draws them: reading order while every column fits, closing its
    /// gaps if it must, the columns it fills once it scrolls.
    /// </summary>
    public static GridFlow ToolFlow(int count, double contentWidth, double contentBudget)
    {
        var rows = ToolRows(count, contentWidth, contentBudget);
        var columns = Columns(count, rows);
        return Gap(columns, QuickLauncherLayout.RailTileWidth, QuickLauncherLayout.RailSpacing,
                   QuickLauncherLayout.RailLeastSpacing, contentWidth) is not null
            ? GridFlow.ByRows(columns) : GridFlow.ByColumns(rows);
    }

    public static int ToolRows(this NotchGeometry geometry, int count) =>
        ToolRows(count, geometry.ContentWidth, geometry.ContentBudget);

    public static GridFlow ToolFlow(this NotchGeometry geometry, int count) =>
        ToolFlow(count, geometry.ContentWidth, geometry.ContentBudget);
}
