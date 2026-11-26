// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/FeatureCatalog.swift: groups, enable keys,
// installed-by-default list, island extensions. Windows status and notes are new.
using Notchly.Core.Settings;

namespace Notchly.Core.Features;

public sealed record FeatureInfo(
    AppFeature Feature,
    string Id,
    FeatureGroup Group,
    string Title,
    string Summary,
    string Glyph,
    IReadOnlyList<string> EnabledKeys,
    bool InstalledByDefault,
    WindowsStatus Status,
    string Milestone,
    string? WindowsNote = null,
    string? SettingsUri = null)
{
    public string AvailabilityKey => DefaultsKey.FeatureAvailable(Id);
    public bool IsBeta => Feature is AppFeature.FanControl or AppFeature.KillProcess;
    /// <summary>Whether Notchly can run it (or flip the Windows setting) today.</summary>
    public bool CanInstall => Status is WindowsStatus.Ported;
}

public static class FeatureCatalog
{
    /// <summary>Upstream raw value: the enum name with a lower-case first letter ("notchLyrics").</summary>
    public static string Id(AppFeature feature)
    {
        var name = feature.ToString();
        // Upstream spells these with an upper-case acronym tail.
        return name switch
        {
            "ScreenOCR" => "screenOCR",
            "MonitorCPU" => "monitorCPU",
            "MonitorGPU" => "monitorGPU",
            _ => char.ToLowerInvariant(name[0]) + name[1..],
        };
    }

    public static AppFeature? FromId(string id) =>
        All.FirstOrDefault(info => info.Id == id)?.Feature;

    public static FeatureInfo Info(AppFeature feature) => ByFeature[feature];

    public static IReadOnlyList<FeatureInfo> All { get; } = Build();

    private static readonly Dictionary<AppFeature, FeatureInfo> ByFeature = All.ToDictionary(i => i.Feature);

    public static IEnumerable<FeatureInfo> InGroup(FeatureGroup group) => All.Where(i => i.Group == group);

    public static string GroupTitle(FeatureGroup group) => group switch
    {
        FeatureGroup.WindowsDock => "Windows & Taskbar",
        FeatureGroup.MouseKeyboard => "Mouse & Keyboard",
        FeatureGroup.ClipboardFiles => "Clipboard & Files",
        FeatureGroup.Sound => "Sound",
        FeatureGroup.EnergyDisplay => "Energy & Display",
        FeatureGroup.Tools => "Tools",
        FeatureGroup.DynamicIsland => "Dynamic Island",
        FeatureGroup.Monitor => "System Monitor",
        _ => group.ToString(),
    };

    /// <summary>The island's extensions do nothing without it; uninstalling it offers to take them along.</summary>
    public static IEnumerable<AppFeature> DynamicIslandExtensions =>
        InGroup(FeatureGroup.DynamicIsland).Select(i => i.Feature).Where(f => f != AppFeature.Notch);

    /// <summary>Agents and the mascot stay separate choices when installing the island.</summary>
    public static IEnumerable<AppFeature> DynamicIslandInitialExtensions =>
        DynamicIslandExtensions.Where(f => f is not (AppFeature.NotchMascot or AppFeature.NotchAgents));

    /// <summary>Registered availability defaults, only for features that can run on Windows.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> AvailabilityDefaults =>
        All.Select(i => new KeyValuePair<string, object?>(i.AvailabilityKey, i.CanInstall && i.InstalledByDefault));

    // Segoe Fluent Icons code points (built into Windows 11).
    private static class Glyph
    {
        public const string Apps = "", Window = "", Snap = "", Close = "", Order = "";
        public const string Mouse = "", Scroll = "", Keyboard = "", Text = "", Shield = "";
        public const string Clipboard = "", Paste = "", Cut = "", Rename = "", Tray = "";
        public const string Link = "", Disk = "", Volume = "", Speaker = "", List = "";
        public const string MicOff = "", Music = "", Moon = "", Brightness = "", Sun = "";
        public const string Bluetooth = "", Launch = "", Toggles = "", Eyedropper = "";
        public const string Scan = "", Sparkle = "", Photo = "", Broom = "", Trash = "";
        public const string Package = "", Update = "", Screenshot = "", Camera = "";
        public const string Wheel = "", Note = "", Command = "", Record = "", Wallpaper = "";
        public const string Stop = "", Network = "", Island = "", Calendar = "", Bell = "";
        public const string Gesture = "", Timer = "", Battery = "", Quote = "", Wave = "";
        public const string Download = "", Robot = "", Eye = "", Smile = "", Cpu = "";
        public const string Gpu = "", Memory = "", Disk2 = "", Power = "", Usb = "";
        public const string Fan = "", Zoom = "";
    }

    private static List<FeatureInfo> Build()
    {
        var list = new List<FeatureInfo>();
        void Add(AppFeature f, FeatureGroup g, string title, string summary, string glyph, string[] keys,
                 bool byDefault, WindowsStatus status, string milestone, string? note = null, string? uri = null) =>
            list.Add(new FeatureInfo(f, Id(f), g, title, summary, glyph, keys, byDefault, status, milestone, note, uri));

        const WindowsStatus P = WindowsStatus.Planned, N = WindowsStatus.Native, D = WindowsStatus.Dropped;
        string[] none = [];

        // Windows and Dock
        var g = FeatureGroup.WindowsDock;
        Add(AppFeature.Switcher, g, "App switcher", "Switch apps and windows with live previews and search.", Glyph.Apps,
            [DefaultsKey.SwitcherEnabled], true, WindowsStatus.Ported, "M7", "Replaces Alt+Tab with live window previews; off until you turn it on.");
        Add(AppFeature.DockPreview, g, "Dock preview", "Hover Dock icons to preview windows.", Glyph.Window,
            [DefaultsKey.DockPreviewEnabled], true, N, "-", "Windows taskbar already previews windows on hover.", "ms-settings:taskbar");
        Add(AppFeature.DockClick, g, "Dock clicks", "Click an active app's icon to minimize or cycle windows.", Glyph.Window,
            [DefaultsKey.DockClickMinimize, DefaultsKey.DockClickHide, DefaultsKey.DockClickCycleWindows], true, N, "-",
            "Clicking the active taskbar button already minimizes; Ctrl+click cycles windows.", "ms-settings:taskbar");
        Add(AppFeature.WindowMaximizer, g, "Maximize windows", "Fill the screen without a new Space.", Glyph.Window,
            [DefaultsKey.WindowMaximizeEnabled], true, D, "-", "Windows' maximize button already does this.");
        Add(AppFeature.WindowLayout, g, "Window layout", "Snap windows, move them between displays and restore them.", Glyph.Snap,
            none, true, WindowsStatus.Ported, "M7", "Shortcuts, dragging with a modifier and edge snap on top of Windows Snap; off until you turn it on.", "ms-settings:multitasking");
        Add(AppFeature.AutoQuit, g, "Quit on close", "Quit selected apps when their last window closes.", Glyph.Close,
            [DefaultsKey.AutoQuitEnabled], true, D, "-", "Windows apps already exit with their last window.");
        Add(AppFeature.SpacesOrder, g, "Fixed Space order", "Keep Spaces in the order you set.", Glyph.Order,
            [DefaultsKey.SpacesOrderEnabled], false, D, "-", "Windows virtual desktops never reorder themselves.");

        // Mouse and keyboard
        g = FeatureGroup.MouseKeyboard;
        Add(AppFeature.ScrollInverter, g, "Scroll direction", "Invert mouse scrolling independently of the touchpad.", Glyph.Scroll,
            [DefaultsKey.ScrollInverterEnabled, DefaultsKey.ScrollInverterHorizontalEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.ScrollHorizontal, g, "Scroll sideways", "Hold a key to turn the wheel into horizontal scrolling.", Glyph.Scroll,
            [DefaultsKey.ScrollHorizontalEnabled], false, WindowsStatus.Ported, "M7");
        Add(AppFeature.ScrollZoom, g, "Scroll zoom", "Zoom with a modifier and the wheel.", Glyph.Zoom,
            [DefaultsKey.PinchZoomEnabled], false, WindowsStatus.Ported, "M7", "Check upstream behaviour when porting; Ctrl+wheel already zooms in most apps.");
        Add(AppFeature.FocusFollowsMouse, g, "Focus follows mouse", "Bring the window under the pointer forward after a pause.", Glyph.Mouse,
            [DefaultsKey.FocusFollowsMouseEnabled], false, WindowsStatus.Ported, "M6", "Turns on Windows' own 'activate window on hover' setting, and puts yours back when you turn it off.");
        Add(AppFeature.SmoothScroll, g, "Smooth scrolling", "Give the mouse wheel a fluid glide.", Glyph.Scroll,
            [DefaultsKey.SmoothScrollEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.LinearScroll, g, "Linear scrolling", "Every wheel notch scrolls the same distance.", Glyph.Scroll,
            [DefaultsKey.LinearScrollEnabled], false, WindowsStatus.Ported, "M7");
        Add(AppFeature.ScrollSpeed, g, "Scroll speed", "Adjust how far each wheel notch scrolls.", Glyph.Scroll,
            [DefaultsKey.ScrollSpeedEnabled], false, WindowsStatus.Ported, "M7");
        Add(AppFeature.MouseAcceleration, g, "Pointer acceleration", "Turn mouse acceleration off and restore it later.", Glyph.Mouse,
            [DefaultsKey.MouseAccelerationDisabled], true, WindowsStatus.Ported, "M6", "Turns off 'Enhance pointer precision', and puts yours back when you turn it off.", "ms-settings:mousetouchpad");
        Add(AppFeature.MouseNavigation, g, "Side buttons", "Mouse Back and Forward buttons in more apps.", Glyph.Mouse,
            [DefaultsKey.MouseNavigationEnabled], true, N, "-", "Windows apps handle side buttons natively.");
        Add(AppFeature.MouseButtonShortcuts, g, "Mouse button shortcuts", "Assign keys to extra mouse buttons.", Glyph.Mouse,
            [DefaultsKey.MouseButtonShortcutsEnabled, DefaultsKey.MouseSpacesGestureEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.MiddleClick, g, "Middle click", "Three-finger touchpad press as middle click.", Glyph.Mouse,
            [DefaultsKey.MiddleClickEnabled], true, N, "-", "Built into Precision Touchpad settings (three-finger tap).", "ms-settings:devices-touchpad");
        Add(AppFeature.MouseClickDebounce, g, "Extra click filter", "Ignore accidental double clicks from a worn button.", Glyph.Mouse,
            [DefaultsKey.MouseClickDebounceEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.KeyboardDebounce, g, "Key debounce", "Filter repeated letters from a worn keyboard.", Glyph.Keyboard,
            [DefaultsKey.KeyboardDebounceEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.TextSnippets, g, "Text snippets", "Expand short triggers into text with variables.", Glyph.Text,
            [DefaultsKey.TextSnippetsEnabled, DefaultsKey.SnippetLibraryEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.SuperKey, g, "Super key", "Use Caps Lock as a Hyper key with its own tap action.", Glyph.Keyboard,
            [DefaultsKey.SuperKeyEnabled], true, WindowsStatus.Ported, "M7");
        Add(AppFeature.QuitWindowProtection, g, "Quit and close protection", "Guard Alt+F4, Ctrl+W and Ctrl+Q with a hold or double press.", Glyph.Shield,
            [DefaultsKey.QuitProtectionQuitEnabled, DefaultsKey.QuitProtectionCloseEnabled], true, WindowsStatus.Ported, "M7");

        // Clipboard and files
        g = FeatureGroup.ClipboardFiles;
        Add(AppFeature.ClipboardHistory, g, "Clipboard history", "Search text, image and file history; pin favorites.", Glyph.Clipboard,
            [DefaultsKey.ClipboardHistoryEnabled], true, WindowsStatus.Ported, "M8");
        Add(AppFeature.PastePlain, g, "Paste as plain text", "Paste without formatting.", Glyph.Paste,
            [DefaultsKey.PastePlainEnabled], true, WindowsStatus.Ported, "M8");
        Add(AppFeature.FinderCutPaste, g, "Explorer image paste", "Paste a copied image into Explorer as a PNG file.", Glyph.Cut,
            [DefaultsKey.FinderPasteImageAsFile], true, WindowsStatus.Ported, "M8", "Cut and paste of files is built into Explorer; this adds pasting a copied image as a PNG file.");
        Add(AppFeature.FinderRename, g, "Rename with F2", "Rename files with F2.", Glyph.Rename,
            [DefaultsKey.FinderRenameEnabled], true, N, "-", "Explorer already renames with F2.");
        Add(AppFeature.Shelf, g, "Shelf", "Park files, text and links while dragging.", Glyph.Tray,
            [DefaultsKey.ShelfEnabled], true, WindowsStatus.Ported, "M3", "Files page on the island, and a floating card with a shortcut, shake, drop zone and edge peek (M8).");
        Add(AppFeature.UrlCleaner, g, "Clean URL", "Remove tracking parameters from links.", Glyph.Link,
            [DefaultsKey.UrlCleanerEnabled], true, WindowsStatus.Ported, "M8");
        Add(AppFeature.DiskImageInstaller, g, "Disk image installer", "Install apps from mounted disk images.", Glyph.Disk,
            none, false, D, "-", "macOS disk images have no Windows equivalent.");

        // Sound
        g = FeatureGroup.Sound;
        Add(AppFeature.Mixer, g, "Volume mixer", "Per-app volume, meters, favorites and per-app output.", Glyph.Volume,
            none, true, WindowsStatus.Ported, "M4",
            "Per-app volume, meters, pinning and per-app output through Windows' own session API; no boost above 100% yet.");
        Add(AppFeature.SoundOutputSwitcher, g, "Output switcher", "Switch audio outputs with a shortcut.", Glyph.Speaker,
            [DefaultsKey.SoundOutputSwitcherEnabled], true, WindowsStatus.Ported, "M4");
        Add(AppFeature.AudioPriority, g, "Device priority", "Prefer devices in your order as they connect.", Glyph.List,
            [DefaultsKey.AudioPriorityOutputEnabled, DefaultsKey.AudioPriorityInputEnabled], false, WindowsStatus.Ported, "M4");
        Add(AppFeature.MicMute, g, "Microphone mute", "Mute every microphone with one shortcut.", Glyph.MicOff,
            none, true, WindowsStatus.Ported, "M4");
        Add(AppFeature.MusicBlock, g, "Music app blocker", "Stop the Music app launching from play keys.", Glyph.Music,
            [DefaultsKey.MusicBlockEnabled], true, D, "-", "Windows has no 'play key launches Music' behaviour.");

        // Energy and display
        g = FeatureGroup.EnergyDisplay;
        Add(AppFeature.KeepAwake, g, "Keep awake", "Keep the PC awake on a timer, lid closed or by rules.", Glyph.Moon,
            none, true, WindowsStatus.Ported, "M6");
        Add(AppFeature.Brightness, g, "Displays", "Brightness for the built-in panel and DDC/CI monitors, software dimming.", Glyph.Brightness,
            [DefaultsKey.BrightnessControlEnabled], true, WindowsStatus.Ported, "M6");
        Add(AppFeature.ExtraBrightness, g, "Extra brightness", "Use HDR headroom beyond normal maximum brightness.", Glyph.Sun,
            [DefaultsKey.ExtraBrightnessEnabled], true, D, "-", "Needs an XDR/HDR panel.");
        Add(AppFeature.BluetoothSleep, g, "Bluetooth on sleep", "Turn Bluetooth off during sleep and back on at wake.", Glyph.Bluetooth,
            [DefaultsKey.BluetoothSleepEnabled], true, WindowsStatus.Ported, "M6");

        // Tools
        g = FeatureGroup.Tools;
        Add(AppFeature.QuickLauncher, g, "Quick panel", "A floating palette of favorite tools.", Glyph.Launch, none, true, WindowsStatus.Ported, "M9",
            "Ctrl+Win+V opens a grid of tools, or the island's Tools page. Tiles for features not built yet (Cleaner, Package manager, Media, Uninstaller, Recorder) stay hidden.");
        Add(AppFeature.QuickToggles, g, "Quick toggles", "Dark mode, hide desktop icons, refresh rate, lock and more.", Glyph.Toggles, none, true, WindowsStatus.Ported, "M6");
        Add(AppFeature.QuitApps, g, "Quit apps", "See every open app and quit them one by one or all at once.", Glyph.Close, none, false, WindowsStatus.Ported, "M6");
        Add(AppFeature.ColorPicker, g, "Color picker", "Copy a screen color as HEX, RGB or HSL.", Glyph.Eyedropper, none, true, WindowsStatus.Ported, "M9",
            "A frozen screen with a magnifier; HEX, RGB, HSL or SwiftUI. Upstream's system sampler fallback and loupe zoom options are not built.");
        Add(AppFeature.ScreenOCR, g, "Copy text from screen", "Recognize text or read a QR code from any area.", Glyph.Scan, none, true, WindowsStatus.Ported, "M9",
            "Text is read by Windows' own engine and codes by ZXing.Net, all on this PC. No second recognition pass and no micro QR.");
        Add(AppFeature.CleaningMode, g, "Cleaning mode", "Lock keyboard input while cleaning.", Glyph.Sparkle, none, true, WindowsStatus.Ported, "M9",
            "Ctrl+Alt+Delete cannot be blocked from a program, so it still works while the keyboard is locked.");
        Add(AppFeature.MediaTools, g, "Media tools", "Compress video, convert images, make GIFs.", Glyph.Photo, none, true, P, "M10");
        Add(AppFeature.Cleaner, g, "Cleaner", "Remove caches, logs and leftovers on a schedule.", Glyph.Broom, none, true, P, "M11");
        Add(AppFeature.Uninstaller, g, "Uninstaller", "Remove apps and review their leftovers.", Glyph.Trash, none, true, P, "M11");
        Add(AppFeature.Homebrew, g, "Package manager", "Search, install and remove winget packages.", Glyph.Package, none, true, P, "M11",
            "Homebrew becomes winget on Windows.");
        Add(AppFeature.AppUpdates, g, "App updates", "Check winget and Store apps for updates in one list.", Glyph.Update, none, true, P, "M11");
        Add(AppFeature.Screenshot, g, "Screenshot", "Capture, annotate, pin and share screenshots.", Glyph.Screenshot, none, true, WindowsStatus.Ported, "M10",
            "Area, window, full screen and scrolling capture with an editor, pin, history and backdrop. Off until you turn its shortcuts on (Ctrl+Alt+Win+4 area, 3 full screen). No upload links and no HEIC.");
        Add(AppFeature.CameraPreview, g, "Camera preview", "Check your camera in a floating mirror or the island.", Glyph.Camera, none, true, WindowsStatus.Ported, "M3",
            "The floating mirror opens from a shortcut or Settings; the island page is the same camera.");
        Add(AppFeature.RadialMenu, g, "Radial menu", "A wheel of apps, files and tools around the pointer.", Glyph.Wheel,
            [DefaultsKey.RadialMenuEnabled], true, WindowsStatus.Ported, "M9",
            "Hold the Forward mouse button (or Ctrl+Alt+Win+Space), move toward a slice and let go. Off until turned on. No Now Playing slice, no trackpad tap.");
        Add(AppFeature.Scratchpad, g, "Scratchpad", "Autosaved notes in tabs with Markdown preview.", Glyph.Note, none, true, WindowsStatus.Ported, "M9",
            "The floating pad. The island's Scratchpad page comes with the launcher.");
        Add(AppFeature.CommandBar, g, "Command bar", "Search apps, windows, files and commands; calculate and convert.", Glyph.Command, none, true, WindowsStatus.Ported, "M9",
            "Alt+Space by default, off until turned on. Apps, windows, files, emoji, menu items, selected text, settings pages and your links; sums, units, dates and colors. No scripts, no droplet animation.");
        Add(AppFeature.ScreenRecorder, g, "Screen recording", "Record with system audio and microphone, then trim and export.", Glyph.Record, none, true, P, "M10");
        Add(AppFeature.Wallpaper, g, "Wallpaper", "Set wallpapers per display.", Glyph.Wallpaper, none, false, WindowsStatus.Ported, "M9",
            "Windows' own pictures and your own images and folders, set through the wallpaper object. No Apple catalog.");
        Add(AppFeature.KillProcess, g, "Kill process", "Find and stop misbehaving processes.", Glyph.Stop, none, false, WindowsStatus.Ported, "M6");
        Add(AppFeature.PortManager, g, "Port manager", "Find processes listening on ports and stop them.", Glyph.Network, none, false, WindowsStatus.Ported, "M6");

        // Dynamic Island
        g = FeatureGroup.DynamicIsland;
        Add(AppFeature.Notch, g, "Dynamic Island", "Music, notices, timers and controls around the webcam.", Glyph.Island,
            [DefaultsKey.NotchEnabled], true, WindowsStatus.Ported, "M2");
        Add(AppFeature.NotchCalendar, g, "Calendar", "Upcoming events and countdowns in the island.", Glyph.Calendar,
            [DefaultsKey.NotchCalendarEnabled], true, WindowsStatus.Ported, "M3", "Google, Outlook, Canvas and iCloud calendars by their private iCal link; links are encrypted on this PC and events stay in memory.");
        Add(AppFeature.NotchNotifications, g, "Notifications", "Mirror Windows notifications in the island.", Glyph.Bell,
            [DefaultsKey.NotchNotificationsEnabled], true, WindowsStatus.Ported, "M3",
            "Through Windows' notification listener; needs a one-time setup (Developer Mode) and your permission.");
        Add(AppFeature.NotchGestures, g, "Island gestures", "Scroll to open or close; swipe to change tracks.", Glyph.Gesture,
            [DefaultsKey.NotchGesturesEnabled], true, WindowsStatus.Ported, "M2");
        Add(AppFeature.NotchTimer, g, "Timer", "Timer, Pomodoro and stopwatch in the island.", Glyph.Timer,
            [DefaultsKey.NotchTimerEnabled], true, WindowsStatus.Ported, "M2");
        Add(AppFeature.NotchAccessories, g, "Accessories", "Bluetooth device connections and batteries.", Glyph.Bluetooth,
            [DefaultsKey.NotchAccessoriesEnabled], true, WindowsStatus.Ported, "M3",
            "Battery levels where Windows reports them (DEVPKEY_Bluetooth_Battery).");
        Add(AppFeature.NotchLyrics, g, "Lyrics", "Synced lyrics for the playing song.", Glyph.Quote,
            [DefaultsKey.NotchLyricsEnabled], true, WindowsStatus.Ported, "M3", "Synced lyrics from an imported .lrc file, or from lrclib.net once you allow online lookup.");
        Add(AppFeature.NotchQueue, g, "Play queue", "The player's upcoming songs.", Glyph.List,
            [DefaultsKey.NotchQueueEnabled], true, D, "-", "Windows media controls expose no system-wide queue.");
        Add(AppFeature.NotchLiveEqualizer, g, "Live equalizer", "Bars that follow the music's real levels.", Glyph.Wave,
            [DefaultsKey.NotchLiveEqualizer], true, WindowsStatus.Ported, "M3", "Follows the system output (WASAPI loopback) instead of tapping the player's process; on by default, as Windows asks no permission.");
        Add(AppFeature.NotchDownloads, g, "Downloads", "Downloads in progress in the island.", Glyph.Download,
            [DefaultsKey.NotchDownloadsEnabled], true, WindowsStatus.Ported, "M3", "Watches your Downloads folder (or one you choose); shows size and speed because Windows browsers don't publish progress.");
        // Windows deviation: opt in rather than installed by default, as it runs the agents' own commands and asks
        // cursor.com; upstream installs it and migrates people onto it.
        Add(AppFeature.NotchAgents, g, "AI agents", "Claude Code, Codex, OpenCode, Copilot and Cursor usage and limits.", Glyph.Robot,
            [DefaultsKey.NotchAgentsEnabled], false, WindowsStatus.Ported, "M3",
            "Reads each agent's local logs; Claude limits via its own claude command; Cursor (beyond upstream) via its sign-in.");
        Add(AppFeature.NotchWatch, g, "Watch", "Watch part of a window and get told when it changes.", Glyph.Eye,
            [DefaultsKey.NotchWatchEnabled], true, WindowsStatus.Ported, "M3",
            "Windows.Graphics.Capture and Windows' own text recognition, on this PC only.");
        Add(AppFeature.NotchMascot, g, "Companion", "An animated companion beside the island.", Glyph.Smile,
            [DefaultsKey.NotchMascotEnabled], false, WindowsStatus.Ported, "M3",
            "Drawn with WPF; Reduce Motion follows Windows' animation effects and Low Power follows Battery Saver.");

        // System monitor
        g = FeatureGroup.Monitor;
        Add(AppFeature.MonitorCPU, g, "Processor", "CPU load per core, temperature and top processes.", Glyph.Cpu, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.MonitorGPU, g, "Graphics", "GPU load for the Intel and NVIDIA GPUs.", Glyph.Gpu, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.MonitorMemory, g, "Memory", "Memory use and pressure.", Glyph.Memory, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.MonitorNetwork, g, "Network", "Live traffic, totals, local IP and speed test.", Glyph.Network, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.MonitorDisk, g, "Disk", "Disk space and activity.", Glyph.Disk2, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.MonitorPower, g, "Power", "Battery health, charge rate and power use.", Glyph.Power, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.ConnectedDevices, g, "Connected devices", "USB devices and peripheral batteries.", Glyph.Usb, none, true, WindowsStatus.Ported, "M5");
        Add(AppFeature.FanControl, g, "Fan control", "OMEN fan speeds and curves (beta, needs admin helper).", Glyph.Fan, none, false, P, "M12");

        return list;
    }
}
