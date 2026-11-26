// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream ScreenshotFeatureTests (file naming ~818-841, unique name ~1742-1757, copied-file prune ~1824, clipboard scale ~3878,
// density ~2870-2905), ScreenshotImageFileTests (format choice, transparency), ScreenshotImageImportTests (the size limit and
// orientation, pure parts) and ScreenshotClipboardTests (point size). The drag, pasteboard and encoder parts need AppKit and are skipped.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class ScreenshotNamingTests
{
    // 2026-07-24 15:04:09, the date the upstream pattern tests use.
    private static readonly DateTime Pattern = new(2026, 7, 24, 15, 4, 9);

    [Fact]
    public void ShortDateTokensExpandZeroPadded() =>
        Assert.Equal("26-07", ScreenshotNaming.ExpandSaveSubfolder("%y-%mo", Pattern));

    [Fact]
    public void LongDateTokensExpandBeforeTheirShortPrefixesAndNestWithSlashes() =>
        Assert.Equal("2026/July", ScreenshotNaming.ExpandSaveSubfolder("%year/%month", Pattern));

    [Fact]
    public void AnEmptySubfolderPatternMeansNoSubfolder() =>
        Assert.Equal("", ScreenshotNaming.ExpandSaveSubfolder("", Pattern));

    [Fact]
    public void DotDotDotAndEmptyComponentsNeverEscapeTheBaseFolder() =>
        Assert.Equal("up/26", ScreenshotNaming.ExpandSaveSubfolder("../up//./%y", Pattern));

    [Fact]
    public void ABackSlashNestsLikeASlash() =>
        Assert.Equal("2026/07", ScreenshotNaming.ExpandSaveSubfolder("%year\\%mo", Pattern));

    [Fact]
    public void AnAllDotsPatternIsNoSubfolder() =>
        Assert.Equal("", ScreenshotNaming.ExpandSaveSubfolder("../..", Pattern));

    [Fact]
    public void WindowsReservedCharactersInASubfolderBecomeDashes() =>
        Assert.Equal("a-b-c", ScreenshotNaming.ExpandSaveSubfolder("a*b?c", Pattern));

    [Theory]
    [InlineData("%year", "2026")]
    [InlineData("%month", "July")]
    [InlineData("%y", "26")]
    [InlineData("%mo", "07")]
    [InlineData("%d", "24")]
    [InlineData("%h", "15")]
    [InlineData("%mi", "04")]
    [InlineData("%s", "09")]
    public void EveryTokenExpands(string token, string expected) =>
        Assert.Equal(expected, ScreenshotNaming.ExpandFileNamePattern(token, Pattern, 0));

    [Fact]
    public void FileNamePatternsExpandTheDayAndTimeTokens() =>
        Assert.Equal("Shot 24 at 15.04.09", ScreenshotNaming.ExpandFileNamePattern("Shot %d at %h.%mi.%s", Pattern, 0));

    [Fact]
    public void ASingleNumberTokenRendersTheSequenceUnpadded() =>
        Assert.Equal("Shot-7", ScreenshotNaming.ExpandFileNamePattern("Shot-%#", Pattern, 7));

    [Fact]
    public void ALongerNumberTokenZeroPadsToItsLength() =>
        Assert.Equal("007-26", ScreenshotNaming.ExpandFileNamePattern("%###-%y", Pattern, 7));

    [Fact]
    public void TwoNumberTokensEachPadOnTheirOwn() =>
        Assert.Equal("07-00007", ScreenshotNaming.ExpandFileNamePattern("%##-%#####", Pattern, 7));

    [Fact]
    public void ANumberWiderThanItsTokenIsNotCut() =>
        Assert.Equal("12345", ScreenshotNaming.ExpandFileNamePattern("%##", Pattern, 12345));

    [Fact]
    public void OnlyPatternsWithTheNumberTokenConsumeTheSequence()
    {
        Assert.True(ScreenshotNaming.FileNamePatternUsesNumber("Shot-%#"));
        Assert.False(ScreenshotNaming.FileNamePatternUsesNumber("Shot-%y"));
    }

    [Fact]
    public void SlashesAndColonsInAFileNamePatternBecomeDashes() =>
        Assert.Equal("a-b 15-04", ScreenshotNaming.ExpandFileNamePattern("a/b %h:%mi", Pattern, 0));

    [Fact]
    public void OtherCharactersWindowsRefusesBecomeDashesToo() =>
        Assert.Equal("a-b-c-d-e-f-g", ScreenshotNaming.ExpandFileNamePattern("a\\b*c?d\"e<f>g", Pattern, 0));

    [Fact]
    public void ThePipeIsRefusedToo() =>
        Assert.Equal("a-b", ScreenshotNaming.ExpandFileNamePattern("a|b", Pattern, 0));

    [Fact]
    public void ADateTokenInsideAWordStillExpands() =>
        Assert.Equal("shot26", ScreenshotNaming.ExpandFileNamePattern("shot%y", Pattern, 0));

    [Fact]
    public void ADefaultNameIsDatedColonFreeAndPng()
    {
        var name = ScreenshotNaming.FileName("Screenshot", new DateTime(2025, 7, 14, 9, 41, 5));
        Assert.Equal("Screenshot 2025-07-14 at 09.41.05.png", name);
        Assert.DoesNotContain(':', name);
    }

    [Fact]
    public void ThePrefixAndExtensionAreOurs() =>
        Assert.Equal("Bildschirmfoto 2025-12-01 at 23.59.59.jpg",
            ScreenshotNaming.FileName("Bildschirmfoto", new DateTime(2025, 12, 1, 23, 59, 59), "jpg"));

    [Fact]
    public void ADefaultNameIgnoresTheCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("Screenshot 2025-07-14 at 09.41.05.png", ScreenshotNaming.FileName("Screenshot", new DateTime(2025, 7, 14, 9, 41, 5)));
            Assert.Equal("July", ScreenshotNaming.ExpandFileNamePattern("%month", Pattern, 0));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    // MARK: - Unique names

    [Fact]
    public void AFreeNameStaysUntouched() =>
        Assert.Equal("a.png", ScreenshotNaming.UniqueFileName("a.png", _ => false));

    [Fact]
    public void ATakenNameGetsTheNextNumberedVariant() =>
        Assert.Equal("a 2.png", ScreenshotNaming.UniqueFileName("a.png", n => n == "a.png"));

    [Fact]
    public void NumberingKeepsWalkingUntilAFreeName() =>
        Assert.Equal("a 3.png", ScreenshotNaming.UniqueFileName("a.png", n => n is "a.png" or "a 2.png"));

    [Fact]
    public void ANameWithoutAnExtensionGetsASuffix() =>
        Assert.Equal("a 2", ScreenshotNaming.UniqueFileName("a", n => n == "a"));

    [Fact]
    public void OnlyTheLastExtensionIsKept() =>
        Assert.Equal("a.b 2.png", ScreenshotNaming.UniqueFileName("a.b.png", n => n == "a.b.png"));

    [Fact]
    public void AllVariantsTakenGivesBackTheOriginalName() =>
        Assert.Equal("a.png", ScreenshotNaming.UniqueFileName("a.png", _ => true));

    [Fact]
    public void TheWalkReaches9999()
    {
        var taken = Enumerable.Range(2, 9997).Select(i => $"a {i}.png").Append("a.png").ToHashSet();
        Assert.Equal("a 9999.png", ScreenshotNaming.UniqueFileName("a.png", n => taken.Contains(n) && n != "a 9999.png"));
        Assert.Equal("a.png", ScreenshotNaming.UniqueFileName("a.png", n => taken.Contains(n) || n == "a 9999.png"));
    }

    // MARK: - Number sequence

    [Fact]
    public void ABlankPatternGivesTheDefaultNameAndKeepsTheSequence()
    {
        var store = new MemoryNumberStore(5);
        var (name, consumed) = ScreenshotNaming.ResolveFileName("Screenshot", "   ", Pattern, "png", store);
        Assert.Equal("Screenshot 2026-07-24 at 15.04.09.png", name);
        Assert.Null(consumed);
        Assert.Equal(5, store.Next);
    }

    [Fact]
    public void ANumberPatternConsumesAndAdvancesTheSequence()
    {
        var store = new MemoryNumberStore(7);
        var (name, consumed) = ScreenshotNaming.ResolveFileName("Screenshot", "edited-%#", Pattern, "png", store);
        Assert.Equal("edited-7.png", name);
        Assert.Equal(7, consumed);
        Assert.Equal(8, store.Next);
    }

    [Fact]
    public void ASecondSaveTakesTheNextNumber()
    {
        var store = new MemoryNumberStore(1);
        ScreenshotNaming.ResolveFileName("S", "n%##", Pattern, "png", store);
        Assert.Equal("n002.png", ScreenshotNaming.ResolveFileName("S", "n%###", Pattern, "png", store).Name);
    }

    [Fact]
    public void ADatePatternWithoutANumberLeavesTheSequenceAlone()
    {
        var store = new MemoryNumberStore(3);
        var (name, consumed) = ScreenshotNaming.ResolveFileName("S", " %y-%mo-%d ", Pattern, "jpg", store);
        Assert.Equal("26-07-24.jpg", name);
        Assert.Null(consumed);
        Assert.Equal(3, store.Next);
    }

    [Fact]
    public void AFailedSaveGivesItsNumberBack()
    {
        var store = new MemoryNumberStore(7);
        var (_, consumed) = ScreenshotNaming.ResolveFileName("S", "n%#", Pattern, "png", store);
        Assert.True(ScreenshotNaming.RewindNumber(store, consumed!.Value));
        Assert.Equal(7, store.Next);
    }

    [Fact]
    public void ARewindNeverUndoesAnotherCapturesNumber()
    {
        var store = new MemoryNumberStore(7);
        var (_, consumed) = ScreenshotNaming.ResolveFileName("S", "n%#", Pattern, "png", store);
        ScreenshotNaming.ResolveFileName("S", "n%#", Pattern, "png", store);
        Assert.False(ScreenshotNaming.RewindNumber(store, consumed!.Value));
        Assert.Equal(9, store.Next);
    }

    [Fact]
    public void ResettingTheSequenceUsesTheChosenStartWithinItsRange()
    {
        var store = new MemoryNumberStore(40);
        ScreenshotNaming.ResetSequence(store, 12);
        Assert.Equal(12, store.Next);
        ScreenshotNaming.ResetSequence(store, -5);
        Assert.Equal(0, store.Next);
        ScreenshotNaming.ResetSequence(store, 5_000_000);
        Assert.Equal(999_999, store.Next);
    }

    // MARK: - Save location

    private static ScreenshotSaveDestination Resolve(string? folder, string? subfolder, string? pattern,
        Func<string, bool> directoryExists, Func<string, bool> createDirectory, Func<string, bool> fileExists,
        IScreenshotNumberStore? store = null) =>
        ScreenshotNaming.ResolveDestination(folder, subfolder, pattern, "Screenshot", Pattern, "png", store ?? new MemoryNumberStore(),
            "C:/Desk", "C:/Home", directoryExists, createDirectory, fileExists);

    [Fact]
    public void AnExistingConfiguredFolderIsUsed()
    {
        var result = Resolve("D:/Shots", "", "", d => d == "D:/Shots", _ => true, _ => false);
        Assert.Equal("D:/Shots", result.Folder);
    }

    [Fact]
    public void AMissingConfiguredFolderFallsBackToTheDesktop()
    {
        var result = Resolve("D:/Gone", "", "", _ => false, _ => true, _ => false);
        Assert.Equal("C:/Desk", result.Folder);
    }

    [Fact]
    public void AnEmptySettingMeansTheDesktop() =>
        Assert.Equal("C:/Desk", Resolve("", "", "", _ => true, _ => true, _ => false).Folder);

    [Fact]
    public void WithoutADesktopTheHomeFolderIsUsed() =>
        Assert.Equal("C:/Home", ScreenshotNaming.ResolveBaseFolder("", _ => true, "", "C:/Home"));

    [Fact]
    public void ATildeReadsAsTheHomeFolder()
    {
        Assert.Equal("C:/Home", ScreenshotNaming.ExpandStoredFolder("~", "C:/Home"));
        Assert.Equal(Path.Combine("C:/Home", "Pictures"), ScreenshotNaming.ExpandStoredFolder("~/Pictures", "C:/Home"));
        Assert.Equal("D:/Elsewhere", ScreenshotNaming.ExpandStoredFolder("  D:/Elsewhere ", "C:/Home"));
    }

    [Fact]
    public void ATildeFolderIsFoundAfterExpanding()
    {
        var expected = Path.Combine("C:/Home", "Pics");
        Assert.Equal(expected, ScreenshotNaming.ResolveBaseFolder("~/Pics", d => d == expected, "C:/Desk", "C:/Home"));
    }

    [Fact]
    public void ADatedSubfolderIsUsedWhenItCanBeMade()
    {
        var made = new List<string>();
        var result = Resolve("D:/Shots", "%year/%mo", "", _ => true, d => { made.Add(d); return true; }, _ => false);
        var expected = Path.Combine("D:/Shots", "2026", "07");
        Assert.Equal(expected, result.Folder);
        Assert.Equal([expected], made);
    }

    [Fact]
    public void AnUncreatableSubfolderFallsBackToTheBaseFolder()
    {
        var result = Resolve("D:/Shots", "%year", "", _ => true, _ => false, _ => false);
        Assert.Equal("D:/Shots", result.Folder);
    }

    [Fact]
    public void ATakenNameInTheChosenFolderGetsANumber()
    {
        var taken = Path.Combine("D:/Shots", "Screenshot 2026-07-24 at 15.04.09.png");
        var result = Resolve("D:/Shots", "", "", _ => true, _ => true, p => p == taken);
        Assert.Equal("Screenshot 2026-07-24 at 15.04.09 2.png", result.FileName);
        Assert.Equal(Path.Combine("D:/Shots", result.FileName), result.FullPath);
    }

    [Fact]
    public void ADestinationCarriesTheConsumedNumber()
    {
        var store = new MemoryNumberStore(4);
        var result = Resolve("D:/Shots", "", "edited-%#", _ => true, _ => true, _ => false, store);
        Assert.Equal("edited-4.png", result.FileName);
        Assert.Equal(4, result.ConsumedNumber);
        Assert.Equal(5, store.Next);
    }

    [Fact]
    public void ASaveWhoseUniqueNameDiffersStillSpendsOneNumber()
    {
        var store = new MemoryNumberStore(4);
        var result = Resolve("D:/Shots", "", "edited-%#", _ => true, _ => true, p => Path.GetFileName(p) == "edited-4.png", store);
        Assert.Equal("edited-4 2.png", result.FileName);
        Assert.Equal(5, store.Next);
    }
}

public class ScreenshotImageRulesTests
{
    [Theory]
    [InlineData("jpeg", CaptureImageFormat.Jpeg)]
    [InlineData("JPG", CaptureImageFormat.Jpeg)]
    [InlineData("png", CaptureImageFormat.Png)]
    [InlineData("heic", CaptureImageFormat.Png)]
    [InlineData("pdf", CaptureImageFormat.Png)]
    [InlineData("", CaptureImageFormat.Png)]
    [InlineData(null, CaptureImageFormat.Png)]
    public void ThePreferredFormatReadsFromItsName(string? raw, CaptureImageFormat expected) =>
        Assert.Equal(expected, ScreenshotImageRules.PreferredFormat(raw));

    [Fact]
    public void AnOpaqueJpegKeepsItsFormat() =>
        Assert.Equal(CaptureImageFormat.Jpeg, ScreenshotImageRules.ChooseFormat(CaptureImageFormat.Jpeg, hasTransparency: false));

    [Fact]
    public void ATransparentJpegBecomesPng() =>
        Assert.Equal(CaptureImageFormat.Png, ScreenshotImageRules.ChooseFormat(CaptureImageFormat.Jpeg, hasTransparency: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PngStaysPng(bool transparent) =>
        Assert.Equal(CaptureImageFormat.Png, ScreenshotImageRules.ChooseFormat(CaptureImageFormat.Png, transparent));

    [Fact]
    public void ExtensionsAndMimeTypesAgreeWithTheFormat()
    {
        Assert.Equal("jpg", ScreenshotImageRules.FileExtension(CaptureImageFormat.Jpeg));
        Assert.Equal("png", ScreenshotImageRules.FileExtension(CaptureImageFormat.Png));
        Assert.Equal("image/jpeg", ScreenshotImageRules.MimeType(CaptureImageFormat.Jpeg));
        Assert.Equal("image/png", ScreenshotImageRules.MimeType(CaptureImageFormat.Png));
    }

    private static byte[] Solid(int width, int height, int stride, byte alpha)
    {
        var bytes = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                bytes[o + 2] = 255; // red, with zero green and blue like the upstream opaque fixture
                bytes[o + 3] = alpha;
            }
        return bytes;
    }

    [Fact]
    public void OpaquePixelsWithZeroColourChannelsAreNotTransparent() =>
        Assert.False(ScreenshotImageRules.HasTransparency(Solid(8, 8, 32, 255), 8, 8, 32));

    [Fact]
    public void EvenOneTransparentInteriorPixelIsTransparency()
    {
        var pixels = Solid(8, 8, 32, 255);
        pixels[4 * 32 + 3 * 4 + 3] = 0;
        Assert.True(ScreenshotImageRules.HasTransparency(pixels, 8, 8, 32));
    }

    [Fact]
    public void AHalfTransparentPixelCounts()
    {
        var pixels = Solid(4, 4, 16, 255);
        pixels[3] = 254;
        Assert.True(ScreenshotImageRules.HasTransparency(pixels, 4, 4, 16));
    }

    [Fact]
    public void StridePaddingIsNotRead()
    {
        var pixels = Solid(4, 4, 24, 255); // 8 bytes of padding per row, left at zero
        Assert.False(ScreenshotImageRules.HasTransparency(pixels, 4, 4, 24));
    }

    [Fact]
    public void AnAlphalessFormatIsOpaqueWhateverTheFourthByteHolds() =>
        Assert.False(ScreenshotImageRules.HasTransparency(Solid(4, 4, 16, 0), 4, 4, 16, hasAlphaChannel: false));

    [Fact]
    public void ABufferThatDoesNotMatchItsSizeCountsAsTransparent() =>
        Assert.True(ScreenshotImageRules.HasTransparency(new byte[10], 8, 8, 32));

    // MARK: - Density, scale

    [Fact]
    public void ARetinaCaptureWritesTwiceTheBaseDpi()
    {
        Assert.Equal(144, ScreenshotImageRules.Dpi(2));
        Assert.Equal(72, ScreenshotImageRules.Dpi(1));
    }

    [Fact]
    public void TheStoredDensityReadsBackAsTheScale()
    {
        Assert.Equal(2, ScreenshotImageRules.CaptureScale(144));
        Assert.Equal(2, ScreenshotImageRules.CaptureScale(ScreenshotImageRules.Dpi(2)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(10.0)]
    [InlineData(1000.0)]
    public void MissingOrBrokenDensityNeverOpensAMisleadingCapture(double? dpi) =>
        Assert.Null(ScreenshotImageRules.CaptureScale(dpi));

    [Theory]
    [InlineData(36.0, 0.5)]
    [InlineData(288.0, 4.0)]
    public void TheScaleRangeIncludesItsEnds(double dpi, double scale) =>
        Assert.Equal(scale, ScreenshotImageRules.CaptureScale(dpi));

    [Fact]
    public void CopiedImagesPreserveAConsistentRetinaScale() =>
        Assert.Equal(2, ScreenshotImageRules.ClipboardImageScale(new SizeD(1200, 800), new SizeD(600, 400)));

    [Fact]
    public void CopiedImagesWithInconsistentMetadataUseOneX() =>
        Assert.Equal(1, ScreenshotImageRules.ClipboardImageScale(new SizeD(1200, 800), new SizeD(600, 800)));

    [Fact]
    public void ASlightlyOffRatioWithinFivePercentStillCounts()
    {
        var scale = ScreenshotImageRules.ClipboardImageScale(new SizeD(1200, 800), new SizeD(600, 402));
        Assert.InRange(scale, 1.99, 2.01);
    }

    [Theory]
    [InlineData(0, 800, 600, 400)]
    [InlineData(1200, 0, 600, 400)]
    [InlineData(1200, 800, 0, 400)]
    [InlineData(1200, 800, 600, -1)]
    public void MissingSizesMeanOneX(double pw, double ph, double sw, double sh) =>
        Assert.Equal(1, ScreenshotImageRules.ClipboardImageScale(new SizeD(pw, ph), new SizeD(sw, sh)));

    [Fact]
    public void AnImplausiblyHighScaleMeansOneX() =>
        Assert.Equal(1, ScreenshotImageRules.ClipboardImageScale(new SizeD(1200, 800), new SizeD(100, 66.6667)));

    [Fact]
    public void TheClipboardImageDecodesAtItsPointSize()
    {
        var size = ScreenshotImageRules.PointSize(new SizeD(64, 64), 2);
        Assert.Equal(new SizeD(32, 32), size);
        Assert.Equal(new SizeD(64, 64), ScreenshotImageRules.PointSize(new SizeD(64, 64), 1));
        Assert.Equal(new SizeD(64, 64), ScreenshotImageRules.PointSize(new SizeD(64, 64), 0));
    }

    [Fact]
    public void TheOneXOptionHalvesARetinaCapture() =>
        Assert.Equal(new SizeD(400, 300), ScreenshotImageRules.DownscaledSize(new SizeD(800, 600), 2));

    [Fact]
    public void AOneXCaptureNeverDownscales() =>
        Assert.Equal(new SizeD(800, 600), ScreenshotImageRules.DownscaledSize(new SizeD(800, 600), 1));

    [Fact]
    public void AnOddRetinaSizeRoundsAwayFromZero() =>
        Assert.Equal(new SizeD(4, 3), ScreenshotImageRules.DownscaledSize(new SizeD(7, 5), 2));

    // MARK: - Thumbnails

    [Theory]
    [InlineData(1000, 500, 360, 180)]
    [InlineData(500, 1000, 180, 360)]
    [InlineData(100, 50, 100, 50)]
    [InlineData(360, 360, 360, 360)]
    [InlineData(4000, 1, 360, 1)]
    public void AThumbnailKeepsItsProportionsAndNeverGrows(int w, int h, int tw, int th) =>
        Assert.Equal((tw, th), ScreenshotImageRules.ThumbnailSize(w, h));

    // MARK: - Import

    [Fact]
    public void AnOversizedImageIsReducedToTheLimit()
    {
        var reduced = ScreenshotImageRules.ReducedSize(new SizeD(11479, 8199));
        Assert.Equal(new SizeD(6000, 4286), reduced);
    }

    [Fact]
    public void ASmallerImageKeepsItsSize() =>
        Assert.Equal(new SizeD(40, 20), ScreenshotImageRules.ReducedSize(new SizeD(40, 20)));

    [Theory]
    [InlineData(5999, false)]
    [InlineData(6000, false)]
    [InlineData(6001, true)]
    public void OnlyAnEdgePastTheLimitAsksForASizeChoice(int edge, bool asks)
    {
        Assert.Equal(asks, ScreenshotImageRules.ImportRequiresSizeChoice(edge, 100));
        Assert.Equal(asks, ScreenshotImageRules.ImportRequiresSizeChoice(100, edge));
    }

    [Fact]
    public void TheReducedLongSideIsTheLimit()
    {
        Assert.Equal(6000, ScreenshotImageRules.ImportReducedMaximumDimension(new SizeD(32, 6001)));
        Assert.Equal(6000, ScreenshotImageRules.ImportReducedMaximumDimension(new SizeD(11479, 8199)));
        Assert.Equal(100, ScreenshotImageRules.ImportReducedMaximumDimension(new SizeD(100, 60)));
    }

    [Fact]
    public void AThinTallImageKeepsItsFullWidthPixel() =>
        Assert.Equal(new SizeD(32, 6000), ScreenshotImageRules.ReducedSize(new SizeD(32, 6001)));

    [Fact]
    public void AnExtremeImageNeverShrinksAnAxisBelowOnePixel() =>
        Assert.Equal(new SizeD(1, 6000), ScreenshotImageRules.ReducedSize(new SizeD(1, 60000)));

    [Theory]
    [InlineData(1, 20, 40, 20, 40)]
    [InlineData(4, 20, 40, 20, 40)]
    [InlineData(5, 20, 40, 40, 20)]
    [InlineData(6, 20, 40, 40, 20)]
    [InlineData(8, 20, 40, 40, 20)]
    public void QuarterTurnOrientationsSwapTheSides(int orientation, double w, double h, double ow, double oh) =>
        Assert.Equal(new SizeD(ow, oh), ScreenshotImageRules.OrientedPixelSize(w, h, orientation));

    [Fact]
    public void AnImportAtItsOwnDensityOpensAtThatScale()
    {
        Assert.Equal(2, ScreenshotImageRules.ImportScale(40, 20, 144, 144));
        Assert.Equal(1, ScreenshotImageRules.ImportScale(40, 20, 72, 72));
    }

    [Fact]
    public void AnImportWithoutDensityEditsAtOneX() =>
        Assert.Equal(1, ScreenshotImageRules.ImportScale(40, 20, null, null));

    [Fact]
    public void AnImportWhoseAxesDisagreeEditsAtOneX() =>
        Assert.Equal(1, ScreenshotImageRules.ImportScale(40, 20, 144, 72));

    [Fact]
    public void AnImportWithAHighPrintDensityEditsAtOneX() =>
        Assert.Equal(1, ScreenshotImageRules.ImportScale(40, 20, 300, 300));

    // MARK: - Copied files

    private static ScreenshotImageRules.CopiedFileCandidate File(string name, long second, long bytes) =>
        new(Path.Combine("C:/cache", name), DateTime.UnixEpoch.AddSeconds(second), bytes);

    [Fact]
    public void ALateStaleCopyIsPrunedWithoutEvictingThePublishedFile()
    {
        var stale = File("A.png", 2, 6);
        var published = File("B.png", 1, 6);
        var victims = ScreenshotImageRules.CopiedFilePruneVictims([stale, published], published.Path, 100, 10);
        Assert.Equal([stale.Path], victims);
    }

    [Fact]
    public void NothingIsPrunedWhileTheBudgetHolds()
    {
        var files = new[] { File("A.png", 3, 1), File("B.png", 2, 1), File("C.png", 1, 1) };
        Assert.Empty(ScreenshotImageRules.CopiedFilePruneVictims(files, files[2].Path));
    }

    [Fact]
    public void TheCountLimitDropsTheOldest()
    {
        var files = Enumerable.Range(0, 5).Select(i => File($"{i}.png", i, 1)).ToArray();
        var victims = ScreenshotImageRules.CopiedFilePruneVictims(files, files[4].Path, 3, 1000);
        Assert.Equal([files[1].Path, files[0].Path], victims);
    }

    [Fact]
    public void TheCurrentFileIsKeptEvenWhenItIsTheOldest()
    {
        var files = new[] { File("new.png", 9, 1), File("old.png", 1, 1) };
        Assert.Equal([files[0].Path], ScreenshotImageRules.CopiedFilePruneVictims(files, files[1].Path, 1, 1000));
    }

    [Fact]
    public void TheByteLimitDropsFilesThatDoNotFit()
    {
        var files = new[] { File("a.png", 3, 6), File("b.png", 2, 6), File("c.png", 1, 3) };
        // current c (3) + a (6) fits in 10, b (6) would not.
        Assert.Equal([files[1].Path], ScreenshotImageRules.CopiedFilePruneVictims(files, files[2].Path, 100, 10));
    }

    [Fact]
    public void EqualDatesBreakTiesByPath()
    {
        var files = new[] { File("b.png", 5, 1), File("a.png", 5, 1), File("c.png", 1, 1) };
        Assert.Equal([files[0].Path], ScreenshotImageRules.CopiedFilePruneVictims(files, files[2].Path, 2, 1000));
    }

    [Fact]
    public void AnUnknownCurrentFileMeansNothingIsPruned()
    {
        var files = new[] { File("a.png", 3, 1), File("b.png", 2, 1) };
        Assert.Empty(ScreenshotImageRules.CopiedFilePruneVictims(files, Path.Combine("C:/cache", "z.png"), 1, 1));
    }

    [Fact]
    public void ANegativeSizeIsDroppedNotCounted()
    {
        var files = new[] { File("bad.png", 3, -5), File("cur.png", 1, 1) };
        Assert.Equal([files[0].Path], ScreenshotImageRules.CopiedFilePruneVictims(files, files[1].Path));
    }

    [Fact]
    public void FilesOlderThanADayGoFirstExceptTheCurrentOne()
    {
        var now = DateTime.UnixEpoch.AddDays(10);
        var files = new ScreenshotImageRules.CopiedFileCandidate[]
        {
            new(Path.Combine("C:/cache", "old.png"), now.AddHours(-25), 1),
            new(Path.Combine("C:/cache", "cur.png"), now.AddHours(-30), 1),
            new(Path.Combine("C:/cache", "fresh.png"), now.AddHours(-1), 1),
        };
        var plan = ScreenshotImageRules.CopiedFilePrunePlan(files, files[1].Path, now);
        Assert.Equal([files[0].Path], plan);
    }

    [Fact]
    public void ThePlanAddsTheBudgetVictimsAfterTheExpiredOnes()
    {
        var now = DateTime.UnixEpoch.AddDays(10);
        var files = Enumerable.Range(0, 105)
            .Select(i => new ScreenshotImageRules.CopiedFileCandidate(Path.Combine("C:/cache", $"{i:000}.png"), now.AddMinutes(-i), 1))
            .Append(new(Path.Combine("C:/cache", "expired.png"), now.AddDays(-3), 1)).ToList();
        var plan = ScreenshotImageRules.CopiedFilePrunePlan(files, files[0].Path, now);
        Assert.Equal(Path.Combine("C:/cache", "expired.png"), plan[0]);
        Assert.Equal(1 + 5, plan.Count);
    }

    [Fact]
    public void ACopyOverTheByteLimitDoesNotFit()
    {
        Assert.True(ScreenshotImageRules.CopiedFileFits(256L * 1024 * 1024));
        Assert.False(ScreenshotImageRules.CopiedFileFits(256L * 1024 * 1024 + 1));
    }
}
