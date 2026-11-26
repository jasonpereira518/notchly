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

    // Monitor (M5)
    public const string MonitorInterval = "monitorIntervalSeconds";
    public const string NetworkSpeedUnit = "networkSpeedUnit";
    public const string MonitorMemoryMetric = "monitorMemoryMetric";
    public const string MonitorShowSystem = "monitorShowSystem";
    public const string MonitorShowNetwork = "monitorShowNetwork";
    public const string MonitorShowDisk = "monitorShowDisk";
    public const string MonitorShowPower = "monitorShowPower";
    public const string MonitorGraphCPU = "monitorGraphCPU";
    public const string MonitorGraphGPU = "monitorGraphGPU";
    public const string MonitorGraphMemory = "monitorGraphMemory";
    public const string MonitorGraphNetwork = "monitorGraphNetwork";
    public const string MonitorGraphDisk = "monitorGraphDisk";
    public const string MonitorGraphBattery = "monitorGraphBattery";
    public const string MonitorGraphScale = "monitorGraphScale";
    public const string MonitorSysCPU = "monitorSysCPU";
    public const string MonitorSysCPUCores = "monitorSysCPUCores";
    public const string MonitorSysGPU = "monitorSysGPU";
    public const string MonitorSysMemory = "monitorSysMemory";
    public const string MonitorSysUptime = "monitorSysUptime";
    public const string MonitorSysConnectedDevices = "monitorSysConnectedDevices";
    public const string MonitorNetSpeed = "monitorNetSpeed";
    public const string MonitorNetTotals = "monitorNetTotals";
    public const string MonitorNetAddresses = "monitorNetAddresses";
    public const string MonitorNetTest = "monitorNetTest";
    public const string MonitorDiskUsage = "monitorDiskUsage";
    public const string MonitorDiskActivity = "monitorDiskActivity";
    public const string MonitorDiskSMART = "monitorDiskSMART";
    public const string MonitorPwrBattery = "monitorPwrBattery";
    public const string MonitorPwrTimeRemaining = "monitorPwrTimeRemaining";
    public const string MonitorPwrHealth = "monitorPwrHealth";
    public const string MonitorPwrPeripherals = "monitorPwrPeripherals";
    public const string MonitorAlertCPU = "monitorAlertCPU";
    public const string MonitorAlertMemory = "monitorAlertMemory";
    public const string MonitorAlertDisk = "monitorAlertDisk";
    public const string MonitorAlertBattery = "monitorAlertBattery";
    public const string MonitorAlertCPUThreshold = "monitorAlertCPUThreshold";
    public const string MonitorAlertDiskFreePercent = "monitorAlertDiskFreePercent";
    public const string MonitorAlertBatteryPercent = "monitorAlertBatteryPercent";
    public const string MonitorAlertCooldownMinutes = "monitorAlertCooldownMinutes";
    public const string MenuBarCPU = "menuBarCPU";
    public const string MenuBarGPU = "menuBarGPU";
    public const string MenuBarMemory = "menuBarMemory";
    public const string MenuBarNetwork = "menuBarNetwork";
    public const string MenuBarDiskUsage = "menuBarDiskUsage";
    public const string MenuBarDiskActivity = "menuBarDiskActivity";
    public const string MenuBarBattery = "menuBarBattery";
    public const string MenuBarBatteryTime = "menuBarBatteryTime";
    public const string MenuBarPeripheralBattery = "menuBarPeripheralBattery";
    public const string MenuBarConnectedDevices = "menuBarConnectedDevices";
    public const string MenuBarMetricAppearance = "menuBarMetricAppearance";
    public const string MenuBarMemoryStyle = "menuBarMemoryStyle";
    public const string MenuBarDiskStyle = "menuBarDiskStyle";
    public const string MenuBarNetworkUploadFirst = "menuBarNetworkUploadFirst";
    public const string MenuBarUsageBarMediumThreshold = "menuBarUsageBarMediumThreshold";
    public const string MenuBarUsageBarHighThreshold = "menuBarUsageBarHighThreshold";
    public const string MenuBarMetricOrder = "menuBarMetricOrder";

    // Energy (M6)
    public const string DefaultDurationMinutes = "defaultDurationMinutes";
    public const string BatteryLimitPercent = "batteryLimitPercent";
    public const string KeepAwakeAutoStart = "keepAwakeAutoStart";
    public const string KeepAwakeAllowDisplaySleep = "keepAwakeAllowDisplaySleep";
    public const string KeepAwakeExternalDisplay = "keepAwakeExternalDisplay";
    public const string KeepAwakeConnectedToPower = "keepAwakeConnectedToPower";
    public const string KeepAwakeRunningApps = "keepAwakeRunningApps";
    /// <summary>Executable names on Windows (upstream stores bundle ids under the same key).</summary>
    public const string KeepAwakeRunningAppBundleIDs = "keepAwakeRunningAppBundleIDs";
    public const string KeepAwakeAutomationRequireAll = "keepAwakeAutomationRequireAll";
    public const string KeepAwakePauseWhenLocked = "keepAwakePauseWhenLocked";
    public const string KeepAwakeSwitchUsesUntil = "keepAwakeSwitchUsesUntil";
    public const string KeepAwakeUntilTime = "keepAwakeUntilTime";
    public const string KeepAwakeMouseJiggleEnabled = "keepAwakeMouseJiggleEnabled";
    public const string KeepAwakeMouseJiggleIntervalMinutes = "keepAwakeMouseJiggleIntervalMinutes";
    public const string HotkeyEnabled = "hotkeyEnabled";
    public const string KeepAwakeShortcut = "keepAwakeShortcut";
    public const string ClamshellPreferred = "clamshellPreferred";
    public const string DimScreenOnLidClose = "dimScreenOnLidClose";
    public const string PanelShowKeepAwake = "panelShowKeepAwake";
    /// <summary>A manual session saved at quit ("end;minutes"); machine state.</summary>
    public const string KeepAwakeSessionEnd = "keepAwakeSessionEnd";
    /// <summary>The lid actions found before closed-lid mode changed them ("ac;dc"); machine state, a crash guard.</summary>
    public const string LidActionBackup = "lidActionBackup";
    /// <summary>The panel level saved while the lid is closed and dimmed; machine state, a crash guard.</summary>
    public const string LidDimSavedBrightness = "lidDimSavedBrightness";

    public const string BrightnessKeyStep = "brightnessKeyStep";
    public const string DisplayBrightnessShortcutsEnabled = "displayBrightnessShortcutsEnabled";
    public const string DisplayBrightnessDecreaseShortcut = "displayBrightnessDecreaseShortcut";
    public const string DisplayBrightnessIncreaseShortcut = "displayBrightnessIncreaseShortcut";
    public const string PanelShowBrightness = "panelShowBrightness";
    /// <summary>Displays forced onto software dimming, by display path (comma-joined); machine state.</summary>
    public const string BrightnessForcedSoftwarePaths = "brightnessForcedSoftwarePaths";
    /// <summary>Displays whose bottom quarter dims the picture below the panel's minimum (comma-joined); machine state.</summary>
    public const string BrightnessExtendedDimmingPaths = "brightnessExtendedDimmingPaths";

    public const string BluetoothSleepRestoreOnWake = "bluetoothSleepRestoreOnWake";
    /// <summary>A restore owed to the radio after sleep; machine state, never backed up.</summary>
    public const string BluetoothSleepRestorePending = "bluetoothSleepRestorePending";

    // Tools and mouse toggles (M6)
    public const string PanelShowToggles = "panelShowToggles";
    public const string PanelToggleOrder = "panelToggleOrder";
    public const string PanelToggleDarkMode = "panelToggleDarkMode";
    /// <summary>Upstream's legacy storage name for the microphone row.</summary>
    public const string PanelUtilityMicMute = "panelUtilityMicMute";
    public const string PanelToggleEmptyTrash = "panelToggleEmptyTrash";
    public const string PanelToggleQuitAllApps = "panelToggleQuitAllApps";
    public const string PanelToggleEjectDisks = "panelToggleEjectDisks";
    public const string PanelToggleHiddenFiles = "panelToggleHiddenFiles";
    public const string PanelToggleDesktopIcons = "panelToggleDesktopIcons";
    public const string PanelToggleLockScreen = "panelToggleLockScreen";
    public const string PanelToggleDisplayOff = "panelToggleDisplayOff";
    public const string PanelToggleScreenSaver = "panelToggleScreenSaver";
    // Rows added on Windows.
    public const string PanelToggleRefreshRate = "panelToggleRefreshRate";
    public const string PanelToggleWifi = "panelToggleWifi";
    public const string PanelToggleBluetooth = "panelToggleBluetooth";
    public const string DiskEjectExcludedVolumes = "diskEjectExcludedVolumes";

    public const string QuitAppsShortcutEnabled = "quitAppsShortcutEnabled";
    public const string QuitAppsShortcut = "quitAppsShortcut";
    public const string QuitAppsExceptFrontShortcutEnabled = "quitAppsExceptFrontShortcutEnabled";
    public const string QuitAppsExceptFrontShortcut = "quitAppsExceptFrontShortcut";
    /// <summary>Executable names on Windows (upstream stores bundle ids under the same key).</summary>
    public const string QuitAppsKeptApps = "quitAppsKeptApps";

    public const string KillProcessCommandBarEnabled = "killProcessCommandBarEnabled";
    public const string KillProcessGroupRelated = "killProcessGroupRelated";
    public const string KillProcessSortBy = "killProcessSortBy";
    public const string KillProcessSortAscending = "killProcessSortAscending";
    public const string PanelUtilityPortManager = "panelUtilityPortManager";

    public const string FocusFollowsMouseDelayMilliseconds = "focusFollowsMouseDelayMilliseconds";
    public const string FocusFollowsMouseRaise = "focusFollowsMouseRaise";
    public const string PanelControlFocusFollowsMouse = "panelControlFocusFollowsMouse";
    public const string PanelControlMouseAcceleration = "panelControlMouseAcceleration";
    /// <summary>What Windows had before hover activation was turned on ("tracking;raise;ms"); machine state, a crash guard.</summary>
    public const string FocusFollowsMouseOriginal = "focusFollowsMouseOriginal";
    /// <summary>The mouse parameters before acceleration was turned off ("t1;t2;accel"); machine state, a crash guard.</summary>
    public const string MouseAccelerationOriginal = "mouseAccelerationOriginal";

    // Startup (M6)
    /// <summary>Windows-only: the logon task that starts Notchly with administrator rights; machine state.</summary>
    public const string RunElevatedAtLogin = "runElevatedAtLogin";

    // Input (M7)
    /// <summary>Windows-only: the input features step aside while a full-screen app or game covers a display.</summary>
    public const string InputPauseInGames = "inputPauseInGames";

    // Windows and input (M7)
    public const string SwitcherTakeOverSystemShortcuts = "switcherTakeOverSystemShortcuts";
    public const string SwitcherShortcut = "switcherShortcut";
    public const string SwitcherWindowShortcut = "switcherWindowShortcut";
    public const string SwitcherAllWindowsShortcut = "switcherAllWindowsShortcut";
    public const string SwitcherAllWindowsShortcutEnabled = "switcherAllWindowsShortcutEnabled";
    public const string SwitcherIconRowMode = "switcherIconRowMode";
    public const string SwitcherSimpleMode = "switcherSimpleMode";
    public const string SwitcherMergeTabs = "switcherMergeTabs";
    public const string SwitcherWindowlessApps = "switcherWindowlessApps";
    public const string SwitcherMinimizedPlacement = "switcherMinimizedPlacement";
    public const string SwitcherTreatHiddenAppsLikeMinimized = "switcherTreatHiddenAppsLikeMinimized";
    public const string SwitcherShowFullscreenWindows = "switcherShowFullscreenWindows";
    public const string SwitcherAppRules = "switcherAppRules";
    public const string SwitcherCurrentDisplayOnly = "switcherCurrentDisplayOnly";
    public const string SwitcherCurrentSpaceOnly = "switcherCurrentSpaceOnly";
    public const string SwitcherSearchPinEnabled = "switcherSearchPinEnabled";
    public const string SwitcherShowShortcutHints = "switcherShowShortcutHints";
    public const string SwitcherAppearanceDelay = "switcherAppearanceDelay";
    public const string SwitcherInstantSelection = "switcherInstantSelection";
    public const string SwitcherIgnoreHoverSelection = "switcherIgnoreHoverSelection";
    public const string SwitcherScreenPlacement = "switcherScreenPlacement";
    public const string SwitcherMaxColumns = "switcherMaxColumns";
    public const string SwitcherPreviewSize = "switcherPreviewSize";
    public const string SwitcherPreviewExcludedApps = "switcherPreviewExcludedApps";
    public const string WindowLayoutHiddenActions = "windowLayoutHiddenActions";
    public const string WindowLayoutWindowGap = "windowLayoutWindowGap";
    public const string WindowLayoutScreenGap = "windowLayoutScreenGap";
    public const string WindowLayoutMarginPercent = "windowLayoutMarginPercent";
    public const string WindowLayoutSideRepeatCyclesThirds = "windowLayoutSideRepeatCyclesThirds";
    public const string WindowLayoutIgnoredApps = "windowLayoutIgnoredApps";
    public const string WindowDirectionalShortcut = "windowDirectionalShortcut";
    public const string PointerDisplayShortcut = "pointerDisplayShortcut";
    public const string WindowEdgeSnapDisabledZones = "windowEdgeSnapDisabledZones";
    public const string WindowEdgeSnapZoneActions = "windowEdgeSnapZoneActions";
    public const string WindowEdgeSnapRestoreOnDrag = "windowEdgeSnapRestoreOnDrag";
    public const string WindowGestureModifiers = "windowGestureModifiers";
    public const string WindowGestureRaiseWindow = "windowGestureRaiseWindow";
    public const string ScrollInverterExceptions = "scrollInverterExceptions";
    public const string ScrollHorizontalModifier = "scrollHorizontalModifier";
    public const string PinchZoomModifier = "pinchZoomModifier";
    public const string LinearScrollLines = "linearScrollLines";
    public const string LinearScrollExceptions = "linearScrollExceptions";
    public const string ScrollSpeedModifier = "scrollSpeedModifier";
    public const string ScrollSpeedMultiplier = "scrollSpeedMultiplier";
    public const string ScrollSpeedExceptions = "scrollSpeedExceptions";
    public const string SmoothScrollStep = "smoothScrollStep";
    public const string SmoothScrollResponse = "smoothScrollResponse";
    public const string SmoothScrollCoast = "smoothScrollCoast";
    public const string SmoothScrollExceptions = "smoothScrollExceptions";
    public const string MouseButtonShortcuts = "mouseButtonShortcuts";
    public const string MouseButtonExceptions = "mouseButtonExceptions";
    public const string MouseSpacesGestureButton = "mouseSpacesGestureButton";
    public const string MouseSpacesGestureFollowsDrag = "mouseSpacesGestureFollowsDrag";
    public const string MouseClickDebounceWindowMs = "mouseClickDebounceWindowMs";
    public const string KeyboardDebounceWindowMs = "keyboardDebounceWindowMs";
    public const string KeyboardDebounceKeyWindows = "keyboardDebounceKeyWindows";
    public const string SuperKeySource = "superKeySource";
    public const string SuperKeyModifiers = "superKeyModifiers";
    public const string SuperKeySoloAction = "superKeySoloAction";
    public const string SuperKeyExceptions = "superKeyExceptions";
    public const string SuperKeyOfficeMask = "superKeyOfficeMask";
    public const string TextSnippets = "textSnippets";
    public const string SnippetLibraryShortcut = "snippetLibraryShortcut";
    public const string QuitProtectionQuitMode = "quitProtectionQuitMode";
    public const string QuitProtectionQuitHoldDurationMs = "quitProtectionQuitHoldDurationMs";
    public const string QuitProtectionQuitDoubleIntervalMs = "quitProtectionQuitDoubleIntervalMs";
    public const string QuitProtectionQuitExtraModifier = "quitProtectionQuitExtraModifier";
    public const string QuitProtectionQuitScope = "quitProtectionQuitScope";
    public const string QuitProtectionQuitExceptions = "quitProtectionQuitExceptions";
    public const string QuitProtectionQuitShowFeedback = "quitProtectionQuitShowFeedback";
    public const string QuitProtectionCloseMode = "quitProtectionCloseMode";
    public const string QuitProtectionCloseHoldDurationMs = "quitProtectionCloseHoldDurationMs";
    public const string QuitProtectionCloseDoubleIntervalMs = "quitProtectionCloseDoubleIntervalMs";
    public const string QuitProtectionCloseExtraModifier = "quitProtectionCloseExtraModifier";
    public const string QuitProtectionCloseScope = "quitProtectionCloseScope";
    public const string QuitProtectionCloseExceptions = "quitProtectionCloseExceptions";
    public const string QuitProtectionCloseShowFeedback = "quitProtectionCloseShowFeedback";

    // MARK: Clipboard and files (M8). Upstream spellings; the shortcuts are Windows chords.
    public const string ClipboardHistoryLimit = "clipboardHistoryLimit";
    public const string ClipboardHistoryRetentionDays = "clipboardHistoryRetentionDays";
    public const string ClipboardHistoryFilterSensitiveText = "clipboardHistoryFilterSensitiveText";
    public const string ClipboardHistoryIncludeImagesFiles = "clipboardHistoryIncludeImagesFiles";
    public const string ClipboardHistoryIgnoredApps = "clipboardHistoryIgnoredApps";
    public const string ClipboardHistoryQuickPreview = "clipboardHistoryQuickPreview";
    public const string ClipboardHistoryShowSourceAppIcons = "clipboardHistoryShowSourceAppIcons";
    public const string ClipboardHistoryLayout = "clipboardHistoryLayout";
    public const string ClipboardHistoryShortcutEnabled = "clipboardHistoryShortcutEnabled";
    public const string ClipboardHistoryShortcut = "clipboardHistoryShortcut";
    public const string ClipboardHistoryWindowWidth = "clipboardHistoryWindowWidth";
    public const string ClipboardHistoryWindowHeight = "clipboardHistoryWindowHeight";
    public const string NotchClipboard = "notchClipboard";
    public const string NotchClipboardWindow = "notchClipboardWindow";
    public const string NotchClipboardCardSize = "notchClipboardCardSize";
    public const string ClipboardAutoClearOnDelay = "clipboardAutoClearOnDelay";
    public const string ClipboardAutoClearDelaySeconds = "clipboardAutoClearDelaySeconds";
    public const string ClipboardAutoClearOnSleep = "clipboardAutoClearOnSleep";
    public const string ClipboardAutoClearOnDisplaySleep = "clipboardAutoClearOnDisplaySleep";
    public const string ClipboardAutoClearOnScreenLock = "clipboardAutoClearOnScreenLock";
    public const string PastePlainShortcut = "pastePlainShortcut";
    public const string UrlCleanerCustomParameters = "urlCleanerCustomParameters";
    public const string UrlCleanerSiteParameters = "urlCleanerSiteParameters";
    public const string UrlCleanerDisabledParameters = "urlCleanerDisabledParameters";
    public const string ShelfShortcutEnabled = "shelfShortcutEnabled";
    public const string ShelfShortcut = "shelfShortcut";
    public const string ShelfShakeToOpen = "shelfShakeToOpen";
    public const string ShelfDropZoneEnabled = "shelfDropZoneEnabled";
    public const string ShelfDockPlacement = "shelfDockPlacement";
    public const string ShelfEdgeDragEnabled = "shelfEdgeDragEnabled";
    public const string ShelfClearOnClose = "shelfClearOnClose";
    public const string ShelfShortcutAddsSelection = "shelfShortcutAddsFinderSelection";
    public const string ShelfAutomaticExclusions = "shelfAutomaticExclusions";
    public const string ColorPickerShortcutEnabled = "colorPickerShortcutEnabled";
    public const string ColorPickerShortcut = "colorPickerShortcut";
    public const string ColorPickerFormat = "colorPickerFormat";
    public const string ColorPickerBareHex = "colorPickerBareHex";
    public const string ScreenOcrShortcutEnabled = "screenOCRShortcutEnabled";
    public const string ScreenOcrShortcut = "screenOCRShortcut";
    public const string ScreenOcrRemoveLineBreaks = "screenOCRRemoveLineBreaks";
    public const string ScreenOcrDetectQrCodes = "screenOCRDetectQRCodes";
}
