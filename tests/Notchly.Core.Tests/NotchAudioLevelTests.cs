// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Tests/NotchAudioLevelTests.swift (maths, ring, silence and tap-change
// contracts), plus the FFT and the Windows player-process rules.
using Notchly.Core.Notch;
using Notchly.Core.Notch.Lyrics;
using S = Notchly.Core.Notch.NotchAudioLevelSupport;

namespace Notchly.Core.Tests;

public class NotchAudioLevelTests
{
    [Fact]
    public void SevenOrderedBandsCoverTheSpectrumWithoutDc()
    {
        var bands = S.BandRanges(48_000, 1024);
        Assert.Equal(7, bands.Count);
        Assert.All(bands, b => Assert.True(b.End.Value > b.Start.Value && b.Start.Value >= 1 && b.End.Value <= 512));
        for (var i = 1; i < bands.Count; i++) Assert.True(bands[i - 1].End.Value <= bands[i].Start.Value);
        var narrow = S.BandRanges(8_000, 64);
        Assert.Equal(7, narrow.Count);
        Assert.All(narrow, b => Assert.True(b.End.Value > b.Start.Value && b.End.Value <= 32));
    }

    [Fact]
    public void SilenceIsFlatAndAToneLightsOnlyItsBand()
    {
        var bands = S.BandRanges(48_000, 1024);
        var magnitudes = new float[512];
        Assert.All(S.BandLevels(magnitudes, bands), l => Assert.Equal(0, l));
        magnitudes[(int)(700 / (48_000 / 1024.0))] = 0.5f;
        var levels = S.BandLevels(magnitudes, bands);
        Assert.True(levels[3] > 0);
        for (var i = 0; i < 7; i++) if (i != 3) Assert.Equal(0, levels[i]);
    }

    [Fact]
    public void BarsRiseQuicklyFallSlowlyAndQuietSourcesFillTheMeter()
    {
        var smoother = new S.Smoother();
        var first = smoother.Next([0, 0, 0, 1, 0, 0, 0]);
        var second = smoother.Next([0, 0, 0, 1, 0, 0, 0]);
        Assert.True(first[3] > 0.4 && second[3] > first[3] && second[3] <= 1);
        var released = smoother.Next([0, 0, 0, 0, 0, 0, 0]);
        Assert.True(released[3] < second[3] && released[3] > second[3] * 0.7);
        var quiet = new S.Smoother();
        double[] scaled = [];
        for (var i = 0; i < 12; i++) scaled = quiet.Next([0.1, 0.2, 0.3, 0.3, 0.2, 0.1, 0.05]);
        Assert.True(scaled.Max() > 0.8);
    }

    [Fact]
    public void FewerBarsSampleBandsEvenlyAndSilentCapturesFallBack()
    {
        Assert.Equal(1, S.BarIndex(0, 3, 7));
        Assert.Equal(3, S.BarIndex(1, 3, 7));
        Assert.Equal(5, S.BarIndex(2, 3, 7));
        Assert.Equal(6, S.BarIndex(6, 7, 7));
        Assert.False(S.FallsBack(heard: false, elapsed: 1));
        Assert.False(S.FallsBack(heard: true, elapsed: 10));
        Assert.True(S.FallsBack(heard: false, elapsed: S.SilenceGrace));
        Assert.True(S.SilenceGrace >= 5);
    }

    [Fact]
    public void GivingUpOnASilentCaptureCostsOnePlay()
    {
        var first = new NotchMusicIdentity("First", "Artist", "Album", 180, "Spotify.exe");
        var second = new NotchMusicIdentity("Second", "Artist", "Album", 180, "Spotify.exe");
        var other = new NotchMusicIdentity("First", "Artist", "Album", 180, "Chrome");
        var memory = new S.SilenceMemory();
        Assert.True(memory.Reads(first));
        memory.GiveUp(first);
        Assert.False(memory.Reads(first));
        Assert.True(memory.Reads(second) && memory.Reads(other));
        memory.Rearm();
        Assert.True(memory.Reads(first));
    }

    [Fact]
    public void ProcessChangesRebuildOrRestartTheCapture()
    {
        var built = new HashSet<int> { 10, 11 };
        Assert.Equal(S.TapChange.None, S.TapChangeFor(built, new HashSet<int> { 10, 11 }));
        Assert.Equal(S.TapChange.Rebuild, S.TapChangeFor(built, new HashSet<int> { 10, 11, 12 }));
        Assert.Equal(S.TapChange.Restart, S.TapChangeFor(built, new HashSet<int> { 10 }));
        Assert.Equal(S.TapChange.Restart, S.TapChangeFor(built, new HashSet<int> { 10, 12 }));
        Assert.Equal(S.TapChange.Restart, S.TapChangeFor(built, new HashSet<int>()));
        Assert.InRange(S.ProcessSettle, 0.01, 0.99);
    }

    [Fact]
    public void RingProvesSoundOnceAndKeepsTheLatestSamples()
    {
        var ring = new AudioRing(2);
        Assert.False(ring.HasHeard(1) || ring.HasHeard(4));
        ring.Write([0.0005f, 0.0005f], channels: 2);
        Assert.False(ring.HasHeard(1));
        Assert.True(ring.HasHeard(4));
        Assert.True(ring.HasHeard(1));
        ring.Write([0f, 0f], channels: 2);
        Assert.True(ring.HasHeard(1));
        Assert.False(new AudioRing(2).HasHeard(4));

        var latest = new AudioRing(4);
        latest.Write([1f, 3f, 2f, 4f, 5f, 7f], channels: 2);
        var output = new float[3];
        Assert.True(latest.Latest(output));
        Assert.Equal(new[] { 2f, 3f, 6f }, output);
        Assert.False(latest.Latest(new float[5]));
    }

    [Fact]
    public void FftMatchesANaiveTransform()
    {
        var random = new Random(7);
        const int n = 64;
        var real = Enumerable.Range(0, n).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        var imaginary = new float[n];
        var expectedReal = new double[n];
        var expectedImaginary = new double[n];
        for (var k = 0; k < n; k++)
            for (var t = 0; t < n; t++)
            {
                var angle = -2 * Math.PI * k * t / n;
                expectedReal[k] += real[t] * Math.Cos(angle);
                expectedImaginary[k] += real[t] * Math.Sin(angle);
            }
        Fft.Forward(real, imaginary);
        for (var k = 0; k < n; k++)
        {
            Assert.Equal(expectedReal[k], real[k], 3);
            Assert.Equal(expectedImaginary[k], imaginary[k], 3);
        }
    }

    [Fact]
    public void AnalyzerPutsAToneInItsBand()
    {
        var analyzer = new SpectrumAnalyzer(48_000);
        var samples = Enumerable.Range(0, SpectrumAnalyzer.Size).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 700 * i / 48_000.0))).ToArray();
        var levels = S.BandLevels(analyzer.Magnitudes(samples), analyzer.Bands);
        Assert.Equal(3, Array.IndexOf(levels, levels.Max()));
        Assert.All(S.BandLevels(analyzer.Magnitudes(new float[SpectrumAnalyzer.Size]), analyzer.Bands), l => Assert.Equal(0, l));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify.exe", null, true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify.exe", null, false)]
    [InlineData("Chrome", "chrome.exe", null, true)]
    [InlineData("Chrome._crx_abcdef", "chrome.exe", null, true)]
    [InlineData("MSEdge", "msedge.exe", null, true)]
    [InlineData("MSEdge", "chrome.exe", null, false)]
    [InlineData("308046B0AF4A39CB", "firefox.exe", null, false)]
    [InlineData("", "chrome.exe", null, false)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc.exe", null, true)]
    public void MediaSessionsMatchTheirProcess(string id, string exe, string? family, bool expected) =>
        Assert.Equal(expected, PlayerProcessMatch.Matches(id, exe, family));
}
