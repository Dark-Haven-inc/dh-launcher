# DarkHaven.Loader

A tiny console exe that boots the game engine in-process. Port of `space-wizards/SS14.Launcher`'s
`SS14.Loader`, using `Robust.LoaderApi` (the engine⇄loader contract, MIT, vendored as a submodule).

```
DarkHaven.Loader <engineZip> <signatureHex> <publicKeyFile> [engineArg...]
```

## What it does

1. **Verify** the engine zip's Ed25519 signature against `<publicKeyFile>` (the SS14
   `signing_key`, PKIX PEM). In non-RELEASE builds `SS14_DISABLE_SIGNING=true` bypasses this.
2. Open the engine zip; register `AssemblyLoadContext.Default.Resolving` /
   `ResolvingUnmanagedDll` so `Robust.Client` and its managed deps load **out of the zip**, and
   native libs load from **next to the loader exe**.
3. Read env:
   - `SS14_LOADER_CONTENT_DB` + `SS14_LOADER_CONTENT_VERSION` → mount that `ContentVersion`'s
     files as the engine VFS via `DarkHaven.ContentDb.ContentDbFileApi` (`ApiMount(api, "/")`).
   - `SS14_LOADER_OVERLAY_ZIP` → an extra zip mounted *first* (masks base files; content bundles).
   - `SS14_LAUNCHER_PATH` → enables redial (engine asks the launcher to reconnect elsewhere).
4. Find `[assembly: LoaderEntryPoint]` on `Robust.Client`, `Activator.CreateInstance` it, and call
   `ILoaderEntryPoint.Main(IMainArgs)`. Control never returns until the client exits.

## Native libraries

The engine zip is **managed only**. ~12 native libs must sit next to `DarkHaven.Loader.exe`:
`OpenAL32 SDL3 e_sqlite3 freetype6 glfw3 libEGL libGLESv2 libfluidsynth-3 libsodium swnfd zlib1 zstd`
(win-x64 names). For local runs: `./scripts/copy-engine-natives.ps1`. Phase 6 packaging will
bundle the correct per-RID set with the launcher.

## Status — Phase 1 verified 2026-09-07

Ran against engine `277.2.1` + the official launcher's `content.db` version 7 (ADT fork, 13114
files, zstd blobs). Result: signature verified, `Robust.Client` loaded from zip, content served
from our SQLite VFS (`res.mod: DONE loading modules`, 1399 textures + 5691 RSIs preloaded), engine
reached `root: Switching to state Content.Client.MainMenu.MainScreen` — the game window opened at
the main menu.

## Notes / deviations from the reference

- `[STAThread]` on `Main` is required (Windows clipboard/DnD).
- The loader project must **not** be `InvariantGlobalization` — the engine needs real ICU cultures.
- Managed `ZstdSharp.Port` replaces the reference's native `SharpZstd.Interop` + `zstd.dll` for
  reading DB blobs. Watch content-load time on large forks; swap to native if it regresses.
