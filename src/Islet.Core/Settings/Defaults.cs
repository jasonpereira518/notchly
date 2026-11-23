// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/Defaults.swift (registeredDefaults). Values are kept
// verbatim unless marked as a Windows deviation.
using Islet.Core.Features;
using Islet.Core.Notch;

namespace Islet.Core.Settings;

public static class Defaults
{
    public static IEnumerable<KeyValuePair<string, object?>> Registered
    {
        get
        {
            foreach (var pair in FeatureCatalog.AvailabilityDefaults) yield return pair;
            foreach (var pair in Values) yield return new KeyValuePair<string, object?>(pair.Key, pair.Value);
        }
    }

    private static readonly Dictionary<string, object?> Values = new()
    {
        [DefaultsKey.HasOnboarded] = false,
        [DefaultsKey.OnboardingStep] = 0,
        [DefaultsKey.PanelSectionOrder] = "",
        [DefaultsKey.PanelCollapsedSections] = "",
        [DefaultsKey.LaunchAtLogin] = false,
        [DefaultsKey.AppTheme] = "system",

        // Island extensions' own switches
        [DefaultsKey.NotchGesturesEnabled] = true,
        [DefaultsKey.NotchTimerEnabled] = true,
        [DefaultsKey.NotchAccessoriesEnabled] = true,
        [DefaultsKey.NotchLyricsEnabled] = true,
        // Online lookup needs consent: off until the user turns it on (upstream default).
        [DefaultsKey.NotchLyricsOnline] = false,
        // Files on the island and the shelf behind it (upstream defaults).
        [DefaultsKey.NotchShelf] = true,
        [DefaultsKey.NotchDragReveal] = true,
        [DefaultsKey.ShelfRemoveAfterDrop] = true,
        [DefaultsKey.ShelfCloseAfterDrop] = false,
        [DefaultsKey.NotchQueueEnabled] = true,
        [DefaultsKey.NotchLiveEqualizer] = false,
        [DefaultsKey.NotchDownloadsEnabled] = true,
        [DefaultsKey.NotchStripMenus] = true,
        [DefaultsKey.NotchStripClock] = true,
        [DefaultsKey.NotchDownloadsFolderPath] = "",
        [DefaultsKey.NotchNotificationsEnabled] = true,
        [DefaultsKey.NotchCalendarEnabled] = true,
        [DefaultsKey.NotchAgentsEnabled] = false,
        [DefaultsKey.NotchWatchEnabled] = true,
        [DefaultsKey.NotchMascotEnabled] = false,
        [DefaultsKey.NotchEnabled] = false,

        // Shape and placement
        [DefaultsKey.NotchDisplay] = nameof(NotchDisplay.Automatic).ToLowerInvariant(),
        // Windows deviation: upstream defaults to a capsule on Macs without a notch; this port
        // exists to give a notch-less laptop its own notch, so the cutout hanging from the top
        // edge under the webcam is the default.
        [DefaultsKey.NotchSilhouette] = "notch",
        [DefaultsKey.NotchSize] = "spacious",
        [DefaultsKey.NotchCustomWidth] = NotchSizes.DefaultWidth,
        [DefaultsKey.NotchCustomHeight] = NotchSizes.DefaultHeight,
        [DefaultsKey.NotchCapsuleFitWidth] = 0.0,
        [DefaultsKey.NotchCapsuleFitHeight] = 0.0,
        [DefaultsKey.NotchCapsuleFitDrop] = 0.0,
        [DefaultsKey.NotchHeight] = 24.0,
        [DefaultsKey.NotchOffsetX] = 0.0,
        [DefaultsKey.NotchReserveStrip] = false,
        [DefaultsKey.NotchShowInCaptures] = true,

        // Behaviour
        [DefaultsKey.NotchOpenOnHover] = false,
        [DefaultsKey.NotchHoverExpands] = true,
        [DefaultsKey.NotchHoverDelay] = NotchSupport.DefaultHoverDelay,
        [DefaultsKey.NotchCloseDelay] = NotchSupport.DefaultCloseDelay,
        // Windows deviation: upstream defaults to false. A topmost island over a borderless
        // full-screen game on a gaming laptop is worse than a hidden one.
        [DefaultsKey.NotchHideInFullscreen] = true,
        [DefaultsKey.NotchHideUntilHover] = false,
        [DefaultsKey.NotchHiddenModules] = "",
        [DefaultsKey.NotchModuleOrder] = "",
        [DefaultsKey.NotchHomeModule] = "controls",
        [DefaultsKey.NotchIdleContent] = "music",

        // Music and notices
        [DefaultsKey.NotchShowPlayingMusic] = true,
        [DefaultsKey.NotchIncludeOtherPlayers] = true,
        [DefaultsKey.NotchPreferredPlayer] = "",
        [DefaultsKey.NotchMusicShowVolume] = false,
        [DefaultsKey.NotchTrackChange] = true,
        [DefaultsKey.NotchVolume] = true,
        [DefaultsKey.NotchBrightness] = true,
        [DefaultsKey.NotchMicrophone] = true,
        [DefaultsKey.NotchBattery] = true,
        [DefaultsKey.NotchLowBatteryTint] = false,
        [DefaultsKey.NotchLowBatteryThreshold] = NotchSupport.DefaultLowBatteryThreshold,
        [DefaultsKey.NotchLowBatteryEarly] = false,
        [DefaultsKey.NotchLowBatteryEarlyThreshold] = NotchSupport.DefaultEarlyBatteryThreshold,
        [DefaultsKey.NotchSwallowVolumeKeys] = false,
        [DefaultsKey.NotchKeepAwakeActivity] = false,

        // Timer
        [DefaultsKey.NotchTimerMode] = "timer",
        [DefaultsKey.NotchTimerMinutes] = 15,
        [DefaultsKey.NotchTimerSoundEnabled] = true,
        [DefaultsKey.NotchHideTimerCountdown] = false,
        [DefaultsKey.NotchPomodoroFocusMinutes] = 25,
        [DefaultsKey.NotchPomodoroShortBreakMinutes] = 5,
        [DefaultsKey.NotchPomodoroLongBreakMinutes] = 15,
        [DefaultsKey.NotchPomodoroLongBreakInterval] = 4,
        [DefaultsKey.NotchPomodoroTotalSessions] = 4,
    };
}
