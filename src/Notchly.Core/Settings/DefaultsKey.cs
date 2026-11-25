// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/Defaults.swift (enum DefaultsKey).
// Keys keep their upstream spelling verbatim; keys that only exist on Windows are marked.
// Keys are added here as features are ported.
namespace Notchly.Core.Settings;

public static class DefaultsKey
{
    // MARK: App
    public const string HasOnboarded = "hasOnboarded";
    public const string OnboardingStep = "onboardingStep";
    public const string PanelSectionOrder = "panelSectionOrder";
    public const string PanelCollapsedSections = "panelCollapsedSections";
    public const string LaunchAtLogin = "launchAtLogin";                 // Windows-only (HKCU Run value)
    public const string AppTheme = "appTheme";                           // Windows-only: system|light|dark
    public const string IslandSwitchesInstalled = "islandSwitchesInstalled";  // Windows-only: island switches turned on before they installed their extension were honoured
    public const string PortedFeaturesSeen = "portedFeaturesSeen";       // Windows-only: features known to be installable

    public static string FeatureAvailable(string featureId) => "featureAvailable." + featureId;

    // MARK: Feature enable switches (FeatureCatalog.enabledKeys)
    public const string SwitcherEnabled = "switcherEnabled";
    public const string DockPreviewEnabled = "dockPreviewEnabled";
    public const string DockClickMinimize = "dockClickMinimize";
    public const string DockClickHide = "dockClickHide";
    public const string DockClickCycleWindows = "dockClickCycleWindows";
    public const string WindowMaximizeEnabled = "windowMaximizeEnabled";
    public const string AutoQuitEnabled = "autoQuitEnabled";
    public const string SpacesOrderEnabled = "spacesOrderEnabled";
    public const string ScrollInverterEnabled = "scrollInverterEnabled";
    public const string ScrollInverterHorizontalEnabled = "scrollInverterHorizontalEnabled";
    public const string ScrollHorizontalEnabled = "scrollHorizontalEnabled";
    public const string PinchZoomEnabled = "pinchZoomEnabled";
    public const string FocusFollowsMouseEnabled = "focusFollowsMouseEnabled";
    public const string SmoothScrollEnabled = "smoothScrollEnabled";
    public const string LinearScrollEnabled = "linearScrollEnabled";
    public const string ScrollSpeedEnabled = "scrollSpeedEnabled";
    public const string MouseAccelerationDisabled = "mouseAccelerationDisabled";
    public const string MouseNavigationEnabled = "mouseNavigationEnabled";
    public const string MouseButtonShortcutsEnabled = "mouseButtonShortcutsEnabled";
    public const string MouseSpacesGestureEnabled = "mouseSpacesGestureEnabled";
    public const string MiddleClickEnabled = "middleClickEnabled";
    public const string KeyboardDebounceEnabled = "keyboardDebounceEnabled";
    public const string QuitProtectionQuitEnabled = "quitProtectionQuitEnabled";
    public const string QuitProtectionCloseEnabled = "quitProtectionCloseEnabled";
    public const string TextSnippetsEnabled = "textSnippetsEnabled";
    public const string SnippetLibraryEnabled = "snippetLibraryEnabled";
    public const string SuperKeyEnabled = "superKeyEnabled";
    public const string MouseClickDebounceEnabled = "mouseClickDebounceEnabled";
    public const string RadialMenuEnabled = "radialMenuEnabled";
    public const string ClipboardHistoryEnabled = "clipboardHistoryEnabled";
    public const string PastePlainEnabled = "pastePlainEnabled";
    public const string FinderCutPasteEnabled = "finderCutPasteEnabled";
    public const string FinderPasteImageAsFile = "finderPasteImageAsFile";
    public const string FinderRenameEnabled = "finderRenameEnabled";
    public const string ShelfEnabled = "shelfEnabled";
    public const string ShelfRemoveAfterDrop = "shelfRemoveAfterDrop";
    public const string ShelfCloseAfterDrop = "shelfCloseAfterDrop";
    public const string NotchShelf = "notchShelf";
    public const string NotchDragReveal = "notchDragReveal";
    public const string UrlCleanerEnabled = "urlCleanerEnabled";
    public const string SoundOutputSwitcherEnabled = "soundOutputSwitcherEnabled";
    public const string AudioPriorityOutputEnabled = "audioPriorityOutputEnabled";
    public const string AudioPriorityInputEnabled = "audioPriorityInputEnabled";
    public const string MusicBlockEnabled = "musicBlockEnabled";
    public const string BrightnessControlEnabled = "brightnessControlEnabled";
    public const string ExtraBrightnessEnabled = "extraBrightnessEnabled";
    public const string BluetoothSleepEnabled = "bluetoothSleepEnabled";
    public const string WindowLayoutShortcutsEnabled = "windowLayoutShortcutsEnabled";
    public const string WindowDirectionalEnabled = "windowDirectionalEnabled";
    public const string PointerDisplayEnabled = "pointerDisplayEnabled";
    public const string WindowEdgeSnapEnabled = "windowEdgeSnapEnabled";
    public const string WindowGestureEnabled = "windowGestureEnabled";

    // MARK: Dynamic Island: extensions' switches
    public const string NotchEnabled = "notchEnabled";
    public const string NotchGesturesEnabled = "notchGesturesEnabled";
    public const string NotchTimerEnabled = "notchTimerEnabled";
    public const string NotchAccessoriesEnabled = "notchAccessoriesEnabled";
    public const string NotchLyricsEnabled = "notchLyricsEnabled";
    public const string NotchLyricsOnline = "notchLyricsOnline";
    public const string NotchQueueEnabled = "notchQueueEnabled";
    /// <summary>Windows-only: songs starred inside Notchly (JSON), for players with no library of their own.</summary>
    public const string NotchMusicLocalStars = "notchMusicLocalStars";
    /// <summary>Windows-only: the Client ID of the user's own Spotify developer app (not a secret).</summary>
    public const string NotchSpotifyClientId = "notchSpotifyClientId";
    public const string NotchLiveEqualizer = "notchLiveEqualizer";
    public const string NotchDownloadsEnabled = "notchDownloadsEnabled";
    /// <summary>Windows-only: the top strip shows the app in front and its menus, as a Mac menu bar.</summary>
    public const string NotchStripMenus = "notchStripMenus";
    /// <summary>Windows-only: the top strip shows the clock and date at its right end.</summary>
    public const string NotchStripClock = "notchStripClock";
    /// <summary>Windows-only: the top strip takes the colour along the top of the app in front.</summary>
    public const string NotchStripMatchesApp = "notchStripMatchesApp";
    /// <summary>The folder Downloads watches; empty for Windows' Downloads folder (upstream keeps a bookmark).</summary>
    public const string NotchDownloadsFolderPath = "notchDownloadsFolderPath";
    public const string NotchNotificationsEnabled = "notchNotificationsEnabled";
    /// <summary>Removes the original from Windows once the island shows it (upstream hides the macOS banner).</summary>
    public const string NotchDismissNativeNotifications = "notchDismissNativeNotifications";
    public const string NotchCameraEnabled = "notchCameraEnabled";
    /// <summary>The camera the island's mirror opens, by Windows device id; machine state (upstream lets macOS remember it).</summary>
    public const string NotchCameraDevice = "notchCameraDevice";
    public const string NotchCalendarEnabled = "notchCalendarEnabled";
    public const string NotchCalendarCountdown = "notchCalendarCountdown";
    /// <summary>Minutes before a start the countdown appears, chosen events included.</summary>
    public const string NotchCalendarCountdownLead = "notchCalendarCountdownLead";
    /// <summary>The event under way counts down to its end.</summary>
    public const string NotchCalendarTimeLeft = "notchCalendarTimeLeft";
    public const string NotchCalendarWeekNumbers = "notchCalendarWeekNumbers";
    /// <summary>Calendars (feed ids) left out of the island; stored as exclusions so new ones start shown.</summary>
    public const string NotchCalendarExcluded = "notchCalendarExcluded";
    /// <summary>Countdown keys of events chosen from their menu, each with its end (JSON; upstream keeps a dictionary).</summary>
    public const string NotchCalendarChosenCountdowns = "notchCalendarChosenCountdowns";
    public const string NotchAgentsEnabled = "notchAgentsEnabled";
    // AI agents: the agents read, the page's cards and period, the closed island's reading and the alerts
    // (upstream Defaults.swift's notchAgents* keys; Cursor is a Windows addition).
    public const string NotchAgentsClaude = "notchAgentsClaude";
    public const string NotchAgentsCodex = "notchAgentsCodex";
    public const string NotchAgentsOpenCode = "notchAgentsOpenCode";
    public const string NotchAgentsCopilot = "notchAgentsCopilot";
    public const string NotchAgentsCursor = "notchAgentsCursor";
    /// <summary>The cards in the person's order, comma-separated raw values.</summary>
    public const string NotchAgentsCardOrder = "notchAgentsCardOrder";
    /// <summary>The cards left out, comma-separated raw values.</summary>
    public const string NotchAgentsHiddenCards = "notchAgentsHiddenCards";
    /// <summary>"today", "week" (7 days) or "month" (30 days).</summary>
    public const string NotchAgentsPeriod = "notchAgentsPeriod";
    /// <summary>"remaining" or "used".</summary>
    public const string NotchAgentsLimitDisplay = "notchAgentsLimitDisplay";
    /// <summary>"mostUsed", "session" or "weekly".</summary>
    public const string NotchAgentsLimitFocus = "notchAgentsLimitFocus";
    public const string NotchAgentsLiveActivity = "notchAgentsLiveActivity";
    /// <summary>"elapsed", "tokens", "cost" or "limit".</summary>
    public const string NotchAgentsReadout = "notchAgentsReadout";
    public const string NotchAgentsFinishAlert = "notchAgentsFinishAlert";
    /// <summary>Seconds a turn lasts before its end earns a notice.</summary>
    public const string NotchAgentsFinishMinimum = "notchAgentsFinishMinimum";
    public const string NotchAgentsLimitAlert = "notchAgentsLimitAlert";
    /// <summary>Percent used that earns a warning.</summary>
    public const string NotchAgentsLimitThreshold = "notchAgentsLimitThreshold";
    /// <summary>US dollars of API value a day; 0 is off.</summary>
    public const string NotchAgentsDailyBudget = "notchAgentsDailyBudget";
    public const string NotchAgentsPriceUpdates = "notchAgentsPriceUpdates";
    public const string NotchWatchEnabled = "notchWatchEnabled";
    public const string NotchWatchSound = "notchWatchSound";
    /// <summary>The last rule chosen: changes, settles, contains or reaches.</summary>
    public const string NotchWatchCondition = "notchWatchCondition";
    public const string NotchMascotEnabled = "notchMascotEnabled";
    /// <summary>Passes through the island now and then.</summary>
    public const string NotchMascotVisits = "notchMascotVisits";
    /// <summary>Comes out to react to what the island sees.</summary>
    public const string NotchMascotReactions = "notchMascotReactions";
    /// <summary>"minimal" or "robot".</summary>
    public const string NotchMascotStyle = "notchMascotStyle";
    /// <summary>"ball", "egg", "squircle" or "pill".</summary>
    public const string NotchMascotShape = "notchMascotShape";
    /// <summary>"pearl", "lemon", "peach", "rose", "lilac", "sky" or "mint".</summary>
    public const string NotchMascotPalette = "notchMascotPalette";
    /// <summary>"left" or "right" of the camera.</summary>
    public const string NotchMascotSide = "notchMascotSide";
    /// <summary>"rare", "normal" or "frequent".</summary>
    public const string NotchMascotVisitFrequency = "notchMascotVisitFrequency";
    /// <summary>Hops into the island after a quiet while.</summary>
    public const string NotchMascotHidesWhenIdle = "notchMascotHidesWhenIdle";

    // MARK: Dynamic Island: shape and placement
    public const string NotchDisplay = "notchDisplay";
    public const string NotchSilhouette = "notchSilhouette";
    public const string NotchSize = "notchSize";
    public const string NotchCustomWidth = "notchCustomWidth";
    public const string NotchCustomHeight = "notchCustomHeight";
    public const string NotchCapsuleFitWidth = "notchCapsuleFitWidth";
    public const string NotchCapsuleFitHeight = "notchCapsuleFitHeight";
    public const string NotchCapsuleFitDrop = "notchCapsuleFitDrop";
    public const string NotchHeight = "notchHeight";                     // Windows-only: stands in for the menu bar height
    public const string NotchOffsetX = "notchOffsetX";                   // Windows-only: centre on an off-centre webcam
    public const string NotchReserveStrip = "notchReserveStrip";         // Windows-only: AppBar strip along the top edge
    public const string NotchShowInCaptures = "notchShowInCaptures";

    // MARK: Dynamic Island: behaviour
    public const string NotchOpenOnHover = "notchOpenOnHover";
    public const string NotchHoverExpands = "notchHoverExpands";
    public const string NotchHoverDelay = "notchHoverDelay";
    public const string NotchCloseDelay = "notchCloseDelay";
    public const string NotchHideInFullscreen = "notchHideInFullscreen";
    public const string NotchHideUntilHover = "notchHideUntilHover";
    public const string NotchHiddenModules = "notchHiddenModules";
    public const string NotchModuleOrder = "notchModuleOrder";
    public const string NotchHomeModule = "notchHomeModule";
    public const string NotchIdleContent = "notchIdleContent";

    // MARK: Dynamic Island: music and notices
    public const string NotchShowPlayingMusic = "notchShowPlayingMusic";
    public const string NotchIncludeOtherPlayers = "notchIncludeOtherPlayers";
    public const string NotchPreferredPlayer = "notchPreferredPlayer";
    public const string NotchMusicShowVolume = "notchMusicShowVolume";
    public const string NotchTrackChange = "notchTrackChange";
    public const string NotchVolume = "notchVolume";
    public const string NotchBrightness = "notchBrightness";
    public const string NotchMicrophone = "notchMicrophone";
    public const string NotchBattery = "notchBattery";
    public const string NotchLowBatteryTint = "notchLowBatteryTint";
    public const string NotchLowBatteryThreshold = "notchLowBatteryThreshold";
    public const string NotchLowBatteryEarly = "notchLowBatteryEarly";
    public const string NotchLowBatteryEarlyThreshold = "notchLowBatteryEarlyThreshold";
    public const string NotchSwallowVolumeKeys = "notchSwallowVolumeKeys"; // Windows-only: own the volume keys (no Windows flyout)
    public const string NotchKeepAwakeActivity = "notchKeepAwakeActivity";

    // MARK: Dynamic Island: timer
    public const string NotchTimerMode = "notchTimerMode";
    public const string NotchTimerMinutes = "notchTimerMinutes";
    public const string NotchTimerSoundEnabled = "notchTimerSoundEnabled";
    public const string NotchHideTimerCountdown = "notchHideTimerCountdown";
    public const string NotchPomodoroFocusMinutes = "notchPomodoroFocusMinutes";
    public const string NotchPomodoroShortBreakMinutes = "notchPomodoroShortBreakMinutes";
    public const string NotchPomodoroLongBreakMinutes = "notchPomodoroLongBreakMinutes";
    public const string NotchPomodoroLongBreakInterval = "notchPomodoroLongBreakInterval";
    public const string NotchPomodoroTotalSessions = "notchPomodoroTotalSessions";

    // Sound (M4)
    public const string AppVolumes = "appVolumes";
    public const string AppOutputDevices = "appOutputDevices";
    public const string MixerAppArrangement = "mixerAppArrangement";
    public const string MixerHiddenApps = "mixerHiddenApps";
    public const string MixerShowSystemSounds = "mixerShowFinder";
    public const string MixerHideInactiveApps = "mixerHideInactiveApps";
    public const string MixerCallAppVolumeEnabled = "mixerCallAppVolumeEnabled";
    public const string MonitorShowMixer = "monitorShowMixer";
    public const string MixerLowerVolumeOnHeadphonesDisconnect = "mixerLowerVolumeOnHeadphonesDisconnect";
    public const string MixerHeadphonesDisconnectVolumePercent = "mixerHeadphonesDisconnectVolumePercent";
    public const string SoundOutputSwitcherShortcut = "soundOutputSwitcherShortcut";
    public const string SoundOutputSwitcherDeviceUIDs = "soundOutputSwitcherDeviceUIDs";
    public const string AudioPriorityOutputUIDs = "audioPriorityOutputUIDs";
    public const string AudioPriorityInputUIDs = "audioPriorityInputUIDs";
    public const string AudioPriorityDeviceNames = "audioPriorityDeviceNames";
    public const string MicMuteShortcutEnabled = "micMuteShortcutEnabled";
    public const string MicMuteShortcut = "micMuteShortcut";
    public const string MicMuteMenuBarIndicator = "micMuteMenuBarIndicator";
    public const string MicMuteActive = "micMuteActive";
    public const string MicMuteSavedVolumes = "micMuteSavedVolumes";
    public const string MicMuteMutedDevices = "micMuteMutedDevices";
    public const string MicMuteSavedVolume = "micMuteSavedVolume";
}
