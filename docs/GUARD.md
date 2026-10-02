# dh_guard

The launcher's trust core, in Rust (`native/dh-guard`): a cdylib the C# launcher loads in-process
(`dh_guard.dll` on Windows, `libdh_guard.so` on Linux) and calls over a small C ABI. It starts the
game and signs the launch proofs Frontier 15 servers check (see `LaunchProof` in
dh-sector-frontier, `Content.Server/_DH/AntiCheat/Launcher`).

It used to be C#: `SplitEcdsa`, `LaunchProof`, `LaunchSigningKey` and the key generator,
`LaunchBroker`, `GameEnvironment` and the launch half of `GameLauncher`. IL decompiles back to
readable source and patches in minutes, so the part that decides *what gets a proof* moved to
native code. The wire formats did not change: a server or engine built against the C# launcher
accepts proofs from this one.

## Trust model

Anyone can load the library into their own process and call its exports. So there is no export
that signs an arbitrary challenge, admits an arbitrary pid or returns key material. The only way
to get a proof signed is to have the guard itself spawn the loader, and the broker then answers
only that child, identified by the kernel (`SO_PEERCRED`, `GetNamedPipeClientProcessId`). The
caller's request is untrusted: it carries structured facts (paths, addresses, the account), and
the guard builds the loader's argv and environment itself.

A keyed (release) guard also checks what it launches:

- **Loader pinning.** `loaderPath` must be named `DarkHaven.Loader.exe` (Windows) or
  `DarkHaven.Loader` (Linux), and its directory must hold exactly the pinned files: each present
  as a regular file with the pinned SHA-256, and no other regular file anywhere under it except
  `.complete` (written by `Update/LoaderCopy.cs` into the AppImage copy), `desktop.ini`,
  `Thumbs.db` and `.DS_Store`. Links anywhere under it (and junctions on Windows) are refused.
  Paths compare with `/` separators, case-insensitively on Windows.
- **Engine.** `sha256:<hex>` is accepted only when `<hex>` is one of the bundled-engine hashes in
  `src/DarkHaven.App/bundled-engines/manifest.json` (each version's top-level `sha256` and every
  `platforms.*.sha256`) and the file hashes to it. Anything else must be a hex Ed25519 signature
  over the zip under the SS14 engine key (`src/DarkHaven.Launcher/Assets/signing_key`), and the
  key file at `enginePublicKeyPath` (which the loader verifies with again) must hold that same key.
- **Environment.** Variables that load code into the game or change what it loads are stripped
  (startup hooks, profilers, `DOTNET_`/`COMPlus_` diagnostics and GC/JIT overrides, the overlay
  zip, `SS14_DISABLE_SIGNING`, `ROBUST_DISABLE_SANDBOX`, `ROBUST_MODULE_*`), and the runtime's
  debugger, profiler and diagnostic IPC are switched off. Only `net.logging=<bool>` may be added
  as an extra cvar; module names and versions must be plain names.

The signing key is never held whole: `build.rs` splits it into 3 to 6 additive shares. No mask is
stored: each share's mask is a run-time SHA-256 of a per-build salt and the share's index (the order
of the pieces chosen per build), the combine differs per share (XOR, additive mod n, or
multiplicative with the stored value = share·mask⁻¹), and the stored values and the salt's pieces
are scattered among random filler statics in a key-shuffled order, read through volatile reads at
signing time and wiped after. Signatures are computed share by share
(`s = k⁻¹·(e + r·d₁ + r·d₂ + …)`); the shares' sum, `d`, is never formed, and the public key is
`Σ dᵢ·G`. The layout follows from the key, so a rebuild with the same key is reproducible and a
rotated key lays out completely differently.

What it does **not** protect:

- **The content.** `content.db` is not re-verified at launch; the loader serves whatever it holds.
  The engine modules' directories (`ROBUST_MODULE_*`) are not pinned either.
- **A same-user attacker with native tools.** Anyone running as the player can still attach a
  native debugger to the game or the launcher, patch memory, or inject a library the OS way. The
  runtime's managed doors are closed; the OS's are not.
- **Time of check to time of use.** The loader and engine are verified, then the loader opens them
  again. Someone who can write to those files can swap them in between.
- **A patient reverse engineer.** No generic, key-independent scan recovers the key: scanning the
  binary for adjacent share/mask pairs, or for any fixed offset/stride pairing of stored bytes (XOR,
  add or multiply), finds nothing, because masks are not stored and the storage differs per build
  and per share. Recovery instead takes per-build reverse engineering (reading that build's
  generated recovery code), not a scan that carries from one build to the next. It is still not
  impossible with enough work. What limits its worth is that proofs are bound to one login's
  challenge and the key rotates.
- **A leaked v1 proof.** For back-compatibility a keyed guard still issues a *v1* proof (in
  `DH_LAUNCH_PROOF` in the child's environment). Unlike a v2 proof it is not bound to a login
  challenge or to the process, so **any caller of `dh_guard_launch`** (which starts the genuine
  loader with an account of its choosing) can read it from the child's environment
  (`/proc/<pid>/environ` on Linux; no debugger needed) and present it as a bearer token for up to
  `anticheat.launch.max_age_hours` (24 h) — but only on a server with `anticheat.launch.accept_v1=true`
  (off by default). v1 stays only until every server is on v2; turning it off (server-side) closes
  this. Removing v1 is the operator's decision, not the guard's.

## Build modes

Decided at compile time by `build.rs`:

| | DEV (no `DH_LAUNCH_SIGNING_KEY`) | KEYED (`DH_LAUNCH_SIGNING_KEY` set) |
|---|---|---|
| Key shares | none | 3–6, masked and scattered (count not exposed) |
| Broker, v2 proofs | no | yes, when an account is passed |
| v1 proof (`DH_LAUNCH_PROOF`) | no | yes, when an account is passed |
| Loader pinning, engine checks | no | yes; refuses on any mismatch |
| argv/env construction, env hardening, cvar and module checks | yes | yes |

A development build is just a process launcher, so dev builds and tests can start a fake loader.

## Build inputs

| Variable | Meaning |
|---|---|
| `DH_LAUNCH_SIGNING_KEY` | The sealed scalar exactly as `dhlauncher launch-key` prints it: base64 of the 32-byte big-endian P-256 private scalar XOR `SHA256("dh-launch-mask/1")`. Unset or empty: DEV build. |
| `DH_GUARD_LOADER_PINS` | Required with the key (the build fails without it). A UTF-8 file, one `<64 hex sha256> <relative/path>` per line, `/` separators, blank lines ignored. An absolute path: cargo runs `build.rs` in `native/dh-guard`, so a relative one fails the build (`DhGuard.targets` resolves one against the directory the build was started in). |
| `DH_LAUNCHER_VERSION` | The version the proofs state; a `+<commit>` suffix is dropped, as `LauncherInfo.Version` does. Default `0.0.0-dev`; at most 63 bytes. |

Files read in a keyed build: `src/DarkHaven.App/bundled-engines/manifest.json` and
`src/DarkHaven.Launcher/Assets/signing_key`.

A keyed build's `target/` holds derivatives of the key (the generated shares, and cargo's record
of the environment it built with, the key's value included, in files other users can read). Build in a fresh
owner-only directory, delete it afterwards, and never cache or publish it.

```
target=$(mktemp -d)   # 0700
DH_LAUNCH_SIGNING_KEY=… DH_GUARD_LOADER_PINS="$PWD/pins.txt" DH_LAUNCHER_VERSION=1.2.3 \
    cargo build --release --manifest-path native/dh-guard/Cargo.toml --target-dir "$target" \
    [--target x86_64-pc-windows-msvc]
# … take the library from "$target"/[<triple>/]release/, then:
rm -rf "$target"
```

## In the launcher

- **Build.** `src/DarkHaven.Launcher/DhGuard.targets` (imported into every project by `Directory.Build.targets`)
  runs, in `DarkHaven.Launcher`, `cargo build --release --locked --target <triple>` on every build (a no-op when
  nothing changed, and the only thing that notices a changed key or pins in the environment) into
  `artifacts/cargo/`, and ships the library as a content file, so it lands next to the assemblies of every project
  that references the launcher library (App, Cli, Tests), on build and on publish. The RID is `DhGuardRid`, else the
  build machine's (a class library never sees the app's `RuntimeIdentifier`), so every project that references it,
  directly or not, refuses a build or publish for another RID without `-p:DhGuardRid`. `LauncherVersion` becomes
  `DH_LAUNCHER_VERSION`, so the proofs state the version `LauncherInfo.Version` reports; a keyed build without it
  fails. `DH_LAUNCH_SIGNING_KEY` and `DH_GUARD_LOADER_PINS` reach cargo through the environment only (the pins path
  made absolute). `-p:DhGuardSkipCargo=true` skips cargo. cargo is looked for in `$CARGO_HOME/bin`,
  `~/.cargo/bin`, then `PATH` (or `-p:DhGuardCargo=`).
- **Release.** `scripts/pack-release.ps1` publishes the loader into `loader/` first, without what vpk leaves out
  of every package (symbols, `createdump`), pins every file in it (`artifacts/loader-pins.txt`), publishes the app
  with the key and the pins in a fresh owner-only cargo target directory (deleted afterwards; no build servers keep
  the key in their environment), and checks that `loader/` still matches the pins. After `vpk pack` it reads
  `loader/` back out of the full package (on Linux out of the AppImage in it, with `unsquashfs`) and fails unless
  that is exactly the pinned set. CI needs a stable Rust toolchain on both runners (`release.yml`).
- **C#.** `Security/Guard.cs` holds the `[LibraryImport]` bindings, the request DTOs (System.Text.Json,
  source-generated, camelCase) and `Guard.Info()`. `Update/GameProcess.cs` stands in for
  `System.Diagnostics.Process`: one thread waits on `dh_guard_game_wait`; `WaitForExitAsync` completes once
  redirected output has drained; output arrives line by line from two reader threads; `Dispose` frees the
  handle, not the game. `Update/GameLauncher.cs` builds the request. In an AppImage it starts the loader from
  `Update/LoaderCopy.cs`'s copy under the data directory, which is compared with the AppImage's loader (file set,
  sizes, SHA-256) before each launch and made again if it drifted, and once more if the guard refuses it anyway.
  `dhlauncher launch-proof` prints `dh_guard_info`: whether the build signs, pinned files, version and public key
  (not the share count, always 0; see below).
- **Checking a release guard.** Build the tests with `DH_LAUNCH_SIGNING_KEY`, `DH_GUARD_LOADER_PINS` and
  `-p:LauncherVersion=…`, and set `DH_GUARD_TEST_LOADER` to the pinned `loader/DarkHaven.Loader[.exe]`:
  `KeyedGuardTests` then check that it refuses a fake engine, a patched or extra loader file, and any other
  executable. The tests for the development guard skip themselves in a keyed build.

## C ABI (version 1)

Every function is `extern "C"`, thread-safe, and never unwinds. Status codes: `0` ok, `1` timeout
(`dh_guard_game_wait` only), negative for errors; the message is kept per thread for
`dh_guard_last_error` and is only meaningful right after a negative status.

```c
#include <stddef.h>
#include <stdint.h>

#define DH_GUARD_ABI_VERSION 1

#define DH_GUARD_OK          0
#define DH_GUARD_TIMEOUT     1   /* dh_guard_game_wait: still running */
#define DH_GUARD_E_INTERNAL (-1) /* a panic or other internal failure */
#define DH_GUARD_E_ARGUMENT (-2) /* null pointer, timeout below -1 */
#define DH_GUARD_E_REQUEST  (-3) /* malformed request, disallowed cvar/module, NUL in a field */
#define DH_GUARD_E_REFUSED  (-4) /* keyed build: loader or engine is not the genuine one */
#define DH_GUARD_E_OS       (-5) /* spawning or killing failed */

typedef struct DhGuardGame DhGuardGame;

typedef struct {
    int32_t abi_version;          /* 1 */
    int32_t has_key;              /* 1 in a keyed build */
    int32_t share_count;          /* always 0: the share count is no longer exposed (a key-scanner hint) */
    int32_t pinned_files;
    int32_t public_key_len;       /* 91 when keyed, else 0 */
    uint8_t public_key[96];       /* P-256 SubjectPublicKeyInfo DER, from the shares */
    uint8_t launcher_version[64]; /* NUL-terminated UTF-8 */
} DhGuardInfo;

/* Starts the game described by a UTF-8 JSON request (below). On success *game is a handle to free
   with dh_guard_game_free and *pid the loader's pid. With redirectOutput the game's stdout and
   stderr are pipes whose read ends are returned as OS handles the caller owns (fd on Unix, HANDLE
   on Windows; synchronous, not inheritable); otherwise both are -1 and the game shares the
   launcher's stdio. stdin is always inherited. */
int32_t dh_guard_launch(const uint8_t *json, size_t len, DhGuardGame **game, int32_t *pid,
                        intptr_t *stdout_handle, intptr_t *stderr_handle);

/* 0 once exited (*exit_code: the Windows exit code; on Unix the status, or 128 + signal when a
   signal killed it, as .NET's Process.ExitCode; -1 if it cannot be known, e.g. something else
   reaped the child), 1 if still running after timeout_ms (-1 = forever). Repeatable, concurrent. */
int32_t dh_guard_game_wait(DhGuardGame *game, int32_t timeout_ms, int32_t *exit_code);

/* SIGKILL on Unix, TerminateProcess(handle, 1) on Windows; a no-op once exited. On Unix the signal
   goes through a pidfd where the kernel has one, and never after the reap, so it cannot hit a
   reused pid even if something else reaped the child. */
int32_t dh_guard_game_kill(DhGuardGame *game);

int32_t dh_guard_game_signed(DhGuardGame *game);        /* proofs signed so far, >= 0 */
int32_t dh_guard_game_broker_state(DhGuardGame *game);  /* 1 running/ran, 0 not requested, -1 failed to start */

/* Releases the handle only: the game keeps running and its broker keeps answering it until it
   exits (a waiter thread owns both). Do not use the handle afterwards. */
void dh_guard_game_free(DhGuardGame *game);

int32_t dh_guard_info(DhGuardInfo *out);

/* Copies up to cap bytes of this thread's last error (UTF-8, no NUL) and returns its full length;
   call with cap 0 to size a buffer. */
size_t dh_guard_last_error(uint8_t *buf, size_t cap);
```

### Launch request

UTF-8 JSON, camelCase; unknown fields are rejected, and every field without `|null` is required.

```json
{
  "loaderPath": "string (absolute)",
  "enginePath": "string",
  "engineSignature": "string: sha256:<hex> or Ed25519 hex",
  "enginePublicKeyPath": "string",
  "contentDbPath": "string",
  "contentVersion": 0,
  "modules": [{ "name": "string", "version": "string" }],
  "launcherPath": "string|null",
  "username": "string|null",
  "compatMode": false,
  "connectAddress": "string",
  "ss14Address": "string",
  "build": {
    "engineVersion": "string|null", "version": "string|null", "forkId": "string|null",
    "hash": "string|null", "manifestHash": "string|null", "manifestUrl": "string|null",
    "manifestDownloadUrl": "string|null", "downloadUrl": "string|null"
  },
  "extraCvars": ["net.logging=true"],
  "account": { "username": "string", "token": "string", "userId": "guid", "authPublicKey": "string|null" },
  "redirectOutput": false
}
```

`build` and `account` may be `null`. The launcher passes `account` only when there is one and the
server's auth mode is not Disabled. No string may contain NUL; module names and versions must
match `^[A-Za-z0-9._-]+$` and not be `.` or `..`; `extraCvars` may only hold `net.logging=true` or
`net.logging=false`.

### What the loader gets

```
<enginePath> <engineSignature> <enginePublicKeyPath> --username <account.username | username | JoeGenero>
--cvar display.compat=<true|false> --cvar launch.launcher=true [--cvar <extraCvar>]...
--launcher --connect-address <connectAddress> --ss14-address <ss14Address>
[--cvar build.<engine_version|version|fork_id|hash|manifest_hash|manifest_url|manifest_download_url|download_url>=<value>]...
```

The environment is the launcher's own, then: injecting variables removed and diagnostics off;
`SS14_LOADER_CONTENT_DB`, `SS14_LOADER_CONTENT_VERSION`, `SS14_LAUNCHER_PATH`,
`DOTNET_MULTILEVEL_LOOKUP=0`, `DOTNET_TieredPGO=1`, `DOTNET_ReadyToRun=0`; the launcher's own
`DH_LAUNCH_BROKER`/`DH_LAUNCH_PROOF` removed; with an account `ROBUST_AUTH_TOKEN`,
`ROBUST_AUTH_USERID`, `ROBUST_AUTH_PUBKEY`, `ROBUST_AUTH_SERVER`, plus (keyed) the broker endpoint
and a v1 proof; `ROBUST_MODULE_<NAME>` = `<dir of contentDbPath>/modules/<name>/<version>` per module.

## Broker

`DH_LAUNCH_BROKER` is `pipe:f15-launch-<32 hex>` (Windows) or `unix:<path>` (Linux).

- **Linux:** the socket lives in `$XDG_RUNTIME_DIR`, or in `<temp>/f15-launch-<user>` (created
  0700; an existing one must be a real directory owned by the user with no group or other bits). If
  that fixed-name directory exists and is not ours, the broker uses a fresh private `mkdtemp`
  directory (`<temp>/f15-launch-<user>-<random>`, 0700) instead of failing, and removes it on stop,
  so another local user cannot disable the broker by squatting on the name. The socket is 0600, the
  peer is identified by `SO_PEERCRED`, and the file is removed on stop.
- **Windows:** a byte-mode named pipe with `PIPE_REJECT_REMOTE_CLIENTS`, the first instance created
  with `FILE_FLAG_FIRST_PIPE_INSTANCE` and each next one before the previous closes, so the name
  never lapses. A per-instance failure (a client that opens the next instance and leaves,
  `ERROR_NO_DATA`, a read/write error) replaces just that instance and the broker carries on; only
  the stop event or failing to create any instance ends the thread. Owner and only DACL entry are
  the token's default owner, the SID the game's `PipeOptions.CurrentUserOnly` compares the pipe
  owner with (the user normally, Administrators when elevated), exactly as .NET's own
  `CurrentUserOnly` server sets it.

One connection at a time. A peer that is not the spawned child gets `"\n"` and the connection is
closed unread. The child gets 5 seconds and 1024 bytes for
`"dh-launch-broker/1\n" userId "\n" base64url(challenge) "\n"` (a 64-byte challenge, its own
account) and is answered `token "\n"`; anything else gets `"\n"`. The broker starts before the
spawn (its endpoint goes into the child's environment) and admits the child's pid right after; a
connection that arrives before the pid is admitted waits for it, up to the request deadline. Right
before signing it re-checks that the child has not exited (a pidfd where the kernel has one, else a
non-reaping `waitid`), so a reused pid is not answered. When the child exits the guard withdraws
admission and stops the broker *before* the child is reaped, so its pid is never both freed and
still trusted. If the broker cannot start, the game still starts, without `DH_LAUNCH_BROKER`, and
the handle reports broker state -1.

## Tests

`dotnet test` (C#): the golden fixture below verified with .NET's ECDSA, the `DhGuardInfo` layout, and on Linux
the whole launch through the development guard with a shell script as the loader (argv order, environment,
exit codes, output lines, kill; the stray variables are set with libc's `setenv`, where the guard reads them, next
to one that must arrive); `LoaderCopyTests`; `KeyedGuardTests` as above.

`cargo test` in `native/dh-guard` (Linux): split signing against `p256`'s verifier, the key
layout, sealing and each combine scheme, byte-exact payloads, the env hardening cases, the broker
over a real socket (including admission-before-connect and a withdrawn/exited game refused), the
`mkdtemp` fallback directory, loader pinning and engine checks, end to end through a fake loader
(the test binary re-run under the loader's name) and the C ABI in a development build. The Windows
named-pipe broker's tests (a proof for the admitted pid with the pipe owner checked as
`CurrentUserOnly` does, other pids and malformed requests refused, an early-leaving client not
ending the broker, and a prompt stop whose name then disappears) live in
`broker/windows.rs` under `#[cfg(all(test, windows))]`; they compile under
`cargo check --tests --target x86_64-pc-windows-gnu` but run only on Windows.
`.github/workflows/guard.yml` runs `cargo test` and the guard's C# tests on Linux and Windows whenever the guard or
the code around it changes. `tests/fixture.rs` keeps `tests/DarkHaven.Launcher.Tests/fixtures/rust-launch-proof.json`,
a proof from the Rust signer for the C# tests (`DH_GUARD_WRITE_FIXTURE=1` rewrites it).
