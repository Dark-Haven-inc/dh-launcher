//! What `build.rs` turns its inputs into: the signing key's share layout, the loader pins, the engine trust anchors.
//!
//! This file is compiled twice: into the build script (`#[path]`), which runs it at compile time, and into the crate,
//! so the same code is unit-tested and the tests can make keys and shares the way a release build does. Not every
//! function is used on both sides, hence the blanket `dead_code` allowance.

#![allow(dead_code)]

use base64::Engine as _;
use p256::elliptic_curve::ops::Reduce;
use p256::elliptic_curve::{Field, PrimeField};
use p256::{FieldBytes, Scalar};
use sha2::{Digest, Sha256};

/// What `dhlauncher launch-key` XORs the scalar with before printing it, so the CI secret is not the raw key.
pub const SECRET_MASK_LABEL: &[u8] = b"dh-launch-mask/1";

/// Seeds the layout stream (the shares and their storage), so the layout follows from the key and nothing else.
pub const LAYOUT_LABEL: &[u8] = b"dh-launch-layout/1";

/// Seeds a second stream `build.rs` draws placement from (filler, offsets, the order statics are emitted in), kept
/// apart from the layout so the two are not correlated.
pub const PLACEMENT_LABEL: &[u8] = b"dh-launch-placement/1";

/// Mixed into every run-time mask hash, so a mask is not a bare `SHA-256(salt || index)` that could be guessed by
/// hashing regions of the binary.
pub const MASK_DERIVE_LABEL: &[u8] = b"dh-launch-mask-derive/1";

/// Files a loader directory may hold besides the pinned ones: the marker `Update/LoaderCopy.cs` writes into its copy,
/// and what file managers leave behind.
pub const IGNORED_LOADER_FILES: &[&str] = &[".complete", "desktop.ini", "Thumbs.db", ".DS_Store"];

/// DER prefix of an Ed25519 SubjectPublicKeyInfo (RFC 8410); the 32-byte key follows it.
const ED25519_SPKI_PREFIX: [u8; 12] = [0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];

pub fn sha256(data: &[u8]) -> [u8; 32] {
    Sha256::digest(data).into()
}

fn secret_mask() -> [u8; 32] {
    sha256(SECRET_MASK_LABEL)
}

/// Masks a 32-byte private scalar the way the CLI prints it for the CI secret (base64, padded).
pub fn seal_scalar(scalar: &[u8; 32]) -> String {
    let mask = secret_mask();
    let mut masked = [0u8; 32];
    for (i, byte) in masked.iter_mut().enumerate() {
        *byte = scalar[i] ^ mask[i];
    }
    base64::engine::general_purpose::STANDARD.encode(masked)
}

/// The private scalar behind a sealed secret, checked to be a valid P-256 key (1 <= d < n).
pub fn unseal_scalar(sealed: &str) -> Result<[u8; 32], String> {
    let compact: String = sealed.chars().filter(|c| !c.is_ascii_whitespace()).collect();
    let masked = base64::engine::general_purpose::STANDARD
        .decode(compact.as_bytes())
        .map_err(|_| "the sealed signing key is not base64".to_string())?;
    if masked.len() != 32 {
        return Err(format!("the sealed signing key is {} bytes, not 32", masked.len()));
    }

    let mask = secret_mask();
    let mut d = [0u8; 32];
    for (i, byte) in d.iter_mut().enumerate() {
        *byte = masked[i] ^ mask[i];
    }

    let scalar = Option::<Scalar>::from(Scalar::from_repr(d.into()));
    match scalar {
        Some(s) if !bool::from(s.is_zero()) => Ok(d),
        _ => Err("the signing key is not a valid P-256 private scalar (1 <= d < n)".to_string()),
    }
}

/// SHA-256(seed || counter) chained into an endless byte stream; a port of `LaunchKeyGenerator.SeedStream`, so the
/// same key gives the same layout it did in the C# generator.
pub struct SeedStream {
    seed: [u8; 32],
    block: [u8; 32],
    pos: usize,
    counter: i64,
}

impl SeedStream {
    pub fn new(key: &[u8]) -> Self {
        Self::labeled(key, LAYOUT_LABEL)
    }

    /// A stream under a different domain label, so two streams from one key do not overlap.
    pub fn labeled(key: &[u8], label: &[u8]) -> Self {
        let mut hasher = Sha256::new();
        hasher.update(key);
        hasher.update(label);
        SeedStream {
            seed: hasher.finalize().into(),
            block: [0; 32],
            pos: 32,
            counter: 0,
        }
    }

    pub fn next_byte(&mut self) -> u8 {
        if self.pos >= self.block.len() {
            let mut hasher = Sha256::new();
            hasher.update(self.seed);
            hasher.update(self.counter.to_le_bytes());
            self.counter += 1;
            self.block = hasher.finalize().into();
            self.pos = 0;
        }
        let byte = self.block[self.pos];
        self.pos += 1;
        byte
    }

    pub fn next_bytes(&mut self) -> [u8; 32] {
        let mut out = [0u8; 32];
        for byte in out.iter_mut() {
            *byte = self.next_byte();
        }
        out
    }

    /// A scalar in [1, n): 32 stream bytes read as a little-endian integer (as the C# generator's
    /// `new BigInteger(bytes)` does), redrawn until in range.
    pub fn next_scalar(&mut self) -> Scalar {
        loop {
            let mut be = self.next_bytes();
            be.reverse();
            if let Some(s) = Option::<Scalar>::from(Scalar::from_repr(be.into()))
                && !bool::from(s.is_zero())
            {
                return s;
            }
        }
    }
}

/// A scalar from 32 big-endian bytes, reduced mod n, the same way at build time and run time.
fn scalar_from(bytes: &[u8; 32]) -> Scalar {
    <Scalar as Reduce<FieldBytes>>::reduce(&FieldBytes::from(*bytes))
}

fn xor(a: &[u8; 32], b: &[u8; 32]) -> [u8; 32] {
    let mut out = [0u8; 32];
    for i in 0..32 {
        out[i] = a[i] ^ b[i];
    }
    out
}

/// How a share is masked in the binary. The stored value is algebraically different under each, so no one rule
/// recovers a share from what is stored: the build picks a scheme per share.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Debug)]
pub enum Scheme {
    /// stored = share XOR mask.
    Xor,
    /// stored = share + mask (mod n).
    Add,
    /// stored = share * mask⁻¹ (mod n).
    Mul,
}

impl Scheme {
    pub fn from_seed(byte: u8) -> Scheme {
        match byte % 3 {
            0 => Scheme::Xor,
            1 => Scheme::Add,
            _ => Scheme::Mul,
        }
    }

    /// The `crate::build_inputs::Scheme` path `build.rs` writes into generated code.
    pub fn path(&self) -> &'static str {
        match self {
            Scheme::Xor => "Scheme::Xor",
            Scheme::Add => "Scheme::Add",
            Scheme::Mul => "Scheme::Mul",
        }
    }
}

/// The 32 mask bytes for `index`, hashed from the per-build `salt` with a per-build `order` and `label`, so the mask
/// is never a plain `SHA-256(salt || index)`. Deterministic: `build.rs` and the run-time recovery agree.
pub fn derive_mask(salt: &[u8; 32], index: u8, order: u8, label: u8) -> [u8; 32] {
    let mut h = Sha256::new();
    h.update(MASK_DERIVE_LABEL);
    // The order of the pieces is chosen per build, one more thing a generic tool would have to know.
    match order % 4 {
        0 => {
            h.update(salt);
            h.update([index, label]);
        }
        1 => {
            h.update([index, label]);
            h.update(salt);
        }
        2 => {
            h.update([label]);
            h.update(salt);
            h.update([index]);
        }
        _ => {
            h.update([index]);
            h.update(salt);
            h.update([label]);
        }
    }
    h.finalize().into()
}

/// Build time: the value stored for a share under `scheme`, given the mask bytes; the inverse of [`recover`].
pub fn store(scheme: Scheme, value: &[u8; 32], mask: &[u8; 32]) -> [u8; 32] {
    match scheme {
        Scheme::Xor => xor(value, mask),
        Scheme::Add => (scalar_from(value) + scalar_from(mask)).to_bytes().into(),
        Scheme::Mul => {
            let inv = Option::<Scalar>::from(scalar_from(mask).invert()).expect("a non-zero mask");
            (scalar_from(value) * inv).to_bytes().into()
        }
    }
}

/// Run time: a share's big-endian bytes from its stored value and mask; the inverse of [`store`].
pub fn recover(scheme: Scheme, stored: &[u8; 32], mask: &[u8; 32]) -> [u8; 32] {
    match scheme {
        Scheme::Xor => xor(stored, mask),
        Scheme::Add => (scalar_from(stored) - scalar_from(mask)).to_bytes().into(),
        Scheme::Mul => (scalar_from(stored) * scalar_from(mask)).to_bytes().into(),
    }
}

/// One additive share: its value `d_i` (the shares sum to `d` mod n) and how the binary stores it.
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct SharePlan {
    pub value: [u8; 32],
    pub scheme: Scheme,
    /// The mask index; not the share's position, so `derive_mask` never lands on a zero mask.
    pub index: u8,
    pub stored: [u8; 32],
}

/// How a build stores the signing key: 3..6 additive shares that sum to `d`, each masked by a run-time hash of the
/// per-build `salt`, under a per-build `order` and `label`. The count, shares, salt, order, label and per-share
/// scheme all come from a stream seeded by the key, so a rotated key lays out completely differently while a rebuild
/// with the same key is reproducible. No mask is stored: it is derived at run time (see `embedded.rs`).
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct KeyLayout {
    pub salt: [u8; 32],
    pub order: u8,
    pub label: u8,
    pub shares: Vec<SharePlan>,
}

pub fn plan(d: &[u8; 32]) -> KeyLayout {
    let key = Option::<Scalar>::from(Scalar::from_repr((*d).into())).expect("a validated scalar");
    let mut stream = SeedStream::new(d);

    let count = 3 + (stream.next_byte() % 4) as usize;
    let mut values = Vec::with_capacity(count);
    let mut sum = Scalar::ZERO;
    for _ in 0..count - 1 {
        let share = stream.next_scalar();
        sum += share;
        values.push(share);
    }
    // The last share makes the total come out to d.
    values.push(key - sum);

    let salt = stream.next_bytes();
    let order = stream.next_byte();
    let label = stream.next_byte();

    let shares = values
        .iter()
        .enumerate()
        .map(|(i, share)| {
            let value: [u8; 32] = share.to_bytes().into();
            let scheme = Scheme::from_seed(stream.next_byte());
            // Start at the share's position and step until the mask is a valid non-zero scalar (so Mul can invert it).
            let mut index = i as u8;
            let mask = loop {
                let mask = derive_mask(&salt, index, order, label);
                if !bool::from(scalar_from(&mask).is_zero()) {
                    break mask;
                }
                index = index.wrapping_add(1);
            };
            SharePlan {
                value,
                scheme,
                index,
                stored: store(scheme, &value, &mask),
            }
        })
        .collect();

    KeyLayout {
        salt,
        order,
        label,
        shares,
    }
}

/// The share values a build lays out for `d` (they sum to `d` mod n); for tests and the golden fixture.
pub fn share_values(d: &[u8; 32]) -> Vec<[u8; 32]> {
    plan(d).shares.into_iter().map(|s| s.value).collect()
}

/// One file the loader directory must hold.
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct Pin {
    /// Relative to the loader directory, '/'-separated.
    pub path: String,
    pub sha256: [u8; 32],
}

/// Reads a pins file: one `<64 hex sha256> <relative/path>` per line, blank lines ignored. `fold_case` rejects paths
/// that differ only in case, for targets where the file system would not tell them apart.
pub fn parse_pins(text: &str, fold_case: bool) -> Result<Vec<Pin>, String> {
    let mut pins: Vec<Pin> = Vec::new();
    for (number, raw) in text.lines().enumerate() {
        let line = raw.trim();
        if line.is_empty() {
            continue;
        }
        let at = |what: &str| format!("pins line {}: {what}", number + 1);

        let (hex, path) = line
            .split_once([' ', '\t'])
            .ok_or_else(|| at("expected \"<sha256> <path>\""))?;
        let path = path.trim_start();
        let sha256: [u8; 32] = decode_hex(hex)
            .and_then(|bytes| bytes.try_into().ok())
            .ok_or_else(|| at("the hash is not 64 hex digits"))?;
        check_relative_path(path).map_err(|e| at(&e))?;

        let key = |p: &str| if fold_case { p.to_lowercase() } else { p.to_string() };
        if pins.iter().any(|pin| key(&pin.path) == key(path)) {
            return Err(at(&format!("{path} is pinned twice")));
        }
        pins.push(Pin {
            path: path.to_string(),
            sha256,
        });
    }

    if pins.is_empty() {
        return Err("the pins file lists no files".to_string());
    }
    Ok(pins)
}

fn check_relative_path(path: &str) -> Result<(), String> {
    if path.is_empty() || path.starts_with('/') || path.contains('\\') || path.contains('\0') || path.contains(':') {
        return Err(format!("{path:?} is not a relative path with '/' separators"));
    }
    if path
        .split('/')
        .any(|part| part.is_empty() || part == "." || part == "..")
    {
        return Err(format!("{path:?} has an empty, '.' or '..' component"));
    }
    Ok(())
}

/// The 32-byte Ed25519 key in a PEM SubjectPublicKeyInfo (the SS14 engine signing key's `signing_key` asset). Exactly
/// one PUBLIC KEY block and nothing else, so the key read here is the one any other PEM reader would find too.
pub fn parse_ed25519_pem(text: &str) -> Result<[u8; 32], String> {
    const BEGIN: &str = "-----BEGIN PUBLIC KEY-----";
    const END: &str = "-----END PUBLIC KEY-----";

    let text = text.trim_start_matches('\u{feff}').trim();
    let body = text
        .strip_prefix(BEGIN)
        .and_then(|rest| rest.strip_suffix(END))
        .ok_or("not a single PEM PUBLIC KEY block")?;
    let compact: String = body.chars().filter(|c| !c.is_ascii_whitespace()).collect();
    let der = base64::engine::general_purpose::STANDARD
        .decode(compact.as_bytes())
        .map_err(|_| "the PEM body is not base64")?;

    if der.len() != ED25519_SPKI_PREFIX.len() + 32 || der[..ED25519_SPKI_PREFIX.len()] != ED25519_SPKI_PREFIX {
        return Err("not an Ed25519 SubjectPublicKeyInfo".to_string());
    }
    let mut key = [0u8; 32];
    key.copy_from_slice(&der[ED25519_SPKI_PREFIX.len()..]);
    Ok(key)
}

/// Every SHA-256 the bundled-engines manifest vouches for: each version's top-level `sha256` (the win-x64 build) and
/// each `platforms.*.sha256`.
pub fn bundled_engine_hashes(manifest_json: &str) -> Result<Vec<[u8; 32]>, String> {
    let manifest: serde_json::Value =
        serde_json::from_str(manifest_json).map_err(|e| format!("the engine manifest is not JSON: {e}"))?;
    let versions = manifest.as_object().ok_or("the engine manifest is not an object")?;

    let mut hashes: Vec<[u8; 32]> = Vec::new();
    let mut add = |value: Option<&serde_json::Value>, what: &str| -> Result<(), String> {
        let Some(value) = value else { return Ok(()) };
        let hex = value
            .as_str()
            .ok_or_else(|| format!("{what}: sha256 is not a string"))?;
        let hash: [u8; 32] = decode_hex(hex)
            .and_then(|b| b.try_into().ok())
            .ok_or_else(|| format!("{what}: sha256 is not 64 hex digits"))?;
        if !hashes.contains(&hash) {
            hashes.push(hash);
        }
        Ok(())
    };

    for (version, entry) in versions {
        add(entry.get("sha256"), version)?;
        if let Some(platforms) = entry.get("platforms").and_then(|p| p.as_object()) {
            for (rid, build) in platforms {
                add(build.get("sha256"), &format!("{version}/{rid}"))?;
            }
        }
    }
    Ok(hashes)
}

/// Hex in either case; None for anything else, including an odd length.
pub fn decode_hex(text: &str) -> Option<Vec<u8>> {
    let bytes = text.as_bytes();
    if !bytes.len().is_multiple_of(2) {
        return None;
    }
    let nibble = |c: u8| match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        b'A'..=b'F' => Some(c - b'A' + 10),
        _ => None,
    };
    bytes
        .chunks(2)
        .map(|pair| Some((nibble(pair[0])? << 4) | nibble(pair[1])?))
        .collect()
}

pub fn encode_hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        out.push(DIGITS[(b >> 4) as usize] as char);
        out.push(DIGITS[(b & 15) as usize] as char);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(label: &str) -> [u8; 32] {
        // Any hash of a label is below n with overwhelming probability; these fixed ones are.
        sha256(label.as_bytes())
    }

    fn sum(values: &[[u8; 32]]) -> [u8; 32] {
        values
            .iter()
            .fold(Scalar::ZERO, |acc, v| acc + scalar_from(v))
            .to_bytes()
            .into()
    }

    #[test]
    fn sealing_round_trips_and_hides_the_key() {
        let d = key("seal");
        let sealed = seal_scalar(&d);
        assert_ne!(sealed, base64::engine::general_purpose::STANDARD.encode(d));
        assert_eq!(unseal_scalar(&sealed).unwrap(), d);
        // CI secrets pick up stray whitespace; it does not matter.
        assert_eq!(unseal_scalar(&format!(" {sealed}\n")).unwrap(), d);
    }

    #[test]
    fn unsealing_rejects_what_is_not_a_key() {
        assert!(unseal_scalar("not base64!").is_err());
        assert!(unseal_scalar(&base64::engine::general_purpose::STANDARD.encode([1u8; 16])).is_err());
        // Zero and n itself are not private keys.
        assert!(unseal_scalar(&seal_scalar(&[0u8; 32])).is_err());
        let n: [u8; 32] = decode_hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551")
            .unwrap()
            .try_into()
            .unwrap();
        assert!(unseal_scalar(&seal_scalar(&n)).is_err());
        let mut n_minus_one = n;
        n_minus_one[31] -= 1;
        assert!(unseal_scalar(&seal_scalar(&n_minus_one)).is_ok());
    }

    #[test]
    fn shares_sum_to_the_key_and_nothing_is_stored_in_the_clear() {
        for label in ["a", "b", "c", "d", "e", "f", "g", "h"] {
            let d = key(label);
            let plan = plan(&d);
            assert!((3..=6).contains(&plan.shares.len()), "{} shares", plan.shares.len());
            assert_eq!(sum(&share_values(&d)), d);
            for share in &plan.shares {
                // The mask is not stored; recovering it from what is stored gives the share back.
                let mask = derive_mask(&plan.salt, share.index, plan.order, plan.label);
                assert_eq!(recover(share.scheme, &share.stored, &mask), share.value);
                assert_ne!(share.stored, share.value, "a share is stored in the clear");
                assert_ne!(share.value, d);
                // Nor is the share the plain XOR of what is stored and the salt (the old adjacency scan).
                assert_ne!(xor(&share.stored, &plan.salt), share.value);
            }
        }
    }

    #[test]
    fn every_scheme_round_trips() {
        let value = key("a value");
        let mask = key("a mask");
        for scheme in [Scheme::Xor, Scheme::Add, Scheme::Mul] {
            assert_eq!(
                recover(scheme, &store(scheme, &value, &mask), &mask),
                value,
                "{scheme:?}"
            );
        }
    }

    #[test]
    fn the_layout_follows_from_the_key() {
        let d = key("layout");
        assert_eq!(plan(&d), plan(&d));
        let other = plan(&key("rotated"));
        let first = plan(&d);
        assert_ne!(first.salt, other.salt);
        assert!(
            first
                .shares
                .iter()
                .all(|s| !other.shares.iter().any(|o| o.stored == s.stored))
        );
    }

    #[test]
    fn the_share_count_and_schemes_vary_with_the_key() {
        let counts: std::collections::BTreeSet<usize> =
            (0..64).map(|i| plan(&key(&format!("k{i}"))).shares.len()).collect();
        assert_eq!(counts, [3, 4, 5, 6].into_iter().collect());
        // Over many keys all three schemes are used, and a build mixes them.
        let schemes: std::collections::BTreeSet<Scheme> = (0..64)
            .flat_map(|i| plan(&key(&format!("s{i}"))).shares.into_iter().map(|s| s.scheme))
            .collect();
        assert_eq!(schemes.len(), 3);
    }

    #[test]
    fn the_seed_stream_is_the_generators() {
        // SeedStream: seed = SHA256(key || "dh-launch-layout/1"); block i = SHA256(seed || i as little-endian i64).
        let d = key("stream");
        let seed = {
            let mut h = Sha256::new();
            h.update(d);
            h.update(b"dh-launch-layout/1");
            <[u8; 32]>::from(h.finalize())
        };
        let block = |i: i64| {
            let mut h = Sha256::new();
            h.update(seed);
            h.update(i.to_le_bytes());
            <[u8; 32]>::from(h.finalize())
        };
        let mut stream = SeedStream::new(&d);
        let first: Vec<u8> = (0..64).map(|_| stream.next_byte()).collect();
        assert_eq!(&first[..32], &block(0));
        assert_eq!(&first[32..], &block(1));
    }

    #[test]
    fn pins_parse() {
        let text = "\n0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789abcdef DarkHaven.Loader\r\n\
                    \n  ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff runtimes/linux x64/lib.so  \n";
        let pins = parse_pins(text, false).unwrap();
        assert_eq!(pins.len(), 2);
        assert_eq!(pins[0].path, "DarkHaven.Loader");
        assert_eq!(pins[0].sha256[0], 0x01);
        assert_eq!(pins[1].path, "runtimes/linux x64/lib.so");
    }

    #[test]
    fn bad_pins_are_rejected() {
        let hash = "00".repeat(32);
        for bad in [
            String::new(),
            "\n\n".to_string(),
            "DarkHaven.Loader".to_string(),
            format!("{} a", "0".repeat(63)),
            hash.clone(),
            format!("{hash} /abs"),
            format!("{hash} a/../b"),
            format!("{hash} a//b"),
            format!("{hash} a\\b"),
            format!("{hash} ./a"),
            format!("{hash} C:/a"),
            format!("{hash} a\n{hash} a"),
        ] {
            assert!(parse_pins(&bad, false).is_err(), "{bad:?}");
        }
        let cased = format!("{hash} A.dll\n{hash} a.dll");
        assert!(parse_pins(&cased, false).is_ok());
        assert!(parse_pins(&cased, true).is_err());
    }

    #[test]
    fn ed25519_pem_parses_and_rejects_the_rest() {
        let pem = "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApQ9mAhMLbmhQqRH7itgNo75S5rCSMsMXvVRmMv1d9NQ=\n-----END PUBLIC KEY-----\n";
        let key = parse_ed25519_pem(pem).unwrap();
        assert_eq!(encode_hex(&key[..4]), "a50f6602");
        assert!(parse_ed25519_pem(&pem.replace("\n", "\r\n")).is_ok());
        assert!(parse_ed25519_pem(&format!("{pem}{pem}")).is_err());
        assert!(parse_ed25519_pem("garbage").is_err());
        // A P-256 key is not an Ed25519 one.
        let p256 = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE\n-----END PUBLIC KEY-----";
        assert!(parse_ed25519_pem(p256).is_err());
    }

    #[test]
    fn engine_hashes_come_from_every_build() {
        let manifest = r#"{
            "1.0.0": {"file": "a.zip", "sha256": "AA00000000000000000000000000000000000000000000000000000000000000",
                      "platforms": {"linux-x64": {"file": "b.zip", "sha256": "bb00000000000000000000000000000000000000000000000000000000000000"}}},
            "2.0.0": {"platforms": {"win-x64": {"sha256": "cc00000000000000000000000000000000000000000000000000000000000000"}}}
        }"#;
        let hashes = bundled_engine_hashes(manifest).unwrap();
        let firsts: Vec<u8> = hashes.iter().map(|h| h[0]).collect();
        assert_eq!(firsts, [0xaa, 0xbb, 0xcc]);
        assert!(bundled_engine_hashes(r#"{"1": {"sha256": "zz"}}"#).is_err());
        assert!(bundled_engine_hashes("[]").is_err());
    }

    #[test]
    fn the_shipped_manifest_parses() {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../src/DarkHaven.App/bundled-engines/manifest.json");
        let hashes = bundled_engine_hashes(&std::fs::read_to_string(path).unwrap()).unwrap();
        assert!(!hashes.is_empty());
    }
}
