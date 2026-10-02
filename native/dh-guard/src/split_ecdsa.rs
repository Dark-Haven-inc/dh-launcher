//! ECDSA P-256 signing where the private key is never held as one value.
//!
//! The key is kept as shares `d1 + d2 + ... = d (mod n)`, and a signature is computed from the shares directly
//! (`s = k^-1 * (e + r*d1 + r*d2 + ...) mod n`), so no variable and no run of bytes in the binary ever equals the key.
//! Each build lays the shares out differently (see `build_inputs::plan`): no mask is stored (each is a run-time hash
//! of a per-build salt and the share's index), the combine differs per share (XOR, additive, or multiplicative), and
//! the stored values and salt pieces are scattered among random filler in a key-shuffled order. So a generic scan
//! for adjacent share/mask pairs, or for any fixed offset/stride pairing of stored bytes (XOR, add or multiply),
//! finds nothing. The result is an ordinary IEEE P1363 signature the server verifies like any other.
//!
//! This does not make the key impossible to recover; it turns a generic, key-independent scan into per-build reverse
//! engineering (an attacker has to read this build's generated recovery code). And a rebuild with the same key is
//! reproducible. The short-lived, one-time, server-bound proofs are what limit what a recovered key is worth.

use p256::elliptic_curve::ops::Reduce;
use p256::elliptic_curve::point::AffineCoordinates;
use p256::elliptic_curve::{Field, PrimeField};
use p256::{FieldBytes, ProjectivePoint, Scalar};
use sha2::{Digest, Sha256};
use zeroize::Zeroize;

/// A P-256 SubjectPublicKeyInfo is this prefix followed by the uncompressed point (0x04 || X || Y).
const SPKI_PREFIX: [u8; 26] = [
    0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce,
    0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
];

/// Bytes in a P-256 SubjectPublicKeyInfo, what the game servers load a launcher key from.
pub const SPKI_LEN: usize = 91;

/// The key's shares, wiped when dropped.
#[derive(Clone)]
pub struct Shares(Vec<Scalar>);

impl Shares {
    /// Shares from 32-byte big-endian values; each is taken mod n, as the C# signer did.
    pub fn from_bytes(shares: &[[u8; 32]]) -> Shares {
        Shares(
            shares
                .iter()
                .map(|bytes| {
                    let mut repr = FieldBytes::from(*bytes);
                    let scalar = <Scalar as Reduce<FieldBytes>>::reduce(&repr);
                    repr.as_mut_slice().zeroize();
                    scalar
                })
                .collect(),
        )
    }

    pub fn len(&self) -> usize {
        self.0.len()
    }

    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
}

impl Drop for Shares {
    fn drop(&mut self) {
        self.0.zeroize();
    }
}

/// Signs `message` (SHA-256) from the shares: a 64-byte P1363 signature, r || s.
pub fn sign(shares: &Shares, message: &[u8]) -> [u8; 64] {
    let hash: [u8; 32] = Sha256::digest(message).into();
    let e = <Scalar as Reduce<FieldBytes>>::reduce(&FieldBytes::from(hash));

    loop {
        let mut k = random_scalar();
        let point = (ProjectivePoint::GENERATOR * k).to_affine();
        let r = <Scalar as Reduce<FieldBytes>>::reduce(&point.x());
        if bool::from(r.is_zero()) {
            k.zeroize();
            continue;
        }

        // acc = e + r*d1 + r*d2 + ... (mod n), share by share, so the sum of the shares is never formed on its own.
        let mut acc = e;
        for share in shares.0.iter() {
            acc += r * share;
        }

        let mut k_inv = Option::<Scalar>::from(k.invert()).expect("k is non-zero");
        let s = k_inv * acc;
        k.zeroize();
        k_inv.zeroize();
        acc.zeroize();
        if bool::from(s.is_zero()) {
            continue;
        }

        let mut signature = [0u8; 64];
        signature[..32].copy_from_slice(&r.to_bytes());
        signature[32..].copy_from_slice(&s.to_bytes());
        return signature;
    }
}

/// The public key the shares make up, `d1*G + d2*G + ...` (the key itself is never summed), as a DER
/// SubjectPublicKeyInfo: what `dhlauncher launch-key` prints and the servers load.
pub fn public_key_spki(shares: &Shares) -> [u8; SPKI_LEN] {
    let point = shares
        .0
        .iter()
        .fold(ProjectivePoint::IDENTITY, |acc, share| {
            acc + ProjectivePoint::GENERATOR * share
        })
        .to_affine();

    let mut spki = [0u8; SPKI_LEN];
    spki[..SPKI_PREFIX.len()].copy_from_slice(&SPKI_PREFIX);
    spki[26] = 0x04;
    spki[27..59].copy_from_slice(&point.x());
    spki[59..91].copy_from_slice(&point.y());
    spki
}

/// Splits a private scalar into `count` random shares that sum to it mod n. For tests and tools; release builds get
/// their layout from `build.rs`.
pub fn split(d: &[u8; 32], count: usize) -> Vec<[u8; 32]> {
    assert!(count >= 1);
    let key = <Scalar as Reduce<FieldBytes>>::reduce(&FieldBytes::from(*d));
    let mut shares = Vec::with_capacity(count);
    let mut sum = Scalar::ZERO;
    for _ in 0..count - 1 {
        let share = random_scalar();
        sum += share;
        shares.push(share.to_bytes().into());
    }
    shares.push((key - sum).to_bytes().into());
    shares
}

/// A uniformly random scalar in [1, n) from the OS, by rejection like the C# signer.
fn random_scalar() -> Scalar {
    let mut bytes = [0u8; 32];
    loop {
        getrandom::fill(&mut bytes).expect("the OS random number generator failed");
        let candidate = Option::<Scalar>::from(Scalar::from_repr(bytes.into()));
        if let Some(scalar) = candidate
            && !bool::from(scalar.is_zero())
        {
            bytes.zeroize();
            return scalar;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use p256::ecdsa::signature::Verifier;
    use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
    use p256::pkcs8::DecodePublicKey;

    fn random_key() -> [u8; 32] {
        random_scalar().to_bytes().into()
    }

    fn whole_key(d: &[u8; 32]) -> VerifyingKey {
        *SigningKey::from_bytes(&FieldBytes::from(*d)).unwrap().verifying_key()
    }

    #[test]
    fn signature_from_shares_verifies_with_the_whole_key() {
        for count in [1, 2, 3, 5, 6] {
            let d = random_key();
            let shares = Shares::from_bytes(&split(&d, count));
            let key = whole_key(&d);
            for i in 0..20 {
                let message = format!("dh-launch/2 payload #{i}");
                let sig = sign(&shares, message.as_bytes());
                let sig = Signature::from_slice(&sig).unwrap();
                assert!(
                    key.verify(message.as_bytes(), &sig).is_ok(),
                    "{count} shares, message {i}"
                );
            }
        }
    }

    #[test]
    fn a_wrong_key_does_not_verify() {
        let shares = Shares::from_bytes(&split(&random_key(), 3));
        let other = whole_key(&random_key());
        let sig = Signature::from_slice(&sign(&shares, b"x")).unwrap();
        assert!(other.verify(b"x", &sig).is_err());
    }

    #[test]
    fn shares_sum_to_the_key() {
        let d = random_key();
        let parts = split(&d, 4);
        let sum = parts.iter().fold(Scalar::ZERO, |acc, p| {
            acc + Option::<Scalar>::from(Scalar::from_repr((*p).into())).unwrap()
        });
        assert_eq!(<[u8; 32]>::from(sum.to_bytes()), d);
    }

    #[test]
    fn public_key_from_shares_is_the_keys() {
        let d = random_key();
        let shares = Shares::from_bytes(&split(&d, 5));
        let spki = public_key_spki(&shares);
        let from_spki = VerifyingKey::from_public_key_der(&spki).unwrap();
        assert_eq!(from_spki, whole_key(&d));

        // And a signature from the shares verifies with it.
        let sig = Signature::from_slice(&sign(&shares, b"m")).unwrap();
        assert!(from_spki.verify(b"m", &sig).is_ok());
    }

    #[test]
    fn signing_is_randomised_but_each_signature_is_valid() {
        let d = random_key();
        let shares = Shares::from_bytes(&split(&d, 2));
        let a = sign(&shares, b"same");
        let b = sign(&shares, b"same");
        assert_ne!(a, b);
        let key = whole_key(&d);
        assert!(key.verify(b"same", &Signature::from_slice(&a).unwrap()).is_ok());
        assert!(key.verify(b"same", &Signature::from_slice(&b).unwrap()).is_ok());
    }
}
