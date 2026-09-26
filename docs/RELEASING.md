# Releasing

The launcher ships as a [Velopack](https://velopack.io/) app:

* **First install** — one `DarkHavenLauncher-win-Setup.exe`. No admin, installs per-user to
  `%LocalAppData%\DarkHavenLauncher`, makes Start-menu + Desktop shortcuts, registers `ss14://`.
* **Every later version** — the running launcher notices the new GitHub Release, downloads a
  **delta** (only the changed files), and applies it on restart. The blue banner at the top of the
  window drives this.

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
   repo is public. It can also be run from the Actions tab
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

### Launch-proof signing key

Frontier 15 servers can require that players come through this launcher: when it starts the game it signs a
launch proof for the account (`src/DarkHaven.Launcher/Security/LaunchProof.cs`), and the server checks the signature
(`anticheat.launch.*` cvars on the game server). The private key is built into release builds from the
`DH_LAUNCH_SIGNING_KEY` repository secret; local and CI builds without it sign nothing.

Set up or rotate the key:

1. `dotnet run --project src/DarkHaven.Cli -- launch-key` prints a new pair.
2. The first value goes into the `DH_LAUNCH_SIGNING_KEY` secret of this repo. Never commit it.
3. The second value is appended to `anticheat.launch.public_keys` on every game server (comma-separated). Keep the
   previous key there until players have updated past the release that used it, then remove it.
4. Cut a release. `dotnet run --project src/DarkHaven.Cli --property:DhLaunchKey=<secret> -- launch-proof` checks
   locally that a build carries the key.

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
