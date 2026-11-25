// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream ClipboardFeatureTests, ClipboardCaptureRestoreTests, ClipboardSensitiveTextPreferenceTests, PastePlainTests and the
// FinderPasteImageSupport assertions of PointerInputFeatureTests: the history rules, auto-clear, the plain paste plan and image names.
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class ClipboardHistoryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClipboardEntry Text(string text, double daysAgo = 0, bool pinned = false) =>
        ClipboardEntry.ForText(text, Now - TimeSpan.FromDays(daysAgo)) with { PinnedAt = pinned ? Now : null };

    [Fact]
    public void LimitsAndRetentionFallBackToTheirDefaults()
    {
        Assert.Equal(10000, ClipboardHistorySupport.SanitizedLimit(10000));
        Assert.Equal(0, ClipboardHistorySupport.SanitizedLimit(0));
        Assert.Equal(50, ClipboardHistorySupport.SanitizedLimit(-99));
        Assert.Equal(50, ClipboardHistorySupport.SanitizedLimit(51));
        Assert.Equal(0, ClipboardHistorySupport.SanitizedRetention(12));
        Assert.Equal(30, ClipboardHistorySupport.SanitizedRetention(30));
    }

    [Fact]
    public void RetentionHasLabels()
    {
        Assert.Equal("Forever", ClipboardHistorySupport.RetentionLabel(0));
        Assert.Equal("1 day", ClipboardHistorySupport.RetentionLabel(1));
        Assert.Equal("30 days", ClipboardHistorySupport.RetentionLabel(30));
    }

    [Fact]
    public void TextIsTrimmedAndOnlyKeptWithinTheCap()
    {
        Assert.Equal("a b", ClipboardHistorySupport.StorableText("  a b \n"));
        Assert.Null(ClipboardHistorySupport.StorableText("   \t\n"));
        Assert.Null(ClipboardHistorySupport.StorableText(null));
        Assert.Null(ClipboardHistorySupport.StorableText(new string('a', ClipboardHistorySupport.MaxCharacters + 1)));
        Assert.NotNull(ClipboardHistorySupport.StorableText(new string('a', 170_000)));
        Assert.NotNull(ClipboardHistorySupport.StorableText(new string('a', ClipboardHistorySupport.MaxCharacters)));
    }

    [Fact]
    public void FilesAreKeptInOrderUpToTheCap()
    {
        Assert.Null(ClipboardHistorySupport.StorableFiles([]));
        Assert.Equal(["b", "a"], ClipboardHistorySupport.StorableFiles(["b", "a"]));
        Assert.NotNull(ClipboardHistorySupport.StorableFiles(Enumerable.Range(0, 100).Select(i => "f" + i)));
        Assert.Null(ClipboardHistorySupport.StorableFiles(Enumerable.Range(0, 101).Select(i => "f" + i)));
    }

    [Fact]
    public void ARepeatKeepsItsIdAndPinAndComesFirstInItsBlock()
    {
        var a = Text("a");
        var b = Text("b");
        var pinned = Text("p", pinned: true);
        var list = ClipboardHistorySupport.Recording([pinned, a, b], Text("b"));
        Assert.Equal(["p", "b", "a"], list.Select(e => e.Text));
        Assert.Equal(b.Id, list[1].Id);

        var again = ClipboardHistorySupport.Recording(list, Text("p"));
        Assert.Equal(["p", "b", "a"], again.Select(e => e.Text));
        Assert.True(again[0].IsPinned);
        Assert.Equal(pinned.Id, again[0].Id);
    }

    [Fact]
    public void ANewCopyGoesJustBelowThePinnedEntries()
    {
        var list = ClipboardHistorySupport.Recording([Text("p", pinned: true), Text("a")], Text("new"));
        Assert.Equal(["p", "new", "a"], list.Select(e => e.Text));
    }

    [Fact]
    public void ContentMatchesByTrimmedTextImageHashAndOrderedFiles()
    {
        Assert.True(ClipboardHistorySupport.MatchesContent(Text("x"), Text(" x ")));
        Assert.False(ClipboardHistorySupport.MatchesContent(Text("x"), Text("X")));
        var img1 = ClipboardEntry.ForImage("1.png", "hash", 2, 2, Now);
        var img2 = ClipboardEntry.ForImage("2.png", "hash", 2, 2, Now);
        Assert.True(ClipboardHistorySupport.MatchesContent(img1, img2));
        Assert.False(ClipboardHistorySupport.MatchesContent(img1, img2 with { ImageHash = "other" }));
        Assert.True(ClipboardHistorySupport.MatchesContent(ClipboardEntry.ForFiles(["a", "b"], Now), ClipboardEntry.ForFiles(["a", "b"], Now)));
        Assert.False(ClipboardHistorySupport.MatchesContent(ClipboardEntry.ForFiles(["a", "b"], Now), ClipboardEntry.ForFiles(["b", "a"], Now)));
        Assert.False(ClipboardHistorySupport.MatchesContent(Text("x"), ClipboardEntry.ForFiles(["x"], Now)));
    }

    [Fact]
    public void RetentionDropsOldRecentEntriesButNeverPinnedOnes()
    {
        var entries = new[] { Text("old", daysAgo: 8), Text("new", daysAgo: 1), Text("pin", daysAgo: 400, pinned: true) };
        var kept = ClipboardHistorySupport.Retained(entries, 50, 7, Now);
        Assert.Equal(["pin", "new"], kept.Select(e => e.Text));
    }

    [Fact]
    public void AnEntryInAnEditorIsExemptFromAgeExpiry()
    {
        var old = Text("old", daysAgo: 8);
        var kept = ClipboardHistorySupport.Retained([old], 50, 7, Now, exempt: new HashSet<Guid> { old.Id });
        Assert.Single(kept);
    }

    [Fact]
    public void TheLimitCountsOnlyRecentEntries()
    {
        var entries = new List<ClipboardEntry> { Text("p", pinned: true) };
        entries.AddRange(Enumerable.Range(0, 30).Select(i => Text("e" + i)));
        var kept = ClipboardHistorySupport.Retained(entries, 20, 0, Now);
        Assert.Equal(21, kept.Count);
        Assert.Equal("p", kept[0].Text);
        Assert.Equal(51, ClipboardHistorySupport.Retained(entries.Concat(Enumerable.Range(0, 40).Select(i => Text("x" + i))).ToList(), 0, 0, Now).Count - 20);
    }

    [Fact]
    public void TheTextByteBudgetKeepsPinnedEntriesAndTheNewestThatFit()
    {
        var entries = new[] { Text("123456", pinned: true), Text("abcd"), Text("efgh") };
        var kept = ClipboardHistorySupport.Retained(entries, 50, 0, Now, textByteLimit: 10);
        Assert.Equal(["123456", "abcd"], kept.Select(e => e.Text));
    }

    [Fact]
    public void PinningAndUnpinningMoveAnEntryBetweenBlocks()
    {
        var a = Text("a");
        var b = Text("b");
        var pinnedList = ClipboardHistorySupport.TogglePin([a, b], b.Id, Now);
        Assert.Equal(["b", "a"], pinnedList.Select(e => e.Text));
        Assert.True(pinnedList[0].IsPinned);
        var later = Now.AddHours(1);
        var unpinned = ClipboardHistorySupport.TogglePin(pinnedList, b.Id, later);
        Assert.Equal(["b", "a"], unpinned.Select(e => e.Text));
        Assert.False(unpinned[0].IsPinned);
        Assert.Equal(later, unpinned[0].CopiedAt);
    }

    [Fact]
    public void MovingStaysInsideItsBlock()
    {
        var p = Text("p", pinned: true);
        var a = Text("a");
        var b = Text("b");
        var moved = ClipboardHistorySupport.Move([p, a, b], b.Id, -1);
        Assert.Equal(["p", "b", "a"], moved.Select(e => e.Text));
        Assert.Equal(["p", "a", "b"], ClipboardHistorySupport.Move([p, a, b], a.Id, -1).Select(e => e.Text));
        Assert.Equal(["p", "a", "b"], ClipboardHistorySupport.Move([p, a, b], b.Id, 1).Select(e => e.Text));
    }

    [Theory]
    [InlineData(101, 100, 100, 101L)]
    [InlineData(100, 100, 100, null)]
    [InlineData(2, 178, 178, 2L)]
    [InlineData(0, 178, 178, 0L)]
    [InlineData(101, 100, 102, null)]
    [InlineData(103, 100, 102, 103L)]
    public void ACountIsAcceptedOnlyWhenItIsNew(long read, long since, long last, long? expected) =>
        Assert.Equal(expected, ClipboardHistorySupport.Accepted(read, since, last));

    [Fact]
    public void ALoneLinkIsKeptAsTheLinkAndASchemeLessCopyPrefersTheLinkFlavour()
    {
        Assert.Equal("https://example.com/a", ClipboardHistorySupport.PreferredText("  https://example.com/a  ", null));
        Assert.Equal("https://example.com/a", ClipboardHistorySupport.PreferredText("example.com/a", "https://example.com/a"));
        Assert.Equal("https://example.com/a", ClipboardHistorySupport.PreferredText("//example.com/a", "https://example.com/a"));
        Assert.Equal("example.com/a", ClipboardHistorySupport.PreferredText("example.com/a", null));
        Assert.Equal("see example.com/a", ClipboardHistorySupport.PreferredText("see example.com/a", "https://example.com/a"));
        Assert.Equal("other", ClipboardHistorySupport.PreferredText("other", "https://example.com/a"));
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("export GITHUB_TOKEN=exampleCredential")]
    [InlineData("SECRET_KEY_BASE=abc123")]
    [InlineData("PASSWORD_2: hunter2")]
    [InlineData("command --password hunter2")]
    [InlineData("Authorization: Bearer abc.def.ghi")]
    [InlineData("abc1234567890-xyz-abc")]
    [InlineData("api_key = \"abc\"")]
    public void CredentialLookingTextIsSensitive(string text) => Assert.True(ClipboardHistorySupport.LooksSensitive(text));

    [Theory]
    [InlineData("max_tokens = 4096")]
    [InlineData("TOKEN=")]
    [InlineData("password")]
    [InlineData("passwordless: true")]
    [InlineData("https://example.com/password/reset")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData("an ordinary sentence with several words in it")]
    public void OrdinaryTextIsNotSensitive(string text) => Assert.False(ClipboardHistorySupport.LooksSensitive(text));

    [Fact]
    public void SearchNeedsEveryWordAndIgnoresCaseAndAccents()
    {
        var entries = new[] { Text("Café au lait"), Text("tea with milk"), Text("coffee") };
        Assert.Equal(["Café au lait"], ClipboardHistorySupport.Search(entries, "CAFE lait").Select(e => e.Text));
        Assert.Equal(3, ClipboardHistorySupport.Search(entries, "  ").Count);
        Assert.Empty(ClipboardHistorySupport.Search(entries, "cafe zzz"));
    }

    [Fact]
    public void SearchRanksWordStartsFirstAndKeepsOrderOtherwise()
    {
        var entries = new[] { Text("scatter"), Text("a cat"), Text("cat food") };
        Assert.Equal(["a cat", "cat food", "scatter"], ClipboardHistorySupport.Search(entries, "cat").Select(e => e.Text));
    }

    [Fact]
    public void FileEntriesSearchByFileName() =>
        Assert.Single(ClipboardHistorySupport.Search([ClipboardEntry.ForFiles([@"C:\Users\me\Report 2026.pdf"], Now)], "report"));

    [Fact]
    public void ABatchPastesAsFilesOnlyWhenEveryEntryIsFiles()
    {
        var files = ClipboardEntry.ForFiles([@"C:\a.txt", @"C:\b.txt"], Now);
        Assert.Equal(PasteMode.Files, ClipboardHistorySupport.ModeFor([files, ClipboardEntry.ForFiles([@"C:\c.txt"], Now)]));
        Assert.Equal(PasteMode.Text, ClipboardHistorySupport.ModeFor([files, Text("x")]));
        Assert.Equal("x\nC:\\a.txt\nC:\\b.txt", ClipboardHistorySupport.BatchText([Text("x"), files]));
        Assert.Equal(3, ClipboardHistorySupport.BatchFiles([files, ClipboardEntry.ForFiles([@"C:\c.txt"], Now)]).Count);
    }

    [Fact]
    public void DigitsPickByPositionAndRemoteDesktopIsCopyOnly()
    {
        Assert.Equal(0, ClipboardHistorySupport.PositionForDigit(1, 3));
        Assert.Equal(2, ClipboardHistorySupport.PositionForDigit(3, 3));
        Assert.Null(ClipboardHistorySupport.PositionForDigit(4, 3));
        Assert.Null(ClipboardHistorySupport.PositionForDigit(0, 3));
        Assert.True(ClipboardHistorySupport.IsCopyOnlyApp("MSTSC.exe"));
        Assert.False(ClipboardHistorySupport.IsCopyOnlyApp("notepad.exe"));
    }

    [Fact]
    public void IgnoredAppsAreNormalizedAndMatchedByProgramName()
    {
        Assert.Equal(["KeePass.exe", "x.exe"], ClipboardHistorySupport.NormalizedIgnoredApps([" KeePass.exe ", "keepass.exe", "", "x.exe"]));
        Assert.True(ClipboardHistorySupport.IsIgnored("keepass.EXE", ["KeePass.exe"]));
        Assert.False(ClipboardHistorySupport.IsIgnored(null, ["KeePass.exe"]));
    }

    [Fact]
    public void AgeIsSaidTheWayACardSaysIt()
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", ClipboardHistorySupport.Age(now.AddSeconds(-59), now));
        Assert.Equal("1 min", ClipboardHistorySupport.Age(now.AddSeconds(-60), now));
        Assert.Equal("59 min", ClipboardHistorySupport.Age(now.AddMinutes(-59), now));
        Assert.Equal("3 h", ClipboardHistorySupport.Age(now.AddHours(-3), now));
        Assert.Equal("2 d", ClipboardHistorySupport.Age(now.AddDays(-2), now));
        Assert.Equal("1 Jan 2026", ClipboardHistorySupport.Age(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void PreviewsAreCut()
    {
        Assert.Equal(2001, ClipboardHistorySupport.RowPreview(new string('a', 3000)).Length);
        Assert.Equal(201, ClipboardHistorySupport.Tooltip(new string('a', 3000)).Length);
        Assert.Equal("short", ClipboardHistorySupport.Tooltip("short"));
    }

    [Fact]
    public void TheDefaultsAreRegisteredAndSwitchedOff()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.False(store.GetBool(DefaultsKey.ClipboardHistoryEnabled));
        Assert.False(store.GetBool(DefaultsKey.PastePlainEnabled));
        Assert.False(store.GetBool(DefaultsKey.UrlCleanerEnabled));
        Assert.Equal(50, store.GetInt(DefaultsKey.ClipboardHistoryLimit));
        Assert.Equal(20, store.GetInt(DefaultsKey.ClipboardAutoClearDelaySeconds));
        Assert.True(store.GetBool(DefaultsKey.ClipboardHistoryIncludeImagesFiles));
        Assert.NotNull(Notchly.Core.Shortcuts.Shortcut.Parse(store.GetString(DefaultsKey.ClipboardHistoryShortcut)));
        Assert.NotNull(Notchly.Core.Shortcuts.Shortcut.Parse(store.GetString(DefaultsKey.PastePlainShortcut)));
        Assert.NotNull(Notchly.Core.Shortcuts.Shortcut.Parse(store.GetString(DefaultsKey.ShelfShortcut)));
    }
}

public class ClipboardPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheHistoryRoundTripsWithPinnedEntriesFirst()
    {
        var entries = new[]
        {
            ClipboardEntry.ForText("hello", Now, "notepad.exe") with { SourcePath = @"C:\Windows
otepad.exe" },
            ClipboardEntry.ForImage("a.png", "abc", 10, 20, Now),
            ClipboardEntry.ForFiles([@"C:\x.txt", @"C:\y.txt"], Now) with { PinnedAt = Now },
        };
        var json = ClipboardPersistence.Encode(entries);
        var back = ClipboardPersistence.Decode(json)!;
        Assert.Equal(3, back.Count);
        Assert.Equal(ClipboardKind.Files, back[0].Kind);
        Assert.True(back[0].IsPinned);
        Assert.Equal(["hello"], back.Where(e => e.Kind == ClipboardKind.Text).Select(e => e.Text));
        Assert.Equal("notepad.exe", back.Single(e => e.Kind == ClipboardKind.Text).SourceApp);
        Assert.Equal(@"C:\Windows
otepad.exe", back.Single(e => e.Kind == ClipboardKind.Text).SourcePath);
        var image = back.Single(e => e.Kind == ClipboardKind.Image);
        Assert.Equal(("a.png", "abc", 10, 20), (image.ImageFile, image.ImageHash, image.ImageWidth, image.ImageHeight));
        Assert.Equal(entries[0].Id, back.Single(e => e.Kind == ClipboardKind.Text).Id);
    }

    [Fact]
    public void ANewerEntryIsLeftOutBeforeAPinnedOneWhenTheFileIsFull()
    {
        var entries = new[]
        {
            ClipboardEntry.ForText(new string('a', 100), Now),
            ClipboardEntry.ForText("pin", Now) with { PinnedAt = Now },
        };
        var json = ClipboardPersistence.Encode(entries, maxBytes: 200);
        var back = ClipboardPersistence.Decode(json)!;
        Assert.Equal(["pin"], back.Select(e => e.Text));
        Assert.True(ClipboardPersistence.PinnedEntriesFit(entries, 200));
        Assert.False(ClipboardPersistence.PinnedEntriesFit(entries, 50));
    }

    [Fact]
    public void TheReaderIsLenientPerEntryAndRefusesAFileItCannotRead()
    {
        var json = """
            [
              {"copiedAt":"2026-03-01T12:00:00+00:00","text":"no kind or id"},
              {"kind":"files","copiedAt":"2026-03-01T12:00:00+00:00","filePaths":[]},
              {"kind":"text","text":"no date"},
              {"kind":"image","copiedAt":"2026-03-01T12:00:00+00:00","imageFile":"i.png","imageHash":"h"}
            ]
            """;
        var back = ClipboardPersistence.Decode(json)!;
        Assert.Equal(2, back.Count);
        Assert.Equal("no kind or id", back[0].Text);
        Assert.Equal(ClipboardKind.Text, back[0].Kind);
        Assert.NotEqual(Guid.Empty, back[0].Id);
        Assert.Null(ClipboardPersistence.Decode("not json"));
        Assert.Null(ClipboardPersistence.Decode("{}"));
        Assert.Null(ClipboardPersistence.Decode(null));
        Assert.Null(ClipboardPersistence.Decode(json, maxBytes: 10));
    }

    [Fact]
    public void ImageFilesNoEntryRefersToAreOrphans()
    {
        var entries = new[] { ClipboardEntry.ForImage("keep.png", "h", 1, 1, Now) };
        Assert.Equal(["gone.png"], ClipboardPersistence.OrphanImages(["KEEP.png", "gone.png"], entries));
    }
}

public class ClipboardAutoClearTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewCountRestartsTheClockEvenAfterALongWait() =>
        Assert.Equal(AutoClearDecision.NoteChange, ClipboardAutoClearSupport.Decide(5, 4, null, T0, T0.AddSeconds(600), 20));

    [Fact]
    public void ItWaitsUntilTheDelayIsReachedAndTheEdgeCounts()
    {
        Assert.Equal(AutoClearDecision.Wait, ClipboardAutoClearSupport.Decide(5, 5, null, T0, T0.AddSeconds(19), 20));
        Assert.Equal(AutoClearDecision.Clear, ClipboardAutoClearSupport.Decide(5, 5, null, T0, T0.AddSeconds(20), 20));
        Assert.Equal(AutoClearDecision.Clear, ClipboardAutoClearSupport.Decide(5, 5, null, T0, T0.AddHours(8), 20));
    }

    [Fact]
    public void AClipboardWeEmptiedOurselvesIsLeftAlone() =>
        Assert.Equal(AutoClearDecision.Wait, ClipboardAutoClearSupport.Decide(7, 7, 7, T0, T0.AddHours(1), 20));

    [Fact]
    public void TheFirstLookIsAChange() =>
        Assert.Equal(AutoClearDecision.NoteChange, ClipboardAutoClearSupport.Decide(5, null, null, T0, T0, 20));

    [Fact]
    public void TheDelayIsKeptBetweenFiveSecondsAndAnHour()
    {
        Assert.Equal(5, ClipboardAutoClearSupport.SanitizedDelay(1));
        Assert.Equal(3600, ClipboardAutoClearSupport.SanitizedDelay(99999));
        Assert.Equal(20, ClipboardAutoClearSupport.SanitizedDelay(20));
        Assert.Equal(AutoClearDecision.Wait, ClipboardAutoClearSupport.Decide(5, 5, null, T0, T0.AddSeconds(4), 1));
        Assert.Equal(AutoClearDecision.Clear, ClipboardAutoClearSupport.Decide(5, 5, null, T0, T0.AddSeconds(5), 1));
    }

    [Fact]
    public void AnEventSkipsAClipboardAlreadyEmptiedAtThisCount()
    {
        Assert.True(ClipboardAutoClearSupport.EventShouldClear(9, null));
        Assert.True(ClipboardAutoClearSupport.EventShouldClear(9, 8));
        Assert.False(ClipboardAutoClearSupport.EventShouldClear(9, 9));
    }
}

public class PastePlainTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/a.png")]
    [InlineData("file:///C:/x/y.png")]
    [InlineData("IMG_1234.HEIC")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("blob:https://example.com/xyz")]
    [InlineData("clip.mov")]
    [InlineData("scan.PDF")]
    public void MediaStandInsAreFallbacks(string? text) => Assert.True(PastePlainSupport.IsMediaTextFallback(text));

    [Theory]
    [InlineData("Item\tQty\nA\t1")]
    [InlineData("Total")]
    [InlineData("1.2.3")]
    [InlineData("example.com")]
    [InlineData("2.4GHz")]
    [InlineData("john@example.com")]
    [InlineData("two words.png")]
    public void RealTextIsNotAFallback(string text) => Assert.False(PastePlainSupport.IsMediaTextFallback(text));

    [Fact]
    public void FilesAlwaysForwardTheOriginalPaste() =>
        Assert.True(PastePlainSupport.Plan(hasFiles: true, hasMedia: false, "x", false, null, null).Forward);

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com/a.png")]
    [InlineData("IMG_1234.HEIC")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("   ")]
    public void MediaWithOnlyAStandInForwardsTheOriginalPaste(string? plain) =>
        Assert.True(PastePlainSupport.Plan(false, hasMedia: true, plain, false, null, null).Forward);

    [Fact]
    public void MediaWithRealTextOrRichTextPastesThePlainText()
    {
        Assert.Equal("Item\tQty\nA\t1", PastePlainSupport.Plan(false, true, "Item\tQty\nA\t1", false, null, null).Text);
        Assert.Equal("photo.png", PastePlainSupport.Plan(false, true, "photo.png", hasRichText: true, null, null).Text);
        Assert.True(PastePlainSupport.Plan(false, true, "photo.png", false, null, null).Forward);
    }

    [Fact]
    public void TextComesFromPlainThenRichTextThenHtml()
    {
        Assert.Equal("plain", PastePlainSupport.Plan(false, false, "plain", true, "rich", "html").Text);
        Assert.Equal("rich", PastePlainSupport.Plan(false, false, null, true, "rich", "html").Text);
        Assert.Equal("html", PastePlainSupport.Plan(false, false, "", false, null, "html").Text);
        Assert.True(PastePlainSupport.Plan(false, false, null, false, null, null).Forward);
    }

    [Fact]
    public void TheClipboardIsRestoredOnlyWhenNobodyChangedIt()
    {
        Assert.True(PastePlainSupport.ShouldRestore(10, 10));
        Assert.False(PastePlainSupport.ShouldRestore(10, 11));
    }
}

public class PasteImageTests
{
    [Fact]
    public void FilesOnTheClipboardLeaveThePasteToExplorer()
    {
        Assert.Null(PasteImageSupport.PreferredFormat(["FileDrop", "PNG"]));
        Assert.Null(PasteImageSupport.PreferredFormat(["Bitmap", "Shell IDList Array"]));
    }

    [Fact]
    public void PngIsPreferredThenBitmaps()
    {
        Assert.Equal("PNG", PasteImageSupport.PreferredFormat(["Bitmap", "PNG"]));
        Assert.Equal("Format17", PasteImageSupport.PreferredFormat(["Bitmap", "Format17", "DeviceIndependentBitmap"]));
        Assert.Equal("Bitmap", PasteImageSupport.PreferredFormat(["Bitmap"]));
        Assert.Null(PasteImageSupport.PreferredFormat(["UnicodeText"]));
    }

    [Fact]
    public void TheNameFollowsTheDateInvariantly() =>
        Assert.Equal("Pasted_Image_19700101_000000.png", PasteImageSupport.FileName(new DateTime(1970, 1, 1, 0, 0, 0)));

    [Fact]
    public void ANameInUseGetsANumberFromTwo()
    {
        var taken = new HashSet<string> { "a.png", "a 2.png", "a 3.png" };
        Assert.Equal("a 4.png", PasteImageSupport.UniqueName("a.png", taken.Contains));
        Assert.Equal("b.png", PasteImageSupport.UniqueName("b.png", taken.Contains));
    }

    [Fact]
    public void TheCapIs64MiB()
    {
        Assert.True(PasteImageSupport.WithinCap(64L * 1024 * 1024));
        Assert.False(PasteImageSupport.WithinCap(64L * 1024 * 1024 + 1));
        Assert.False(PasteImageSupport.WithinCap(0));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Pictures", true)]
    [InlineData(@"\\server\share\x", true)]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRealFoldersTakeAFile(string? path, bool expected) => Assert.Equal(expected, PasteImageSupport.IsWritableFolderPath(path));
}
