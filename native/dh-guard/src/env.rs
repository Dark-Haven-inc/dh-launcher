//! The game's environment: the launcher's own, minus everything that would load code into the game or change what it
//! loads, with the runtime's doors for attaching to it later closed.
//!
//! The broker vouches for the process the guard starts, so that process has to run what we ship. It inherits the
//! player's environment, and a handful of variables would let anyone slip a cheat into a genuine game: a .NET startup
//! hook or profiler, a zip mounted over the content, the content sandbox switched off, an engine module read from a
//! directory of their choosing. None of them has a use for a player. Everything else is passed on untouched: graphics,
//! locale, overlay and performance tools included. A port of `GameEnvironment.cs`.

use std::ffi::{OsStr, OsString};

/// Removed in any letter case.
const NAMES: &[&str] = &[
    // .NET host: code run at startup, extra or replaced assemblies.
    "DOTNET_STARTUP_HOOKS",
    "DOTNET_ADDITIONAL_DEPS",
    "DOTNET_SHARED_STORE",
    // Loader and engine: a zip over the content, an unsigned engine, no content sandbox.
    "SS14_LOADER_OVERLAY_ZIP",
    "SS14_DISABLE_SIGNING",
    "ROBUST_DISABLE_SANDBOX",
];

/// Every variable starting with one of these is removed, in any letter case.
const PREFIXES: &[&str] = &[
    // Profilers: native code loaded into the runtime.
    "CORECLR_",
    "COR_ENABLE_PROFILING",
    "COR_PROFILER",
    // Engine modules loaded from elsewhere. The guard sets the ones the server asks for.
    "ROBUST_MODULE_",
];

/// Runtime settings that load a native library into the runtime or open the process to diagnostic tools. The runtime
/// reads each under `DOTNET_` and `COMPlus_`; both are removed, as prefixes.
const RUNTIME_SETTINGS: &[&str] = &[
    "EnableDiagnostics",
    "DiagnosticPorts",
    "DefaultDiagnosticPortSuspend",
    "GCName",
    "GCPath",
    "AltJit",
];

const RUNTIME_PREFIXES: &[&str] = &["DOTNET_", "COMPlus_"];

fn starts_with_ignore_case(name: &[u8], prefix: &str) -> bool {
    name.len() >= prefix.len() && name[..prefix.len()].eq_ignore_ascii_case(prefix.as_bytes())
}

/// Whether `name` is a variable that must not reach the game.
pub fn is_injecting(name: &OsStr) -> bool {
    // Every name we look for is ASCII, so comparing the raw bytes (WTF-8 on Windows) case-insensitively is exact,
    // and a name that is not valid Unicode is still caught by its prefix.
    let name = name.as_encoded_bytes();

    if NAMES
        .iter()
        .any(|candidate| name.eq_ignore_ascii_case(candidate.as_bytes()))
    {
        return true;
    }
    if PREFIXES.iter().any(|prefix| starts_with_ignore_case(name, prefix)) {
        return true;
    }
    RUNTIME_PREFIXES.iter().any(|prefix| {
        starts_with_ignore_case(name, prefix)
            && RUNTIME_SETTINGS
                .iter()
                .any(|setting| starts_with_ignore_case(&name[prefix.len()..], setting))
    })
}

/// Environment variables with the platform's idea of a name: case-insensitive on Windows, exact elsewhere (as .NET's
/// `ProcessStartInfo.Environment` treats them).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct EnvBlock {
    vars: Vec<(OsString, OsString)>,
}

impl EnvBlock {
    /// The launcher's own environment.
    pub fn inherited() -> EnvBlock {
        let mut block = EnvBlock::default();
        for (name, value) in std::env::vars_os() {
            // Windows' hidden per-drive "=C:" entries; .NET leaves them out too.
            if name.as_encoded_bytes().first() == Some(&b'=') {
                continue;
            }
            // First one wins, as when .NET reads a duplicated name.
            if block.get(&name).is_none() {
                block.vars.push((name, value));
            }
        }
        block
    }

    pub fn from_pairs<K: Into<OsString>, V: Into<OsString>>(pairs: impl IntoIterator<Item = (K, V)>) -> EnvBlock {
        let mut block = EnvBlock::default();
        for (name, value) in pairs {
            block.set(name, value);
        }
        block
    }

    fn same_name(a: &OsStr, b: &OsStr) -> bool {
        if cfg!(windows) {
            a.as_encoded_bytes().eq_ignore_ascii_case(b.as_encoded_bytes())
        } else {
            a == b
        }
    }

    pub fn get(&self, name: impl AsRef<OsStr>) -> Option<&OsStr> {
        let name = name.as_ref();
        self.vars
            .iter()
            .find(|(n, _)| Self::same_name(n, name))
            .map(|(_, v)| v.as_os_str())
    }

    pub fn set(&mut self, name: impl Into<OsString>, value: impl Into<OsString>) {
        let name = name.into();
        let value = value.into();
        match self.vars.iter_mut().find(|(n, _)| Self::same_name(n, &name)) {
            Some(entry) => entry.1 = value,
            None => self.vars.push((name, value)),
        }
    }

    pub fn remove(&mut self, name: impl AsRef<OsStr>) {
        let name = name.as_ref();
        self.vars.retain(|(n, _)| !Self::same_name(n, name));
    }

    pub fn retain(&mut self, mut keep: impl FnMut(&OsStr) -> bool) {
        self.vars.retain(|(n, _)| keep(n));
    }

    pub fn iter(&self) -> impl Iterator<Item = (&OsStr, &OsStr)> {
        self.vars.iter().map(|(n, v)| (n.as_os_str(), v.as_os_str()))
    }

    pub fn len(&self) -> usize {
        self.vars.len()
    }

    pub fn is_empty(&self) -> bool {
        self.vars.is_empty()
    }
}

/// Removes the injecting variables and turns off the runtime's debugger, profiler and diagnostic IPC for the game, so
/// nothing attaches to it once it runs either.
pub fn harden(env: &mut EnvBlock) {
    env.retain(|name| !is_injecting(name));
    env.set("DOTNET_EnableDiagnostics_IPC", "0");
    env.set("DOTNET_EnableDiagnostics_Debugger", "0");
    env.set("DOTNET_EnableDiagnostics_Profiler", "0");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

    #[test]
    fn injecting_variables_are_recognised() {
        for name in [
            "DOTNET_STARTUP_HOOKS",
            "dotnet_startup_hooks",
            "DOTNET_ADDITIONAL_DEPS",
            "DOTNET_SHARED_STORE",
            "SS14_LOADER_OVERLAY_ZIP",
            "SS14_DISABLE_SIGNING",
            "ROBUST_DISABLE_SANDBOX",
            "ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW",
            "CORECLR_ENABLE_PROFILING",
            "CORECLR_PROFILER_PATH_64",
            "CORECLR_NOTIFICATION_PROFILERS",
            "COR_PROFILER",
            "DOTNET_EnableDiagnostics",
            "DOTNET_EnableDiagnostics_IPC",
            "COMPlus_EnableDiagnostics",
            "DOTNET_DiagnosticPorts",
            "COMPlus_DefaultDiagnosticPortSuspend",
            "DOTNET_GCName",
            "DOTNET_GCPath",
            "COMPlus_AltJitName",
        ] {
            assert!(is_injecting(OsStr::new(name)), "{name}");
        }
    }

    #[test]
    fn everything_else_is_passed_on() {
        for name in [
            "PATH",
            "LD_PRELOAD",
            "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT",
            "DOTNET_gcServer",
            "DOTNET_GCHeapHardLimit",
            "ROBUST_INTEGRATED_GPU",
            "ROBUST_CVARS",
            "SS14_LOADER_CONTENT_POOL_SIZE",
            "DOTNETX_STARTUP_HOOKS",
        ] {
            assert!(!is_injecting(OsStr::new(name)), "{name}");
        }
    }

    #[cfg(unix)]
    #[test]
    fn a_name_that_is_not_unicode_is_still_caught_by_its_prefix() {
        use std::os::unix::ffi::OsStrExt;
        assert!(is_injecting(OsStr::from_bytes(b"CORECLR_\xff")));
        assert!(is_injecting(OsStr::from_bytes(b"dotnet_gcpath\xfe")));
        assert!(!is_injecting(OsStr::from_bytes(b"PATH\xff")));
    }

    #[test]
    fn harden_strips_injection_and_closes_diagnostics() {
        let mut env = EnvBlock::from_pairs([
            ("PATH", "/usr/bin"),
            ("DOTNET_STARTUP_HOOKS", "/tmp/hook.dll"),
            ("CORECLR_ENABLE_PROFILING", "1"),
            ("CORECLR_PROFILER_PATH", "/tmp/profiler.so"),
            ("SS14_LOADER_OVERLAY_ZIP", "/tmp/overlay.zip"),
            ("ROBUST_DISABLE_SANDBOX", "1"),
            ("DOTNET_EnableDiagnostics_IPC", "1"),
            ("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1"),
        ]);

        harden(&mut env);

        let got: BTreeMap<String, String> = env
            .iter()
            .map(|(n, v)| (n.to_string_lossy().into_owned(), v.to_string_lossy().into_owned()))
            .collect();
        let expected: BTreeMap<String, String> = [
            ("PATH", "/usr/bin"),
            ("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1"),
            ("DOTNET_EnableDiagnostics_IPC", "0"),
            ("DOTNET_EnableDiagnostics_Debugger", "0"),
            ("DOTNET_EnableDiagnostics_Profiler", "0"),
        ]
        .into_iter()
        .map(|(n, v)| (n.to_string(), v.to_string()))
        .collect();
        assert_eq!(got, expected);
    }

    #[test]
    fn setting_replaces_and_removing_removes() {
        let mut env = EnvBlock::from_pairs([("A", "1"), ("B", "2")]);
        env.set("A", "3");
        env.remove("B");
        assert_eq!(env.get("A"), Some(OsStr::new("3")));
        assert_eq!(env.get("B"), None);
        assert_eq!(env.len(), 1);
        // Names differ by case only on platforms where the OS says so.
        env.set("a", "4");
        assert_eq!(env.len(), if cfg!(windows) { 1 } else { 2 });
    }
}
