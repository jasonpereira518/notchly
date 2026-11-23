// SPDX-License-Identifier: GPL-3.0-or-later
// The menu bar's colour from the app in front, its readable text, and the island's edge.
using Notchly.Core.MenuBar;

namespace Notchly.Core.Tests;

public class MenuBarTintTests
{
    private static byte[] Row(params (Rgb Color, int Count)[] runs)
    {
        var bytes = new List<byte>();
        foreach (var (c, n) in runs)
            for (var i = 0; i < n; i++) bytes.AddRange([c.B, c.G, c.R, 255]);
        return [.. bytes];
    }

    [Fact]
    public void TheColourMostOfTheRowSharesWins()
    {
        var title = new Rgb(0x20, 0x21, 0x24);
        var row = Row((title, 300), (Rgb.White, 40), (new Rgb(0x1A, 0x73, 0xE8), 20));
        Assert.Equal(title, MenuBarTint.Dominant(row));
    }

    [Fact]
    public void CloseShadesCountAsOneColour()
    {
        // A gentle gradient is still one colour, against a smaller run of something else.
        var row = Row((new Rgb(240, 240, 240), 60), (new Rgb(242, 242, 242), 60), (new Rgb(30, 30, 30), 90));
        var dominant = MenuBarTint.Dominant(row)!.Value;
        Assert.InRange(dominant.R, 240, 242);
    }

    [Fact]
    public void TooFewPixelsGiveNoColour()
    {
        Assert.Null(MenuBarTint.Dominant(Row((Rgb.White, MenuBarTint.MinimumPixels - 1))));
        Assert.Null(MenuBarTint.Dominant([]));
    }

    [Theory]
    [InlineData(255, 255, 255, true)]
    [InlineData(243, 243, 243, true)]    // a light title bar
    [InlineData(0, 0, 0, false)]
    [InlineData(0x20, 0x21, 0x24, false)] // a dark title bar
    [InlineData(0x1A, 0x73, 0xE8, false)] // a saturated blue reads better with white
    [InlineData(255, 214, 10, true)]      // yellow reads better with dark text
    public void TextStaysReadable(byte r, byte g, byte b, bool light)
    {
        var bar = new Rgb(r, g, b);
        Assert.Equal(light, MenuBarTint.IsLight(bar));
        Assert.Equal(light ? MenuBarTint.DarkText : Rgb.White, MenuBarTint.Text(bar));
    }

    [Fact]
    public void TheIslandAlwaysStandsOut()
    {
        // Black on a light bar stands out by itself; on dark bars, and over anything, an edge shows.
        Assert.Equal(0, MenuBarTint.EdgeAlpha(new Rgb(243, 243, 243), open: false));
        Assert.True(MenuBarTint.EdgeAlpha(Rgb.Black, open: false) > MenuBarTint.EdgeAlpha(new Rgb(0x30, 0x40, 0x70), open: false));
        Assert.True(MenuBarTint.EdgeAlpha(new Rgb(0x30, 0x40, 0x70), open: false) > 0);
        Assert.True(MenuBarTint.EdgeAlpha(null, open: false) > 0);
        Assert.True(MenuBarTint.EdgeAlpha(new Rgb(243, 243, 243), open: true) > 0);
    }
}
