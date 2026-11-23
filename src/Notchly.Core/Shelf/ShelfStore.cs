// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the item-tree logic in upstream ShelfService.swift: adding drops, removing items
// (piles dissolve or disappear), Clear all keeping pinned items, expanded piles, selection
// and range selection, and conversion to and from the saved form.
namespace Notchly.Core.Shelf;

public sealed class ShelfStore
{
    private readonly List<ShelfItem> _items = [];
    private readonly HashSet<Guid> _pinned = [];
    private readonly HashSet<Guid> _expanded = [];
    private readonly HashSet<Guid> _selection = [];
    private Guid? _anchor;

    public IReadOnlyList<ShelfItem> Items => _items;
    public IReadOnlySet<Guid> Pinned => _pinned;
    public IReadOnlySet<Guid> Expanded => _expanded;
    public IReadOnlySet<Guid> Selection => _selection;
    public int LeafCount => _items.Sum(i => i.LeafCount);
    public int AddSerial { get; private set; }

    // MARK: Adding

    /// <summary>
    /// One drop: deduplicated by path, becoming a single item or a pile. Refused past the
    /// 200-leaf cap. Returns the item added, or null.
    /// </summary>
    public ShelfItem? Add(IReadOnlyList<ShelfItem> dropped)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = dropped.Where(i => i.Kind != ShelfKind.File || i.Path is null || seen.Add(NormalizePath(i.Path))).ToList();
        if (unique.Count == 0) return null;
        var item = unique.Count == 1 ? unique[0] : ShelfItem.Batch(unique);
        if (!ShelfPersistence.CanAdd(LeafCount, item.LeafCount)) return null;
        _items.Add(item);
        AddSerial++;
        return item;
    }

    public static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    // MARK: Removing

    /// <summary>Removes items; returns what left, so files the shelf wrote itself can be retired.</summary>
    public IReadOnlyList<ShelfItem> Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = new List<ShelfItem>();
        var kept = RemoveFrom(_items, set, removed);
        _items.Clear();
        _items.AddRange(kept);
        var live = _items.SelectMany(i => i.AllIds()).ToHashSet();
        _selection.IntersectWith(live);
        _pinned.IntersectWith(live);
        _expanded.IntersectWith(live);
        if (_anchor is { } anchor && !live.Contains(anchor)) _anchor = null;
        return removed;
    }

    private List<ShelfItem> RemoveFrom(IReadOnlyList<ShelfItem> items, HashSet<Guid> ids, List<ShelfItem> removed)
    {
        var kept = new List<ShelfItem>();
        foreach (var item in items)
        {
            if (ids.Contains(item.Id)) { removed.Add(item); continue; }
            if (item.Kind != ShelfKind.Batch) { kept.Add(item); continue; }
            var children = RemoveFrom(item.Children, ids, removed);
            if (children.Count == 0) { _expanded.Remove(item.Id); continue; }
            if (children.Count == 1)
            {
                _expanded.Remove(item.Id);
                // The pile dissolves into its last item, which keeps the pile's pin.
                if (_pinned.Contains(item.Id)) _pinned.Add(children[0].Id);
                kept.Add(children[0]);
                continue;
            }
            kept.Add(children.Count == item.Children.Count ? item : ShelfItem.Batch(children, item.Id));
        }
        return kept;
    }

    /// <summary>Pinned items and everything nested in a pinned pile.</summary>
    public IReadOnlySet<Guid> ProtectedIds
    {
        get
        {
            var result = new HashSet<Guid>(_pinned);
            foreach (var item in Flatten(_items).Where(i => _pinned.Contains(i.Id))) result.UnionWith(item.AllIds());
            return result;
        }
    }

    /// <summary>Clears everything except pinned items, as clipboard history keeps its pins.</summary>
    public IReadOnlyList<ShelfItem> ClearAll()
    {
        var protectedIds = ProtectedIds;
        if (protectedIds.Count == 0)
        {
            var all = _items.ToList();
            _items.Clear();
            _selection.Clear();
            _expanded.Clear();
            _anchor = null;
            return all;
        }
        var removable = _items.SelectMany(i => i.Leaves()).Select(l => l.Id).Where(id => !protectedIds.Contains(id)).ToList();
        return removable.Count == 0 ? [] : Remove(removable);
    }

    // MARK: Pins, piles, selection

    public void TogglePin(Guid id)
    {
        if (Find(id) is null) return;
        if (!_pinned.Remove(id)) _pinned.Add(id);
    }

    public void ToggleExpanded(Guid id)
    {
        if (Find(id) is not { Kind: ShelfKind.Batch }) return;
        if (!_expanded.Remove(id)) _expanded.Add(id);
    }

    public void ToggleSelection(Guid id)
    {
        if (!_selection.Remove(id)) _selection.Add(id);
        _anchor = id;
    }

    /// <summary>Adds the visible range from the last tile touched to this one.</summary>
    public void ExtendSelection(Guid id)
    {
        var range = ShelfInteraction.RangeSelection(Visible.Select(i => i.Id).ToList(), _anchor, id);
        if (range.Count == 0) return;
        _selection.UnionWith(range);
        _anchor = id;
    }

    public void ClearSelection()
    {
        _selection.Clear();
        _anchor = null;
    }

    /// <summary>Top-level items, with each expanded pile's children right after it.</summary>
    public IReadOnlyList<ShelfItem> Visible => VisibleIn(_items);

    private List<ShelfItem> VisibleIn(IEnumerable<ShelfItem> items)
    {
        var result = new List<ShelfItem>();
        foreach (var item in items)
        {
            result.Add(item);
            if (item.Kind == ShelfKind.Batch && _expanded.Contains(item.Id)) result.AddRange(VisibleIn(item.Children));
        }
        return result;
    }

    /// <summary>What an action works on: the selection if there is one, otherwise everything.</summary>
    public IReadOnlyList<ShelfItem> ActionTargets =>
        _selection.Count > 0 ? Flatten(_items).Where(i => _selection.Contains(i.Id)).ToList() : _items;

    /// <summary>The leaves of some items, each once, in order.</summary>
    public static IReadOnlyList<ShelfItem> LeavesOf(IEnumerable<ShelfItem> items)
    {
        var seen = new HashSet<Guid>();
        return items.SelectMany(i => i.Leaves()).Where(l => seen.Add(l.Id)).ToList();
    }

    public ShelfItem? Find(Guid id) => Flatten(_items).FirstOrDefault(i => i.Id == id);

    private static IEnumerable<ShelfItem> Flatten(IEnumerable<ShelfItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children)));

    // MARK: Saved form

    public IReadOnlyList<ShelfPersistedItem> ToPersisted() => _items.Select(Persist).ToList();

    private ShelfPersistedItem Persist(ShelfItem item) => new(
        item.Id, item.Kind, item.Title, item.Text, item.Url, item.Path,
        item.Kind == ShelfKind.Batch ? item.Children.Select(Persist).ToList() : null,
        _pinned.Contains(item.Id));

    /// <summary>Restored items go first; anything added meanwhile follows.</summary>
    public void Restore(IReadOnlyList<ShelfPersistedItem> saved, Func<string, bool> isImagePath)
    {
        var restored = saved.Select(s => Live(s, isImagePath)).ToList();
        _items.InsertRange(0, restored);
    }

    private ShelfItem Live(ShelfPersistedItem saved, Func<string, bool> isImagePath)
    {
        if (saved.Pinned) _pinned.Add(saved.Id);
        return saved.Kind switch
        {
            ShelfKind.Batch => ShelfItem.Batch((saved.Children ?? []).Select(c => Live(c, isImagePath)).ToList(), saved.Id),
            ShelfKind.File => new ShelfItem { Id = saved.Id, Kind = ShelfKind.File, Title = saved.Title, Path = saved.Path, IsImage = saved.Path is { } p && isImagePath(p) },
            _ => new ShelfItem { Id = saved.Id, Kind = saved.Kind, Title = saved.Title, Text = saved.Text, Url = saved.Url },
        };
    }
}
