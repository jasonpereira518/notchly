// SPDX-License-Identifier: GPL-3.0-or-later
// What the Music page knows from Spotify's web API, which Windows' media controls cannot tell: the songs queued
// next, whether the current song is in Liked Songs, the player's own shuffle and loop, and the user's playlists
// with a way to start one. A Windows addition; upstream reads the Mac player's queue through private MediaRemote.
// Everything here is idle until Spotify is the source, someone is signed in and the island is open on the page.
using System.Diagnostics;
using System.Windows.Threading;
using Notchly.App.Platform.Media;
using Notchly.App.Platform.Net.Spotify;
using Notchly.Core.Notch.Music;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Island.Music;

public sealed class MusicService : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaylistsFresh = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _poll;
    private int _version;
    private string _trackKey = "";
    private bool _live;
    private string? _userId;
    private DateTimeOffset _playlistsAt;
    private bool _loadingPlaylists;

    public MusicService(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollEvery };
        _poll.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Raised on the UI thread when anything here changed.</summary>
    public event Action? Changed;

    /// <summary>Spotify is the source and someone is signed in, so its answers apply to what is playing.</summary>
    public bool SpotifyActive { get; private set; }
    public SpotifyPlayerState? Player { get; private set; }
    public IReadOnlyList<NotchQueueItem> Queue { get; private set; } = [];
    /// <summary>True or false once Spotify said; null while unknown or for something that cannot be starred.</summary>
    public bool? Liked { get; private set; }
    /// <summary>Null until loaded.</summary>
    public IReadOnlyList<SpotifyPlaylist>? Playlists { get; private set; }
    /// <summary>The Liked Songs row's context, once the user's id is known.</summary>
    public string? LikedSongsUri => _userId is null ? null : SpotifyModels.LikedSongsUri(_userId);
    public bool QueueEnabled => _settings.GetBool(DefaultsKey.NotchQueueEnabled);

    /// <summary>Tells the service what is playing and whether the Music page is on screen.</summary>
    public void Update(NowPlaying? playing, bool live)
    {
        var active = playing is { SourceName: "Spotify" } && SpotifyAccount.IsSignedIn;
        var key = active ? $"{playing!.Title}|{playing.Artist}" : "";
        var wasLive = _live;
        var wasActive = SpotifyActive;
        SpotifyActive = active;
        _live = live && active;
        _poll.IsEnabled = _live;
        if (!active)
        {
            _trackKey = "";
            if (Player is not null || Queue.Count > 0 || Liked is not null)
            {
                _version++;
                (Player, Queue, Liked) = (null, [], null);
            }
            if (wasActive) Changed?.Invoke();
            return;
        }
        if (key != _trackKey || (_live && !wasLive))
        {
            var changedTrack = key != _trackKey;
            _trackKey = key;
            if (changedTrack) (Queue, Liked) = ([], null);
            // Spotify updates its queue a moment after announcing a song.
            if (_live) _ = RefreshAsync(changedTrack ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero);
        }
        if (wasActive != active) Changed?.Invoke();
    }

    public async Task RefreshAsync(TimeSpan? delay = null)
    {
        var version = ++_version;
        if (delay is { } wait && wait > TimeSpan.Zero) await Task.Delay(wait);
        if (version != _version || !SpotifyActive) return;
        try
        {
            var playerReply = await SpotifyApi.GetAsync("/me/player");
            if (version != _version) return;
            var player = playerReply.Ok && !playerReply.Empty ? SpotifyModels.ParsePlayer(playerReply.Body) : null;
            var queue = Queue;
            if (QueueEnabled)
            {
                var queueReply = await SpotifyApi.GetAsync("/me/player/queue");
                if (version != _version) return;
                if (queueReply.Ok) queue = SpotifyModels.ParseQueue(queueReply.Body);
            }
            bool? liked = null;
            if (SpotifyModels.IsStarrable(player?.TrackUri))
            {
                var likedReply = await SpotifyApi.GetAsync($"/me/library/contains?uris={Uri.EscapeDataString(player!.TrackUri!)}");
                if (version != _version) return;
                if (likedReply.Ok) liked = SpotifyModels.ParseContains(likedReply.Body);
            }
            (Player, Queue, Liked) = (player ?? Player, queue, liked);
            Changed?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Spotify state could not be read");
        }
    }

    // MARK: Commands (each answers whether Spotify took it; the caller falls back to Windows' controls when not)

    public async Task<bool> SetShuffleAsync(bool on)
    {
        if (Player is not { } before) return false;
        Player = before with { Shuffle = on };
        Changed?.Invoke();
        var reply = await SpotifyApi.PutAsync($"/me/player/shuffle?state={(on ? "true" : "false")}");
        if (reply.Ok) return true;
        Player = Player is { } now ? now with { Shuffle = before.Shuffle } : before;
        Changed?.Invoke();
        return false;
    }

    public async Task<bool> SetRepeatAsync(NotchRepeatMode mode)
    {
        if (Player is not { } before) return false;
        Player = before with { Repeat = mode };
        Changed?.Invoke();
        var reply = await SpotifyApi.PutAsync($"/me/player/repeat?state={mode.SpotifyState()}");
        if (reply.Ok) return true;
        Player = Player is { } now ? now with { Repeat = before.Repeat } : before;
        Changed?.Invoke();
        return false;
    }

    /// <summary>Adds the current song to Liked Songs or takes it out. False when it cannot be starred or Spotify refused.</summary>
    public async Task<bool> ToggleLikedAsync()
    {
        var uri = Player?.TrackUri;
        if (!SpotifyModels.IsStarrable(uri)) return false;
        var before = Liked ?? false;
        Liked = !before;
        Changed?.Invoke();
        var path = $"/me/library?uris={Uri.EscapeDataString(uri!)}";
        var reply = before ? await SpotifyApi.DeleteAsync(path) : await SpotifyApi.PutAsync(path);
        if (reply.Ok) return true;
        Liked = before;
        Changed?.Invoke();
        return false;
    }

    // MARK: Playlists

    /// <summary>Loads the playlists (and the user's id for Liked Songs) unless they were loaded recently.</summary>
    public async Task LoadPlaylistsAsync(bool force = false)
    {
        if (_loadingPlaylists || !SpotifyAccount.IsSignedIn) return;
        if (!force && Playlists is not null && DateTimeOffset.Now - _playlistsAt < PlaylistsFresh) return;
        _loadingPlaylists = true;
        try
        {
            if (_userId is null)
            {
                var me = await SpotifyApi.GetAsync("/me");
                if (me.Ok) _userId = SpotifyModels.ParseUserId(me.Body);
            }
            var all = new List<SpotifyPlaylist>();
            for (var offset = 0; offset < 100; offset += 50)
            {
                var reply = await SpotifyApi.GetAsync($"/me/playlists?limit=50&offset={offset}");
                if (!reply.Ok) { if (offset == 0) return; break; }
                var page = SpotifyModels.ParsePlaylists(reply.Body);
                all.AddRange(page);
                if (page.Count < 50) break;
            }
            var last = _settings.GetString(DefaultsKey.NotchMusicLastPlaylist);
            // The one played last comes first, so "pick up where I left off" is one click.
            Playlists = all.OrderBy(p => p.Uri == last ? 0 : 1).ToList();
            _playlistsAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Spotify playlists could not be read");
        }
        finally
        {
            _loadingPlaylists = false;
        }
    }

    /// <summary>
    /// Starts a playlist (or Liked Songs) on this PC's Spotify, opening the app first when it is not running.
    /// Returns null when it started, else what went wrong in plain words.
    /// </summary>
    public async Task<string?> PlayAsync(string contextUri)
    {
        if (!SpotifyAccount.IsSignedIn) return "Connect Spotify in Settings first.";
        var device = await FindDeviceAsync();
        if (device is null)
        {
            try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return "Spotify is not running on any of your devices. Open it, then try again.";
            }
            for (var attempt = 0; attempt < 8 && device is null; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                device = await FindDeviceAsync();
            }
            if (device is null) return "Spotify did not come up in time. Open it, then try again.";
        }
        var reply = await Play(contextUri, device.Id);
        if (reply.Status == 404)
        {
            // The device is listed but idle: hand playback to it, then ask again.
            await SpotifyApi.PutAsync("/me/player", $$"""{"device_ids":["{{device.Id}}"],"play":false}""");
            await Task.Delay(500);
            reply = await Play(contextUri, device.Id);
        }
        if (reply.Ok)
        {
            _settings.Set(DefaultsKey.NotchMusicLastPlaylist, contextUri);
            return null;
        }
        return reply.Status switch
        {
            403 => "Spotify only lets Premium accounts start playback from other apps.",
            429 => "Spotify is busy. Try again in a moment.",
            0 => "Could not reach Spotify.",
            _ => "Spotify would not start that playlist.",
        };
    }

    private static Task<SpotifyReply> Play(string contextUri, string deviceId) =>
        SpotifyApi.PutAsync($"/me/player/play?device_id={Uri.EscapeDataString(deviceId)}",
                            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["context_uri"] = contextUri }));

    private static async Task<SpotifyDevice?> FindDeviceAsync()
    {
        var reply = await SpotifyApi.GetAsync("/me/player/devices");
        return reply.Ok ? SpotifyModels.PickDevice(SpotifyModels.ParseDevices(reply.Body)) : null;
    }

    public void Dispose()
    {
        _poll.Stop();
        _version++;
    }
}
