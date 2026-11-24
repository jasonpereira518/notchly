# Notchly

A Windows 11 port of [vorssaint-utils](https://github.com/vorssaint/vorssaint-utils), the free macOS menu-bar
toolkit, built for an HP OMEN 16 with no physical notch. It starts with the **Dynamic Island**: a notch drawn under the
webcam at the top centre of the screen, with music, volume/brightness/battery notices and timers.

Status: milestones M0–M2 (setup, shell, island core) and the first M3 extensions (lyrics, live equalizer, files, downloads, calendar, AI agents, notifications, camera, Watch). See
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
