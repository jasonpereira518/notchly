# Notchly roadmap

The master porting plan, approved 2026-11-19. Feature status lives in [PARITY.md](PARITY.md); each milestone or feature request gets its own plan before work starts.


## Context
- **Source.** [vorssaint/vorssaint-utils](https://github.com/vorssaint/vorssaint-utils) is a free, GPL-3.0 macOS menu-bar toolkit written in Swift/AppKit/SwiftUI, with about 720 Swift files.
  - It has **80 switchable features** in 8 groups, and you install or uninstall each one individually in a Features hub.
  - Together they replace about a dozen paid Mac utilities: per-app volume mixer, system monitor, Alt-Tab-style switcher, window snapping, clipboard history, snippets, file shelf, screenshot and recording, uninstaller, cleaner, a command bar, and a **"Dynamic Island" notch** with music, lyrics, timers, calendar, notifications and AI-agent usage.
- **Target.** This laptop: HP OMEN 16-wf0xxx (i7-13700HX, 32 GB, Intel UHD + RTX 4060 Optimus, Windows 11 Home build 26300).
  - The 2560×1440 panel runs at 240 Hz and 150% scaling, so it is **1707×960 DIP**. The taskbar is centered at the bottom.
  - There's **no physical notch**; the webcam is centered in the top bezel. The island will be a simulated notch at top-center. The original already supports "simulated notch" geometry for Macs without one, so we reuse it.
- **Decisions (from you).**
  - C# / .NET 10 + WPF.
  - The island is a floating click-through pill by default, with an optional reserved top strip.
  - Named Notchly (formerly the working name "Islet"); the name lives in `AppIdentity`.
  - **Island first**, after the base shell.
- **Constraints.**
  - The port is a derivative work, so it stays **GPL-3.0-or-later**. Keep the LICENSE, attribution and SPDX headers.
  - Upstream `TRADEMARKS.md` says forks must not use the Vorssaint name, icon (black-hole glyph), bundle id, update feed or look. We use our own name, icon and the Windows 11 Fluent style, and never point at upstream's update feed.
- **Goal.** Port everything that makes sense on Windows. Drop features that are macOS-only or that Windows already has. Make each milestone a usable app.

## How the original works (what we're porting)
- **Shell.**
  - `main.swift` → `App/AppDelegate.swift` runs a tray-only app.
  - `Core/FeatureCatalog.swift` defines `enum AppFeature` (80 cases). Each has a raw-value id, group, `enabledKeys`, `permissions`, `initialEnableKeys` and presets (`Core/FeaturePresets.swift`).
  - `App/FeatureRuntime.swift` keeps a table of lazy closures, each calling `XService.shared.syncWithPreferences()` (idempotent start/stop). Installing a feature just sets the `featureAvailable.<id>` flag.
- **Settings.**
  - About 930 flat UserDefaults keys in `Core/Defaults.swift` (`enum DefaultsKey`, `registeredDefaults`, sanitizers, about 30 migrations).
  - Plist backup and dotfile handling in `Core/SettingsBackupSupport.swift`.
- **UI.**
  - A popover with an icon tab bar of up to 12 sections (`UI/MenuPanel/PanelLayout.swift`, `PanelSectionID`, gated by installed features).
  - A sidebar Settings window (`UI/Settings/SettingsDirectory.swift`).
  - Onboarding: language → presets → permissions.
- **Input.**
  - Carbon `RegisterEventHotKey` for plain shortcuts.
  - `CGEventTap` in about 25 services (switcher, snippets, super key, mouse tweaks, quit protection…).
  - Shortcut model and serialization (`"control+option:40"`) in `Core/GlobalShortcut.swift`.
- **Island.**
  - `Services/Notch/*` + `UI/Notch/*`, about 1 MB in total.
  - Key files: `NotchService.swift` (state machine), `NotchSupport.swift` (`NotchGeometry`, `NotchMotion` spring, modules, compact activities), `NotchWindowHost.swift` (panel), `UI/Notch/NotchView.swift` (`NotchShape`).
- **Tests.**
  - About 330 files in a homemade harness (`Tests/TestSuite.swift`, `./build.sh --test`).
  - The pure-logic suites are **executable specs** to port to xUnit.

## Architecture

**Stack**
- .NET 10 LTS, WPF, C#.
- Target framework `net10.0-windows10.0.26100.0`, which gives direct access to WinRT APIs: media controls, OCR, radios, capture.
- Per-monitor-v2 DPI app manifest.

**Libraries** (all MIT unless noted)
| Library | Used for |
|---|---|
| CommunityToolkit.Mvvm | view models |
| WPF-UI (lepoco) | Fluent controls, Mica, NavigationView |
| H.NotifyIcon.Wpf | tray icon and balloon/toast |
| Microsoft.Windows.CsWin32 | generated P/Invoke |
| NAudio | WASAPI sessions, endpoint volume, loopback |
| Serilog.Sinks.File | logging |
| xUnit | tests |

Added later by the milestone that needs them:
- Ical.Net
- Markdig
- ZXing.Net
- Microsoft.Data.Sqlite
- LibreHardwareMonitorLib (MPL-2.0, PawnIO build)
- FFmpeg binaries

**Repo layout** (git repo at `C:\JASON\College\Projects\vorssaint`)
```
upstream/                  git clone of vorssaint-utils, pinned SHA in docs/UPSTREAM.md (gitignored, read-only reference)
Notchly.sln
src/Notchly.Core/            net10.0, NO Windows APIs: settings store, feature catalog, shortcut model,
                           ported pure logic (notch geometry/motion/state, LRC, agent parsers, math, units, URL cleaning…)
src/Notchly.App/             WPF exe
  Shell/                   App.xaml, tray, panel window, settings window, features hub, onboarding
  Platform/                interop wrappers: Input/ (hooks, hotkeys, SendInput), Audio/, Display/, Power/,
                           Windowing/ (EnumWindows, DWM, foreground, fullscreen), Media/ (GSMTC), Shell/ (AppBar)
  Island/                  NotchWindow, NotchShape, animator, Modules/<Module>/
  Features/<Group>/<Feature>/  XService.cs (Sync/Start/Stop), XPanelSection.xaml, XSettingsPage.xaml, XViewModel.cs
src/Notchly.Helper/          (later, M12) elevated Windows Service over a named pipe: temperatures, fan control
tests/Notchly.Core.Tests/    xUnit; ports of upstream Tests/*.swift for each pure module
docs/PARITY.md             one row per upstream AppFeature with status/milestone
docs/UPSTREAM.md           pinned commit + porting conventions
```

**Shared services** (built in M1, used everywhere)
- **`SettingsStore`.** A flat key→JSON-value store with typed `Get<T>`/`Set`, a `Changed` event, debounced atomic save to `%APPDATA%\Notchly\settings.json`, schema version and migrations.
  - **Upstream key names and default values are kept verbatim** as each feature is ported, so every Swift line maps 1:1.
  - Export and import use JSON, porting the validation rules from `SettingsBackupSupport.swift`.
  - The dotfile `%USERPROFILE%\.config\notchly\settings.json` is applied at launch.
- **`FeatureCatalog` + `FeatureRuntime`.**
  - A port of `FeatureCatalog.swift` with the same ids (`switcher`, `notchLyrics`…), groups, `enabledKeys`, first-install enables and presets.
  - New field `WindowsStatus`: `Ported | NativeToggle | Native | Dropped | Planned`. Native/Dropped features appear in the hub greyed out with the reason and an "Open Windows setting" link (`ms-settings:` URI).
  - Runtime: services are created lazily and expose `IFeatureService.Sync()`.
- **`HotkeyService`.**
  - `RegisterHotKey` on a message-only window.
  - Shortcut model ported from `GlobalShortcut.swift` with modifiers Ctrl/Alt/Shift/Win and the same `"ctrl+alt:0x41"` text format.
  - A failed registration means the shortcut is taken, which gives us conflict detection.
  - macOS default shortcuts are remapped feature by feature.
- **`InputHookService`.**
  - **One** dedicated high-priority thread with its own message loop.
  - `WH_KEYBOARD_LL`/`WH_MOUSE_LL` are installed only while a subscriber exists.
  - Callbacks must return in well under 1 s (`LowLevelHooksTimeout`), so heavy work is posted elsewhere. A watchdog reinstalls hooks.
  - Injecting features pause while a fullscreen game runs, which avoids input lag and anti-cheat trouble.
- **`ForegroundWatcher`** (`SetWinEventHook`) and **`FullscreenDetector`** (`SHQueryUserNotificationState` + "foreground window covers its monitor").
- **Notifications.**
  - Alerts show as island notices first.
  - Fallback is a tray balloon (H.NotifyIcon), which Windows 11 renders as a toast.
- **Startup.**
  - Single instance through a named mutex plus a pipe that forwards "open settings".
  - Launch at login via the HKCU `Run` key.
  - Optional "run elevated at login" via a scheduled task with highest privileges (M6), so hooks also work over admin windows.

**Porting rules**
- Port pure Swift logic file-for-file into `Notchly.Core`, together with its upstream tests.
- Rewrite platform glue against the Windows API listed per feature.
- Use upstream user-facing strings (English) through `.resx`. The 15 upstream languages can be converted mechanically later.
- UI uses Windows 11 Fluent (Mica, Segoe Fluent Icons), not the Mac look.

## The island on this laptop

**Geometry** (port `NotchGeometry`)
- Simulated cutout width = `180 × height / 32`. The default height of 24 DIP gives **135×24 DIP** (203×36 px).
- Centered at x = (1707 − 135)/2 = **786 DIP**, with an x-offset slider in case the camera is off-center.
- Silhouettes:
  - "notch": hangs from the top edge. Shoulder = `min(14, h·0.19)`, bottom radius = `min(28, h·0.34)`, Bézier constant 0.55228475.
  - "capsule".
- Open sizes: spacious 560×370, compact 480×286, or custom (W 360–600, H 260–640).

**Window**
- A WPF window with `AllowsTransparency`, `Topmost`, no taskbar button, and `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`.
- It is sized to the largest animation envelope. Fully transparent pixels of a layered window are click-through, so only the black shape takes clicks.
- Keyboard focus is taken only when you click it open (as with upstream `acceptsKeyFocus`).
- Optional `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- Topmost is reasserted on foreground changes. A `WM_DPICHANGED` or display change triggers a geometry recompute.

**Behaviour** (port the `NotchService` state machine)
- States: collapsed, peeking, hover-expanded, pinned, notice, hidden in fullscreen, hidden until hover (a thin lip).
- Hover delay 0.25 s, close delay 0.18 s. Click toggles; clicking elsewhere collapses.
- Spring motion is ported from `NotchMotion`:

  | Motion | Duration | Bounce |
  |---|---|---|
  | Grow width | 0.44 s | 0.25 |
  | Grow height | 0.38 s | 0.22 |
  | Shrink width | 0.30 s | 0 |
  | Shrink height | 0.26 s | 0 |

  Overshoot is capped at 12. It's driven per frame from `CompositionTarget.Rendering` using elapsed time, so it stays smooth at 240 Hz.
- Gestures: wheel down ≥16 opens, up closes, horizontal ≥40 skips track.

**Reserved-strip mode**
- `SHAppBarMessage(ABM_NEW/ABM_SETPOS, ABE_TOP)` reserves the notch height, so maximized windows start below it.
- The strip is drawn black with the notch in the middle. Later it can show compact readouts on either side, the Windows stand-in for upstream's menu-bar readouts.
- **Menu bar (done):** the strip reads like a Mac menu bar: the app in front in bold and its menus left of the notch, the clock on the right.
  - Classic Win32 menus drop down under the bar (`WM_INITMENUPOPUP`, then `WM_COMMAND`); UI Automation menu bars open the app's own menu.
  - Apps without menus get File, Edit, View, Window and Help, whose items send the usual shortcut (`SendInput`).
  - The strip unregisters (`ABM_REMOVE`) while its window still exists, so turning it off gives maximized windows their full height back.

**Displays and fullscreen**
- Display options: built-in / primary / pointer / all (port `NotchDisplay`).
- Hidden while fullscreen video or games run.

**Pages and activities**
- Pages: `notchModuleOrder` / `notchHiddenModules`.
- Compact live activities in priority order: timer > watch > downloads > agents > calendar > music > keepAwake, with companion pairs (port `NotchCompactActivity`).
- Music strip: album art on the left, 7 bars on the right.

## Feature parity (80 upstream features)
Legend: **P** = port, **T** = toggle the built-in Windows setting, **N** = Windows already does it (link only), **D** = drop.

**Dynamic Island (13)**
| Feature | Status | Windows approach |
|---|---|---|
| notch core | P | see above |
| now playing | P | GSMTC (`GlobalSystemMediaTransportControlsSessionManager`), replacing upstream's MediaRemote + perl adapter |
| timer / pomodoro | P | |
| gestures | P | |
| lyrics | P | same LRCLIB API + LRC parser |
| live equalizer | P | WASAPI process loopback (falls back to system loopback), 7 bands at 50…12 kHz |
| downloads | P | `FileSystemWatcher` on Downloads (`.crdownload`/`.part`) |
| calendar | P | ICS feed URLs via Ical.Net; Graph/Google OAuth later. The Windows AppointmentStore is out because Mail & Calendar is retired |
| notifications | P | `UserNotificationListener` with package identity from a sparse package registered in Developer Mode (unsigned, per user, no admin); `NotificationChanged` events with a 10 s backup look (2 s polling where events aren't offered). "Hide the system banner" removes the original toast, which also takes it out of the notification centre. Ported in M3; a signed package for PCs without Developer Mode comes later (Windows SDK `makeappx`/`signtool` and a trusted certificate) |
| agents | P | `%USERPROFILE%\.claude\projects`, `.codex\sessions`, `.copilot`, OpenCode SQLite; `claude` CLI for limits. Ported in M3 with Cursor added beyond upstream (its plan usage from cursor.com, with Cursor's own sign-in); Claude profiles, Codex accounts and Codex resets come later |
| accessories | P | `DeviceWatcher` + Bluetooth battery property |
| watch | P | Windows.Graphics.Capture + `Windows.Media.Ocr` |
| mascot | P | |
| queue | D | no system-wide queue on Windows |

The island lock-screen overlay is also **D**: no app can draw over the Windows lock screen.

**Sound (5)**
| Feature | Status | Windows approach |
|---|---|---|
| mixer | P | `IAudioSessionManager2`/`ISimpleAudioVolume` + `IAudioMeterInformation` meters. Easier than macOS. Per-app output via undocumented `IAudioPolicyConfigFactory` (EarTrumpet does the same). Boosting above 100% is experimental and last |
| output switcher | P | `IPolicyConfig` |
| audio priority | P | `IMMNotificationClient` |
| mic mute | P | `IAudioEndpointVolume::SetMute` on every capture endpoint |
| music blocker | D | |

**Monitor (8)**
| Feature | Status | Windows approach |
|---|---|---|
| CPU | P | PDH, P/E-core split |
| GPU | P | PDH GPU Engine; NVML only while the dGPU is awake, so we don't keep Optimus from sleeping it |
| memory | P | |
| network | P | `GetIfTable2`; per-process needs ETW + admin |
| disk | P | |
| power | P | battery health and rate via `IOCTL_BATTERY_QUERY_INFORMATION` |
| connected devices | P | |
| fan control | P (beta, M12) | HP OMEN BIOS WMI (`hpqBIntM`; OmenMon, BSD-licensed, as reference) in the elevated helper. Temperatures via LibreHardwareMonitor on PawnIO |

Menu-bar readouts become per-metric tray icons, plus island/strip readouts.

**Windows & Dock (7)**
| Feature | Status | Windows approach |
|---|---|---|
| switcher | P | LL hook on Alt+Tab, `EnumWindows` Alt-Tab eligibility, live **DWM thumbnails** (no permission needed), MRU via foreground events |
| window layout | P | shortcuts, move to display, restore, modifier-drag in 9 regions; `SetWindowPos` corrected with `DWMWA_EXTENDED_FRAME_BOUNDS` |
| dock preview, dock click | N | taskbar thumbnails and click-to-minimize already exist |
| window maximizer, auto quit, spaces order | D | |

**Mouse & Keyboard (16)**
| Feature | Status | Windows approach |
|---|---|---|
| focus follows mouse | T | `SPI_SETACTIVEWINDOWTRACKING` |
| mouse acceleration | T | `SPI_SETMOUSE` |
| scroll inverter | P | |
| horizontal scroll | P | |
| linear scroll | P | |
| scroll speed | P | |
| smooth scroll | P | re-inject wheel deltas smaller than 120 |
| mouse button shortcuts | P | |
| click debounce | P | |
| key debounce | P | |
| super key | P | Caps Lock → Ctrl+Alt+Shift+Win; mask the Office-key launch |
| text snippets | P | LL hook + `SendInput` Unicode; skip password fields via UIA |
| quit protection | P | Alt+F4 / Ctrl+W / Ctrl+Q |
| mouse navigation, middle click | N | built-in side buttons and touchpad three-finger tap |
| scroll zoom | check | verify upstream's behaviour when porting; likely N (Ctrl+wheel) |

**Clipboard & Files (7)**
| Feature | Status | Windows approach |
|---|---|---|
| clipboard history | P | `AddClipboardFormatListener`; honours apps that exclude themselves from history |
| paste plain | P | |
| URL cleaner | P | port the rules |
| shelf | P | OLE drop target; shake to summon |
| Finder cut/paste | P (part) | only "paste image as PNG in Explorer" (folder found via `IShellWindows`). Ctrl+X/V is built in |
| Finder rename | N | F2 |
| disk image installer | D | |

**Energy & Display (4)**
| Feature | Status | Windows approach |
|---|---|---|
| keep awake | P | `PowerCreateRequest`; closed-lid mode by temporarily setting `LIDACTION` and restoring it, easier than on macOS; rules by app, power, display, battery |
| brightness | P | internal panel via WMI; external via DDC/CI `SetVCPFeature` 0x10/0x62; software dimming overlay |
| Bluetooth on sleep | P | `Windows.Devices.Radios` + suspend/resume notification |
| extra brightness | D | SDR panel (unless Settings shows HDR) |

**Tools (20) — all P**
| Feature | Windows approach |
|---|---|
| command bar | apps via `shell:AppsFolder` + Start Menu, files via Windows Search OLE DB, math/units/dates/emoji ported, `ms-settings:` |
| quick launcher | |
| quick toggles | dark mode, hide desktop icons, empty Recycle Bin, lock, eject, radios, plus **60↔240 Hz refresh toggle** for this laptop |
| quit apps | `WM_CLOSE` |
| kill process | |
| port manager | `GetExtendedTcpTable` |
| radial menu | |
| scratchpad | Markdig |
| cleaning mode | |
| color picker | |
| OCR + QR | `Windows.Media.Ocr` + ZXing |
| camera preview | `MediaCapture` (shared, read-only) + `MediaFrameReader` into a `WriteableBitmap`. The island's Camera page is ported in M3; the floating mirror window comes in M9 |
| wallpaper | `IDesktopWallpaper` |
| screenshot | Windows.Graphics.Capture + annotate editor |
| screen recorder | WGC + WASAPI loopback/process loopback + Media Foundation (NVENC) |
| media tools | FFmpeg |
| homebrew | becomes a **winget** manager |
| app updates | `winget upgrade` + Store |
| uninstaller | registry Uninstall keys + MSIX + leftover scan |
| cleaner | temp files, crash dumps, shader caches, Recycle Bin, schedule, plus the messaging-downloads organizer |

**Totals:** about 66 ported or toggled, about 14 dropped or native.

## Milestones (each ends with a running app; `docs/PARITY.md` updated each time)
- **M0 Setup.**
  - `winget install Microsoft.DotNet.SDK.10`. Recommend the VS Code C# Dev Kit or Visual Studio Community.
  - `git init`, `.gitignore`, LICENSE (GPL-3.0) + NOTICE crediting upstream.
  - Clone upstream into `upstream/` and record the SHA.
  - Create the solution and projects, add the NuGet packages, and get `dotnet build` and `dotnet test` green.
- **M1 Shell.**
  - `AppIdentity.cs` (name, id, data paths: the single place to rename).
  - SettingsStore + backup + dotfile.
  - FeatureCatalog (all 80 ids, groups, presets, WindowsStatus) + FeatureRuntime.
  - Tray icon (left click opens the panel, right click opens the menu).
  - Panel: Fluent flyout above the tray, with an icon tab bar gated like `PanelLayout.swift`. Sections are empty placeholders for now.
  - Settings window (WPF-UI NavigationView: General, Features hub, Island, Shortcuts, Advanced, About).
  - Features hub with install/uninstall and presets.
  - Hotkey and hook services, ForegroundWatcher, FullscreenDetector, single instance, launch at login, logging.
  - Tests: catalog, settings, shortcut parsing (ported from `FeatureCatalogTests`, `SettingsFileTests`, `ShortcutClearingTests` where they apply).
- **M2 Island core.**
  - Core: `NotchGeometry`, `NotchMotion`, `NotchStateMachine`, module order, compact activity priority, gesture thresholds, all with ported tests.
  - App: NotchWindow + NotchShape + animator, hover/click/gestures, display choice, x-offset, reserved-strip AppBar mode, fullscreen hiding, island settings page.
  - Modules:
    - **Now playing** (GSMTC session choice and preferred player, art, timeline, controls, track-change notice, artwork-tinted halo).
    - **Volume HUD** (endpoint volume callback; optionally swallow `VK_VOLUME_*` for fine steps, which also avoids the Windows flyout).
    - **Brightness notice** (WMI event; the native brightness flyout will still show because the OMEN firmware handles those keys).
    - **Battery and charging notices.**
    - **Timer / pomodoro / stopwatch.**
- **M3 Island extensions.** In order: lyrics → live equalizer → file drop/shelf on the island → downloads → calendar (ICS) → agents → notifications (sparse package registered in Developer Mode; the signed package for other PCs, with `makeappx`/`signtool` and a certificate, later) → camera → accessories → watch → mascot.
- **M4 Sound.**
  - Mixer panel section and island page: per-app sessions with icons, volume, mute, meters, pinning and order.
  - Per-app output device, output switcher hotkey, priority auto-switch, quieter on headphone disconnect, mic mute with island indicator.
  - Boost above 100% last, as an experiment.
- **M5 Monitor.**
  - Samplers: CPU, GPU, memory, disk, network, battery.
  - History graphs, top processes, alerts (port `SustainedAlertGate`).
  - Tray readout icons, island "system" page and strip readouts, speed test (same endpoint as upstream), connected devices.
- **M6 Energy & quick wins.**
  - Keep awake (+ island activity), brightness (internal, DDC, dim), Bluetooth on sleep.
  - Focus-follows-mouse and acceleration toggles.
  - Quick toggles (incl. refresh rate), quit apps, kill process, port manager.
  - Optional elevated-at-login task.
- **M7 Windows & input.** Switcher, window layout, quit protection, scroll family, mouse buttons, debounce, super key, snippets.
- **M8 Clipboard & files.** History, auto-clear, paste plain, URL cleaner, Shelf window, paste image into Explorer.
- **M9 Tools.** Command bar, quick launcher, radial menu, scratchpad, cleaning mode, color picker, OCR/QR, camera preview window, wallpaper.
- **M10 Capture & media.** Screenshot + editor, recorder + editor, media tools.
- **M11 App management.** winget manager, app updates, uninstaller, cleaner + schedule.
- **M12 Elevated helper.** `Notchly.Helper` service (named pipe locked to the user): LibreHardwareMonitor temperatures, OMEN fan curves (port the policy in `FanControlSupport.swift`), per-process network via ETW.

## Execution after approval
- I'll do M0 → M1 → M2 in this session, with a runnable build and a short demo checklist after each milestone.
- M3 onward: each milestone (or each sub-feature you ask for) starts with its own short plan in plan mode.

## Process for your later change/feature requests
Each request gets a plan with these sections:
1. **Context**: what and why.
2. **Upstream reference**: the Swift files, keys and tests involved; or "new, Windows-only".
3. **Windows mechanism**: APIs and libraries, permission or elevation needs, risks.
4. **Changes**: Core logic + tests, Platform wrapper, Feature service, UI surfaces (panel section, settings page, island module), settings keys and defaults, catalog/PARITY entry.
5. **Verification**: unit tests plus a manual checklist on this laptop.

## Risks to check early (spikes in M1/M2)
1. WPF layered-window animation cost at 240 Hz. Fallback: animate a window region, or host Windows.UI.Composition.
2. Whether `WDA_EXCLUDEFROMCAPTURE` works on a layered window.
3. Undocumented audio COM interfaces (`IPolicyConfig`, `IAudioPolicyConfigFactory`) on Insider build 26300. Feature-detect them and fail softly.
4. UIPI: hooks and `SendInput` don't reach elevated windows unless we run elevated (M6 option).
5. OMEN volume keys must arrive as `VK_VOLUME_*` for the HUD swallow to work. Check with a hook logger.
6. `UserNotificationListener` events with a sparse package; fall back to polling.

## Verification (per milestone)
- `dotnet build Notchly.sln -c Debug` and `dotnet test` are green, with ported upstream specs passing.
- Run `src/Notchly.App` and check:
  - **M1:** tray icon; panel opens above the clock; feature install/uninstall persists across restarts; settings export → import round-trips.
  - **M2:**
    - The island sits centered under the webcam (x≈786 DIP, 135×24).
    - Hover expands it with the spring.
    - Clicks on transparent areas reach the Chrome tabs underneath.
    - Spotify or a YouTube tab shows art and working controls.
    - Volume keys show the island HUD.
    - Plugging in or unplugging the charger shows a notice.
    - F11 video or a fullscreen game hides the island.
    - In strip mode, maximized windows start below the strip.
    - Idle CPU stays under about 1% and memory under about 150 MB (Task Manager).
