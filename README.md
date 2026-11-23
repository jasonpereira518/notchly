# Notchly

A Windows 11 port of [vorssaint-utils](https://github.com/vorssaint/vorssaint-utils), the free macOS menu-bar
toolkit, built for an HP OMEN 16 with no physical notch. It starts with the **Dynamic Island**: a notch drawn under the
webcam at the top centre of the screen, with music, volume/brightness/battery notices and timers.

Status: milestones M0–M2 (setup, shell, island core) and the first M3 extensions (lyrics, live equalizer, files, downloads, calendar). See
[docs/PARITY.md](docs/PARITY.md) for every upstream feature and where it stands, and [docs/ROADMAP.md](docs/ROADMAP.md)
for the porting plan.

## What works now

- **Tray icon and panel** — left click opens a flyout with island controls and quick timers; right click for a menu.
- **Settings** — General (launch at sign-in, theme, export/import, dotfile), **Features hub** (install/uninstall per
  feature, presets, status of all 80 upstream features), **Dynamic Island**, About.
- **Dynamic Island**
  - A notch (or capsule) 135×24 DIP centred under the webcam; transparent areas pass clicks through.
  - Hover grows it slightly; click opens it (or enable *Open on hover*). Click elsewhere, Esc, or the chevron closes it.
  - Pages: **Home** (now playing + transport, volume, battery, output device), **Music** (art, seekable timeline),
    **Files**, **Downloads**, **Calendar**, **Timer** (timer, Pomodoro, stopwatch).
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
  - Live activities in the closed island: music (cover + bars), timer countdown, downloads, event countdown.
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
