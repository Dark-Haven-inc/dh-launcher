# Releasing

The launcher ships as a [Velopack](https://velopack.io/) app:

* **First install** — one `DarkHavenLauncher-win-Setup.exe`. No admin, installs per-user to
  `%LocalAppData%\DarkHavenLauncher`, makes Start-menu + Desktop shortcuts, registers `ss14://`.
* **First install on Linux** — either the one-liner

  ```
  curl -fsSL https://github.com/Dark-Haven-inc/frontier15-launcher/releases/latest/download/install.sh | sh
  ```

  (`… | sh -s -- --uninstall` removes it), or the `Frontier15Launcher.AppImage` itself: made executable
  and run from wherever it was downloaded, it copies itself to
  `~/.local/share/Frontier15Launcher/` and restarts from there (`AppImageInstall`; `F15_PORTABLE=1`
  keeps it where it is). Either way it then adds itself to the application menu, registers `ss14://`,
  and keeps its data in `~/.local/share/DarkHavenLauncher`. The game runs from a copy of the loader
  there, so it keeps running when the launcher is closed. Arch users can take the AUR package
  (below). The AppImage needs FUSE (`fusermount3`/`fusermount`, present on nearly every desktop);
  without it the installer runs it with `APPIMAGE_EXTRACT_AND_RUN=1`.
* **Every later version** — the running launcher notices the new GitHub Release, downloads a
  **delta** (only the changed files), and applies it on restart. The blue banner at the top of the
  window drives this. Windows and Linux are separate Velopack channels (`win`, `linux`) in the same
  GitHub Release.

Releases live as **GitHub Releases on [`Dark-Haven-inc/frontier15-launcher`](https://github.com/Dark-Haven-inc/frontier15-launcher)**,
a public repo that holds nothing but releases — so this source repo can be private (the updater
reads the feed anonymously and can't see a private repo). The release feed URL is overridable at
runtime with the `UpdateFeedUrl` config key (a GitHub repo URL, or a plain static-file base URL for
a self-hosted feed); `UpdateChannel` overrides the channel.

### The releases-repo token

CI writes to the releases repo with the **`RELEASES_TOKEN`** secret of this repo: a fine-grained
personal access token, resource owner `Dark-Haven-inc`, access to `frontier15-launcher` only,
permission **Contents: Read and write**. The workflow's first step checks it, so a missing or
expired token fails the release in seconds, not after the build. When it expires, make a new one
the same way and replace the secret.

### Moving players off the old feed (0.3.5)

Launchers up to 0.3.4 read their updates from `Dark-Haven-inc/dh-launcher` itself. While that repo
is public, `release.yml` publishes every release there too (after the releases repo), so they update
to a version that reads the new feed. **Make `dh-launcher` private only once АДМИН → Журнал →
ВЕРСИИ ЛАУНЧЕРА is green** (nobody seen in 14 days is below 0.3.5) — anyone still older after that
stops getting updates and has to reinstall from the releases repo (settings and content survive a
reinstall). After the flip the mirror step sees the repo is private and does nothing.

## Cut a release

1. Bump nothing by hand — the version comes from the git tag.
2. Tag and push:

   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. `.github/workflows/release.yml` builds it on `windows-latest` and publishes the GitHub Release
   (installer + delta + `RELEASES` manifest) to the releases repo, and mirrors it here while this
   repo is public. Then the `release-linux` job builds the AppImage on `ubuntu-latest` and adds it
   to the same release (channel `linux`), with `install.sh`. It can also be run from the Actions tab
   (`workflow_dispatch`) with an explicit version; untick **publish** there to only build (on any
   branch) and download the packages from the run's artifacts, nothing released.

### The bundled engine

The forked Dark Haven engine is **not** in git (large, build-specific — see
`src/DarkHaven.App/bundled-engines/README.md`). It lives as assets on the **`engine-bundles`**
release in this repo. CI (`release.yml`) runs `gh release download engine-bundles` before packing,
using the built-in `GITHUB_TOKEN` — no secret needed. `pack-release.ps1` then verifies, for every
engine version in `manifest.json`, the build for the RID it packs against its pinned SHA-256 and
**fails the build on a mismatch, a missing file or a missing build for that RID**, so a release
can't silently ship without a connectable engine. Other RIDs' zips are left out of the package.

To build locally: make sure `src/DarkHaven.App/bundled-engines/` has the zip(s) `manifest.json`
names (either build them per that folder's README, or
`gh release download engine-bundles --repo Dark-Haven-inc/dh-launcher -D src/DarkHaven.App/bundled-engines -p '*.zip'`),
then:

```
pwsh scripts/pack-release.ps1 -Version 0.2.0                  # Windows
pwsh scripts/pack-release.ps1 -Version 0.2.0 -Rid linux-x64   # Linux; needs mksquashfs (squashfs-tools)
```

Output lands in `artifacts/releases/`.

### Bumping the engine

When the live server moves to a new RT commit:

1. Build the new client zips, win-x64 and linux-x64 (folder README recipe), and note their SHA-256.
2. `gh release upload engine-bundles <new>.zip <new>_linux-x64.zip --repo Dark-Haven-inc/dh-launcher`
   (add `--clobber` if reusing a filename).
3. Update `src/DarkHaven.App/bundled-engines/manifest.json` — the version key, `file`, `sha256`,
   `note`, and `platforms.linux-x64`. Commit it.
4. Cut a normal `vX.Y.Z` release. Every installed launcher pulls the new engine as a delta.

### AUR package (`frontier15-launcher-bin`)

`packaging/aur/` holds the PKGBUILD: the release AppImage under `/opt/frontier15-launcher`, a
`frontier15-launcher` command, the menu entry and icon. Installed there the launcher neither copies
nor updates itself (it can't write to /opt) — new versions come through the package. After a release,
on Arch or in an `archlinux` container:

```
packaging/aur/update.sh 0.3.0     # pkgver, checksums, .SRCINFO
```

then commit `PKGBUILD`, `.SRCINFO`, `frontier15-launcher.desktop` and `LICENSE` to
`ssh://aur@aur.archlinux.org/frontier15-launcher-bin.git` (an AUR account with an SSH key; set the
Maintainer line in the PKGBUILD before the first push).
### Launch-proof signing key

Frontier 15 servers can require that players come through this launcher: when it starts the game it signs a
launch proof for the account (`src/DarkHaven.Launcher/Security/LaunchProof.cs`), and the server checks the signature
(`anticheat.launch.*` cvars on the game server). The signing key is built into release builds from the
`DH_LAUNCH_SIGNING_KEY` repository secret; local and CI builds without it sign nothing. In the build the key is
split into shares (the `DarkHaven.Launcher.KeyGen` source generator) and never stored or reconstructed whole, and
the layout changes whenever the key is rotated - so the key cannot be lifted out of a build as one value, and a
tool written to scrape one release does not carry to the next.

Set up or rotate the key:

1. `dotnet run --project src/DarkHaven.Cli -- launch-key` prints a new pair.
2. The first value goes into the `DH_LAUNCH_SIGNING_KEY` secret of this repo. Never commit it.
3. The second value is appended to `anticheat.launch.public_keys` on every game server (comma-separated). Keep the
   previous key there until players have updated past the release that used it, then remove it.
4. Cut a release. `dotnet build src/DarkHaven.Cli -p:DhLaunchKey=<secret>` then
   `dotnet run --no-build --project src/DarkHaven.Cli -- launch-proof` checks locally that a build carries the key
   (it prints how many shares the key was split into and a sample proof).

The key ships inside every copy of the launcher, so a determined person can dig it out; rotating it with releases
limits how long a leaked key is any use. Servers start in `anticheat.launch.mode log` - watch the admin log for
players still arriving without a proof before switching to `enforce`.

## Local test of the update flow

```
pwsh scripts/pack-release.ps1 -Version 0.1.0
artifacts/releases/DarkHavenLauncher-win-Setup.exe        # install 0.1.0
pwsh scripts/pack-release.ps1 -Version 0.1.1              # build a newer one
dotnet vpk upload local --outputDir artifacts/releases    # or point UpdateFeedUrl at a file:// path
```

Then launch the installed 0.1.0 — the banner should offer 0.1.1.
