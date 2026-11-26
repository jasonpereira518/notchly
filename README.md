# Notchly

A Windows 11 port of [vorssaint-utils](https://github.com/vorssaint/vorssaint-utils), the free macOS menu-bar
toolkit, built for an HP OMEN 16 with no physical notch. It starts with the **Dynamic Island**: a notch drawn under the
webcam at the top centre of the screen, with music, volume/brightness/battery notices and timers.

Status: milestones M0–M10a (setup, shell, island core, the island extensions: lyrics, live equalizer, files, downloads, calendar, AI agents, notifications, camera, accessories, Watch and the companion; sound: mixer, output switcher, device priority, microphone mute; the system monitor; energy and quick tools; windows and input; clipboard and files; the small tools; the command bar; the quick panel and radial menu; and screenshots). See
[docs/PARITY.md](docs/PARITY.md) for every upstream feature and where it stands, and [docs/ROADMAP.md](docs/ROADMAP.md)
for the porting plan.

## What works now

- **Tray icon and panel** — left click opens a flyout with island controls and quick timers; right click for a menu.
- **Settings** — General (launch at sign-in, theme, export/import, dotfile), **Features hub** (install/uninstall per
  feature, presets, status of all 80 upstream features), **Dynamic Island**, About.
- **Dynamic Island**
  - A notch (or capsule) 135×24 DIP centred under the webcam; transparent areas pass clicks through.
  - Hover grows it slightly; click opens it (or enable *Open on hover*). Click elsewhere, Esc, or the chevron closes it.
  - Pages: **Home** (now playing + transport; volume and built-in screen brightness side by side; a speaker menu to switch the output for every app; a battery chip that opens its watts in or out, time left, charge and health), **Music** (art, seekable timeline),
    **Files**, **Downloads**, **Calendar**, **AI agents**, **Notifications**, **Camera**, **Watch**, **Timer** (timer, Pomodoro, stopwatch).
  - **Lyrics** on the Music page: synced verses that light up with the song, a ±0.25 s timing row, `.lrc`
    import, and opt-in online lookup from lrclib.net (with musicbrainz.org alias checks as a last resort).
  - **Live equalizer** (on by default): the closed music strip's bars follow what the PC plays on its default
    output, read with WASAPI loopback; nothing is stored or sent.
  - **Files** page: drag files, links, text or images toward the notch and it grows into a drop zone. Shelved items
    keep across restarts; drag them back out into any app, open, share, pin, group into piles, or Create ZIP.
  - **Downloads**: while a browser saves into your Downloads folder (or one you choose), the closed island shows the
    file's name and the size so far; a notice names it when it finishes, and the Downloads page lists the folder's
    newest files to open, show in Explorer or add to Files. Windows browsers publish no percentage, so size and speed
    stand in for it.
  - **Calendar**: add your Google, Outlook, Canvas or iCloud calendars by their private iCal link (or an .ics file).
    The page shows the week strip and the next seven days, with a month grid; the closed island can count down to an
    event before it starts (every event or only those you right-click) and through the last hour of one under way.
    Links are encrypted on this PC and never backed up; events stay in memory.
  - **AI agents** (opt in): Claude Code, Codex, OpenCode and GitHub Copilot usage read from their logs on this PC,
    plus Cursor's plan usage from cursor.com with Cursor's own sign-in. The page shows plan limits per agent, API
    value, the turns under way, a trend, top models and projects, and thirteen weeks of activity; the closed island
    shows a working agent's mark with its time, tokens, cost or limit, and notices say when a task finishes, a limit
    runs low or renews, or a daily budget is reached. Only counters, model names, times and folder names are kept.
  - **Notifications**: new Windows notifications show beside the camera for 3 s (the app's icon and the sender or
    title on one side, the message on the other), then wait on the Notifications page with Open, Dismiss and Clear
    All; *Hide the system banner* removes the original from Windows once the island shows it. Do Not Disturb keeps
    banners away while the page still lists them. One-time setup in Settings → Dynamic Island → Notifications:
    with Developer Mode on, Notchly registers a small sparse package (`Package\AppxManifest.xml`, no admin rights,
    no certificate) so Windows lets it read notifications, restarts, and Windows asks for your permission.
    Messages stay in memory and are cleared when you lock the PC or turn this off.
  - **Camera**: a live mirror before a video call. The page stays off until you click *Open camera*; the image then
    fills the page, mirrored, with *Stop camera* (and a camera menu when there is more than one) in the top row.
    The camera stops the moment you leave the page, the island closes or the PC locks or sleeps. It opens the camera
    in Windows' shared mode, so a call can use it at the same time; nothing is recorded or saved.
  - **Accessories**: when headphones, a speaker, a game controller, a keyboard or a mouse connects over Bluetooth, the
    island shows its icon and name for 4 s, with its battery when Windows reports one; an accessory dropping to 20%
    warns once, and again only after it has charged past 25%. Phones and computers stay quiet, Do Not Disturb holds
    the notices back, and clicking one opens Windows' Bluetooth settings. Only names, classes and battery levels
    Windows already shows are read, in memory.
  - **Watch**: *Choose Area*, then drag over any part of any window — a progress bar, a build status, a score — and
    the closed island shows its reading beside an eye. Tell it to speak up when the area changes, stops changing for
    30 s, shows some text (“Done”) or its number reaches a target; a notice (with an optional sound) says so, and
    *Watch Again* starts over. The window is read with Windows.Graphics.Capture, so it can move or be covered;
    minimized or locked pauses watching, and closing it ends the watch. Text comes from Windows' own recognition on
    this PC; only the chosen area is read, in memory, and nothing is saved or sent.
  - **Companion** (opt in): a little friend who rests in the closed island's wing beside the camera when nothing else
    shows, in one of two styles (minimal, four shapes, or a robot) and seven colours. It blinks, follows the pointer
    with its eyes, answers a resting pointer with heart eyes and dozes off when left alone; now and then it strolls
    through the island, and it hides in the island after half a minute of quiet. It comes out to react to music
    starting, timers, downloads, the microphone, unlocking the PC, agents finishing, events starting, the charger,
    Watch and files dropped in, standing in for the notice's icon when the notice brings the news. With Windows'
    animation effects off it only changes face, and Battery Saver keeps it at rest.
  - **Sound** (M4): a **Volume mixer** page on the island and a section in the panel list every app that uses audio, with
    its icon, a level slider with a live meter, mute, reset, pinning, order, and (through Windows' own per-app output)
    the speaker or headphones it plays to; call apps, Zoom and music-production apps keep their own volume. Windows plays
    an app up to 100%, so there is no boost. The **output switcher** steps through the outputs you tick with a shortcut
    (Ctrl+Alt+Win+S); **device priority** makes the first connected device in your list the default as devices come and
    go; **lower volume when headphones disconnect** drops the speakers to a level you pick and restores it when the
    headphones return; **mute microphone** (Ctrl+Alt+Win+M, the tray menu or Settings → Sound) mutes every microphone and
    gives each back exactly what it had, with a badge on the tray icon.
  - **System monitor** (M5): a **System** page on the island (press `I`) with cards for the processor, graphics, memory,
    battery, network, disk, power and connected devices, balanced across the island's width with a meter each; a card opens its
    graph, detail rows, each core (performance and efficiency cores marked), the top apps and, for Network, a speed test. The
    panel has System, Network, Disk and Power sections with usage bars, history graphs and expandable top apps. Optional alerts
    (sustained high processor use, critical memory, a nearly full disk, a low battery) and optional tray readouts (one small
    icon per metric, values or bars) are off until you turn them on in Settings → System monitor. Readings are taken only while
    something shows them. Windows reports no temperatures, fan speeds, system or adapter power, or per-app network use to apps,
    so those wait for the elevated helper (M12).
  - **Energy** (M6): **Keep awake** holds Windows awake for a length you pick, until a time, or until you stop it, with extend buttons,
    a battery limit, pause while locked, a pointer nudge, rules (an external display, mains power, certain apps), a countdown on
    the island and in the tray menu, and closed-lid mode that changes Windows' lid action for the session and always puts it
    back. **Displays** has a brightness slider per display in the panel and on the island (the built-in panel, external monitors
    over DDC/CI, and a software dim for the rest), shortcuts, and dimming below the panel's minimum. **Bluetooth on sleep** turns
    the radio off as the PC sleeps and back on at wake. Settings → Energy has the options.
  - **Quick tools** (M6): **Quick toggles** in the panel (dark mode, microphone, Recycle Bin, quit apps, eject drives, hidden files,
    desktop icons, lock, display off, screen saver, refresh rate, Wi-Fi, Bluetooth), hidden and reordered in Settings → Tools.
    **Quit apps** lists open apps and asks each to close (two shortcuts, kept apps). **Kill process** and the **port manager**
    are in Settings → Processes and ports: every kill is confirmed and checked against the process's start time, and a process
    Notchly may not stop gets one administrator prompt. **Focus follows mouse** and **pointer acceleration** (Settings → Mouse)
    flip Windows' own settings and give yours back. **Start with administrator rights** (Settings → General) starts Notchly
    elevated at sign-in through a scheduled task, so its shortcuts reach administrator windows.
  - **Windows and input** (M7, all off until you turn them on in Settings → Windows, Mouse and Keyboard): an **app switcher** that
    replaces Alt+Tab with live window previews (recent order, search, W to close, Q to quit, the wheel and the mouse); **window
    layout** by shortcut (halves, thirds, quarters, sixths, corners, maximize, display moves, restore), by dragging with a
    modifier held, by dragging a title bar to a screen edge, or by holding a chord and moving the pointer; **scroll** options
    for the mouse wheel only (reverse, sideways, zoom, one notch at a time, faster with a key, smooth); **mouse button shortcuts**
    and a desktop drag; **bounce filters** for a worn mouse button or key; a **super key** (Caps Lock as Ctrl+Alt+Shift+Win);
    **text snippets** with variables and a searchable library; and **quit and close protection** for Alt+F4, Ctrl+Q and Ctrl+W.
    They step aside in full-screen games, and cannot reach apps running as administrator unless Notchly does too.
  - **Clipboard and files** (M8, in Settings → Clipboard and files; history, paste as plain text, the link cleaner and Explorer image
    paste start switched off): a **clipboard history** of text, images and files with search, pins, a limit and an age (a page on the
    island, and a window on Ctrl+Alt+Win+V; it skips what password managers and other apps ask not to be recorded), **clearing the
    clipboard by itself** after a delay or on sleep, display off or lock, **paste as plain text** (Ctrl+Alt+Shift+V; the clipboard is
    put back as it was), a **link cleaner** that takes tracking parameters out of copied links, **image paste** (Ctrl+V with a copied
    picture in a folder makes a PNG file there), and a floating **Shelf** card (shortcut, shake while dragging, a drop zone above the
    tray, a peek from the screen edge) that shares its items with the island's Files page.
  - **Small tools** (M9a, in Settings → Tools; every shortcut starts switched off): a **color picker** (a frozen screen and a magnifier;
    HEX, RGB, HSL or SwiftUI), **copy text from screen** (drag over text and it is read on this PC; a QR code opens its own panel),
    **cleaning mode** (locks the keyboard and the wheel until five Escapes; Ctrl+Alt+Delete cannot be blocked), a floating **camera
    mirror**, a **scratchpad** (tabs, Markdown with a preview and tick boxes, kept in %APPDATA%\Notchly) and a **wallpaper** gallery
    (Windows' own pictures and yours, on every display or one).
  - **Command bar** (M9b, in Settings → Tools; the shortcut, Alt+Space, starts switched off): one field that finds and runs what Notchly
    does: apps, open windows, Windows settings pages, folders, emoji (`:fire`), files in folders you name (through Windows Search),
    menu items of the app you came from, what you copied, what you had selected (copy, UPPER, lower, Title Case, count), and your own
    links. It answers sums, unit conversions, dates and colors as you type (`2+3*4`, `100 km to mi`, `in 3 weeks`, `#00bc7d to rgb`),
    learns what you run, and lets you pin rows, give them your own names (Ctrl+K) and hide them. Nothing you type is saved.
  - **Quick panel and radial menu** (M9c, in Settings → Tools): **Ctrl+Win+V** opens a grid of the tools you reach for most (keep awake,
    quick toggles, microphone, copy text, color picker, clipboard, window layout, cleaning mode, clean URL, camera, scratchpad) with
    arrows, 1 to 9 and Return, a customize switch to reorder and hide tiles, and small option cards; with the island on it opens the
    island's **Tools** page instead. The **radial menu** (off until turned on) opens a wheel of up to 12 slices (apps, files, links, key
    combinations, tools, toggles, window layouts, media keys, one submenu level) at the pointer: hold the Forward mouse button or
    Ctrl+Alt+Win+Space, move toward a slice and let go to run it. Several wheels, each with its own color, shortcut and button; an editor
    with a live preview, an icon grid, your own images and site icons for links.
  - **Screenshots** (M10a, in Settings → Tools; every shortcut starts off): **Ctrl+Alt+Win+4** chooses an area (drag it, or click a
    window; R repeats the last area, L shows a loupe, C copies the color under it, 1 to 5 switch between screenshot, copy text, color and
    scrolling), **+3** the whole screen, **Shift+4** a scrolling capture (it can scroll the page for you), **+E** the last capture,
    **+H** the recent ones, **+P** a picture from the clipboard. A small preview offers Edit, Pin, Copy, Save and Share; the editor has arrow,
    box, oval, line, pen, marker, text, counters, stickers, blur (pixelate, soft or erase, optionally only on text), redact, spotlight,
    crop, canvas expansion and pictures, selectable text from the capture, a backdrop and a watermark, undo, and save names with
    patterns (`%year %mo %d %#`). The island has a **Captures** page. No upload links, and no HEIC.
  - Live activities in the closed island: music (cover + bars), timer countdown, a watched area, downloads, event countdown, working agents.
  - Notices: volume (optionally owns the volume keys — no Windows flyout), brightness, mic mute, charger and low
    battery, track changes.
  - Gestures: scroll down on it to open, up to close; touchpad sideways swipe changes track.
  - Hides during full-screen games, videos and presentations.
  - **Menu bar at the top** (optional): a bar like a Mac's, with the app in front in bold and its menus left of
    the notch, and the clock and date on the right (click for notifications and the calendar). Classic menus drop down
    under the bar; Notepad, VS Code and other modern menus open the app's own menu; apps without menus get File, Edit,
    View, Window and Help, whose items send the usual shortcut. Maximized windows start below it; off, they get their
    full height back and the island floats over them. The bar takes the colour along the top of the app in front, with
    dark text on light apps, and the notch keeps a faint light edge wherever it would melt into a dark bar.
  - `Notchly.exe --toggle-island` opens/closes it (bind it to a key with any launcher).

`Notchly.exe --import-settings <file>` applies a settings backup, to the running Notchly when there is one.

## Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build Notchly.sln
dotnet test tests/Notchly.Core.Tests
dotnet run --project src/Notchly.App
```

Settings: `%APPDATA%\Notchly\settings.json` · Logs: `%LOCALAPPDATA%\Notchly\logs` ·
Dotfile applied at launch: `%USERPROFILE%\.config\notchly\settings.json`.

Regenerate the parity table after changing a feature's status:

```powershell
dotnet run --project tools/ParityGen -- docs/PARITY.md
```

## Layout

```
src/Notchly.Core/      pure logic, no Windows APIs (settings store, feature catalog, shortcuts,
                     island geometry / springs / gestures / timer) — ported from upstream Swift
src/Notchly.App/       WPF app: Shell/ (tray, panel, settings), Platform/ (Win32/WinRT wrappers),
                     Island/ (window + controller)
tests/               xUnit, including ports of upstream tests
docs/UPSTREAM.md     pinned upstream commit and porting conventions
upstream/            read-only clone of vorssaint-utils (gitignored)
```

## Licence

GPL-3.0-or-later, as upstream; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). The Vorssaint name, icon and
look are not used.
