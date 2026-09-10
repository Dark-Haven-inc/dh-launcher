# Releasing

The launcher ships as a [Velopack](https://velopack.io/) app:

* **First install** — one `DarkHavenLauncher-win-Setup.exe`. No admin, installs per-user to
  `%LocalAppData%\DarkHavenLauncher`, makes Start-menu + Desktop shortcuts, registers `ss14://`.
* **Every later version** — the running launcher notices the new GitHub Release, downloads a
  **delta** (only the changed files), and applies it on restart. The blue banner at the top of the
  window drives this.

Releases live as **GitHub Releases on `Dark-Haven-inc/dh-launcher`**. The release feed URL is
overridable at runtime with the `UpdateFeedUrl` config key (a GitHub repo URL, or a plain
static-file base URL for a self-hosted feed); `UpdateChannel` overrides the channel.

## Cut a release

1. Bump nothing by hand — the version comes from the git tag.
2. Tag and push:

   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. `.github/workflows/release.yml` builds it on `windows-latest` and publishes the GitHub Release
   (installer + delta + `RELEASES` manifest). It can also be run from the Actions tab
   (`workflow_dispatch`) with an explicit version.

### The bundled engine

The forked Dark Haven engine is **not** in git (large, build-specific — see
`src/DarkHaven.App/bundled-engines/README.md`). It lives as assets on the **`engine-bundles`**
release in this repo. CI (`release.yml`) runs `gh release download engine-bundles` before packing,
using the built-in `GITHUB_TOKEN` — no secret needed. `pack-release.ps1` then verifies every file
named in `manifest.json` against its pinned SHA-256 and **fails the build on a mismatch or a
missing file**, so a release can't silently ship without a connectable engine.

To build locally: make sure `src/DarkHaven.App/bundled-engines/` has the zip(s) `manifest.json`
names (either build them per that folder's README, or
`gh release download engine-bundles --repo Dark-Haven-inc/dh-launcher -D src/DarkHaven.App/bundled-engines -p '*.zip'`),
then:

```
pwsh scripts/pack-release.ps1 -Version 0.2.0
```

Output lands in `artifacts/releases/`.

### Bumping the engine

When the live server moves to a new RT commit:

1. Build the new client zip (folder README recipe) and note its SHA-256.
2. `gh release upload engine-bundles <new>.zip --repo Dark-Haven-inc/dh-launcher`
   (add `--clobber` if reusing a filename).
3. Update `src/DarkHaven.App/bundled-engines/manifest.json` — the version key, `file`, `sha256`,
   `note`. Commit it.
4. Cut a normal `vX.Y.Z` release. Every installed launcher pulls the new engine as a delta.

## Local test of the update flow

```
pwsh scripts/pack-release.ps1 -Version 0.1.0
artifacts/releases/DarkHavenLauncher-win-Setup.exe        # install 0.1.0
pwsh scripts/pack-release.ps1 -Version 0.1.1              # build a newer one
dotnet vpk upload local --outputDir artifacts/releases    # or point UpdateFeedUrl at a file:// path
```

Then launch the installed 0.1.0 — the banner should offer 0.1.1.
