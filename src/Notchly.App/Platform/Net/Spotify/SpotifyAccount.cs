// SPDX-License-Identifier: GPL-3.0-or-later
// The Spotify account Notchly is signed in to: Authorization Code with PKCE (no client secret), through the
// default browser and a one-shot listener on 127.0.0.1. The refresh token is sealed for this Windows user (DPAPI)
// in %APPDATA%\Notchly\spotify.json, never in settings.json, backups, the dotfile or the log; the access token
// lives in memory only. A Windows addition: upstream reads the Mac player's own queue instead.
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchly.Core;
using Notchly.Core.Notch.Music;
using Serilog;

namespace Notchly.App.Platform.Net.Spotify;

public static class SpotifyAccount
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Notchly Spotify");
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static State _state = new("", "", "", "");
    private static SpotifyTokens? _tokens;
    private static bool _loaded;

    /// <summary>The account file; settable so a self-check never touches the real one.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppIdentity.DataDirectory, "spotify.json");

    /// <summary>Raised when signing in or out, on the thread that did it.</summary>
    public static event Action? Changed;

    private sealed record State(string ClientId, string RefreshToken, string Scope, string Name);

    public static bool IsSignedIn { get { Load(); return _state.RefreshToken.Length > 0; } }
    public static string DisplayName { get { Load(); return _state.Name; } }
    public static string ClientId { get { Load(); return _state.ClientId; } }

    /// <summary>The address to register in the Spotify developer dashboard.</summary>
    public static string RedirectUri => SpotifyAuthSupport.RedirectUri(SpotifyAuthSupport.PreferredPort);

    // MARK: Storage

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(FilePath)) return;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(FilePath));
                var sealedToken = root?["refresh"]?.GetValue<string>();
                var clientId = root?["clientId"]?.GetValue<string>();
                if (sealedToken is null || clientId is null) return;
                var refresh = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedToken), Entropy, DataProtectionScope.CurrentUser));
                _state = new State(clientId, refresh, root?["scope"]?.GetValue<string>() ?? "", root?["name"]?.GetValue<string>() ?? "");
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or CryptographicException or FormatException)
            {
                Log.Warning("The Spotify sign-in could not be read; sign in again");
            }
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            var sealedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(_state.RefreshToken), Entropy, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, new JsonObject
            {
                ["version"] = 1, ["clientId"] = _state.ClientId, ["name"] = _state.Name, ["scope"] = _state.Scope, ["refresh"] = sealedToken,
            }.ToJsonString());
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    public static void SignOut()
    {
        lock (Gate)
        {
            _loaded = true;
            _state = new State(_state.ClientId, "", "", "");
            _tokens = null;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (IOException e) { Log.Warning(e, "The Spotify sign-in file could not be removed"); }
        }
        Changed?.Invoke();
    }

    // MARK: Sign in

    /// <summary>
    /// Opens Spotify's consent page in the default browser and waits (up to three minutes) for the answer.
    /// Returns null when signed in, or a problem in plain words.
    /// </summary>
    public static async Task<string?> SignInAsync(string clientId, CancellationToken token = default)
    {
        clientId = clientId.Trim();
        if (clientId.Length == 0) return "Paste your Spotify Client ID first.";
        var listener = StartListener(out var port);
        if (listener is null) return "Notchly could not listen for Spotify's answer on this PC.";
        try
        {
            var verifier = SpotifyAuthSupport.NewVerifier();
            var state = SpotifyAuthSupport.NewState();
            var redirect = SpotifyAuthSupport.RedirectUri(port);
            var url = SpotifyAuthSupport.AuthorizeUrl(clientId, redirect, SpotifyAuthSupport.Challenge(verifier), state);
            try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return "Notchly could not open your browser."; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            SpotifyAuthSupport.CallbackResult? answer;
            try { answer = await WaitForCallbackAsync(listener, state, timeout.Token); }
            catch (OperationCanceledException) { return token.IsCancellationRequested ? "Sign-in cancelled." : "Spotify did not answer in time."; }

            if (answer.Error is not null) return answer.Error == "access_denied" ? "Spotify sign-in was declined." : $"Spotify refused the sign-in ({answer.Error}).";
            if (!answer.StateMatches || answer.Code is null) return "Spotify's answer did not match this sign-in. Try again.";

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = answer.Code, ["redirect_uri"] = redirect,
                ["client_id"] = clientId, ["code_verifier"] = verifier,
            });
            var (tokens, problem) = await RequestTokensAsync(form, null, token);
            if (tokens is null) return problem;
            if (!SpotifyAuthSupport.CoversScopes(tokens.Scope)) return "Spotify did not grant every permission Notchly asks for. Sign in again and allow them all.";
            lock (Gate)
            {
                _loaded = true;
                _tokens = tokens;
                _state = new State(clientId, tokens.RefreshToken, tokens.Scope, "");
            }
            var name = await ReadNameAsync(tokens.AccessToken, token);
            lock (Gate) _state = _state with { Name = name };
            Save();
            Changed?.Invoke();
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static TcpListener? StartListener(out int port)
    {
        foreach (var candidate in new[] { SpotifyAuthSupport.PreferredPort, 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                return listener;
            }
            catch (SocketException) { listener.Stop(); }
        }
        port = 0;
        return null;
    }

    private static async Task<SpotifyAuthSupport.CallbackResult> WaitForCallbackAsync(TcpListener listener, string state, CancellationToken token)
    {
        var done = new TaskCompletionSource<SpotifyAuthSupport.CallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => done.TrySetCanceled(token));
        // Browsers open extra connections ahead of time that send nothing, so each is handled on its own.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleAsync(client, state, done, token), token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }, token);
        return await done.Task;
    }

    private static async Task HandleAsync(TcpClient client, string state, TaskCompletionSource<SpotifyAuthSupport.CallbackResult> done, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
                read.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(read.Token);
                var result = SpotifyAuthSupport.ParseCallback(line, state);
                var body = result is null ? "" : Page(result);
                var head = $"HTTP/1.1 {(result is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head + body), token);
                await stream.FlushAsync(token);
                if (result is not null) done.TrySetResult(result);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
        }
    }

    private static string Page(SpotifyAuthSupport.CallbackResult result)
    {
        var message = result.Code is not null && result.StateMatches
            ? "Notchly is connected to Spotify. You can close this tab."
            : "Notchly could not connect to Spotify. You can close this tab and try again.";
        return $"<!doctype html><meta charset=\"utf-8\"><title>Notchly</title><body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;text-align:center;padding-top:20vh\"><h2>{message}</h2></body>";
    }

    // MARK: Tokens

    private static async Task<(SpotifyTokens? Tokens, string? Problem)> RequestTokensAsync(HttpContent form, string? previousRefresh, CancellationToken token)
    {
        try
        {
            using var response = await SpotifyHttp.Client.PostAsync(SpotifyAuthSupport.TokenEndpoint, form, token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (response.IsSuccessStatusCode)
            {
                var tokens = SpotifyAuthSupport.ParseTokens(text, DateTimeOffset.Now, previousRefresh);
                return tokens is null ? (null, "Spotify's reply could not be read.") : (tokens, null);
            }
            Log.Warning("Spotify's token endpoint answered {Status}", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.BadRequest && text.Contains("invalid_grant", StringComparison.Ordinal)) return (null, "invalid_grant");
            if (response.StatusCode == HttpStatusCode.BadRequest && text.Contains("invalid_client", StringComparison.Ordinal))
                return (null, "Spotify does not know that Client ID. Check it in your developer dashboard.");
            return (null, $"Spotify answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            return (null, "Could not reach Spotify.");
        }
    }

    /// <summary>A valid access token, refreshed when it is about to lapse; null when signed out or Spotify refuses the refresh.</summary>
    public static async Task<string?> AccessTokenAsync(CancellationToken token = default)
    {
        Load();
        if (_tokens is { } current && !current.NeedsRefresh(DateTimeOffset.Now)) return current.AccessToken;
        await RefreshGate.WaitAsync(token);
        try
        {
            if (_tokens is { } fresh && !fresh.NeedsRefresh(DateTimeOffset.Now)) return fresh.AccessToken;
            State state;
            lock (Gate) state = _state;
            if (state.RefreshToken.Length == 0 || state.ClientId.Length == 0) return null;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = state.RefreshToken, ["client_id"] = state.ClientId,
            });
            var (tokens, problem) = await RequestTokensAsync(form, state.RefreshToken, token);
            if (tokens is null)
            {
                // A revoked or expired grant cannot recover: ask for a new sign-in. A network problem can, so it stays signed in.
                if (problem == "invalid_grant") SignOut();
                return null;
            }
            lock (Gate)
            {
                _tokens = tokens;
                if (tokens.RefreshToken != _state.RefreshToken) _state = _state with { RefreshToken = tokens.RefreshToken };
            }
            if (tokens.RefreshToken != state.RefreshToken) Save();
            return tokens.AccessToken;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    /// <summary>Forgets the access token so the next call refreshes (after Spotify answered 401).</summary>
    public static void ExpireAccessToken() { lock (Gate) _tokens = null; }

    private static async Task<string> ReadNameAsync(string accessToken, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await SpotifyHttp.Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return "";
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return document.RootElement.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (token.IsCancellationRequested) throw;
            return "";
        }
    }
}

/// <summary>One HTTP client for Spotify's accounts and web API: short timeouts, no redirects, an identifying User-Agent.</summary>
internal static class SpotifyHttp
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(AppIdentity.Name, AppIdentity.Version));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
