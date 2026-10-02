//! What a keyed guard checks before it starts anything: that the loader is the one this release shipped, file for
//! file, and that the engine it is told to load is one we or the SS14 maintainers signed. The proofs it later signs
//! vouch for exactly this process, so it must not start whatever a caller points it at.
//!
//! The checks are as good as the moment they run: the loader reads the files again afterwards, and a same-user
//! attacker could swap them in between (see docs/GUARD.md).

use crate::build_inputs::{IGNORED_LOADER_FILES, decode_hex, encode_hex};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

/// The loader's file name; a keyed guard starts nothing else.
pub const LOADER_FILE_NAME: &str = if cfg!(windows) {
    "DarkHaven.Loader.exe"
} else {
    "DarkHaven.Loader"
};

/// One file the loader directory must hold, relative to it with '/' separators.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LoaderPin {
    pub path: String,
    pub sha256: [u8; 32],
}

/// The engines a keyed guard lets the loader run.
#[derive(Clone, Debug)]
pub struct EngineTrust {
    /// Bundled engines, vouched for by content hash (`sha256:<hex>`).
    pub bundled_sha256: Vec<[u8; 32]>,
    /// The SS14 engine signing key every other engine must be signed with.
    pub ed25519_key: [u8; 32],
}

/// Paths compare case-insensitively where the file system does.
const FOLD_CASE: bool = cfg!(windows);

/// Checks `loader_path` is the pinned loader, in a directory that holds the pinned files and nothing else.
pub fn verify_loader(loader_path: &Path, pins: &[LoaderPin]) -> Result<(), String> {
    verify_loader_with(loader_path, pins, FOLD_CASE)
}

/// [`verify_loader`] with the case rule spelled out, so both can be tested anywhere.
pub fn verify_loader_with(loader_path: &Path, pins: &[LoaderPin], fold_case: bool) -> Result<(), String> {
    let key = |s: &str| if fold_case { s.to_lowercase() } else { s.to_string() };

    let name = loader_path
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or("the loader path has no file name")?;
    if key(name) != key(LOADER_FILE_NAME) {
        return Err(format!("the loader must be {LOADER_FILE_NAME}, not {name}"));
    }
    let dir = loader_path.parent().ok_or("the loader path has no directory")?;
    if pins.is_empty() {
        return Err("this build pins no loader files".to_string());
    }

    let mut found = Vec::new();
    collect_files(dir, "", &mut found)?;

    let mut seen = vec![false; pins.len()];
    for (relative, path) in &found {
        match pins.iter().position(|pin| key(&pin.path) == key(relative)) {
            Some(index) => {
                seen[index] = true;
                let actual = sha256_file(path)?;
                if actual != pins[index].sha256 {
                    return Err(format!("loader file {relative} has been modified"));
                }
            }
            None => {
                let file_name = relative.rsplit('/').next().unwrap_or(relative);
                if !IGNORED_LOADER_FILES
                    .iter()
                    .any(|ignored| key(ignored) == key(file_name))
                {
                    return Err(format!("unexpected file in the loader directory: {relative}"));
                }
            }
        }
    }

    if let Some(missing) = pins.iter().zip(&seen).find(|(_, seen)| !**seen) {
        return Err(format!("loader file {} is missing", missing.0.path));
    }
    Ok(())
}

/// Every regular file under `dir`, with its '/'-separated path relative to the loader directory. Links (and on
/// Windows junctions and other name-surrogate reparse points) and anything that is neither file nor directory are
/// refused outright: they would let the directory's contents live somewhere the pins do not describe.
fn collect_files(dir: &Path, prefix: &str, out: &mut Vec<(String, PathBuf)>) -> Result<(), String> {
    let entries = fs::read_dir(dir).map_err(|e| format!("cannot list {}: {e}", dir.display()))?;
    for entry in entries {
        let entry = entry.map_err(|e| format!("cannot list {}: {e}", dir.display()))?;
        let name = entry.file_name();
        let name = name
            .to_str()
            .ok_or_else(|| format!("a file name in {} is not valid Unicode", dir.display()))?;
        let relative = if prefix.is_empty() {
            name.to_string()
        } else {
            format!("{prefix}/{name}")
        };

        let path = entry.path();
        let kind = fs::symlink_metadata(&path)
            .map_err(|e| format!("cannot stat {relative}: {e}"))?
            .file_type();
        if kind.is_symlink() {
            return Err(format!("{relative} in the loader directory is a link"));
        } else if kind.is_dir() {
            collect_files(&path, &relative, out)?;
        } else if kind.is_file() {
            out.push((relative, path));
        } else {
            return Err(format!("{relative} in the loader directory is not a regular file"));
        }
    }
    Ok(())
}

/// Checks the engine the loader will run: `sha256:<hex>` must be a bundled engine's hash and the file's; anything else
/// is a hex Ed25519 signature over the zip that must hold under the SS14 key. The key file the loader will check with
/// must be that same key.
pub fn verify_engine(
    engine_path: &Path,
    signature: &str,
    public_key_path: &Path,
    trust: &EngineTrust,
) -> Result<(), String> {
    let key_text = fs::read_to_string(public_key_path)
        .map_err(|e| format!("cannot read the engine signing key {}: {e}", public_key_path.display()))?;
    let key = crate::build_inputs::parse_ed25519_pem(&key_text).map_err(|e| format!("engine signing key: {e}"))?;
    if key != trust.ed25519_key {
        return Err("the engine signing key file is not the SS14 key".to_string());
    }

    // The loader checks the prefix case-insensitively; so does this, so both take the same branch.
    let prefix = signature.get(..7);
    if prefix.is_some_and(|p| p.eq_ignore_ascii_case("sha256:")) {
        let expected: [u8; 32] = decode_hex(&signature[7..])
            .and_then(|b| b.try_into().ok())
            .ok_or("the engine hash is not 64 hex digits")?;
        if !trust.bundled_sha256.contains(&expected) {
            return Err(format!(
                "engine sha256:{} is not a bundled engine",
                encode_hex(&expected)
            ));
        }
        if sha256_file(engine_path)? != expected {
            return Err("the engine does not match its bundled hash".to_string());
        }
        return Ok(());
    }

    let signature: [u8; 64] = decode_hex(signature)
        .and_then(|b| b.try_into().ok())
        .ok_or("the engine signature is not 128 hex digits")?;
    let key = ed25519_dalek::VerifyingKey::from_bytes(&trust.ed25519_key)
        .map_err(|_| "the built-in engine signing key is invalid".to_string())?;
    let engine = fs::read(engine_path).map_err(|e| format!("cannot read the engine {}: {e}", engine_path.display()))?;
    key.verify_strict(&engine, &ed25519_dalek::Signature::from_bytes(&signature))
        .map_err(|_| "the engine's signature does not hold".to_string())
}

fn sha256_file(path: &Path) -> Result<[u8; 32], String> {
    let mut file = fs::File::open(path).map_err(|e| format!("cannot open {}: {e}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; 1 << 16];
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|e| format!("cannot read {}: {e}", path.display()))?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finalize().into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::build_inputs::sha256;
    use ed25519_dalek::Signer as _;

    struct Loader {
        dir: tempfile::TempDir,
        pins: Vec<LoaderPin>,
    }

    impl Loader {
        fn new() -> Loader {
            let dir = tempfile::tempdir().unwrap();
            let files: [(&str, &[u8]); 3] = [
                (LOADER_FILE_NAME, b"loader"),
                ("DarkHaven.Loader.dll", b"managed"),
                ("runtimes/linux-x64/native/libsodium.so", b"sodium"),
            ];
            let mut pins = Vec::new();
            for (path, contents) in files {
                let full = dir.path().join(path);
                fs::create_dir_all(full.parent().unwrap()).unwrap();
                fs::write(&full, contents).unwrap();
                pins.push(LoaderPin {
                    path: path.to_string(),
                    sha256: sha256(contents),
                });
            }
            Loader { dir, pins }
        }

        fn exe(&self) -> PathBuf {
            self.dir.path().join(LOADER_FILE_NAME)
        }

        fn check(&self) -> Result<(), String> {
            verify_loader_with(&self.exe(), &self.pins, false)
        }
    }

    #[test]
    fn a_matching_loader_passes() {
        Loader::new().check().unwrap();
    }

    #[test]
    fn a_missing_file_fails() {
        let loader = Loader::new();
        fs::remove_file(loader.dir.path().join("DarkHaven.Loader.dll")).unwrap();
        assert!(loader.check().unwrap_err().contains("missing"));
    }

    #[test]
    fn an_extra_file_fails() {
        let loader = Loader::new();
        fs::write(loader.dir.path().join("runtimes/cheat.dll"), b"x").unwrap();
        assert!(loader.check().unwrap_err().contains("unexpected"));
    }

    #[test]
    fn ignored_files_and_empty_directories_pass() {
        let loader = Loader::new();
        fs::write(loader.dir.path().join(".complete"), b"").unwrap();
        fs::write(loader.dir.path().join("runtimes/Thumbs.db"), b"x").unwrap();
        fs::write(loader.dir.path().join(".DS_Store"), b"x").unwrap();
        fs::write(loader.dir.path().join("desktop.ini"), b"x").unwrap();
        fs::create_dir(loader.dir.path().join("empty")).unwrap();
        loader.check().unwrap();
    }

    #[test]
    fn a_modified_file_fails() {
        let loader = Loader::new();
        fs::write(loader.dir.path().join("DarkHaven.Loader.dll"), b"patched").unwrap();
        assert!(loader.check().unwrap_err().contains("modified"));
    }

    #[cfg(unix)]
    #[test]
    fn links_are_refused() {
        let loader = Loader::new();
        let real = loader.dir.path().join("DarkHaven.Loader.dll");
        let elsewhere = tempfile::tempdir().unwrap();
        let moved = elsewhere.path().join("DarkHaven.Loader.dll");
        fs::rename(&real, &moved).unwrap();
        std::os::unix::fs::symlink(&moved, &real).unwrap();
        assert!(loader.check().unwrap_err().contains("link"));

        // A linked directory too, even with the right files behind it.
        let loader = Loader::new();
        let runtimes = loader.dir.path().join("runtimes");
        let moved = elsewhere.path().join("runtimes");
        fs::rename(&runtimes, &moved).unwrap();
        std::os::unix::fs::symlink(&moved, &runtimes).unwrap();
        assert!(loader.check().unwrap_err().contains("link"));
    }

    #[test]
    fn the_wrong_executable_name_fails() {
        let loader = Loader::new();
        let other = loader.dir.path().join("Other.Loader");
        assert!(
            verify_loader_with(&other, &loader.pins, false)
                .unwrap_err()
                .contains("must be")
        );
        // Case matters only where the file system ignores it.
        let upper = loader.dir.path().join(LOADER_FILE_NAME.to_uppercase());
        assert!(verify_loader_with(&upper, &loader.pins, false).is_err());
    }

    #[test]
    fn case_folding_matches_differently_cased_names() {
        let loader = Loader::new();
        let pins: Vec<LoaderPin> = loader
            .pins
            .iter()
            .map(|p| LoaderPin {
                path: p.path.to_uppercase(),
                sha256: p.sha256,
            })
            .collect();
        assert!(verify_loader_with(&loader.exe(), &pins, false).is_err());
        verify_loader_with(&loader.exe(), &pins, true).unwrap();
    }

    struct Engine {
        dir: tempfile::TempDir,
        signing: ed25519_dalek::SigningKey,
        zip: PathBuf,
        key_file: PathBuf,
    }

    fn pem(key: &[u8; 32]) -> String {
        let mut der = vec![0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];
        der.extend_from_slice(key);
        format!(
            "-----BEGIN PUBLIC KEY-----\n{}\n-----END PUBLIC KEY-----\n",
            crate::proof::to_base64(&der)
        )
    }

    impl Engine {
        fn new() -> Engine {
            let dir = tempfile::tempdir().unwrap();
            let signing = ed25519_dalek::SigningKey::from_bytes(&sha256(b"engine test key"));
            let zip = dir.path().join("engine.zip");
            fs::write(&zip, b"PK engine bytes").unwrap();
            let key_file = dir.path().join("signing_key");
            fs::write(&key_file, pem(&signing.verifying_key().to_bytes())).unwrap();
            Engine {
                dir,
                signing,
                zip,
                key_file,
            }
        }

        fn trust(&self, bundled: Vec<[u8; 32]>) -> EngineTrust {
            EngineTrust {
                bundled_sha256: bundled,
                ed25519_key: self.signing.verifying_key().to_bytes(),
            }
        }

        fn signature(&self) -> String {
            encode_hex(&self.signing.sign(&fs::read(&self.zip).unwrap()).to_bytes())
        }
    }

    #[test]
    fn a_bundled_engine_passes_by_hash() {
        let e = Engine::new();
        let hash = sha256(b"PK engine bytes");
        let trust = e.trust(vec![sha256(b"other"), hash]);
        verify_engine(&e.zip, &format!("sha256:{}", encode_hex(&hash)), &e.key_file, &trust).unwrap();
        // Either case, prefix included, as the loader reads it.
        let upper = format!("SHA256:{}", encode_hex(&hash).to_uppercase());
        verify_engine(&e.zip, &upper, &e.key_file, &trust).unwrap();
    }

    #[test]
    fn a_hash_not_in_the_manifest_fails() {
        let e = Engine::new();
        let hash = sha256(b"PK engine bytes");
        let trust = e.trust(vec![sha256(b"other")]);
        let err = verify_engine(&e.zip, &format!("sha256:{}", encode_hex(&hash)), &e.key_file, &trust).unwrap_err();
        assert!(err.contains("not a bundled engine"), "{err}");
    }

    #[test]
    fn a_listed_hash_the_file_does_not_have_fails() {
        let e = Engine::new();
        let listed = sha256(b"the real engine");
        let trust = e.trust(vec![listed]);
        let err = verify_engine(&e.zip, &format!("sha256:{}", encode_hex(&listed)), &e.key_file, &trust).unwrap_err();
        assert!(err.contains("does not match"), "{err}");
    }

    #[test]
    fn a_signed_engine_passes() {
        let e = Engine::new();
        verify_engine(&e.zip, &e.signature(), &e.key_file, &e.trust(vec![])).unwrap();
        verify_engine(&e.zip, &e.signature().to_uppercase(), &e.key_file, &e.trust(vec![])).unwrap();
    }

    #[test]
    fn a_bad_signature_fails() {
        let e = Engine::new();
        let signature = e.signature();
        fs::write(&e.zip, b"PK patched engine").unwrap();
        assert!(verify_engine(&e.zip, &signature, &e.key_file, &e.trust(vec![])).is_err());
        assert!(verify_engine(&e.zip, "abcd", &e.key_file, &e.trust(vec![])).is_err());
        assert!(verify_engine(&e.zip, "", &e.key_file, &e.trust(vec![])).is_err());
    }

    #[test]
    fn a_signature_by_another_key_fails() {
        let e = Engine::new();
        let other = ed25519_dalek::SigningKey::from_bytes(&sha256(b"not the SS14 key"));
        let signature = encode_hex(&other.sign(&fs::read(&e.zip).unwrap()).to_bytes());
        assert!(verify_engine(&e.zip, &signature, &e.key_file, &e.trust(vec![])).is_err());
    }

    #[test]
    fn a_swapped_key_file_fails() {
        let e = Engine::new();
        let other = ed25519_dalek::SigningKey::from_bytes(&sha256(b"attacker key"));
        let swapped = e.dir.path().join("attacker_key");
        fs::write(&swapped, pem(&other.verifying_key().to_bytes())).unwrap();
        let signature = encode_hex(&other.sign(&fs::read(&e.zip).unwrap()).to_bytes());
        let err = verify_engine(&e.zip, &signature, &swapped, &e.trust(vec![])).unwrap_err();
        assert!(err.contains("not the SS14 key"), "{err}");

        // The key file is checked on the bundled path too.
        let hash = sha256(b"PK engine bytes");
        let err = verify_engine(
            &e.zip,
            &format!("sha256:{}", encode_hex(&hash)),
            &swapped,
            &e.trust(vec![hash]),
        )
        .unwrap_err();
        assert!(err.contains("not the SS14 key"), "{err}");
    }

    #[test]
    fn the_shipped_signing_key_parses() {
        let text = fs::read_to_string(
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../../src/DarkHaven.Launcher/Assets/signing_key"),
        )
        .unwrap();
        let key = crate::build_inputs::parse_ed25519_pem(&text).unwrap();
        assert!(ed25519_dalek::VerifyingKey::from_bytes(&key).is_ok());
    }
}
