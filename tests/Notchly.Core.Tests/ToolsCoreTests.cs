// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream FeatureCatalogTests (cleaning-mode unlock gesture, about lines 39-140) and WallpaperTests (gallery lifecycle,
// folder scan, merge and filter, display targets, paging), plus new tests for the camera mirror's rules, which upstream does not test.
using Notchly.Core.Camera;
using Notchly.Core.Notch;
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class CleaningUnlockTests
{
    private const int Esc = CleaningUnlockCounter.EscapeKey;

    private static CleaningUnlockCounter Counter(double window = 2.0) => new(Esc, 5, window);

    [Fact]
    public void FiveEscapeTapsUnlockOnTheFifth()
    {
        var taps = Counter();
        var unlocked = false;
        var times = new[] { 0.0, 0.3, 0.6, 0.9, 1.2 };
        for (var i = 0; i < times.Length; i++)
        {
            unlocked = taps.RegisterKeyDown(Esc, times[i], false);
            if (i < 4) Assert.False(unlocked);
        }
        Assert.True(unlocked);
        Assert.Equal(5, taps.Progress);
    }

    [Fact]
    public void WipingOtherKeysNeverMakesProgress()
    {
        var wipe = Counter();
        var unlocked = false;
        for (var i = 0; i < 8; i++) unlocked |= wipe.RegisterKeyDown(10 + i, i * 0.1, false);
        Assert.False(unlocked);
        Assert.Equal(0, wipe.Progress);
    }

    [Fact]
    public void ADifferentKeyMidStreakClearsProgress()
    {
        var streak = Counter();
        streak.RegisterKeyDown(Esc, 0.0, false);
        streak.RegisterKeyDown(Esc, 0.2, false);
        streak.RegisterKeyDown(Esc, 0.4, false);
        streak.RegisterKeyDown(8, 0.6, false);
        Assert.Equal(0, streak.Progress);
    }

    [Fact]
    public void AutoRepeatNeverCounts()
    {
        var held = Counter();
        var unlocked = false;
        for (var i = 0; i < 10; i++) unlocked |= held.RegisterKeyDown(Esc, i * 0.1, true);
        Assert.False(unlocked);
        Assert.Equal(0, held.Progress);
    }

    [Fact]
    public void APauseBeyondTheWindowRestartsTheCount()
    {
        var paused = Counter();
        paused.RegisterKeyDown(Esc, 0.0, false);
        paused.RegisterKeyDown(Esc, 0.5, false);
        Assert.Equal(2, paused.Progress);
        paused.RegisterKeyDown(Esc, 10.0, false);
        Assert.Equal(1, paused.Progress);
    }

    [Fact]
    public void ResetClearsEverythingAndEscapeStartsFresh()
    {
        var cleared = Counter();
        cleared.RegisterKeyDown(Esc, 0.0, false);
        cleared.RegisterKeyDown(Esc, 0.2, false);
        Assert.Equal(2, cleared.Progress);
        cleared.Reset();
        Assert.Equal(0, cleared.Progress);
        Assert.False(cleared.RegisterKeyDown(Esc, 0.4, false));
        Assert.Equal(1, cleared.Progress);
    }

    [Fact]
    public void AModifierBetweenEscapesResetsTheCount()
    {
        const int leftShift = 0xA0;
        var smeared = Counter(6.0);
        var unlocked = false;
        foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5 }) unlocked |= smeared.RegisterKeyDown(Esc, t, false);
        Assert.Equal(4, smeared.Progress);
        smeared.RegisterKeyDown(leftShift, 2.0, false);
        Assert.Equal(0, smeared.Progress);
        smeared.RegisterKeyDown(leftShift, 2.1, false);
        Assert.Equal(0, smeared.Progress);
        unlocked |= smeared.RegisterKeyDown(Esc, 2.5, false);
        Assert.False(unlocked);
        Assert.Equal(1, smeared.Progress);
    }

    [Fact]
    public void TheStandardCounterIsFiveEscapesWithinSixSecondsOfEachOther()
    {
        Assert.Equal(5, CleaningUnlockCounter.UnlockThreshold);
        Assert.Equal(6.0, CleaningUnlockCounter.UnlockWindowSeconds);
        var counter = CleaningUnlockCounter.Standard();
        var unlocked = false;
        foreach (var t in new[] { 0.0, 5.9, 11.8, 17.7, 23.6 }) unlocked |= counter.RegisterKeyDown(CleaningUnlockCounter.EscapeKey, t, false);
        Assert.True(unlocked);
        var slow = CleaningUnlockCounter.Standard();
        foreach (var t in new[] { 0.0, 6.5, 13.0, 19.5, 26.0 }) Assert.False(slow.RegisterKeyDown(CleaningUnlockCounter.EscapeKey, t, false));
    }
}

public class WallpaperSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notchly-wallpaper-" + Guid.NewGuid().ToString("N"));

    public WallpaperSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void ScanningWaitsForTheGalleryAndRejectsLateResults()
    {
        var gallery = new WallpaperGalleryLifecycle();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var beforeOpen = gallery.Invalidate();
        Assert.False(gallery.IsVisible);
        Assert.False(gallery.Accepts(beforeOpen));
        gallery.Begin(first);
        var firstScan = gallery.Invalidate();
        Assert.True(gallery.Accepts(firstScan));
        gallery.End(first);
        Assert.False(gallery.Accepts(firstScan));
        gallery.Begin(first);
        var reopened = gallery.Invalidate();
        Assert.True(gallery.Accepts(reopened));
        Assert.False(gallery.Accepts(firstScan));
        var replacement = gallery.Invalidate();
        Assert.False(gallery.Accepts(reopened));
        Assert.True(gallery.Accepts(replacement));
        gallery.Begin(second);
        var overlapping = gallery.Invalidate();
        Assert.False(gallery.End(first));
        Assert.True(gallery.Accepts(overlapping));
        Assert.True(gallery.End(second));
        Assert.False(gallery.Accepts(overlapping));
        gallery.Begin(first);
        gallery.EndAll();
        gallery.Begin(second);
        var afterReset = gallery.Invalidate();
        Assert.False(gallery.End(first));
        Assert.True(gallery.Accepts(afterReset));
    }

    [Fact]
    public void FolderScansProduceOwnEntriesAndSkipWhatIsNotAPicture()
    {
        Touch(@"Mine\shot.jpg");
        Touch(@"Mine\notes.txt");
        Touch(@"Mine\deeper\Beach.PNG");
        var images = WallpaperSupport.ImagesInFolder(Path.Combine(_root, "Mine"));
        Assert.Equal(["Beach.PNG", "shot.jpg"], images.Select(Path.GetFileName));
        var own = WallpaperSupport.OwnEntries(images);
        Assert.All(own, e => Assert.Equal(WallpaperSource.Own, e.Source));
        Assert.Equal(["Beach", "shot"], own.Select(e => e.Title));
    }

    [Fact]
    public void HiddenFilesAndHiddenFoldersAreSkipped()
    {
        var shown = Touch(@"Mine\shot.jpg");
        var hidden = Touch(@"Mine\secret.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var hiddenFolderFile = Touch(@"Mine\Private\inside.jpg");
        File.SetAttributes(Path.GetDirectoryName(hiddenFolderFile)!, FileAttributes.Hidden | FileAttributes.Directory);
        Assert.Equal([shown], WallpaperSupport.ImagesInFolder(Path.Combine(_root, "Mine")));
    }

    [Fact]
    public void AScanStopsWhenShouldContinueTurnsFalse()
    {
        Touch(@"Mine\a.jpg");
        Touch(@"Mine\b.jpg");
        var checks = 0;
        var cancelled = WallpaperSupport.ImagesInFolder(Path.Combine(_root, "Mine"), () => ++checks < 2);
        Assert.Empty(cancelled);
        Assert.True(checks >= 2);
    }

    [Fact]
    public void AFolderThatIsMissingScansToNothing() => Assert.Empty(WallpaperSupport.ImagesInFolder(Path.Combine(_root, "nowhere")));

    [Fact]
    public void MergeFilterAndTheSameFileOnce()
    {
        var system = WallpaperSupport.SystemEntries([Path.GetDirectoryName(Touch(@"Sys\Plain Blue.png"))!]);
        var own = WallpaperSupport.OwnEntries([Touch(@"Mine\shot.jpg"), Touch(@"Mine\shot.jpg")]);
        Assert.Single(own);
        var merged = WallpaperSupport.Merge(system, own);
        Assert.Equal(["Plain Blue", "shot"], merged.Select(e => e.Title));
        Assert.Equal(merged.Count, WallpaperSupport.Filtered(merged, WallpaperFilter.All).Count);
        Assert.All(WallpaperSupport.Filtered(merged, WallpaperFilter.Own), e => Assert.Equal(WallpaperSource.Own, e.Source));
        Assert.All(WallpaperSupport.Filtered(merged, WallpaperFilter.System), e => Assert.Equal(WallpaperSource.System, e.Source));
        Assert.Single(WallpaperSupport.Merge(own, own));
    }

    [Fact]
    public void HiddenOwnPicturesAreDroppedButNotSystemOnes()
    {
        var system = WallpaperSupport.SystemEntries([Path.GetDirectoryName(Touch(@"Sys\a.png"))!]);
        var own = WallpaperSupport.OwnEntries([Touch(@"Mine\b.jpg")]);
        var excluded = new HashSet<string>([own[0].Id, system[0].Id], StringComparer.OrdinalIgnoreCase);
        var kept = WallpaperSupport.WithoutExcluded(WallpaperSupport.Merge(system, own), excluded);
        Assert.Equal(["a"], kept.Select(e => e.Title));
    }

    [Fact]
    public void FilterNamesRoundTripAndUnknownIsAll()
    {
        foreach (var filter in Enum.GetValues<WallpaperFilter>())
            Assert.Equal(filter, WallpaperSupport.FilterFromRaw(WallpaperSupport.FilterRaw(filter)));
        Assert.Equal(WallpaperFilter.All, WallpaperSupport.FilterFromRaw("apple"));
        Assert.Equal(WallpaperFilter.All, WallpaperSupport.FilterFromRaw(null));
    }

    [Fact]
    public void TargetDisplays()
    {
        string[] ids = ["1", "2", "3"];
        Assert.Equal(ids, WallpaperSupport.TargetDisplays(true, ids, "2"));
        Assert.Equal(["2"], WallpaperSupport.TargetDisplays(false, ids, "2"));
        Assert.Equal(["1"], WallpaperSupport.TargetDisplays(false, ids, "9"));
        Assert.Equal(["1"], WallpaperSupport.TargetDisplays(false, ids, null));
        Assert.Empty(WallpaperSupport.TargetDisplays(false, [], null));
    }

    [Fact]
    public void Paging()
    {
        var numbers = Enumerable.Range(1, 50).ToList();
        Assert.Equal(0, WallpaperSupport.PageCount(0));
        Assert.Equal(1, WallpaperSupport.PageCount(24));
        Assert.Equal(2, WallpaperSupport.PageCount(25));
        Assert.Equal(3, WallpaperSupport.PageCount(50));
        Assert.Equal(1, WallpaperSupport.ClampedPage(0, 50));
        Assert.Equal(3, WallpaperSupport.ClampedPage(99, 50));
        Assert.Equal(Enumerable.Range(1, 24), WallpaperSupport.PageSlice(numbers, 1));
        Assert.Equal(Enumerable.Range(25, 24), WallpaperSupport.PageSlice(numbers, 2));
        Assert.Equal([49, 50], WallpaperSupport.PageSlice(numbers, 3));
        Assert.Empty(WallpaperSupport.PageSlice(new List<int>(), 1));
    }

    [Fact]
    public void PathListsRoundTripAndDropBlanks()
    {
        Assert.Equal(@"C:\a b.png" + "\n" + @"D:\Pics", WallpaperSupport.JoinPaths([@"C:\a b.png", @"D:\Pics"]));
        Assert.Equal([@"C:\a b.png", @"D:\Pics"], WallpaperSupport.SplitPaths("C:\\a b.png\r\n\n  \nD:\\Pics\n"));
        Assert.Empty(WallpaperSupport.SplitPaths(null));
    }
}

public class CameraWindowTests
{
    private static readonly SizeD Size = new(CameraWindowSupport.Width, CameraWindowSupport.Height);

    [Fact]
    public void SitsCentredAndFortyEightBelowTheTop()
    {
        var origin = CameraWindowSupport.Origin(new RectD(0, 0, 1920, 1080), Size);
        Assert.Equal(new PointD(800, 48), origin);
    }

    [Fact]
    public void FollowsADisplayToTheLeftOfThePrimary()
    {
        var origin = CameraWindowSupport.Origin(new RectD(-1440, 0, 1440, 900), Size);
        Assert.Equal(new PointD(-880, 48), origin);
    }

    [Fact]
    public void StaysSixteenInsideATinyScreen()
    {
        var origin = CameraWindowSupport.Origin(new RectD(100, 50, 340, 260), Size);
        Assert.Equal(116, origin.X);
        Assert.Equal(66, origin.Y);
    }

    [Fact]
    public void PermissionPromptKeepsTheMirrorOpen()
    {
        Assert.False(CameraWindowSupport.DismissesOnOutsideInteraction(CameraPreviewState.WaitingPermission));
        Assert.True(CameraWindowSupport.DismissesOnOutsideInteraction(CameraPreviewState.Running));
        Assert.False(CameraWindowSupport.HidesWhenAnotherAppActivates(CameraPreviewState.WaitingPermission, null));
    }

    [Fact]
    public void AnotherAppComingForwardHidesItExceptRightAfterTheAnswer()
    {
        Assert.True(CameraWindowSupport.HidesWhenAnotherAppActivates(CameraPreviewState.Running, null));
        Assert.False(CameraWindowSupport.HidesWhenAnotherAppActivates(CameraPreviewState.Running, 0.4));
        Assert.True(CameraWindowSupport.HidesWhenAnotherAppActivates(CameraPreviewState.Running, 1.0));
    }

    [Theory]
    [InlineData(CameraPreviewState.Running, 2, false, CameraDeviceChange.SwitchToFirst)]
    [InlineData(CameraPreviewState.Starting, 1, false, CameraDeviceChange.SwitchToFirst)]
    [InlineData(CameraPreviewState.Running, 0, false, CameraDeviceChange.StopNoCamera)]
    [InlineData(CameraPreviewState.Running, 2, true, CameraDeviceChange.None)]
    [InlineData(CameraPreviewState.NoCamera, 1, false, CameraDeviceChange.Start)]
    [InlineData(CameraPreviewState.NoCamera, 0, false, CameraDeviceChange.None)]
    [InlineData(CameraPreviewState.Idle, 1, false, CameraDeviceChange.None)]
    [InlineData(CameraPreviewState.Denied, 1, false, CameraDeviceChange.None)]
    [InlineData(CameraPreviewState.Unavailable, 1, false, CameraDeviceChange.None)]
    [InlineData(CameraPreviewState.WaitingPermission, 1, false, CameraDeviceChange.None)]
    public void ChangingCameras(CameraPreviewState state, int count, bool selectedPresent, CameraDeviceChange expected) =>
        Assert.Equal(expected, CameraWindowSupport.OnDevicesChanged(state, count, selectedPresent));

    [Fact]
    public void ThePreferredCameraIsTheStoredOneOtherwiseTheFirst()
    {
        Assert.Equal("b", CameraWindowSupport.Preferred(["a", "b"], "b"));
        Assert.Equal("a", CameraWindowSupport.Preferred(["a", "b"], "gone"));
        Assert.Equal("a", CameraWindowSupport.Preferred(["a", "b"], ""));
        Assert.Null(CameraWindowSupport.Preferred([], "b"));
        Assert.True(CameraWindowSupport.ShowsPicker(2));
        Assert.False(CameraWindowSupport.ShowsPicker(1));
    }
}

public class ToolsShortcutDefaultsTests
{
    [Fact]
    public void NoTwoShortcutDefaultsAreTheSame()
    {
        var shortcuts = Settings.Defaults.Registered
            .Where(pair => pair.Key.Contains("Shortcut", StringComparison.OrdinalIgnoreCase) && !pair.Key.EndsWith("Enabled", StringComparison.Ordinal)
                           && pair.Value is string value && value.Contains(':'))
            .Select(pair => (pair.Key, Value: (string)pair.Value!)).ToList();
        var duplicates = shortcuts.GroupBy(s => s.Value).Where(g => g.Count() > 1).Select(g => string.Join(" = ", g.Select(s => s.Key))).ToList();
        Assert.Empty(duplicates);
    }
}
