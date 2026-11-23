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
}
