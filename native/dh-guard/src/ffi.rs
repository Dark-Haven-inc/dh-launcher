//! The C ABI the launcher calls (version 1). See docs/GUARD.md for the header.
//!
//! Anyone can load this library and call these functions, so none of them signs an arbitrary challenge, admits an
//! arbitrary process or hands out key material: the only way to get a proof signed is to have the guard start the
//! loader itself, and it answers only that child. Every function catches panics (a panic must not unwind into .NET)
//! and reports failures as a negative status with a message kept per thread for `dh_guard_last_error`.

use crate::embedded;
use crate::launch::{self, Game, LaunchContext, LaunchError, Trust};
use crate::split_ecdsa;
use std::cell::RefCell;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

pub const ABI_VERSION: i32 = 1;

pub const DH_GUARD_OK: i32 = 0;
/// `dh_guard_game_wait` only: still running when the timeout passed.
pub const DH_GUARD_TIMEOUT: i32 = 1;
/// A panic or another failure inside the guard.
pub const DH_GUARD_E_INTERNAL: i32 = -1;
/// A null pointer, a bad timeout or similar misuse of the API.
pub const DH_GUARD_E_ARGUMENT: i32 = -2;
/// The launch request is not valid JSON of the right shape, or asks for something not allowed.
pub const DH_GUARD_E_REQUEST: i32 = -3;
/// A keyed build refused to launch: the loader or the engine is not the genuine one.
pub const DH_GUARD_E_REFUSED: i32 = -4;
/// Starting or controlling the process failed (the OS said no).
pub const DH_GUARD_E_OS: i32 = -5;

/// Opaque to the caller; freed with `dh_guard_game_free`.
pub struct DhGuardGame(Game);

#[repr(C)]
pub struct DhGuardInfo {
    pub abi_version: i32,
    pub has_key: i32,
    /// Kept for ABI v1, but always 0: a keyed build no longer reports how many shares it split the key into (it was
    /// a free hint to a key scanner). See docs/GUARD.md.
    pub share_count: i32,
    pub pinned_files: i32,
    pub public_key_len: i32,
    /// P-256 SubjectPublicKeyInfo (DER) in a keyed build.
    pub public_key: [u8; 96],
    /// NUL-terminated UTF-8.
    pub launcher_version: [u8; 64],
}

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_error(message: impl Into<String>) {
    let message = message.into();
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

fn fail(code: i32, message: impl Into<String>) -> i32 {
    set_error(message);
    code
}

/// Runs `body`, turning a panic into `DH_GUARD_E_INTERNAL`.
fn guarded(body: impl FnOnce() -> i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(code) => code,
        Err(panic) => {
            let detail = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_default();
            fail(DH_GUARD_E_INTERNAL, format!("internal error in dh_guard: {detail}"))
        }
    }
}

/// This build's launch context: the embedded key, pins and engine anchors, or none of them in a development build.
fn context() -> &'static LaunchContext {
    static CONTEXT: OnceLock<LaunchContext> = OnceLock::new();
    CONTEXT.get_or_init(|| LaunchContext {
        trust: if embedded::has_key() {
            Some(Trust {
                shares: Arc::new(|| embedded::shares().expect("a keyed build has shares")),
                loader_pins: embedded::loader_pins(),
                engine: embedded::engine_trust().expect("a keyed build has the engine key"),
            })
        } else {
            None
        },
        launcher_version: embedded::launcher_version().to_string(),
    })
}

#[cfg(unix)]
fn into_os_handle(reader: os_pipe::PipeReader) -> isize {
    use std::os::fd::IntoRawFd;
    reader.into_raw_fd() as isize
}

#[cfg(windows)]
fn into_os_handle(reader: os_pipe::PipeReader) -> isize {
    use std::os::windows::io::IntoRawHandle;
    reader.into_raw_handle() as isize
}

/// Starts the game described by the UTF-8 JSON launch request.
///
/// On success `*game` is a handle to free with `dh_guard_game_free` and `*pid` the loader's pid. With
/// `redirectOutput` the game's stdout and stderr are pipes whose read ends are returned as OS handles the caller owns
/// (a file descriptor on Unix, a HANDLE on Windows; synchronous and not inheritable); otherwise both are -1 and the
/// game shares the launcher's.
///
/// # Safety
/// `json` must point to `len` readable bytes; the out-pointers must be valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_launch(
    json: *const u8,
    len: usize,
    game: *mut *mut DhGuardGame,
    pid: *mut i32,
    stdout_handle: *mut isize,
    stderr_handle: *mut isize,
) -> i32 {
    guarded(|| {
        if json.is_null() || game.is_null() || pid.is_null() || stdout_handle.is_null() || stderr_handle.is_null() {
            return fail(DH_GUARD_E_ARGUMENT, "dh_guard_launch: a pointer argument is null");
        }
        // SAFETY: the caller promises `len` readable bytes at `json`.
        let request = unsafe { std::slice::from_raw_parts(json, len) };

        let launched = match launch::launch_json(request, context()) {
            Ok(launched) => launched,
            Err(e) => {
                let code = match e {
                    LaunchError::InvalidRequest(_) => DH_GUARD_E_REQUEST,
                    LaunchError::Refused(_) => DH_GUARD_E_REFUSED,
                    LaunchError::Failed(_) => DH_GUARD_E_OS,
                };
                return fail(code, e.to_string());
            }
        };

        let child_pid = launched.game.pid() as i32;
        let out = launched.stdout.map_or(-1, into_os_handle);
        let err = launched.stderr.map_or(-1, into_os_handle);
        let handle = Box::into_raw(Box::new(DhGuardGame(launched.game)));
        // SAFETY: checked non-null above; the caller promises they are writable.
        unsafe {
            *game = handle;
            *pid = child_pid;
            *stdout_handle = out;
            *stderr_handle = err;
        }
        DH_GUARD_OK
    })
}

/// Borrows the game behind a handle.
///
/// # Safety
/// `game` must be null or a live handle from `dh_guard_launch`.
unsafe fn game_ref<'a>(game: *mut DhGuardGame) -> Option<&'a Game> {
    // SAFETY: per the caller's promise.
    unsafe { game.as_ref() }.map(|g| &g.0)
}

/// Waits for the game to exit: 0 with `*exit_code` set once it has (-1 if the code could not be determined), 1 if it
/// is still running after `timeout_ms` (-1 waits forever). Callable repeatedly and from several threads.
///
/// # Safety
/// `game` must be a live handle; `exit_code` must be valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_game_wait(game: *mut DhGuardGame, timeout_ms: i32, exit_code: *mut i32) -> i32 {
    guarded(|| {
        // SAFETY: per the caller's promise.
        let Some(game) = (unsafe { game_ref(game) }) else {
            return fail(DH_GUARD_E_ARGUMENT, "dh_guard_game_wait: null handle");
        };
        if exit_code.is_null() || timeout_ms < -1 {
            return fail(
                DH_GUARD_E_ARGUMENT,
                "dh_guard_game_wait: null exit_code or a timeout below -1",
            );
        }
        let timeout = (timeout_ms >= 0).then(|| Duration::from_millis(timeout_ms as u64));
        match game.wait(timeout) {
            Some(code) => {
                // SAFETY: checked non-null; the caller promises it is writable.
                unsafe { *exit_code = code };
                DH_GUARD_OK
            }
            None => DH_GUARD_TIMEOUT,
        }
    })
}

/// Terminates the game (SIGKILL on Unix, TerminateProcess on Windows); does nothing if it already exited.
///
/// # Safety
/// `game` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_game_kill(game: *mut DhGuardGame) -> i32 {
    guarded(|| {
        // SAFETY: per the caller's promise.
        let Some(game) = (unsafe { game_ref(game) }) else {
            return fail(DH_GUARD_E_ARGUMENT, "dh_guard_game_kill: null handle");
        };
        match game.kill() {
            Ok(()) => DH_GUARD_OK,
            Err(e) => fail(DH_GUARD_E_OS, format!("could not kill the game: {e}")),
        }
    })
}

/// Proofs the broker has signed for this game so far.
///
/// # Safety
/// `game` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_game_signed(game: *mut DhGuardGame) -> i32 {
    guarded(|| {
        // SAFETY: per the caller's promise.
        match unsafe { game_ref(game) } {
            Some(game) => game.signed(),
            None => fail(DH_GUARD_E_ARGUMENT, "dh_guard_game_signed: null handle"),
        }
    })
}

/// 1: the broker is running (or ran until the game exited); 0: not asked for (development build, or no account);
/// -1: it failed to start, and the game was started without one. -2 for a null handle.
///
/// # Safety
/// `game` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_game_broker_state(game: *mut DhGuardGame) -> i32 {
    guarded(|| {
        // SAFETY: per the caller's promise.
        match unsafe { game_ref(game) } {
            Some(game) => game.broker_state(),
            None => fail(DH_GUARD_E_ARGUMENT, "dh_guard_game_broker_state: null handle"),
        }
    })
}

/// Releases the handle. The game keeps running, and its broker keeps answering it until it exits.
///
/// # Safety
/// `game` must be null or a live handle, not used again afterwards (nor concurrently with this call).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_game_free(game: *mut DhGuardGame) {
    if game.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: per the caller's promise it came from Box::into_raw in dh_guard_launch.
        drop(unsafe { Box::from_raw(game) });
    }));
}

/// Describes this build: whether it has a key, how many shares and pinned files, the public key it signs for.
///
/// # Safety
/// `out` must be valid for writes of a `DhGuardInfo`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_info(out: *mut DhGuardInfo) -> i32 {
    guarded(|| {
        if out.is_null() {
            return fail(DH_GUARD_E_ARGUMENT, "dh_guard_info: null out");
        }
        let mut info = DhGuardInfo {
            abi_version: ABI_VERSION,
            has_key: embedded::has_key() as i32,
            // The share count is not exposed (it was a free hint to a key scanner); the field stays for ABI v1.
            share_count: 0,
            pinned_files: 0,
            public_key_len: 0,
            public_key: [0; 96],
            launcher_version: [0; 64],
        };
        if let Some(shares) = embedded::shares() {
            let spki = split_ecdsa::public_key_spki(&shares);
            info.public_key[..spki.len()].copy_from_slice(&spki);
            info.public_key_len = spki.len() as i32;
        }
        info.pinned_files = embedded::loader_pins().len() as i32;
        let version = embedded::launcher_version().as_bytes();
        let n = version.len().min(info.launcher_version.len() - 1);
        info.launcher_version[..n].copy_from_slice(&version[..n]);

        // SAFETY: checked non-null; the caller promises it is writable.
        unsafe { out.write(info) };
        DH_GUARD_OK
    })
}

/// Copies up to `cap` bytes of this thread's last error message (UTF-8, not NUL-terminated) into `buf` and returns
/// its full length, so a caller can size a buffer by calling with `cap` 0 first.
///
/// # Safety
/// `buf` must be valid for `cap` bytes of writes (it may be null when `cap` is 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dh_guard_last_error(buf: *mut u8, cap: usize) -> usize {
    catch_unwind(AssertUnwindSafe(|| {
        LAST_ERROR.with(|e| {
            let message = e.borrow();
            let bytes = message.as_bytes();
            let n = bytes.len().min(cap);
            if n > 0 && !buf.is_null() {
                // SAFETY: the caller promises `cap` writable bytes at `buf`.
                unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, n) };
            }
            bytes.len()
        })
    }))
    .unwrap_or(0)
}
