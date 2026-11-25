// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/MicMuteSupport.swift: the decisions behind a global
// microphone mute, kept apart from the audio calls so tests can pin them down: which devices the mute has to
// reach, which it must never touch, and how a device gets its level back.
// Windows difference: an endpoint has one mute switch that recording apps honour, so "silenced" is the switch
// being on or the level being down; upstream's per-channel elements do not exist here.
namespace Notchly.Core.Sound;

public static class MicMuteSupport
{
    /// <summary>The level a device falls back to when nothing was saved for it: usable, but not startling.</summary>
    public const double FallbackVolume = 0.75;
    /// <summary>Below this, a level counts as silence.</summary>
    public const double SilenceThreshold = 0.01;

    /// <summary>A level worth remembering: saving a zero would make the unmute restore silence.</summary>
    public static bool ShouldSaveVolume(double? volume) => volume is { } v && v > SilenceThreshold;

    /// <summary>
    /// The first real level is the one unmute puts back. A later re-apply, from an app raising gain while
    /// muted, must not replace it.
    /// </summary>
    public static bool ShouldReplaceSavedVolume(double? existing, double? current) =>
        existing is not > SilenceThreshold && ShouldSaveVolume(current);

    /// <summary>Capture is silent when the mute switch is on or the input level is down.</summary>
    public static bool IsCaptureSilenced(bool? muteSwitch, double? volume) =>
        muteSwitch == true || volume is { } v && v <= SilenceThreshold;

    /// <summary>A mic the user silenced themselves is left alone; one this app already muted is still ours.</summary>
    public static bool ShouldLeaveUntouched(bool owned, bool captureSilenced) => !owned && captureSilenced;

    public enum RestoreKind { AlreadyAudible, Unreadable, Restore }

    public readonly record struct RestoreNeed(RestoreKind Kind, bool ClearMute = false, bool RestoreVolume = false);

    /// <summary>What an unmute has to do to a device this app muted: the switch and the level are separate controls.</summary>
    public static RestoreNeed NeedFor(bool? muteSwitch, double? volume, bool hasSavedVolume = false)
    {
        var muteOn = muteSwitch == true;
        if (volume is not { } level)
            return hasSavedVolume || !muteOn ? new RestoreNeed(RestoreKind.Unreadable) : new RestoreNeed(RestoreKind.Restore, true, false);
        var quiet = level <= SilenceThreshold;
        return !muteOn && !quiet ? new RestoreNeed(RestoreKind.AlreadyAudible) : new RestoreNeed(RestoreKind.Restore, muteOn, quiet);
    }

    public static double VolumeToRestore(string id, IReadOnlyDictionary<string, double> saved, double legacy)
    {
        if (saved.TryGetValue(id, out var value) && value > SilenceThreshold) return value;
        return legacy > SilenceThreshold ? legacy : FallbackVolume;
    }

    /// <summary>
    /// Which devices an unmute touches: normally those this app muted, so a mic silenced in Settings stays so.
    /// With no record at all every present device is restored (leaving someone muted with no way back is the
    /// worse failure); an empty record means every device was already silent by the user's hand.
    /// </summary>
    public static IReadOnlyList<string> RestoreTargets(IReadOnlyList<string>? recorded, IReadOnlyList<string> present)
    {
        if (recorded is null) return present;
        var wanted = recorded.ToHashSet(StringComparer.Ordinal);
        return present.Where(wanted.Contains).ToList();
    }

    /// <summary>Claims a sweep carries forward: devices this app silenced that are not here now stay its to release.</summary>
    public static IReadOnlyList<string> AbsentClaims(IReadOnlyList<string>? recorded, IReadOnlyList<string> present)
    {
        var here = present.ToHashSet(StringComparer.Ordinal);
        return (recorded ?? []).Where(id => !here.Contains(id)).ToList();
    }

    /// <summary>
    /// Consecutive attempts to silence one device back off from a quarter second to half a minute: the
    /// first two retry at once, then 0.25·2^(n-3) seconds, capped at 30, at most 10 attempts.
    /// </summary>
    public sealed record Retry(string DeviceId, bool OnlyWhileIdle, int Attempts, double LastAttempt, double? Deadline)
    {
        public static Retry Next(string deviceId, bool onlyWhileIdle, Retry? previous, double now)
        {
            var continues = previous is not null && previous.DeviceId == deviceId && previous.OnlyWhileIdle == onlyWhileIdle
                            && (!onlyWhileIdle || previous.Deadline is not null || now - previous.LastAttempt < 0.25);
            var attempts = continues ? Math.Min(previous!.Attempts + 1, 10) : 1;
            double? deadline = attempts < 3 ? null : now + Math.Min(30, 0.25 * (1 << (attempts - 3)));
            return new Retry(deviceId, onlyWhileIdle, attempts, now, deadline);
        }
    }
}
