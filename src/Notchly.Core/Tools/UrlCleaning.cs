// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/URLCleaning.swift: take the tracking parameters out of a link. The rule lists are upstream's,
// word for word. Rules are kept as a difference from the built-in ones (extra names, extra per-site names, names switched off).
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly.Core.Tools;

public enum UrlCleanOutcome { NotAUrl, Removed, Unchanged, Rewritten }

/// <summary>A cleaned link and the parameter names that were taken out, in link order.</summary>
public sealed record UrlCleanResult(string Url, IReadOnlyList<string> Removed);

/// <summary>The user's changes to the built-in rules. A site rule or a switched-off name is a <c>host|name</c> pair; an empty host is every site.</summary>
public sealed record UrlCleanRules(IReadOnlyCollection<string> CustomNames, IReadOnlyCollection<(string Host, string Name)> SiteNames,
                                   IReadOnlyCollection<(string Host, string Name)> DisabledNames)
{
    public static UrlCleanRules Default { get; } = new([], [], []);

    public static UrlCleanRules FromStorage(string? custom, string? site, string? disabled) =>
        new(UrlCleaning.CustomNames(custom), UrlCleaning.Tokens(site), UrlCleaning.Tokens(disabled));
}

public static partial class UrlCleaning
{
    public const string PrefixWildcard = "utm_*";

    /// <summary>Names removed from every link, apart from anything starting with <c>utm_</c>.</summary>
    public static readonly IReadOnlyList<string> GlobalNames =
    [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "utm_id", "utm_name", "utm_reader", "utm_viz_id", "utm_pubreferrer",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "twclid", "ttclid", "li_fat_id",
        "mkt_tok", "_hsenc", "_hsmi", "__twitter_impression",
        "fb_action_ids", "fb_action_types", "fb_source", "mibextid",
    ];

    /// <summary>Names removed on one site and its subdomains.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SiteNames = new Dictionary<string, IReadOnlyList<string>>
    {
        ["youtube.com"] = ["si", "pp", "feature", "kw"],
        ["youtu.be"] = ["si", "pp", "feature", "kw"],
        ["twitter.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["x.com"] = ["s", "t", "cn", "src", "refsrc", "ref_src", "ref_url"],
        ["instagram.com"] = ["igsh"],
        ["spotify.com"] = ["si"],
        ["reddit.com"] = ["correlation_id", "ref_campaign", "ref_source", "rdt", "share_id", "_branch_match_id", "$deep_link", "$3p", "$original_url"],
        ["tiktok.com"] = ["u_code", "preview_pb", "_d", "_t", "_r", "timestamp", "user_id", "share_app_name", "share_iid"],
        ["bilibili.com"] =
        [
            "spm_id_from", "from_spmid", "from_source", "share_source", "share_from", "share_medium", "share_plat", "share_tag",
            "share_session_id", "msource", "refer_from", "seid", "unique_k", "vd_source", "plat_id", "buvid", "bbid", "up_id",
            "is_story_h5", "timestamp", "ts", "visit_id", "session_id", "broadcast_type", "is_room_feed",
        ],
        ["xiaohongshu.com"] =
        [
            "xhsshare", "author_share", "xsec_source", "share_from_user_hidden", "shareredid", "share_id", "exsource",
            "app_version", "app_platform", "apptime", "appuid",
        ],
    };

    /// <summary>The global names as Settings lists them: the <c>utm_*</c> wildcard first, then the others in order.</summary>
    public static IReadOnlyList<string> DisplayGlobalNames() =>
        [PrefixWildcard, .. GlobalNames.Where(n => !n.StartsWith("utm_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    // MARK: Storage

    private static readonly char[] ListSeparators = [',', '\n', '\r'];

    /// <summary>A name that can be stored: lower case, no spaces, and none of the separators.</summary>
    public static string? ParameterName(string? input)
    {
        var name = input?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(name) || name.Any(c => char.IsWhiteSpace(c) || c is '|' or ',' or '&' or '=') ? null : name;
    }

    /// <summary>The extra global names: split on commas and new lines, lower case, no repeats.</summary>
    public static IReadOnlyList<string> CustomNames(string? stored) =>
        (stored ?? "").Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries).Select(ParameterName).OfType<string>().Distinct().ToList();

    public static string CustomStorage(IEnumerable<string> names) => string.Join(",", names.Select(ParameterName).OfType<string>().Distinct());

    /// <summary><c>host|name</c> pairs; an empty host stands for every site. A bad pair is skipped.</summary>
    public static IReadOnlyList<(string Host, string Name)> Tokens(string? stored)
    {
        var list = new List<(string, string)>();
        foreach (var piece in (stored ?? "").Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = piece.Split('|');
            if (parts.Length != 2) continue;
            var host = parts[0].Trim().Length == 0 ? "" : SiteKey(parts[0]);
            var name = parts[1].Trim() == PrefixWildcard ? PrefixWildcard : ParameterName(parts[1]);
            if (host is null || name is null || list.Contains((host, name))) continue;
            list.Add((host, name));
        }
        return list;
    }

    public static string TokenStorage(IEnumerable<(string Host, string Name)> tokens) =>
        string.Join(",", tokens.Distinct().OrderBy(t => t.Host, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Host + "|" + t.Name));

    /// <summary>
    /// The host a site rule is about, from a bare host or a whole link (its scheme, login, port, path and a leading <c>www.</c> do not count);
    /// null when it is not a host: it needs a dot, no spaces, no separators, no dot at either end.
    /// </summary>
    public static string? SiteKey(string? input)
    {
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end < 0) return null;
            var rest = s[(end + 1)..];
            if (rest.Length > 0 && !(rest[0] == ':' && rest[1..].All(char.IsAsciiDigit))) return null;
            s = s[..(end + 1)];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!s[(colon + 1)..].All(char.IsAsciiDigit)) return null;
                s = s[..colon];
            }
        }
        s = s.ToLowerInvariant();
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        if (s.Length == 0 || !s.Contains('.') || s.StartsWith('.') || s.EndsWith('.')) return null;
        return s.Any(c => char.IsWhiteSpace(c) || c is '|' or ',') ? null : s;
    }

    private static bool HostMatches(string host, string key) =>
        host == key || host.EndsWith("." + key, StringComparison.Ordinal);

    // MARK: Cleaning

    private static bool IsTracking(string name, string host, UrlCleanRules rules)
    {
        bool switchedOff(string candidate) => rules.DisabledNames.Any(d => d.Name == candidate && (d.Host.Length == 0 || HostMatches(host, d.Host)));
        if (switchedOff(name)) return false;
        if (name.StartsWith("utm_", StringComparison.Ordinal) && !switchedOff(PrefixWildcard)) return true;
        // The utm_ names of the list are covered by the wildcard, so switching the wildcard off keeps them.
        if (GlobalNames.Contains(name) && !name.StartsWith("utm_", StringComparison.Ordinal) || rules.CustomNames.Contains(name)) return true;
        if (SiteNames.Any(p => HostMatches(host, p.Key) && p.Value.Contains(name))) return true;
        return rules.SiteNames.Any(s => s.Name == name && s.Host.Length > 0 && HostMatches(host, s.Host));
    }

    /// <summary>
    /// The link without its tracking parameters, or null when the text is not a single web link. The query is cut from the text itself
    /// (names are compared decoded and in lower case), the fragment stays, an emptied query loses its <c>?</c>, and a link with nothing to
    /// remove comes back exactly as given (trimmed).
    /// </summary>
    public static UrlCleanResult? Clean(string? text, UrlCleanRules? rules = null)
    {
        rules ??= UrlCleanRules.Default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.Host.ToLowerInvariant();

        var hash = trimmed.IndexOf('#');
        var question = trimmed.IndexOf('?');
        if (question < 0 || hash >= 0 && hash < question) return new UrlCleanResult(trimmed, []);
        var end = hash >= 0 ? hash : trimmed.Length;
        var head = trimmed[..question];
        var tail = trimmed[end..];
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var pair in trimmed[(question + 1)..end].Split('&'))
        {
            var rawName = pair.Contains('=') ? pair[..pair.IndexOf('=')] : pair;
            var name = rawName.Contains('%') ? SafeUnescape(rawName) : rawName;
            name = name.ToLowerInvariant();
            if (name.Length > 0 && IsTracking(name, host, rules))
            {
                if (!removed.Contains(name)) removed.Add(name);
            }
            else kept.Add(pair);
        }
        if (removed.Count == 0) return new UrlCleanResult(trimmed, []);
        var query = kept.Count > 0 ? "?" + string.Join("&", kept) : "";
        return new UrlCleanResult(head + query + tail, removed);
    }

    private static string SafeUnescape(string text)
    {
        try { return Uri.UnescapeDataString(text); }
        catch (UriFormatException) { return text; }
    }

    public static UrlCleanOutcome Outcome(string? text, UrlCleanRules? rules = null)
    {
        if (Clean(text, rules) is not { } result) return UrlCleanOutcome.NotAUrl;
        if (result.Removed.Count > 0) return UrlCleanOutcome.Removed;
        return result.Url == text?.Trim() ? UrlCleanOutcome.Unchanged : UrlCleanOutcome.Rewritten;
    }

    // MARK: The HTML flavour

    public const int MaxHtmlBytes = 64 * 1024;

    [GeneratedRegex(@"<\s*(?:img|video|audio|picture|svg|iframe|object|embed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MediaTagRegex();

    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"href\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    private static string VisibleText(string html) => System.Net.WebUtility.HtmlDecode(TagRegex().Replace(html, "")).Trim();

    /// <summary>
    /// Whether the HTML that came with a copied link may be dropped when the link is rewritten: small, with no pictures or media, every
    /// link in it pointing at the same address, and either no empty-looking link or text that is only the address.
    /// </summary>
    public static bool HtmlAllowsRewrite(string? html, string link)
    {
        if (string.IsNullOrEmpty(html)) return true;
        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes || MediaTagRegex().IsMatch(html)) return false;
        var anchors = AnchorRegex().Matches(html);
        var emptyAnchor = false;
        foreach (Match anchor in anchors)
        {
            var href = HrefRegex().Match(anchor.Groups[1].Value);
            var target = href.Success ? System.Net.WebUtility.HtmlDecode(href.Groups[1].Success ? href.Groups[1].Value : href.Groups[2].Value) : null;
            if (target is null || target.Trim() != link) return false;
            if (VisibleText(anchor.Groups[2].Value).Length == 0) emptyAnchor = true;
        }
        return !emptyAnchor || VisibleText(html) == link;
    }
}
