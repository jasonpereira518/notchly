// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the downloading half of upstream Sources/Vorssaint/Services/AgentUsage/AgentPriceSource.swift and
// AgentUsageService's syncPrices: the price list published with the project, fetched at most once a day (six hours
// after a failure) while the caller keeps prices up to date, cached at %LOCALAPPDATA%\Notchly\agent-prices.json.
// The request carries no usage and nothing from this computer; redirects stay on the same host, over HTTPS.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Notchly.Core;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

/// <summary>Keeps the installed price list the newest one known. Thread-safe.</summary>
public sealed class AgentPriceUpdater
{
    private static readonly HttpClient Client = CreateClient();
    private readonly Lock gate = new();
    private DateTimeOffset attempted = AgentTime.DistantPast;
    private bool failed, inFlight;

    public static string CachePath => Path.Combine(AppIdentity.LocalDataDirectory, "agent-prices.json");

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, AppIdentity.Version));
        return client;
    }

    /// <summary>The last list downloaded and when it was saved.</summary>
    public static (AgentPriceList List, DateTimeOffset Saved)? Cached()
    {
        try
        {
            var info = new FileInfo(CachePath);
            if (!info.Exists || info.Length > AgentPriceList.MaximumSize) return null;
            return AgentPriceList.Decode(File.ReadAllBytes(info.FullName)) is { } list
                ? (list, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Installs the newer of the shipped list and the cached one; true when prices changed.</summary>
    public static bool InstallCached() =>
        AgentPricing.Install(AgentPriceList.Newer(AgentPriceSource.Bundled(), Cached()?.List) ?? AgentPriceList.Empty);

    /// <summary>Whether a download is due now.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        var saved = Cached()?.Saved ?? AgentTime.DistantPast;
        lock (gate) return !inFlight && AgentPriceSource.DownloadDue(now, saved, attempted, failed);
    }

    /// <summary>Downloads the published list when due, saves it and installs the newer one. True when the installed
    /// list changed, so the store should <see cref="AgentLogStore.Reprice"/>.</summary>
    public async Task<bool> RefreshAsync(CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!IsDue(now)) return false;
        lock (gate)
        {
            inFlight = true;
            attempted = now;
        }
        byte[]? data = null;
        try { data = await DownloadAsync(AgentPriceSource.Remote, AgentPriceList.MaximumSize, token); }
        finally
        {
            lock (gate) inFlight = false;
        }
        var list = data is null ? null : AgentPriceList.Decode(data);
        lock (gate) failed = list is null;
        if (list is null || data is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + ".tmp";
            await File.WriteAllBytesAsync(temporary, data, token);
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return AgentPricing.Install(AgentPriceList.Newer(AgentPriceSource.Bundled(), list) ?? list);
    }

    /// <summary>One bounded GET that follows redirects only on its own host, over HTTPS; null on any failure.</summary>
    private static async Task<byte[]?> DownloadAsync(Uri url, int limit, CancellationToken token)
    {
        var host = url.Host;
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location) return null;
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme != Uri.UriSchemeHttps || !string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase)) return null;
                    continue;
                }
                if (status != 200 || response.Content.Headers.ContentLength > limit) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, token)) > 0)
                {
                    if (buffer.Length + read > limit) return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }
}
