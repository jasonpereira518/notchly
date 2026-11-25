// SPDX-License-Identifier: GPL-3.0-or-later
// The loop button's cycle and its Spotify mapping (a Windows addition, so there is no upstream test to port).
using Notchly.Core.Notch.Music;

namespace Notchly.Core.Tests;

public class NotchRepeatTests
{
    [Fact]
    public void LoopCyclesOffListSongOff()
    {
        var mode = NotchRepeatMode.Off;
        mode = mode.Next();
        Assert.Equal(NotchRepeatMode.All, mode);
        mode = mode.Next();
        Assert.Equal(NotchRepeatMode.One, mode);
        mode = mode.Next();
        Assert.Equal(NotchRepeatMode.Off, mode);
    }

    [Theory]
    [InlineData(NotchRepeatMode.Off, "off")]
    [InlineData(NotchRepeatMode.All, "context")]
    [InlineData(NotchRepeatMode.One, "track")]
    public void SpotifyStatesRoundTrip(NotchRepeatMode mode, string state)
    {
        Assert.Equal(state, mode.SpotifyState());
        Assert.Equal(mode, NotchRepeatSupport.FromSpotify(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shuffle")]
    public void UnknownSpotifyStateIsOff(string? state) =>
        Assert.Equal(NotchRepeatMode.Off, NotchRepeatSupport.FromSpotify(state));
}
