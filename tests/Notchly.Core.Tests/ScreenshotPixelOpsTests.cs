// SPDX-License-Identifier: GPL-3.0-or-later
// Tests of PixelOps against small hand-computed buffers, carrying over the pixel scenes of upstream ScreenshotFeatureTests
// (~2060-2110 block sizes, ~2420-2700 text-only blur, erase on a gradient, translucent and cornered captures, a soft blur easing
// across an edge). Buffers are 8 bit BGRA, premultiplied, top row first.
using Notchly.Core.Capture;
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class ScreenshotPixelOpsTests
{
    private sealed class Img(int width, int height, int stride = 0)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Stride { get; } = stride == 0 ? width * 4 : stride;
        public byte[] Bytes { get; } = new byte[(stride == 0 ? width * 4 : stride) * height];

        public static Img Make(int width, int height, Func<int, int, (int R, int G, int B, int A)> pixel, int stride = 0)
        {
            var img = new Img(width, height, stride);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++) img.Set(x, y, pixel(x, y));
            return img;
        }

        /// <summary>Opaque pixels from an RGB function.</summary>
        public static Img Rgb(int width, int height, Func<int, int, (int R, int G, int B)> pixel) =>
            Make(width, height, (x, y) => { var (r, g, b) = pixel(x, y); return (r, g, b, 255); });

        public void Set(int x, int y, (int R, int G, int B, int A) p)
        {
            var o = y * Stride + x * 4;
            Bytes[o] = (byte)p.B; Bytes[o + 1] = (byte)p.G; Bytes[o + 2] = (byte)p.R; Bytes[o + 3] = (byte)p.A;
        }

        public (int R, int G, int B, int A) Get(int x, int y)
        {
            var o = y * Stride + x * 4;
            return (Bytes[o + 2], Bytes[o + 1], Bytes[o], Bytes[o + 3]);
        }

        public int Red(int x, int y) => Get(x, y).R;

        public byte[] Copy() => (byte[])Bytes.Clone();
    }

    /// <summary>Half-open, like CGRect.contains (RectD.Contains includes its bottom and right edges).</summary>
    private static bool In(RectD r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    private static IEnumerable<(int X, int Y)> Points(RectD box)
    {
        for (var y = (int)box.Y; y < (int)box.Bottom; y++)
            for (var x = (int)box.X; x < (int)box.Right; x++) yield return (x, y);
    }

    // MARK: - Levels and block size

    [Fact]
    public void BlockFactorsFollowTheFiveLevels()
    {
        Assert.Equal([0.4, 0.65, 1, 1.5, 2.2], Enumerable.Range(1, 5).Select(PixelOps.BlockFactor).ToArray());
        Assert.Equal(1, PixelOps.BlockFactor(PixelOps.DefaultLevel));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-9, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    public void BlurLevelsStayInRange(int level, int expected) => Assert.Equal(expected, PixelOps.SanitizedLevel(level));

    [Fact]
    public void AnOutOfRangeLevelUsesTheFactorOfTheNearestEnd()
    {
        Assert.Equal(0.4, PixelOps.BlockFactor(0));
        Assert.Equal(2.2, PixelOps.BlockFactor(99));
    }

    [Fact]
    public void PixelationBlocksScaleWithTheCaptureAndNeverGetTooFine()
    {
        Assert.Equal(65, PixelOps.BlockSize(new SizeD(5500, 3600)));
        Assert.Equal(10, PixelOps.BlockSize(new SizeD(200, 120)));
    }

    [Fact]
    public void EachBlurLevelCoarsensTheMosaicAndTheMiddleKeepsTheOldStrength()
    {
        var capture = new SizeD(1920, 1080);
        var sizes = Enumerable.Range(1, 5).Select(level => PixelOps.BlockSize(capture, level)).ToArray();
        Assert.Equal([8, 12, 19, 29, 42], sizes);
        Assert.Equal(PixelOps.BlockSize(capture), PixelOps.BlockSize(capture, PixelOps.DefaultLevel));
        Assert.Equal(sizes.Order().ToArray(), sizes);
    }

    [Fact]
    public void ALightLevelNeverShrinksTheMosaicToNothing() =>
        Assert.True(PixelOps.BlockSize(new SizeD(40, 40), 1) >= 2);

    [Fact]
    public void ANewCaptureNeverStartsAtALightLevel()
    {
        Assert.All(new[] { 0, 1, 2, 3 }, remembered => Assert.Equal(3, PixelOps.StartingLevel(remembered)));
        Assert.Equal(4, PixelOps.StartingLevel(4));
        Assert.Equal(5, PixelOps.StartingLevel(5));
        Assert.Equal(5, PixelOps.StartingLevel(9));
    }

    // MARK: - Pixelate

    private static Img Ramp() => Img.Rgb(4, 4, (x, y) => (10 * (x + 4 * y), 0, 0));

    [Fact]
    public void ABlockTakesTheAverageOfItsPixels()
    {
        var img = Ramp();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(0, 0, 4, 4), blockSize: 2);
        // 2x2 blocks of 0,10,40,50 / 20,30,60,70 / 80,90,120,130 / 100,110,140,150.
        int[,] expected = { { 25, 45 }, { 105, 125 } };
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++) Assert.Equal(expected[y / 2, x / 2], img.Red(x, y));
    }

    [Fact]
    public void AMosaicLeavesEverythingOutsideTheAreaAlone()
    {
        var img = Ramp();
        var before = img.Copy();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(0, 0, 2, 2), blockSize: 2);
        Assert.Equal(25, img.Red(0, 0));
        Assert.Equal(25, img.Red(1, 1));
        foreach (var (x, y) in Points(new RectD(0, 0, 4, 4)).Where(p => p.X >= 2 || p.Y >= 2))
            Assert.Equal(before[y * 16 + x * 4 + 2], img.Bytes[y * 16 + x * 4 + 2]);
    }

    [Fact]
    public void TheGridBelongsToThePictureSoAnAreaCutsAcrossItsBlocks()
    {
        var img = Ramp();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(1, 1, 2, 2), blockSize: 2);
        Assert.Equal(25, img.Red(1, 1));   // block (0,0)
        Assert.Equal(45, img.Red(2, 1));   // block (1,0)
        Assert.Equal(105, img.Red(1, 2));  // block (0,1)
        Assert.Equal(125, img.Red(2, 2));  // block (1,1)
        Assert.Equal(0, img.Red(0, 0));    // untouched
    }

    [Fact]
    public void ABlockLargerThanThePictureAveragesEverything()
    {
        var img = Ramp();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(0, 0, 4, 4), blockSize: 100);
        // Mean of 0,10,...,150 is 75.
        Assert.All(Points(new RectD(0, 0, 4, 4)), p => Assert.Equal(75, img.Red(p.X, p.Y)));
    }

    [Fact]
    public void APictureNotDivisibleByTheBlockGivesTheLastBlockTheRemainder()
    {
        // 5 wide, block 2: two cells, the first three pixels wide and the second two.
        var img = Img.Rgb(5, 1, (x, _) => (new[] { 0, 30, 60, 90, 120 }[x], 0, 0));
        PixelOps.Pixelate(img.Bytes, 5, 1, img.Stride, new RectD(0, 0, 5, 1), blockSize: 2);
        Assert.Equal([30, 30, 30, 105, 105], Enumerable.Range(0, 5).Select(x => img.Red(x, 0)).ToArray());
    }

    [Fact]
    public void ARowStrideWiderThanThePixelsIsRespected()
    {
        var img = Img.Make(4, 4, (x, y) => (10 * (x + 4 * y), 0, 0, 255), stride: 24);
        PixelOps.Pixelate(img.Bytes, 4, 4, 24, new RectD(0, 0, 4, 4), blockSize: 2);
        Assert.Equal(125, img.Red(3, 3));
        Assert.Equal(25, img.Red(0, 0));
        // The padding after each row is not touched.
        Assert.All(Enumerable.Range(0, 4), y => Assert.Equal(0, img.Bytes[y * 24 + 16]));
    }

    [Fact]
    public void AllFourChannelsAreAveraged()
    {
        var img = Img.Make(2, 2, (x, y) => ((x + y) * 20, 100 + x * 10, 50, 255 - x * 40));
        PixelOps.Pixelate(img.Bytes, 2, 2, img.Stride, new RectD(0, 0, 2, 2), blockSize: 2);
        // R: 0,20,20,40 -> 20; G: 100,110,100,110 -> 105; B 50; A: 255,215,255,215 -> 235 (premultiplied colour is capped at alpha).
        Assert.Equal((20, 105, 50, 235), img.Get(0, 0));
        Assert.Equal((20, 105, 50, 235), img.Get(1, 1));
    }

    [Fact]
    public void ARectangleDraggedBackwardsPixelatesTheSameArea()
    {
        var a = Ramp();
        var b = Ramp();
        PixelOps.Pixelate(a.Bytes, 4, 4, a.Stride, new RectD(1, 1, 2, 2), blockSize: 2);
        PixelOps.Pixelate(b.Bytes, 4, 4, b.Stride, new RectD(3, 3, -2, -2), blockSize: 2);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Theory]
    [InlineData(0, 0, 0, 3)]
    [InlineData(0, 0, 3, 0)]
    [InlineData(-10, -10, 5, 5)]
    [InlineData(10, 10, 5, 5)]
    public void AnEmptyOrOffPictureAreaChangesNothing(double x, double y, double w, double h)
    {
        var img = Ramp();
        var before = img.Copy();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(x, y, w, h), blockSize: 2);
        PixelOps.SoftBlur(img.Bytes, 4, 4, img.Stride, new RectD(x, y, w, h), sigma: 2);
        PixelOps.Erase(img.Bytes, 4, 4, img.Stride, new RectD(x, y, w, h));
        Assert.Equal(before, img.Bytes);
    }

    [Fact]
    public void AFractionalAreaCoversEveryPixelItTouches()
    {
        var img = Ramp();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(0.5, 0.5, 1, 1), blockSize: 2);
        Assert.Equal(25, img.Red(0, 0));
        Assert.Equal(25, img.Red(1, 1));
        Assert.Equal(20, img.Red(2, 0));
    }

    [Fact]
    public void AnAreaPastTheEdgeIsCutToThePicture()
    {
        var img = Ramp();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(2, 2, 50, 50), blockSize: 2);
        Assert.Equal(125, img.Red(3, 3));
        Assert.Equal(60, img.Red(2, 1));
    }

    [Fact]
    public void TheLevelPicksTheBlockOfTheWholePicture()
    {
        // 200x120 gives a base block of 10; level 5 makes it 22 and level 1 makes it 4.
        var coarse = Img.Rgb(200, 120, (x, _) => (x, 0, 0));
        var fine = Img.Rgb(200, 120, (x, _) => (x, 0, 0));
        PixelOps.Pixelate(coarse.Bytes, 200, 120, coarse.Stride, new RectD(0, 0, 200, 120), level: 5);
        PixelOps.Pixelate(fine.Bytes, 200, 120, fine.Stride, new RectD(0, 0, 200, 120), level: 1);
        int Distinct(Img img) => Enumerable.Range(0, 200).Select(x => img.Red(x, 0)).Distinct().Count();
        Assert.Equal(200 / 22, Distinct(coarse));
        Assert.Equal(200 / 4, Distinct(fine));
    }

    [Fact]
    public void ANoisyMosaicShiftsEachBlockByAtMostNineLevels()
    {
        var exact = Img.Rgb(20, 20, (_, _) => (128, 128, 128));
        var noisy = Img.Rgb(20, 20, (_, _) => (128, 128, 128));
        PixelOps.Pixelate(exact.Bytes, 20, 20, exact.Stride, new RectD(0, 0, 20, 20), blockSize: 4);
        PixelOps.Pixelate(noisy.Bytes, 20, 20, noisy.Stride, new RectD(0, 0, 20, 20), blockSize: 4, noise: new Random(7));
        var spread = 0;
        for (var by = 0; by < 5; by++)
            for (var bx = 0; bx < 5; bx++)
            {
                var first = noisy.Get(bx * 4, by * 4);
                Assert.All(Points(new RectD(bx * 4, by * 4, 4, 4)), p => Assert.Equal(first, noisy.Get(p.X, p.Y)));
                var shift = first.R - exact.Red(bx * 4, by * 4);
                Assert.InRange(shift, -9, 9);
                Assert.Equal(first.R, first.G);
                spread = Math.Max(spread, Math.Abs(shift));
            }
        Assert.True(spread > 0);
    }

    [Fact]
    public void NoiseNeverPushesAColourPastItsAlpha()
    {
        var img = Img.Make(8, 8, (_, _) => (0, 0, 0, 5));
        PixelOps.Pixelate(img.Bytes, 8, 8, img.Stride, new RectD(0, 0, 8, 8), blockSize: 2, noise: new Random(1));
        Assert.All(Points(new RectD(0, 0, 8, 8)), p =>
        {
            var (r, g, b, a) = img.Get(p.X, p.Y);
            Assert.Equal(5, a);
            Assert.True(r <= a && g <= a && b <= a);
        });
    }

    [Fact]
    public void ACoarseningLevelNeverChangesAFlatPicture()
    {
        foreach (var level in Enumerable.Range(1, 5))
        {
            var img = Img.Rgb(60, 40, (_, _) => (10, 200, 30));
            PixelOps.Pixelate(img.Bytes, 60, 40, img.Stride, new RectD(5, 5, 40, 20), level);
            Assert.All(Points(new RectD(0, 0, 60, 40)), p => Assert.Equal((10, 200, 30, 255), img.Get(p.X, p.Y)));
        }
    }

    [Fact]
    public void ATextOnlyMosaicHidesTheWordAndLeavesTheRestOfThePictureAlone()
    {
        var word = new RectD(20, 20, 40, 12);
        var square = new RectD(85, 25, 20, 20);
        Img Scene() => Img.Rgb(120, 60, (x, y) =>
            In(word, x, y) ? (x % 2 == 0 ? (0, 0, 0) : (255, 255, 255))
            : In(square, x, y) ? (255, 0, 0) : (255, 255, 255));
        var original = Scene();
        var hidden = Scene();
        var runs = ScreenshotRecognition.TextRuns([new RecognizedWord("secret", word, 0)]);
        PixelOps.Pixelate(hidden.Bytes, 120, 60, hidden.Stride, new RectD(10, 10, 100, 40), textOnly: true, textRuns: runs, blockSize: 10);
        Assert.All(Points(square), p => Assert.Equal(original.Get(p.X, p.Y), hidden.Get(p.X, p.Y)));
        var changed = Points(word).Count(p => original.Get(p.X, p.Y) != hidden.Get(p.X, p.Y));
        Assert.True(changed > (int)(word.Width * word.Height) / 3, $"{changed} pixels of the word changed");
    }

    [Fact]
    public void BeforeRecognitionATextOnlyMosaicCoversAllOfItself()
    {
        var img = Img.Rgb(120, 60, (x, _) => (x % 2 == 0 ? 0 : 255, 0, 0));
        var before = img.Copy();
        PixelOps.Pixelate(img.Bytes, 120, 60, img.Stride, new RectD(10, 10, 100, 40), textOnly: true, textRuns: null, blockSize: 10);
        Assert.NotEqual(before, img.Bytes);
        Assert.NotEqual(before[(30 * 120 + 50) * 4 + 2], img.Bytes[(30 * 120 + 50) * 4 + 2]);
    }

    [Fact]
    public void ATextOnlyAreaWithNoRunsInsideItChangesNothing()
    {
        var img = Ramp();
        var before = img.Copy();
        PixelOps.Pixelate(img.Bytes, 4, 4, img.Stride, new RectD(0, 0, 4, 4), textOnly: true, textRuns: [new RectD(100, 100, 5, 5)], blockSize: 2);
        Assert.Equal(before, img.Bytes);
    }

    [Fact]
    public void ABufferThatDoesNotMatchItsSizeIsRefused()
    {
        Assert.Throws<ArgumentException>(() => PixelOps.Pixelate(new byte[10], 4, 4, 16, new RectD(0, 0, 4, 4)));
        Assert.Throws<ArgumentException>(() => PixelOps.SoftBlur(new byte[64], 4, 4, 8, new RectD(0, 0, 4, 4)));
        Assert.Throws<ArgumentException>(() => PixelOps.Erase(new byte[64], 0, 4, 16, new RectD(0, 0, 4, 4)));
        Assert.Throws<ArgumentNullException>(() => PixelOps.Erase(null!, 4, 4, 16, new RectD(0, 0, 4, 4)));
    }

    // MARK: - Soft blur

    [Fact]
    public void ASoftBlurLeavesAFlatPictureExactlyAsItWas()
    {
        var img = Img.Rgb(40, 30, (_, _) => (77, 150, 220));
        PixelOps.SoftBlur(img.Bytes, 40, 30, img.Stride, new RectD(5, 5, 20, 15), sigma: 4);
        Assert.All(Points(new RectD(0, 0, 40, 30)), p => Assert.Equal((77, 150, 220, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void ASoftBlurEasesAcrossAnEdgeInsteadOfSteppingLikeAMosaic()
    {
        // Black beside white, as upstream's scene; the default level on a 120x60 picture blurs with sigma 8.
        Img Halves() => Img.Rgb(120, 60, (x, _) => x < 60 ? (0, 0, 0) : (255, 255, 255));
        var blurred = Halves();
        var mosaic = Halves();
        var area = new RectD(10, 10, 100, 40);
        PixelOps.SoftBlur(blurred.Bytes, 120, 60, blurred.Stride, area);
        PixelOps.Pixelate(mosaic.Bytes, 120, 60, mosaic.Stride, area);
        int Largest(Img img) => Enumerable.Range(11, 98).Select(x => Math.Abs(img.Red(x + 1, 30) - img.Red(x, 30))).Max();
        Assert.True(Largest(blurred) < 40, $"largest step {Largest(blurred)}");
        Assert.True(blurred.Red(59, 30) > 20);
        Assert.True(Largest(mosaic) > 100);
        Assert.InRange(blurred.Red(60, 30), 100, 155);
    }

    [Fact]
    public void ASoftBlurIsMonotoneAcrossAStep()
    {
        var img = Img.Rgb(100, 3, (x, _) => x < 50 ? (0, 0, 0) : (240, 240, 240));
        PixelOps.SoftBlur(img.Bytes, 100, 3, img.Stride, new RectD(0, 0, 100, 3), sigma: 5);
        for (var x = 1; x < 100; x++) Assert.True(img.Red(x, 1) >= img.Red(x - 1, 1), $"x={x}");
        Assert.InRange(img.Red(49, 1) + img.Red(50, 1), 235, 245);
    }

    [Fact]
    public void ASoftBlurReadsPastTheAreaButOnlyChangesInsideIt()
    {
        var img = Img.Rgb(60, 3, (x, _) => x < 30 ? (0, 0, 0) : (255, 255, 255));
        var before = img.Copy();
        PixelOps.SoftBlur(img.Bytes, 60, 3, img.Stride, new RectD(20, 0, 10, 3), sigma: 4);
        // Pixels 20..29 were black but the white beyond the area bleeds in.
        Assert.True(img.Red(29, 1) > 0);
        Assert.True(img.Red(29, 1) > img.Red(20, 1));
        Assert.All(Enumerable.Range(0, 20), x => Assert.Equal(0, img.Red(x, 1)));
        Assert.All(Enumerable.Range(30, 30), x => Assert.Equal(255, img.Red(x, 1)));
        Assert.Equal(before[(1 * 60 + 40) * 4 + 2], img.Bytes[(1 * 60 + 40) * 4 + 2]);
    }

    [Fact]
    public void ASoftBlurSpreadsAnImpulseSymmetricallyAndKeepsItsWeight()
    {
        var img = Img.Rgb(81, 1, (x, _) => x == 40 ? (255, 255, 255) : (0, 0, 0));
        PixelOps.SoftBlur(img.Bytes, 81, 1, img.Stride, new RectD(0, 0, 81, 1), sigma: 3);
        for (var k = 1; k <= 20; k++) Assert.Equal(img.Red(40 - k, 0), img.Red(40 + k, 0));
        for (var k = 1; k <= 20; k++) Assert.True(img.Red(40 + k - 1, 0) >= img.Red(40 + k, 0));
        Assert.InRange(img.Red(40, 0), 20, 120);
        Assert.InRange(Enumerable.Range(0, 81).Sum(x => img.Red(x, 0)), 200, 310);
    }

    [Fact]
    public void ABiggerSigmaSpreadsFurther()
    {
        int Reach(double sigma)
        {
            var img = Img.Rgb(121, 1, (x, _) => x == 60 ? (255, 255, 255) : (0, 0, 0));
            PixelOps.SoftBlur(img.Bytes, 121, 1, img.Stride, new RectD(0, 0, 121, 1), sigma: sigma);
            return Enumerable.Range(0, 121).Count(x => img.Red(x, 0) > 0);
        }
        Assert.True(Reach(2) < Reach(5));
        Assert.True(Reach(5) < Reach(12));
    }

    [Fact]
    public void TheBlurLevelChangesTheDefaultSigma()
    {
        // The width of the soft edge a step blurs into grows with the level.
        int Edge(int level)
        {
            var img = Img.Rgb(400, 20, (x, _) => x < 200 ? (0, 0, 0) : (255, 255, 255));
            PixelOps.SoftBlur(img.Bytes, 400, 20, img.Stride, new RectD(0, 0, 400, 20), level);
            return Enumerable.Range(0, 400).Count(x => img.Red(x, 10) is > 5 and < 250);
        }
        Assert.True(Edge(1) < Edge(3));
        Assert.True(Edge(3) < Edge(5));
    }

    [Fact]
    public void ABlurNeverLeavesAColourAboveItsAlpha()
    {
        var img = Img.Make(30, 30, (x, y) => ((x * 8) % 100, (y * 8) % 100, 40, 100 + (x + y) % 60));
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 30; x++)
            {
                var (r, g, b, a) = img.Get(x, y);
                img.Set(x, y, (Math.Min(r, a), Math.Min(g, a), Math.Min(b, a), a));
            }
        PixelOps.SoftBlur(img.Bytes, 30, 30, img.Stride, new RectD(0, 0, 30, 30), sigma: 3);
        Assert.All(Points(new RectD(0, 0, 30, 30)), p =>
        {
            var (r, g, b, a) = img.Get(p.X, p.Y);
            Assert.True(r <= a && g <= a && b <= a);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonPositiveOrBrokenSigmaChangesNothing(double sigma)
    {
        var img = Ramp();
        var before = img.Copy();
        PixelOps.SoftBlur(img.Bytes, 4, 4, img.Stride, new RectD(0, 0, 4, 4), sigma: sigma);
        Assert.Equal(before, img.Bytes);
    }

    [Fact]
    public void ATextOnlySoftBlurTouchesOnlyTheRuns()
    {
        var img = Img.Rgb(60, 20, (x, _) => (x % 2 == 0 ? 0 : 255, 0, 0));
        var before = img.Copy();
        PixelOps.SoftBlur(img.Bytes, 60, 20, img.Stride, new RectD(0, 0, 60, 20), textOnly: true, textRuns: [new RectD(20, 5, 10, 10)], sigma: 3);
        Assert.All(Points(new RectD(0, 0, 60, 20)).Where(p => p.X < 20 || p.X >= 30 || p.Y < 5 || p.Y >= 15),
            p => Assert.Equal(before[p.Y * 240 + p.X * 4 + 2], img.Bytes[p.Y * 240 + p.X * 4 + 2]));
        Assert.True(Enumerable.Range(21, 8).All(x => img.Red(x, 10) is > 0 and < 255));
    }

    [Fact]
    public void BoxWidthsAreOddAndTheirVarianceMatchesTheDeviation()
    {
        foreach (var sigma in new[] { 5.0, 8, 20, 31.2 })
        {
            var sizes = PixelOps.BoxSizes(sigma);
            Assert.Equal(3, sizes.Length);
            Assert.All(sizes, size => Assert.True(size % 2 == 1 && size >= 1));
            var variance = sizes.Sum(size => (size * size - 1) / 12.0);
            Assert.InRange(variance / (sigma * sigma), 0.9, 1.1);
        }
    }

    // MARK: - Erase

    [Fact]
    public void ErasingOnAFlatBackgroundPaintsExactlyThatBackground()
    {
        var img = Img.Rgb(60, 40, (x, y) => x is >= 20 and < 40 && y is >= 10 and < 30 ? (0, 0, 0) : (240, 230, 220));
        PixelOps.Erase(img.Bytes, 60, 40, img.Stride, new RectD(15, 5, 30, 30));
        Assert.All(Points(new RectD(0, 0, 60, 40)), p => Assert.Equal((240, 230, 220, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void ErasingAnAreaContinuesTheGradientAroundIt()
    {
        var block = new RectD(50, 26, 28, 12);
        var img = Img.Rgb(128, 64, (x, y) => In(block, x, y) ? (0, 0, 0) : (x * 2, y * 2, 100));
        PixelOps.Erase(img.Bytes, 128, 64, img.Stride, new RectD(44, 20, 40, 24));
        foreach (var (x, y) in new[] { (52, 28), (64, 32), (76, 36) })
        {
            var (r, g, b, _) = img.Get(x, y);
            Assert.InRange(Math.Abs(r - x * 2), 0, 6);
            Assert.InRange(Math.Abs(g - y * 2), 0, 6);
            Assert.InRange(Math.Abs(b - 100), 0, 2);
        }
    }

    [Fact]
    public void ErasingChangesNothingOutsideTheArea()
    {
        var img = Img.Rgb(128, 64, (x, y) => (x * 2, y * 2, 100));
        var before = img.Copy();
        var area = new RectD(44, 20, 40, 24);
        PixelOps.Erase(img.Bytes, 128, 64, img.Stride, area);
        foreach (var (x, y) in Points(new RectD(0, 0, 128, 64)).Where(p => !In(area, p.X, p.Y)))
            Assert.Equal(before.AsSpan(y * 512 + x * 4, 4).ToArray(), img.Bytes.AsSpan(y * 512 + x * 4, 4).ToArray());
    }

    [Fact]
    public void ErasingAVerticalGradientFollowsItEvenOnAnAreaWithAGlyphOnTheEdge()
    {
        var img = Img.Rgb(100, 100, (_, y) => (y * 2, y, 50));
        // A stray dark glyph pixel touching the area's left edge is outvoted by the median.
        img.Set(39, 50, (0, 0, 0, 255));
        PixelOps.Erase(img.Bytes, 100, 100, img.Stride, new RectD(40, 40, 20, 20));
        foreach (var y in new[] { 41, 50, 58 })
            Assert.InRange(Math.Abs(img.Red(50, y) - y * 2), 0, 6);
    }

    private static readonly RectD WordBox = new(20, 20, 40, 12);
    private static readonly RectD SquareBox = new(85, 25, 20, 20);

    private static Img TextScene() => Img.Rgb(120, 60, (x, y) =>
        In(WordBox, x, y) ? (x % 2 == 0 ? (0, 0, 0) : (255, 255, 255))
        : In(SquareBox, x, y) ? (255, 0, 0) : (255, 255, 255));

    [Fact]
    public void ErasingTextPaintsTheWordOverWithTheFlatBackgroundAndKeepsTheRest()
    {
        var img = TextScene();
        var runs = ScreenshotRecognition.TextRuns([new RecognizedWord("secret", WordBox, 0)]);
        PixelOps.Erase(img.Bytes, 120, 60, img.Stride, new RectD(10, 10, 100, 40), textOnly: true, textRuns: runs);
        Assert.All(Points(WordBox), p => Assert.Equal((255, 255, 255, 255), img.Get(p.X, p.Y)));
        Assert.All(Points(SquareBox), p => Assert.Equal((255, 0, 0, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void BeforeRecognitionATextOnlyEraseCoversAllOfItself()
    {
        var img = TextScene();
        PixelOps.Erase(img.Bytes, 120, 60, img.Stride, new RectD(10, 10, 100, 40), textOnly: true, textRuns: null);
        Assert.Contains(Points(SquareBox), p => img.Get(p.X, p.Y) != (255, 0, 0, 255));
        Assert.All(Points(WordBox), p => Assert.Equal((255, 255, 255, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void AWholeAreaEraseCoversTheSquareToo()
    {
        var img = TextScene();
        PixelOps.Erase(img.Bytes, 120, 60, img.Stride, new RectD(10, 10, 100, 40));
        Assert.All(Points(new RectD(10, 10, 100, 40)), p => Assert.Equal((255, 255, 255, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void ErasingALineNeverSmearsInWhatSitsInsideTheTextNextToIt()
    {
        // Two close lines, the first led by a pink bullet that recognition counts as text. The second line's fill must not pick the pink up.
        var firstLine = new RectD(8, 8, 100, 28);
        var secondLine = new RectD(8, 34, 100, 28);
        var img = Img.Rgb(120, 80, (x, y) =>
            x is >= 10 and < 30 && y is >= 10 and < 34 ? (255, 105, 180)
            : x is >= 36 and < 100 && y is >= 40 and < 56 && x % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        PixelOps.Erase(img.Bytes, 120, 80, img.Stride, new RectD(0, 0, 120, 80), textOnly: true, textRuns: [firstLine, secondLine]);
        Assert.All(Points(new RectD(10, 10, 90, 48)), p =>
        {
            var (r, g, b, _) = img.Get(p.X, p.Y);
            Assert.True(r >= 250 && g >= 250 && b >= 250, $"({p.X},{p.Y}) = {r},{g},{b}");
        });
    }

    // Glyph columns (even x inside the box) against the mean of the background columns beside them, in red: a smooth fill, even a
    // steep gradient, scores near zero; glyphs showing through do not.
    private static int GlyphContrast(Img img, RectD box)
    {
        var largest = 0;
        for (var y = (int)box.Y; y < (int)box.Bottom; y++)
            for (var x = (int)box.X; x < (int)box.Right - 1; x += 2)
            {
                var sides = img.Red(x - 1, y) + img.Red(x + 1, y);
                largest = Math.Max(largest, Math.Abs(2 * img.Red(x, y) - sides) / 2);
            }
        return largest;
    }

    [Fact]
    public void ErasingTextOnATranslucentCaptureLeavesNoGlyphShowingThrough()
    {
        // Opaque white glyph columns on a dark background at 80 percent alpha, premultiplied, like a translucent terminal.
        var glyphs = new RectD(30, 24, 60, 12);
        var img = Img.Make(120, 60, (x, y) => In(glyphs, x, y) && x % 2 == 0 ? (255, 255, 255, 255) : (16, 16, 16, 204));
        PixelOps.Erase(img.Bytes, 120, 60, img.Stride, new RectD(20, 16, 80, 28));
        Assert.InRange(GlyphContrast(img, glyphs), 0, 4);
        // The fill replaces what was there: the alpha is the surroundings', not a blend with the glyphs.
        Assert.All(Points(glyphs), p => Assert.Equal((16, 16, 16, 204), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void BlurringTextOnATranslucentCaptureLeavesNoGlyphShowingThrough()
    {
        var glyphs = new RectD(30, 24, 60, 12);
        var img = Img.Make(120, 60, (x, y) => In(glyphs, x, y) && x % 2 == 0 ? (255, 255, 255, 255) : (16, 16, 16, 204));
        PixelOps.SoftBlur(img.Bytes, 120, 60, img.Stride, new RectD(20, 16, 80, 28));
        Assert.InRange(GlyphContrast(img, glyphs), 0, 6);
    }

    [Fact]
    public void ErasingBesideATransparentMarginLeavesNoGlyphShowingThrough()
    {
        // Opaque white content with black glyph columns, and a fully transparent margin on the left, like a window's rounded corner.
        var glyphs = new RectD(24, 20, 16, 12);
        var img = Img.Make(120, 60, (x, y) =>
            x < 20 ? (0, 0, 0, 0) : In(glyphs, x, y) && x % 2 == 0 ? (0, 0, 0, 255) : (255, 255, 255, 255));
        PixelOps.Erase(img.Bytes, 120, 60, img.Stride, new RectD(20, 14, 48, 24));
        // The fill itself bends near the transparent side, so allow some curvature; glyphs showing through stand out by about 130.
        Assert.InRange(GlyphContrast(img, glyphs), 0, 16);
    }

    [Fact]
    public void AnEraseFillIsAlwaysAValidPremultipliedPixel()
    {
        var img = Img.Make(60, 40, (x, y) => (Math.Min(x * 3, 120), 20, Math.Min(y * 4, 120), 120 + x % 20));
        PixelOps.Erase(img.Bytes, 60, 40, img.Stride, new RectD(10, 10, 30, 20));
        Assert.All(Points(new RectD(10, 10, 30, 20)), p =>
        {
            var (r, g, b, a) = img.Get(p.X, p.Y);
            Assert.True(r <= a && g <= a && b <= a);
        });
    }

    [Fact]
    public void AnAreaAsLargeAsThePictureReadsItsOwnBorder()
    {
        var img = Img.Rgb(30, 30, (x, y) => x is >= 10 and < 20 && y is >= 10 and < 20 ? (0, 0, 0) : (200, 200, 200));
        PixelOps.Erase(img.Bytes, 30, 30, img.Stride, new RectD(0, 0, 30, 30));
        Assert.All(Points(new RectD(0, 0, 30, 30)), p => Assert.Equal((200, 200, 200, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void AnEraseStripAgainstTheEdgeLeavesOutTheSideThatIsNotThere()
    {
        // The area touches the left edge, so only the other three sides are read.
        var img = Img.Rgb(40, 40, (x, y) => x < 10 && y is >= 15 and < 25 ? (0, 0, 0) : (90, 90, 90));
        PixelOps.Erase(img.Bytes, 40, 40, img.Stride, new RectD(0, 12, 12, 16));
        Assert.All(Points(new RectD(0, 12, 12, 16)), p => Assert.Equal((90, 90, 90, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void AnEraseWithNothingToReadChangesNothing()
    {
        // Every pixel around is inside skipped text and the fallbacks read the outside again without skipping, so it still fills.
        var img = Img.Rgb(30, 30, (_, _) => (50, 60, 70));
        PixelOps.Erase(img.Bytes, 30, 30, img.Stride, new RectD(10, 10, 10, 10), textOnly: true, textRuns: [new RectD(0, 0, 30, 30)]);
        Assert.All(Points(new RectD(0, 0, 30, 30)), p => Assert.Equal((50, 60, 70, 255), img.Get(p.X, p.Y)));
    }

    [Fact]
    public void ErasePatchCoversTheWholePixelsTheRegionTouches()
    {
        var img = Img.Rgb(40, 40, (_, _) => (9, 9, 9));
        var patch = PixelOps.BuildErasePatch(img.Bytes, 40, 40, img.Stride, new RectD(2.3, 3.7, 4, 4))!;
        Assert.Equal((2, 3, 5, 5), (patch.X, patch.Y, patch.Width, patch.Height));
        Assert.Equal(5 * 5 * 4, patch.Bgra.Length);
        Assert.Equal(9, patch[3, 4, 2]);
        Assert.Equal(255, patch[3, 4, 3]);
    }

    [Fact]
    public void ErasePatchOfAnAreaOffThePictureIsNull()
    {
        var img = Img.Rgb(10, 10, (_, _) => (9, 9, 9));
        Assert.Null(PixelOps.BuildErasePatch(img.Bytes, 10, 10, img.Stride, new RectD(50, 50, 4, 4)));
        Assert.Null(PixelOps.BuildErasePatch(img.Bytes, 10, 10, img.Stride, new RectD(2, 2, 0, 4)));
    }

    [Fact]
    public void ASkippedRunKeepsItsGlyphsOutOfTheFill()
    {
        // A dark text run sits right above the area; skipped, the fill reads the white below, left and right instead of the black above.
        var run = new RectD(10, 6, 20, 8);
        var img = Img.Rgb(40, 40, (x, y) => In(run, x, y) ? (0, 0, 0) : (255, 255, 255));
        var skipped = PixelOps.BuildErasePatch(img.Bytes, 40, 40, img.Stride, new RectD(10, 14, 20, 8), [run])!;
        var unskipped = PixelOps.BuildErasePatch(img.Bytes, 40, 40, img.Stride, new RectD(10, 14, 20, 8))!;
        Assert.Equal(255, skipped[20, 14, 2]);
        Assert.True(unskipped[20, 14, 2] < 255);
    }

    [Fact]
    public void AMovedPictureOffsetKeepsTheErasedAreaWhereItWas()
    {
        var img = Img.Rgb(40, 20, (x, _) => (x * 6, 0, 0));
        PixelOps.Erase(img.Bytes, 40, 20, img.Stride, new RectD(10, 5, 10, 10));
        // A horizontal ramp: the area is filled with that ramp's own values, not flattened to one colour.
        Assert.True(img.Red(18, 10) > img.Red(11, 10));
        Assert.InRange(Math.Abs(img.Red(15, 10) - 90), 0, 12);
    }
}
