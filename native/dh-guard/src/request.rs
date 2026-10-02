//! What the launcher asks the guard to start. Everything in it is untrusted: anyone can load the guard and call it, so
//! the request carries structured facts (paths, addresses, the account) and the guard builds the loader's command line
//! and environment itself; there is no field that passes arguments or variables through as they are.

use serde::Deserialize;
use uuid::Uuid;

/// The one extra cvar a player may set: the CLI's `--net-debug`.
const ALLOWED_EXTRA_CVARS: &[&str] = &["net.logging=true", "net.logging=false"];

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LaunchRequest {
    pub loader_path: String,
    pub engine_path: String,
    pub engine_signature: String,
    pub engine_public_key_path: String,
    pub content_db_path: String,
    pub content_version: i64,
    pub modules: Vec<Module>,
    pub launcher_path: Option<String>,
    pub username: Option<String>,
    pub compat_mode: bool,
    pub connect_address: String,
    pub ss14_address: String,
    pub build: Option<Build>,
    pub extra_cvars: Vec<String>,
    pub account: Option<Account>,
    pub redirect_output: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Module {
    pub name: String,
    pub version: String,
}

/// The server's build info, passed on as `build.*` cvars.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Build {
    pub engine_version: Option<String>,
    pub version: Option<String>,
    pub fork_id: Option<String>,
    pub hash: Option<String>,
    pub manifest_hash: Option<String>,
    pub manifest_url: Option<String>,
    pub manifest_download_url: Option<String>,
    pub download_url: Option<String>,
}

/// Auth material for the game. The launcher sends it only when there is an account and the server's auth mode is not
/// Disabled.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Account {
    pub username: String,
    pub token: String,
    pub user_id: Uuid,
    pub auth_public_key: Option<String>,
}

impl Build {
    /// `(cvar suffix, value)` in the order the loader gets them, as `GameLauncher.Start` passed them.
    pub fn cvars(&self) -> [(&'static str, Option<&str>); 8] {
        [
            ("engine_version", self.engine_version.as_deref()),
            ("version", self.version.as_deref()),
            ("fork_id", self.fork_id.as_deref()),
            ("hash", self.hash.as_deref()),
            ("manifest_hash", self.manifest_hash.as_deref()),
            ("manifest_url", self.manifest_url.as_deref()),
            ("manifest_download_url", self.manifest_download_url.as_deref()),
            ("download_url", self.download_url.as_deref()),
        ]
    }
}

impl LaunchRequest {
    /// Parses and checks a request; the error says what is wrong with it.
    pub fn parse(json: &[u8]) -> Result<LaunchRequest, String> {
        let request: LaunchRequest =
            serde_json::from_slice(json).map_err(|e| format!("the launch request is not valid: {e}"))?;
        request.validate()?;
        Ok(request)
    }

    pub fn validate(&self) -> Result<(), String> {
        for (what, value) in self.strings() {
            if value.contains('\0') {
                return Err(format!("{what} contains a NUL character"));
            }
        }

        // A bare name would be looked up on PATH, and the guard would start something it never looked at.
        if !std::path::Path::new(&self.loader_path).is_absolute() {
            return Err("loaderPath must be an absolute path".to_string());
        }
        if self.content_db_path.is_empty() {
            return Err("contentDbPath is empty".to_string());
        }

        for cvar in &self.extra_cvars {
            if !ALLOWED_EXTRA_CVARS.contains(&cvar.as_str()) {
                return Err(format!("extra cvar {cvar:?} is not allowed"));
            }
        }

        for module in &self.modules {
            for (what, value) in [("name", &module.name), ("version", &module.version)] {
                if !is_module_component(value) {
                    return Err(format!("module {what} {value:?} is not a plain name"));
                }
            }
        }
        Ok(())
    }

    /// Every string in the request, named, for the checks that apply to all of them.
    fn strings(&self) -> Vec<(&'static str, &str)> {
        let mut all = vec![
            ("loaderPath", self.loader_path.as_str()),
            ("enginePath", &self.engine_path),
            ("engineSignature", &self.engine_signature),
            ("enginePublicKeyPath", &self.engine_public_key_path),
            ("contentDbPath", &self.content_db_path),
            ("connectAddress", &self.connect_address),
            ("ss14Address", &self.ss14_address),
        ];
        if let Some(v) = &self.launcher_path {
            all.push(("launcherPath", v));
        }
        if let Some(v) = &self.username {
            all.push(("username", v));
        }
        for module in &self.modules {
            all.push(("modules.name", &module.name));
            all.push(("modules.version", &module.version));
        }
        if let Some(build) = &self.build {
            for (_, value) in build.cvars() {
                if let Some(v) = value {
                    all.push(("build", v));
                }
            }
        }
        for cvar in &self.extra_cvars {
            all.push(("extraCvars", cvar));
        }
        if let Some(account) = &self.account {
            all.push(("account.username", &account.username));
            all.push(("account.token", &account.token));
            if let Some(v) = &account.auth_public_key {
                all.push(("account.authPublicKey", v));
            }
        }
        all
    }
}

/// `^[A-Za-z0-9._-]+$` and not `.` or `..`: it becomes a directory name under `modules/`, and must not climb out.
fn is_module_component(value: &str) -> bool {
    !value.is_empty()
        && value != "."
        && value != ".."
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    pub(crate) fn sample() -> serde_json::Value {
        json!({
            "loaderPath": "/opt/dh/loader/DarkHaven.Loader",
            "enginePath": "/data/engines/1.0.0.zip",
            "engineSignature": "sha256:00",
            "enginePublicKeyPath": "/opt/dh/signing_key",
            "contentDbPath": "/data/content.db",
            "contentVersion": 42,
            "modules": [{"name": "Robust.Client.WebView", "version": "1.2.3"}],
            "launcherPath": null,
            "username": "Player",
            "compatMode": false,
            "connectAddress": "udp://127.0.0.1:1212/",
            "ss14Address": "ss14://127.0.0.1/",
            "build": null,
            "extraCvars": [],
            "account": null,
            "redirectOutput": false
        })
    }

    fn parse(value: &serde_json::Value) -> Result<LaunchRequest, String> {
        LaunchRequest::parse(value.to_string().as_bytes())
    }

    #[test]
    fn a_full_request_parses() {
        let mut v = sample();
        v["build"] = json!({"engineVersion": "1.0.0", "version": null, "forkId": "f", "hash": null,
            "manifestHash": null, "manifestUrl": null, "manifestDownloadUrl": null, "downloadUrl": null});
        v["account"] = json!({"username": "u", "token": "t", "userId": "11111111-2222-3333-4444-555555555555",
            "authPublicKey": null});
        v["extraCvars"] = json!(["net.logging=true"]);
        let r = parse(&v).unwrap();
        assert_eq!(r.content_version, 42);
        assert_eq!(
            r.account.unwrap().user_id.to_string(),
            "11111111-2222-3333-4444-555555555555"
        );
        assert_eq!(r.build.unwrap().fork_id.as_deref(), Some("f"));
    }

    #[test]
    fn unknown_fields_are_rejected() {
        let mut v = sample();
        v["arguments"] = json!(["--evil"]);
        assert!(parse(&v).is_err());

        let mut v = sample();
        v["modules"] = json!([{"name": "a", "version": "1", "path": "/x"}]);
        assert!(parse(&v).is_err());
    }

    #[test]
    fn required_fields_are_required() {
        let mut v = sample();
        v.as_object_mut().unwrap().remove("connectAddress");
        assert!(parse(&v).is_err());
    }

    #[test]
    fn only_the_net_logging_cvar_is_allowed() {
        for ok in ["net.logging=true", "net.logging=false"] {
            let mut v = sample();
            v["extraCvars"] = json!([ok]);
            assert!(parse(&v).is_ok(), "{ok}");
        }
        for bad in ["net.logging=1", "sys.x=1", "net.logging=true ", "NET.LOGGING=true"] {
            let mut v = sample();
            v["extraCvars"] = json!([bad]);
            assert!(parse(&v).is_err(), "{bad}");
        }
    }

    #[test]
    fn module_names_must_be_plain() {
        for bad in ["..", ".", "a/b", "a\\b", "", "a b", "é"] {
            let mut v = sample();
            v["modules"] = json!([{"name": bad, "version": "1"}]);
            assert!(parse(&v).is_err(), "name {bad:?}");
            let mut v = sample();
            v["modules"] = json!([{"name": "a", "version": bad}]);
            assert!(parse(&v).is_err(), "version {bad:?}");
        }
        let mut v = sample();
        v["modules"] = json!([{"name": "Robust.Client-Web_View", "version": "1.2.3-rc.1"}]);
        assert!(parse(&v).is_ok());
    }

    #[test]
    fn nul_is_rejected_anywhere() {
        let mut v = sample();
        v["username"] = json!("a\u{0}b");
        assert!(parse(&v).is_err());
        let mut v = sample();
        v["account"] = json!({"username": "u", "token": "t\u{0}", "userId": "11111111-2222-3333-4444-555555555555",
            "authPublicKey": null});
        assert!(parse(&v).is_err());
    }

    #[test]
    fn the_loader_path_must_be_absolute() {
        let mut v = sample();
        v["loaderPath"] = json!("DarkHaven.Loader");
        assert!(parse(&v).is_err());
    }
}
