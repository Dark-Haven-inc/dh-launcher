# Download mirrors — planned, not built

**Status:** designed 2026-10-01 and parked on purpose: it gets built if the project grows enough to need it.
Everything needed to start is below — what to mirror and why, how the mirror works, where it plugs into the
launcher, where to host it and what it costs. Nothing here exists in code yet.

## Why

Players download three things, and all of them come from one place each:

| What | From | Problem |
|---|---|---|
| **Game content** (first join ≈ 800 MB, then the changed files of every update) | The game server itself — it hosts its own content (`acz: true` in `/info`), i.e. GameServerX's home PC | One home uplink for every player; far regions get a long path on top |
| **Launcher updates** (Velopack `.nupkg`, 10–110 MB) | GitHub releases of `Dark-Haven-inc/frontier15-launcher` | GitHub is slow or throttled in parts of Russia |
| **ЛОКАЛКА server builds** (≈ 800 MB per platform) | The `local-servers` pre-release of the same repo | Same |

The engine ships inside the launcher, and .NET for ЛОКАЛКА comes from Microsoft's CDN — neither needs a mirror.

The plan: two mirrors, one for Siberia/Asia and one for Europe/the West. The launcher picks the closer one by ping
and falls back to the original source whenever the mirror can't help.

## Trust: a mirror is never believed

Every byte a mirror serves is checked against a hash that comes **from the original source**, exactly as today:

- **Content:** the manifest's BLAKE2b must equal `manifest_hash` from the game server's own `/info`; every file's
  BLAKE2b must match its line in that manifest (`Content/ContentManifest.cs`, `Content/ManifestDownloader.cs`).
- **Launcher updates:** the release feed (`releases.win.json` / `releases.linux.json`) is still read from GitHub; only
  the package files come from the mirror, checked against the feed's SHA.
- **ЛОКАЛКА builds:** `local-servers/manifest.json` is still read from GitHub; the zip from the mirror must match its
  SHA-256 (`Local/LocalBuildStore.cs` already checks it).

A broken or hostile mirror can therefore only be slow or fail — and failing falls back to the source.

## The mirror (`src/DarkHaven.Mirror`, ASP.NET, this repo)

One small service per mirror, behind Caddy for TLS. It links `Content/Blake2.cs` and `Content/ContentManifest.cs`
from `DarkHaven.Launcher` so it checks content exactly as the launcher does. Packages: `ZstdSharp.Port`,
`SauceControl.Blake2Fast` (already in `Directory.Packages.props`).

### Configuration (`Mirror` section; `Mirror__…` in docker-compose)

| Key | Default | Meaning |
|---|---|---|
| `DataDir` | `/data` | blobs, manifests, files |
| `Servers:<name>` | — | game servers to mirror, `ss14://host:port` (e.g. `Servers:haven = ss14://95.31.51.216:1212`) |
| `ServerPollSeconds` | 60 | how often each server's `/info` is checked |
| `KeepVersions` | 3 | content versions kept per server |
| `GitHub:N:Repo` / `Latest` / `Tags` / `Endings` | — | releases to mirror: `frontier15-launcher`, latest 2, endings `.nupkg`; and tag `local-servers`, ending `.zip` |
| `GitHubPollMinutes` | 10 | anonymous API limit is 60 calls/hour per IP |
| `GitHubToken` | — | optional, only to lift that limit |

### Content sync (per server, every `ServerPollSeconds`)

1. `GET {server}/info` → `build.manifest_hash`. URLs: `build.manifest_url` / `build.manifest_download_url` if set
   (a CDN build), otherwise `{http base}/manifest` and `{http base}/download` (self-hosted content — the same
   derivation as `Api/ServerInfoApi.cs` / `Ss14Address.SelfhostedManifest`).
2. Same hash as last time → nothing to do.
3. `GET` the manifest with `Accept-Encoding: zstd`, unwrap, check its BLAKE2b against `manifest_hash`.
4. Files whose hash isn't in the store yet → fetch them with the Robust download protocol (below), in chunks of a
   few thousand, checking every file's BLAKE2b before storing it.
5. Save the manifest, record it as this server's newest version, delete versions beyond `KeepVersions` and every blob
   no kept manifest uses.

**Robust download protocol, version 1** (what `ManifestDownloader.DownloadMissingAsync` speaks):

- `OPTIONS {download}` → headers `X-Robust-Download-Min-Protocol` / `X-Robust-Download-Max-Protocol`.
- `POST {download}`, header `X-Robust-Download-Protocol: 1`, body = the wanted manifest line indices as int32
  little-endian. The response may be zstd as a whole (`Content-Encoding: zstd`). Inside: int32 flags (bit 0 =
  pre-compressed), then per requested file: int32 uncompressed length, and if pre-compressed an int32 compressed
  length (0 = this one is raw), then the bytes.

### Storage

- `blobs/<first two hex digits>/<HASH>` — 1 byte (0 raw, 1 zstd), int32 LE uncompressed length, the data. Written to
  a temporary name and renamed, so a half-written blob never looks valid. Kept zstd-compressed where that saves space
  (what the origin sent pre-compressed stays as it came).
- `manifests/<HASH>` — the raw manifest; a small per-server history file says which are kept.
- `files/<name>` — mirrored GitHub release assets, flat by name. This relies on those names being versioned and unique
  (`Frontier15Launcher-0.3.10-full.nupkg`, `SS14.Server_win-x64_<sha12>.zip`) — **keep naming them like that**.

### Endpoints

| | |
|---|---|
| `GET /ping` | tiny, uncached — what the launcher measures |
| `GET /status` | servers, current hashes, last sync, files (for staff) |
| `GET /content/{manifestHash}/manifest` | the stored manifest, or 404 |
| `OPTIONS /content/{manifestHash}/download` | protocol headers (min = max = 1) |
| `POST /content/{manifestHash}/download` | the protocol above, answered from the store, always pre-compressed. Checks every requested blob is there **before** streaming; missing → 503, and the launcher goes to the origin |
| `GET /files/{name}` | a mirrored asset, with range requests |

Content is addressed by manifest hash, not by server: the launcher already has that hash from `/info`, needs no
address mapping, and can't be handed another version.

### GitHub sync (every `GitHubPollMinutes`)

`GET /repos/{repo}/releases` → the newest `Latest` non-draft, non-pre-release releases plus the listed `Tags` →
assets with a matching ending → download those missing or of a different size, check the asset's `digest`
(`sha256:…`) where GitHub gives one, move into `files/`; delete files no listed release has any more.

## The launcher

| Where | Change |
|---|---|
| New `Mirrors/` (DarkHaven.Launcher) | The mirror list: the platform's `GET /api/mirrors` (`[{ id, name, url }]`) plus a built-in fallback list. Pick: three `GET /ping` to each in parallel, 2 s timeout, lowest median wins; re-pick after a failure; remember the pick for the session. |
| `Content/ContentUpdater.cs` → `ManifestDownloader.DownloadAsync` | Try `{mirror}/content/{ManifestHash}/manifest` and `/download` first; on 404, 503 or any failure, the origin's `ManifestUrl` / `ManifestDownloadUrl` (as now). Hash checks unchanged. |
| `Update/` (Velopack 1.2) | A custom `IUpdateSource` around the current GitHub source: `GetReleaseFeed` from GitHub, `DownloadReleaseEntry` from `{mirror}/files/{FileName}`, then from GitHub. **Check first** that Velopack verifies a package's checksum after a custom source's download; if it doesn't, verify the feed's SHA ourselves. |
| `Local/LocalBuildStore.cs` → `DownloadVerifiedAsync` | Try `{mirror}/files/{zip name}` first; the SHA-256 still comes from GitHub's `manifest.json`. |
| Settings (both themes) | «Зеркало загрузок»: Авто / Новосибирск / Амстердам / Напрямую. |
| dh-platform | `GET /api/mirrors` from configuration (`Mirrors__N__Id/Name/Url`), so mirrors can change without a launcher release; optionally mirror health in АДМИН → МОНИТОРИНГ. |

Older launchers keep downloading from the sources; mirrors start working with the release that adds this.

## Tests to write

- End to end without a network: a fake game server (`HttpMessageHandler` answering `/info`, the manifest and the
  download protocol) → the mirror's sync → the mirror hosted in-process (`WebApplicationFactory`) → the launcher's own
  `ManifestDownloader` downloading from it.
- The mirror keeps `KeepVersions` and collects unused blobs; a half-written blob never counts.
- GitHub sync against a fake API: new assets, size/digest mismatch, removed releases.
- The launcher falls back to the origin when the mirror says 404/503, drops the connection, or serves bytes that
  don't match the hash.

## Hosting (prices as of 2026-10-01)

Timeweb Cloud — the provider the team already uses, paid with a Russian card, hourly billing.

| Mirror | Plan | Network | Price |
|---|---|---|---|
| **Siberia / Asia — Novosibirsk** | 2 vCPU, 2 GB, 40 GB NVMe | 1 Gbit/s, unmetered | ≈ 1000 ₽/month |
| **Europe / West — Amsterdam** | NL-40: 2 vCPU, 2 GB, 40 GB | 1 Gbit/s | ≈ 1620 ₽/month |

- **Krasnoyarsk** has no real VPS hosting: CloudX's "Krasnoyarsk" plans run in Moscow and weren't even on sale.
  Novosibirsk is ~800 km away, 10–15 ms from Krasnoyarsk.
- **Poland:** Timeweb has no VPS there; Amsterdam is one of Europe's main exchange points and better for the West.
- **Disk:** 40 GB is plenty — a few content versions, 3 ЛОКАЛКА builds × 2 platforms, 2 launcher versions ≈ 10 GB.

Setup per mirror: Ubuntu 24.04, Docker, docker-compose with the mirror and Caddy, DNS `sib.mirror.dark-haven.xyz`
and `eu.mirror.dark-haven.xyz` (A records in Timeweb, like `api.dark-haven.xyz`). SSH access by the dedicated key
`dh-mirror-deploy` (it already exists; public part):

```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINcjBwmfn4Vi51cd0dOgSHc20TXeXwnsSYhUBvewHkta dh-mirror-deploy
```

## Order of work

1. The mirror service with its tests (1–2 days).
2. The launcher side and `/api/mirrors` on the platform (about a day).
3. Rent the two servers, DNS, deploy (an hour once access is there).
4. A launcher release.

## Keep in mind until then

- The game server must stay reachable from the mirrors over HTTP; each mirror pulls each new version from it once.
- If the game server moves to a CDN (`build.manifest_url` set in `/info`), the mirror follows the same fields — no
  change needed.
- Keep release asset names versioned and unique (see Storage).
- `Dark-Haven-inc/frontier15-launcher` must stay public: mirrors read its releases anonymously, like the updater does.
