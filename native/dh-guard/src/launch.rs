//! Starts the game: checks what it is asked to start (in a keyed build), builds the loader's command line and
//! environment from the request's facts, starts the broker, spawns the loader and admits exactly that process.
//!
//! The command line and environment mirror what `GameLauncher.Start` built, so the loader and the engine see the same
//! launch they always did; the difference is that the caller no longer gets to write them.

use crate::broker::{self, Broker, Signer};
use crate::env::{self, EnvBlock};
use crate::proof;
use crate::request::LaunchRequest;
use crate::split_ecdsa::Shares;
use crate::verify::{self, EngineTrust, LoaderPin};
use shared_child::SharedChild;
use std::fmt;
use std::io;
use std::path::Path;
use std::process::{Command, ExitStatus};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

/// The auth server the game logs in with, as the reference launcher sets it.
const AUTH_SERVER: &str = "https://auth.spacestation14.com/";

/// The username the engine is given when there is none.
const DEFAULT_USERNAME: &str = "JoeGenero";

/// Hands out the signing key's shares for one use; they are wiped when the caller drops them.
pub type ShareSource = Arc<dyn Fn() -> Shares + Send + Sync>;

/// What a keyed guard signs with and agrees to launch. A development build has none.
#[derive(Clone)]
pub struct Trust {
    pub shares: ShareSource,
    pub loader_pins: Vec<LoaderPin>,
    pub engine: EngineTrust,
}

pub struct LaunchContext {
    pub trust: Option<Trust>,
    /// The version the proofs state.
    pub launcher_version: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LaunchError {
    /// The request is malformed or asks for something the guard does not do.
    InvalidRequest(String),
    /// A keyed build refused what it was asked to start.
    Refused(String),
    /// Starting the process failed.
    Failed(String),
}

impl fmt::Display for LaunchError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LaunchError::InvalidRequest(m) => write!(f, "invalid launch request: {m}"),
            LaunchError::Refused(m) => write!(f, "refused to launch: {m}"),
            LaunchError::Failed(m) => write!(f, "could not start the game: {m}"),
        }
    }
}

/// Where the broker ended up: running (or ran), not asked for (no key or no account), or failed to start.
pub const BROKER_RUNNING: i32 = 1;
pub const BROKER_NOT_REQUESTED: i32 = 0;
pub const BROKER_FAILED: i32 = -1;

/// A started game, and the read ends of its output when it was redirected.
pub struct Launched {
    pub game: Game,
    pub stdout: Option<os_pipe::PipeReader>,
    pub stderr: Option<os_pipe::PipeReader>,
}

/// The loader's arguments, in `GameLauncher.Start`'s order.
pub fn loader_args(request: &LaunchRequest) -> Vec<String> {
    let mut args = vec![
        request.engine_path.clone(),
        request.engine_signature.clone(),
        request.engine_public_key_path.clone(),
    ];
    let cvar = |args: &mut Vec<String>, kv: String| {
        args.push("--cvar".into());
        args.push(kv);
    };

    let username = match (&request.account, &request.username) {
        (Some(account), _) => account.username.clone(),
        (None, Some(username)) => username.clone(),
        (None, None) => DEFAULT_USERNAME.to_string(),
    };
    args.push("--username".into());
    args.push(username);
    cvar(&mut args, format!("display.compat={}", request.compat_mode));
    cvar(&mut args, "launch.launcher=true".into());
    for extra in &request.extra_cvars {
        cvar(&mut args, extra.clone());
    }

    args.push("--launcher".into());
    args.push("--connect-address".into());
    args.push(request.connect_address.clone());
    args.push("--ss14-address".into());
    args.push(request.ss14_address.clone());

    if let Some(build) = &request.build {
        for (name, value) in build.cvars() {
            if let Some(value) = value.filter(|v| !v.is_empty()) {
                cvar(&mut args, format!("build.{name}={value}"));
            }
        }
    }
    args
}

/// The loader's environment: `base` (the launcher's own) hardened, then the loader's settings, the account and the
/// modules, in `GameLauncher.Start`'s order.
pub fn loader_env(
    request: &LaunchRequest,
    mut env: EnvBlock,
    broker_endpoint: Option<&str>,
    v1_proof: Option<&str>,
) -> Result<EnvBlock, LaunchError> {
    // The broker vouches for this process: nothing from the player's environment may put code into it.
    env::harden(&mut env);

    env.set("SS14_LOADER_CONTENT_DB", &request.content_db_path);
    env.set("SS14_LOADER_CONTENT_VERSION", request.content_version.to_string());
    env.set("SS14_LAUNCHER_PATH", request.launcher_path.as_deref().unwrap_or(""));
    env.set("DOTNET_MULTILEVEL_LOOKUP", "0");
    env.set("DOTNET_TieredPGO", "1");
    env.set("DOTNET_ReadyToRun", "0");

    // Never pass on our own (a redial may have started the launcher with the previous game's).
    env.remove(broker::ENV_VAR);
    env.remove(proof::ENV_VAR);

    if let Some(account) = &request.account {
        env.set("ROBUST_AUTH_TOKEN", &account.token);
        env.set("ROBUST_AUTH_USERID", proof::format_user_id(&account.user_id));
        env.set("ROBUST_AUTH_PUBKEY", account.auth_public_key.as_deref().unwrap_or(""));
        env.set("ROBUST_AUTH_SERVER", AUTH_SERVER);
        if let Some(endpoint) = broker_endpoint {
            env.set(broker::ENV_VAR, endpoint);
        }
        if let Some(token) = v1_proof {
            env.set(proof::ENV_VAR, token);
        }
    }

    if !request.modules.is_empty() {
        let content_dir = Path::new(&request.content_db_path)
            .parent()
            .ok_or_else(|| LaunchError::InvalidRequest("contentDbPath has no directory".into()))?;
        for module in &request.modules {
            let dir = content_dir.join("modules").join(&module.name).join(&module.version);
            let name = format!("ROBUST_MODULE_{}", module.name.to_ascii_uppercase().replace('.', "_"));
            env.set(name, dir.into_os_string());
        }
    }
    Ok(env)
}

/// Makes version 2 proofs for the broker, unmasking the shares for each one.
fn broker_signer(shares: ShareSource, launcher_version: String) -> Signer {
    Arc::new(move |user_id, challenge| {
        let shares = shares();
        Some(proof::token(
            &shares,
            &proof::payload_v2(user_id, challenge, &launcher_version),
        ))
    })
}

/// Parses `json` and launches it.
pub fn launch_json(json: &[u8], context: &LaunchContext) -> Result<Launched, LaunchError> {
    let request = LaunchRequest::parse(json).map_err(LaunchError::InvalidRequest)?;
    launch(&request, context)
}

/// Checks, builds and starts the loader for `request`.
pub fn launch(request: &LaunchRequest, context: &LaunchContext) -> Result<Launched, LaunchError> {
    request.validate().map_err(LaunchError::InvalidRequest)?;

    if let Some(trust) = &context.trust {
        verify::verify_loader(Path::new(&request.loader_path), &trust.loader_pins).map_err(LaunchError::Refused)?;
        verify::verify_engine(
            Path::new(&request.engine_path),
            &request.engine_signature,
            Path::new(&request.engine_public_key_path),
            &trust.engine,
        )
        .map_err(LaunchError::Refused)?;
    }

    // Tells Frontier 15 servers this client was started by the genuine launcher: the game asks the broker for a proof
    // at each login. Keyed builds only.
    let mut broker = None;
    let mut broker_state = BROKER_NOT_REQUESTED;
    let mut v1_proof = None;
    if let (Some(trust), Some(account)) = (&context.trust, &request.account) {
        let signer = broker_signer(trust.shares.clone(), context.launcher_version.clone());
        match Broker::start(account.user_id, signer) {
            Ok(started) => {
                broker = Some(started);
                broker_state = BROKER_RUNNING;
            }
            // The game still starts; Frontier 15 servers will not let it in.
            Err(_) => broker_state = BROKER_FAILED,
        }

        // For servers from before login-bound proofs.
        let shares = (trust.shares)();
        let payload = proof::payload_v1(&account.user_id, proof::unix_now(), &context.launcher_version);
        v1_proof = Some(proof::token(&shares, &payload));
    }

    let env = loader_env(
        request,
        EnvBlock::inherited(),
        broker.as_ref().map(Broker::endpoint),
        v1_proof.as_deref(),
    )?;

    let mut command = Command::new(&request.loader_path);
    command.args(loader_args(request));
    command.env_clear();
    command.envs(env.iter());

    let mut output = (None, None);
    if request.redirect_output {
        let (out_read, out_write) = pipe()?;
        let (err_read, err_write) = pipe()?;
        command.stdout(out_write).stderr(err_write);
        output = (Some(out_read), Some(err_read));
    }

    let spawned = SharedChild::spawn(&mut command);
    // Our copies of the write ends go with the command, so the readers see the end when the game exits.
    drop(command);
    let child = spawned.map_err(|e| LaunchError::Failed(format!("{}: {e}", request.loader_path)))?;

    let game = Game::watch(child, broker, broker_state)?;
    Ok(Launched {
        game,
        stdout: output.0,
        stderr: output.1,
    })
}

fn pipe() -> Result<(os_pipe::PipeReader, os_pipe::PipeWriter), LaunchError> {
    os_pipe::pipe().map_err(|e| LaunchError::Failed(format!("cannot make an output pipe: {e}")))
}

/// A running (or finished) game. Cloning shares it; the waiter thread owns the child and the broker, so the game is
/// reaped and the broker stopped when it exits whether or not anyone still holds a handle.
#[derive(Clone)]
pub struct Game {
    inner: Arc<GameInner>,
}

struct GameInner {
    child: SharedChild,
    /// Waits, checks and signals without trusting a pid that may have been reused.
    #[cfg(unix)]
    watch: crate::child::ChildWatch,
    pid: u32,
    exit: Mutex<Option<i32>>,
    exited: Condvar,
    signed: Option<Arc<AtomicI32>>,
    broker_state: i32,
}

impl GameInner {
    /// Whether the game is still running: the process the broker admitted is still the one it names.
    #[cfg(unix)]
    fn alive(&self) -> bool {
        !self.watch.has_exited()
    }

    /// Whether the game is still running. The open process handle keeps its pid reserved until it is dropped, after
    /// the broker has stopped.
    #[cfg(not(unix))]
    fn alive(&self) -> bool {
        matches!(self.child.try_wait(), Ok(None))
    }

    /// Blocks until the game has exited, without reaping it on Unix, so its pid is still its own.
    #[cfg(unix)]
    fn wait_exited(&self) {
        self.watch.wait_exited();
    }

    #[cfg(not(unix))]
    fn wait_exited(&self) {
        let _ = self.child.wait();
    }

    #[cfg(unix)]
    fn kill(&self) -> io::Result<()> {
        self.watch.kill()
    }

    /// SharedChild takes the lock it reaps under, and the process handle keeps the pid from being reused anyway.
    #[cfg(not(unix))]
    fn kill(&self) -> io::Result<()> {
        self.child.kill()
    }

    /// Reaps the game after [`GameInner::wait_exited`]; -1 if the code cannot be known (reaped by someone else).
    fn reap(&self) -> i32 {
        #[cfg(unix)]
        let status = self.watch.reap(|| self.child.wait());
        #[cfg(not(unix))]
        let status = self.child.wait();
        status.map_or(-1, exit_code)
    }
}

impl Game {
    fn watch(child: SharedChild, broker: Option<Broker>, broker_state: i32) -> Result<Game, LaunchError> {
        let pid = child.id();
        let inner = Arc::new(GameInner {
            #[cfg(unix)]
            watch: crate::child::ChildWatch::new(pid),
            child,
            pid,
            exit: Mutex::new(None),
            exited: Condvar::new(),
            signed: broker.as_ref().map(Broker::signed_counter),
            broker_state,
        });

        // The broker answers this process only, only while it runs, and asks again right before each signature.
        if let Some(broker) = &broker {
            let game = inner.clone();
            broker.admit_child(pid, Arc::new(move || game.alive()));
        }

        let waiter = inner.clone();
        let spawned = std::thread::Builder::new().name("dh-guard-game".into()).spawn(move || {
            let mut broker = broker;
            // Admission ends before the pid is freed: the exit is seen without reaping, the broker stopped, and only
            // then is the child reaped.
            waiter.wait_exited();
            if let Some(broker) = broker.as_mut() {
                broker.stop();
            }
            let code = waiter.reap();
            *waiter.exit.lock().unwrap_or_else(|p| p.into_inner()) = Some(code);
            waiter.exited.notify_all();
        });

        if let Err(e) = spawned {
            // Nothing would reap it or stop the broker (dropped with the closure): do not leave it running unwatched.
            let _ = inner.kill();
            inner.wait_exited();
            let _ = inner.reap();
            return Err(LaunchError::Failed(format!("cannot start the waiter thread: {e}")));
        }
        Ok(Game { inner })
    }

    pub fn pid(&self) -> u32 {
        self.inner.pid
    }

    /// The exit code once the game has exited (within `timeout`, or ever with None), else None.
    pub fn wait(&self, timeout: Option<Duration>) -> Option<i32> {
        let deadline = timeout.map(|t| Instant::now() + t);
        let mut exit = self.inner.exit.lock().unwrap_or_else(|p| p.into_inner());
        loop {
            if let Some(code) = *exit {
                return Some(code);
            }
            exit = match deadline {
                None => self.inner.exited.wait(exit).unwrap_or_else(|p| p.into_inner()),
                Some(deadline) => {
                    let left = deadline.saturating_duration_since(Instant::now());
                    if left.is_zero() {
                        return None;
                    }
                    self.inner
                        .exited
                        .wait_timeout(exit, left)
                        .unwrap_or_else(|p| p.into_inner())
                        .0
                }
            };
        }
    }

    /// Terminates the game; nothing happens if it already exited. It never signals a reaped child's pid: on Unix the
    /// signal goes through a pidfd where there is one, and never after the waiter's reap.
    pub fn kill(&self) -> io::Result<()> {
        if self.inner.exit.lock().unwrap_or_else(|p| p.into_inner()).is_some() {
            return Ok(());
        }
        self.inner.kill()
    }

    pub fn signed(&self) -> i32 {
        self.inner.signed.as_ref().map_or(0, |s| s.load(Ordering::SeqCst))
    }

    pub fn broker_state(&self) -> i32 {
        self.inner.broker_state
    }
}

/// The exit code as .NET's `Process.ExitCode` reports it: on Unix the status, or 128 + the signal that killed it.
#[cfg(unix)]
fn exit_code(status: ExitStatus) -> i32 {
    use std::os::unix::process::ExitStatusExt;
    match (status.code(), status.signal()) {
        (Some(code), _) => code,
        (None, Some(signal)) => 128 + signal,
        (None, None) => -1,
    }
}

/// The exit code as .NET's `Process.ExitCode` reports it: the process's DWORD, as a signed int.
#[cfg(not(unix))]
fn exit_code(status: ExitStatus) -> i32 {
    status.code().unwrap_or(-1)
}

impl From<io::Error> for LaunchError {
    fn from(e: io::Error) -> Self {
        LaunchError::Failed(e.to_string())
    }
}

// The FFI hands a Game to whatever threads the launcher calls from.
const _: () = {
    const fn shareable<T: Send + Sync>() {}
    shareable::<Game>();
};

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// An absolute loader path on the system the tests run on (`/opt/…` isn't absolute on Windows).
    #[cfg(windows)]
    const LOADER: &str = r"C:\dh\loader\DarkHaven.Loader.exe";
    #[cfg(not(windows))]
    const LOADER: &str = "/opt/dh/loader/DarkHaven.Loader";

    fn request(extra: serde_json::Value) -> LaunchRequest {
        let mut v = json!({
            "loaderPath": LOADER,
            "enginePath": "/data/engines/1.0.0.zip",
            "engineSignature": "abcd",
            "enginePublicKeyPath": "/opt/dh/signing_key",
            "contentDbPath": "/data/content.db",
            "contentVersion": 42,
            "modules": [{"name": "Robust.Client.WebView", "version": "1.2.3"}],
            "launcherPath": null,
            "username": null,
            "compatMode": true,
            "connectAddress": "udp://127.0.0.1:1212/",
            "ss14Address": "ss14://127.0.0.1/",
            "build": null,
            "extraCvars": [],
            "account": null,
            "redirectOutput": false
        });
        for (k, value) in extra.as_object().unwrap() {
            v[k] = value.clone();
        }
        LaunchRequest::parse(v.to_string().as_bytes()).unwrap()
    }

    #[test]
    fn args_are_in_the_loaders_order() {
        let r = request(json!({
            "extraCvars": ["net.logging=true"],
            "build": {"engineVersion": "1.0.0", "version": "", "forkId": "frontier", "hash": null,
                "manifestHash": "MH", "manifestUrl": null, "manifestDownloadUrl": "https://d", "downloadUrl": null},
        }));
        assert_eq!(
            loader_args(&r),
            [
                "/data/engines/1.0.0.zip",
                "abcd",
                "/opt/dh/signing_key",
                "--username",
                "JoeGenero",
                "--cvar",
                "display.compat=true",
                "--cvar",
                "launch.launcher=true",
                "--cvar",
                "net.logging=true",
                "--launcher",
                "--connect-address",
                "udp://127.0.0.1:1212/",
                "--ss14-address",
                "ss14://127.0.0.1/",
                "--cvar",
                "build.engine_version=1.0.0",
                "--cvar",
                "build.fork_id=frontier",
                "--cvar",
                "build.manifest_hash=MH",
                "--cvar",
                "build.manifest_download_url=https://d",
            ]
        );
    }

    #[test]
    fn the_username_comes_from_the_account_then_the_request() {
        let with_name = request(json!({"username": "Guest"}));
        assert_eq!(loader_args(&with_name)[4], "Guest");
        let with_account = request(
            json!({"username": "Guest", "account": {"username": "Player", "token": "t",
            "userId": "11111111-2222-3333-4444-555555555555", "authPublicKey": null}}),
        );
        assert_eq!(loader_args(&with_account)[4], "Player");
    }

    #[test]
    fn env_is_hardened_and_filled_in() {
        let r = request(
            json!({"launcherPath": "/opt/dh/launcher", "account": {"username": "Player",
            "token": "tok", "userId": "ABCDEF01-2345-6789-ABCD-EF0123456789", "authPublicKey": "PK"},
            "modules": [{"name": "Robust.Client.WebView", "version": "1.2.3"}, {"name": "x", "version": "2"}]}),
        );
        let base = EnvBlock::from_pairs([
            ("PATH", "/usr/bin"),
            ("DOTNET_STARTUP_HOOKS", "/tmp/hook.dll"),
            ("ROBUST_MODULE_EVIL", "/tmp/evil"),
            ("DH_LAUNCH_BROKER", "unix:/stale"),
            ("DH_LAUNCH_PROOF", "stale"),
        ]);
        let env = loader_env(&r, base, Some("unix:/run/x.sock"), Some("v1token")).unwrap();
        let get = |n: &str| env.get(n).map(|v| v.to_string_lossy().into_owned());

        assert_eq!(get("PATH").as_deref(), Some("/usr/bin"));
        assert_eq!(get("DOTNET_STARTUP_HOOKS"), None);
        assert_eq!(get("ROBUST_MODULE_EVIL"), None);
        assert_eq!(get("DOTNET_EnableDiagnostics_IPC").as_deref(), Some("0"));
        assert_eq!(get("DOTNET_EnableDiagnostics_Debugger").as_deref(), Some("0"));
        assert_eq!(get("DOTNET_EnableDiagnostics_Profiler").as_deref(), Some("0"));
        assert_eq!(get("SS14_LOADER_CONTENT_DB").as_deref(), Some("/data/content.db"));
        assert_eq!(get("SS14_LOADER_CONTENT_VERSION").as_deref(), Some("42"));
        assert_eq!(get("SS14_LAUNCHER_PATH").as_deref(), Some("/opt/dh/launcher"));
        assert_eq!(get("DOTNET_MULTILEVEL_LOOKUP").as_deref(), Some("0"));
        assert_eq!(get("DOTNET_TieredPGO").as_deref(), Some("1"));
        assert_eq!(get("DOTNET_ReadyToRun").as_deref(), Some("0"));
        assert_eq!(get("ROBUST_AUTH_TOKEN").as_deref(), Some("tok"));
        assert_eq!(
            get("ROBUST_AUTH_USERID").as_deref(),
            Some("abcdef01-2345-6789-abcd-ef0123456789")
        );
        assert_eq!(get("ROBUST_AUTH_PUBKEY").as_deref(), Some("PK"));
        assert_eq!(get("ROBUST_AUTH_SERVER").as_deref(), Some(AUTH_SERVER));
        assert_eq!(get("DH_LAUNCH_BROKER").as_deref(), Some("unix:/run/x.sock"));
        assert_eq!(get("DH_LAUNCH_PROOF").as_deref(), Some("v1token"));
        let module = Path::new("/data")
            .join("modules")
            .join("Robust.Client.WebView")
            .join("1.2.3");
        assert_eq!(
            get("ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW"),
            Some(module.to_string_lossy().into_owned())
        );
        assert!(get("ROBUST_MODULE_X").is_some());
    }

    #[test]
    fn without_an_account_there_is_no_auth_broker_or_proof() {
        let r = request(json!({}));
        let base = EnvBlock::from_pairs([("DH_LAUNCH_BROKER", "unix:/stale"), ("DH_LAUNCH_PROOF", "stale")]);
        let env = loader_env(&r, base, Some("unix:/ignored"), Some("ignored")).unwrap();
        for name in [
            "ROBUST_AUTH_TOKEN",
            "ROBUST_AUTH_USERID",
            "DH_LAUNCH_BROKER",
            "DH_LAUNCH_PROOF",
        ] {
            assert!(env.get(name).is_none(), "{name}");
        }
        assert_eq!(env.get("SS14_LAUNCHER_PATH").unwrap(), "");
    }
}
