# Feature parity: vorssaint-utils → Notchly (Windows)

Generated from `src/Notchly.Core/Features/FeatureCatalog.cs`; regenerate whenever a feature's status changes.

**14 ported**, 54 planned, 5 built into Windows, 7 not applicable — 80 upstream features.

## Windows & Taskbar

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `switcher` | App switcher | 🛠️ Planned | M7 | Alt+Tab replacement using live DWM thumbnails. |
| `dockPreview` | Dock preview | 🪟 Built into Windows | - | Windows taskbar already previews windows on hover. |
| `dockClick` | Dock clicks | 🪟 Built into Windows | - | Clicking the active taskbar button already minimizes; Ctrl+click cycles windows. |
| `windowMaximizer` | Maximize windows | ➖ Not on Windows | - | Windows' maximize button already does this. |
| `windowLayout` | Window layout | 🛠️ Planned | M7 | Shortcuts and modifier-drag on top of Windows Snap. |
| `autoQuit` | Quit on close | ➖ Not on Windows | - | Windows apps already exit with their last window. |
| `spacesOrder` | Fixed Space order | ➖ Not on Windows | - | Windows virtual desktops never reorder themselves. |

## Mouse & Keyboard

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `scrollInverter` | Scroll direction | 🛠️ Planned | M7 |  |
| `scrollHorizontal` | Scroll sideways | 🛠️ Planned | M7 |  |
| `scrollZoom` | Scroll zoom | 🛠️ Planned | M7 | Check upstream behaviour when porting; Ctrl+wheel already zooms in most apps. |
| `focusFollowsMouse` | Focus follows mouse | 🔁 Windows-setting toggle | M6 | Flips Windows' own 'activate window on hover' setting. |
| `smoothScroll` | Smooth scrolling | 🛠️ Planned | M7 |  |
| `linearScroll` | Linear scrolling | 🛠️ Planned | M7 |  |
| `scrollSpeed` | Scroll speed | 🛠️ Planned | M7 |  |
| `mouseAcceleration` | Pointer acceleration | 🔁 Windows-setting toggle | M6 | Flips 'Enhance pointer precision'. |
| `mouseNavigation` | Side buttons | 🪟 Built into Windows | - | Windows apps handle side buttons natively. |
| `mouseButtonShortcuts` | Mouse button shortcuts | 🛠️ Planned | M7 |  |
| `middleClick` | Middle click | 🪟 Built into Windows | - | Built into Precision Touchpad settings (three-finger tap). |
| `mouseClickDebounce` | Extra click filter | 🛠️ Planned | M7 |  |
| `keyboardDebounce` | Key debounce | 🛠️ Planned | M7 |  |
| `textSnippets` | Text snippets | 🛠️ Planned | M7 |  |
| `superKey` | Super key | 🛠️ Planned | M7 |  |
| `quitWindowProtection` | Quit and close protection | 🛠️ Planned | M7 |  |

## Clipboard & Files

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `clipboardHistory` | Clipboard history | 🛠️ Planned | M8 |  |
| `pastePlain` | Paste as plain text | 🛠️ Planned | M8 |  |
| `finderCutPaste` | Explorer image paste | 🛠️ Planned | M8 | Cut/paste of files is built into Explorer. |
| `finderRename` | Rename with F2 | 🪟 Built into Windows | - | Explorer already renames with F2. |
| `shelf` | Shelf | ✅ Ported | M3 | Files page on the island; the floating shelf window comes in M8. |
| `urlCleaner` | Clean URL | 🛠️ Planned | M8 |  |
| `diskImageInstaller` | Disk image installer | ➖ Not on Windows | - | macOS disk images have no Windows equivalent. |

## Sound

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `mixer` | Volume mixer | 🛠️ Planned | M4 |  |
| `soundOutputSwitcher` | Output switcher | 🛠️ Planned | M4 |  |
| `audioPriority` | Device priority | 🛠️ Planned | M4 |  |
| `micMute` | Microphone mute | 🛠️ Planned | M4 |  |
| `musicBlock` | Music app blocker | ➖ Not on Windows | - | Windows has no 'play key launches Music' behaviour. |

## Energy & Display

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `keepAwake` | Keep awake | 🛠️ Planned | M6 |  |
| `brightness` | Displays | 🛠️ Planned | M6 |  |
| `extraBrightness` | Extra brightness | ➖ Not on Windows | - | Needs an XDR/HDR panel. |
| `bluetoothSleep` | Bluetooth on sleep | 🛠️ Planned | M6 |  |

## Tools

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `quickLauncher` | Quick panel | 🛠️ Planned | M9 |  |
| `quickToggles` | Quick toggles | 🛠️ Planned | M6 |  |
| `quitApps` | Quit apps | 🛠️ Planned | M6 |  |
| `colorPicker` | Color picker | 🛠️ Planned | M9 |  |
| `screenOCR` | Copy text from screen | 🛠️ Planned | M9 |  |
| `cleaningMode` | Cleaning mode | 🛠️ Planned | M9 |  |
| `mediaTools` | Media tools | 🛠️ Planned | M10 |  |
| `cleaner` | Cleaner | 🛠️ Planned | M11 |  |
| `uninstaller` | Uninstaller | 🛠️ Planned | M11 |  |
| `homebrew` | Package manager | 🛠️ Planned | M11 | Homebrew becomes winget on Windows. |
| `appUpdates` | App updates | 🛠️ Planned | M11 |  |
| `screenshot` | Screenshot | 🛠️ Planned | M10 |  |
| `cameraPreview` | Camera preview | ✅ Ported | M3 | Floating mirror window comes with the tools in M9. |
| `radialMenu` | Radial menu | 🛠️ Planned | M9 |  |
| `scratchpad` | Scratchpad | 🛠️ Planned | M9 |  |
| `commandBar` | Command bar | 🛠️ Planned | M9 |  |
| `screenRecorder` | Screen recording | 🛠️ Planned | M10 |  |
| `wallpaper` | Wallpaper | 🛠️ Planned | M9 |  |
| `killProcess` | Kill process | 🛠️ Planned | M6 |  |
| `portManager` | Port manager | 🛠️ Planned | M6 |  |

## Dynamic Island

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `notch` | Dynamic Island | ✅ Ported | M2 |  |
| `notchCalendar` | Calendar | ✅ Ported | M3 | Google, Outlook, Canvas and iCloud calendars by their private iCal link; links are encrypted on this PC and events stay in memory. |
| `notchNotifications` | Notifications | ✅ Ported | M3 | Through Windows' notification listener; needs a one-time setup (Developer Mode) and your permission. |
| `notchGestures` | Island gestures | ✅ Ported | M2 |  |
| `notchTimer` | Timer | ✅ Ported | M2 |  |
| `notchAccessories` | Accessories | ✅ Ported | M3 | Battery levels where Windows reports them (DEVPKEY_Bluetooth_Battery). |
| `notchLyrics` | Lyrics | ✅ Ported | M3 | Synced lyrics from an imported .lrc file, or from lrclib.net once you allow online lookup. |
| `notchQueue` | Play queue | ➖ Not on Windows | - | Windows media controls expose no system-wide queue. |
| `notchLiveEqualizer` | Live equalizer | ✅ Ported | M3 | Follows the system output (WASAPI loopback) instead of tapping the player's process; on by default, as Windows asks no permission. |
| `notchDownloads` | Downloads | ✅ Ported | M3 | Watches your Downloads folder (or one you choose); shows size and speed because Windows browsers don't publish progress. |
| `notchAgents` | AI agents | ✅ Ported | M3 | Reads each agent's local logs; Claude limits via its own claude command; Cursor (beyond upstream) via its sign-in. |
| `notchWatch` | Watch | ✅ Ported | M3 | Windows.Graphics.Capture and Windows' own text recognition, on this PC only. |
| `notchMascot` | Companion | ✅ Ported | M3 | Drawn with WPF; Reduce Motion follows Windows' animation effects and Low Power follows Battery Saver. |

## System Monitor

| Upstream id | Feature | Status | Milestone | Windows notes |
|---|---|---|---|---|
| `monitorCPU` | Processor | 🛠️ Planned | M5 |  |
| `monitorGPU` | Graphics | 🛠️ Planned | M5 |  |
| `monitorMemory` | Memory | 🛠️ Planned | M5 |  |
| `monitorNetwork` | Network | 🛠️ Planned | M5 |  |
| `monitorDisk` | Disk | 🛠️ Planned | M5 |  |
| `monitorPower` | Power | 🛠️ Planned | M5 |  |
| `connectedDevices` | Connected devices | 🛠️ Planned | M5 |  |
| `fanControl` | Fan control | 🛠️ Planned | M12 |  |

