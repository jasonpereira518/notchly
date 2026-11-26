// SPDX-License-Identifier: GPL-3.0-or-later
// Reading Spotify's replies: the queue, the player, playlists, devices and Liked Songs checks.
using Notchly.Core.Notch.Music;

namespace Notchly.Core.Tests;

public class SpotifyModelsTests
{
    private const string Queue = """
    {
      "currently_playing": { "type": "track", "uri": "spotify:track:now", "name": "Now" },
      "queue": [
        { "type": "track", "uri": "spotify:track:a", "name": "First", "duration_ms": 215500,
          "artists": [ { "name": "One" }, { "name": "Two" } ],
          "album": { "images": [ { "url": "https://i/640", "width": 640 }, { "url": "https://i/300", "width": 300 }, { "url": "https://i/64", "width": 64 } ] } },
        { "type": "episode", "uri": "spotify:episode:e", "name": "A podcast" },
        { "type": "track", "uri": "spotify:track:b", "name": "  ", "artists": [] },
        { "type": "track", "uri": "spotify:track:c", "name": "Third" },
        { "type": "track", "name": "No uri" },
        "junk"
      ]
    }
    """;

    [Fact]
    public void QueueKeepsSongsAndSkipsEpisodesAndNamelessEntries()
    {
        var queue = SpotifyModels.ParseQueue(Queue);
        Assert.Equal(["First", "Third"], queue.Select(i => i.Title));
        Assert.Equal("One, Two", queue[0].Artist);
        Assert.Equal("spotify:track:a", queue[0].Uri);
        Assert.Equal(215.5, queue[0].DurationSeconds);
        Assert.Equal("https://i/64", queue[0].ArtUrl);
        Assert.Equal("", queue[1].Artist);
        Assert.Null(queue[1].ArtUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("""{"queue":{}}""")]
    [InlineData("""{"queue":[]}""")]
    public void NoQueueIsAnEmptyList(string? json) => Assert.Empty(SpotifyModels.ParseQueue(json));

    [Fact]
    public void ArtPrefersTheSmallestPictureThatIsBigEnough()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""[{"url":"big","width":640},{"url":"mid","width":300},{"url":"tiny","width":32}]""");
        Assert.Equal("mid", SpotifyModels.PickArt(document.RootElement, 64));
        Assert.Equal("big", SpotifyModels.PickArt(document.RootElement, 500));
        Assert.Equal("big", SpotifyModels.PickArt(document.RootElement, 1000));
    }

    [Fact]
    public void PlayerStateIsRead()
    {
        var state = SpotifyModels.ParsePlayer("""
        { "is_playing": true, "shuffle_state": true, "repeat_state": "context",
          "item": { "uri": "spotify:track:x" }, "device": { "id": "dev1", "is_active": true } }
        """);
        Assert.NotNull(state);
        Assert.True(state.IsPlaying);
        Assert.True(state.Shuffle);
        Assert.Equal(NotchRepeatMode.All, state.Repeat);
        Assert.Equal("spotify:track:x", state.TrackUri);
        Assert.Equal("dev1", state.DeviceId);
    }

    [Fact]
    public void PlayerStateToleratesMissingParts()
    {
        var state = SpotifyModels.ParsePlayer("""{ "is_playing": false, "item": null, "device": {} }""");
        Assert.NotNull(state);
        Assert.False(state.IsPlaying);
        Assert.False(state.Shuffle);
        Assert.Equal(NotchRepeatMode.Off, state.Repeat);
        Assert.Null(state.TrackUri);
        Assert.Null(state.DeviceId);
        Assert.Null(SpotifyModels.ParsePlayer(""));
        Assert.Null(SpotifyModels.ParsePlayer("nope"));
    }

    [Fact]
    public void PlaylistsAreReadWithEitherTrackCountName()
    {
        var playlists = SpotifyModels.ParsePlaylists("""
        { "items": [
          { "id": "p1", "uri": "spotify:playlist:p1", "name": "Gym", "owner": { "display_name": "Me" },
            "images": [ { "url": "https://i/p1", "width": 300 } ], "items": { "total": 42 } },
          { "id": "p2", "uri": "spotify:playlist:p2", "name": "Old shape", "tracks": { "total": 7 } },
          { "id": "p3", "uri": "spotify:playlist:p3", "name": "" },
          { "uri": "spotify:playlist:p4", "name": "No id" },
          null
        ] }
        """);
        Assert.Equal(["Gym", "Old shape"], playlists.Select(p => p.Name));
        Assert.Equal(42, playlists[0].TrackCount);
        Assert.Equal("Me", playlists[0].Owner);
        Assert.Equal("https://i/p1", playlists[0].ArtUrl);
        Assert.Equal(7, playlists[1].TrackCount);
        Assert.Null(playlists[1].ArtUrl);
        Assert.Empty(SpotifyModels.ParsePlaylists("""{"items":"x"}"""));
    }

    [Fact]
    public void DevicePicksTheActiveOneThenAnyThatAcceptsCommands()
    {
        var devices = SpotifyModels.ParseDevices("""
        { "devices": [
          { "id": "locked", "name": "TV", "is_active": true, "is_restricted": true },
          { "id": "phone", "name": "Phone", "is_active": false },
          { "id": "pc", "name": "PC", "is_active": true },
          { "name": "No id" }
        ] }
        """);
        Assert.Equal(3, devices.Count);
        Assert.Equal("pc", SpotifyModels.PickDevice(devices)?.Id);
        Assert.Equal("phone", SpotifyModels.PickDevice(devices.Where(d => d.Id != "pc").ToList())?.Id);
        Assert.Null(SpotifyModels.PickDevice(devices.Where(d => d.IsRestricted).ToList()));
        Assert.Null(SpotifyModels.PickDevice([]));
    }

    [Theory]
    [InlineData("[true]", true)]
    [InlineData("[false]", false)]
    [InlineData("[]", null)]
    [InlineData("{}", null)]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void LikedCheckReadsTheFirstAnswer(string json, bool? expected) => Assert.Equal(expected, SpotifyModels.ParseContains(json));

    [Fact]
    public void UserIdAndLikedSongsContext()
    {
        Assert.Equal("abc", SpotifyModels.ParseUserId("""{"id":"abc","display_name":"A"}"""));
        Assert.Null(SpotifyModels.ParseUserId("[]"));
        Assert.Equal("spotify:user:abc:collection", SpotifyModels.LikedSongsUri("abc"));
    }

    [Theory]
    [InlineData("spotify:track:abc", true)]
    [InlineData("spotify:episode:abc", false)]
    [InlineData("spotify:local:a:b:c:1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTracksCanBeStarred(string? uri, bool expected) => Assert.Equal(expected, SpotifyModels.IsStarrable(uri));
}
