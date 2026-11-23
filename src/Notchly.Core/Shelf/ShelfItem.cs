// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Shelf/ShelfService.swift (Item, titles, piles) and
// ShelfSupport.swift (ShelfPersistedItem): what the shelf holds. A pile ("batch") nests other items.
namespace Notchly.Core.Shelf;

public enum ShelfKind { File, Text, Link, Batch }

public sealed record ShelfItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ShelfKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string? Path { get; init; }
    public string? Text { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<ShelfItem> Children { get; init; } = [];
    /// <summary>A picture the shelf wrote itself from dropped image data.</summary>
    public bool IsImage { get; init; }

    public int LeafCount => Kind == ShelfKind.Batch ? Children.Sum(c => c.LeafCount) : 1;

    public IEnumerable<ShelfItem> Leaves() =>
        Kind == ShelfKind.Batch ? Children.SelectMany(c => c.Leaves()) : [this];

    public IEnumerable<Guid> AllIds() => new[] { Id }.Concat(Children.SelectMany(c => c.AllIds()));

    // Equality by id only, as upstream; content comparison is explicit.
    public bool Equals(ShelfItem? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();

    public static ShelfItem File(string path, bool isImage = false, string? title = null) => new()
    {
        Kind = ShelfKind.File,
        Path = path,
        Title = title ?? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)),
        IsImage = isImage,
    };

    /// <summary>The first line, at most 48 characters; null for blank text.</summary>
    public static ShelfItem? FromText(string text)
    {
        if (ShelfPersistence.BoundedLiveText(text) is not { } bounded) return null;
        var trimmed = bounded.Trim();
        var firstLine = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
        return new ShelfItem { Kind = ShelfKind.Text, Text = bounded, Title = firstLine.Length > 48 ? firstLine[..48] : firstLine };
    }

    /// <summary>A link shows its host, or the whole address when it has none.</summary>
    public static ShelfItem? FromLink(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IsFile) return null;
        return new ShelfItem { Kind = ShelfKind.Link, Url = uri.OriginalString, Title = uri.Host.Length > 0 ? uri.Host : uri.OriginalString };
    }

    /// <summary>A pile is titled by its first item and how many more it holds.</summary>
    public static ShelfItem Batch(IReadOnlyList<ShelfItem> children, Guid? id = null)
    {
        var total = children.Sum(c => c.LeafCount);
        return new ShelfItem
        {
            Id = id ?? Guid.NewGuid(),
            Kind = ShelfKind.Batch,
            Children = children,
            Title = children.Count > 0 ? $"{children[0].Title} +{Math.Max(0, total - 1)}" : "",
        };
    }
}
