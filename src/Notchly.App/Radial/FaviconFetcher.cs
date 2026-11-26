// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's radial menu favicon fetch (RadialMenuSupport / the URL editor): `<origin>/favicon.ico` for a link slice, fetched only
// when the person presses the button. Limits as upstream: 5 s, 2 MB downloaded, 4096 pixels a side and 16.7 million pixels, redirects only
// within the same site, scaled to 64 pixels, and kept only when the PNG fits in 64 KB.
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Notchly.Core.Radial;
using Serilog;

namespace Notchly.App.Radial;

public static class FaviconFetcher
{
    public const int DownloadLimit = 2 * 1024 * 1024, MaxSide = 4096, MaxPixels = 16_700_000, IconSide = 64, RedirectLimit = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The favicon's address for a link slice's payload, or null when the payload is not a web address.</summary>
    public static Uri? FaviconUrl(string payload)
    {
        if (RadialMenuSupport.NormalizedUrl(payload) is not { } normalized || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
    }

    /// <summary>The icon as PNG bytes, or null when it could not be fetched, is not an image, or does not fit.</summary>
    public static async Task<byte[]?> FetchAsync(string payload, HttpMessageHandler? handler = null, CancellationToken token = default)
    {
        if (FaviconUrl(payload) is not { } start) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null);
            var uri = start;
            for (var hop = 0; hop <= RedirectLimit; hop++)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    // Only within the same site: an icon is not worth following a link to somewhere else for.
                    if (!SameSite(start, next)) return null;
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                if (response.Content.Headers.ContentLength > DownloadLimit) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var data = await ReadLimited(stream, timeout.Token);
                return data is null ? null : Scaled(data);
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            Log.Debug("Favicon not fetched: {Message}", e.Message);
            return null;
        }
    }

    private static bool SameSite(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static async Task<byte[]?> ReadLimited(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > DownloadLimit) return null;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>Decodes an image of any common kind, refuses a huge one, and scales it to 64 pixels as a PNG; null if it does not fit in 64 KB.</summary>
    public static byte[]? Scaled(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            // An .ico holds several sizes: the largest one is scaled down.
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > MaxSide || frame.PixelHeight > MaxSide
                || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels) return null;
            var scale = Math.Min(1.0, (double)IconSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource source = frame;
            if (scale < 1) source = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            var png = output.ToArray();
            return png.Length <= RadialMenuSupport.MaxStoredIconBytes ? png : null;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or System.IO.FileFormatException)
        {
            return null;
        }
    }
}
