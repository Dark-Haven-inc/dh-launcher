//! The C ABI, driven the way the launcher drives it. The launches need a development build (tests are built without a
//! key unless DH_LAUNCH_SIGNING_KEY is set) and a Unix shell as the stand-in loader.

use dh_guard::ffi::*;
use std::ptr;

fn info() -> DhGuardInfo {
    let mut info = std::mem::MaybeUninit::<DhGuardInfo>::uninit();
    assert_eq!(unsafe { dh_guard_info(info.as_mut_ptr()) }, DH_GUARD_OK);
    unsafe { info.assume_init() }
}

fn last_error() -> String {
    let len = unsafe { dh_guard_last_error(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { dh_guard_last_error(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

#[test]
fn info_describes_the_build() {
    let info = info();
    assert_eq!(info.abi_version, 1);
    let version_len = info.launcher_version.iter().position(|&b| b == 0).unwrap();
    assert!(version_len > 0);
    // The share count is no longer exposed: always 0, keyed or not.
    assert_eq!(info.share_count, 0);
    if info.has_key == 0 {
        assert_eq!(info.pinned_files, 0);
        assert_eq!(info.public_key_len, 0);
    } else {
        assert!(info.pinned_files > 0);
        assert_eq!(info.public_key_len, 91);
    }
}

#[test]
fn bad_calls_fail_with_a_message() {
    let (mut game, mut pid, mut out, mut err) = (ptr::null_mut(), 0, 0isize, 0isize);
    let json = b"{}";
    let code = unsafe { dh_guard_launch(json.as_ptr(), json.len(), &mut game, &mut pid, &mut out, &mut err) };
    assert_eq!(code, DH_GUARD_E_REQUEST);
    assert!(last_error().contains("invalid launch request"), "{}", last_error());
    assert!(game.is_null());

    let code = unsafe { dh_guard_launch(json.as_ptr(), json.len(), ptr::null_mut(), &mut pid, &mut out, &mut err) };
    assert_eq!(code, DH_GUARD_E_ARGUMENT);

    let mut exit = 0;
    assert_eq!(
        unsafe { dh_guard_game_wait(ptr::null_mut(), 0, &mut exit) },
        DH_GUARD_E_ARGUMENT
    );
    assert_eq!(unsafe { dh_guard_game_kill(ptr::null_mut()) }, DH_GUARD_E_ARGUMENT);
    unsafe { dh_guard_game_free(ptr::null_mut()) };

    // A short buffer gets a prefix and the full length back.
    let full = last_error();
    let mut small = [0u8; 4];
    assert_eq!(
        unsafe { dh_guard_last_error(small.as_mut_ptr(), small.len()) },
        full.len()
    );
    assert_eq!(&small, &full.as_bytes()[..4]);
}

#[cfg(unix)]
mod unix {
    use super::*;
    use std::fs::File;
    use std::io::Read;
    use std::os::fd::FromRawFd;

    /// The loader is /bin/sh and the "engine" is a script, so the guard's argv lands in the script's "$@".
    fn launch(script: &str, redirect: bool) -> (*mut DhGuardGame, i32, isize, isize) {
        let dir = tempfile::tempdir().unwrap();
        let script_path = dir.path().join("fake-loader.sh");
        std::fs::write(&script_path, script).unwrap();
        let json = serde_json::json!({
            "loaderPath": "/bin/sh",
            "enginePath": script_path,
            "engineSignature": "sig",
            "enginePublicKeyPath": "/nonexistent/key",
            "contentDbPath": dir.path().join("content.db"),
            "contentVersion": 1,
            "modules": [],
            "launcherPath": null,
            "username": null,
            "compatMode": true,
            "connectAddress": "udp://localhost:1212/",
            "ss14Address": "ss14://localhost/",
            "build": null,
            "extraCvars": [],
            "account": {"username": "Player", "token": "t", "userId": "11111111-2222-3333-4444-555555555555",
                "authPublicKey": null},
            "redirectOutput": redirect
        })
        .to_string();

        let (mut game, mut pid, mut out, mut err) = (ptr::null_mut(), 0, 0isize, 0isize);
        let code = unsafe { dh_guard_launch(json.as_ptr(), json.len(), &mut game, &mut pid, &mut out, &mut err) };
        assert_eq!(code, DH_GUARD_OK, "{}", last_error());
        // The shell has read the script by the time it exits; keep the directory until then anyway.
        std::mem::forget(dir);
        (game, pid, out, err)
    }

    fn read_all(handle: isize) -> String {
        let mut text = String::new();
        unsafe { File::from_raw_fd(handle as i32) }
            .read_to_string(&mut text)
            .unwrap();
        text
    }

    #[test]
    fn a_dev_build_launches_waits_and_reports_the_exit_code() {
        if info().has_key != 0 {
            return; // a keyed build only starts the pinned loader
        }
        let (game, pid, out, err) = launch(
            "printf '%s\\n' \"$@\"; printf '%s\\n' \"$DH_LAUNCH_BROKER|$ROBUST_AUTH_USERID\" >&2; exit 3",
            true,
        );
        assert!(pid > 0);
        assert!(out >= 0 && err >= 0);

        let stdout = read_all(out);
        let args: Vec<&str> = stdout.lines().collect();
        assert_eq!(
            args,
            [
                "sig",
                "/nonexistent/key",
                "--username",
                "Player",
                "--cvar",
                "display.compat=true",
                "--cvar",
                "launch.launcher=true",
                "--launcher",
                "--connect-address",
                "udp://localhost:1212/",
                "--ss14-address",
                "ss14://localhost/"
            ]
        );
        // No key, no broker: the account still reaches the game.
        assert_eq!(read_all(err), "|11111111-2222-3333-4444-555555555555\n");

        let mut exit = 0;
        assert_eq!(unsafe { dh_guard_game_wait(game, -1, &mut exit) }, DH_GUARD_OK);
        assert_eq!(exit, 3);
        assert_eq!(unsafe { dh_guard_game_wait(game, 0, &mut exit) }, DH_GUARD_OK);
        assert_eq!(unsafe { dh_guard_game_broker_state(game) }, 0);
        assert_eq!(unsafe { dh_guard_game_signed(game) }, 0);
        assert_eq!(unsafe { dh_guard_game_kill(game) }, DH_GUARD_OK);
        unsafe { dh_guard_game_free(game) };
    }

    #[test]
    fn wait_times_out_and_kill_ends_it() {
        if info().has_key != 0 {
            return;
        }
        let (game, _, out, err) = launch("exec sleep 30", false);
        assert_eq!((out, err), (-1, -1));

        let mut exit = 0;
        assert_eq!(unsafe { dh_guard_game_wait(game, 100, &mut exit) }, DH_GUARD_TIMEOUT);
        assert_eq!(unsafe { dh_guard_game_wait(game, -2, &mut exit) }, DH_GUARD_E_ARGUMENT);
        assert_eq!(unsafe { dh_guard_game_kill(game) }, DH_GUARD_OK);
        assert_eq!(unsafe { dh_guard_game_wait(game, 10_000, &mut exit) }, DH_GUARD_OK);
        assert_eq!(exit, 128 + 9);
        unsafe { dh_guard_game_free(game) };
    }

    #[test]
    fn freeing_the_handle_leaves_the_game_running() {
        if info().has_key != 0 {
            return;
        }
        let (game, pid, _, _) = launch("exec sleep 30", false);
        unsafe { dh_guard_game_free(game) };
        // Still there (signal 0 only checks), then cleaned up by hand.
        assert_eq!(unsafe { libc::kill(pid, 0) }, 0);
        assert_eq!(unsafe { libc::kill(pid, libc::SIGKILL) }, 0);
    }
}
