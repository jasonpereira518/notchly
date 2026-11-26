// SPDX-License-Identifier: GPL-3.0-or-later
// Loop modes for the Music page. A Windows addition: upstream's Music page has shuffle but no loop control.
namespace Notchly.Core.Notch.Music;

/// <summary>What happens when a song or the list ends.</summary>
public enum NotchRepeatMode
{
    Off,
    /// <summary>Play the whole list again (Windows "List", Spotify "context").</summary>
    All,
    /// <summary>Play the same song again (Windows "Track", Spotify "track").</summary>
    One,
}

public static class NotchRepeatSupport
{
    /// <summary>The next mode when the loop button is pressed: off, then the list, then one song, then off.</summary>
    public static NotchRepeatMode Next(this NotchRepeatMode mode) => mode switch
    {
        NotchRepeatMode.Off => NotchRepeatMode.All,
        NotchRepeatMode.All => NotchRepeatMode.One,
        _ => NotchRepeatMode.Off,
    };

    /// <summary>Spotify's `state` value for `PUT /me/player/repeat`.</summary>
    public static string SpotifyState(this NotchRepeatMode mode) => mode switch
    {
        NotchRepeatMode.All => "context",
        NotchRepeatMode.One => "track",
        _ => "off",
    };

    /// <summary>The mode for a Spotify `repeat_state` value; anything unknown is off.</summary>
    public static NotchRepeatMode FromSpotify(string? state) => state switch
    {
        "context" => NotchRepeatMode.All,
        "track" => NotchRepeatMode.One,
        _ => NotchRepeatMode.Off,
    };
}
