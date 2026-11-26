// SPDX-License-Identifier: GPL-3.0-or-later
// Windows addition: the matching rules of the Settings search box (after upstream's SettingsSearchSupport.swift).
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class SettingsSearchTests
{
    private static bool Hit(string query, string text) => SettingsSearchSupport.Matches(SettingsSearchSupport.Terms(query), text);

    [Fact]
    public void BlankQueryMatchesEverything()
    {
        Assert.Empty(SettingsSearchSupport.Terms("   "));
        Assert.Empty(SettingsSearchSupport.Terms(null));
        Assert.True(Hit("", "anything"));
    }

    [Fact]
    public void IgnoresCase()
    {
        Assert.True(Hit("LYRICS", "Show lyrics"));
        Assert.True(Hit("show", "SHOW LYRICS"));
    }

    [Fact]
    public void FindsPartsOfWords()
    {
        Assert.True(Hit("equal", "Live equalizer"));
    }

    [Fact]
    public void IgnoresAccentsAndWidth()
    {
        Assert.True(Hit("metr", "Métrics"));
        Assert.True(Hit("métr", "Metrics"));
        Assert.True(Hit("ＭＩＣ", "mic mute"));
    }

    [Fact]
    public void EveryWordMustBeFound()
    {
        Assert.True(Hit("hide full", "Hide in full screen"));
        Assert.True(Hit("full hide", "Hide in full screen"));
        Assert.False(Hit("hide camera", "Hide in full screen"));
    }

    [Fact]
    public void NoMatchWhenAbsent()
    {
        Assert.False(Hit("zzz", "Show lyrics"));
    }

    [Fact]
    public void SynonymsFindTheLabelsWord()
    {
        Assert.True(Hit("storage", "Low disk space"));
        Assert.True(Hit("mic", "Mute microphone"));
        Assert.True(Hit("ram", "Memory use"));
        Assert.False(Hit("storage", "Show lyrics"));
    }

    [Fact]
    public void FoldedTextTakesTheSameWay()
    {
        var terms = SettingsSearchSupport.Terms("Café");
        Assert.True(SettingsSearchSupport.MatchesFolded(terms, SettingsSearchSupport.Fold("The CAFE page")));
    }
}
