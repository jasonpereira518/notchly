// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Notch/NotchAudioLevelService.swift: decides when to read audio and
// publishes the bars' levels, or null for their synthetic motion. Upstream taps the player's
// process; here the bars follow the system output, everything the default device plays.
// It reads only while the feature is on, music plays and the closed music strip is visible.
// Every member runs on the UI thread; reader callbacks are posted back to it.
using System.Windows.Threading;
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Media;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Lyrics;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Island;

public sealed class LiveEqualizerService(SettingsStore settings, Dispatcher dispatcher, Func<NowPlaying?> current) : IDisposable
{
    private ProcessLevelReader? _reader;
    private string? _readerSource;
    private Guid _readerId;
    private NotchAudioLevelSupport.SilenceMemory _silence;
    private DispatcherTimer? _stopTimer;
    private bool _visible;

    /// <summary>Seven levels from 0 to 1, or null while the bars keep their synthetic motion.</summary>
    public double[]? Levels { get; private set; }

    public bool IsEnabled =>
        NotchSupport.IsEnabled(settings) && settings.IsAvailable(AppFeature.NotchLiveEqualizer)
        && settings.GetBool(DefaultsKey.NotchLiveEqualizer);

    /// <summary>Whether the closed music strip, which shows the bars, is on screen.</summary>
    public void SetVisible(bool visible, NowPlaying? playing)
    {
        _visible = visible;
        Update(playing);
    }

    /// <summary>Upstream playbackChanged: start, keep, pause-hold or stop the reader.</summary>
    public void Update(NowPlaying? playing)
    {
        if (!IsEnabled)
        {
            _silence.Rearm();
            Stop();
            return;
        }
        if (playing is not { IsPlaying: true } || !_visible)
        {
            // Pausing or stopping arms the next play to read again.
            if (playing is not { IsPlaying: true }) _silence.Rearm();
            ScheduleStop();
            return;
        }
        // A reader that has not heard sound may already be counting silence from the pause:
        // give the resumed play a fresh chance, keeping audible readers through short pauses.
        var resumeBeforeSound = _stopTimer?.IsEnabled == true && Levels is null;
        _stopTimer?.Stop();
        // One reader hears the whole output, whichever player is in front.
        if (_reader is not null && !resumeBeforeSound) return;
        Stop();
        var identity = Identity(playing);
        if (!_silence.Reads(identity)) return;
        Read(playing.SourceAppId, identity);
    }

    private static NotchMusicIdentity Identity(NowPlaying playing) =>
        new(playing.Title, playing.Artist, playing.Album, playing.Duration.TotalSeconds, playing.SourceAppId);

    private void Read(string source, NotchMusicIdentity identity)
    {
        var id = Guid.NewGuid();
        _readerId = id;
        _readerSource = source;
        var reader = ProcessLevelReader.ForSystemOutput();
        // Only this reading may publish: a stopped reader can finish late.
        reader.Levels += levels => dispatcher.BeginInvoke(() => { if (_readerId == id) Receive(levels); });
        reader.Silent += () => dispatcher.BeginInvoke(() => { if (_readerId == id) GiveUp(identity); });
        reader.Unavailable += () => dispatcher.BeginInvoke(() => { if (_readerId == id) Stop(); });
        reader.ProcessesLeft += () => dispatcher.BeginInvoke(() => { if (_readerId == id) Restart(); });
        _reader = reader;
        _ = reader.StartAsync();
    }

    private void Receive(double[] levels)
    {
        if (_reader is null) return;
        Levels = levels;
    }

    /// <summary>Only a play that stayed silent is written off; a paused one already armed the next.</summary>
    private void GiveUp(NotchMusicIdentity identity)
    {
        if (_stopTimer?.IsEnabled != true) _silence.GiveUp(identity);
        Log.Information("Live equalizer heard only silence; synthetic bars for this song");
        Stop();
    }

    /// <summary>The default output moved to another device: read it from scratch.</summary>
    private void Restart()
    {
        var pausing = _stopTimer?.IsEnabled == true;
        Stop();
        _readerSource = null;
        if (!pausing) Update(current());
    }

    /// <summary>A pause keeps the bars a moment, so a skipped track does not blink the meter.</summary>
    private void ScheduleStop()
    {
        if (_reader is null && Levels is null) return;
        if (_stopTimer is null)
        {
            _stopTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(NotchAudioLevelSupport.PauseGrace) };
            _stopTimer.Tick += (_, _) => { _stopTimer.Stop(); Stop(); };
        }
        if (!_stopTimer.IsEnabled) _stopTimer.Start();
    }

    public void Stop()
    {
        _stopTimer?.Stop();
        _readerId = Guid.Empty;
        _reader?.Dispose();
        _reader = null;
        _readerSource = null;
        Levels = null;
    }

    public void Dispose() => Stop();
}
