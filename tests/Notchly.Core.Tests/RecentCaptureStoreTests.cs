// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream RecentCaptureStoreTests (the history.json rules, with the file system replaced by an in-memory folder) and the
// recent-capture parts of ScreenshotFeatureTests (cache file names, the capped set). The panel and monitor parts of
// RecentCapturePanelTests only drive AppKit windows and have no pure content to port.
using Notchly.Core.Capture;

namespace Notchly.Core.Tests;

public class RecentCaptureStoreTests
{
    /// <summary>An in-memory cache folder that can fail reads and writes on demand.</summary>
    private sealed class FakeFolder : IRecentCaptureStorage
    {
        public Dictionary<string, string> Files { get; } = [];
        public IndexReadStatus IndexStatus { get; set; } = IndexReadStatus.Missing;
        public string? IndexText { get; set; }
        public bool FailWrites { get; set; }
        public bool Unlistable { get; set; }
        public int Writes { get; private set; }
        public List<string> Deleted { get; } = [];

        public IndexRead ReadIndex() => new(IndexStatus, IndexText);

        public bool WriteIndex(string json)
        {
            if (FailWrites) return false;
            IndexText = json;
            IndexStatus = IndexReadStatus.Ok;
            Writes++;
            return true;
        }

        public IReadOnlyList<string>? ListFiles() => Unlistable ? null : Files.Keys.ToList();

        public void DeleteFile(string name)
        {
            Files.Remove(name);
            Deleted.Add(name);
        }
    }

    private static RecentCaptureEntry Shot(Guid id, long seconds = 1, string? thumbnail = "") => new(
        id, RecentCaptureKind.Screenshot, DateTimeOffset.FromUnixTimeSeconds(seconds),
        RecentCapturePolicy.ScreenshotFileName(id), null, thumbnail == "" ? RecentCapturePolicy.ThumbnailFileName(id) : thumbnail,
        2, 0, 0, 1, 1);

    private static RecentCaptureEntry Recording(Guid id, string path, long seconds = 1, string? thumbnail = null) => new(
        id, RecentCaptureKind.Recording, DateTimeOffset.FromUnixTimeSeconds(seconds), null, path, thumbnail);

    private static void AddFiles(FakeFolder folder, Guid id)
    {
        folder.Files[RecentCapturePolicy.ScreenshotFileName(id)] = "png";
        folder.Files[RecentCapturePolicy.ThumbnailFileName(id)] = "png";
    }

    // MARK: - Loading and persisting

    [Fact]
    public void ANewHistoryCanCreateItsFirstIndex()
    {
        var folder = new FakeFolder();
        var store = new RecentCaptureStore(folder);
        Assert.True(store.LoadIfNeeded());
        Assert.Empty(store.Entries);
        Assert.True(store.Persist());
        Assert.Equal("[]", folder.IndexText);
    }

    [Fact]
    public void ACommittedHistorySurvivesReloadUnchanged()
    {
        var folder = new FakeFolder();
        var id = Guid.NewGuid();
        var entry = Shot(id);
        var first = new RecentCaptureStore(folder);
        first.LoadIfNeeded();
        AddFiles(folder, id);
        first.Entries = [entry];
        Assert.True(first.Persist());
        var healthy = new RecentCaptureStore(folder);
        Assert.True(healthy.LoadIfNeeded());
        Assert.Equal([entry], healthy.Entries);
        var writes = folder.Writes;
        Assert.True(healthy.Persist());
        Assert.Equal(writes, folder.Writes);
        Assert.Equal(2, folder.Files.Count);
    }

    [Fact]
    public void AnUnchangedHistoryIsNotRewritten()
    {
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = "[]" };
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Persist();
        store.Persist();
        Assert.Equal(0, folder.Writes);
    }

    [Fact]
    public void ACorruptIndexPermitsNeitherCleanupNorReplacement()
    {
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = "{broken history" };
        var id = Guid.NewGuid();
        AddFiles(folder, id);
        var store = new RecentCaptureStore(folder);
        Assert.False(store.LoadIfNeeded());
        Assert.False(store.Persist());
        Assert.Equal("{broken history", folder.IndexText);
        Assert.Equal(2, folder.Files.Count);
        Assert.Empty(folder.Deleted);
    }

    [Fact]
    public void AFailedLoadCanRetryOnceTheIndexIsReadable()
    {
        var id = Guid.NewGuid();
        var entry = Shot(id);
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = "{broken" };
        var store = new RecentCaptureStore(folder);
        Assert.False(store.LoadIfNeeded());
        folder.IndexText = RecentCaptureIndex.Encode([entry]);
        Assert.True(store.LoadIfNeeded());
        Assert.Equal([entry], store.Entries);
    }

    [Fact]
    public void ADeniedIndexReadPreservesCapturesAndBlocksReplacement()
    {
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Unreadable };
        AddFiles(folder, Guid.NewGuid());
        var store = new RecentCaptureStore(folder);
        Assert.False(store.LoadIfNeeded());
        Assert.False(store.Persist());
        Assert.Equal(2, folder.Files.Count);
        Assert.Equal(0, folder.Writes);
    }

    [Fact]
    public void AMissingIndexWithExistingCapturesNeverAuthorisesANewEmptyHistory()
    {
        var folder = new FakeFolder();
        AddFiles(folder, Guid.NewGuid());
        var store = new RecentCaptureStore(folder);
        Assert.False(store.LoadIfNeeded());
        Assert.False(store.Persist());
        Assert.Equal(2, folder.Files.Count);
    }

    [Fact]
    public void AMissingIndexNextToUnrelatedFilesIsAFreshHistory()
    {
        var folder = new FakeFolder();
        folder.Files["Notes.txt"] = "keep";
        var store = new RecentCaptureStore(folder);
        Assert.True(store.LoadIfNeeded());
        Assert.True(store.Persist());
        Assert.Equal("keep", folder.Files["Notes.txt"]);
    }

    [Fact]
    public void AnUnlistableFolderWithAMissingIndexDoesNotLoad()
    {
        var folder = new FakeFolder { Unlistable = true };
        Assert.False(new RecentCaptureStore(folder).LoadIfNeeded());
    }

    [Fact]
    public void AStoreWithoutAFolderNeverLoadsOrWrites()
    {
        var store = new RecentCaptureStore(null);
        Assert.False(store.LoadIfNeeded());
        Assert.False(store.Persist());
    }

    [Fact]
    public void PersistBeforeLoadWritesNothing()
    {
        var folder = new FakeFolder();
        var store = new RecentCaptureStore(folder);
        Assert.False(store.Persist());
        Assert.Equal(0, folder.Writes);
    }

    [Fact]
    public void FailedPersistenceNeverDeletesCapturesFromThePreviouslyCommittedHistory()
    {
        var id = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = RecentCaptureIndex.Encode([Shot(id)]) };
        AddFiles(folder, id);
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Entries = [];
        folder.FailWrites = true;
        Assert.False(store.Persist());
        Assert.Equal(2, folder.Files.Count);
        folder.FailWrites = false;
        Assert.True(store.Persist());
        Assert.Empty(folder.Files);
    }

    [Fact]
    public void RetryingAnExplicitRemovalCleansOnlyOwnedImages()
    {
        var id = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = RecentCaptureIndex.Encode([Shot(id)]) };
        AddFiles(folder, id);
        folder.Files["Notes.txt"] = "keep";
        folder.Files["history.json"] = "index is not a cache file";
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Entries = [];
        Assert.True(store.Persist());
        Assert.Equal(["Notes.txt", "history.json"], folder.Files.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ClearingRecordingHistoryRemovesItsThumbnailButNotTheVideo()
    {
        var id = Guid.NewGuid();
        var thumbnail = RecentCapturePolicy.ThumbnailFileName(id);
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = "[]" };
        folder.Files[thumbnail] = "png";
        var video = Path.Combine(Path.GetTempPath(), "Original.mov");
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Entries = [Recording(id, video, 5, thumbnail)];
        Assert.True(store.Persist());
        Assert.True(folder.Files.ContainsKey(thumbnail));
        store.Clear();
        Assert.True(store.Persist());
        Assert.False(folder.Files.ContainsKey(thumbnail));
        Assert.DoesNotContain(video, folder.Deleted);
    }

    [Fact]
    public void TheFilesOfAKeptEntryStayWhenAnotherIsRemoved()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = RecentCaptureIndex.Encode([Shot(keep, 2), Shot(drop, 1)]) };
        AddFiles(folder, keep);
        AddFiles(folder, drop);
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        Assert.True(store.Remove(drop));
        Assert.False(store.Remove(drop));
        store.Persist();
        Assert.Equal(new[] { RecentCapturePolicy.ScreenshotFileName(keep), RecentCapturePolicy.ThumbnailFileName(keep) }.Order(),
            folder.Files.Keys.Order());
    }

    [Fact]
    public void OnlyTheCacheFilesTheStoreNamesAreEverDeleted()
    {
        var id = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = "[]" };
        folder.Files[$"{id}.PNG"] = "x";
        folder.Files["../escape.png"] = "x";
        folder.Files[$"{id}-thumbnail.jpg"] = "x";
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Persist();
        Assert.Equal([$"{id}.PNG"], folder.Deleted);
    }

    [Fact]
    public void ALoadedHistoryIsNewestFirst()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = RecentCaptureIndex.Encode([Shot(a, 1), Shot(b, 9)]) };
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        Assert.Equal([b, a], store.Entries.Select(e => e.Id).ToArray());
    }

    // MARK: - JSON

    [Fact]
    public void EntriesRoundTripThroughTheIndex()
    {
        var entries = new[]
        {
            Shot(Guid.NewGuid(), 1_700_000_300),
            Recording(Guid.NewGuid(), "C:\\Videos\\clip.mp4", 1_700_000_200, "t.png"),
            Recording(Guid.NewGuid(), "C:\\Videos\\other.gif", 1_700_000_100),
        };
        var decoded = RecentCaptureIndex.Decode(RecentCaptureIndex.Encode(entries), out var dropped);
        Assert.Equal(0, dropped);
        Assert.Equal(entries, decoded);
    }

    [Fact]
    public void FractionalDatesSurviveToTheMillisecond()
    {
        var entry = Shot(Guid.NewGuid()) with { CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123) };
        var back = RecentCaptureIndex.Decode(RecentCaptureIndex.Encode([entry]), out _)!.Single();
        Assert.Equal(entry.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void TheIndexUsesUpstreamsKeyNames()
    {
        var id = Guid.NewGuid();
        var json = RecentCaptureIndex.Encode([Shot(id)]);
        foreach (var key in new[] { "\"id\"", "\"kind\":\"screenshot\"", "\"createdAt\"", "\"screenshotName\"", "\"thumbnailName\"",
                     "\"scale\"", "\"anchorX\"", "\"anchorY\"", "\"anchorWidth\"", "\"anchorHeight\"" })
            Assert.Contains(key, json);
        Assert.DoesNotContain("recordingPath", json);
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        var id = Guid.NewGuid();
        var json = $$"""[{"id":"{{id}}","kind":"screenshot","createdAt":5,"extra":true}]""";
        var decoded = RecentCaptureIndex.Decode(json, out _)!;
        Assert.Equal(id, decoded.Single().Id);
        Assert.Null(decoded.Single().ScreenshotName);
    }

    [Fact]
    public void ABrokenRowIsDroppedAndTheRestKept()
    {
        var good = Guid.NewGuid();
        var json = $$"""
            [{"id":"{{good}}","kind":"screenshot","createdAt":5},
             {"id":"not-a-uuid","kind":"screenshot","createdAt":5},
             {"id":"{{Guid.NewGuid()}}","kind":"hologram","createdAt":5},
             {"id":"{{Guid.NewGuid()}}","kind":"recording"},
             {"id":"{{Guid.NewGuid()}}","kind":"recording","createdAt":"yesterday"},
             42, null, "x"]
            """;
        var decoded = RecentCaptureIndex.Decode(json, out var dropped)!;
        Assert.Equal([good], decoded.Select(e => e.Id).ToArray());
        Assert.Equal(7, dropped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void ATextThatIsNotAnArrayIsUnreadable(string json) =>
        Assert.Null(RecentCaptureIndex.Decode(json, out _));

    [Fact]
    public void ANullTextIsUnreadable() => Assert.Null(RecentCaptureIndex.Decode(null, out _));

    [Fact]
    public void AnEmptyArrayIsAnEmptyHistory() => Assert.Empty(RecentCaptureIndex.Decode("[]", out _)!);

    [Fact]
    public void ANonNumericFieldIsTreatedAsAbsent()
    {
        var id = Guid.NewGuid();
        var json = $$"""[{"id":"{{id}}","kind":"screenshot","createdAt":5,"scale":"big","anchorX":2}]""";
        var entry = RecentCaptureIndex.Decode(json, out _)!.Single();
        Assert.Null(entry.Scale);
        Assert.Equal(2, entry.AnchorX);
    }

    // MARK: - Names

    [Fact]
    public void RecentCaptureCleanupRecognisesOnlyAppOwnedUuidPngNames()
    {
        var id = Guid.NewGuid();
        Assert.True(RecentCapturePolicy.IsCacheFileName($"{id}.png"));
        Assert.True(RecentCapturePolicy.IsCacheFileName($"{id}-thumbnail.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName("history.json"));
        Assert.False(RecentCapturePolicy.IsCacheFileName($"../{id}.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName($"..\\{id}.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName($"{id}.jpg"));
        Assert.False(RecentCapturePolicy.IsCacheFileName($"{id}-thumb.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName("not-a-uuid.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName("-thumbnail.png"));
        Assert.False(RecentCapturePolicy.IsCacheFileName($"{id:N}.png"));
    }

    [Fact]
    public void TheNamesAreTheIdWithAnOptionalThumbnailSuffix()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("11111111-2222-3333-4444-555555555555.png", RecentCapturePolicy.ScreenshotFileName(id));
        Assert.Equal("11111111-2222-3333-4444-555555555555-thumbnail.png", RecentCapturePolicy.ThumbnailFileName(id));
        Assert.True(RecentCapturePolicy.IsCacheFileName(RecentCapturePolicy.ThumbnailFileName(id)));
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b.png", false)]
    [InlineData("a\\b.png", false)]
    [InlineData("C:b.png", false)]
    public void ASafeNameIsABareFileName(string name, bool safe) =>
        Assert.Equal(safe, RecentCapturePolicy.IsSafeName(name));

    // MARK: - Cap

    [Fact]
    public void RecentCapturesKeepOnlyTheNewestBoundedSet()
    {
        var ids = Enumerable.Range(0, 14).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(ids.Take(12).ToArray(), RecentCapturePolicy.CappedIds(ids));
        Assert.Equal(12, RecentCapturePolicy.Limit);
    }

    [Fact]
    public void RecentCapturesKeepTheLatestScreenshotAndRespectTheDiskBudget()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var bytes = new Dictionary<Guid, long> { [ids[0]] = RecentCapturePolicy.MaximumBytes - 1, [ids[1]] = 2 };
        Assert.Equal([ids[0], ids[2]], RecentCapturePolicy.CappedIds(ids, bytes));
    }

    [Fact]
    public void AnOversizedFirstScreenshotIsStillKept()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bytes = new Dictionary<Guid, long> { [ids[0]] = RecentCapturePolicy.MaximumBytes * 2, [ids[1]] = 1 };
        Assert.Equal([ids[0]], RecentCapturePolicy.CappedIds(ids, bytes));
    }

    [Fact]
    public void ARowWithAnUnknownSizeAlwaysStays()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var bytes = new Dictionary<Guid, long> { [ids[0]] = RecentCapturePolicy.MaximumBytes, [ids[1]] = 5 };
        Assert.Equal([ids[0], ids[2]], RecentCapturePolicy.CappedIds(ids, bytes));
    }

    [Fact]
    public void ANegativeSizeCountsAsNothing()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bytes = new Dictionary<Guid, long> { [ids[0]] = -50, [ids[1]] = RecentCapturePolicy.MaximumBytes };
        Assert.Equal(ids, RecentCapturePolicy.CappedIds(ids, bytes));
    }

    // MARK: - Pruning and editing

    [Fact]
    public void MissingFilesAreProneOutOfTheHistory()
    {
        var gone = Guid.NewGuid();
        var here = Guid.NewGuid();
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [Shot(here, 2), Shot(gone, 1)] };
        store.PruneMissing(name => name == RecentCapturePolicy.ScreenshotFileName(here));
        Assert.Equal([here], store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void RecordingsAreCheckedByTheirOwnPath()
    {
        var clip = Guid.NewGuid();
        var lost = Guid.NewGuid();
        var store = new RecentCaptureStore(new FakeFolder())
        {
            Entries = [Recording(clip, "C:\\v\\clip.mp4", 2), Recording(lost, "C:\\v\\lost.mp4", 1)],
        };
        store.PruneMissing(_ => false, path => path == "C:\\v\\clip.mp4");
        Assert.Equal([clip], store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void WithOneCallbackRecordingsUseItToo()
    {
        var clip = Guid.NewGuid();
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [Recording(clip, "C:\\v\\clip.mp4")] };
        store.PruneMissing(_ => true);
        Assert.Single(store.Entries);
        store.PruneMissing(_ => false);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void AScreenshotWithAnUnsafeNameIsPrunedWithoutAsking()
    {
        var asked = new List<string>();
        var id = Guid.NewGuid();
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [Shot(id) with { ScreenshotName = "..\\escape.png" }] };
        store.PruneMissing(name => { asked.Add(name); return true; });
        Assert.Empty(store.Entries);
        Assert.Empty(asked);
    }

    [Fact]
    public void ARecordingWithoutAPathIsPruned()
    {
        var store = new RecentCaptureStore(new FakeFolder())
        {
            Entries = [new RecentCaptureEntry(Guid.NewGuid(), RecentCaptureKind.Recording, DateTimeOffset.UnixEpoch)],
        };
        store.PruneMissing(_ => true);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void PruningAlsoAppliesTheCap()
    {
        var entries = Enumerable.Range(0, 15).Select(i => Shot(Guid.NewGuid(), 100 - i)).ToList();
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = entries };
        store.PruneMissing(_ => true);
        Assert.Equal(12, store.Entries.Count);
        Assert.Equal(entries.Take(12).Select(e => e.Id).ToArray(), store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void PruningUsesTheByteBudgetOfScreenshots()
    {
        var a = Shot(Guid.NewGuid(), 3);
        var b = Shot(Guid.NewGuid(), 2);
        var c = Shot(Guid.NewGuid(), 1);
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [a, b, c] };
        store.PruneMissing(_ => true, null, e => e == a ? RecentCapturePolicy.MaximumBytes : 10);
        Assert.Equal([a.Id], store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void PrependPutsTheNewestFirst()
    {
        var older = Shot(Guid.NewGuid(), 1);
        var newer = Shot(Guid.NewGuid(), 5);
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [older] };
        store.Prepend(newer);
        Assert.Equal([newer.Id, older.Id], store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void ThirteenCapturesKeepTheNewestTwelve()
    {
        var store = new RecentCaptureStore(new FakeFolder());
        for (var i = 0; i < 13; i++) store.Prepend(Shot(Guid.NewGuid(), i));
        Assert.Equal(12, store.Entries.Count);
        Assert.Equal(12, store.Entries[0].CreatedAt.ToUnixTimeSeconds());
        Assert.Equal(1, store.Entries[^1].CreatedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void ARecordingOfTheSameFileReplacesItsPreviousRow()
    {
        var first = Recording(Guid.NewGuid(), "C:\\v\\clip.mp4", 1);
        var again = Recording(Guid.NewGuid(), "C:\\V\\CLIP.mp4", 9);
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [first] };
        store.Prepend(again);
        Assert.Equal([again.Id], store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void TwoScreenshotsNeverReplaceEachOther()
    {
        var store = new RecentCaptureStore(new FakeFolder());
        store.Prepend(Shot(Guid.NewGuid(), 1));
        store.Prepend(Shot(Guid.NewGuid(), 2));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void ClearEmptiesTheHistory()
    {
        var store = new RecentCaptureStore(new FakeFolder()) { Entries = [Shot(Guid.NewGuid())] };
        store.Clear();
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void ClearThenPersistRemovesEveryCacheFile()
    {
        var id = Guid.NewGuid();
        var folder = new FakeFolder { IndexStatus = IndexReadStatus.Ok, IndexText = RecentCaptureIndex.Encode([Shot(id)]) };
        AddFiles(folder, id);
        var store = new RecentCaptureStore(folder);
        store.LoadIfNeeded();
        store.Clear();
        store.Persist();
        Assert.Empty(folder.Files);
        Assert.Equal("[]", folder.IndexText);
    }
}
