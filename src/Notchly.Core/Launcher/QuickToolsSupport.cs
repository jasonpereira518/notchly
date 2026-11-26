// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the quick launcher parts of upstream Sources/Vorssaint/Services/QuickTools/QuickToolsSupport.swift
// (GridDirection, GridFlow, gridIndex, hiddenIDs, serializeHiddenIDs) and Defaults.sanitizedPanelItemOrder
// (Core/Defaults.swift).
namespace Notchly.Core.Launcher;

public enum GridDirection { Up, Down, Left, Right }

public enum GridFlowKind { Rows, Columns }

/// <summary>
/// Which way a grid fills: row by row with a fixed column count (<see cref="ByRows"/>), or column by column with a
/// fixed row count (<see cref="ByColumns"/>), as a rail in the island does. Equal when both parts are.
/// </summary>
/// <param name="Count">The fixed count: columns for <see cref="GridFlowKind.Rows"/>, rows for <see cref="GridFlowKind.Columns"/>.</param>
public readonly record struct GridFlow(GridFlowKind Kind, int Count)
{
    public static GridFlow ByRows(int columns) => new(GridFlowKind.Rows, columns);
    public static GridFlow ByColumns(int rows) => new(GridFlowKind.Columns, rows);
}

public static class QuickToolsSupport
{
    /// <summary>
    /// Keyboard navigation over a row-major grid: arrows move by one cell, clamped to the existing items (no
    /// wrapping, so the selection never jumps from one edge to the other).
    /// </summary>
    public static int GridIndex(int index, int count, int columns, GridDirection direction)
    {
        if (count <= 0 || columns <= 0) return 0;
        var current = Math.Min(Math.Max(index, 0), count - 1);
        var candidate = direction switch
        {
            GridDirection.Left => current - 1,
            GridDirection.Right => current + 1,
            GridDirection.Up => current - columns,
            _ => current + columns,
        };
        if (candidate < 0 || candidate >= count) return current;
        if (direction == GridDirection.Left && current % columns == 0) return current;
        if (direction == GridDirection.Right && current % columns == columns - 1) return current;
        return candidate;
    }

    /// <summary>A column-major rail is the row-major walk with the axes swapped.</summary>
    public static int GridIndex(int index, int count, GridFlow flow, GridDirection direction)
    {
        if (flow.Kind == GridFlowKind.Rows) return GridIndex(index, count, flow.Count, direction);
        var transposed = direction switch
        {
            GridDirection.Up => GridDirection.Left,
            GridDirection.Down => GridDirection.Right,
            GridDirection.Left => GridDirection.Up,
            _ => GridDirection.Down,
        };
        return GridIndex(index, count, Math.Max(1, flow.Count), transposed);
    }

    /// <summary>The launcher's hidden-item set travels as a comma-joined string.</summary>
    public static HashSet<string> HiddenIds(string? raw) =>
        new((raw ?? "").Split(',').Where(s => s.Length > 0), StringComparer.Ordinal);

    public static string SerializeHiddenIds(IEnumerable<string> ids) =>
        string.Join(",", ids.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// A saved item order made valid: saved ids that exist come first, once each, in saved order; any default id
    /// the saved order lacks is appended in default order (so a tile added later joins at the end).
    /// </summary>
    public static IReadOnlyList<string> SanitizedPanelItemOrder(string? raw, IReadOnlyList<string> defaultOrder)
    {
        var allowed = new HashSet<string>(defaultOrder, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in (raw ?? "").Split(','))
        {
            if (id.Length > 0 && allowed.Contains(id) && seen.Add(id)) result.Add(id);
        }
        foreach (var id in defaultOrder)
        {
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>The launcher's tiles in the user's saved order (unknown ids dropped, new tiles at the end).</summary>
    public static IReadOnlyList<QuickLauncherItem> OrderedItems(string? raw) =>
        [.. SanitizedPanelItemOrder(raw, QuickLauncherItems.DefaultOrder).Select(id => QuickLauncherItems.FromId(id)!.Value)];

    public static string SerializeItemOrder(IEnumerable<QuickLauncherItem> items) => string.Join(",", items.Select(QuickLauncherItems.Id));
}
