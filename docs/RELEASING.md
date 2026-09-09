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
`src/DarkHaven.App/bundled-engines/README.md`). CI pulls it from the `BUNDLED_ENGINE_URL` repo
secret before packing. Without that secret the installer still builds, but the launcher can only
connect to servers whose engine is on the public robust-builds CDN — i.e. not Dark Haven.

To build locally with the engine, drop the zip + `manifest.json` into
`src/DarkHaven.App/bundled-engines/` first, then:

```
pwsh scripts/pack-release.ps1 -Version 0.2.0
```

Output lands in `artifacts/releases/`.

## Local test of the update flow

```
pwsh scripts/pack-release.ps1 -Version 0.1.0
artifacts/releases/DarkHavenLauncher-win-Setup.exe        # install 0.1.0
pwsh scripts/pack-release.ps1 -Version 0.1.1              # build a newer one
dotnet vpk upload local --outputDir artifacts/releases    # or point UpdateFeedUrl at a file:// path
```

Then launch the installed 0.1.0 — the banner should offer 0.1.1.
