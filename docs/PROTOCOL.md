# SS14 Launcher — protocol research (from space-wizards/SS14.Launcher master + RobustToolbox)

## Architecture (3 pieces)
1. **Launcher UI app** — Avalonia/C#. Server browser, account mgmt, content/engine download, spawns loader.
2. **SS14.Loader** — tiny .NET console exe. `SS14.Loader <robustZipPath> <sigHex> <pubKeyFile> [engineArgs...]`.
   - Verifies engine zip Ed25519 sig (NSec.Cryptography). `SS14_DISABLE_SIGNING=true` bypass (non-RELEASE only).
   - Opens RobustToolbox engine zip, custom `AssemblyLoadContext.Resolving` loads `Robust.Client.dll` + deps FROM the zip.
   - Mounts content: `ContentDbFileApi(contentDb, versionId)` as `ApiMount(api,"/")`; optional overlay zip mount (masks files).
   - Finds `[LoaderEntryPoint]` attr on Robust.Client → `ILoaderEntryPoint.Main(IMainArgs)`. Hands off.
   - Interfaces come from **Robust.LoaderApi** submodule (github.com/space-wizards/Robust.LoaderApi, MIT) — `IFileApi`, `IMainArgs`, `ILoaderEntryPoint`, `IRedialApi`, `ApiMount`. This is the contract with the engine — must use as-is.
3. **Robust.LoaderApi** — shared interface pkg (submodule).

## Connect flow
1. Parse `ss14://host[:port]` (default port 1212) or `ss14s://host/path` (TLS). → info URL `http(s)://.../info`.
2. `GET /info` JSON:
   - `connect_address`: `udp://host:port` (may be empty → derive from info addr)
   - `auth`: `{ mode: "Optional"|"Required"|"Disabled", public_key: base64|null }`
   - `build`: `{ engine_version, fork_id, version, download_url, hash, acz: bool, manifest_url, manifest_download_url, manifest_hash }`
   - `desc`, `privacy_policy: {identifier,version,link}?`
3. If `acz==true` or `download_url` empty → derive from server API base (`/manifest.txt`, `/download`, `/client.zip`).
4. **Content update (manifest/delta):**
   - GET `manifest_url` (accept zstd). Text: line 1 `Robust Content Manifest 1`, then `<blake2b-hex> <path>` per line.
   - Verify whole-manifest Blake2B-256 == `manifest_hash`.
   - Local store = SQLite Content DB. Tables: `Content(Id,Hash,Size,Compression,Data blob)`, `ContentManifest(VersionId,Path,ContentId)`, `ContentVersion`, `ContentEngineDependency`.
   - Diff manifest vs `Content.Hash` → missing indices.
   - `OPTIONS manifest_download_url` → headers `X-Robust-Download-Min/Max-Protocol` (current protocol v1).
   - `POST manifest_download_url` body = int32-LE array of missing indices, header `X-Robust-Download-Protocol: 1`, accept zstd.
     Response stream: int32-LE flags (bit0 = PreCompressed), then per file: int32-LE uncompressed len [+ int32-LE compressed len if PreCompressed], then bytes. Verify each blob Blake2B == manifest hash. Store (zstd-compress if worth it).
5. **Engine update:**
   - `GET https://robust-builds.cdn.spacestation14.com/manifest.json` → `{ "<version>": { insecure, redirect?, platforms: { "<rid>": { url, sha256, sig } } } }`
   - Pick best RID for current OS/arch, download zip to `engines/<version>.zip`, record signature. (Verify sig at launch, not download.)
   - Modules: `GET https://robust-builds.cdn.spacestation14.com/modules.json` (e.g. `Robust.Client.WebView`/CEF). Extract to disk. Env `ROBUST_MODULE_<NAME>`.
6. **Auth (Wizard's Den):**
   - `POST https://auth.spacestation14.com/api/auth/authenticate` `{Username|UserId, Password, TfaCode?}` → `{Token, Username, UserId, ExpireTime}`.
   - `POST /api/auth/refresh` `{Token}` → `{NewToken, ExpireTime}`. `GET /api/auth/ping` (Authorization: `SS14Auth <token>`) validity. `/api/auth/logout`, `/register`, `/resetPassword`, `/resendConfirmation`.
   - Override via env `SS14_LAUNCHER_OVERRIDE_AUTH`.
7. **Launch:** `SS14.Loader(.exe) <engineZipPath> <sigHex> <pubKeyFile>` + args:
   - `--username <name>` `--cvar display.compat=false` `--cvar launch.launcher=true` `--launcher` `--connect-address udp://host:port` `--ss14-address ss14://...`
   - `--cvar build.engine_version=…` `build.version=…` `build.fork_id=…` `build.hash=…` `build.manifest_hash=…` `build.manifest_url=…` `build.manifest_download_url=…` `build.download_url=…`
   - env: `ROBUST_AUTH_TOKEN`, `ROBUST_AUTH_USERID`, `ROBUST_AUTH_PUBKEY` (= server's info.auth.public_key), `ROBUST_AUTH_SERVER` (auth base URL)
   - env: `SS14_LOADER_CONTENT_DB`, `SS14_LOADER_CONTENT_VERSION` (versionId), `SS14_LOADER_OVERLAY_ZIP?`, `SS14_LAUNCHER_PATH` (for redial), `DOTNET_MULTILEVEL_LOOKUP=0`, `DOTNET_ReadyToRun=0`, `DOTNET_TieredPGO=1`
8. **Redial:** engine calls back to launcher via named pipe `SS14.Launcher.CommandPipe` (`IRedialApi`), launcher connects to new address.

## Hub / server list
- `GET https://hub.spacestation14.com/api/servers` → `[{ address, statusData: {name, players, soft_max_players, round_start_time, run_level, tags[]} }]`
- `GET {hub}/api/servers/info?url=<ss14addr>` → same ServerInfo as `/info` (hub proxies).
- Tags: `18+`, `lang:xx`, `rp:none|low|med|high`, `region:xx`. Multiple hubs allowed (fallback set).

## Official launcher deps (for reference; "from scratch" can pick equivalents)
Avalonia 11, `Microsoft.Data.Sqlite` / SQLitePCLRaw, `SpaceWizards.Sodium` (Blake2B), a zstd binding (`ZStdSharp` or SpaceWizards fork), `NSec.Cryptography` (Ed25519), Dapper, Serilog, CommunityToolkit.Mvvm, Splat/DI.
Data dir: `%LOCALAPPDATA%/Space Station 14/launcher/` (overridable `SS14_LAUNCHER_APPDATA_NAME`).

## "From scratch" scope note
The engine ships as a signed zip loaded in-process via `Robust.LoaderApi` — the loader MUST be .NET and MUST use that interface package. So "from scratch" = our own UI + our own content/engine/auth/hub/update code + a thin loader, reusing only `Robust.LoaderApi` (MIT, unavoidable). Everything else is fair game to rewrite.
DH decisions: (1) DH regions + full any-server browser (hub-style). (2) Wizard's auth for now, launcher optional. (3) From scratch. Licence target: MIT (launcher is the one piece DH can legally keep closed/own per dh-what-can-be-closed, but MIT fork norm expected by SS14 community).
