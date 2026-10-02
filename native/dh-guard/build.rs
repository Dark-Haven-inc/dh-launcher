//! Bakes the build's trust anchors into the guard.
//!
//! Without `DH_LAUNCH_SIGNING_KEY` this is a development build: no key, nothing pinned, the guard only launches.
//! With it, the key is split into masked shares (never written out whole), and the build also needs the loader pins
//! (`DH_GUARD_LOADER_PINS`), the bundled-engine hashes and the SS14 engine signing key, since a guard that signs for
//! whatever it is told to start would vouch for anything.

#[path = "src/build_inputs.rs"]
mod build_inputs;

use std::env;
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};

const KEY_VAR: &str = "DH_LAUNCH_SIGNING_KEY";
const PINS_VAR: &str = "DH_GUARD_LOADER_PINS";
const VERSION_VAR: &str = "DH_LAUNCHER_VERSION";

fn main() {
    for var in [KEY_VAR, PINS_VAR, VERSION_VAR] {
        println!("cargo:rerun-if-env-changed={var}");
    }
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=src/build_inputs.rs");

    let out_dir = PathBuf::from(env::var_os("OUT_DIR").expect("cargo sets OUT_DIR"));
    let manifest_dir = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("cargo sets CARGO_MANIFEST_DIR"));
    let repo = manifest_dir.join("..").join("..");
    let windows = env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "windows");

    write(&out_dir.join("version.rs"), &version_source(&launcher_version()));

    let sealed = env::var(KEY_VAR).unwrap_or_default();
    if sealed.trim().is_empty() {
        write(&out_dir.join("key_shares.rs"), &shares_source(None));
        write(&out_dir.join("trust.rs"), &trust_source(&[], &[], None));
        return;
    }

    // A keyed guard must know what it is allowed to start.
    let pins_path = env::var_os(PINS_VAR).filter(|p| !p.is_empty()).unwrap_or_else(|| {
        fail(&format!(
            "{KEY_VAR} is set but {PINS_VAR} is not: a keyed build must pin the loader it launches"
        ))
    });
    let pins_path = PathBuf::from(pins_path);
    // cargo runs this script in the crate's directory, not where the build was started, so a relative path would
    // quietly mean native/dh-guard/<path>. (DhGuard.targets makes it absolute; a plain cargo build must pass one.)
    if !pins_path.is_absolute() {
        fail(&format!(
            "{PINS_VAR} must be an absolute path, not {} (cargo runs the build script in {})",
            pins_path.display(),
            manifest_dir.display()
        ));
    }
    let pins = build_inputs::parse_pins(&read(&pins_path), windows)
        .unwrap_or_else(|e| fail(&format!("{PINS_VAR} ({}): {e}", pins_path.display())));

    let manifest_path = repo.join("src/DarkHaven.App/bundled-engines/manifest.json");
    let engine_hashes = build_inputs::bundled_engine_hashes(&read(&manifest_path))
        .unwrap_or_else(|e| fail(&format!("{}: {e}", manifest_path.display())));

    let key_path = repo.join("src/DarkHaven.Launcher/Assets/signing_key");
    let engine_key = build_inputs::parse_ed25519_pem(&read(&key_path))
        .unwrap_or_else(|e| fail(&format!("{}: {e}", key_path.display())));

    // Never echo the secret, even in an error.
    let d = build_inputs::unseal_scalar(&sealed).unwrap_or_else(|e| fail(&format!("{KEY_VAR}: {e}")));
    let layout = build_inputs::plan(&d);

    write(&out_dir.join("key_shares.rs"), &shares_source(Some((&d, &layout))));
    write(
        &out_dir.join("trust.rs"),
        &trust_source(&pins, &engine_hashes, Some(&engine_key)),
    );
}

/// The version the proofs state. Like `LauncherInfo.Version`, without a `+<commit>` suffix.
fn launcher_version() -> String {
    let raw = env::var(VERSION_VAR).unwrap_or_default();
    let version = raw.split('+').next().unwrap_or_default().trim();
    let version = if version.is_empty() { "0.0.0-dev" } else { version };
    // It is the last line of a signed payload and a NUL-terminated field of DhGuardInfo.
    if version.len() > 63 || version.chars().any(char::is_control) {
        fail(&format!(
            "{VERSION_VAR} must be at most 63 bytes without control characters"
        ));
    }
    version.to_string()
}

fn version_source(version: &str) -> String {
    format!(
        "/// The launcher version this build signs into its proofs.\npub(crate) const LAUNCHER_VERSION: &str = {version:?};\n"
    )
}

/// The generated module that holds the key. A development build (`None`) has no shares and no statics.
///
/// A keyed build stores each share's masked value and the per-build salt (XOR-split) as separate byte statics, in a
/// key-shuffled order, with random-length filler statics between them, and no mask anywhere: `recover_shares` derives
/// each mask at run time from the salt and the share's index. So a tool cannot scan the binary for adjacent
/// share/mask pairs, or for any fixed offset/stride pairing of stored bytes: it has to reverse-engineer this build's
/// generated code. Every static is `#[used]` and read through `read_window`/`touch`, so nothing is folded or dropped.
fn shares_source(keyed: Option<(&[u8; 32], &build_inputs::KeyLayout)>) -> String {
    let Some((d, layout)) = keyed else {
        return String::from(
            "// @generated by build.rs: a development build holds no signing key.\n\
             pub(crate) const SHARE_COUNT: usize = 0;\n\
             pub(crate) fn recover_shares() -> Vec<[u8; 32]> {\n    Vec::new()\n}\n",
        );
    };

    // A blob is one emitted static; the recovery reads a 32-byte window from it (a share's stored value or an
    // XOR-piece of the salt) or just touches it (filler).
    enum Role {
        Share(usize),
        Salt(usize),
        Filler,
    }
    struct Blob {
        role: Role,
        bytes: Vec<u8>,
        /// Where the meaningful 32 bytes sit inside `bytes` (0 for filler).
        offset: usize,
    }

    let mut placement = build_inputs::SeedStream::labeled(d, build_inputs::PLACEMENT_LABEL);
    let byte = |p: &mut build_inputs::SeedStream| p.next_byte();
    // A static of `payload.len() + slack` random bytes with `payload` planted at a per-static offset.
    let framed = |p: &mut build_inputs::SeedStream, role: Role, payload: &[u8]| -> Blob {
        let slack = (byte(p) % 33) as usize; // 0..=32 extra bytes
        let offset = if slack == 0 {
            0
        } else {
            (byte(p) as usize) % (slack + 1)
        };
        let mut bytes = vec![0u8; payload.len() + slack];
        for b in bytes.iter_mut() {
            *b = byte(p);
        }
        bytes[offset..offset + payload.len()].copy_from_slice(payload);
        Blob { role, bytes, offset }
    };

    let mut blobs: Vec<Blob> = Vec::new();
    for (i, share) in layout.shares.iter().enumerate() {
        let stored = share.stored;
        blobs.push(framed(&mut placement, Role::Share(i), &stored));
    }
    // Split the salt into 2..=4 XOR pieces that reassemble to it.
    let salt_pieces = 2 + (byte(&mut placement) % 3) as usize;
    let mut running = [0u8; 32];
    for k in 0..salt_pieces {
        let piece: [u8; 32] = if k + 1 == salt_pieces {
            build_inputs::store(build_inputs::Scheme::Xor, &layout.salt, &running)
        } else {
            let mut piece = [0u8; 32];
            for b in piece.iter_mut() {
                *b = byte(&mut placement);
            }
            for (r, p) in running.iter_mut().zip(piece.iter()) {
                *r ^= *p;
            }
            piece
        };
        blobs.push(framed(&mut placement, Role::Salt(k), &piece));
    }
    // A handful of filler statics of their own random length.
    let filler = 3 + (byte(&mut placement) % 4) as usize;
    for _ in 0..filler {
        let len = 24 + (byte(&mut placement) % 41) as usize; // 24..=64
        let mut bytes = vec![0u8; len];
        for b in bytes.iter_mut() {
            *b = byte(&mut placement);
        }
        blobs.push(Blob {
            role: Role::Filler,
            bytes,
            offset: 0,
        });
    }

    // Shuffle the emission order (Fisher-Yates from the stream), so a static's name says nothing about its role.
    for i in (1..blobs.len()).rev() {
        let j = (byte(&mut placement) as usize) % (i + 1);
        blobs.swap(i, j);
    }

    let name = |k: usize| format!("K{k:02}");
    let mut share_at: Vec<(usize, usize)> = vec![(0, 0); layout.shares.len()];
    let mut salt_at: Vec<(usize, usize)> = vec![(0, 0); salt_pieces];
    let mut fillers: Vec<usize> = Vec::new();

    let mut src =
        String::from("// @generated by build.rs: the signing key, masked and scattered (see shares_source).\n");
    src.push_str("use crate::build_inputs::Scheme;\n\n");
    for (k, blob) in blobs.iter().enumerate() {
        let _ = writeln!(
            src,
            "#[used]\nstatic {}: [u8; {}] = {};",
            name(k),
            blob.bytes.len(),
            bytes(&blob.bytes)
        );
        match blob.role {
            Role::Share(i) => share_at[i] = (k, blob.offset),
            Role::Salt(j) => salt_at[j] = (k, blob.offset),
            Role::Filler => fillers.push(k),
        }
    }

    let _ = writeln!(src, "\npub(crate) const SHARE_COUNT: usize = {};", layout.shares.len());
    src.push_str("\n/// Recovers the share scalars' bytes for one use; their sum is never formed here.\n");
    src.push_str("pub(crate) fn recover_shares() -> Vec<[u8; 32]> {\n");
    src.push_str("    use zeroize::Zeroize as _;\n");
    // Reassemble the salt from its XOR pieces.
    let (k0, off0) = salt_at[0];
    let _ = writeln!(src, "    let mut salt = super::read_window(&{}, {off0});", name(k0));
    for &(k, off) in &salt_at[1..] {
        let _ = writeln!(
            src,
            "    super::xor_into(&mut salt, super::read_window(&{}, {off}));",
            name(k)
        );
    }
    // Touch the filler so it cannot be dropped.
    for &k in &fillers {
        let _ = writeln!(src, "    super::touch(&{});", name(k));
    }
    let _ = writeln!(src, "    let mut out = Vec::with_capacity(SHARE_COUNT);");
    for (i, share) in layout.shares.iter().enumerate() {
        let (k, off) = share_at[i];
        let _ = writeln!(
            src,
            "    out.push(super::share_value({}, super::read_window(&{}, {off}), &salt, {}, {}, {}));",
            share.scheme.path(),
            name(k),
            share.index,
            layout.order,
            layout.label
        );
    }
    src.push_str("    salt.zeroize();\n    out\n}\n");
    src
}

fn trust_source(pins: &[build_inputs::Pin], engine_hashes: &[[u8; 32]], engine_key: Option<&[u8; 32]>) -> String {
    let mut src = String::from("// @generated by build.rs: what a keyed guard agrees to launch.\n");
    src.push_str("pub(crate) static LOADER_PINS: &[(&str, [u8; 32])] = &[\n");
    for pin in pins {
        let _ = writeln!(src, "    ({:?}, {}),", pin.path, bytes(&pin.sha256));
    }
    src.push_str("];\n");
    src.push_str("pub(crate) static ENGINE_SHA256: &[[u8; 32]] = &[\n");
    for hash in engine_hashes {
        let _ = writeln!(src, "    {},", bytes(hash));
    }
    src.push_str("];\n");
    match engine_key {
        Some(key) => {
            let _ = writeln!(
                src,
                "pub(crate) static ENGINE_ED25519_KEY: Option<[u8; 32]> = Some({});",
                bytes(key)
            );
        }
        None => src.push_str("pub(crate) static ENGINE_ED25519_KEY: Option<[u8; 32]> = None;\n"),
    }
    src
}

fn bytes(data: &[u8]) -> String {
    let items: Vec<String> = data.iter().map(|b| format!("0x{b:02x}")).collect();
    format!("[{}]", items.join(", "))
}

fn read(path: &Path) -> String {
    println!("cargo:rerun-if-changed={}", path.display());
    fs::read_to_string(path).unwrap_or_else(|e| fail(&format!("cannot read {}: {e}", path.display())))
}

fn write(path: &Path, contents: &str) {
    fs::write(path, contents).unwrap_or_else(|e| fail(&format!("cannot write {}: {e}", path.display())));
}

fn fail(message: &str) -> ! {
    eprintln!("error: dh-guard: {message}");
    std::process::exit(1);
}
