# Bundled engines

Dark Haven runs a **forked** RobustToolbox (`Dark-Haven-inc/RobustToolbox`). It is on no public
`robust-builds` CDN, so the launcher ships it and `EngineManager` serves it from here instead of
downloading — verified by SHA-256 (`manifest.json`), which the loader accepts as `sha256:<hex>` in
place of an Ed25519 signature.

The `.zip` is **not** in git (large). It lives as an asset on the **`engine-bundles`** release of
`Dark-Haven-inc/dh-launcher`; CI and `pack-release.ps1` pull it from there and check its SHA-256
against `manifest.json`. Build it from the exact RT commit the target server's
`dh-sector-frontier` submodule points at — otherwise the client can't deserialise the server's
game state.

To get the current one without building:

```bash
gh release download engine-bundles --repo Dark-Haven-inc/dh-launcher -D . -p '*.zip'
```

### Currently bundled

| engine | RT commit | server |
|--------|-----------|--------|
| `275.1.0.zip` | `c333ccb58145767922946bf08d919d8856bc1f40` | live ХЕЙВЕН (`медецина` build; content `a240a1d47c`). Verified end-to-end 2026-09-10: launcher → local server on that pair → in-game lobby. |

## Build recipe

```bash
cd <dh-sector-frontier>/RobustToolbox
git checkout <RT commit that dh-sector-frontier's submodule points at>
git submodule update --init --recursive   # NetSerializer + Lidgren ARE the wire format — must be in sync
dotnet publish Robust.Client/Robust.Client.csproj \
  -r win-x64 --no-self-contained -c Release \
  -p:TargetOS=Windows -p:FullRelease=True -p:UseAppHost=False
```

Then zip `bin/Client/win-x64/publish/*` (minus `Robust.Client` / `Robust.Client.exe`) **plus**
`RobustToolbox/Resources/*` at the zip root, with **forward-slash** entry names (Windows
`Compress-Archive` writes backslashes — use `7z` or a small Python `zipfile` script), into
`275.1.0.zip` (name = the `engine_version` the server reports in `/info`), and put its uppercase
SHA-256 into `manifest.json`.

Repeat per RID for a cross-platform release (`win-arm64`, `linux-x64`, …); `manifest.json` keys
by engine version, and the file name can carry the RID once multi-RID support lands.

> **Ideal:** the person who builds the server runs `RobustToolbox/Tools/package_client_build.py`
> on that machine and hands over the resulting `Robust.Client_<rid>.zip` — then it is guaranteed
> to match.
