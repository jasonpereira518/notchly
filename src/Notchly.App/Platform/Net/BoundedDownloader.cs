// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream NotchLyricsDownload (Services/Notch/NotchLyricsService.swift): one GET with a
// byte cap, no redirects, short timeouts and an identifying User-Agent. Status codes map to the
// results the lookup chain needs to decide whether to keep searching.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Notchly.Core;
using Notchly.Core.Notch.Lyrics;

namespace Notchly.App.Platform.Net;

public static class BoundedDownloader
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, AppIdentity.Version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows port of vorssaint-utils)"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// 200 with at most `maximumBytes` is a payload, 404 is missing, 5xx is a service outage, and
    /// everything else (network errors, redirects, rate limits, oversized replies) is a failure.
    /// Cancellation by the caller propagates as OperationCanceledException.
    /// </summary>
    public static async Task<NotchLyricsDownloadResult> LoadAsync(Uri url, int maximumBytes, CancellationToken token)
    {
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            if (status == 404) return new NotchLyricsDownloadResult.Missing();
            if (status is >= 500 and <= 599) return new NotchLyricsDownloadResult.ServiceUnavailable();
            if (status != 200 || response.Content.Headers.ContentLength > maximumBytes) return new NotchLyricsDownloadResult.Failed();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > maximumBytes) return new NotchLyricsDownloadResult.Failed();
                buffer.Write(chunk, 0, read);
            }
            return new NotchLyricsDownloadResult.Payload(buffer.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new NotchLyricsDownloadResult.Failed();
        }
    }

    // MARK: Calendar feeds

    private static readonly HttpClient FeedClient = CreateFeedClient();

    private static HttpClient CreateFeedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, AppIdentity.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
        return client;
    }

    public sealed record FeedResult(byte[]? Payload, bool NotModified, string? ETag, DateTimeOffset? LastModified, string? Problem);

    /// <summary>
    /// A calendar feed: HTTPS only, at most three HTTPS redirects, <paramref name="maximumBytes"/> at most,
    /// and a conditional request when validators from the last read are given. The link is a secret, so a
    /// problem names only the host.
    /// </summary>
    public static async Task<FeedResult> LoadFeedAsync(Uri url, int maximumBytes, string? etag, DateTimeOffset? lastModified,
                                                       CancellationToken token)
    {
        var host = url.Host;
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                if (url.Scheme != Uri.UriSchemeHttps) return new FeedResult(null, false, null, null, $"{host} asked for an unencrypted link");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                if (lastModified is { } since) request.Headers.IfModifiedSince = since;
                using var response = await FeedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    continue;
                }
                if (status == 304) return new FeedResult(null, true, etag, lastModified, null);
                if (status is 401 or 403) return new FeedResult(null, false, null, null, $"{host} refused the link (it may have been reset or unpublished)");
                if (status == 404) return new FeedResult(null, false, null, null, $"{host} no longer has this calendar");
                if (status != 200) return new FeedResult(null, false, null, null, $"{host} answered {status}");
                if (response.Content.Headers.ContentLength > maximumBytes) return new FeedResult(null, false, null, null, "The calendar is too large");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, token)) > 0)
                {
                    if (buffer.Length + read > maximumBytes) return new FeedResult(null, false, null, null, "The calendar is too large");
                    buffer.Write(chunk, 0, read);
                }
                return new FeedResult(buffer.ToArray(), false, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, null);
            }
            return new FeedResult(null, false, null, null, $"{host} redirected too many times");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new FeedResult(null, false, null, null, $"Couldn't reach {host}");
        }
    }
}
