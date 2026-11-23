// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Notch/NotchLyricsService.swift: the island's lyrics state machine.
// Online lookup needs the user's consent (notchLyricsOnline). Requests run one at a time; each
// step re-checks that lyrics are still visible, consent is still given and the song is the same.
// Every member runs on the UI thread: awaits resume there through the dispatcher's context.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Notchly.App.Platform.Net;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Lyrics;
using Notchly.Core.Settings;
using Serilog;
using Support = Notchly.Core.Notch.Lyrics.NotchLyricsSupport;

namespace Notchly.App.Island.Lyrics;

public enum LyricsState { Idle, Consent, Loading, Unavailable, Failed, Ready }

public sealed class LyricsService(SettingsStore settings) : IDisposable
{
    private readonly NotchLyricsMemory _memory = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (Support.ArtistIdentityInfo? Identity, bool Failed, double Expires)> _artistCache = [];
    private CancellationTokenSource _generation = new();
    private bool _visible, _online;
    private (NotchMusicIdentity Track, bool Failed, double Expires)? _settledAttempt;
    private double _lastAliasRequest = -10;

    public LyricsState State { get; private set; } = LyricsState.Idle;
    public NotchLyrics? Lyrics => _memory.Lyrics;
    public double Offset => _memory.Offset;
    public NotchMusicIdentity? Track => _memory.Track;

    /// <summary>Raised whenever state, lyrics or offset change.</summary>
    public event Action? Changed;

    private double Now => _clock.Elapsed.TotalSeconds;

    public bool IsEnabled =>
        NotchSupport.IsEnabled(settings) && settings.IsAvailable(AppFeature.NotchLyrics)
        && settings.GetBool(DefaultsKey.NotchLyricsEnabled)
        && NotchSupport.Modules(settings.GetString(DefaultsKey.NotchModuleOrder), settings.GetString(DefaultsKey.NotchHiddenModules))
                       .Contains(NotchModule.Music);

    public bool OnlineEnabled => IsEnabled && settings.GetBool(DefaultsKey.NotchLyricsOnline);

    private void Raise() => Changed?.Invoke();

    // MARK: Inputs (upstream update / playbackChanged / retry / hide / stop)

    public void Update(NotchMusicIdentity? next, bool visible)
    {
        if (!IsEnabled) { Stop(); return; }
        var wanted = visible && next is not null;
        var online = OnlineEnabled;
        var changedTrack = next is not null && next != Track;
        if (!changedTrack && wanted == _visible && online == _online) return;
        Cancel();
        if (changedTrack) { _memory.Select(next); _settledAttempt = null; }
        _visible = wanted;
        _online = online;
        if (!wanted || Track is not { } track) { SetState(Lyrics is null ? LyricsState.Idle : LyricsState.Ready); return; }
        if (Lyrics is not null) { SetState(LyricsState.Ready); return; }
        if (!online) { SetState(LyricsState.Consent); return; }
        Run(LoadAsync(track, _generation.Token));
    }

    /// <summary>Actual player metadata changed, including to nothing playing.</summary>
    public void PlaybackChanged(NotchMusicIdentity? next)
    {
        if (!IsEnabled) { Stop(); return; }
        if (next == Track) return;
        var wasVisible = _visible;
        Cancel();
        _memory.Select(next);
        _settledAttempt = null;
        _visible = false;
        SetState(LyricsState.Idle);
        if (next is not null) Update(next, wasVisible);
    }

    public void Retry()
    {
        if (!_visible || !OnlineEnabled || Track is not { } track) return;
        Cancel();
        _settledAttempt = null;
        var key = Support.ArtistCacheKey(track);
        if (_artistCache.TryGetValue(key, out var cached) && (cached.Failed || cached.Identity is null)) _artistCache.Remove(key);
        Run(LoadAsync(track, _generation.Token));
    }

    public void AdjustOffset(double amount) { _memory.AdjustOffset(amount); Raise(); }
    public void ResetOffset() { _memory.ResetOffset(); Raise(); }

    public void Hide()
    {
        Cancel();
        _visible = false;
        if (!IsEnabled)
        {
            _memory.Clear();
            _settledAttempt = null;
            _artistCache.Clear();
        }
        SetState(Lyrics is null ? LyricsState.Idle : LyricsState.Ready);
    }

    public void Stop()
    {
        Cancel();
        _visible = false;
        _online = false;
        _memory.Clear();
        _settledAttempt = null;
        _artistCache.Clear();
        SetState(LyricsState.Idle);
    }

    /// <summary>Timed lyrics from a file for the current recording; kept only while it plays.</summary>
    public void Import(string path)
    {
        if (!IsEnabled || Track is not { } track) return;
        Cancel();
        try
        {
            var info = new FileInfo(path);
            var lines = info.Length <= Support.MaximumBytes ? Support.Parse(File.ReadAllText(path), track.Duration) : [];
            if (lines.Count == 0) { SetState(LyricsState.Failed); return; }
            _memory.Replace(new NotchLyrics(lines, "", false), track);
            _settledAttempt = null;
            SetState(LyricsState.Ready);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read lyrics file");
            SetState(LyricsState.Failed);
        }
    }

    private void Cancel()
    {
        _generation.Cancel();
        _generation.Dispose();
        _generation = new CancellationTokenSource();
    }

    private void SetState(LyricsState state)
    {
        State = state;
        Raise();
    }

    private static async void Run(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error(e, "Lyrics lookup failed"); }
    }

    private bool Accepts(NotchMusicIdentity track, CancellationToken token) =>
        _visible && !token.IsCancellationRequested && Track == track && OnlineEnabled;

    // MARK: Lookup chain

    private async Task LoadAsync(NotchMusicIdentity track, CancellationToken token)
    {
        if (_settledAttempt is { } attempt && attempt.Track == track && attempt.Expires > Now)
        {
            SetState(attempt.Failed ? LyricsState.Failed : LyricsState.Unavailable);
            return;
        }
        _settledAttempt = null;
        if (Support.LookupUrl(track) is not { } url) { SetState(LyricsState.Unavailable); return; }
        SetState(LyricsState.Loading);
        var result = await Download(url, Support.MaximumBytes, token);
        if (!Accepts(track, token)) return;
        var lyrics = result.Data is { } data ? Support.Decode(data, track) ?? Support.DecodeCatalogMatch(data, track) : null;
        if (lyrics is not null) Finish(lyrics, track, failed: false);
        else if (!result.CanSearch) Finish(null, track, failed: true);
        else await SearchAsync(track, Support.SearchUrls(track), [], result.IsFailure, token);
    }

    private void Finish(NotchLyrics? lyrics, NotchMusicIdentity track, bool failed)
    {
        if (!_memory.Replace(lyrics, track)) return;
        _settledAttempt = lyrics is null ? (track, failed, Now + (failed ? 15 : 60)) : null;
        SetState(lyrics is not null ? LyricsState.Ready : failed ? LyricsState.Failed : LyricsState.Unavailable);
    }

    /// <summary>Sequential, bounded catalog fallback.</summary>
    private async Task SearchAsync(NotchMusicIdentity track, IReadOnlyList<Uri> urls, List<Support.Candidate> candidates, bool failed,
                                   CancellationToken token, IReadOnlyList<string>? aliases = null,
                                   bool mayResolveAliases = true, bool mayDiscover = true)
    {
        foreach (var url in urls)
        {
            if (!Accepts(track, token)) return;
            var result = await Download(url, Support.MaximumSearchBytes, token);
            if (!Accepts(track, token)) return;
            if (result.Data is { } data)
            {
                candidates = [.. candidates, .. Support.SearchCandidates(data, track, verifiedArtistAliases: aliases?.ToList())];
                if (Support.ContainsCatalogRecording(data, track)) { mayResolveAliases = false; mayDiscover = false; }
            }
            if (!result.CanSearch) { Finish(Support.BestLyrics(candidates), track, failed: true); return; }
            failed |= result.IsFailure;
        }
        if (!Accepts(track, token)) return;
        if (candidates.Count == 0 && !failed && mayDiscover && Support.PrefersDiscovery(track))
        {
            await SearchAsync(track, Support.DiscoveryUrls(track), [], false, token, aliases, mayResolveAliases, mayDiscover: false);
            return;
        }
        if (candidates.Count == 0 && !failed && mayResolveAliases)
        {
            await ResolveArtistAsync(track, token);
            return;
        }
        if (candidates.Count == 0 && !failed && mayDiscover)
        {
            await SearchAsync(track, Support.DiscoveryUrls(track), [], false, token, aliases, mayResolveAliases: false, mayDiscover: false);
            return;
        }
        Finish(Support.BestLyrics(candidates), track, failed);
    }

    /// <summary>Alias verification is a last resort. Only exact identity membership is accepted.</summary>
    private async Task ResolveArtistAsync(NotchMusicIdentity track, CancellationToken token)
    {
        var key = Support.ArtistCacheKey(track);
        if (_artistCache.TryGetValue(key, out var cached) && cached.Expires > Now)
        {
            await SearchArtistAsync(track, cached.Identity, cached.Failed, token);
            return;
        }
        if (Support.ArtistIdentityUrl(track) is not { } url) { Finish(null, track, failed: false); return; }
        if (await RequestArtistMetadata(url, track, token) is not { } result) return;
        if (result.Data is not { } data) { await CacheArtist(null, track, failed: true, token); return; }
        var broad = CountAbove(data, 5);
        var artists = Support.ArtistIdentities(data, track, allowTruncated: broad);
        if (artists.Count == 1 && artists[0].QueryNames.Count == 0 && Support.ArtistRecordingUrl(track, artists) is { } recordingUrl)
        {
            if (await RequestArtistMetadata(recordingUrl, track, token) is not { } reply) return;
            var identity = reply.Data is { } d ? Support.RecordingArtistIdentity(d, track, artists) : null;
            await CacheArtist(identity, track, failed: reply.Data is null, token);
        }
        else if (artists.Count == 1 && !broad)
        {
            await CacheArtist(artists[0], track, failed: false, token);
        }
        else if (artists.Count > 0 && Support.ArtistAlbumUrl(track) is { } albumUrl)
        {
            if (await RequestArtistMetadata(albumUrl, track, token) is not { } reply) return;
            var identity = reply.Data is { } d ? Support.ArtistIdentity(d, track, artists) : null;
            await CacheArtist(identity, track, failed: reply.Data is null, token);
        }
        else
        {
            await CacheArtist(null, track, failed: false, token);
        }
    }

    /// <summary>MusicBrainz allows one request per second. A superseded request never leaves the PC.</summary>
    private async Task<NotchLyricsDownloadResult?> RequestArtistMetadata(Uri url, NotchMusicIdentity track, CancellationToken token)
    {
        var delay = Math.Max(0, 1.05 - (Now - _lastAliasRequest));
        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token);
        if (!Accepts(track, token)) return null;
        _lastAliasRequest = Now;
        var result = await Download(url, Support.MaximumBytes, token);
        return Accepts(track, token) ? result : null;
    }

    private async Task CacheArtist(Support.ArtistIdentityInfo? identity, NotchMusicIdentity track, bool failed, CancellationToken token)
    {
        if (!Accepts(track, token)) return;
        foreach (var expired in _artistCache.Where(e => e.Value.Expires <= Now).Select(e => e.Key).ToList()) _artistCache.Remove(expired);
        if (_artistCache.Count >= 32) _artistCache.Remove(_artistCache.MinBy(e => e.Value.Expires).Key);
        _artistCache[Support.ArtistCacheKey(track)] = (identity, failed, Now + (failed ? 15 : identity is null ? 600 : 3600));
        await SearchArtistAsync(track, identity, failed, token);
    }

    private async Task SearchArtistAsync(NotchMusicIdentity track, Support.ArtistIdentityInfo? identity, bool failed, CancellationToken token)
    {
        if (!Accepts(track, token)) return;
        if (identity is null)
        {
            if (!failed && !Support.PrefersDiscovery(track))
                await SearchAsync(track, Support.DiscoveryUrls(track), [], false, token, mayResolveAliases: false, mayDiscover: false);
            else Finish(null, track, failed);
            return;
        }
        await SearchAsync(track, Support.AliasSearchUrls(track, identity), [], false, token, identity.Names,
                          mayResolveAliases: false, mayDiscover: !Support.PrefersDiscovery(track));
    }

    private static async Task<NotchLyricsDownloadResult> Download(Uri url, int maximumBytes, CancellationToken token)
    {
        var result = await BoundedDownloader.LoadAsync(url, maximumBytes, token);
        Log.Debug("Lyrics request {Host}{Path} → {Result}", url.Host, url.AbsolutePath, result.GetType().Name);
        return result;
    }

    private static bool CountAbove(byte[] data, int limit)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("count", out var count)
                   && count.ValueKind == JsonValueKind.Number && count.GetDouble() > limit;
        }
        catch (JsonException) { return false; }
    }

    public void Dispose()
    {
        _generation.Cancel();
        _generation.Dispose();
    }
}
