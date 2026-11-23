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
