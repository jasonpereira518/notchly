// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream AppVolumeMixer's "lower volume when headphones disconnect" (loweringOutputVolumeIfHeadphonesDisconnected
// and restoringLoweredOutputVolume), kept apart from the audio calls: the device volume and mute go through
// IOutputControl so the rules can be pinned down by tests.
namespace Notchly.Core.Sound;

public interface IOutputControl
{
    double? Volume(string deviceId);
    bool SetVolume(string deviceId, double volume);
    bool? Muted(string deviceId);
    bool SetMuted(string deviceId, bool muted);
}

public sealed record OutputInfo(string Id, bool IsHeadphones);

/// <summary>What the guard did to the speakers, so it can be undone when the headphones return.</summary>
public sealed record LoweredOutput(string Id, double PreviousVolume, double AppliedVolume, bool MutedByLowering);

public sealed record LoweredOutputState(string? LastAutomaticLoweredOutputId = null, LoweredOutput? Lowered = null);

public static class HeadphoneGuard
{
    public const int DefaultPercent = 25;

    public static LoweredOutputState Apply(LoweredOutputState state, string? previousDefaultId, IReadOnlyList<OutputInfo> previousDevices,
                                           string? nextDefaultId, IReadOnlyList<OutputInfo> nextDevices,
                                           bool lowerOnDisconnect, int lowerToPercent, IOutputControl control)
    {
        if (nextDefaultId is not null && nextDevices.FirstOrDefault(d => d.Id == nextDefaultId)?.IsHeadphones == true)
        {
            // Headphones are back: the speakers get the volume they had before the disconnect lowered them.
            return state with { LastAutomaticLoweredOutputId = null, Lowered = Restore(state.Lowered, nextDevices, control) };
        }

        if (!lowerOnDisconnect || previousDefaultId is null || nextDefaultId is null) return state;
        var previousDefault = previousDevices.FirstOrDefault(d => d.Id == previousDefaultId);
        var nextDefault = nextDevices.FirstOrDefault(d => d.Id == nextDefaultId);
        if (previousDefault is not { IsHeadphones: true } || nextDefault is null || nextDefault.IsHeadphones) return state;
        if (nextDevices.Any(d => d.Id == previousDefaultId && d.IsHeadphones)) return state;
        if (state.LastAutomaticLoweredOutputId == nextDefaultId) return state;

        var percent = MixerSupport.SanitizedHeadphonesDisconnectPercent(lowerToPercent);
        var volume = percent / 100.0;
        var previousVolume = control.Volume(nextDefault.Id);
        if (!control.SetVolume(nextDefault.Id, volume)) return state;
        var muted = MixerSupport.ShouldMuteOutputLoweredForDisconnect(percent, control.Muted(nextDefault.Id))
                    && control.SetMuted(nextDefault.Id, true);
        state = state with { LastAutomaticLoweredOutputId = nextDefaultId };
        if (previousVolume is { } before && (before > volume || muted))
            state = state with { Lowered = new LoweredOutput(nextDefaultId, before, volume, muted) };
        return state;
    }

    /// <summary>
    /// Puts back the volume the guard lowered, and the mute it set at zero, while the volume is still the one
    /// it set: anything else was changed since, and that choice wins. Returns what is left to restore later.
    /// </summary>
    public static LoweredOutput? Restore(LoweredOutput? lowered, IReadOnlyList<OutputInfo> devices, IOutputControl control)
    {
        if (lowered is null) return null;
        // The device is not around to restore right now; it may come back.
        if (devices.All(d => d.Id != lowered.Id)) return lowered;
        if (!MixerSupport.ShouldRestoreOutputVolume(lowered.AppliedVolume, control.Volume(lowered.Id))) return null;
        control.SetVolume(lowered.Id, lowered.PreviousVolume);
        // A mute switched off since then was someone's choice, and one the guard did not set was never its to undo.
        if (lowered.MutedByLowering && control.Muted(lowered.Id) == true) control.SetMuted(lowered.Id, false);
        return null;
    }
}
