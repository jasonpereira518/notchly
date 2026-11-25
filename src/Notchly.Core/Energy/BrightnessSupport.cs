// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Display/BrightnessSupport.swift and Services/Bluetooth/BluetoothSleepSupport.swift:
// the level model, key steps, extended dimming, software dimming and the DDC value math, and the Bluetooth
// sleep plan, kept apart from the device calls so tests can pin them down.
// Windows differences: software dimming draws an opacity overlay (Windows rejects dark gamma ramps), so the
// factor maps to an overlay opacity; there is no per-display power switch.
namespace Notchly.Core.Energy;

public enum BrightnessKeyStep { Standard, Half, Quarter }

public static class BrightnessSupport
{
    /// <summary>Slider levels run 0 to 1.</summary>
    public static double Clamp(double level) => double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;

    public const int Segments = 16;
    /// <summary>The bottom quarter of the slider dims the picture below the panel's own minimum.</summary>
    public const double ExtendedDimmingRange = 0.25;
    public const double ReconnectFloor = 0.25;
    /// <summary>The overlay never goes fully opaque, so a dimmed screen can always be read to bring it back.</summary>
    public const double MaxOverlayOpacity = 0.95;

    public const double LevelTrustSeconds = 3;
    public const double WakeSettleSeconds = 3;
    public const int CommandIntervalMilliseconds = 50;
    public static readonly int[] DiscoveryRetrySeconds = [2, 5, 15];
    public const double RebuildDebounceSeconds = 0.5;

    // MARK: Steps

    public static double Fraction(BrightnessKeyStep step) => step switch
    {
        BrightnessKeyStep.Half => 1.0 / 32,
        BrightnessKeyStep.Quarter => 1.0 / 64,
        _ => 1.0 / 16,
    };

    /// <summary>"standard", "half" or "quarter"; anything else is standard.</summary>
    public static BrightnessKeyStep ParseStep(string? raw) => raw?.ToLowerInvariant() switch
    {
        "half" => BrightnessKeyStep.Half,
        "quarter" => BrightnessKeyStep.Quarter,
        _ => BrightnessKeyStep.Standard,
    };

    public static string StepName(BrightnessKeyStep step) => step.ToString().ToLowerInvariant();

    /// <summary>A press is clamped to the chosen step, and an already finer step is never made coarser.</summary>
    public static double Limited(BrightnessKeyStep step, double delta)
    {
        var fraction = Fraction(step);
        return Math.Sign(delta) * Math.Min(Math.Abs(delta), fraction);
    }

    public static double SteppedLevel(double current, double delta) => Clamp(current + delta);

    public static int WholePercent(double level) => (int)Math.Round(Clamp(level) * 100, MidpointRounding.AwayFromZero);

    /// <summary>Meter segments to fill: rounded up, so any non-zero level shows at least one, and exact 0 shows none.</summary>
    public static int FilledSegments(double level)
    {
        var l = Clamp(level);
        return l <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(l * Segments), 1, Segments);
    }

    // MARK: Extended dimming

    public readonly record struct Components(double Hardware, double Picture);

    /// <summary>Below a quarter the panel stays at its minimum and the picture dims; above it the panel brightens.</summary>
    public static Components ExtendedDimming(double level)
    {
        var l = Clamp(level);
        return l < ExtendedDimmingRange ? new Components(0, l / ExtendedDimmingRange) : new Components((l - ExtendedDimmingRange) / (1 - ExtendedDimmingRange), 1);
    }

    /// <summary>The slider level for a panel and picture pair: the inverse of <see cref="ExtendedDimming"/>.</summary>
    public static double LevelFrom(double hardware, double picture) =>
        picture < 1 ? Clamp(picture) * ExtendedDimmingRange : ExtendedDimmingRange + Clamp(hardware) * (1 - ExtendedDimmingRange);

    // MARK: Software dimming

    /// <summary>A display coming back after a reconnect never returns looking dead.</summary>
    public static double ReconnectedDimLevel(double saved) => Math.Max(Math.Min(saved, 1), ReconnectFloor);

    public static double SoftwareDimFactor(double level) => Clamp(level);

    /// <summary>Only a dim this app applied is remembered; anything else restores to full.</summary>
    public static double SoftwareDimToRestore(double remembered, bool appliedByApp) => appliedByApp ? remembered : 1.0;

    /// <summary>The overlay's opacity for a dim factor: clear at 1, never past <see cref="MaxOverlayOpacity"/>.</summary>
    public static double OverlayOpacity(double factor) => Math.Min(1 - SoftwareDimFactor(factor), MaxOverlayOpacity);

    // MARK: DDC

    /// <summary>A monitor that reports no maximum is treated as 0 to 100.</summary>
    public static int EffectiveMaximum(int reportedMax) => reportedMax > 0 ? reportedMax : 100;

    public static int DeviceValue(double level, int reportedMax) => (int)Math.Round(Clamp(level) * EffectiveMaximum(reportedMax), MidpointRounding.AwayFromZero);

    public static double Normalized(int current, int reportedMax) => Clamp((double)current / EffectiveMaximum(reportedMax));

    /// <summary>A remembered level is believed for a few seconds; older, from the future or unknown, the monitor is read again.</summary>
    public static bool TrustsRememberedLevel(double? ageSeconds) => ageSeconds is { } age && age >= 0 && age <= LevelTrustSeconds;

    /// <summary>A zero or unreadable internal panel is never remembered as its level.</summary>
    public static bool ShouldRememberInternalLevel(double? level) => level is > 0;

    // MARK: Targeting

    /// <summary>
    /// The display a shortcut acts on: the one under the pointer when that is how it is set up, otherwise the
    /// primary one; nothing if that display cannot be controlled.
    /// </summary>
    public static string? ShortcutDisplay(bool followsPointer, string? pointerDisplay, string? primaryDisplay, IReadOnlyCollection<string> eligible)
    {
        var target = followsPointer ? pointerDisplay ?? primaryDisplay : primaryDisplay;
        return target is not null && eligible.Contains(target) ? target : null;
    }
}

/// <summary>Port of BluetoothSleepSupport.swift.</summary>
public static class BluetoothSleepSupport
{
    public readonly record struct Plan(bool PowersOff, bool OwesRestore);

    /// <summary>Put back only what was taken away: an already-off radio is left off and owes nothing.</summary>
    public static Plan SleepPlan(bool isPoweredOn, bool restoresOnWake) => new(isPoweredOn, isPoweredOn && restoresOnWake);

    /// <summary>
    /// A restore is paid only when it is owed and the radio is off now. The radio is read only when owed, so a
    /// launch that owes nothing never touches the controller.
    /// </summary>
    public static bool Restores(bool owesRestore, Func<bool> isPoweredOn) => owesRestore && !isPoweredOn();
}
