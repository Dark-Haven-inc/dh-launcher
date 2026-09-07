# Dark Haven Launcher

A from-scratch launcher for [Space Station 14](https://spacestation14.com/): a first-class view of
the **Dark Haven** region network plus a full browser of any public SS14 server.

Status: **Phase 0** (skeleton + protocol probe). See
[`whimsical-petting-reddy.md`](../../.claude/plans/whimsical-petting-reddy.md) for the full plan
and [`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the reverse-engineered SS14 connect protocol.

## Layout

| Project | What |
| --- | --- |
| `src/DarkHaven.Launcher` | class library — all launcher logic (APIs, content, engine, auth, update) |
| `src/DarkHaven.Cli` (`dhlauncher`) | dev/debug console — `probe`, later `login` / `connect` |
| `src/DarkHaven.Loader` | in-process engine loader (phase 1) |
| `src/DarkHaven.App` | Avalonia UI (phase 4) |
| `Robust.LoaderApi` | submodule, MIT — the engine⇄loader interface contract |
| `tests/DarkHaven.Launcher.Tests` | xUnit |

## Build & try

```
git submodule update --init
dotnet build
dotnet test
dotnet run --project src/DarkHaven.Cli -- probe ss14://game.hardlight.space
dotnet run --project src/DarkHaven.Cli -- probe --hub
```

## Tech

C# / .NET 10, Avalonia 11 (UI). Pure-managed deps where possible: `Microsoft.Data.Sqlite`,
`ZstdSharp.Port`, `SauceControl.Blake2Fast`, `NSec.Cryptography` (Ed25519).

## Licence

TBD — SS14 launcher forks are conventionally MIT.
