// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchAudioLevelSupport.swift and the
// NotchAudioRing / NotchAudioAnalyzer classes of NotchAudioLevelService.swift: the arithmetic
// behind the island's live music bars, free of any audio API so the tests can exercise it.
// The FFT replaces Accelerate's vDSP DFT; PlayerProcessMatch is new for Windows.
using Notchly.Core.Notch.Lyrics;

namespace Notchly.Core.Notch;

public static class NotchAudioLevelSupport
{
    public const int BandCount = 7;
    /// <summary>Band edges in hertz, roughly a third of an octave apart from the bass to the air.</summary>
    public static readonly double[] BandEdgesHz = [50, 120, 250, 500, 1000, 2000, 4500, 12_000];
    /// <summary>Levels below this many decibels under full scale read as silence.</summary>
    public const double FloorDecibels = 60.0;
    /// <summary>Enough for motion, few enough to stay cheap.</summary>
    public const double UpdatesPerSecond = 30.0;
    /// <summary>How long a playing capture may stay silent before the bars go back to synthetic motion.</summary>
    public const double SilenceGrace = 8.0;
    /// <summary>How long the player's processes settle before they are read again.</summary>
    public const double ProcessSettle = 0.25;
    /// <summary>A pause keeps the bars this long, so a skipped track does not blink the meter.</summary>
    public const double PauseGrace = 1.5;

    public static bool FallsBack(bool heard, double elapsed) => !heard && elapsed >= SilenceGrace;

    /// <summary>What a change in the player's audio processes asks of its capture.</summary>
    public enum TapChange
    {
        /// <summary>Nothing the capture was built from moved.</summary>
        None,
        /// <summary>Only new processes: build again around all of them, keeping what was heard.</summary>
        Rebuild,
        /// <summary>Something left, taking the capture's ears with it: read the player from scratch.</summary>
        Restart,
    }

    public static TapChange TapChangeFor<T>(IReadOnlySet<T> tapped, IReadOnlySet<T> current)
    {
        if (current.SetEquals(tapped)) return TapChange.None;
        if (!tapped.IsSubsetOf(current)) return TapChange.Restart;
        return TapChange.Rebuild;
    }

    /// <summary>
    /// Remembers the single play a silent capture was given up on: only that play keeps the
    /// synthetic motion; the next track, or pressing play again, reads again.
    /// </summary>
    public struct SilenceMemory
    {
        private NotchMusicIdentity? _givenUp;
        public readonly bool Reads(NotchMusicIdentity identity) => identity != _givenUp;
        public void GiveUp(NotchMusicIdentity identity) => _givenUp = identity;
        /// <summary>A pause, a stop or switching the feature off arms the next play.</summary>
        public void Rearm() => _givenUp = null;
    }

    /// <summary>The bins of a real spectrum belonging to each band; bin 0 (DC) belongs to none.</summary>
    public static IReadOnlyList<Range> BandRanges(double sampleRate, int size)
    {
        var usable = Math.Max(2, size / 2);
        var binWidth = sampleRate > 0 && size > 0 ? sampleRate / size : 1;
        var ranges = new List<Range>(BandCount);
        for (var band = 0; band < BandCount; band++)
        {
            var lower = (int)Math.Floor(BandEdgesHz[band] / binWidth);
            var upper = (int)Math.Floor(BandEdgesHz[band + 1] / binWidth);
            lower = Math.Min(Math.Max(lower, 1), usable - 1);
            upper = Math.Min(Math.Max(upper, lower + 1), usable);
            ranges.Add(lower..upper);
        }
        return ranges;
    }

    /// <summary>Each band's mean magnitude on a decibel scale with a floor, from 0 to 1.</summary>
    public static double[] BandLevels(IReadOnlyList<float> magnitudes, IReadOnlyList<Range> bands)
    {
        var levels = new double[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            var start = Math.Clamp(bands[i].Start.Value, 0, magnitudes.Count);
            var end = Math.Clamp(bands[i].End.Value, 0, magnitudes.Count);
            if (end <= start) continue;
            double sum = 0;
            for (var bin = start; bin < end; bin++) sum += magnitudes[bin];
            var mean = sum / (end - start);
            if (mean <= 0) continue;
            var decibels = 20 * Math.Log10(mean);
            levels[i] = Math.Min(1, Math.Max(0, (decibels + FloorDecibels) / FloorDecibels));
        }
        return levels;
    }

    /// <summary>Which band a bar reads when there are fewer bars than bands: they sample evenly.</summary>
    public static int BarIndex(int bar, int bars, int bands)
    {
        if (bars <= 0 || bands <= 0) return 0;
        var position = (bar + 0.5) / bars * bands;
        return Math.Min(bands - 1, Math.Max(0, (int)Math.Floor(position)));
    }

    /// <summary>
    /// Bars rise fast and fall slowly, and the loudest band sets the scale so a quiet album still
    /// fills the meter. The scale itself relaxes slowly.
    /// </summary>
    public sealed class Smoother
    {
        public const double Attack = 0.55, Decay = 0.12, CeilingDecay = 0.995, PeakTarget = 0.92;
        private double[] _levels = [];
        private double _ceiling = 0.05;

        public double[] Next(IReadOnlyList<double> raw)
        {
            if (_levels.Length != raw.Count) _levels = new double[raw.Count];
            _ceiling = Math.Max(Math.Max(raw.Count > 0 ? raw.Max() : 0, _ceiling * CeilingDecay), 0.05);
            var gain = PeakTarget / _ceiling;
            for (var i = 0; i < raw.Count; i++)
            {
                var target = Math.Min(1, Math.Max(0, raw[i] * gain));
                var rate = target > _levels[i] ? Attack : Decay;
                _levels[i] += (target - _levels[i]) * rate;
            }
            return [.. _levels];
        }
    }
}

/// <summary>
/// The most recent captured audio as mono samples. Written from the capture thread, read by the
/// analysis timer (port of NotchAudioRing).
/// </summary>
public sealed class AudioRing(int capacity)
{
    private readonly object _gate = new();
    private readonly float[] _samples = new float[capacity];
    private int _head, _filled;
    private float _peak;
    private bool _heard;

    /// <summary>Whether any sample so far, turned up by `gain`, carried sound. Once heard, never undone.</summary>
    public bool HasHeard(float gain)
    {
        lock (_gate)
        {
            if (_peak * gain > 0.001f) _heard = true;
            return _heard;
        }
    }

    /// <summary>Interleaved float frames, downmixed to mono.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0) return;
        lock (_gate)
        {
            var frames = interleaved.Length / channels;
            var scale = 1f / channels;
            var loudest = 0f;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++) sum += interleaved[frame * channels + channel];
                var mono = sum * scale;
                _samples[_head] = mono;
                _head = (_head + 1) % capacity;
                loudest = Math.Max(loudest, Math.Abs(mono));
            }
            _filled = Math.Min(capacity, _filled + frames);
            _peak = Math.Max(_peak, loudest);
        }
    }

    /// <summary>The most recent `output.Length` samples, oldest first; false until that many arrived.</summary>
    public bool Latest(Span<float> output)
    {
        lock (_gate)
        {
            var count = output.Length;
            if (count <= 0 || count > capacity || _filled < count) return false;
            var index = (_head - count + capacity) % capacity;
            for (var position = 0; position < count; position++)
            {
                output[position] = _samples[index];
                index = (index + 1) % capacity;
            }
            return true;
        }
    }
}

/// <summary>A Hann-windowed FFT of one block of samples (port of NotchAudioAnalyzer).</summary>
public sealed class SpectrumAnalyzer
{
    public const int Size = 1024;
    public IReadOnlyList<Range> Bands { get; }
    private readonly float[] _window = new float[Size];
    private readonly float[] _real = new float[Size];
    private readonly float[] _imaginary = new float[Size];
    private readonly float[] _magnitudes = new float[Size / 2];

    public SpectrumAnalyzer(double sampleRate)
    {
        // vDSP_HANN_NORM: 0.5 * (1 - cos(2πn / N)).
        for (var n = 0; n < Size; n++) _window[n] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * n / Size)));
        Bands = NotchAudioLevelSupport.BandRanges(sampleRate, Size);
    }

    /// <summary>Magnitudes of the first Size/2 bins relative to full scale.</summary>
    public float[] Magnitudes(ReadOnlySpan<float> samples)
    {
        if (samples.Length != Size) return _magnitudes;
        for (var i = 0; i < Size; i++) { _real[i] = samples[i] * _window[i]; _imaginary[i] = 0; }
        Fft.Forward(_real, _imaginary);
        for (var k = 0; k < Size / 2; k++)
            _magnitudes[k] = MathF.Sqrt(_real[k] * _real[k] + _imaginary[k] * _imaginary[k]) / Size;
        return _magnitudes;
    }
}

/// <summary>An in-place iterative radix-2 complex FFT. The length must be a power of two.</summary>
public static class Fft
{
    public static void Forward(Span<float> real, Span<float> imaginary)
    {
        var n = real.Length;
        if (n != imaginary.Length || n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");
        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = (float)Math.Cos(angle);
            var stepImaginary = (float)Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                float wReal = 1, wImaginary = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = start + k;
                    var odd = even + length / 2;
                    var tReal = real[odd] * wReal - imaginary[odd] * wImaginary;
                    var tImaginary = real[odd] * wImaginary + imaginary[odd] * wReal;
                    real[odd] = real[even] - tReal;
                    imaginary[odd] = imaginary[even] - tImaginary;
                    real[even] += tReal;
                    imaginary[even] += tImaginary;
                    var nextReal = wReal * stepReal - wImaginary * stepImaginary;
                    wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                    wReal = nextReal;
                }
            }
        }
    }
}

/// <summary>
/// Which process a Windows media session belongs to. The media controls name the player by
/// app user model id: an exe name ("Spotify.exe"), a packaged app ("Family_hash!App"), or a
/// browser's own id ("Chrome", "MSEdge", "Chrome._crx_…" for installed web apps). New for Windows.
/// </summary>
public static class PlayerProcessMatch
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chrome"] = "chrome.exe",
        ["MSEdge"] = "msedge.exe",
        ["Brave"] = "brave.exe",
        ["Opera"] = "opera.exe",
        ["Vivaldi"] = "vivaldi.exe",
    };

    public static bool Matches(string? appUserModelId, string? exeName, string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return false;
        var id = appUserModelId.Trim();
        var bang = id.IndexOf('!');
        if (bang > 0)
            return packageFamilyName is { Length: > 0 } && id[..bang].Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(exeName)) return false;
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(id).Equals(exeName, StringComparison.OrdinalIgnoreCase);
        var stem = id.Split('.')[0];
        if (Aliases.TryGetValue(stem, out var alias)) return alias.Equals(exeName, StringComparison.OrdinalIgnoreCase);
        return Path.GetFileNameWithoutExtension(exeName).Equals(stem, StringComparison.OrdinalIgnoreCase);
    }
}
