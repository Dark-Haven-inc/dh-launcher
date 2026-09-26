# Dark Haven Launcher

A from-scratch launcher for [Space Station 14](https://spacestation14.com/): a first-class view of
the **Dark Haven** region network plus a full browser of any public SS14 server.

Status: **Phase 6** — Avalonia GUI (blue HUD), end-to-end connect (fetch → content/engine download
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
and copied to the loader output automatically — no SS14 install needed.

## Release

Releases are GitHub Releases on the public, releases-only
[`frontier15-launcher`](https://github.com/Dark-Haven-inc/frontier15-launcher/releases): players run
`Setup.exe` once, every later version arrives as a small in-app delta (Velopack). Push a `v*` tag to
build one (`.github/workflows/release.yml`), or run `scripts/pack-release.ps1 -Version x.y.z`
locally. Details in [`docs/RELEASING.md`](docs/RELEASING.md).

## Tech

C# / .NET 10, Avalonia 11 (UI), Velopack (install + self-update). Pure-managed deps where possible:
`Microsoft.Data.Sqlite`, `ZstdSharp.Port`, `SauceControl.Blake2Fast`, `NSec.Cryptography` (Ed25519).

## Licence

MIT — SS14 launcher forks are conventionally MIT. See [`LICENSE`](LICENSE).
