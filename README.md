# Islet (working name)

A Windows 11 port of [vorssaint-utils](https://github.com/vorssaint/vorssaint-utils), the free macOS menu-bar
toolkit, built for an HP OMEN 16 with no physical notch. It starts with the **Dynamic Island**: a notch drawn under the
webcam at the top centre of the screen, with music, volume/brightness/battery notices and timers.

Status: milestones M0–M2 (setup, shell, island core) and the first M3 extensions (lyrics, live equalizer, files). See
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
    **Files**, **Timer** (timer, Pomodoro, stopwatch).
  - **Lyrics** on the Music page: synced verses that light up with the song, a ±0.25 s timing row, `.lrc`
    import, and opt-in online lookup from lrclib.net (with musicbrainz.org alias checks as a last resort).
  - **Live equalizer** (opt-in): the closed music strip's bars follow the playing app's real audio, read with
    Windows process loopback; nothing is stored or sent.
  - **Files** page: drag files, links, text or images toward the notch and it grows into a drop zone. Shelved items
    keep across restarts; drag them back out into any app, open, share, pin, group into piles, or Create ZIP.
  - Live activities in the closed island: music (cover + bars), timer countdown.
  - Notices: volume (optionally owns the volume keys — no Windows flyout), brightness, mic mute, charger and low
    battery, track changes.
  - Gestures: scroll down on it to open, up to close; touchpad sideways swipe changes track.
  - Hides during full-screen games, videos and presentations; optional reserved top strip (AppBar) so maximized
    windows start below it.
  - `Islet.exe --toggle-island` opens/closes it (bind it to a key with any launcher).

## Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build Islet.sln
dotnet test tests/Islet.Core.Tests
dotnet run --project src/Islet.App
```

Settings: `%APPDATA%\Islet\settings.json` · Logs: `%LOCALAPPDATA%\Islet\logs` ·
Dotfile applied at launch: `%USERPROFILE%\.config\islet\settings.json`.

Regenerate the parity table after changing a feature's status:

```powershell
dotnet run --project tools/ParityGen -- docs/PARITY.md
```

## Layout

```
src/Islet.Core/      pure logic, no Windows APIs (settings store, feature catalog, shortcuts,
                     island geometry / springs / gestures / timer) — ported from upstream Swift
src/Islet.App/       WPF app: Shell/ (tray, panel, settings), Platform/ (Win32/WinRT wrappers),
                     Island/ (window + controller)
tests/               xUnit, including ports of upstream tests
docs/UPSTREAM.md     pinned upstream commit and porting conventions
upstream/            read-only clone of vorssaint-utils (gitignored)
```

## Licence

GPL-3.0-or-later, as upstream; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). The Vorssaint name, icon and
look are not used.
