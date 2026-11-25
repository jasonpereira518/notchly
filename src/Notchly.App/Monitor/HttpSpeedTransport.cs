// SPDX-License-Identifier: GPL-3.0-or-later
// The speed test's real transport: Cloudflare's public speed endpoints over HttpClient. The download body is only read
// for a successful status (an error body never counts as downloaded bytes) and the upload body is zeros.
using System.IO;
using System.Net.Http;
using Notchly.Core.Monitor;

namespace Notchly.App.Monitor;

public sealed class HttpSpeedTransport : ISpeedTransport, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) }) { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<int> LatencyAsync(CancellationToken cancellation)
    {
        using var response = await _client.GetAsync($"{SpeedTest.Host}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    public async Task<int> DownloadAsync(int bytes, Action<long> received, CancellationToken cancellation)
    {
        using var response = await _client.GetAsync($"{SpeedTest.Host}/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) return status;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0) received(read);
        return status;
    }

    public async Task<int> UploadAsync(int bytes, Action<long> sent, CancellationToken cancellation)
    {
        using var content = new StreamContent(new ZeroStream(bytes, sent));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = bytes;
        using var response = await _client.PostAsync($"{SpeedTest.Host}/__up", content, cancellation).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    public void Dispose() => _client.Dispose();

    /// <summary>A stream of zeros that reports how much has been read out of it (how much has been sent).</summary>
    private sealed class ZeroStream(long length, Action<long> progress) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, take);
            _position += take;
            progress(_position);
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
