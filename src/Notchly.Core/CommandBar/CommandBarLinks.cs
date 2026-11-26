// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarLinks.swift (Copyright (C) 2026 Vorssaint).

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notchly.Core.CommandBar;

/// <summary>What a saved link opens. There are no scripts on Windows: upstream's script kind is
/// gone, and a stored script entry decodes to nothing.</summary>
public enum CommandBarLinkKind
{
    /// <summary>Anything with a scheme (a site, or a link another app answers to), written as a
    /// URL template that may carry placeholders.</summary>
    Link,

    /// <summary>A folder, a file or an app on this PC, written as a path.</summary>
    Place,
}

/// <summary>
/// A place the person goes to often, saved by them and answering to a name of their choosing:
/// a site, a folder, a file, an app, or a search that takes what they type after the name.
/// </summary>
public sealed record CommandBarLink
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public CommandBarLinkKind Kind { get; init; } = CommandBarLinkKind.Link;

    /// <summary>The destination, with placeholders still in it.</summary>
    public string Destination { get; init; } = "";

    /// <summary>Whether the bare name is enough to open a query link, with an empty query. Kept
    /// from upstream (where it governed scripts) so a saved list round-trips; it only has an
    /// effect on a link whose destination takes a query.</summary>
    public bool RunsWithoutArgument { get; init; }

    /// <summary>Whether the link answers to its own global shortcut all by itself: it opens at
    /// once with nothing on screen, instead of opening the bar.</summary>
    public bool RunsDirectly { get; init; }

    /// <summary>True when the destination waits for whatever is typed after the name, which is
    /// what turns a link into a search.</summary>
    public bool TakesQuery => Destination.Contains(CommandBarLinkPlaceholders.Token(CommandBarLinkPlaceholder.Query),
        StringComparison.Ordinal);

    /// <summary>True for anything that reads whatever follows its name. With no scripts that is
    /// exactly the query links.</summary>
    public bool TakesArgument => TakesQuery;
}

/// <summary>The words a destination can carry that are filled in when it opens. They are written
/// the same way in every language: a stored destination has to keep working when the person
/// changes the interface language.</summary>
public enum CommandBarLinkPlaceholder
{
    Query,
    Clipboard,
    Selection,
    Date,
}

public static class CommandBarLinkPlaceholders
{
    public static IReadOnlyList<CommandBarLinkPlaceholder> All { get; } =
        [CommandBarLinkPlaceholder.Query, CommandBarLinkPlaceholder.Clipboard,
         CommandBarLinkPlaceholder.Selection, CommandBarLinkPlaceholder.Date];

    /// <summary>The raw name, as written between the braces.</summary>
    public static string Name(CommandBarLinkPlaceholder placeholder) => placeholder switch
    {
        CommandBarLinkPlaceholder.Query => "query",
        CommandBarLinkPlaceholder.Clipboard => "clipboard",
        CommandBarLinkPlaceholder.Selection => "selection",
        _ => "date",
    };

    /// <summary>The token as it appears in a destination, e.g. <c>{query}</c>.</summary>
    public static string Token(CommandBarLinkPlaceholder placeholder) => "{" + Name(placeholder) + "}";
}

/// <summary>Reading, writing and filling in what the person saved. Pure, so every rule here is
/// pinned by tests.</summary>
public static class CommandBarLinks
{
    /// <summary>Enough for a person, few enough that the list stays a list.</summary>
    public const int Limit = 60;

    // MARK: - Storage

    /// <summary>
    /// Reads the JSON stored in the <c>commandBarLinks</c> setting. Lenient on purpose: a missing
    /// field takes its default, a malformed entry is dropped without taking the others down,
    /// unreadable text is an empty list, a script entry (upstream's third kind) and any unknown
    /// kind decode to nothing, names and destinations are sanitised, a half-written link (no name
    /// or no destination) never survives, and at most <see cref="Limit"/> links are kept.
    /// </summary>
    public static List<CommandBarLink> Decode(string? json)
    {
        var result = new List<CommandBarLink>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            var seen = new HashSet<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (result.Count >= Limit) break;
                var link = DecodeOne(element);
                if (link is null) continue;
                if (!seen.Add(link.Id)) link = link with { Id = NewUnusedId(seen) };
                result.Add(link);
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    private static Guid NewUnusedId(HashSet<Guid> seen)
    {
        var id = Guid.NewGuid();
        while (!seen.Add(id)) id = Guid.NewGuid();
        return id;
    }

    private static CommandBarLink? DecodeOne(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var kind = CommandBarLinkKind.Link;
        var id = Guid.NewGuid();
        var name = "";
        var destination = "";
        var runsWithoutArgument = false;
        var runsDirectly = false;
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "id":
                    if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed))
                        id = parsed;
                    break;
                case "name":
                    if (value.ValueKind == JsonValueKind.String) name = value.GetString() ?? "";
                    break;
                case "destination":
                    if (value.ValueKind == JsonValueKind.String) destination = value.GetString() ?? "";
                    break;
                case "kind":
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var text = value.GetString() ?? "";
                        if (text.Equals("link", StringComparison.OrdinalIgnoreCase)) kind = CommandBarLinkKind.Link;
                        else if (text.Equals("place", StringComparison.OrdinalIgnoreCase)) kind = CommandBarLinkKind.Place;
                        else return null; // "script", or a kind a later version knows about
                    }
                    break;
                case "runsWithoutArgument":
                    runsWithoutArgument = value.ValueKind == JsonValueKind.True;
                    break;
                case "runsDirectly":
                    runsDirectly = value.ValueKind == JsonValueKind.True;
                    break;
            }
        }
        name = SanitizedName(name);
        destination = SanitizedDestination(destination);
        if (name.Length == 0 || destination.Length == 0) return null;
        return new CommandBarLink
        {
            Id = id,
            Name = name,
            Kind = kind,
            Destination = destination,
            RunsWithoutArgument = runsWithoutArgument,
            RunsDirectly = runsDirectly,
        };
    }

    /// <summary>The JSON to store: the first <see cref="Limit"/> links, each sanitised, the ones
    /// with no name or no destination left out. Field names and the upper-case id match the
    /// macOS app's, so a settings export reads the same.</summary>
    public static string Encode(IEnumerable<CommandBarLink> links)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartArray();
            var written = 0;
            foreach (var raw in links)
            {
                if (written >= Limit) break;
                var link = Sanitized(raw);
                if (link.Name.Length == 0 || link.Destination.Length == 0) continue;
                writer.WriteStartObject();
                writer.WriteString("id", link.Id.ToString("D").ToUpperInvariant());
                writer.WriteString("name", link.Name);
                writer.WriteString("kind", link.Kind == CommandBarLinkKind.Place ? "place" : "link");
                writer.WriteString("destination", link.Destination);
                writer.WriteBoolean("runsWithoutArgument", link.RunsWithoutArgument);
                writer.WriteBoolean("runsDirectly", link.RunsDirectly);
                writer.WriteEndObject();
                written++;
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The link with a clean name and destination.</summary>
    public static CommandBarLink Sanitized(CommandBarLink link) =>
        link with { Name = SanitizedName(link.Name), Destination = SanitizedDestination(link.Destination) };

    /// <summary>A name as it is matched against what is typed: control and invisible characters
    /// dropped, every run of whitespace one space, no wrapping whitespace.</summary>
    public static string SanitizedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsWhiteSpace(character)) builder.Append(' ');
            else if (!IsInvisibleOrControl(character)) builder.Append(character);
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A destination with control characters (a pasted line break, say) dropped and no
    /// wrapping whitespace. Inner spaces are kept: a path may contain them.</summary>
    public static string SanitizedDestination(string? destination)
    {
        if (string.IsNullOrEmpty(destination)) return "";
        var builder = new StringBuilder(destination.Length);
        foreach (var character in destination)
        {
            if (!IsInvisibleOrControl(character)) builder.Append(character);
        }
        return builder.ToString().Trim();
    }

    private static bool IsInvisibleOrControl(char character) =>
        char.IsControl(character)
        || (character >= '­' && char.GetUnicodeCategory(character) == UnicodeCategory.Format);

    // MARK: - Placeholders

    private static readonly Regex PlaceholderPattern = new(
        @"\{(query|clipboard|selection|date)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The destination with every placeholder filled in. A value going into a web address is
    /// percent-encoded; one going into a path is not, because a path is not a URL and escaping
    /// it would break the folder name. Filled in a single pass, so a value that itself contains
    /// <c>{date}</c> is never expanded a second time (upstream replaced one placeholder after
    /// another).
    /// </summary>
    public static string Expand(string destination, CommandBarLinkKind kind,
        string query = "", string clipboard = "", string selection = "", string date = "")
    {
        return PlaceholderPattern.Replace(destination, match =>
        {
            var raw = match.Groups[1].Value switch
            {
                "query" => query,
                "clipboard" => clipboard,
                "selection" => selection,
                _ => date,
            };
            return kind == CommandBarLinkKind.Link ? Escaped(raw) : raw;
        });
    }

    /// <summary>What a value has to look like inside a web address: everything that is not an
    /// unreserved ASCII character is escaped (as UTF-8 bytes), including the "+" and "&amp;" that
    /// would otherwise change the meaning of a search.</summary>
    public static string Escaped(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    // MARK: - Matching a typed query

    /// <summary>Case, accent and width differences never matter; invisible format characters are
    /// dropped and any run of whitespace (including the full-width space some input methods
    /// produce) becomes one space. Folded without a culture, so Turkish rules never apply.
    /// Stands in for upstream's <c>CommandBarSearch.normalized</c>.</summary>
    internal static string Fold(string value)
    {
        var stripped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character >= '­' && char.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;
            stripped.Append(character);
        }
        var decomposed = stripped.ToString().Normalize(NormalizationForm.FormKD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.ToLowerInvariant(character));
        }
        return string.Join(' ', folded.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// What the person typed after the name of a link, which is what a search link opens with.
    /// Null when the query does not name this link at all. The name is matched whole and
    /// case-insensitively, so "gh vorssaint" finds the link called "gh" and "ghost" does not.
    /// </summary>
    public static string? TrailingArgument(string query, string name)
    {
        var normalizedName = Fold(name);
        if (normalizedName.Length == 0) return null;
        var normalizedQuery = Fold(query);
        if (!normalizedQuery.StartsWith(normalizedName + " ", StringComparison.Ordinal)) return null;
        // The remainder comes from the ORIGINAL text: what someone searches for keeps its
        // capitals and its accents. Splitting on any whitespace keeps a full-width space in step
        // with the width-insensitive prefix check above.
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var nameWords = normalizedName.Split(' ').Length;
        if (words.Length <= nameWords) return null;
        return string.Join(' ', words.Skip(nameWords));
    }

    /// <summary>What a link's row is scored against. Its own name works until the person types
    /// something after it; from there the row is scored against everything typed, or the extra
    /// words sink it out of the list.</summary>
    public static string RankingTitle(string name, string query) =>
        TrailingArgument(query, name) is not null ? query : name;

    /// <summary>What a query link would open with for this query, or null when the query does not
    /// name it. Ordinarily that is whatever follows the name; a link marked
    /// <see cref="CommandBarLink.RunsWithoutArgument"/> also answers to its bare name, with an
    /// empty argument. The bare name has to match the whole query, so the link never fires off a
    /// prefix of its name.</summary>
    public static string? ArgumentFor(CommandBarLink link, string query)
    {
        if (!link.TakesQuery) return null;
        var trailing = TrailingArgument(query, link.Name);
        if (trailing is not null) return trailing;
        if (!link.RunsWithoutArgument) return null;
        var normalizedName = Fold(link.Name);
        if (normalizedName.Length == 0 || Fold(query) != normalizedName) return null;
        return "";
    }

    /// <summary>Every query link the query names. An answer row stands in for all of them, so
    /// these are the rows the list drops once it has one.</summary>
    public static List<CommandBarLink> MatchingLinks(IEnumerable<CommandBarLink> links, string query) =>
        links.Where(link => ArgumentFor(link, query) is not null).ToList();

    /// <summary>The most specific query link the query names, with what it would open with, or
    /// null when nothing matches. A longer name wins over one that is only its prefix.</summary>
    public static (CommandBarLink Link, string Argument)? MatchingLink(IEnumerable<CommandBarLink> links, string query)
    {
        (CommandBarLink Link, string Argument)? best = null;
        var bestLength = -1;
        foreach (var link in MatchingLinks(links, query))
        {
            var argument = ArgumentFor(link, query);
            if (argument is null) continue;
            var length = Fold(link.Name).Length;
            if (length <= bestLength) continue;
            best = (link, argument);
            bestLength = length;
        }
        return best;
    }

    /// <summary>The link a global shortcut should open with nothing on screen, if the row it is
    /// bound to is one. A saved link's row id is the links source's prefix plus the link's GUID;
    /// anything else (another kind of row, a stale id, a link not marked for it) answers null,
    /// and the shortcut falls back to opening the bar.</summary>
    public static CommandBarLink? DirectRunLink(string stableKey, IEnumerable<CommandBarLink> links,
        string idPrefix = "link.")
    {
        if (!stableKey.StartsWith(idPrefix, StringComparison.Ordinal)) return null;
        if (!Guid.TryParse(stableKey.AsSpan(idPrefix.Length), out var id)) return null;
        var link = links.FirstOrDefault(candidate => candidate.Id == id);
        return link is { RunsDirectly: true } ? link : null;
    }

    // MARK: - Opening

    /// <summary>Where a saved place sits on the disk, for rows that can be shown in Explorer.
    /// Only a destination that is already a finished path qualifies: one still holding a
    /// placeholder is a different place every time it runs. Environment variables such as
    /// <c>%USERPROFILE%</c> are expanded; there is no <c>~</c>.</summary>
    public static string? RevealPath(CommandBarLink link, Func<string, string?>? environment = null)
    {
        if (link.Kind != CommandBarLinkKind.Place) return null;
        var trimmed = link.Destination.Trim();
        if (trimmed.Length == 0
            || CommandBarLinkPlaceholders.All.Any(p => trimmed.Contains(CommandBarLinkPlaceholders.Token(p), StringComparison.Ordinal)))
            return null;
        return CommandBarFileSearchSupport.ExpandEnvironment(trimmed, environment);
    }

    private static readonly Regex SchemePattern = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.Compiled);

    // "example.com:8080/path" starts like a scheme but is a host and a port.
    private static readonly Regex HostAndPort = new(
        @"^[^/\s:?#@]+\.[A-Za-z]{2,}:\d{1,5}(?:[/?#]|$)", RegexOptions.Compiled);

    /// <summary>The URL a link opens, or null when what was saved cannot be opened. A link
    /// destination without a scheme is treated as a site, which is what people mean when they
    /// paste one in; a place becomes a <c>file:</c> URL when it is a fully qualified Windows path
    /// (see <see cref="PlacePath"/> for the plain path to hand to the shell instead).</summary>
    public static Uri? Url(CommandBarLink link, string expanded, Func<string, string?>? environment = null)
    {
        var trimmed = expanded.Trim();
        if (trimmed.Length == 0) return null;
        if (link.Kind == CommandBarLinkKind.Place)
        {
            var path = CommandBarFileSearchSupport.ExpandEnvironment(trimmed, environment);
            return IsFullyQualified(path) && Uri.TryCreate(path, UriKind.Absolute, out var file) ? file : null;
        }
        if (SchemePattern.IsMatch(trimmed) && !HostAndPort.IsMatch(trimmed)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var explicitUrl))
            return explicitUrl;
        return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var site) ? site : null;
    }

    /// <summary>The path a place opens, with environment variables expanded, or null when the
    /// destination is empty or not a fully qualified Windows path (drive-letter or UNC).</summary>
    public static string? PlacePath(CommandBarLink link, string expanded, Func<string, string?>? environment = null)
    {
        if (link.Kind != CommandBarLinkKind.Place) return null;
        var trimmed = expanded.Trim();
        if (trimmed.Length == 0) return null;
        var path = CommandBarFileSearchSupport.ExpandEnvironment(trimmed, environment);
        return IsFullyQualified(path) ? path : null;
    }

    private static bool IsFullyQualified(string path) =>
        Regex.IsMatch(path, @"^(?:[A-Za-z]:[\\/]|\\\\|//)");

    // MARK: - A web address typed on its own

    /// <summary>The scheme-and-host shape of a bare address: host, optional port, optional rest.</summary>
    private static readonly Regex BareAddress = new(
        @"^(?<host>[^\s/:?#@\\]+)(?::(?<port>\d{1,5}))?(?<rest>[/?#]\S*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitAddress = new(
        @"^https?://\S+$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The final labels of well-known file types. A bare "report.pdf" or "photo.png" is a
    /// file name, not a site, even where the extension happens to be a real top-level domain
    /// (.zip, .mov, .md, .py ...). Replaces the macOS data detector's judgement; an explicit
    /// http(s) scheme is always honoured.</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "rtf", "doc", "docx", "dot", "odt", "pdf", "xls", "xlsx", "xlsm",
        "csv", "tsv", "ppt", "pptx", "odp", "ods", "png", "jpg", "jpeg", "gif", "bmp", "svg", "webp", "ico",
        "heic", "tif", "tiff", "psd", "mp3", "wav", "flac", "aac", "ogg", "m4a", "mp4", "mkv", "avi", "mov",
        "wmv", "webm", "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "iso", "dmg", "exe", "msi",
        "dll", "sys", "bat", "cmd", "ps1", "sh", "log", "ini", "cfg", "conf", "json", "yaml", "yml", "xml",
        "toml", "lock", "bak", "tmp", "cs", "csproj", "sln", "vb", "fs", "c", "h", "cpp", "hpp", "java",
        "kt", "swift", "py", "js", "jsx", "ts", "tsx", "css", "scss", "php", "rb", "go", "rs", "lua",
        "sql", "db", "sqlite", "apk", "jar", "lnk", "url", "torrent", "ttf", "otf", "woff", "pem", "crt",
        "key", "bin", "dat", "obj", "pdb",
    };

    /// <summary>
    /// A web address typed on its own, normalised to the string to open (an https scheme is added
    /// to a bare domain; the text is otherwise kept exactly, so "example.com" gives
    /// "https://example.com" with no added slash), or null when the text should remain a search.
    /// Only http and https are accepted.
    ///
    /// Upstream leans on Apple's data detector; this is a plain equivalent. Bare text counts only
    /// when it is a host with at least two labels, a final label that is letters (or an
    /// <c>xn--</c> label) and not a well-known file extension, an optional port and an optional
    /// path. Bare IPv4 addresses, single-label hosts ("localhost"), anything with whitespace or an
    /// "@" stay searches; with an explicit http(s) scheme any non-empty host is accepted.
    /// </summary>
    public static string? TypedUrl(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return null;

        if (ExplicitAddress.IsMatch(trimmed))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var explicitUrl)
                && (explicitUrl.Scheme == Uri.UriSchemeHttp || explicitUrl.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(explicitUrl.Host)
                ? trimmed
                : null;
        }

        var match = BareAddress.Match(trimmed);
        if (!match.Success) return null;
        var host = match.Groups["host"].Value;
        if (!IsPlausibleHost(host)) return null;
        var result = "https://" + trimmed;
        return Uri.TryCreate(result, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host)
            ? result
            : null;
    }

    private static bool IsPlausibleHost(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        var top = labels[^1];
        if (top.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) return top.Length > 4;
        if (top.Length < 2 || !top.All(char.IsLetter)) return false;
        return !FileExtensions.Contains(top);
    }
}
