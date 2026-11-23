// SPDX-License-Identifier: GPL-3.0-or-later
// Reads one player's audio and turns it into seven band levels: the Windows counterpart of
// upstream's NotchAudioLevelReader (a Core Audio process tap). Windows process loopback
// (NAudio's WasapiRecorder) hears the target process and its children, with no permission
// prompt. Captured packets go into a mono ring; a 30 Hz timer analyses the latest block.
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Notchly.Core.Notch;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Notchly.App.Platform.Audio;

public sealed class ProcessLevelReader : IDisposable
{
    private readonly uint _processId;
    private readonly AudioRing _ring = new(8192);
    private readonly NotchAudioLevelSupport.Smoother _smoother = new();
    private readonly float[] _samples = new float[SpectrumAnalyzer.Size];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WasapiRecorder? _recorder;
    private SpectrumAnalyzer? _analyzer;
    private Timer? _timer;
    private Process? _process;
    private WaveFormat? _format;
    private int _analysing, _stopped;
    private double _nextProcessCheck;

    /// <summary>Seven smoothed levels, about 30 times a second (on a pool thread).</summary>
    public event Action<double[]>? Levels;
    /// <summary>The capture only ever delivered silence for the grace period.</summary>
    public event Action? Silent;
    /// <summary>No capture could be created, or it failed.</summary>
    public event Action? Unavailable;
    /// <summary>The player's process exited, taking the capture's ears with it.</summary>
    public event Action? ProcessesLeft;

    public ProcessLevelReader(uint processId) => _processId = processId;

    public async Task StartAsync()
    {
        try
        {
            _process = Process.GetProcessById((int)_processId);
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(_processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
                .BuildAsync();
            if (Volatile.Read(ref _stopped) != 0) { recorder.Dispose(); return; }
            _recorder = recorder;
            _format = recorder.WaveFormat;
            _analyzer = new SpectrumAnalyzer(_format.SampleRate);
            recorder.DataAvailable += OnData;
            recorder.StartRecording();
            _timer = new Timer(_ => Analyse(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1 / NotchAudioLevelSupport.UpdatesPerSecond));
            Log.Information("Live equalizer reading process {Pid} ({Format})", _processId, _format);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Live equalizer could not read process {Pid}", _processId);
            Unavailable?.Invoke();
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_format is not { } format || buffer.IsEmpty || (flags & AudioClientBufferFlags.Silent) != 0) return;
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            _ring.Write(MemoryMarshal.Cast<byte, float>(buffer), channels);
        }
        else if (format.BitsPerSample == 16)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(buffer);
            var floats = ArrayPool<float>.Shared.Rent(pcm.Length);
            for (var i = 0; i < pcm.Length; i++) floats[i] = pcm[i] / 32768f;
            _ring.Write(floats.AsSpan(0, pcm.Length), channels);
            ArrayPool<float>.Shared.Return(floats);
        }
    }

    private void Analyse()
    {
        if (Volatile.Read(ref _stopped) != 0 || _analyzer is null || Interlocked.Exchange(ref _analysing, 1) == 1) return;
        try
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (now >= _nextProcessCheck)
            {
                _nextProcessCheck = now + 1;
                if (_process is null || _process.HasExited) { ProcessesLeft?.Invoke(); return; }
            }
            // Levels are reported only once sound has arrived, so the bars keep their usual motion
            // while the capture warms up, and this play is given up when it only ever hears silence.
            if (!_ring.HasHeard(1))
            {
                if (NotchAudioLevelSupport.FallsBack(heard: false, elapsed: now)) Silent?.Invoke();
                return;
            }
            if (!_ring.Latest(_samples)) return;
            var raw = NotchAudioLevelSupport.BandLevels(_analyzer.Magnitudes(_samples), _analyzer.Bands);
            Levels?.Invoke(_smoother.Next(raw));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ProcessesLeft?.Invoke();
        }
        finally { Volatile.Write(ref _analysing, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _timer?.Dispose();
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnData;
            // Stopping can wait on the capture thread: keep it off the UI thread.
            _ = Task.Run(() =>
            {
                try { recorder.StopRecording(); } catch (Exception) { }
                recorder.Dispose();
            });
        }
        _process?.Dispose();
    }
}
