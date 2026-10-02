//! What `build.rs` baked into this build, and the only place that reads it. Everything else takes keys, pins and
//! hashes as parameters, so tests can hand in their own and only the FFI layer wires in these.

use crate::build_inputs::{self, Scheme};
use crate::split_ecdsa::Shares;
use crate::verify::{EngineTrust, LoaderPin};
use core::hint::black_box;
use core::ptr;
use zeroize::Zeroize;

mod generated {
    include!(concat!(env!("OUT_DIR"), "/key_shares.rs"));
    include!(concat!(env!("OUT_DIR"), "/trust.rs"));
    include!(concat!(env!("OUT_DIR"), "/version.rs"));
}

/// Whether this is a release build with the signing key (see `build.rs`).
pub(crate) fn has_key() -> bool {
    generated::SHARE_COUNT != 0
}

pub(crate) fn launcher_version() -> &'static str {
    generated::LAUNCHER_VERSION
}

/// The 32 bytes of a static starting at `offset`, each read through `read_volatile`, so the compiler cannot fold the
/// value away into a constant that would sit in `.rodata` in the clear.
// Only a keyed build's generated code calls these; a development build has no shares to recover.
#[allow(dead_code)]
fn read_window(bytes: &'static [u8], offset: usize) -> [u8; 32] {
    let mut out = [0u8; 32];
    for (i, byte) in out.iter_mut().enumerate() {
        // SAFETY: build.rs sizes every static so this window is in bounds.
        *byte = unsafe { ptr::read_volatile(black_box(&bytes[offset + i])) };
    }
    out
}

#[allow(dead_code)]
fn xor_into(a: &mut [u8; 32], b: [u8; 32]) {
    for (x, y) in a.iter_mut().zip(b.iter()) {
        *x ^= *y;
    }
}

/// Reads a filler static so it is neither dropped nor optimised away, without keeping its bytes.
#[allow(dead_code)]
fn touch(bytes: &'static [u8]) {
    let mut acc = 0u8;
    for byte in bytes {
        // SAFETY: `byte` points into a live static.
        acc ^= unsafe { ptr::read_volatile(black_box(byte)) };
    }
    let _ = black_box(acc);
}

/// One share's big-endian bytes from its stored value: the mask is derived here from the per-build salt and the
/// share's index, never stored, then wiped.
#[allow(dead_code)]
fn share_value(scheme: Scheme, stored: [u8; 32], salt: &[u8; 32], index: u8, order: u8, label: u8) -> [u8; 32] {
    let mut mask = build_inputs::derive_mask(salt, index, order, label);
    let value = build_inputs::recover(scheme, &stored, &mask);
    mask.zeroize();
    value
}

/// The key's shares, unmasked for one use; they are wiped when dropped. None in a development build.
///
/// The shares are reassembled at run time (`generated::recover_shares`) from statics that hold only masked values and
/// pieces of a per-build salt; each mask is derived from the salt, not stored. Nothing forms the key here: the shares
/// are handed to the signer, which accumulates them one at a time.
pub(crate) fn shares() -> Option<Shares> {
    if !has_key() {
        return None;
    }
    let mut raw = generated::recover_shares();
    let shares = Shares::from_bytes(&raw);
    for share in raw.iter_mut() {
        share.zeroize();
    }
    Some(shares)
}

pub(crate) fn loader_pins() -> Vec<LoaderPin> {
    generated::LOADER_PINS
        .iter()
        .map(|(path, sha256)| LoaderPin {
            path: (*path).to_string(),
            sha256: *sha256,
        })
        .collect()
}

pub(crate) fn engine_trust() -> Option<EngineTrust> {
    Some(EngineTrust {
        bundled_sha256: generated::ENGINE_SHA256.to_vec(),
        ed25519_key: generated::ENGINE_ED25519_KEY?,
    })
}
