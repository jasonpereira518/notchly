// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/NotchDownloadProgressTests.swift (folder contents, merging,
// announcements) and NotchDownloadScanTests.swift (completed versus failed transfers), plus the Windows
// browser patterns: Chromium's "Unconfirmed" rename and Firefox's placeholder beside its .part.
using Notchly.Core.Downloads;

namespace Notchly.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notchly-downloads-" + Guid.NewGuid().ToString("N"));

    public DownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, int bytes = 0) => File.WriteAllBytes(path, new byte[bytes]);

    /// <summary>Identity by a fake table, so tests decide which entries are "the same file".</summary>
    private static Func<string, FileFacts?> FactsFrom(Dictionary<string, string> identities) => path =>
        File.Exists(path) && identities.TryGetValue(Path.GetFileName(path), out var id)
            ? new FileFacts(id, new FileInfo(path).Length, false)
            : null;

    private static FileFacts? PlainFacts(string path) =>
        File.Exists(path) ? new FileFacts(path.ToLowerInvariant(), new FileInfo(path).Length, false) : null;

    [Theory]
    [InlineData(@"C:\d\report.pdf.crdownload", @"C:\d\report.pdf")]
    [InlineData(@"C:\d\movie.mkv.PART", @"C:\d\movie.mkv")]
    [InlineData(@"C:\d\archive.zip.download", @"C:\d\archive.zip")]
    [InlineData(@"C:\d\setup.exe.partial", @"C:\d\setup.exe")]
    [InlineData(@"C:\d\Unconfirmed 123456.crdownload", @"C:\d\Unconfirmed 123456")]
    [InlineData(@"C:\d\.part", null)]
    [InlineData(@"C:\d\report.pdf", null)]
    [InlineData(@"C:\d\notes.txt.bak", null)]
    public void PartialsNameTheFileTheyBecome(string partial, string? expected) =>
        Assert.Equal(expected, DownloadSupport.ExpectedPath(partial));

    [Fact]
    public void OnlyDirectChildrenOfTheFolderCount()
    {
        Assert.True(DownloadSupport.IsDirectChild(@"C:\Users\me\Downloads\a.zip", @"C:\Users\me\Downloads\"));
        Assert.True(DownloadSupport.IsDirectChild(@"c:\users\ME\downloads\a.zip", @"C:\Users\me\Downloads"));
        Assert.False(DownloadSupport.IsDirectChild(@"C:\Users\me\Downloads\sub\a.zip", @"C:\Users\me\Downloads"));
        Assert.False(DownloadSupport.IsDirectChild(@"C:\Users\me\a.zip", @"C:\Users\me\Downloads"));
        Assert.False(DownloadSupport.IsDirectChild(@"Downloads\a.zip", @"C:\Users\me\Downloads"));
    }

    [Fact]
    public void FractionsNeedAKnownTotal()
    {
        Assert.Null(DownloadSupport.Fraction(5, 10, 0.5, indeterminate: true));
        Assert.Null(DownloadSupport.Fraction(5, 0, 0.5, indeterminate: false));
        Assert.Null(DownloadSupport.Fraction(-1, 10, 0.5, indeterminate: false));
        Assert.Null(DownloadSupport.Fraction(5, 10, double.NaN, indeterminate: false));
        Assert.Equal(0.5, DownloadSupport.Fraction(5, 10, 0.5, indeterminate: false));
        Assert.Equal(1.0, DownloadSupport.Fraction(12, 10, 1.2, indeterminate: false));
        Assert.Equal(1.0 / 2000, DownloadSupport.Fraction(1, 2000, 1.0 / 2000, indeterminate: false));
    }

    [Fact]
    public void FolderContentsListEveryVisibleEntryAndKeepPartialsApart()
    {
        var root = Folder("contents");
        var image = Path.Combine(root, "image.jpg");
        Write(image, 2);
        var hidden = Path.Combine(root, "hidden.txt");
        Write(hidden);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var desktopIni = Path.Combine(root, "desktop.ini");
        Write(desktopIni);
        File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);
        Write(Path.Combine(root, "transfer.download"));
        var linked = TryLink(Path.Combine(root, "alias"), image);
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        Write(Path.Combine(nested, "child.txt"));
        for (var index = 0; index < 40; index++) Write(Path.Combine(root, $"file-{index}"));

        var snapshot = DownloadSupport.ScanFolder(root, PlainFacts);
        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot!.Files.Count);
        Assert.Contains(snapshot.Files, f => f.Path == image && f.Completed && f.ReceivedBytes == 2);
        Assert.Single(snapshot.Partials);
        Assert.All(snapshot.Files, f => Assert.Equal(root, Path.GetDirectoryName(f.Path)));
        Assert.DoesNotContain(snapshot.Files, f => f.Name is "hidden.txt" or "desktop.ini" or "child.txt");
        if (linked) Assert.DoesNotContain(snapshot.Files, f => f.Name == "alias");

        var old = snapshot.Files.Single(f => f.Path == image) with { Date = new DateTime(2020, 1, 1) };
        var recent = snapshot.Files.Single(f => f.Path == nested) with { Date = new DateTime(2021, 1, 1) };
        var sorted = DownloadSupport.MergedItems([], [old, recent], [old]);
        Assert.Equal(new[] { nested, image }, sorted.Select(i => i.Path));

        var active = new DownloadItem(image, "image.jpg", 1, 0.5, Completed: false, Active: true, old.Date);
        Assert.Equal(new[] { active }, DownloadSupport.MergedItems([active], [old], []));

        File.Delete(image);
        Assert.DoesNotContain(DownloadSupport.ScanFolder(root, PlainFacts)!.Files, f => f.Path == image);
    }

    [Fact]
    public void ACrowdedFolderHandsOverOnlyItsNewestEntries()
    {
        var crowded = Folder("crowded");
        var start = DateTime.UtcNow.AddDays(-1);
        for (var index = 0; index < DownloadSupport.MaximumListedFiles + 20; index++)
        {
            var path = Path.Combine(crowded, $"entry-{index:000}");
            Write(path);
            File.SetCreationTimeUtc(path, start.AddMinutes(index));
        }
        var listed = DownloadSupport.ScanFolder(crowded, PlainFacts)!.Files;
        Assert.Equal(DownloadSupport.MaximumListedFiles, listed.Count);
        Assert.DoesNotContain(listed, f => f.Name == "entry-019");
        Assert.Contains(listed, f => f.Name == "entry-020");
    }

    [Fact]
    public void AnActivePartialHidesTheBrowsersPlaceholder()
    {
        // Firefox creates the final name empty beside the .part it writes.
        var root = Folder("placeholder");
        Write(Path.Combine(root, "movie.mkv"));
        Write(Path.Combine(root, "movie.mkv.part"), 10);
        var snapshot = DownloadSupport.ScanFolder(root, PlainFacts)!;
        var partial = snapshot.Partials.Values.Single();
        var active = new DownloadItem(partial.Path, "movie.mkv", partial.Bytes, null, false, true, partial.Created);
        var merged = DownloadSupport.MergedItems([active], snapshot.Files, []);
        Assert.Equal(new[] { partial.Path }, merged.Select(i => i.Path));
    }

    [Fact]
    public void AFinishedAndACancelledDownloadInOneScanAreToldApart()
    {
        var root = Folder("concurrent");
        var success = Path.Combine(root, "success.part");
        var cancelled = Path.Combine(root, "cancelled.part");
        Write(success, 1);
        Write(cancelled, 2);
        var identities = new Dictionary<string, string> { ["success.part"] = "1", ["success"] = "1", ["cancelled.part"] = "2" };
        var facts = FactsFrom(identities);
        var previous = DownloadSupport.ScanFolder(root, facts)!.Partials;

        File.Move(success, Path.Combine(root, "success"));
        File.Delete(cancelled);
        var current = DownloadSupport.ScanFolder(root, facts);
        var outcome = Compare(previous, current, facts);
        Assert.Equal(new[] { Path.Combine(root, "success") }, outcome.Completed.Select(c => c.Path));
        Assert.Equal(new[] { cancelled }, outcome.Failed.Select(f => f.Path));
        Assert.Empty(current!.Partials);
    }

    [Fact]
    public void AnUnrelatedFileAtTheExpectedNameIsNoArrival()
    {
        var root = Folder("unrelated");
        var partial = Path.Combine(root, "a.bin.crdownload");
        Write(partial, 1);
        var identities = new Dictionary<string, string> { ["a.bin.crdownload"] = "1", ["a.bin"] = "other" };
        var facts = FactsFrom(identities);
        var previous = DownloadSupport.ScanFolder(root, facts)!.Partials;
        File.Delete(partial);
        Write(Path.Combine(root, "a.bin"));
        var outcome = Compare(previous, DownloadSupport.ScanFolder(root, facts), facts);
        Assert.Empty(outcome.Completed);
        Assert.Empty(outcome.Failed); // something sits at its name, so it is not called a failure either
    }

    [Fact]
    public void ChromiumsUnconfirmedNameBecomingTheRealOneIsNoFailure()
    {
        var root = Folder("unconfirmed");
        var unconfirmed = Path.Combine(root, "Unconfirmed 482913.crdownload");
        Write(unconfirmed, 5);
        var identities = new Dictionary<string, string>
        {
            ["Unconfirmed 482913.crdownload"] = "7", ["report.pdf.crdownload"] = "7", ["report.pdf"] = "7",
        };
        var facts = FactsFrom(identities);
        var first = DownloadSupport.ScanFolder(root, facts)!.Partials;

        var named = Path.Combine(root, "report.pdf.crdownload");
        File.Move(unconfirmed, named);
        var second = DownloadSupport.ScanFolder(root, facts)!;
        var renamed = Compare(first, second, facts);
        Assert.Empty(renamed.Completed);
        Assert.Empty(renamed.Failed);

        File.Move(named, Path.Combine(root, "report.pdf"));
        var finished = Compare(second.Partials, DownloadSupport.ScanFolder(root, facts), facts);
        Assert.Equal(new[] { "report.pdf" }, finished.Completed.Select(c => c.Name));
        Assert.Empty(finished.Failed);
    }

    [Fact]
    public void FirefoxMovesItsPartOverThePlaceholderOrDeletesBoth()
    {
        var root = Folder("firefox");
        var placeholder = Path.Combine(root, "movie.mkv");
        var part = Path.Combine(root, "movie.mkv.part");
        Write(placeholder);
        Write(part, 10);
        // After the move, the final name carries the .part's identity.
        var identities = new Dictionary<string, string> { ["movie.mkv.part"] = "part" , ["movie.mkv"] = "placeholder" };
        var previous = DownloadSupport.ScanFolder(root, FactsFrom(identities))!.Partials;

        File.Move(part, placeholder, overwrite: true);
        identities["movie.mkv"] = "part";
        var facts = FactsFrom(identities);
        var finished = Compare(previous, DownloadSupport.ScanFolder(root, facts), facts);
        Assert.Equal(new[] { "movie.mkv" }, finished.Completed.Select(c => c.Name));
        Assert.Equal(10, finished.Completed[0].ReceivedBytes);

        var again = Folder("firefox-cancel");
        Write(Path.Combine(again, "movie.mkv"));
        Write(Path.Combine(again, "movie.mkv.part"), 10);
        identities["movie.mkv"] = "placeholder";
        var before = DownloadSupport.ScanFolder(again, FactsFrom(identities))!.Partials;
        File.Delete(Path.Combine(again, "movie.mkv.part"));
        File.Delete(Path.Combine(again, "movie.mkv"));
        var cancelled = Compare(before, DownloadSupport.ScanFolder(again, FactsFrom(identities)), FactsFrom(identities));
        Assert.Empty(cancelled.Completed);
        Assert.Single(cancelled.Failed);
    }

    [Fact]
    public void ARenameTheWatcherSawSuppressesTheFailureAndAnUnreadFolderDecidesNothing()
    {
        var root = Folder("evidence");
        var partial = Path.Combine(root, "Unconfirmed 1.crdownload");
        Write(partial, 1);
        var facts = FactsFrom(new() { ["Unconfirmed 1.crdownload"] = "1" });
        var previous = DownloadSupport.ScanFolder(root, facts)!.Partials;
        File.Move(partial, Path.Combine(root, "report.pdf"));
        var current = DownloadSupport.ScanFolder(root, facts);
        Assert.Single(Compare(previous, current, facts).Failed);
        Assert.Empty(DownloadSupport.Compare(previous, current, facts, File.Exists, _ => true, DateTime.UtcNow).Failed);
        Assert.Empty(Compare(previous, null, facts).Failed);
    }

    [Fact]
    public void RenamesIntoAFinalNameAreArrivalsOnlyForVisibleDirectEntries()
    {
        var root = Folder("renamed");
        var file = Path.Combine(root, "report-1.pdf");
        Write(file, 2);
        var opened = Path.Combine(root, "archive");
        Directory.CreateDirectory(opened);
        var nested = Path.Combine(opened, "inside.txt");
        Write(nested);
        var now = DateTime.UtcNow;

        var item = DownloadSupport.RenamedItem(file + ".crdownload", file, root, now);
        Assert.NotNull(item);
        Assert.Equal(("report-1.pdf", 2L, true), (item!.Name, item.ReceivedBytes!.Value, item.Completed));
        Assert.Equal("archive", DownloadSupport.RenamedItem(opened + ".download", opened, root, now)?.Name);

        Assert.Null(DownloadSupport.RenamedItem(Path.Combine(root, "x.txt"), file, root, now)); // not from a partial
        Assert.Null(DownloadSupport.RenamedItem(file + ".part", file + ".crdownload", root, now)); // still partial
        Assert.Null(DownloadSupport.RenamedItem(file + ".part", Path.Combine(root, "missing"), root, now));
        Assert.Null(DownloadSupport.RenamedItem(nested + ".part", nested, root, now));
        Assert.Null(DownloadSupport.RenamedItem(file + ".part", Path.Combine(_root, "outside"), root, now));
        Assert.Null(DownloadSupport.RenamedItem("report-1.pdf.part", "report-1.pdf", root, now));
        var alias = Path.Combine(root, "alias");
        if (TryLink(alias, file)) Assert.Null(DownloadSupport.RenamedItem(alias + ".part", alias, root, now));
    }

    [Fact]
    public void GrowthDecidesActiveAndRate()
    {
        var tracker = new DownloadProgressTracker();
        var t0 = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        tracker.Observe("a", 0, t0, t0);
        Assert.True(tracker.IsActive("a", t0));
        Assert.Null(tracker.Rate("a", t0));
        for (var second = 1; second <= 5; second++) tracker.Observe("a", second * 1_000_000L, t0, t0.AddSeconds(second));
        Assert.Equal(1_000_000, tracker.Rate("a", t0.AddSeconds(5))!.Value, 3);
        tracker.Observe("a", 5_000_000, t0, t0.AddSeconds(8));
        Assert.True(tracker.Rate("a", t0.AddSeconds(8)) < 1_000_000);
        Assert.True(tracker.IsActive("a", t0.AddSeconds(124)));
        Assert.False(tracker.IsActive("a", t0.AddSeconds(126)));

        // A partial left behind long ago starts paused.
        tracker.Observe("stale", 10, t0.AddDays(-3), t0);
        Assert.False(tracker.IsActive("stale", t0));
        tracker.Retain(["a"]);
        Assert.False(tracker.IsActive("stale", t0));
        Assert.Null(tracker.Rate("stale", t0));
    }

    [Fact]
    public void CompactWingFitsTheNameWithinItsBounds()
    {
        Assert.Equal(56, DownloadSupport.CompactWing(null, 80, 8));
        Assert.Equal(56, DownloadSupport.CompactWing("  ", 80, 8));
        Assert.Equal(56, DownloadSupport.CompactWing("a.zip", 80, 8, room: 60));
        Assert.Equal(64, DownloadSupport.CompactWing("a", 20, 8));
        Assert.Equal(93, DownloadSupport.CompactWing("report.pdf", 80.5, 8));
        Assert.Equal(160, DownloadSupport.CompactWing("a very long installer name.exe", 400, 8));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(50_541_363, "48.2 MB")]
    [InlineData(327_155_712, "312 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void SizesReadAsExplorerWritesThem(long bytes, string text)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { Assert.Equal(text, DownloadSupport.FormatBytes(bytes)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void ProgressLinesSaySizeAndSpeedInsteadOfAPercentage()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { CheckProgressLines(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static void CheckProgressLines()
    {
        var item = new DownloadItem(@"C:\d\a.zip.crdownload", "a.zip", 50_541_363, null, false, true, DateTime.UtcNow)
            { BytesPerSecond = 3_250_586 };
        Assert.Equal("Downloading · 48.2 MB · 3.1 MB/s", DownloadSupport.ProgressLine(item));
        Assert.Equal("Paused · 48.2 MB", DownloadSupport.ProgressLine(item with { Active = false }));
        Assert.Equal("Downloading · Total size unavailable", DownloadSupport.ProgressLine(item with { ReceivedBytes = 0, BytesPerSecond = null }));
    }

    private static DownloadScanOutcome Compare(IReadOnlyDictionary<string, PartialDownload> previous,
                                               DownloadFolderSnapshot? current, Func<string, FileFacts?> facts) =>
        DownloadSupport.Compare(previous, current, facts, p => File.Exists(p) || Directory.Exists(p), _ => false, DateTime.UtcNow);

    /// <summary>Symbolic links need Developer Mode or elevation on Windows; tests skip that part without them.</summary>
    private static bool TryLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
