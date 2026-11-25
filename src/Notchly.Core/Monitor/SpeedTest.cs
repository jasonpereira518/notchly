// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Metrics/SpeedTest.swift: a user-started test of latency, then download, then upload,
// against Cloudflare's public speed endpoints, time-boxed so it stays bounded on any link. The transport and the
// clock are injected, so the request sequences are testable. No user data leaves the PC (the upload body is zeros).
namespace Notchly.Core.Monitor;

public enum SpeedPhase { Idle, Latency, Download, Upload, Done, Failed }

/// <summary>What the test asks of the network. Each call throws on a transport failure; a status outside 200–299 fails the test.</summary>
public interface ISpeedTransport
{
    /// <summary>One latency request (an empty download); returns its HTTP status.</summary>
    Task<int> LatencyAsync(CancellationToken cancellation);

    /// <summary>One download chunk; reports body bytes as they arrive, and none for an error status.</summary>
    Task<int> DownloadAsync(int bytes, Action<long> received, CancellationToken cancellation);

    /// <summary>One upload of `bytes` zeros; reports bytes as they are sent.</summary>
    Task<int> UploadAsync(int bytes, Action<long> sent, CancellationToken cancellation);
}

public sealed class SpeedTest
{
    public const string Host = "https://speed.cloudflare.com";
    public const int LatencyRequests = 5;
    // Cloudflare's __down caps the size just under 100 MB, so chunks are requested under that and looped until the time box.
    public const int DownloadBytes = 90_000_000, UploadBytes = 100_000_000;

    private readonly ISpeedTransport _transport;
    private readonly Func<double> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly double _sampleSeconds;
    private int _generation;
    private CancellationTokenSource? _run;

    public SpeedPhase Phase { get; private set; } = SpeedPhase.Idle;
    public double? LatencyMs { get; private set; }
    public double? DownloadMbps { get; private set; }
    public double? UploadMbps { get; private set; }
    public string? Error { get; private set; }
    public bool IsRunning => Phase is SpeedPhase.Latency or SpeedPhase.Download or SpeedPhase.Upload;

    /// <summary>Raised whenever the phase or a result changes (on the thread that ran the step).</summary>
    public event Action? Changed;

    public SpeedTest(ISpeedTransport transport, Func<double> clock, Func<TimeSpan, CancellationToken, Task> delay, double sampleSeconds = 5)
    {
        _transport = transport;
        _clock = clock;
        _delay = delay;
        _sampleSeconds = sampleSeconds;
    }

    private static bool Successful(int status) => status is >= 200 and <= 299;

    private void Set(SpeedPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        _generation++;
        _run?.Cancel();
        if (IsRunning) Set(SpeedPhase.Idle);
    }

    public async Task RunAsync()
    {
        if (IsRunning) return;
        LatencyMs = DownloadMbps = UploadMbps = null;
        Error = null;
        var generation = ++_generation;
        using var run = _run = new CancellationTokenSource();
        try
        {
            Set(SpeedPhase.Latency);
            var best = double.MaxValue;
            for (var i = 0; i < LatencyRequests; i++)
            {
                var started = _clock();
                var status = await _transport.LatencyAsync(run.Token).ConfigureAwait(false);
                var rtt = (_clock() - started) * 1000;
                if (generation != _generation) return;
                if (!Successful(status)) { Fail($"The server answered {status}"); return; }
                best = Math.Min(best, rtt);
            }
            LatencyMs = best == double.MaxValue ? null : best;
            Changed?.Invoke();

            if (!await TransferAsync(download: true, generation, run.Token).ConfigureAwait(false)) return;
            if (!await TransferAsync(download: false, generation, run.Token).ConfigureAwait(false)) return;
            if (generation == _generation) Set(SpeedPhase.Done);
        }
        catch (OperationCanceledException) when (generation != _generation) { }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            if (generation == _generation) Fail(e.Message);
        }
    }

    /// <summary>One time-boxed transfer. A download loops chunks to keep the pipe full; an upload is a single body.</summary>
    private async Task<bool> TransferAsync(bool download, int generation, CancellationToken cancellation)
    {
        Set(download ? SpeedPhase.Download : SpeedPhase.Upload);
        long transferred = 0;
        var started = _clock();
        using var box = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var delay = _delay(TimeSpan.FromSeconds(_sampleSeconds), box.Token);
        var timeBox = delay.ContinueWith(t => { if (!t.IsCanceled) box.Cancel(); }, TaskScheduler.Default);
        var timedOut = false;
        try
        {
            do
            {
                var status = download
                    ? await _transport.DownloadAsync(DownloadBytes, n => Interlocked.Add(ref transferred, n), box.Token).ConfigureAwait(false)
                    : await _transport.UploadAsync(UploadBytes, n => Interlocked.Exchange(ref transferred, n), box.Token).ConfigureAwait(false);
                if (generation != _generation) return false;
                if (!Successful(status)) { Fail($"The server answered {status}"); return false; }
            }
            while (download && !delay.IsCompleted);
        }
        catch (OperationCanceledException) when (box.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            // The time box ended the transfer, which is how it normally finishes.
            timedOut = true;
        }
        finally
        {
            box.Cancel();
            try { await timeBox.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (generation != _generation) return false;
        _ = timedOut;
        var elapsed = _clock() - started;
        var mbps = elapsed > 0 ? Math.Max(0, Interlocked.Read(ref transferred) * 8 / elapsed / 1_000_000) : 0;
        if (download) DownloadMbps = mbps; else UploadMbps = mbps;
        Changed?.Invoke();
        return true;
    }

    private void Fail(string message)
    {
        _generation++;
        Error = message;
        Set(SpeedPhase.Failed);
    }
}
