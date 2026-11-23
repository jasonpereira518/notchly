// SPDX-License-Identifier: GPL-3.0-or-later
// Windows addition, not in upstream: Cursor keeps no usage logs, so its plan's included-usage allowance is read
// from cursor.com's usage summary with the session Cursor itself keeps signed in. The token is read from Cursor's
// state.vscdb (read-only), unsealed with Electron's safeStorage key (DPAPI, then AES-GCM) when sealed, kept in
// memory for the one request and never logged or written. The request goes to cursor.com only.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Notchly.Core;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

/// <summary>What one check of Cursor's usage found.</summary>
public abstract record CursorUsageResult
{
    /// <summary>The allowance as the island keeps limits, with the summary it came from (plan name, cycle).</summary>
    public sealed record Ok(CursorUsageSummary Summary, AgentLimits Limits) : CursorUsageResult;
    /// <summary>Cursor is not installed, keeps no session, or the service refused it.</summary>
    public sealed record SignedOut : CursorUsageResult;
    /// <summary>The service answered with something this reader does not understand.</summary>
    public sealed record Unrecognized : CursorUsageResult;
    public sealed record Unreachable : CursorUsageResult;

    /// <summary>The words the island shows for a result without numbers; null for <see cref="Ok"/>.</summary>
    public string? Message => this switch
    {
        SignedOut => "Sign in to Cursor",
        Unrecognized => "Cursor's usage service changed",
        Unreachable => "Couldn't reach Cursor",
        _ => null,
    };
}

/// <summary>Checks Cursor's included usage, and when the next check is due. Thread-safe.</summary>
public sealed class CursorUsageReader
{
    private const int MaximumBytes = 256 << 10;
    private static readonly HttpClient Client = CreateClient();

    private readonly Lock gate = new();
    private DateTimeOffset attempted = AgentTime.DistantPast;
    private int unreachable;
    private bool checking;

    public CursorUsageResult? Last { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, AppIdentity.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static string CursorUserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor");
    public static string StateDatabase => Path.Combine(CursorUserData, "User", "globalStorage", "state.vscdb");
    public static string LocalState => Path.Combine(CursorUserData, "Local State");

    /// <summary>Whether a check is due: five minutes after the last success (one while the page shows), backing off after
    /// failures up to half an hour; what only the person can fix waits the longest.</summary>
    public bool IsDue(DateTimeOffset now, bool pageVisible)
    {
        lock (gate)
        {
            if (checking) return false;
            AgentClaudeServer.Failure? failure = Last switch
            {
                null or CursorUsageResult.Ok => null,
                CursorUsageResult.Unreachable => AgentClaudeServer.Failure.Unreachable,
                _ => AgentClaudeServer.Failure.NeedsSignIn,
            };
            return now.Since(attempted) >= AgentClaudeServer.Interval(failure, unreachable, pageVisible);
        }
    }

    public void Retry()
    {
        lock (gate) attempted = AgentTime.DistantPast;
    }

    public void ResetBackoff()
    {
        lock (gate) unreachable = 0;
    }

    public async Task<CursorUsageResult> CheckAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            if (checking) return new CursorUsageResult.Unreachable();
            checking = true;
            attempted = DateTimeOffset.UtcNow;
        }
        CursorUsageResult result;
        try { result = await RunAsync(token); }
        catch (OperationCanceledException) { result = new CursorUsageResult.Unreachable(); }
        lock (gate)
        {
            checking = false;
            Last = result;
            unreachable = result is CursorUsageResult.Unreachable ? unreachable + 1 : 0;
        }
        return result;
    }

    private static async Task<CursorUsageResult> RunAsync(CancellationToken token)
    {
        var jwt = await Task.Run(() => CursorToken.Jwt(ReadStoredToken(), Unseal), token);
        if (jwt is null || CursorToken.Cookie(jwt) is not { } cookie) return new CursorUsageResult.SignedOut();
        var url = CursorUsageSummary.Endpoint;
        var host = url.Host;
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    // The session goes nowhere but cursor.com, over HTTPS.
                    if (response.Headers.Location is not { } location) return new CursorUsageResult.Unrecognized();
                    var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, host, StringComparison.OrdinalIgnoreCase))
                        return new CursorUsageResult.SignedOut();
                    url = next;
                    continue;
                }
                if (status is 401 or 403) return new CursorUsageResult.SignedOut();
                if (status is >= 500 or 408 or 429) return new CursorUsageResult.Unreachable();
                if (status != 200 || response.Content.Headers.ContentLength > MaximumBytes) return new CursorUsageResult.Unrecognized();
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, token)) > 0)
                {
                    if (buffer.Length + read > MaximumBytes) return new CursorUsageResult.Unrecognized();
                    buffer.Write(chunk, 0, read);
                }
                var summary = CursorUsageSummary.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                return summary.Limits(DateTimeOffset.UtcNow) is { } limits ? new CursorUsageResult.Ok(summary, limits) : new CursorUsageResult.Unrecognized();
            }
            return new CursorUsageResult.Unrecognized();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new CursorUsageResult.Unreachable();
        }
    }

    /// <summary>The value Cursor keeps under <see cref="CursorToken.StorageKey"/>, read without writing to its database.</summary>
    private static string? ReadStoredToken()
    {
        if (!File.Exists(StateDatabase)) return null;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = StateDatabase, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2,
        };
        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = $key";
            command.Parameters.AddWithValue("$key", CursorToken.StorageKey);
            return command.ExecuteScalar() switch
            {
                string text => text,
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => null,
            };
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>Opens a value Cursor sealed with Electron's safeStorage; the key is unsealed for this call only.</summary>
    private static byte[]? Unseal(byte[] blob)
    {
        byte[]? key = null;
        try
        {
            var info = new FileInfo(LocalState);
            if (!info.Exists || info.Length > 4 << 20) return null;
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            if (ElectronSafeStorage.SealedKey(reader.ReadToEnd()) is not { } sealedKey) return null;
            key = ProtectedData.Unprotect(sealedKey, null, DataProtectionScope.CurrentUser);
            return ElectronSafeStorage.Open(blob, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}
