# Dark Haven Launcher

A from-scratch launcher for [Space Station 14](https://spacestation14.com/): a first-class view of
the **Dark Haven** region network plus a full browser of any public SS14 server.

Status: **Phase 3** — headless launcher core works end to end (fetch → content/engine
download → auth → launch → connect). No GUI yet (phase 4). See
[`whimsical-petting-reddy.md`](../../.claude/plans/whimsical-petting-reddy.md) for the full plan
and [`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the reverse-engineered SS14 connect protocol.

## Layout

| Project | What |
| --- | --- |
| `src/DarkHaven.ContentDb` | shared: content-DB schema + the loader's `IFileApi` over it |
| `src/DarkHaven.Launcher` | class library — APIs, content/engine download, auth, launch |
| `src/DarkHaven.Cli` (`dhlauncher`) | dev/debug console |
| `src/DarkHaven.Loader` | in-process engine loader |
| `src/DarkHaven.App` | Avalonia UI (phase 4) |
| `Robust.LoaderApi` | submodule, MIT — the engine⇄loader interface contract |
| `tests/DarkHaven.Launcher.Tests` | xUnit |

## Build & try

```
git submodule update --init
dotnet build && dotnet test
dotnet run --project src/DarkHaven.Cli -- probe --hub
dotnet run --project src/DarkHaven.Cli -- login GODWINCH --password …
dotnet build src/DarkHaven.Loader -c Release
./scripts/copy-engine-natives.ps1 -Config Release       # local loader natives, until phase 6
dotnet run --project src/DarkHaven.Cli -c Release -- connect ss14://frontier.radiant-sector.ru
```

## Tech

C# / .NET 10, Avalonia 11 (UI). Pure-managed deps where possible: `Microsoft.Data.Sqlite`,
`ZstdSharp.Port`, `SauceControl.Blake2Fast`, `NSec.Cryptography` (Ed25519).

## Licence

TBD — SS14 launcher forks are conventionally MIT.
