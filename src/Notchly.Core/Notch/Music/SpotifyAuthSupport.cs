// SPDX-License-Identifier: GPL-3.0-or-later
// The pure parts of signing in to Spotify: the PKCE codes (RFC 7636), the authorisation address, the loopback
// reply and the token reply. A Windows addition; the socket and the browser stay in the app project.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notchly.Core.Notch.Music;

public sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset Expires, string Scope)
{
    /// <summary>True from a minute before expiry, so a call never starts with a token about to lapse.</summary>
    public bool NeedsRefresh(DateTimeOffset now) => now >= Expires - TimeSpan.FromSeconds(60);
}

public static class SpotifyAuthSupport
{
    /// <summary>Read the player, start playback, read and change Liked Songs, and read playlists.</summary>
    public static readonly string[] Scopes =
    [
        "user-read-playback-state", "user-modify-playback-state", "user-read-currently-playing",
        "playlist-read-private", "playlist-read-collaborative", "user-library-read", "user-library-modify",
    ];

    /// <summary>The port tried first, so the redirect address to register is always the same.</summary>
    public const int PreferredPort = 47821;
    public const string CallbackPath = "/callback";
    public const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    public const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    public static string RedirectUri(int port) => $"http://127.0.0.1:{port}{CallbackPath}";

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A fresh code verifier: 64 random bytes as 86 URL-safe characters (RFC 7636 allows 43 to 128).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    /// <summary>A fresh value that must come back unchanged, so a stray request to the callback is ignored.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>The S256 challenge for a verifier.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static Uri AuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
    {
        var query = string.Join('&', new (string Key, string Value)[]
        {
            ("client_id", clientId.Trim()),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("code_challenge_method", "S256"),
            ("code_challenge", challenge),
            ("state", state),
            ("scope", string.Join(' ', Scopes)),
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    /// <summary>What the browser sent back to the loopback address.</summary>
    public sealed record CallbackResult(string? Code, string? Error, bool StateMatches);

    /// <summary>
    /// Reads the request line of the browser's call to the loopback address ("GET /callback?code=..&amp;state=.. HTTP/1.1").
    /// Null when it is some other request, such as the browser asking for a favicon.
    /// </summary>
    public static CallbackResult? ParseCallback(string? requestLine, string expectedState)
    {
        if (string.IsNullOrWhiteSpace(requestLine)) return null;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "GET") return null;
        var target = parts[1];
        var mark = target.IndexOf('?');
        var path = mark < 0 ? target : target[..mark];
        if (path != CallbackPath) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mark >= 0)
            foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.IndexOf('=');
                var key = split < 0 ? pair : pair[..split];
                var value = split < 0 ? "" : pair[(split + 1)..];
                values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
        var matches = values.TryGetValue("state", out var state) && state == expectedState;
        values.TryGetValue("code", out var code);
        values.TryGetValue("error", out var error);
        return new CallbackResult(string.IsNullOrEmpty(code) ? null : code, string.IsNullOrEmpty(error) ? null : error, matches);
    }

    /// <summary>
    /// The tokens from the token endpoint's reply. A refresh reply may leave the refresh token out, in which case the
    /// one already held stays good. Null when the reply has no access token.
    /// </summary>
    public static SpotifyTokens? ParseTokens(string? json, DateTimeOffset now, string? previousRefreshToken = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            refresh = string.IsNullOrEmpty(refresh) ? previousRefreshToken : refresh;
            if (string.IsNullOrEmpty(refresh)) return null;
            var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) && s > 0 ? s : 3600;
            var scope = root.TryGetProperty("scope", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            return new SpotifyTokens(access, refresh, now + TimeSpan.FromSeconds(seconds), scope);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True when the granted scopes cover everything the Music page needs.</summary>
    public static bool CoversScopes(string grantedScope)
    {
        var granted = grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return Scopes.All(granted.Contains);
    }
}
