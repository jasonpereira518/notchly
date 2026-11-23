// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/FeatureCatalog.swift (enum AppFeature, FeatureGroup).
namespace Notchly.Core.Features;

/// <summary>
/// Every feature the Features hub can switch off entirely. The string id (see
/// <see cref="FeatureCatalog.Id"/>) is the upstream raw value and is persisted in the
/// availability key, so cases can be added but never renamed.
/// </summary>
public enum AppFeature
{
    // Windows and Dock
    Switcher, DockPreview, DockClick, WindowMaximizer, WindowLayout, AutoQuit, SpacesOrder,
    // Mouse and keyboard
    ScrollInverter, ScrollHorizontal, ScrollZoom, FocusFollowsMouse, SmoothScroll, LinearScroll, ScrollSpeed,
    MouseAcceleration, MouseNavigation, MouseButtonShortcuts, MiddleClick, MouseClickDebounce, KeyboardDebounce,
    TextSnippets, SuperKey, QuitWindowProtection,
    // Clipboard and files
    ClipboardHistory, PastePlain, FinderCutPaste, FinderRename, Shelf, UrlCleaner, DiskImageInstaller,
    // Sound
    Mixer, SoundOutputSwitcher, AudioPriority, MicMute, MusicBlock,
    // Energy and display
    KeepAwake, Brightness, ExtraBrightness, BluetoothSleep,
    // Tools
    QuickLauncher, QuickToggles, QuitApps, ColorPicker, ScreenOCR, CleaningMode, MediaTools, Cleaner, Uninstaller,
    Homebrew, AppUpdates, Screenshot, CameraPreview, RadialMenu, Scratchpad, CommandBar, ScreenRecorder, Wallpaper,
    KillProcess, PortManager,
    // Dynamic Island, then its extensions
    Notch, NotchCalendar, NotchNotifications, NotchGestures, NotchTimer, NotchAccessories, NotchLyrics, NotchQueue,
    NotchLiveEqualizer, NotchDownloads, NotchAgents, NotchWatch, NotchMascot,
    // System monitor, one entry per metric family
    MonitorCPU, MonitorGPU, MonitorMemory, MonitorNetwork, MonitorDisk, MonitorPower, ConnectedDevices, FanControl,
}

/// <summary>Hub sections, in display order.</summary>
public enum FeatureGroup
{
    WindowsDock, MouseKeyboard, ClipboardFiles, Sound, EnergyDisplay, Tools, DynamicIsland, Monitor,
}

/// <summary>Where a feature stands on Windows. New for the port.</summary>
public enum WindowsStatus
{
    /// <summary>Works in this build.</summary>
    Ported,
    /// <summary>Will be ported; the milestone is in <see cref="FeatureInfo.Milestone"/>.</summary>
    Planned,
    /// <summary>Planned as a switch that flips the built-in Windows setting.</summary>
    NativeToggle,
    /// <summary>Windows already does this; the hub links to the setting.</summary>
    Native,
    /// <summary>macOS-only concept with no useful Windows meaning.</summary>
    Dropped,
}
