// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/ShelfFeatureTests.swift, ShelfDragCompletionTests.swift,
// ShelfDropRoutingTests.swift and NotchFileToolsTests.swift that apply on Windows.
using Notchly.Core.Shelf;

namespace Notchly.Core.Tests;

public class ShelfLayoutTests
{
    [Fact]
    public void ColumnsAndRowsFitTheirSpace()
    {
        Assert.Equal(3, ShelfLayout.ColumnCount(276, 78, 10, 4));
        Assert.Equal(1, ShelfLayout.ColumnCount(40, 78, 10, 4));
        Assert.Equal(1, ShelfLayout.RowCount(140));
        Assert.Equal(2, ShelfLayout.RowCount(194));
        Assert.Equal(1, ShelfLayout.RowCount(0));
    }

    [Fact]
    public void TilesSitWhereUpstreamPutsThem()
    {
        Assert.Equal(new TileFrame(4, 4, 78, 88), ShelfLayout.GridTileFrame(0, 3));
        Assert.Equal(new TileFrame(180, 4, 78, 88), ShelfLayout.GridTileFrame(2, 3));
        Assert.Equal(new TileFrame(4, 102, 78, 88), ShelfLayout.GridTileFrame(3, 3));
        Assert.Equal(new TileFrame(4, 200, 78, 88), ShelfLayout.GridTileFrame(6, 3));
        Assert.Equal(new TileFrame(4, 102, 78, 88), ShelfLayout.SidewaysTileFrame(1, rows: 2));
        Assert.Equal(new TileFrame(92, 4, 78, 88), ShelfLayout.SidewaysTileFrame(2, rows: 2));
        Assert.Equal((262.0, 96.0), ShelfLayout.SidewaysDocumentSize(3, 1));
        Assert.Equal((500.0, 300.0), ShelfLayout.SidewaysDocumentSize(1, 1, 500, 300));
    }
}

public class ShelfStoreTests
{
    private static ShelfItem Note(string text) => ShelfItem.FromText(text)!;

    [Fact]
    public void TheCapAllowsExactlyTwoHundredLeaves()
    {
        Assert.True(ShelfPersistence.CanAdd(199, 1));
        Assert.False(ShelfPersistence.CanAdd(200, 1));
        Assert.False(ShelfPersistence.CanAdd(199, 2));
        Assert.False(ShelfPersistence.CanAdd(0, 0));
        Assert.False(ShelfPersistence.CanAdd(-1, 1));
    }

    [Fact]
    public void ADropOfSeveralItemsBecomesOnePileAndDedupesPaths()
    {
        var store = new ShelfStore();
        var pile = store.Add([ShelfItem.File(@"C:\a\one.txt"), ShelfItem.File(@"C:\A\ONE.txt"), ShelfItem.File(@"C:\a\two.txt")]);
        Assert.NotNull(pile);
        Assert.Equal(ShelfKind.Batch, pile!.Kind);
        Assert.Equal(2, pile.LeafCount);
        Assert.Equal("one.txt +1", pile.Title);
        Assert.Single(store.Items);
        Assert.Equal(1, store.AddSerial);
    }

    [Fact]
    public void TitlesFollowUpstreamRules()
    {
        Assert.Equal("first line", Note("  first line\nsecond").Title);
        Assert.Equal(48, Note(new string('x', 80)).Title.Length);
        Assert.Null(ShelfItem.FromText("   \n "));
        Assert.Equal("example.com", ShelfItem.FromLink("https://example.com/a?b=c")!.Title);
        Assert.Null(ShelfItem.FromLink(@"file:///C:/x.txt"));
        Assert.Equal("report.pdf", ShelfItem.File(@"C:\docs\report.pdf").Title);
    }

    [Fact]
    public void RemovingFromAPileDissolvesItAndKeepsThePin()
    {
        var store = new ShelfStore();
        var a = Note("a"); var b = Note("b");
        var pile = store.Add([a, b])!;
        store.TogglePin(pile.Id);
        store.Remove([a.Id]);
        Assert.Single(store.Items);
        Assert.Equal(b.Id, store.Items[0].Id);
        Assert.Contains(b.Id, store.Pinned);
        store.Remove([b.Id]);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void ClearAllKeepsPinnedItemsAndPinnedPiles()
    {
        var store = new ShelfStore();
        var loose = store.Add([Note("loose")])!;
        var kept = store.Add([Note("kept")])!;
        var pile = store.Add([Note("p1"), Note("p2")])!;
        store.TogglePin(kept.Id);
        store.TogglePin(pile.Id);
        var removed = store.ClearAll();
        Assert.Contains(removed, i => i.Id == loose.Id);
        Assert.Equal(new[] { kept.Id, pile.Id }, store.Items.Select(i => i.Id));
        Assert.Contains(pile.Children[0].Id, store.ProtectedIds);
    }

    [Fact]
    public void RangeSelectionWorksBothWaysAndStartsAtTheTargetWithoutAnAnchor()
    {
        int[] ids = [1, 2, 3, 4, 5];
        Assert.Equal(new[] { 2, 3, 4 }, ShelfInteraction.RangeSelection(ids, 2, 4));
        Assert.Equal(new[] { 2, 3, 4 }, ShelfInteraction.RangeSelection(ids, 4, 2));
        Assert.Equal(new[] { 3 }, ShelfInteraction.RangeSelection(ids, (int?)null, 3));
        Assert.Empty(ShelfInteraction.RangeSelection(ids, 2, 9));
        Assert.True(ShelfInteraction.IsClearSelectionShortcut(isEscape: true, hasModifiers: false));
        Assert.False(ShelfInteraction.IsClearSelectionShortcut(isEscape: true, hasModifiers: true));
    }

    [Fact]
    public void ExpandedPilesShowTheirChildrenAndActionsUseTheSelection()
    {
        var store = new ShelfStore();
        var first = store.Add([Note("first")])!;
        var pile = store.Add([Note("p1"), Note("p2")])!;
        Assert.Equal(2, store.Visible.Count);
        store.ToggleExpanded(pile.Id);
        Assert.Equal(4, store.Visible.Count);
        Assert.Equal(2, store.ActionTargets.Count);
        store.ToggleSelection(first.Id);
        store.ExtendSelection(pile.Children[0].Id);
        Assert.Equal(3, store.Selection.Count);
        Assert.Equal(3, ShelfStore.LeavesOf(store.ActionTargets).Count);
    }

    [Fact]
    public void DragOutRulesRespectPinsAndThePreferences()
    {
        Assert.True(ShelfInteraction.ShouldRemoveAfterDrag(true, 1, true));
        Assert.False(ShelfInteraction.ShouldRemoveAfterDrag(false, 1, true));
        Assert.False(ShelfInteraction.ShouldRemoveAfterDrag(true, 0, true));
        Assert.False(ShelfInteraction.ShouldRemoveAfterDrag(true, 1, false));
        Assert.True(ShelfInteraction.ShouldCloseAfterDrag(true, 1, true, pinned: false));
        Assert.False(ShelfInteraction.ShouldCloseAfterDrag(true, 1, true, pinned: true));
        Assert.False(ShelfInteraction.ShouldCloseAfterDrag(true, 1, false, pinned: false));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(new[] { b }, ShelfInteraction.RemovableAfterDrag([a, b], new HashSet<Guid> { a }));
        Assert.True(ShelfInteraction.OffersMoveOutside(true, dragIncludesPinned: false));
        Assert.False(ShelfInteraction.OffersMoveOutside(true, dragIncludesPinned: true));
        Assert.False(ShelfInteraction.OffersMoveOutside(false, dragIncludesPinned: false));
    }

    [Fact]
    public void TooltipsTrimCapAndDescribePiles()
    {
        Assert.Equal("a.txt\nText Document", ShelfTooltip.ForFile("a.txt", "Text Document"));
        Assert.Equal("a.txt", ShelfTooltip.ForFile("a.txt", ""));
        Assert.Equal(new string('x', 500) + "…", ShelfTooltip.ForText("  " + new string('x', 600)));
        Assert.Equal("2 files and 1 link", ShelfTooltip.ForPile([ShelfItem.File(@"C:\a"), ShelfItem.File(@"C:\b"), ShelfItem.FromLink("https://x.y")!]));
        Assert.Equal("1 image, 1 note and 1 link", ShelfTooltip.ForPile([ShelfItem.File(@"C:\i.png", isImage: true), Note("n"), ShelfItem.FromLink("https://x.y")!]));
    }
}

public class ShelfPersistenceTests
{
    [Fact]
    public void ItemsRoundTripWithPins()
    {
        var store = new ShelfStore();
        var file = store.Add([ShelfItem.File(@"C:\x\a.txt")])!;
        store.Add([ShelfItem.FromText("note")!, ShelfItem.FromLink("https://example.com")!]);
        store.TogglePin(file.Id);
        var json = ShelfPersistence.Encode(store.ToPersisted());
        var load = Assert.IsType<ShelfStoreLoad.Items>(ShelfPersistence.Load(json));
        var restored = new ShelfStore();
        restored.Restore(load.Value, _ => false);
        Assert.Equal(store.Items.Select(i => i.Id), restored.Items.Select(i => i.Id));
        Assert.Contains(file.Id, restored.Pinned);
        Assert.Equal(ShelfKind.Batch, restored.Items[1].Kind);
        Assert.Equal("note +1", restored.Items[1].Title);
    }

    [Fact]
    public void UnknownKindsAndGarbageAreKeptApart()
    {
        Assert.IsType<ShelfStoreLoad.Items>(ShelfPersistence.Load(null));
        Assert.IsType<ShelfStoreLoad.Items>(ShelfPersistence.Load("[]"));
        Assert.IsType<ShelfStoreLoad.Unreadable>(ShelfPersistence.Load("{\"not\":\"a list\"}"));
        Assert.IsType<ShelfStoreLoad.Unreadable>(ShelfPersistence.Load("not json"));
        Assert.IsType<ShelfStoreLoad.Unreadable>(ShelfPersistence.Load("[{\"kind\":\"hologram\",\"title\":\"x\"}]"));
        var partial = Assert.IsType<ShelfStoreLoad.Partial>(ShelfPersistence.Load(
            "[{\"kind\":\"text\",\"title\":\"t\",\"text\":\"hi\"},{\"kind\":\"batch\",\"title\":\"b\",\"children\":[{\"kind\":\"hologram\"},{\"kind\":\"text\",\"text\":\"x\"}]}]"));
        Assert.Equal(2, partial.Value.Count);
        var loaded = Assert.IsType<ShelfStoreLoad.Items>(ShelfPersistence.Load("[{\"kind\":\"text\",\"text\":\"no id\"}]"));
        Assert.NotEqual(Guid.Empty, loaded.Value[0].Id);
        Assert.Equal("", loaded.Value[0].Title);
    }

    [Fact]
    public void SanitizingDropsWhatCannotBeHonouredAndCollapsesPiles()
    {
        ShelfPersistedItem Text(string t, bool pinned = false) => new(Guid.NewGuid(), ShelfKind.Text, t, Text: t, Pinned: pinned);
        ShelfPersistedItem File(string p) => new(Guid.NewGuid(), ShelfKind.File, Path.GetFileName(p), Path: p);
        ShelfPersistedItem Link(string u) => new(Guid.NewGuid(), ShelfKind.Link, u, Url: u);
        var survivor = File(@"C:\kept.txt");
        var items = new List<ShelfPersistedItem>
        {
            File(@"C:\gone.txt"), survivor, Text("   "), Text(new string('y', 300_000)),
            Link("no scheme"), Link("file:///C:/x"), Link("https://ok.example"),
            new(Guid.NewGuid(), ShelfKind.Batch, "pile", Children: [File(@"C:\gone2.txt"), Text("only child")], Pinned: true),
            new(Guid.NewGuid(), ShelfKind.Batch, "empty", Children: [File(@"C:\gone3.txt")]),
        };
        var sanitized = ShelfPersistence.Sanitized(items, path => path == survivor.Path);
        Assert.Equal(4, sanitized.Count);
        Assert.Equal(survivor.Id, sanitized[0].Id);
        Assert.Equal(ShelfPersistence.MaxTextLength, sanitized[1].Text!.Length);
        Assert.Equal("https://ok.example", sanitized[2].Url);
        Assert.Equal("only child", sanitized[3].Text);
        Assert.True(sanitized[3].Pinned);
    }

    [Fact]
    public void RestoringStopsAtTwoHundredLeaves()
    {
        var many = Enumerable.Range(0, 250).Select(i => new ShelfPersistedItem(Guid.NewGuid(), ShelfKind.Text, $"t{i}", Text: $"t{i}")).ToList();
        Assert.Equal(200, ShelfPersistence.Sanitized(many, _ => true).Count);
    }

    [Fact]
    public void FilesOnAnUnpluggedDriveKeepTheirRoot()
    {
        Assert.Equal(@"E:\", ShelfPersistence.VolumeRoot(@"E:\Photos\a.jpg"));
        Assert.Equal(@"\\server\share", ShelfPersistence.VolumeRoot(@"\\server\share\a.jpg"));
    }
}

public class ShelfDropAndArchiveTests
{
    [Fact]
    public void DropsFollowUpstreamPriority()
    {
        var files = ShelfDrop.Items(new ShelfDropContent([@"C:\a.txt", @"C:\b.txt"], Url: "https://x.y", Text: "hi"));
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal(ShelfKind.File, f.Kind));
        var image = Assert.Single(ShelfDrop.Items(new ShelfDropContent([], ImageFile: @"C:\store\i.png", Url: "https://x.y")));
        Assert.True(image.IsImage);
        Assert.Equal("Image", image.Title);
        Assert.Equal(ShelfKind.Link, Assert.Single(ShelfDrop.Items(new ShelfDropContent([], Url: "https://x.y/page", Text: "https://x.y/page"))).Kind);
        Assert.Equal(ShelfKind.Link, Assert.Single(ShelfDrop.Items(new ShelfDropContent([], Text: " https://x.y "))).Kind);
        Assert.Equal(ShelfKind.Text, Assert.Single(ShelfDrop.Items(new ShelfDropContent([], Text: "plain words"))).Kind);
        Assert.Empty(ShelfDrop.Items(new ShelfDropContent([], Text: "   ")));
    }

    [Fact]
    public void ArchivesAreNeverWrittenInsideTheirInputs()
    {
        Assert.False(ArchiveSupport.DestinationIsOutsideInputs(@"C:\Work\Project\out.zip", [@"C:\Work\Project"]));
        Assert.False(ArchiveSupport.DestinationIsOutsideInputs(@"c:\work\PROJECT\out.zip", [@"C:\Work\Project\"]));
        Assert.True(ArchiveSupport.DestinationIsOutsideInputs(@"C:\Work\Project.zip", [@"C:\Work\Project"]));
        Assert.True(ArchiveSupport.DestinationIsOutsideInputs(@"C:\Work\ProjectX\out.zip", [@"C:\Work\Project"]));
        Assert.False(ArchiveSupport.IsArchivableInput("https://example.com/file.txt"));
        Assert.True(ArchiveSupport.IsArchivableInput(@"C:\a.txt"));
    }

    [Fact]
    public void ArchiveNamesAreUnique()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\out\report.zip", @"C:\out\report 2.zip" };
        Assert.Equal(@"C:\out\report 3.zip", ArchiveSupport.UniqueArchiveName(@"C:\docs\report.pdf", taken.Contains, @"C:\out"));
        Assert.Equal(@"C:\out\photos.zip", ArchiveSupport.UniqueArchiveName(@"C:\docs\photos", _ => false, @"C:\out"));
    }
}
