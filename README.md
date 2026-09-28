# Dark Haven Launcher

A from-scratch launcher for [Space Station 14](https://spacestation14.com/): a first-class view of
the **Dark Haven** region network plus a full browser of any public SS14 server.

Status: **Phase 6** — Avalonia GUI (themes: monochrome, legacy), end-to-end connect (fetch → content/engine download
→ auth → launch), the forked Dark Haven engine bundled and served in place of the public CDN, and
self-update + one-click install via Velopack. See
[`whimsical-petting-reddy.md`](../../.claude/plans/whimsical-petting-reddy.md) for the plan,
[`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the reverse-engineered SS14 connect protocol, and
[`docs/RELEASING.md`](docs/RELEASING.md) for cutting a release.

## Layout

| Project | What |
| --- | --- |
| `src/DarkHaven.ContentDb` | shared: content-DB schema + the loader's `IFileApi` over it |
| `src/DarkHaven.Launcher` | class library — APIs, content/engine download, auth, launch, self-update |
| `src/DarkHaven.Cli` (`dhlauncher`) | dev/debug console |
| `src/DarkHaven.Loader` | in-process engine loader (+ vendored engine native libs in `natives/`) |
| `src/DarkHaven.App` | Avalonia UI (`DarkHavenLauncher.exe`) |
| `Robust.LoaderApi` | submodule, MIT — the engine⇄loader interface contract |
| `tests/DarkHaven.Launcher.Tests` | xUnit |

## Build & try

```
git submodule update --init
dotnet build && dotnet test
dotnet run --project src/DarkHaven.App                       # the launcher GUI
dotnet run --project src/DarkHaven.Cli -- probe --hub        # dev console
dotnet run --project src/DarkHaven.Cli -c Release -- connect ss14://frontier.radiant-sector.ru
```

The engine native libs (`SDL3`, `OpenAL`, …) are vendored in `src/DarkHaven.Loader/natives/win-x64/`
and copied to the loader output automatically — no SS14 install needed. On Linux they come from the
engine's own `Robust.Natives` NuGet packages; the system provides freetype, EGL and zlib.

Linux (x64) is supported: `dotnet run` works as above, releases ship as an AppImage that installs
itself on first run, with a one-line `install.sh` and an AUR package (`docs/RELEASING.md`); `ss14://`
links go through a desktop entry, and the saved account token is encrypted with a per-user key file
next to `settings.db` instead of Windows DPAPI.

## Themes

НАСТРОЙКИ → ВИД → Тема switches the look live (saved as config `Theme`). A theme is a *layout* — a main
window with its views and the control styles they use (`Views/` + `Themes/Styles/Monochrome.axaml`, or
`Views/Legacy/` + `Themes/Styles/Legacy.axaml`, the pre-redesign blue HUD) — painted in a *palette*
(`Themes/Palettes/*.axaml`). A new palette on an existing layout is one file there plus one line in
`Theme.All` (`src/DarkHaven.App/Themes/Theme.cs`, which lists the keys every palette defines).
`retro` is a text-mode program on a green CRT, after Midnight Commander and cool-retro-term: its own window
(`Views/Retro/`: a menu bar, the page in a double frame, a function-key bar — F1–F7 open the pages) and its own
main pages, laid out as text screens (titled boxes, `key ····· value` lines, tables); the rest are the monochrome
pages, which `Themes/Styles/Retro.axaml` redraws in text-mode terms (`[ bracketed ]` buttons, `[x]` boxes,
reverse video, dotted rules). НАСТРОЙКИ and АДМИН open over the blurred page they were opened from;
`Controls/CrtScreen.cs` lays the tube over it all — glow, scanlines, grain, a rolling band, flicker, switched in
НАСТРОЙКИ → ВИД (config `Retro.*`); the moving ones stand still while the window isn't active. Monochrome and retro
are set in JetBrains Mono (the launcher's default font); legacy keeps its Inter.
The button next to the monochrome theme recolors it live with two HSV picks, saved per theme
(`Theme.<id>.Base`, `Theme.<id>.Active`), worked out much like Material You (`Themes/ColorMath.cs`): a
pick sets the hue, and each color's lightness follows from what it must stand out against. The
background is the first pick (pushed off mid-gray, so text keeps 9:1); every gray keeps the contrast it
had with the original background, so a light background gets dark text. The accent fills buttons and
checked boxes as long as it looks different from the background (hue counts), and is moved to 4.5:1
where it is a 1–2 px line or text (selected chips, the nav marker).

## Release

Releases are GitHub Releases on the public, releases-only
[`frontier15-launcher`](https://github.com/Dark-Haven-inc/frontier15-launcher/releases): players run
`Setup.exe` once, every later version arrives as a small in-app delta (Velopack). Push a `v*` tag to
build one (`.github/workflows/release.yml`), or run `scripts/pack-release.ps1 -Version x.y.z`
(`-Rid linux-x64` for the Linux AppImage) locally. Details in [`docs/RELEASING.md`](docs/RELEASING.md).

## Tech

C# / .NET 10, Avalonia 11 (UI), Velopack (install + self-update). Pure-managed deps where possible:
`Microsoft.Data.Sqlite`, `ZstdSharp.Port`, `SauceControl.Blake2Fast`, `NSec.Cryptography` (Ed25519).

## Licence

MIT — SS14 launcher forks are conventionally MIT. See [`LICENSE`](LICENSE).
