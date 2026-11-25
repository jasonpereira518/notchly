// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/Defaults.swift (registeredDefaults). Values are kept
// verbatim unless marked as a Windows deviation.
using Notchly.Core.Features;
using Notchly.Core.Notch;

namespace Notchly.Core.Settings;

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
        [DefaultsKey.NotchMusicLocalStars] = "",
        // Windows-only default: on. Upstream starts off because macOS asks for audio recording
        // permission; system loopback asks for nothing.
        [DefaultsKey.NotchLiveEqualizer] = true,
        [DefaultsKey.NotchDownloadsEnabled] = true,
        [DefaultsKey.NotchStripMenus] = true,
        [DefaultsKey.NotchStripClock] = true,
        [DefaultsKey.NotchStripMatchesApp] = true,
        [DefaultsKey.NotchDownloadsFolderPath] = "",
        [DefaultsKey.NotchNotificationsEnabled] = true,
        [DefaultsKey.NotchDismissNativeNotifications] = false,
        [DefaultsKey.NotchCameraEnabled] = true,
        [DefaultsKey.NotchCameraDevice] = "",
        [DefaultsKey.NotchCalendarEnabled] = true,
        [DefaultsKey.NotchCalendarCountdown] = false,
        [DefaultsKey.NotchCalendarCountdownLead] = Notchly.Core.Calendar.CalendarSupport.DefaultCountdownLeadMinutes,
        [DefaultsKey.NotchCalendarTimeLeft] = false,
        [DefaultsKey.NotchCalendarWeekNumbers] = false,
        [DefaultsKey.NotchCalendarExcluded] = "",
        [DefaultsKey.NotchCalendarChosenCountdowns] = "",
        [DefaultsKey.NotchAgentsEnabled] = false,
        [DefaultsKey.NotchAgentsClaude] = true,
        [DefaultsKey.NotchAgentsCodex] = true,
        [DefaultsKey.NotchAgentsOpenCode] = true,
        [DefaultsKey.NotchAgentsCopilot] = true,
        [DefaultsKey.NotchAgentsCursor] = true,
        [DefaultsKey.NotchAgentsCardOrder] = "",
        [DefaultsKey.NotchAgentsHiddenCards] = "",
        [DefaultsKey.NotchAgentsPeriod] = "today",
        [DefaultsKey.NotchAgentsLimitDisplay] = "remaining",
        [DefaultsKey.NotchAgentsLimitFocus] = "mostUsed",
        [DefaultsKey.NotchAgentsLiveActivity] = true,
        [DefaultsKey.NotchAgentsReadout] = "elapsed",
        [DefaultsKey.NotchAgentsFinishAlert] = true,
        [DefaultsKey.NotchAgentsFinishMinimum] = 60.0,
        [DefaultsKey.NotchAgentsLimitAlert] = true,
        [DefaultsKey.NotchAgentsLimitThreshold] = 80.0,
        [DefaultsKey.NotchAgentsDailyBudget] = 0.0,
        [DefaultsKey.NotchAgentsPriceUpdates] = true,
        [DefaultsKey.NotchWatchEnabled] = true,
        [DefaultsKey.NotchWatchSound] = true,
        [DefaultsKey.NotchWatchCondition] = "changes",
        [DefaultsKey.NotchMascotEnabled] = false,
        [DefaultsKey.NotchMascotVisits] = true,
        [DefaultsKey.NotchMascotReactions] = true,
        [DefaultsKey.NotchMascotStyle] = "minimal",
        [DefaultsKey.NotchMascotShape] = "ball",
        [DefaultsKey.NotchMascotPalette] = "pearl",
        [DefaultsKey.NotchMascotSide] = "left",
        [DefaultsKey.NotchMascotVisitFrequency] = "normal",
        [DefaultsKey.NotchMascotHidesWhenIdle] = true,

        // Sound (upstream defaults; lists and maps are JSON, shortcuts use Windows modifiers: Win in place of Command).
        [DefaultsKey.AppVolumes] = new Dictionary<string, double>(),
        [DefaultsKey.AppOutputDevices] = new Dictionary<string, string>(),
        [DefaultsKey.MixerAppArrangement] = "",
        [DefaultsKey.MixerHiddenApps] = new Dictionary<string, string>(),
        [DefaultsKey.MixerShowSystemSounds] = true,
        [DefaultsKey.MixerHideInactiveApps] = false,
        [DefaultsKey.MixerCallAppVolumeEnabled] = false,
        [DefaultsKey.MonitorShowMixer] = true,
        [DefaultsKey.MixerLowerVolumeOnHeadphonesDisconnect] = false,
        [DefaultsKey.MixerHeadphonesDisconnectVolumePercent] = 25,
        [DefaultsKey.SoundOutputSwitcherEnabled] = false,
        [DefaultsKey.SoundOutputSwitcherShortcut] = "control+alt+win:83",
        [DefaultsKey.SoundOutputSwitcherDeviceUIDs] = new List<string>(),
        // Ships uninstalled, but both directions are ready on first install.
        [DefaultsKey.AudioPriorityOutputEnabled] = true,
        [DefaultsKey.AudioPriorityInputEnabled] = true,
        [DefaultsKey.AudioPriorityOutputUIDs] = new List<string>(),
        [DefaultsKey.AudioPriorityInputUIDs] = new List<string>(),
        [DefaultsKey.AudioPriorityDeviceNames] = new Dictionary<string, string>(),
        [DefaultsKey.MicMuteShortcutEnabled] = false,
        [DefaultsKey.MicMuteShortcut] = "control+alt+win:77",
        [DefaultsKey.MicMuteMenuBarIndicator] = true,
        [DefaultsKey.MicMuteActive] = false,
        [DefaultsKey.MicMuteSavedVolumes] = new Dictionary<string, double>(),
        [DefaultsKey.MicMuteMutedDevices] = new List<string>(),
        [DefaultsKey.MicMuteSavedVolume] = 0.75,
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
