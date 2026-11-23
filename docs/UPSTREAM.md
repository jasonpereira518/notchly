# Upstream reference

- Repository: <https://github.com/vorssaint/vorssaint-utils> (GPL-3.0-or-later)
- Pinned commit: `faada18df1b90572cddc6182ce7fc04e0eb1435c` (2026-11-18, "docs: record display, search and installer fixes")
- Local checkout (read-only, gitignored): `upstream/`

```powershell
git clone https://github.com/vorssaint/vorssaint-utils.git upstream
git -C upstream checkout faada18df1b90572cddc6182ce7fc04e0eb1435c
```

## Porting conventions

1. **Pure logic is translated file for file** into `src/Notchly.Core`. Each C# file starts with
   `// Port of <upstream path>`. The matching upstream tests in `upstream/Tests/*.swift` go to
   `tests/Notchly.Core.Tests` as xUnit tests.
2. **Settings keys and default values are kept verbatim** (`DefaultsKey.notchHoverDelay`, …), so a
   Swift line maps to a C# line. Windows-only keys are marked `// Windows-only` in `DefaultsKey`.
3. **Feature ids are the upstream `AppFeature` raw values** (`switcher`, `notchLyrics`, …). Never
   rename one: they are stored in the settings file.
4. **Platform glue is rewritten** against Windows APIs under `src/Notchly.App/Platform`. It never
   goes into Core.
5. **No Vorssaint branding**: our own name, icon and the Windows 11 Fluent look.
6. To move to a newer upstream commit, run `git -C upstream log --stat <old>..<new> -- Sources Tests`,
   port the relevant changes, then update the pinned SHA above.
