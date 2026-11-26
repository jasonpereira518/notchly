// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the saved-links and "open what was typed as a URL" contracts in upstream
// Tests/CommandBarFeatureTests.swift that apply on Windows (the script kind is gone).
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarLinksTests
{
    private static CommandBarLink Link(string name, string destination,
        CommandBarLinkKind kind = CommandBarLinkKind.Link, bool bare = false, bool direct = false) =>
        new() { Name = name, Destination = destination, Kind = kind, RunsWithoutArgument = bare, RunsDirectly = direct };

    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.GetValueOrDefault(name);
    }

    // MARK: - Placeholders

    [Fact]
    public void WhatGoesIntoAWebAddressIsEscaped() =>
        Assert.Equal("https://x.com/search?q=caf%C3%A9%20com%20leite",
            CommandBarLinks.Expand("https://x.com/search?q={query}", CommandBarLinkKind.Link, query: "café com leite"));

    [Fact]
    public void APathIsNotAUrlAndIsNeverEscaped() =>
        Assert.Equal(@"C:\Projects\my folder",
            CommandBarLinks.Expand(@"C:\Projects\{query}", CommandBarLinkKind.Place, query: "my folder"));

    [Fact]
    public void APlusSignInsideASearchIsEscapedNotReadAsASpace() =>
        Assert.Equal("https://x.com/a%2Bb",
            CommandBarLinks.Expand("https://x.com/{clipboard}", CommandBarLinkKind.Link, clipboard: "a+b"));

    [Fact]
    public void EveryPlaceholderIsFilledIn()
    {
        Assert.Equal("a=1&b=two&c=3&d=2026-11-26",
            CommandBarLinks.Expand("a={query}&b={clipboard}&c={selection}&d={date}", CommandBarLinkKind.Link,
                query: "1", clipboard: "two", selection: "3", date: "2026-11-26"));
        Assert.Equal(@"D:\Notes\2026-11-26.md",
            CommandBarLinks.Expand(@"D:\Notes\{date}.md", CommandBarLinkKind.Place, date: "2026-11-26"));
    }

    [Fact]
    public void APlaceholderLeftEmptyBecomesNothingAndUnknownBracesStay()
    {
        Assert.Equal("https://x/?q=", CommandBarLinks.Expand("https://x/?q={query}", CommandBarLinkKind.Link));
        Assert.Equal("https://x/{other}", CommandBarLinks.Expand("https://x/{other}", CommandBarLinkKind.Link));
    }

    [Fact]
    public void AValueIsNeverExpandedASecondTime() =>
        // Upstream replaced one placeholder after another, so a place query holding "{date}" was
        // expanded again; here it is filled in a single pass.
        Assert.Equal(@"C:\x\{date}-2026", CommandBarLinks.Expand(@"C:\x\{query}-{date}", CommandBarLinkKind.Place,
            query: "{date}", date: "2026"));

    [Fact]
    public void EscapedKeepsOnlyUnreservedCharacters()
    {
        Assert.Equal("Az09-._~", CommandBarLinks.Escaped("Az09-._~"));
        Assert.Equal("a%26b%3Dc%2Fd%3F%23%20%25", CommandBarLinks.Escaped("a&b=c/d?# %"));
        Assert.Equal("%E4%BE%8B%E5%AD%90", CommandBarLinks.Escaped("例子"));
        Assert.Equal("%F0%9F%98%80", CommandBarLinks.Escaped("\U0001F600"));
    }

    [Fact]
    public void PlaceholderTokensAreWrittenTheSameInEveryLanguage()
    {
        Assert.Equal(["{query}", "{clipboard}", "{selection}", "{date}"],
            CommandBarLinkPlaceholders.All.Select(CommandBarLinkPlaceholders.Token));
    }

    // MARK: - Matching what is typed to a link

    [Fact]
    public void WhatComesAfterTheNameIsWhatTheSavedSearchOpensWith() =>
        Assert.Equal("vorssaint utils", CommandBarLinks.TrailingArgument("gh vorssaint utils", "gh"));

    [Fact]
    public void TheNameIsMatchedWithoutCaseButTheArgumentKeepsItsOwn() =>
        Assert.Equal("Vorssaint", CommandBarLinks.TrailingArgument("GH Vorssaint", "gh"));

    [Fact]
    public void ALongerWordIsNotTheNameAndTheNameAloneIsNotAnArgument()
    {
        Assert.Null(CommandBarLinks.TrailingArgument("ghost writer", "gh"));
        Assert.Null(CommandBarLinks.TrailingArgument("gh", "gh"));
        Assert.Null(CommandBarLinks.TrailingArgument("gh   ", "gh"));
        Assert.Null(CommandBarLinks.TrailingArgument("gh x", ""));
    }

    [Fact]
    public void AFullWidthSpaceSeparatesTheNameFromItsArgument() =>
        Assert.Equal("123", CommandBarLinks.TrailingArgument("bd\u3000123", "bd"));

    [Fact]
    public void AccentsAndInvisibleCharactersDoNotStopTheNameFromMatching()
    {
        Assert.Equal("Rio", CommandBarLinks.TrailingArgument("café Rio", "cafe"));
        Assert.Equal("x", CommandBarLinks.TrailingArgument("\u200Egh x", "gh"));
    }

    [Fact]
    public void ANameMadeOfSeveralWordsKeepsTheRestAsTheArgument() =>
        Assert.Equal("C# generics", CommandBarLinks.TrailingArgument("my docs  C# generics", "my docs"));

    [Fact]
    public void OnceAnArgumentFollowsTheNameTheRowIsScoredAgainstTheWholeQuery()
    {
        Assert.Equal("gh vorssaint utils", CommandBarLinks.RankingTitle("gh", "gh vorssaint utils"));
        Assert.Equal("gh", CommandBarLinks.RankingTitle("gh", "gh"));
        Assert.Equal("gh", CommandBarLinks.RankingTitle("gh", "ghost writer"));
    }

    [Fact]
    public void ADestinationThatWaitsForAQueryIsASearch()
    {
        Assert.True(Link("gh", "https://x/{query}").TakesQuery);
        Assert.False(Link("a", "https://x").TakesQuery);
        Assert.True(Link("gh", "https://x/{query}").TakesArgument);
        Assert.False(Link("a", @"C:\tmp", CommandBarLinkKind.Place).TakesArgument);
    }

    [Fact]
    public void TheMostSpecificSearchNameWins()
    {
        var links = new[]
        {
            Link("run", "https://short/{query}"),
            Link("run report", "https://specific/{query}"),
            Link("plain", "https://nothing"),
        };
        var match = CommandBarLinks.MatchingLink(links, "run report today");
        Assert.Equal("run report", match?.Link.Name);
        Assert.Equal("today", match?.Argument);
        Assert.Equal(["run", "run report"], CommandBarLinks.MatchingLinks(links, "run report x").Select(l => l.Name).Order());
    }

    [Fact]
    public void ANonQueryLinkNeverMatchesEvenWithAnArgument()
    {
        var links = new[] { Link("a", "https://x"), Link("cur", "https://x/{query}") };
        Assert.Null(CommandBarLinks.MatchingLink(links, "a 100 usd eur"));
        Assert.Empty(CommandBarLinks.MatchingLinks(links, "a 1"));
        Assert.Null(CommandBarLinks.MatchingLink(links, "cur"));
        Assert.Equal("100 usd eur", CommandBarLinks.MatchingLink(links, "cur 100 usd eur")?.Argument);
    }

    [Fact]
    public void ABareNameOpensASearchOnlyWhenTheLinkIsMarkedForIt()
    {
        var marked = Link("gh", "https://x/{query}", bare: true);
        var unmarked = Link("gh", "https://x/{query}");
        Assert.Equal("", CommandBarLinks.ArgumentFor(marked, "gh"));
        Assert.Equal("", CommandBarLinks.ArgumentFor(marked, "  GH  "));
        Assert.Equal("code", CommandBarLinks.ArgumentFor(marked, "gh code"));
        Assert.Null(CommandBarLinks.ArgumentFor(marked, "g"));
        Assert.Null(CommandBarLinks.ArgumentFor(marked, "ghost"));
        Assert.Null(CommandBarLinks.ArgumentFor(unmarked, "gh"));
    }

    // MARK: - Direct run

    [Fact]
    public void ALinkStaysABarRowUnlessMarkedToRunDirectly()
    {
        Assert.False(Link("h", "https://h").RunsDirectly);
        var direct = Link("h", "https://h", direct: true);
        var plain = Link("p", "https://p");
        Assert.Equal(direct, CommandBarLinks.DirectRunLink("link." + direct.Id.ToString().ToUpperInvariant(), [direct, plain]));
        Assert.Null(CommandBarLinks.DirectRunLink("link." + plain.Id, [direct, plain]));
    }

    [Fact]
    public void AKeyThatIsNotASavedLinksRowAnswersNull()
    {
        var direct = Link("h", "https://h", direct: true);
        Assert.Null(CommandBarLinks.DirectRunLink("kill.browse", [direct]));
        Assert.Null(CommandBarLinks.DirectRunLink("link.00000000-0000-0000-0000-000000000000", [direct]));
        Assert.Null(CommandBarLinks.DirectRunLink("link.not-a-guid", [direct]));
        Assert.Null(CommandBarLinks.DirectRunLink("link.", [direct]));
    }

    // MARK: - Opening

    [Fact]
    public void ADestinationPastedWithoutASchemeIsStillASite()
    {
        Assert.Equal("https", CommandBarLinks.Url(Link("a", "x.com"), "x.com")?.Scheme);
        Assert.Equal("https://x.com/a", CommandBarLinks.Url(Link("a", "x.com/a"), "x.com/a")?.AbsoluteUri);
    }

    [Fact]
    public void ALinkWithASchemeKeepsIt()
    {
        Assert.Equal("ms-settings", CommandBarLinks.Url(Link("s", "ms-settings:display"), "ms-settings:display")?.Scheme);
        Assert.Equal("mailto", CommandBarLinks.Url(Link("m", "mailto:a@b.co"), "mailto:a@b.co")?.Scheme);
        Assert.Equal("http", CommandBarLinks.Url(Link("w", "http://x.test"), "http://x.test")?.Scheme);
    }

    [Fact]
    public void AHostAndPortIsNotMistakenForAScheme()
    {
        var uri = CommandBarLinks.Url(Link("d", "example.com:8080/path"), "example.com:8080/path");
        Assert.Equal("https", uri?.Scheme);
        Assert.Equal(8080, uri?.Port);
    }

    [Fact]
    public void AnEmptyDestinationOpensNothing()
    {
        Assert.Null(CommandBarLinks.Url(Link("a", " "), "   "));
        Assert.Null(CommandBarLinks.Url(Link("a", "x", CommandBarLinkKind.Place), ""));
    }

    [Fact]
    public void APlaceOpensAsAFileUrlAndAPlainPath()
    {
        var place = Link("notes", @"C:\Users\x\Notes", CommandBarLinkKind.Place);
        var uri = CommandBarLinks.Url(place, place.Destination);
        Assert.True(uri?.IsFile);
        Assert.Equal(@"C:\Users\x\Notes", uri?.LocalPath);
        Assert.Equal(@"C:\Users\x\Notes", CommandBarLinks.PlacePath(place, place.Destination));
        Assert.Null(CommandBarLinks.Url(Link("rel", @"Notes\a", CommandBarLinkKind.Place), @"Notes\a"));
        Assert.Null(CommandBarLinks.PlacePath(Link("rel", @"Notes\a", CommandBarLinkKind.Place), @"Notes\a"));
        Assert.Null(CommandBarLinks.PlacePath(Link("site", "https://x"), "https://x"));
    }

    [Fact]
    public void AnEnvironmentVariableInAPlaceIsExpanded()
    {
        var env = Env(("USERPROFILE", @"C:\Users\x"));
        var place = Link("notes", @"%USERPROFILE%\Notes", CommandBarLinkKind.Place);
        Assert.Equal(@"C:\Users\x\Notes", CommandBarLinks.PlacePath(place, place.Destination, env));
        Assert.Equal(@"C:\Users\x\Notes", CommandBarLinks.RevealPath(place, env));
    }

    [Fact]
    public void ASavedFolderCanBeShownWhereItLives()
    {
        Assert.Equal(@"D:\Notes", CommandBarLinks.RevealPath(Link("notes", @"D:\Notes", CommandBarLinkKind.Place)));
        Assert.Null(CommandBarLinks.RevealPath(Link("site", "https://example.invalid")));
        Assert.Null(CommandBarLinks.RevealPath(Link("day", @"D:\Notes\{date}.md", CommandBarLinkKind.Place)));
        Assert.Null(CommandBarLinks.RevealPath(Link("empty", "  ", CommandBarLinkKind.Place)));
    }

    // MARK: - Storage

    [Fact]
    public void AHalfWrittenShortcutNeverSurvivesARoundTrip()
    {
        var back = CommandBarLinks.Decode(CommandBarLinks.Encode([Link("", "x"), Link("ok", "x"), Link("no", "  ")]));
        Assert.Single(back);
        Assert.Equal("ok", back[0].Name);
    }

    [Fact]
    public void AShortcutSavedBeforeAFieldExistedStillLoads()
    {
        const string old = """[{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"gh","kind":"link","destination":"https://x"}]""";
        var loaded = CommandBarLinks.Decode(old);
        Assert.Single(loaded);
        Assert.Equal("gh", loaded[0].Name);
        Assert.False(loaded[0].RunsWithoutArgument);
        Assert.False(loaded[0].RunsDirectly);
        Assert.Equal(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), loaded[0].Id);
    }

    [Fact]
    public void TheFlagsSurviveBeingSavedAndLoadedAgain()
    {
        var saved = Link("gh", "https://x/{query}", bare: true, direct: true);
        var back = CommandBarLinks.Decode(CommandBarLinks.Encode([saved]));
        Assert.Equal([saved], back);
    }

    [Fact]
    public void ThePlaceKindSurvivesARoundTrip()
    {
        var saved = Link("notes", @"C:\Users\x\My Notes", CommandBarLinkKind.Place);
        var json = CommandBarLinks.Encode([saved]);
        Assert.Contains("\"kind\":\"place\"", json);
        Assert.Contains(saved.Id.ToString("D").ToUpperInvariant(), json);
        Assert.Equal(saved, Assert.Single(CommandBarLinks.Decode(json)));
    }

    [Fact]
    public void AStoredScriptEntryDecodesToNothing()
    {
        const string json = """
            [{"name":"cur","kind":"script","destination":"/tmp/x.sh"},
             {"name":"gh","kind":"link","destination":"https://x"},
             {"name":"odd","kind":"hologram","destination":"y"}]
            """;
        var loaded = CommandBarLinks.Decode(json);
        Assert.Equal(["gh"], loaded.Select(l => l.Name));
        Assert.Empty(CommandBarLinks.Decode("""[{"name":"cur","kind":"script","destination":"x"}]"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("[1, null, \"x\"]")]
    public void UnreadableTextIsAnEmptyList(string? json) => Assert.Empty(CommandBarLinks.Decode(json));

    [Fact]
    public void AMalformedEntryDoesNotTakeTheOthersDown()
    {
        const string json = """[5, {"name":"a","destination":"https://a","id":"nope","runsDirectly":"yes"}, {"name":"b","destination":"https://b"}]""";
        var loaded = CommandBarLinks.Decode(json);
        Assert.Equal(["a", "b"], loaded.Select(l => l.Name));
        Assert.All(loaded, l => Assert.Equal(CommandBarLinkKind.Link, l.Kind));
        Assert.All(loaded, l => Assert.False(l.RunsDirectly));
        Assert.NotEqual(loaded[0].Id, loaded[1].Id);
    }

    [Fact]
    public void ADuplicateIdIsGivenAFreshOne()
    {
        const string json = """[{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"a","destination":"x"},{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"b","destination":"y"}]""";
        var loaded = CommandBarLinks.Decode(json);
        Assert.Equal(2, loaded.Count);
        Assert.NotEqual(loaded[0].Id, loaded[1].Id);
    }

    [Fact]
    public void NamesAndDestinationsAreSanitisedOnTheWayIn()
    {
        const string json = """[{"name":"  my\n  docs\t\u200E ","destination":" C:\\My Files\\\r\nNotes ","kind":"PLACE"}]""";
        var link = Assert.Single(CommandBarLinks.Decode(json));
        Assert.Equal("my docs", link.Name);
        Assert.Equal(@"C:\My Files\Notes", link.Destination);
        Assert.Equal(CommandBarLinkKind.Place, link.Kind);
        Assert.Equal("a b", CommandBarLinks.SanitizedName("a \u00A0 b"));
        Assert.Equal("", CommandBarLinks.SanitizedName(null));
        Assert.Equal("", CommandBarLinks.SanitizedDestination("\u0000 \n"));
    }

    [Fact]
    public void TheListNeverHoldsMoreThanTheLimit()
    {
        var many = Enumerable.Range(0, CommandBarLinks.Limit + 10).Select(i => Link("n" + i, "https://x/" + i)).ToList();
        Assert.Equal(CommandBarLinks.Limit, CommandBarLinks.Decode(CommandBarLinks.Encode(many)).Count);
        Assert.Equal(60, CommandBarLinks.Limit);
        var json = "[" + string.Join(",", many.Select(l => $"{{\"name\":\"{l.Name}\",\"destination\":\"{l.Destination}\"}}")) + "]";
        Assert.Equal(CommandBarLinks.Limit, CommandBarLinks.Decode(json).Count);
    }

    [Fact]
    public void ANonAsciiNameIsStoredReadably()
    {
        var json = CommandBarLinks.Encode([Link("café", "https://x/é?a=1&b=2")]);
        Assert.Contains("café", json);
        Assert.Contains("a=1&b=2", json);
        Assert.Equal("café", CommandBarLinks.Decode(json)[0].Name);
    }

    // MARK: - Open what was typed as a URL

    [Theory]
    [InlineData("example.com")]
    [InlineData("example.com/x")]
    [InlineData("https://example.com")]
    [InlineData("example.com:8080/path")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("sub.domain.co.uk")]
    [InlineData("https://example.museum")]
    [InlineData("https://例子.中国")]
    [InlineData("  example.com  ")]
    [InlineData("EXAMPLE.COM")]
    [InlineData("HTTPS://Example.com/A")]
    [InlineData("example.com?q=1")]
    [InlineData("example.com#top")]
    [InlineData("例子.中国")]
    [InlineData("xn--fsq.xn--fiqs8s")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://192.168.1.1/admin")]
    [InlineData("https://user@example.com")]
    [InlineData("my-site.dev/path/to/page?x=1&y=2#frag")]
    public void ReadsLikeAUrl(string address) => Assert.NotNull(CommandBarLinks.TypedUrl(address));

    [Theory]
    [InlineData("hello")]
    [InlineData("file.txt")]
    [InlineData("3.14")]
    [InlineData("version 2.0")]
    [InlineData("notes")]
    [InlineData("a b c")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("v1.2")]
    [InlineData("localhost")]
    [InlineData("user@example.com")]
    [InlineData("something/else")]
    [InlineData("https://")]
    [InlineData("http:notes")]
    [InlineData("https://exa mple.com")]
    [InlineData("ftp://files.example.org")]
    // Cases the macOS data detector rejected implicitly and this rule has to reject on purpose.
    [InlineData("report.pdf")]
    [InlineData("photo.PNG")]
    [InlineData("archive.zip")]
    [InlineData("README.md")]
    [InlineData("script.py")]
    [InlineData("Program.cs")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1:8080")]
    [InlineData("1.2.3")]
    [InlineData("e.g.")]
    [InlineData(".com")]
    [InlineData("example.")]
    [InlineData("-bad.example.com")]
    [InlineData("a..b.com")]
    [InlineData("example.c0m")]
    [InlineData("example.com:99999999")]
    [InlineData("C:\\Users\\x\\file.txt")]
    [InlineData("mailto:a@b.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    [InlineData("https://example.com/a b")]
    public void IsASearchNotAUrl(string plain) => Assert.Null(CommandBarLinks.TypedUrl(plain));

    [Fact]
    public void ATypedBareDomainIsOpenedWithAnHttpsScheme() =>
        Assert.Equal("https://example.com", CommandBarLinks.TypedUrl("example.com"));

    [Fact]
    public void APortOnABareDomainDoesNotBecomeAFakeScheme() =>
        Assert.Equal("https://example.com:8080/path", CommandBarLinks.TypedUrl("example.com:8080/path"));

    [Fact]
    public void ATypedAddressWithASchemeKeepsItsScheme()
    {
        Assert.Equal("https://example.com/x", CommandBarLinks.TypedUrl("https://example.com/x"));
        Assert.Equal("http://example.com/x", CommandBarLinks.TypedUrl("  http://example.com/x\n"));
    }

    [Fact]
    public void AnImageFileIsStillAUrlWhenTheSchemeSaysSo() =>
        // An explicit scheme is always honoured, even for a file-like last label.
        Assert.Equal("https://example.com/logo.png", CommandBarLinks.TypedUrl("https://example.com/logo.png"));
}
