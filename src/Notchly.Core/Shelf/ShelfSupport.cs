// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream ShelfSupport.swift (ShelfSelectionSupport, ShelfTileLayout,
// ShelfInteractionSupport, ShelfTooltipSupport) and NotchFileToolsSupport.swift (ZIP rules),
// plus the drop-reading priority of ShelfService.swift as a pure function.
namespace Notchly.Core.Shelf;

public readonly record struct TileFrame(double X, double Y, double Width, double Height);

public static class ShelfInteraction
{
    /// <summary>Escape clears the selection only when pressed on its own.</summary>
    public static bool IsClearSelectionShortcut(bool isEscape, bool hasModifiers) => isEscape && !hasModifiers;

    /// <summary>From the last tile touched to the clicked one, inclusive, in either direction.</summary>
    public static IReadOnlyList<T> RangeSelection<T>(IReadOnlyList<T> all, T? anchor, T target) where T : struct
    {
        var targetIndex = IndexOf(all, target);
        if (targetIndex < 0) return [];
        var anchorIndex = anchor is { } value ? IndexOf(all, value) : -1;
        if (anchorIndex < 0) anchorIndex = targetIndex;
        var (low, high) = (Math.Min(anchorIndex, targetIndex), Math.Max(anchorIndex, targetIndex));
        return all.Skip(low).Take(high - low + 1).ToList();
    }

    private static int IndexOf<T>(IReadOnlyList<T> all, T value)
    {
        for (var i = 0; i < all.Count; i++) if (EqualityComparer<T>.Default.Equals(all[i], value)) return i;
        return -1;
    }

    /// <summary>A drag that really left the shelf can close it; cancelled drags and pins never do.</summary>
    public static bool ShouldCloseAfterDrag(bool dropAccepted, int draggedItemCount, bool closeAfterDrop, bool pinned) =>
        dropAccepted && draggedItemCount > 0 && closeAfterDrop && !pinned;

    public static bool ShouldRemoveAfterDrag(bool dropAccepted, int draggedItemCount, bool removeAfterDrop) =>
        dropAccepted && draggedItemCount > 0 && removeAfterDrop;

    /// <summary>Pinned items, and anything inside a pinned pile, stay after a drop.</summary>
    public static IReadOnlyList<Guid> RemovableAfterDrag(IEnumerable<Guid> dragged, IReadOnlySet<Guid> protectedIds) =>
        dragged.Where(id => !protectedIds.Contains(id)).ToList();

    /// <summary>A destination is offered a move only when the dragged items may leave the shelf.</summary>
    public static bool OffersMoveOutside(bool removeAfterDrop, bool dragIncludesPinned) =>
        removeAfterDrop && !dragIncludesPinned;
}

public static class ShelfLayout
{
    public const double TileWidth = 78, TileHeight = 88, Spacing = 10, Inset = 4;

    public static int ColumnCount(double contentWidth, double tileWidth = TileWidth, double spacing = Spacing, double inset = Inset) =>
        Math.Max(1, (int)((contentWidth - inset * 2 + spacing) / (tileWidth + spacing)));

    public static int RowCount(double contentHeight, double tileHeight = TileHeight, double spacing = Spacing, double inset = Inset) =>
        Math.Max(1, (int)((contentHeight - inset * 2 + spacing) / (tileHeight + spacing)));

    /// <summary>Tiles fill each column top to bottom, then continue to the right.</summary>
    public static TileFrame SidewaysTileFrame(int index, int rows, double tileWidth = TileWidth, double tileHeight = TileHeight,
                                              double spacing = Spacing, double inset = Inset)
    {
        var safeRows = Math.Max(1, rows);
        var column = index / safeRows;
        var row = index % safeRows;
        return new TileFrame(inset + column * (tileWidth + spacing), inset + row * (tileHeight + spacing), tileWidth, tileHeight);
    }

    public static (double Width, double Height) SidewaysDocumentSize(int itemCount, int rows, double visibleWidth = 0, double visibleHeight = 0,
                                                                     double tileWidth = TileWidth, double tileHeight = TileHeight,
                                                                     double spacing = Spacing, double inset = Inset)
    {
        var safeRows = Math.Max(1, rows);
        var columns = Math.Max(1, (int)Math.Ceiling((double)itemCount / safeRows));
        var filledRows = Math.Min(safeRows, Math.Max(1, itemCount));
        var width = inset * 2 + columns * tileWidth + (columns - 1) * spacing;
        var height = inset * 2 + filledRows * tileHeight + (filledRows - 1) * spacing;
        return (Math.Max(width, visibleWidth), Math.Max(height, visibleHeight));
    }

    public static TileFrame GridTileFrame(int index, int columns, double tileWidth = TileWidth, double tileHeight = TileHeight,
                                          double spacing = Spacing, double inset = Inset)
    {
        var safeColumns = Math.Max(1, columns);
        return new TileFrame(inset + index % safeColumns * (tileWidth + spacing), inset + index / safeColumns * (tileHeight + spacing),
                             tileWidth, tileHeight);
    }
}

public static class ShelfTooltip
{
    public const int TextCap = 500;

    public static string ForFile(string title, string? kind) => string.IsNullOrEmpty(kind) ? title : $"{title}\n{kind}";

    public static string ForText(string text, int cap = TextCap)
    {
        var trimmed = text.Trim();
        return trimmed.Length > cap ? trimmed[..cap] + "…" : trimmed;
    }

    public static string ForLink(string url, int cap = TextCap) => url.Length > cap ? url[..cap] + "…" : url;

    /// <summary>"3 images, 2 files and 1 link" for a pile's leaves (English).</summary>
    public static string ForPile(IEnumerable<ShelfItem> leaves)
    {
        var list = leaves.ToList();
        var parts = new List<string>();
        void Part(int count, string one, string many) { if (count > 0) parts.Add($"{count} {(count == 1 ? one : many)}"); }
        Part(list.Count(l => l.Kind == ShelfKind.File && l.IsImage), "image", "images");
        Part(list.Count(l => l.Kind == ShelfKind.File && !l.IsImage), "file", "files");
        Part(list.Count(l => l.Kind == ShelfKind.Text), "note", "notes");
        Part(list.Count(l => l.Kind == ShelfKind.Link), "link", "links");
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}

/// <summary>What one drop carried, already read from the platform's drag data.</summary>
public sealed record ShelfDropContent(
    IReadOnlyList<string> Files,
    string? ImageFile = null,
    string? Url = null,
    string? Text = null);

public static class ShelfDrop
{
    /// <summary>
    /// Upstream priority for one entry: files, then an image (already written to the store), then
    /// a non-file URL as a link, then text. Text that only repeats the link is not added twice.
    /// </summary>
    public static IReadOnlyList<ShelfItem> Items(ShelfDropContent content)
    {
        if (content.Files.Count > 0) return content.Files.Select(f => ShelfItem.File(f)).ToList();
        if (content.ImageFile is { } image) return [ShelfItem.File(image, isImage: true, title: "Image")];
        if (content.Url is { } url && ShelfItem.FromLink(url) is { } link) return [link];
        if (content.Text is { } text)
        {
            if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme is "http" or "https"
                && ShelfItem.FromLink(text) is { } textLink) return [textLink];
            if (ShelfItem.FromText(text) is { } note) return [note];
        }
        return [];
    }
}

public static class ArchiveSupport
{
    /// <summary>"name.zip", or "name 2.zip" and onwards when taken.</summary>
    public static string UniqueArchiveName(string inputPath, Func<string, bool> exists, string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputPath));
        var stem = Directory.Exists(inputPath) || Path.GetExtension(name).Length == 0 ? name : Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0) stem = "Archive";
        var candidate = Path.Combine(folder, stem + ".zip");
        for (var n = 2; exists(candidate); n++) candidate = Path.Combine(folder, $"{stem} {n}.zip");
        return candidate;
    }

    /// <summary>
    /// An archive must not be written inside one of its inputs (it would try to contain itself).
    /// Paths compare case-insensitively after symlinks are resolved by the caller.
    /// </summary>
    public static bool DestinationIsOutsideInputs(string destination, IEnumerable<string> inputs)
    {
        var target = ShelfStore.NormalizePath(destination);
        foreach (var input in inputs)
        {
            var root = ShelfStore.NormalizePath(input);
            if (target.Equals(root, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Only local files can be archived: no URLs, no UNC-less remote schemes.</summary>
    public static bool IsArchivableInput(string path) =>
        Path.IsPathFullyQualified(path) && !path.Contains("://", StringComparison.Ordinal);
}
