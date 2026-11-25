// SPDX-License-Identifier: GPL-3.0-or-later
// One call to Spotify's web API with the signed-in account: a bearer token, one retry after a 401 (the token
// lapsed early) and one after a short 429 wait. Replies are read, never logged.
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Serilog;

namespace Notchly.App.Platform.Net.Spotify;

/// <summary>Status 0 means the call never reached Spotify or nobody is signed in.</summary>
public sealed record SpotifyReply(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
    /// <summary>204 and 202 answer commands that have nothing to say.</summary>
    public bool Empty => Status is 202 or 204 || (Ok && Body.Length == 0);
}

public static class SpotifyApi
{
    private const string Base = "https://api.spotify.com/v1";
    /// <summary>A longer Retry-After than this is not waited for: the page just shows what it had.</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(3);

    public static Task<SpotifyReply> GetAsync(string path, CancellationToken token = default) => SendAsync(HttpMethod.Get, path, null, token);
    public static Task<SpotifyReply> PutAsync(string path, string? json = null, CancellationToken token = default) => SendAsync(HttpMethod.Put, path, json, token);
    public static Task<SpotifyReply> DeleteAsync(string path, string? json = null, CancellationToken token = default) => SendAsync(HttpMethod.Delete, path, json, token);

    public static async Task<SpotifyReply> SendAsync(HttpMethod method, string path, string? json, CancellationToken token = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await SpotifyAccount.AccessTokenAsync(token);
            if (access is null) return new SpotifyReply(0, "");
            try
            {
                using var request = new HttpRequestMessage(method, Base + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                else if (method != HttpMethod.Get) request.Content = new StringContent("", Encoding.UTF8, "application/json");
                using var response = await SpotifyHttp.Client.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    SpotifyAccount.ExpireAccessToken();
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0
                    && response.Headers.RetryAfter?.Delta is { } wait && wait <= LongestWait)
                {
                    await Task.Delay(wait, token);
                    continue;
                }
                if (!response.IsSuccessStatusCode) Log.Debug("Spotify {Method} {Path} answered {Status}", method, path.Split('?')[0], (int)response.StatusCode);
                return new SpotifyReply((int)response.StatusCode, body);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                Log.Debug("Spotify {Method} {Path} could not be reached", method, path.Split('?')[0]);
                return new SpotifyReply(0, "");
            }
        }
        return new SpotifyReply(0, "");
    }
}
