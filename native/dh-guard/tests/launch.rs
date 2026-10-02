//! End to end on Linux: the guard verifies and starts a loader, the loader asks the broker for a proof, and the proof
//! verifies. The loader is this very test binary, copied into a pinned directory under the loader's file name: when
//! it runs under that name, `main` plays the loader instead of running the tests.

#[cfg(target_os = "linux")]
fn main() {
    linux::main();
}

#[cfg(not(target_os = "linux"))]
fn main() {
    println!("launch tests run on Linux only");
}

#[cfg(target_os = "linux")]
mod linux {
    use dh_guard::build_inputs::{self, encode_hex, sha256};
    use dh_guard::launch::{self, LaunchContext, LaunchError, Launched, Trust};
    use dh_guard::proof;
    use dh_guard::split_ecdsa::{self, Shares};
    use dh_guard::verify::{EngineTrust, LOADER_FILE_NAME, LoaderPin};
    use ed25519_dalek::Signer as _;
    use p256::ecdsa::signature::Verifier;
    use p256::ecdsa::{Signature, VerifyingKey};
    use p256::pkcs8::DecodePublicKey;
    use serde_json::json;
    use std::collections::BTreeMap;
    use std::fs;
    use std::io::{Read, Write};
    use std::os::unix::fs::PermissionsExt;
    use std::os::unix::net::UnixStream;
    use std::panic::{AssertUnwindSafe, catch_unwind};
    use std::path::{Path, PathBuf};
    use std::sync::{Arc, OnceLock};
    use std::time::Duration;

    const USER: &str = "11111111-2222-3333-4444-555555555555";
    const VERSION: &str = "9.9.9";

    fn challenge() -> proof::Challenge {
        let mut c = [0u8; 64];
        for (i, b) in c.iter_mut().enumerate() {
            *b = (i * 3 + 1) as u8;
        }
        c
    }

    pub fn main() {
        let exe = std::env::current_exe().unwrap();
        if exe.file_name().and_then(|n| n.to_str()) == Some(LOADER_FILE_NAME) {
            fake_loader();
        }

        // What a player's environment might carry; set before any thread exists.
        for (name, value) in [
            ("DOTNET_STARTUP_HOOKS", "/tmp/hook.dll"),
            ("ROBUST_MODULE_EVIL", "/tmp/evil"),
            ("COMPlus_EnableDiagnostics", "1"),
            ("SS14_DISABLE_SIGNING", "true"),
            ("DH_LAUNCH_BROKER", "unix:/stale.sock"),
            ("DH_LAUNCH_PROOF", "stale"),
            ("DH_GUARD_TEST_PASSTHROUGH", "kept"),
        ] {
            // SAFETY: single-threaded at this point.
            unsafe { std::env::set_var(name, value) };
        }
        let _ = fixture();

        let tests: &[(&str, fn())] = &[
            ("keyed_launch_end_to_end", keyed_launch_end_to_end),
            ("kill_reports_the_signal", kill_reports_the_signal),
            ("bundled_engine_by_hash", bundled_engine_by_hash),
            ("refuses_what_it_does_not_trust", refuses_what_it_does_not_trust),
            ("dev_mode_only_launches", dev_mode_only_launches),
            (
                "output_is_inherited_unless_redirected",
                output_is_inherited_unless_redirected,
            ),
            (
                "a_broker_that_cannot_start_does_not_stop_the_game",
                a_broker_that_cannot_start_does_not_stop_the_game,
            ),
            (
                "a_squatted_socket_directory_is_sidestepped",
                a_squatted_socket_directory_is_sidestepped,
            ),
        ];
        let filter: Vec<String> = std::env::args().skip(1).filter(|a| !a.starts_with('-')).collect();

        let mut failed = 0;
        let mut ran = 0;
        for (name, test) in tests {
            if !filter.is_empty() && !filter.iter().any(|f| name.contains(f.as_str())) {
                continue;
            }
            ran += 1;
            match catch_unwind(AssertUnwindSafe(test)) {
                Ok(()) => println!("test {name} ... ok"),
                Err(_) => {
                    println!("test {name} ... FAILED");
                    failed += 1;
                }
            }
        }
        println!(
            "\ntest result: {}. {} passed; {failed} failed",
            if failed == 0 { "ok" } else { "FAILED" },
            ran - failed
        );
        std::process::exit(if failed == 0 { 0 } else { 1 });
    }

    /// The fake loader: reports its argv and environment on stdout, asks the broker (if any) for a proof, then does
    /// what `--connect-address` says (`exit:<code>` or `sleep`).
    fn fake_loader() -> ! {
        let args: Vec<String> = std::env::args().skip(1).collect();
        let mut out = String::new();
        for arg in &args {
            out.push_str(&format!("ARG={arg}\n"));
        }
        let mut vars: Vec<(String, String)> = std::env::vars_os()
            .map(|(n, v)| {
                (
                    n.to_string_lossy().into_owned(),
                    v.to_string_lossy().replace('\n', "\\n"),
                )
            })
            .collect();
        vars.sort();
        for (name, value) in vars {
            out.push_str(&format!("ENV={name}={value}\n"));
        }

        if let Ok(endpoint) = std::env::var("DH_LAUNCH_BROKER") {
            let user = std::env::var("ROBUST_AUTH_USERID").unwrap_or_default();
            let token = ask(
                &endpoint,
                &format!("dh-launch-broker/1\n{user}\n{}\n", proof::to_base64url(&challenge())),
            )
            .unwrap_or_else(|e| format!("<error {e}>"));
            out.push_str(&format!("TOKEN={token}\n"));
        }

        let mode = args
            .iter()
            .position(|a| a == "--connect-address")
            .and_then(|i| args.get(i + 1))
            .cloned()
            .unwrap_or_default();
        // "quiet:" keeps a loader whose output is not redirected from cluttering the test log.
        let (quiet, mode) = match mode.strip_prefix("quiet:") {
            Some(rest) => (true, rest.to_string()),
            None => (false, mode),
        };
        if !quiet {
            let mut stdout = std::io::stdout();
            stdout.write_all(out.as_bytes()).unwrap();
            stdout.flush().unwrap();
            eprintln!("STDERR from the loader");
        }
        if mode == "sleep" {
            std::thread::sleep(Duration::from_secs(60));
        }
        std::process::exit(mode.strip_prefix("exit:").and_then(|c| c.parse().ok()).unwrap_or(0));
    }

    /// What the engine's LaunchBrokerClient does.
    fn ask(endpoint: &str, request: &str) -> std::io::Result<String> {
        let path = endpoint.strip_prefix("unix:").unwrap_or(endpoint);
        let mut stream = UnixStream::connect(path)?;
        stream.set_read_timeout(Some(Duration::from_secs(5)))?;
        stream.write_all(request.as_bytes())?;
        let mut answer = Vec::new();
        let mut buf = [0u8; 2048];
        loop {
            let n = stream.read(&mut buf)?;
            if n == 0 {
                return Ok(String::new());
            }
            answer.extend_from_slice(&buf[..n]);
            if let Some(i) = answer.iter().position(|&b| b == b'\n') {
                return Ok(String::from_utf8_lossy(&answer[..i]).into_owned());
            }
        }
    }

    /// A pinned loader directory, a signed engine and a split key, made once.
    struct Fixture {
        _dir: tempfile::TempDir,
        loader: PathBuf,
        pins: Vec<LoaderPin>,
        engine: PathBuf,
        engine_signature: String,
        key_file: PathBuf,
        engine_key: [u8; 32],
        content_db: PathBuf,
        shares: Arc<Shares>,
        public_key: VerifyingKey,
    }

    fn fixture() -> &'static Fixture {
        static FIXTURE: OnceLock<Fixture> = OnceLock::new();
        FIXTURE.get_or_init(|| {
            let dir = tempfile::tempdir().unwrap();
            let loader_dir = dir.path().join("loader");
            fs::create_dir_all(loader_dir.join("runtimes")).unwrap();
            let loader = loader_dir.join(LOADER_FILE_NAME);
            fs::copy(std::env::current_exe().unwrap(), &loader).unwrap();
            fs::set_permissions(&loader, fs::Permissions::from_mode(0o755)).unwrap();
            fs::write(loader_dir.join("runtimes/DarkHaven.Loader.dll"), b"managed loader").unwrap();
            let pins = [LOADER_FILE_NAME, "runtimes/DarkHaven.Loader.dll"]
                .iter()
                .map(|p| LoaderPin {
                    path: p.to_string(),
                    sha256: sha256(&fs::read(loader_dir.join(p)).unwrap()),
                })
                .collect();

            let signing = ed25519_dalek::SigningKey::from_bytes(&sha256(b"launch test engine key"));
            let engine = dir.path().join("engine.zip");
            fs::write(&engine, b"PK not really an engine").unwrap();
            let engine_signature = encode_hex(&signing.sign(&fs::read(&engine).unwrap()).to_bytes());
            let mut der = vec![0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];
            der.extend_from_slice(&signing.verifying_key().to_bytes());
            let key_file = dir.path().join("signing_key");
            fs::write(
                &key_file,
                format!(
                    "-----BEGIN PUBLIC KEY-----\n{}\n-----END PUBLIC KEY-----\n",
                    proof::to_base64(&der)
                ),
            )
            .unwrap();

            let d =
                build_inputs::unseal_scalar(&build_inputs::seal_scalar(&sha256(b"launch test signing key"))).unwrap();
            let values = build_inputs::share_values(&d);
            let shares = Arc::new(Shares::from_bytes(&values));
            let public_key = VerifyingKey::from_public_key_der(&split_ecdsa::public_key_spki(&shares)).unwrap();

            Fixture {
                content_db: dir.path().join("data").join("content.db"),
                _dir: dir,
                loader,
                pins,
                engine,
                engine_signature,
                key_file,
                engine_key: signing.verifying_key().to_bytes(),
                shares,
                public_key,
            }
        })
    }

    fn keyed(f: &Fixture, bundled: Vec<[u8; 32]>) -> LaunchContext {
        let shares = f.shares.clone();
        LaunchContext {
            trust: Some(Trust {
                shares: Arc::new(move || (*shares).clone()),
                loader_pins: f.pins.clone(),
                engine: EngineTrust {
                    bundled_sha256: bundled,
                    ed25519_key: f.engine_key,
                },
            }),
            launcher_version: VERSION.to_string(),
        }
    }

    fn request(f: &Fixture, overrides: serde_json::Value) -> Vec<u8> {
        let mut v = json!({
            "loaderPath": f.loader,
            "enginePath": f.engine,
            "engineSignature": f.engine_signature,
            "enginePublicKeyPath": f.key_file,
            "contentDbPath": f.content_db,
            "contentVersion": 1234567890123i64,
            "modules": [{"name": "Robust.Client.WebView", "version": "0.1.2"}],
            "launcherPath": "/opt/dh/DarkHaven.Launcher",
            "username": "Guest",
            "compatMode": false,
            "connectAddress": "exit:0",
            "ss14Address": "ss14://frontier.example/",
            "build": {"engineVersion": "275.1.0", "version": null, "forkId": "frontier", "hash": "",
                "manifestHash": "ABC", "manifestUrl": null, "manifestDownloadUrl": null, "downloadUrl": "https://dl"},
            "extraCvars": ["net.logging=true"],
            "account": {"username": "Player", "token": "secret-token", "userId": USER, "authPublicKey": "AUTHPK"},
            "redirectOutput": true
        });
        for (k, value) in overrides.as_object().unwrap() {
            v[k] = value.clone();
        }
        v.to_string().into_bytes()
    }

    /// Reads everything the loader printed; returns once it has exited and closed its end.
    fn output(launched: &mut Launched) -> (String, String) {
        let mut out = String::new();
        launched.stdout.take().unwrap().read_to_string(&mut out).unwrap();
        let mut err = String::new();
        launched.stderr.take().unwrap().read_to_string(&mut err).unwrap();
        (out, err)
    }

    struct Report {
        args: Vec<String>,
        env: BTreeMap<String, String>,
        token: Option<String>,
    }

    fn report(stdout: &str) -> Report {
        let mut r = Report {
            args: Vec::new(),
            env: BTreeMap::new(),
            token: None,
        };
        for line in stdout.lines() {
            if let Some(arg) = line.strip_prefix("ARG=") {
                r.args.push(arg.to_string());
            } else if let Some(var) = line.strip_prefix("ENV=") {
                let (n, v) = var.split_once('=').unwrap();
                r.env.insert(n.to_string(), v.to_string());
            } else if let Some(token) = line.strip_prefix("TOKEN=") {
                r.token = Some(token.to_string());
            }
        }
        r
    }

    fn verify_token(f: &Fixture, token: &str) -> String {
        let (payload, signature) = proof::split_token(token).expect("a token");
        let signature = Signature::from_slice(&signature).unwrap();
        f.public_key.verify(&payload, &signature).expect("the token verifies");
        String::from_utf8(payload).unwrap()
    }

    fn keyed_launch_end_to_end() {
        let f = fixture();
        let mut launched = launch::launch_json(&request(f, json!({"connectAddress": "exit:7"})), &keyed(f, vec![]))
            .unwrap_or_else(|e| panic!("{e}"));
        assert!(launched.game.pid() > 0);
        assert_eq!(launched.game.broker_state(), launch::BROKER_RUNNING);

        let (stdout, stderr) = output(&mut launched);
        assert_eq!(stderr, "STDERR from the loader\n");
        let r = report(&stdout);

        let expected_args: Vec<String> = [
            f.engine.to_str().unwrap(),
            &f.engine_signature,
            f.key_file.to_str().unwrap(),
            "--username",
            "Player",
            "--cvar",
            "display.compat=false",
            "--cvar",
            "launch.launcher=true",
            "--cvar",
            "net.logging=true",
            "--launcher",
            "--connect-address",
            "exit:7",
            "--ss14-address",
            "ss14://frontier.example/",
            "--cvar",
            "build.engine_version=275.1.0",
            "--cvar",
            "build.fork_id=frontier",
            "--cvar",
            "build.manifest_hash=ABC",
            "--cvar",
            "build.download_url=https://dl",
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        assert_eq!(r.args, expected_args);

        let env = |n: &str| r.env.get(n).map(String::as_str);
        for gone in [
            "DOTNET_STARTUP_HOOKS",
            "ROBUST_MODULE_EVIL",
            "COMPlus_EnableDiagnostics",
            "SS14_DISABLE_SIGNING",
        ] {
            assert_eq!(env(gone), None, "{gone}");
        }
        assert_eq!(env("DH_GUARD_TEST_PASSTHROUGH"), Some("kept"));
        assert_eq!(env("DOTNET_EnableDiagnostics_IPC"), Some("0"));
        assert_eq!(env("DOTNET_EnableDiagnostics_Debugger"), Some("0"));
        assert_eq!(env("DOTNET_EnableDiagnostics_Profiler"), Some("0"));
        assert_eq!(env("SS14_LOADER_CONTENT_DB"), f.content_db.to_str());
        assert_eq!(env("SS14_LOADER_CONTENT_VERSION"), Some("1234567890123"));
        assert_eq!(env("SS14_LAUNCHER_PATH"), Some("/opt/dh/DarkHaven.Launcher"));
        assert_eq!(env("DOTNET_MULTILEVEL_LOOKUP"), Some("0"));
        assert_eq!(env("DOTNET_TieredPGO"), Some("1"));
        assert_eq!(env("DOTNET_ReadyToRun"), Some("0"));
        assert_eq!(env("ROBUST_AUTH_TOKEN"), Some("secret-token"));
        assert_eq!(env("ROBUST_AUTH_USERID"), Some(USER));
        assert_eq!(env("ROBUST_AUTH_PUBKEY"), Some("AUTHPK"));
        assert_eq!(env("ROBUST_AUTH_SERVER"), Some("https://auth.spacestation14.com/"));
        let module = f
            .content_db
            .parent()
            .unwrap()
            .join("modules/Robust.Client.WebView/0.1.2");
        assert_eq!(env("ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW"), module.to_str());

        let endpoint = env("DH_LAUNCH_BROKER").expect("a broker endpoint").to_string();
        assert!(
            endpoint.starts_with("unix:") && endpoint != "unix:/stale.sock",
            "{endpoint}"
        );

        // The login-bound proof the loader asked the broker for.
        let token = r.token.expect("the loader asked for a proof");
        assert_eq!(
            verify_token(f, &token),
            format!("dh-launch/2\n{USER}\n{}\n{VERSION}", proof::to_base64url(&challenge()))
        );
        assert_eq!(launched.game.signed(), 1);

        // The version 1 proof in the environment.
        let v1 = verify_token(f, env("DH_LAUNCH_PROOF").unwrap());
        let lines: Vec<&str> = v1.split('\n').collect();
        assert_eq!(lines.len(), 4);
        assert_eq!((lines[0], lines[1], lines[3]), ("dh-launch/1", USER, VERSION));
        let issued: i64 = lines[2].parse().unwrap();
        assert!((proof::unix_now() - issued).abs() < 60);

        // The exit code comes through, and the broker is gone once the game is.
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(7));
        assert_eq!(launched.game.wait(None), Some(7));
        let socket = Path::new(endpoint.strip_prefix("unix:").unwrap());
        assert!(!socket.exists(), "the socket outlived the game");
        assert!(UnixStream::connect(socket).is_err());
        launched.game.kill().unwrap(); // a no-op now
    }

    fn kill_reports_the_signal() {
        let f = fixture();
        let mut launched = launch::launch_json(
            &request(f, json!({"connectAddress": "sleep", "account": null})),
            &keyed(f, vec![]),
        )
        .unwrap_or_else(|e| panic!("{e}"));
        // No account: no broker and no proofs, keyed or not.
        assert_eq!(launched.game.broker_state(), launch::BROKER_NOT_REQUESTED);

        assert_eq!(launched.game.wait(Some(Duration::from_millis(300))), None);
        launched.game.kill().unwrap();
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(128 + 9));

        let (stdout, _) = output(&mut launched);
        let r = report(&stdout);
        assert_eq!(r.args[4], "Guest");
        assert!(r.token.is_none());
        for name in [
            "DH_LAUNCH_BROKER",
            "DH_LAUNCH_PROOF",
            "ROBUST_AUTH_TOKEN",
            "ROBUST_AUTH_USERID",
        ] {
            assert!(!r.env.contains_key(name), "{name}");
        }
        assert_eq!(launched.game.signed(), 0);
    }

    fn bundled_engine_by_hash() {
        let f = fixture();
        let hash = sha256(&fs::read(&f.engine).unwrap());
        let signature = format!("sha256:{}", encode_hex(&hash));
        let body = request(
            f,
            json!({"engineSignature": signature, "account": null, "redirectOutput": false, "connectAddress": "quiet:exit:0"}),
        );

        let launched = launch::launch_json(&body, &keyed(f, vec![sha256(b"another"), hash])).unwrap();
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(0));

        let err = launch::launch_json(&body, &keyed(f, vec![sha256(b"another")]))
            .err()
            .unwrap();
        assert!(matches!(err, LaunchError::Refused(_)), "{err}");
    }

    fn refuses_what_it_does_not_trust() {
        let f = fixture();
        let ctx = keyed(f, vec![]);
        let refused = |overrides: serde_json::Value| launch::launch_json(&request(f, overrides), &ctx).err();

        // Tampered loader directory.
        let extra = f.loader.parent().unwrap().join("cheat.dll");
        fs::write(&extra, b"x").unwrap();
        let err = refused(json!({}));
        fs::remove_file(&extra).unwrap();
        assert!(
            matches!(err, Some(LaunchError::Refused(ref m)) if m.contains("unexpected")),
            "{err:?}"
        );

        // Some other program under the right directory, or the right name elsewhere.
        let err = refused(json!({"loaderPath": "/bin/sh"}));
        assert!(matches!(err, Some(LaunchError::Refused(_))), "{err:?}");

        // An engine the key did not sign.
        let mut forged = f.engine_signature.clone().into_bytes();
        forged[0] = if forged[0] == b'0' { b'1' } else { b'0' };
        let err = refused(json!({"engineSignature": String::from_utf8(forged).unwrap()}));
        assert!(matches!(err, Some(LaunchError::Refused(_))), "{err:?}");

        // Requests the guard does not do at all.
        let err = refused(json!({"extraCvars": ["sys.sandbox=false"]}));
        assert!(matches!(err, Some(LaunchError::InvalidRequest(_))), "{err:?}");
        let err = refused(json!({"modules": [{"name": "..", "version": "1"}]}));
        assert!(matches!(err, Some(LaunchError::InvalidRequest(_))), "{err:?}");
        let err = refused(json!({"env": {"LD_PRELOAD": "/tmp/x.so"}}));
        assert!(matches!(err, Some(LaunchError::InvalidRequest(_))), "{err:?}");

        // And the untouched request still goes through.
        let launched = launch::launch_json(
            &request(
                f,
                json!({"account": null, "redirectOutput": false, "connectAddress": "quiet:exit:0"}),
            ),
            &ctx,
        )
        .unwrap();
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(0));
    }

    fn dev_mode_only_launches() {
        let f = fixture();
        let dev = LaunchContext {
            trust: None,
            launcher_version: VERSION.to_string(),
        };
        // Unpinned, unsigned: a development build checks nothing and signs nothing.
        let mut launched = launch::launch_json(
            &request(f, json!({"engineSignature": "whatever", "connectAddress": "exit:3"})),
            &dev,
        )
        .unwrap_or_else(|e| panic!("{e}"));
        assert_eq!(launched.game.broker_state(), launch::BROKER_NOT_REQUESTED);
        let (stdout, _) = output(&mut launched);
        let r = report(&stdout);
        assert!(r.token.is_none());
        assert!(!r.env.contains_key("DH_LAUNCH_BROKER"));
        assert!(!r.env.contains_key("DH_LAUNCH_PROOF"));
        assert_eq!(r.env.get("ROBUST_AUTH_USERID").map(String::as_str), Some(USER));
        assert_eq!(r.env.get("DOTNET_EnableDiagnostics_IPC").map(String::as_str), Some("0"));
        assert_eq!(launched.game.wait(None), Some(3));
    }

    fn output_is_inherited_unless_redirected() {
        let f = fixture();
        let launched = launch::launch_json(
            &request(
                f,
                json!({"redirectOutput": false, "account": null, "connectAddress": "quiet:exit:4"}),
            ),
            &keyed(f, vec![]),
        )
        .unwrap();
        assert!(launched.stdout.is_none() && launched.stderr.is_none());
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(4));
    }

    fn a_broker_that_cannot_start_does_not_stop_the_game() {
        let f = fixture();
        // No runtime dir, and a temp directory that does not exist: nowhere to put the socket.
        let tmp = tempfile::tempdir().unwrap();
        let result = with_temp_only(&tmp.path().join("missing"), || {
            launch::launch_json(&request(f, json!({"connectAddress": "exit:5"})), &keyed(f, vec![]))
        });

        let mut launched = result.unwrap_or_else(|e| panic!("{e}"));
        assert_eq!(launched.game.broker_state(), launch::BROKER_FAILED);
        let (stdout, _) = output(&mut launched);
        let r = report(&stdout);
        assert!(!r.env.contains_key("DH_LAUNCH_BROKER"));
        // The version 1 proof does not need the broker.
        verify_token(f, r.env.get("DH_LAUNCH_PROOF").unwrap());
        assert_eq!(launched.game.wait(None), Some(5));
    }

    /// Runs `body` with no XDG_RUNTIME_DIR and TMPDIR set to `temp`, then puts both back.
    fn with_temp_only<T>(temp: &Path, body: impl FnOnce() -> T) -> T {
        let saved = (std::env::var_os("XDG_RUNTIME_DIR"), std::env::var_os("TMPDIR"));
        // SAFETY: nothing else in this process reads the environment concurrently (the tests run one at a time).
        unsafe {
            std::env::remove_var("XDG_RUNTIME_DIR");
            std::env::set_var("TMPDIR", temp);
        }
        let result = body();
        unsafe {
            match &saved.0 {
                Some(v) => std::env::set_var("XDG_RUNTIME_DIR", v),
                None => std::env::remove_var("XDG_RUNTIME_DIR"),
            }
            match &saved.1 {
                Some(v) => std::env::set_var("TMPDIR", v),
                None => std::env::remove_var("TMPDIR"),
            }
        }
        result
    }

    fn a_squatted_socket_directory_is_sidestepped() {
        let f = fixture();
        // Another user made the fixed-name directory first; whatever name the broker picks for this user.
        let tmp = tempfile::tempdir().unwrap();
        let squatted: Vec<PathBuf> = [
            std::env::var("USER").unwrap_or_default(),
            unsafe { libc::geteuid() }.to_string(),
        ]
        .iter()
        .map(|name| tmp.path().join(format!("f15-launch-{name}")))
        .collect();
        for d in &squatted {
            fs::create_dir_all(d).unwrap();
            fs::set_permissions(d, fs::Permissions::from_mode(0o755)).unwrap();
        }

        let result = with_temp_only(tmp.path(), || {
            launch::launch_json(&request(f, json!({"connectAddress": "exit:6"})), &keyed(f, vec![]))
        });
        let mut launched = result.unwrap_or_else(|e| panic!("{e}"));
        assert_eq!(launched.game.broker_state(), launch::BROKER_RUNNING);
        let (stdout, _) = output(&mut launched);
        let r = report(&stdout);
        let endpoint = r.env.get("DH_LAUNCH_BROKER").expect("a broker endpoint").clone();
        let socket = PathBuf::from(endpoint.strip_prefix("unix:").unwrap());
        let dir = socket.parent().unwrap().to_path_buf();
        assert!(
            dir.starts_with(tmp.path()) && !squatted.contains(&dir),
            "{}",
            dir.display()
        );
        verify_token(f, &r.token.expect("the loader asked for a proof"));
        assert_eq!(launched.game.wait(Some(Duration::from_secs(10))), Some(6));

        // Its own directory goes with the broker; the squatted ones are left alone.
        assert!(!dir.exists(), "{} outlived the broker", dir.display());
        assert!(squatted.iter().all(|d| d.is_dir()));
    }
}
