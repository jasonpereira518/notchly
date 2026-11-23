// SPDX-License-Identifier: GPL-3.0-or-later
// Now playing through Windows' Global System Media Transport Controls: the public API that
// replaces upstream's NowPlayingAdapter + now-playing.pl (MediaRemote, private on macOS).
// Spotify, browsers (YouTube, etc.), Media Player and most players publish a session here.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using Windows.Media.Control;
using WinRT;

namespace Notchly.App.Platform.Media;

public sealed record NowPlaying(
    string Title,
    string Artist,
    string Album,
    string SourceAppId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdated,
    double PlaybackRate,
    bool CanPlayPause,
    bool CanGoNext,
    bool CanGoPrevious,
    bool CanSeek,
    BitmapSource? Artwork,
    Color? ArtworkTint)
{
    /// <summary>The position now, extrapolated from the last timeline report while playing.</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var elapsed = DateTimeOffset.Now - PositionUpdated;
            var position = Position + TimeSpan.FromTicks((long)(elapsed.Ticks * (PlaybackRate > 0 ? PlaybackRate : 1)));
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }

    public string SourceName => SourceAppId switch
    {
        var id when id.Contains("Spotify", StringComparison.OrdinalIgnoreCase) => "Spotify",
        var id when id.Contains("chrome", StringComparison.OrdinalIgnoreCase) => "Chrome",
        var id when id.Contains("msedge", StringComparison.OrdinalIgnoreCase) || id.Contains("Edge", StringComparison.OrdinalIgnoreCase) => "Edge",
        var id when id.Contains("firefox", StringComparison.OrdinalIgnoreCase) => "Firefox",
        var id when id.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) => "Media Player",
        var id => Path.GetFileNameWithoutExtension(id.Split('!')[0]),
    };
}

public sealed class MediaSessionService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artworkKey = "";
    private BitmapSource? _artwork;
    private Color? _tint;
    private int _refreshVersion;

    /// <summary>Preferred player app id fragment (upstream notchPreferredPlayer).</summary>
    public string PreferredPlayer { get; set; } = "";

    public NowPlaying? Current { get; private set; }

    /// <summary>Raised on the UI thread when anything about the playing item changes.</summary>
    public event Action? Changed;
    /// <summary>Raised on the UI thread when a different song starts.</summary>
    public event Action<NowPlaying>? TrackChanged;

    public MediaSessionService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Post(Reattach);
            _manager.CurrentSessionChanged += (_, _) => Post(Reattach);
            Reattach();
        }
        catch (Exception e)
        {
            Log.Error(e, "Media controls are unavailable");
        }
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    /// <summary>Preferred player, else one that is playing, else Windows' current session.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions();
        if (!string.IsNullOrWhiteSpace(PreferredPlayer))
        {
            var preferred = sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains(PreferredPlayer, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }
        var playing = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        return playing ?? _manager.GetCurrentSession();
    }

    private void Reattach()
    {
        var next = PickSession();
        if (!ReferenceEquals(next, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = next;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }
        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Post(() => _ = RefreshAsync());
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Post(() => _ = RefreshAsync());
    // Play/pause in another player can make it the one to follow.
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Post(Reattach);

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var session = _session;
        if (session is null)
        {
            Publish(null);
            return;
        }
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback?.Controls;
            var key = $"{session.SourceAppUserModelId}|{properties?.Title}|{properties?.Artist}|{properties?.AlbumTitle}";
            if (key != _artworkKey)
            {
                _artworkKey = key;
                (_artwork, _tint) = await LoadArtworkAsync(properties?.Thumbnail);
                if (version != _refreshVersion) return;
            }
            var title = properties?.Title ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                Publish(null);
                return;
            }
            Publish(new NowPlaying(
                title,
                properties?.Artist ?? "",
                properties?.AlbumTitle ?? "",
                session.SourceAppUserModelId,
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline?.Position ?? TimeSpan.Zero,
                (timeline?.EndTime ?? TimeSpan.Zero) - (timeline?.StartTime ?? TimeSpan.Zero),
                timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
                playback?.PlaybackRate ?? 1,
                controls?.IsPlayPauseToggleEnabled ?? false,
                controls?.IsNextEnabled ?? false,
                controls?.IsPreviousEnabled ?? false,
                controls?.IsPlaybackPositionEnabled ?? false,
                _artwork,
                _tint));
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not read the media session");
        }
    }

    private void Publish(NowPlaying? next)
    {
        var previous = Current;
        Current = next;
        Changed?.Invoke();
        if (next is not null && (previous is null || previous.Title != next.Title || previous.Artist != next.Artist))
            TrackChanged?.Invoke(next);
    }

    private static async Task<(BitmapSource?, Color?)> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null) return (null, null);
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var managed = stream.AsStream();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return (image, AverageColor(image));
        }
        catch (Exception e)
        {
            Log.Debug(e, "Artwork could not be decoded");
            return (null, null);
        }
    }

    /// <summary>The artwork's average colour, lifted so it reads as an accent on black.</summary>
    private static Color? AverageColor(BitmapSource image)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(8.0 / image.PixelWidth, 8.0 / image.PixelHeight)),
                                              PixelFormats.Bgra32, null, 0);
        var pixels = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(pixels, small.PixelWidth * 4, 0);
        double r = 0, g = 0, b = 0;
        var count = pixels.Length / 4;
        for (var i = 0; i < pixels.Length; i += 4) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
        if (count == 0) return null;
        r /= count; g /= count; b /= count;
        var max = Math.Max(r, Math.Max(g, b));
        var lift = max < 140 ? 140 / Math.Max(1, max) : 1;
        return Color.FromRgb((byte)Math.Min(255, r * lift), (byte)Math.Min(255, g * lift), (byte)Math.Min(255, b * lift));
    }

    // MARK: Commands

    public async void TogglePlayPause() { if (_session is { } s) await s.TryTogglePlayPauseAsync(); }
    public async void Next() { if (_session is { } s) await s.TrySkipNextAsync(); }
    public async void Previous() { if (_session is { } s) await s.TrySkipPreviousAsync(); }
    public async void Seek(TimeSpan position) { if (_session is { } s) await s.TryChangePlaybackPositionAsync(position.Ticks); }

    public void Dispose()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
    }
}
