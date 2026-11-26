// SPDX-License-Identifier: GPL-3.0-or-later
// Signing in to Spotify: the RFC 7636 example, the authorisation address, the loopback reply and the token reply.
using System.Web;
using Notchly.Core.Notch.Music;

namespace Notchly.Core.Tests;

public class SpotifyAuthTests
{
    [Fact]
    public void ChallengeMatchesTheRfc7636Example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                     SpotifyAuthSupport.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void VerifiersAreLongEnoughUrlSafeAndFresh()
    {
        var first = SpotifyAuthSupport.NewVerifier();
        Assert.InRange(first.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, SpotifyAuthSupport.NewVerifier());
        Assert.NotEqual(SpotifyAuthSupport.NewState(), SpotifyAuthSupport.NewState());
    }

    [Fact]
    public void AuthorizeAddressCarriesEverySpotifyAsksFor()
    {
        var redirect = SpotifyAuthSupport.RedirectUri(SpotifyAuthSupport.PreferredPort);
        Assert.Equal("http://127.0.0.1:47821/callback", redirect);
        var url = SpotifyAuthSupport.AuthorizeUrl(" client123 ", redirect, "challenge", "state42");
        Assert.Equal("accounts.spotify.com", url.Host);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("client123", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(redirect, query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("challenge", query["code_challenge"]);
        Assert.Equal("state42", query["state"]);
        Assert.Equal(string.Join(' ', SpotifyAuthSupport.Scopes), query["scope"]);
    }

    [Fact]
    public void CallbackWithTheRightStateGivesTheCode()
    {
        var result = SpotifyAuthSupport.ParseCallback("GET /callback?code=abc%2Fdef&state=s1 HTTP/1.1", "s1");
        Assert.NotNull(result);
        Assert.Equal("abc/def", result.Code);
        Assert.True(result.StateMatches);
        Assert.Null(result.Error);
    }

    [Fact]
    public void CallbackWithAnotherStateIsFlagged()
    {
        var result = SpotifyAuthSupport.ParseCallback("GET /callback?code=abc&state=other HTTP/1.1", "s1");
        Assert.NotNull(result);
        Assert.False(result.StateMatches);
    }

    [Fact]
    public void RefusedSignInReportsTheError()
    {
        var result = SpotifyAuthSupport.ParseCallback("GET /callback?error=access_denied&state=s1 HTTP/1.1", "s1");
        Assert.NotNull(result);
        Assert.Null(result.Code);
        Assert.Equal("access_denied", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GET /favicon.ico HTTP/1.1")]
    [InlineData("POST /callback?code=a&state=s1 HTTP/1.1")]
    [InlineData("garbage")]
    public void OtherRequestsAreIgnored(string? line) => Assert.Null(SpotifyAuthSupport.ParseCallback(line, "s1"));

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TokenReplyIsParsedWithItsLifetime()
    {
        var tokens = SpotifyAuthSupport.ParseTokens(
            """{"access_token":"A","token_type":"Bearer","expires_in":3600,"refresh_token":"R","scope":"user-read-playback-state"}""", Now);
        Assert.NotNull(tokens);
        Assert.Equal("A", tokens.AccessToken);
        Assert.Equal("R", tokens.RefreshToken);
        Assert.Equal(Now.AddHours(1), tokens.Expires);
        Assert.Equal("user-read-playback-state", tokens.Scope);
    }

    [Fact]
    public void RefreshReplyWithoutANewRefreshTokenKeepsTheOldOne()
    {
        var tokens = SpotifyAuthSupport.ParseTokens("""{"access_token":"B","expires_in":3600}""", Now, "old");
        Assert.NotNull(tokens);
        Assert.Equal("old", tokens.RefreshToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"error":"invalid_grant"}""")]
    [InlineData("""{"access_token":"A","expires_in":3600}""")]
    public void BadReplyGivesNoTokens(string? json) => Assert.Null(SpotifyAuthSupport.ParseTokens(json, Now));

    [Fact]
    public void TokenNeedsRefreshingAMinuteBeforeItLapses()
    {
        var tokens = new SpotifyTokens("A", "R", Now.AddMinutes(10), "");
        Assert.False(tokens.NeedsRefresh(Now));
        Assert.False(tokens.NeedsRefresh(Now.AddMinutes(8)));
        Assert.True(tokens.NeedsRefresh(Now.AddMinutes(9).AddSeconds(30)));
        Assert.True(tokens.NeedsRefresh(Now.AddMinutes(11)));
    }

    [Fact]
    public void ScopeCheckNeedsEveryScope()
    {
        Assert.True(SpotifyAuthSupport.CoversScopes(string.Join(' ', SpotifyAuthSupport.Scopes)));
        Assert.False(SpotifyAuthSupport.CoversScopes("user-read-playback-state"));
        Assert.False(SpotifyAuthSupport.CoversScopes(""));
    }
}
