// SPDX-License-Identifier: GPL-3.0-or-later
// Windows addition, not in upstream: Cursor keeps no usage logs, so it only contributes its plan's included-usage
// allowance, read from cursor.com's usage summary with the session Cursor itself keeps signed in. These are the
// pure halves: the token as Cursor stores it, the session cookie, and a tolerant reading of the reply. Reading
// state.vscdb, unsealing with DPAPI and AES-GCM, and the request are the app's job.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Notchly.Core.Agents;

/// <summary>Cursor's access token, as <c>cursorAuth/accessToken</c> holds it in its <c>state.vscdb</c>.</summary>
public static class CursorToken
{
    /// <summary>The key of the token in state.vscdb's ItemTable.</summary>
    public const string StorageKey = "cursorAuth/accessToken";
    /// <summary>Newer Cursor builds seal the token with the OS's safe storage behind this prefix.</summary>
    public const string SealedPrefix = "cursor-safe-storage:v1:";

    /// <summary>
    /// The JWT the stored value holds: the value itself when it is three base64url segments, or, behind
    /// <see cref="SealedPrefix"/>, the base64 remainder passed through <paramref name="unseal"/> and read as UTF-8.
    /// Null when it is neither, or unsealing fails.
    /// </summary>
    public static string? Jwt(string? stored, Func<byte[], byte[]?>? unseal)
    {
        if (stored is null) return null;
        var value = stored.Trim().Trim('"').Trim();
        if (value.StartsWith(SealedPrefix, StringComparison.Ordinal))
        {
            if (unseal is null) return null;
            byte[] sealedBytes;
            try { sealedBytes = Convert.FromBase64String(value[SealedPrefix.Length..].Trim()); }
            catch (FormatException) { return null; }
            byte[]? opened;
            try { opened = unseal(sealedBytes); }
            catch (Exception) { return null; }
            if (opened is null) return null;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(opened).Trim().Trim('"').Trim(); }
            catch (DecoderFallbackException) { return null; }
            return IsJwt(text) ? text : null;
        }
        return IsJwt(value) ? value : null;
    }

    /// <summary>Three non-empty base64url segments separated by dots.</summary>
    public static bool IsJwt(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '='));
    }

    /// <summary>The user the token names: its payload's <c>sub</c>, after the last "|" when it has one, as in
    /// "auth0|user_123".</summary>
    public static string? UserId(string jwt)
    {
        if (!IsJwt(jwt)) return null;
        try
        {
            var payload = jwt.Split('.')[1].TrimEnd('=').Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = AgentLogParser.Object(Convert.FromBase64String(payload));
            if (document?.RootElement.Str("sub") is not { Length: > 0 } sub) return null;
            var bar = sub.LastIndexOf('|');
            var id = bar >= 0 ? sub[(bar + 1)..] : sub;
            return id.Length > 0 ? id : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The cookie cursor.com's own pages send: <c>WorkosCursorSessionToken=&lt;user&gt;%3A%3A&lt;jwt&gt;</c>.</summary>
    public static string? Cookie(string jwt) =>
        UserId(jwt) is { } user ? "WorkosCursorSessionToken=" + user + "%3A%3A" + jwt : null;
}

/// <summary>What cursor.com's usage summary says, or that it could not be read.</summary>
/// <param name="Recognized">False when the reply did not hold a usage this reader understands: show "Cursor's usage
/// service changed" rather than numbers.</param>
public sealed record CursorUsageSummary(bool Recognized, string? Plan, DateTimeOffset? CycleStart, DateTimeOffset? CycleEnd,
                                        AgentLimitWindow? Window)
{
    /// <summary>The usage summary cursor.com's dashboard reads, with the session cookie.</summary>
    public static readonly Uri Endpoint = new("https://cursor.com/api/usage-summary");
    public const string WindowId = "cursor.included";
    /// <summary>The window's name in English; the app may translate it.</summary>
    public const string WindowName = "Included usage";

    public static readonly CursorUsageSummary Unrecognized = new(false, null, null, null, null);

    /// <summary>The allowance as the island keeps limits, read at <paramref name="observedAt"/>; null when unrecognized.</summary>
    public AgentLimits? Limits(DateTimeOffset observedAt) =>
        Recognized && Window is { } window ? new AgentLimits(AgentProvider.Cursor, [window], observedAt, AgentLimitSource.Account) : null;

    /// <summary>
    /// Reads a reply tolerantly: the plan from <c>membershipType</c> or <c>membership</c>, the billing cycle from
    /// <c>billingCycleStart</c>/<c>billingCycleEnd</c> (ISO text or epoch milliseconds), and the share used from
    /// <c>individualUsage.plan</c>, then <c>teamUsage</c>: <c>totalPercentUsed</c> or <c>percentUsed</c> (0–100), else
    /// <c>used</c> (or <c>limit</c> − <c>remaining</c>) over <c>limit</c>. Never throws.
    /// </summary>
    public static CursorUsageSummary Parse(string? json)
    {
        try
        {
            using var document = json is null ? null : AgentLogParser.Object(json);
            if (document is null) return Unrecognized;
            var root = document.RootElement;
            var plan = Text(root.Field("membershipType")) ?? Text(root.Field("membership"));
            var start = Date(root.Field("billingCycleStart"));
            var end = Date(root.Field("billingCycleEnd"));
            var candidates = new[]
            {
                root.Obj("individualUsage").Obj("plan"), root.Obj("teamUsage").Obj("plan"), root.Obj("teamUsage"),
                root.Obj("individualUsage"),
            };
            double? percent = null;
            foreach (var candidate in candidates)
            {
                if (candidate is { } usage && Percent(usage) is { } found) { percent = found; break; }
            }
            if (percent is not { } used) return Unrecognized;
            int? minutes = start is { } s && end is { } e && e > s ? (int)Math.Round(e.Since(s) / 60) : null;
            var window = new AgentLimitWindow(WindowId, AgentLimitKind.Other, minutes, null, Math.Min(100, Math.Max(0, used)), end);
            return new CursorUsageSummary(true, plan, start, end, window);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
                                              or OverflowException or ArgumentException)
        {
            return Unrecognized;
        }
    }

    private static double? Percent(JsonElement usage)
    {
        foreach (var key in new[] { "totalPercentUsed", "percentUsed" })
            if (Number(usage.Field(key)) is { } direct) return direct;
        var limit = Number(usage.Field("limit"));
        if (limit is not { } total || total <= 0) return null;
        if (Number(usage.Field("used")) is { } used) return used / total * 100;
        if (Number(usage.Field("remaining")) is { } remaining) return (total - remaining) / total * 100;
        return null;
    }

    /// <summary>A finite number, or numeric text.</summary>
    private static double? Number(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var d) && double.IsFinite(d) => d,
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                                 && double.IsFinite(d) => d,
        _ => null,
    };

    private static string? Text(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } s && s.GetString()?.Trim() is { Length: > 0 } text ? text : null;

    private static DateTimeOffset? Date(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } s && s.GetString() is { } text)
        {
            if (AgentTimestamp.Parse(text) is { } parsed) return parsed;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose))
                return loose;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)) return Epoch(numeric);
            return null;
        }
        return Number(value) is { } number ? Epoch(number) : null;
    }

    private static DateTimeOffset? Epoch(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1e14) return null;
        return value > 100_000_000_000 ? AgentTime.FromUnixMilliseconds(value) : AgentTime.FromUnixSeconds(value);
    }
}

/// <summary>
/// Electron's safeStorage on Windows, as Cursor (a Chromium app) seals its secrets: a 32-byte AES key kept in
/// <c>Local State</c> under <c>os_crypt.encrypted_key</c>, base64 of <c>"DPAPI"</c> and the key sealed with the
/// user's DPAPI, and each value as <c>"v10"</c>, a 12-byte nonce, the AES-256-GCM ciphertext and its 16-byte tag.
/// Unsealing the key with DPAPI is the app's job; what opens stays in memory.
/// </summary>
public static class ElectronSafeStorage
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] Version10 = "v10"u8.ToArray();
    public const int NonceSize = 12, TagSize = 16, KeySize = 32;

    /// <summary>The DPAPI-sealed key that <c>Local State</c>'s JSON holds, without its prefix; null when it holds none.</summary>
    public static byte[]? SealedKey(string? localStateJson)
    {
        using var document = localStateJson is null || localStateJson.Length > 4 << 20 ? null : AgentLogParser.Object(localStateJson);
        if (document?.RootElement.Obj("os_crypt").Str("encrypted_key") is not { Length: > 0 } encoded) return null;
        byte[] raw;
        try { raw = Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
        return raw.Length > DpapiPrefix.Length && raw.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix)
            ? raw[DpapiPrefix.Length..] : null;
    }

    /// <summary>The plaintext of a <c>v10</c> blob under <paramref name="key"/>; null when it is not one or fails to open.</summary>
    public static byte[]? Open(byte[] blob, byte[] key)
    {
        if (key.Length != KeySize || blob.Length < Version10.Length + NonceSize + TagSize
            || !blob.AsSpan(0, Version10.Length).SequenceEqual(Version10)) return null;
        var nonce = blob.AsSpan(Version10.Length, NonceSize);
        var cipher = blob.AsSpan(Version10.Length + NonceSize, blob.Length - Version10.Length - NonceSize - TagSize);
        var tag = blob.AsSpan(blob.Length - TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new System.Security.Cryptography.AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>Seals <paramref name="plain"/> the way Electron does; for tests and round trips.</summary>
    public static byte[] Seal(byte[] plain, byte[] key, byte[] nonce)
    {
        var blob = new byte[Version10.Length + NonceSize + plain.Length + TagSize];
        Version10.CopyTo(blob, 0);
        nonce.CopyTo(blob, Version10.Length);
        using var aes = new System.Security.Cryptography.AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(Version10.Length + NonceSize, plain.Length), blob.AsSpan(blob.Length - TagSize));
        return blob;
    }
}
