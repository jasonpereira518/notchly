// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream RepositoryFeatureTests (the URL cleaner cases, about lines 245-470) and URLCleanerMetadataTests.
using Notchly.Core.Clipboard;
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class UrlCleanerTests
{
    private static string? Clean(string text, UrlCleanRules? rules = null) => UrlCleaning.Clean(text, rules)?.Url;

    [Fact]
    public void TrackingParametersGoAndTheRestStays() =>
        Assert.Equal("https://example.com/path?id=42", Clean("https://example.com/path?utm_source=news&id=42&fbclid=abc"));

    [Fact]
    public void NamesAreComparedInLowerCaseAndTheFragmentStays() =>
        Assert.Equal("https://example.com/#section", Clean(" https://example.com/?GCLID=one&utm_campaign=x#section "));

    [Fact]
    public void AnyUtmPrefixGoes() =>
        Assert.Equal("https://example.com/", Clean("https://example.com/?utm_anything=1"));

    [Fact]
    public void RemovedNamesAreReportedInLinkOrder() =>
        Assert.Equal(["utm_source", "fbclid"], UrlCleaning.Clean("https://example.com/?utm_source=a&id=1&fbclid=b&utm_source=c")!.Removed);

    [Fact]
    public void CustomNamesAreExactMatchesOnly()
    {
        var rules = UrlCleanRules.FromStorage("Ref, source", null, null);
        Assert.Equal("https://example.com/?id=42", Clean("https://example.com/?REF=one&id=42&source=two", rules));
        Assert.Equal("https://example.com/?reference=one", Clean("https://example.com/?reference=one", rules));
    }

    [Fact]
    public void SwitchingTheUtmWildcardOffKeepsUtmButNotTheOthers()
    {
        var rules = UrlCleanRules.FromStorage(null, null, "|utm_*");
        Assert.Equal("https://example.com/?utm_source=a", Clean("https://example.com/?utm_source=a&fbclid=b", rules));
    }

    [Fact]
    public void AGlobalNameCanBeSwitchedOffOnItsOwn()
    {
        var rules = UrlCleanRules.FromStorage(null, null, "|utm_source");
        Assert.Equal("https://example.com/?utm_source=a", Clean("https://example.com/?utm_source=a&utm_medium=b", rules));
    }

    [Fact]
    public void SiteNamesApplyToTheHostAndItsSubdomainsOnly()
    {
        Assert.Equal("https://youtu.be/abc", Clean("https://youtu.be/abc?si=x"));
        Assert.Equal("https://www.youtube.com/watch?v=1", Clean("https://www.youtube.com/watch?v=1&si=x&feature=share"));
        Assert.Equal("https://example.com/?si=x", Clean("https://example.com/?si=x"));
        Assert.Equal("https://notyoutube.com/?si=x", Clean("https://notyoutube.com/?si=x"));
    }

    [Fact]
    public void ASiteNameCanBeSwitchedOffForThatSiteOnly()
    {
        var rules = UrlCleanRules.FromStorage(null, null, "youtube.com|si");
        Assert.Equal("https://youtube.com/watch?v=1&si=x", Clean("https://youtube.com/watch?v=1&si=x&feature=a", rules));
        Assert.Equal("https://youtu.be/abc", Clean("https://youtu.be/abc?si=x", rules));
    }

    [Fact]
    public void ACustomSiteRuleReachesOnlyThatSite()
    {
        var rules = UrlCleanRules.FromStorage(null, "weibo.com|sudaref", null);
        Assert.Equal("https://weibo.com/a?id=1", Clean("https://weibo.com/a?sudaref=x&id=1", rules));
        Assert.Equal("https://m.weibo.com/a", Clean("https://m.weibo.com/a?sudaref=x", rules));
        Assert.Equal("https://example.com/?sudaref=x", Clean("https://example.com/?sudaref=x", rules));
    }

    [Fact]
    public void XiaohongshuNamesAreMatchedCaseInsensitively() =>
        Assert.Equal("https://www.xiaohongshu.com/?id=1", Clean("https://www.xiaohongshu.com/?shareRedId=a&exSource=b&id=1"));

    [Fact]
    public void PercentEncodedNamesAreDecodedBeforeTheyAreCompared() =>
        Assert.Equal("https://example.com/?a=1", Clean("https://example.com/?a=1&%75tm_source=x"));

    [Fact]
    public void ALinkWithNothingToRemoveComesBackAsGiven()
    {
        var result = UrlCleaning.Clean("  https://example.com/a?b=1&c=%20  ")!;
        Assert.Equal("https://example.com/a?b=1&c=%20", result.Url);
        Assert.Empty(result.Removed);
        Assert.Equal("https://example.com/#frag?utm_source=x", Clean("https://example.com/#frag?utm_source=x"));
    }

    [Theory]
    [InlineData("not a link")]
    [InlineData("ftp://example.com/?utm_source=x")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData("see https://example.com/?utm_source=x")]
    [InlineData("example.com/?utm_source=x")]
    public void OnlyASingleWebLinkIsCleaned(string text) => Assert.Null(UrlCleaning.Clean(text));

    [Fact]
    public void TheOutcomeSaysWhatHappened()
    {
        Assert.Equal(UrlCleanOutcome.NotAUrl, UrlCleaning.Outcome("hello"));
        Assert.Equal(UrlCleanOutcome.Removed, UrlCleaning.Outcome("https://example.com/?fbclid=1"));
        Assert.Equal(UrlCleanOutcome.Unchanged, UrlCleaning.Outcome("https://example.com/?a=1"));
        Assert.Equal(UrlCleanOutcome.Unchanged, UrlCleaning.Outcome("  https://example.com/?a=1 "));
    }

    [Theory]
    [InlineData("https://user@Shop.Example.com:8080/x", "shop.example.com")]
    [InlineData("www.Example.com", "example.com")]
    [InlineData("http://www.example.com/a/b?c#d", "example.com")]
    [InlineData("[::ffff:192.0.2.1]", "[::ffff:192.0.2.1]")]
    public void SiteKeysAreHosts(string input, string expected) => Assert.Equal(expected, UrlCleaning.SiteKey(input));

    [Theory]
    [InlineData("not a host")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("https://user@:8080/x")]
    [InlineData("example.com|ref")]
    [InlineData("localhost")]
    [InlineData("")]
    public void NotAHostIsRefused(string input) => Assert.Null(UrlCleaning.SiteKey(input));

    [Fact]
    public void ParameterNamesAreLowerCaseWithoutSeparators()
    {
        Assert.Equal("ref", UrlCleaning.ParameterName(" Ref "));
        Assert.Null(UrlCleaning.ParameterName("a b"));
        Assert.Null(UrlCleaning.ParameterName("a|b"));
        Assert.Null(UrlCleaning.ParameterName("a=b"));
        Assert.Null(UrlCleaning.ParameterName("a&b"));
        Assert.Null(UrlCleaning.ParameterName("a,b"));
        Assert.Equal(["ref", "source"], UrlCleaning.CustomNames("Ref, source\nREF"));
    }

    [Fact]
    public void TokensRoundTripSortedAndSkipBadPairs()
    {
        var tokens = UrlCleaning.Tokens("youtube.com|si, |utm_*, bad, www.A.com|X, example.com|ref|x, nothost|y");
        Assert.Equal([("youtube.com", "si"), ("", "utm_*"), ("a.com", "x")], tokens);
        Assert.Equal("|utm_*,a.com|x,youtube.com|si", UrlCleaning.TokenStorage(tokens));
    }

    [Fact]
    public void AllBuiltInNamesAreLowerCase()
    {
        Assert.All(UrlCleaning.GlobalNames, n => Assert.Equal(n.ToLowerInvariant(), n));
        Assert.All(UrlCleaning.SiteNames.Values.SelectMany(v => v), n => Assert.Equal(n.ToLowerInvariant(), n));
        Assert.All(UrlCleaning.SiteNames.Keys, k => Assert.Equal(k.ToLowerInvariant(), k));
    }

    [Fact]
    public void TheDisplayListStartsWithTheWildcard()
    {
        var names = UrlCleaning.DisplayGlobalNames();
        Assert.Equal("utm_*", names[0]);
        Assert.DoesNotContain("utm_source", names);
        Assert.Contains("fbclid", names);
        Assert.Equal(names.Skip(1).Order(StringComparer.Ordinal), names.Skip(1));
    }

    // MARK: The HTML flavour

    [Fact]
    public void HtmlThatIsJustTheLinkMayBeDropped()
    {
        const string link = "https://example.com/a?utm_source=x&id=1";
        Assert.True(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link.Replace("&", "&amp;")}\">{link.Replace("&", "&amp;")}</a>", link));
        Assert.True(UrlCleaning.HtmlAllowsRewrite(null, link));
        Assert.True(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link.Replace("&", "&amp;")}\">Read more</a>", link));
    }

    [Fact]
    public void HtmlWithOtherLinksMediaOrBulkIsKept()
    {
        const string link = "https://example.com/a?utm_source=x";
        Assert.False(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\">x</a><a href=\"https://other.com\">y</a>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\"><img src=\"a.png\"></a>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\">{new string('a', UrlCleaning.MaxHtmlBytes)}</a>", link));
        Assert.False(UrlCleaning.HtmlAllowsRewrite($"<a href=\"{link}\"></a> text", link));
    }
}

public class ClipboardFormatsTests
{
    [Fact]
    public void ContentThatAsksNotToBeRecordedIsExcluded()
    {
        Assert.True(ClipboardFormats.IsExcluded(["UnicodeText", "ExcludeClipboardContentFromMonitorProcessing"]));
        Assert.True(ClipboardFormats.IsExcluded(["UnicodeText", "Clipboard Viewer Ignore"]));
        Assert.True(ClipboardFormats.IsExcluded(["UnicodeText"], canIncludeInHistory: 0));
        Assert.True(ClipboardFormats.IsExcluded(["UnicodeText"], canUploadToCloud: 0));
        Assert.False(ClipboardFormats.IsExcluded(["UnicodeText"], canIncludeInHistory: 1, canUploadToCloud: 1));
    }

    [Fact]
    public void OnlyTextAndLinkContentCountsAsALink()
    {
        Assert.True(ClipboardFormats.IsPlainLinkContent(["UnicodeText", "Text", "HTML Format", "UniformResourceLocatorW"]));
        Assert.False(ClipboardFormats.IsPlainLinkContent(["UnicodeText", "FileDrop"]));
        Assert.False(ClipboardFormats.IsPlainLinkContent(["UnicodeText", "PNG"]));
        Assert.False(ClipboardFormats.IsPlainLinkContent(["UnicodeText", "ExcludeClipboardContentFromMonitorProcessing"]));
        Assert.False(ClipboardFormats.IsPlainLinkContent(["UnicodeText", "SomeCustomPrivateFormat"]));
        Assert.False(ClipboardFormats.IsPlainLinkContent(["HTML Format"]));
    }

    [Fact]
    public void TheFragmentIsReadFromAClipboardHtmlBlock()
    {
        const string body = "<html><body><!--StartFragment--><a href=\"x\">é</a><!--EndFragment--></body></html>";
        var start = System.Text.Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        var end = start + System.Text.Encoding.UTF8.GetByteCount("<a href=\"x\">é</a>");
        // The header offsets count bytes of the whole block, header included.
        var header = $"Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:{{0:D10}}\r\nEndFragment:{{1:D10}}\r\n";
        var headerLength = string.Format(header, 0, 0).Length;
        var block = string.Format(header, headerLength + start, headerLength + end) + body;
        Assert.Equal("<a href=\"x\">é</a>", ClipboardFormats.HtmlFragment(System.Text.Encoding.UTF8.GetBytes(block)));
        Assert.Null(ClipboardFormats.HtmlFragment(System.Text.Encoding.UTF8.GetBytes("no header")));
        Assert.Null(ClipboardFormats.HtmlFragment(null));
    }
}
