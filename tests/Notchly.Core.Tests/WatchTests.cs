// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/NotchWatchTests.swift (readings, numbers, changes, settling, matches, areas, pictures and
// the gate) with the same numbers, and of NotchWatchChoiceTests.swift's choice lifecycle as NotchWatchChoices.
// Not ported: the mascot reactions, the macOS permission list and the ScreenCaptureKit source pins.
using System.Globalization;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class WatchTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => Start.AddSeconds(seconds);
    private const double Hold = NotchWatchTracker.ChangeConfirmation;

    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    // MARK: Readings

    [Fact]
    public void ReadingsPickWhatIsWorthShowing()
    {
        Assert.Equal("73%", NotchWatchSupport.Headline("Exporting video\n73 % complete"));
        Assert.Equal("1250%", NotchWatchSupport.Headline("All cores\n1250% CPU"));
        Assert.Equal("−5%", NotchWatchSupport.Headline("Change today\n−5 %"));
        Assert.Equal("Build Succeeded", NotchWatchSupport.Headline("  Build Succeeded \n"));
        Assert.Equal("14:30", NotchWatchSupport.Headline("Your order will arrive at the door at 14:30 today"));
        Assert.Equal("3.2 GB", NotchWatchSupport.Headline("Uploading the whole project folder, 3.2 GB left"));
        var cut = NotchWatchSupport.Headline("Waiting for the other participants to join the call");
        Assert.Equal(NotchWatchSupport.HeadlineLength, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal("", NotchWatchSupport.Headline(" \n "));
    }

    [Fact]
    public void AChosenLineIsWhatTheIslandShows()
    {
        const string area = "Project Alpha\nUploading 3 files\n12 MB of 40 MB";
        Assert.Equal(["Project Alpha", "Uploading 3 files", "12 MB of 40 MB"], NotchWatchSupport.Lines(area));
        Assert.Equal("12 MB of 40 MB", NotchWatchSupport.Headline(area, line: 2));
        Assert.Equal("Project Alpha", NotchWatchSupport.Headline(area, line: null));
        Assert.Equal("Done", NotchWatchSupport.Headline("Done", line: 2));
        Assert.Equal("concluido agora", NotchWatchSupport.Normalized("  Concluído\n  AGORA "));
    }

    // MARK: Numbers

    public static TheoryData<string, double?> Readings => new()
    {
        { "45%", 45 }, { "Progress 12,5 %", 12.5 }, { "1,234 of 5,000 files", 1234 },
        { "1.234,5 MB", 1234.5 }, { "1,234.5 MB", 1234.5 }, { "Score -3", -3 },
        { "3 of 10, then 80% done", 80 }, { "No numbers here", null }, { "1.234.567", 1234567 },
        { "1250%", 1250 }, { "Zoom 1,250%", 1250 }, { "CPU 1250.5 %", 1250.5 },
        { "−5%", -5 }, { "Change −12,5 %", -12.5 }, { "-5%", -5 },
    };

    [Theory, MemberData(nameof(Readings))]
    public void NumbersAreReadInEitherMark(string text, double? expected) =>
        Assert.Equal(expected, NotchWatchSupport.Number(text));

    [Fact]
    public void SpacedPercentagesKeepEveryDigitAndTheirSign()
    {
        foreach (var space in new[] { " ", " ", " " })
        foreach (var (sign, multiplier) in new[] { ("", 1.0), ("+", 1.0), ("-", -1.0), ("−", -1.0) })
        {
            var reading = $"Change {sign}1{space}250,5{space}%";
            Assert.Equal(multiplier * 1250.5, NotchWatchSupport.Number(reading, ",", groupsWithSpace: true));
            Assert.Equal($"{sign}1250,5%", NotchWatchSupport.Headline(reading, groupsWithSpace: true));
        }
    }

    [Theory]
    [InlineData("Step 2 100%", 100)]
    [InlineData("1 of 3 100%", 100)]
    [InlineData("Elapsed 02:13 100%", 100)]
    [InlineData("CPU 2 150%", 150)]
    public void ANumberBeforeAPercentageIsNotJoinedToIt(string reading, double percent)
    {
        Assert.Equal(percent, NotchWatchSupport.Number(reading));
        Assert.Equal($"{(int)percent}%", NotchWatchSupport.Headline(reading, groupsWithSpace: false));
    }

    [Fact]
    public void PercentagesStayOnTheirLine()
    {
        Assert.Equal(45, NotchWatchSupport.Number("Progress 45\n% done"));
        Assert.Equal("Progress 45", NotchWatchSupport.Headline("Progress 45\n% done", groupsWithSpace: true));
        Assert.Equal(50, NotchWatchSupport.Number("Step 2 50%"));
        Assert.Equal(250, NotchWatchSupport.Number("Step 2\n250%", groupsWithSpace: true));
        Assert.Equal("250%", NotchWatchSupport.Headline("Step 2\n250%"));
    }

    [Fact]
    public void ALoneMarkBeforeThreeDigitsFollowsTheRegion()
    {
        Assert.Equal(1000, NotchWatchSupport.Number("1.000", ","));
        Assert.Equal(12500, NotchWatchSupport.Number("12.500", ","));
        Assert.Equal(12.5, NotchWatchSupport.Number("12,500", ","));
        Assert.Equal(1, NotchWatchSupport.Number("1.000", "."));
        Assert.Equal(12500, NotchWatchSupport.Number("12,500", "."));
        Assert.Equal(0.125, NotchWatchSupport.Number("0,125"));
        Assert.Equal(0.125, NotchWatchSupport.Number("0.125", ","));
    }

    [Fact]
    public void TypedTargetsReadBackAsTheyAreShown()
    {
        foreach (var name in new[] { "pt-BR", "de-DE", "en-US", "fr-FR", "ja-JP" })
        {
            var culture = CultureInfo.GetCultureInfo(name);
            foreach (var value in new[] { 1000.0, 25_000, 1_234_567, 12.5, 0.25 })
                Assert.Equal(value, NotchWatchSupport.TypedNumber(NotchWatchSupport.Formatted(value, culture), culture));
        }
        Assert.Equal(1000, NotchWatchSupport.TypedNumber("1.000", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal(1.5, NotchWatchSupport.TypedNumber("1,5", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal(10000, NotchWatchSupport.TypedNumber("10 000", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal(1000, NotchWatchSupport.TypedNumber("1,000", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void AWindowsNumbersAreReadAsTheRegionWritesThem()
    {
        var brazil = CultureInfo.GetCultureInfo("pt-BR");
        var climbing = NotchWatchTracker.For(NotchWatchCondition.Reaches, "", 1000, brazil);
        Assert.Null(climbing.Observe("a", "850 de 1.200", Start));
        Assert.Equal(new NotchWatchOutcome.Reached("1.000 de 1.200"), climbing.Observe("b", "1.000 de 1.200", Start));

        var france = CultureInfo.GetCultureInfo("fr-FR");
        var percentage = NotchWatchTracker.For(NotchWatchCondition.Reaches, "", 1000, france);
        Assert.Null(percentage.Observe("a", "900%", Start));
        Assert.Equal(new NotchWatchOutcome.Reached("1250%"), percentage.Observe("b", "1 250%", Start));

        var falling = NotchWatchTracker.For(NotchWatchCondition.Reaches, "", 10, france);
        Assert.Null(falling.Observe("a", "Reste 10 000 fichiers", Start));
        Assert.Null(falling.Observe("b", "Reste 999 fichiers", Start));
        Assert.Equal(new NotchWatchOutcome.Reached("Reste 3 fichiers"), falling.Observe("c", "Reste 3 fichiers", Start));

        Assert.Equal(3, NotchWatchSupport.Number("3 of 10 000", ",", groupsWithSpace: true));
        Assert.Equal(10000.5, NotchWatchSupport.Number("10 000,5 Mo", ",", groupsWithSpace: true));
        Assert.True(NotchWatchSupport.GroupsWithSpace(france));
        Assert.False(NotchWatchSupport.GroupsWithSpace(brazil));
    }

    // MARK: Changes

    [Fact]
    public void AChangeMustHoldBeforeItCounts()
    {
        var tracker = new NotchWatchTracker(NotchWatchCondition.Changes);
        Assert.Null(tracker.Observe("uploading", "Uploading", At(0)));
        Assert.Null(tracker.Observe(null, "Uploading", At(2)));
        Assert.Null(tracker.Observe("uploading", "Uploading", At(4)));
        // A reading that flips back is noise, not a change.
        Assert.Null(tracker.Observe("uploadin", "Uploadin", At(6)));
        Assert.Null(tracker.Observe("uploading", "Uploading", At(6 + Hold)));
        // A new reading waits a few seconds before it counts.
        Assert.Null(tracker.Observe("done", "Done", At(20)));
        Assert.Null(tracker.Observe("done", "Done", At(21)));
        Assert.Equal(new NotchWatchOutcome.Changed(), tracker.Observe("done", "Done", At(20 + Hold)));
    }

    [Fact]
    public void AStillAreaConfirmsAChangeAtTheBackgroundPace()
    {
        var still = new NotchWatchTracker(NotchWatchCondition.Changes);
        still.Observe("uploading", "Uploading", At(0));
        Assert.Null(still.Observe("done", "Done", At(2)));
        Assert.Null(still.Observe("done", "Done", At(4)));
        Assert.Equal(new NotchWatchOutcome.Changed(), still.Observe("done", "Done", At(6)));

        var moving = new NotchWatchTracker(NotchWatchCondition.Changes);
        moving.Observe("12%", "12%", At(0));
        Assert.Null(moving.Observe("13%", "13%", At(1)));
        Assert.Null(moving.Observe("14%", "14%", At(2)));
        Assert.Equal(new NotchWatchOutcome.Changed(), moving.Observe("15%", "15%", At(1 + Hold)));
    }

    private static string? Picture(byte level, int nudged = 0)
    {
        var cells = Enumerable.Repeat(level, 256).ToArray();
        for (var i = 0; i < nudged; i++) cells[i] = (byte)(level + 1);
        return NotchWatchSupport.Signature("", cells);
    }

    [Fact]
    public void APictureMustMoveAndHold()
    {
        var bar = new NotchWatchTracker(NotchWatchCondition.Changes);
        bar.Observe(Picture(4), "", At(0));
        Assert.Null(bar.Observe(Picture(4, nudged: 3), "", At(2)));
        Assert.Null(bar.Observe(Picture(4, nudged: 2), "", At(2 + Hold)));
        Assert.Null(bar.Observe(Picture(9), "", At(10)));
        Assert.Equal(new NotchWatchOutcome.Changed(), bar.Observe(Picture(9), "", At(10 + Hold)));
    }

    // MARK: Settling

    [Fact]
    public void SettlingNeedsAChangeThenStillness()
    {
        var tracker = new NotchWatchTracker(NotchWatchCondition.Settles);
        tracker.Observe("line 1", "line 1", At(0));
        Assert.Null(tracker.Observe(null, "line 1", At(120)));
        tracker.Observe("line 2", "line 2", At(121));
        Assert.Null(tracker.Observe(null, "line 2", At(140)));
        Assert.Equal(new NotchWatchOutcome.Settled(), tracker.Observe("line 2", "line 2", At(121 + NotchWatchTracker.SettleInterval)));
        Assert.True(NotchWatchSupport.SameSignature("done", "done"));
        Assert.False(NotchWatchSupport.SameSignature("done", null));
        Assert.False(NotchWatchSupport.SameSignature("done", "image:1.2"));
    }

    // MARK: Matches

    [Fact]
    public void WordsAndNumbersAreFound()
    {
        var words = new NotchWatchTracker(NotchWatchCondition.Contains, "concluida");
        Assert.Null(words.Observe("x", "Exportando…", Start));
        Assert.Equal(new NotchWatchOutcome.Shows("concluida"), words.Observe("y", "Exportação CONCLUÍDA", Start));

        var empty = new NotchWatchTracker(NotchWatchCondition.Contains, "  ");
        Assert.False(empty.IsReady);
        Assert.Null(empty.Observe("x", "anything", Start));

        var up = new NotchWatchTracker(NotchWatchCondition.Reaches, target: 100);
        Assert.Null(up.Observe("a", "Copying 40%", Start));
        Assert.Null(up.Observe("b", "Copying 99%", Start));
        Assert.Equal(new NotchWatchOutcome.Reached("100%"), up.Observe("c", "Copying 100%", Start));

        var down = new NotchWatchTracker(NotchWatchCondition.Reaches, target: 0);
        Assert.Null(down.Observe("a", "5 left", Start));
        Assert.Equal(new NotchWatchOutcome.Reached("0 left"), down.Observe("b", "0 left", Start));

        var already = new NotchWatchTracker(NotchWatchCondition.Reaches, target: 3);
        Assert.Equal(new NotchWatchOutcome.Reached("3"), already.Observe("a", "3", Start));

        var unread = new NotchWatchTracker(NotchWatchCondition.Reaches, target: 10);
        Assert.Null(unread.Observe("a", "no digits", Start));
        Assert.Null(unread.Start);
    }

    [Fact]
    public void OutcomesSayWhatHappened()
    {
        Assert.Equal("It changed", new NotchWatchOutcome.Changed().Title);
        Assert.Equal("It stopped changing", new NotchWatchOutcome.Settled().Title);
        Assert.Equal("It shows Done", new NotchWatchOutcome.Shows("Done").Title);
        Assert.Equal("It reached 100", new NotchWatchOutcome.Reached("100").Title);
        Assert.Equal("The window closed", new NotchWatchOutcome.Closed().Title);
    }

    // MARK: Areas

    [Fact]
    public void AreasFollowTheirWindow()
    {
        (int, RectD)[] windows = [(7, new RectD(100, 100, 400, 300)), (3, new RectD(0, 0, 1000, 800))];
        var front = NotchWatchSupport.WindowCrop(new RectD(150, 120, 100, 40), windows);
        Assert.Equal((7, new RectD(50, 20, 100, 40)), front);
        var overhanging = NotchWatchSupport.WindowCrop(new RectD(420, 360, 100, 60), windows);
        Assert.Equal((7, new RectD(320, 260, 80, 40)), overhanging);
        Assert.Null(NotchWatchSupport.WindowCrop(new RectD(1200, 10, 50, 50), windows));

        Assert.Equal(new RectD(100, 40, 200, 80),
            NotchWatchSupport.PixelCrop(new RectD(50, 20, 100, 40), new SizeD(400, 300), new SizeD(800, 600)));
        Assert.Equal(new RectD(350, 250, 50, 50),
            NotchWatchSupport.PixelCrop(new RectD(350, 250, 100, 100), new SizeD(400, 300), new SizeD(400, 300)));
        Assert.Null(NotchWatchSupport.PixelCrop(new RectD(500, 500, 10, 10), new SizeD(400, 300), new SizeD(400, 300)));
    }

    // MARK: Pictures

    private static byte[] Image(Func<int, int, double> grey, int width = 64, int height = 32)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var level = (byte)Math.Round(grey(x, y) * 255);
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void PicturesCompareCoarsely()
    {
        var empty = Image((_, _) => 0.1);
        var half = Image((x, _) => x < 32 ? 0.9 : 0.1);
        var a = NotchWatchSupport.Fingerprint(empty, 64, 32, 64 * 4);
        var b = NotchWatchSupport.Fingerprint(empty, 64, 32, 64 * 4);
        var c = NotchWatchSupport.Fingerprint(half, 64, 32, 64 * 4);
        Assert.NotNull(a);
        Assert.True(NotchWatchSupport.SameFingerprint(a, b));
        Assert.False(NotchWatchSupport.SameFingerprint(a, c));
        Assert.Equal("done", NotchWatchSupport.Signature("  Done ", c));
        Assert.StartsWith("image:", NotchWatchSupport.Signature("", c));
    }

    // MARK: The gate

    [Fact]
    public void WatchNeedsTheIslandItsSwitchAndItsPage()
    {
        var store = NewStore();
        Assert.False(NotchWatchSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchEnabled, true);
        Assert.True(NotchWatchSupport.IsEnabled(store));
        Assert.Equal(NotchWatchCondition.Changes, NotchWatchSupport.SavedCondition(store));
        store.Set(DefaultsKey.NotchWatchCondition, "settles");
        Assert.Equal(NotchWatchCondition.Settles, NotchWatchSupport.SavedCondition(store));

        store.Set(DefaultsKey.NotchHiddenModules, "watch");
        Assert.False(NotchWatchSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchHiddenModules, "");
        store.Set(DefaultsKey.NotchWatchEnabled, false);
        Assert.False(NotchWatchSupport.IsEnabled(store));
        store.Set(DefaultsKey.NotchWatchEnabled, true);
        store.Set(FeatureCatalog.Info(AppFeature.NotchWatch).AvailabilityKey, false);
        Assert.False(NotchWatchSupport.IsEnabled(store));

        Assert.Equal([NotchCompactActivity.Timer, NotchCompactActivity.Watch, NotchCompactActivity.Downloads, NotchCompactActivity.Music],
            NotchActivities.Prioritized([NotchCompactActivity.Music, NotchCompactActivity.Downloads, NotchCompactActivity.Watch, NotchCompactActivity.Timer]));
    }

    [Fact]
    public void WatchStartsOnWithASoundAndIsPorted()
    {
        var defaults = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(true, defaults[DefaultsKey.NotchWatchEnabled]);
        Assert.Equal(true, defaults[DefaultsKey.NotchWatchSound]);
        Assert.Equal("changes", defaults[DefaultsKey.NotchWatchCondition]);
        var info = FeatureCatalog.Info(AppFeature.NotchWatch);
        Assert.Equal(WindowsStatus.Ported, info.Status);
        Assert.True(info.InstalledByDefault);
    }

    // MARK: Choosing (upstream NotchWatchChoiceTests)

    [Fact]
    public void StaleChoicesCannotStartWatching()
    {
        var stopped = new NotchWatchChoices();
        var late = stopped.Begin();
        stopped.Cancel();
        Assert.False(stopped.Accepts(late, enabled: true));

        var disabled = new NotchWatchChoices();
        Assert.False(disabled.Accepts(disabled.Begin(), enabled: false));

        var replaced = new NotchWatchChoices();
        var older = replaced.Begin();
        var newer = replaced.Begin();
        Assert.True(replaced.Accepts(newer, enabled: true));
        Assert.False(replaced.Accepts(older, enabled: true));

        var valid = new NotchWatchChoices();
        Assert.True(valid.Accepts(valid.Begin(), enabled: true));
    }

    [Fact]
    public void TheStripFitsItsReading()
    {
        Assert.Equal(56, NotchWatchSupport.FittedWing(10));
        Assert.Equal(150, NotchWatchSupport.FittedWing(400));
        Assert.Equal(91, NotchWatchSupport.FittedWing(90.2));
        Assert.Equal(56, NotchWatchSupport.FittedWing(double.NaN));
    }
}
