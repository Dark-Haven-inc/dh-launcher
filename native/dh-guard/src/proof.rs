//! The tokens a Frontier 15 server checks to tell its players came through the genuine launcher. The server's
//! `LaunchProof` (dh-sector-frontier, Content.Server/_DH/AntiCheat/Launcher) verifies them; both must agree:
//!
//! ```text
//! token     = base64url(payload) "." base64url(signature)
//! payload   = "dh-launch/2\n" userId(guid, "D") "\n" base64url(challenge) "\n" launcherVersion      (UTF-8)
//! signature = ECDSA P-256 over SHA-256 of the payload bytes, IEEE P1363 (r || s, 64 bytes)
//! ```
//!
//! The challenge is the server's nonce for one login followed by that session's auth hash; the game asks for the proof
//! mid-handshake through the broker, so each proof is good for exactly one connection.
//!
//! Version 1 (`"dh-launch/1\n" userId "\n" unixSeconds "\n" launcherVersion`) is signed once at launch and handed
//! over in `DH_LAUNCH_PROOF` for servers that predate version 2; it goes once they all verify version 2.

use crate::split_ecdsa::{self, Shares};
use base64::Engine as _;
use base64::engine::general_purpose::{GeneralPurpose, GeneralPurposeConfig, URL_SAFE_NO_PAD};
use base64::engine::{DecodePaddingMode, general_purpose};
use uuid::Uuid;

pub const MAGIC_V1: &str = "dh-launch/1";
pub const MAGIC: &str = "dh-launch/2";

/// Environment variable the engine reads a version 1 proof from (`NetManager.LaunchProofEnvVar`).
pub const ENV_VAR: &str = "DH_LAUNCH_PROOF";

/// Bytes in a challenge: the server's 32-byte nonce and the 32-byte auth hash.
pub const CHALLENGE_LEN: usize = 64;

pub type Challenge = [u8; CHALLENGE_LEN];

/// URL-safe alphabet, padding optional, stray trailing bits tolerated: what .NET's `Convert.FromBase64String` accepts
/// once the C# side has swapped the alphabet and re-padded.
const LENIENT: GeneralPurpose = GeneralPurpose::new(
    &base64::alphabet::URL_SAFE,
    GeneralPurposeConfig::new()
        .with_decode_padding_mode(DecodePaddingMode::Indifferent)
        .with_decode_allow_trailing_bits(true),
);

/// base64url without padding, as `LaunchProof.ToBase64Url` writes it.
pub fn to_base64url(data: &[u8]) -> String {
    URL_SAFE_NO_PAD.encode(data)
}

/// Reads base64url the way `LaunchProof.TryFromBase64Url` does: either alphabet, with or without padding, whitespace
/// ignored.
pub fn from_base64url(text: &str) -> Option<Vec<u8>> {
    let normalized: String = text
        .chars()
        .filter(|c| !matches!(c, ' ' | '\t' | '\r' | '\n'))
        .map(|c| match c {
            '+' => '-',
            '/' => '_',
            other => other,
        })
        .collect();
    LENIENT.decode(normalized.as_bytes()).ok()
}

/// A GUID the way .NET formats `{guid:D}`: lowercase, hyphenated.
pub fn format_user_id(user_id: &Uuid) -> String {
    user_id.hyphenated().to_string()
}

/// Parses a GUID the way `Guid.TryParseExact(text, "D")` does: surrounding whitespace allowed, then exactly
/// 8-4-4-4-12 hex digits in either case.
pub fn parse_user_id_d(text: &str) -> Option<Uuid> {
    let text = text.trim();
    let bytes = text.as_bytes();
    if bytes.len() != 36 {
        return None;
    }
    for (i, &b) in bytes.iter().enumerate() {
        let ok = match i {
            8 | 13 | 18 | 23 => b == b'-',
            _ => b.is_ascii_hexdigit(),
        };
        if !ok {
            return None;
        }
    }
    Uuid::try_parse(text).ok()
}

/// The version 2 payload for one login.
pub fn payload_v2(user_id: &Uuid, challenge: &Challenge, launcher_version: &str) -> Vec<u8> {
    format!(
        "{MAGIC}\n{}\n{}\n{launcher_version}",
        format_user_id(user_id),
        to_base64url(challenge)
    )
    .into_bytes()
}

/// The version 1 payload, stamped with the time it was signed.
pub fn payload_v1(user_id: &Uuid, unix_seconds: i64, launcher_version: &str) -> Vec<u8> {
    format!(
        "{MAGIC_V1}\n{}\n{unix_seconds}\n{launcher_version}",
        format_user_id(user_id)
    )
    .into_bytes()
}

/// `base64url(payload) "." base64url(signature)`, signed from the shares.
pub fn token(shares: &Shares, payload: &[u8]) -> String {
    let signature = split_ecdsa::sign(shares, payload);
    format!("{}.{}", to_base64url(payload), to_base64url(&signature))
}

/// The token's two halves, decoded; None if it is not `payload.signature`.
pub fn split_token(token: &str) -> Option<(Vec<u8>, Vec<u8>)> {
    let (payload, signature) = token.split_once('.')?;
    Some((from_base64url(payload)?, from_base64url(signature)?))
}

/// The seconds since the Unix epoch, for a version 1 proof.
pub fn unix_now() -> i64 {
    match std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH) {
        Ok(elapsed) => elapsed.as_secs() as i64,
        Err(before) => -(before.duration().as_secs_f64().ceil() as i64),
    }
}

/// Standard base64 with padding, as .NET's `Convert.ToBase64String` writes it (for public keys).
pub fn to_base64(data: &[u8]) -> String {
    general_purpose::STANDARD.encode(data)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `LaunchProofTests.VectorChallenge`: byte i is i*3+1.
    pub(crate) fn vector_challenge() -> Challenge {
        let mut c = [0u8; CHALLENGE_LEN];
        for (i, b) in c.iter_mut().enumerate() {
            *b = (i * 3 + 1) as u8;
        }
        c
    }

    fn user() -> Uuid {
        Uuid::parse_str("11111111-2222-3333-4444-555555555555").unwrap()
    }

    #[test]
    fn base64url_matches_dotnet() {
        // Convert.ToBase64String(...).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        assert_eq!(to_base64url(&[0xfb, 0xff]), "-_8");
        assert_eq!(to_base64url(&[]), "");
        assert_eq!(to_base64url(b"f"), "Zg");
        assert_eq!(
            to_base64url(&vector_challenge()),
            "AQQHCg0QExYZHB8iJSgrLjE0Nzo9QENGSUxPUlVYW15hZGdqbXBzdnl8f4KFiIuOkZSXmp2go6aprK-ytbi7vg"
        );
    }

    #[test]
    fn base64url_reads_what_dotnet_reads() {
        assert_eq!(from_base64url("-_8").unwrap(), vec![0xfb, 0xff]);
        assert_eq!(from_base64url("+/8=").unwrap(), vec![0xfb, 0xff]);
        assert_eq!(from_base64url("Zg==").unwrap(), b"f");
        assert!(from_base64url("Z").is_none());
        assert!(from_base64url("Zg!").is_none());
    }

    #[test]
    fn v2_payload_is_byte_exact() {
        let payload = payload_v2(&user(), &vector_challenge(), "9.9.9");
        let expected = "dh-launch/2\n11111111-2222-3333-4444-555555555555\nAQQHCg0QExYZHB8iJSgrLjE0Nzo9QENGSUxPUlVYW15hZGdqbXBzdnl8f4KFiIuOkZSXmp2go6aprK-ytbi7vg\n9.9.9";
        assert_eq!(String::from_utf8(payload).unwrap(), expected);
    }

    #[test]
    fn user_id_is_lowercase_d_format() {
        let upper = Uuid::parse_str("ABCDEF01-2345-6789-ABCD-EF0123456789").unwrap();
        let payload = payload_v2(&upper, &vector_challenge(), "1.2.3");
        assert!(
            String::from_utf8(payload)
                .unwrap()
                .contains("\nabcdef01-2345-6789-abcd-ef0123456789\n")
        );
    }

    #[test]
    fn v1_payload_has_the_old_shape() {
        let payload = payload_v1(&user(), 1_790_000_000, "1.2.3");
        assert_eq!(
            String::from_utf8(payload).unwrap(),
            "dh-launch/1\n11111111-2222-3333-4444-555555555555\n1790000000\n1.2.3"
        );
    }

    #[test]
    fn guid_d_parsing_matches_dotnet() {
        assert_eq!(parse_user_id_d("11111111-2222-3333-4444-555555555555"), Some(user()));
        assert_eq!(parse_user_id_d(" 11111111-2222-3333-4444-555555555555 "), Some(user()));
        assert!(parse_user_id_d("ABCDEF01-2345-6789-ABCD-EF0123456789").is_some());
        assert!(parse_user_id_d("11111111222233334444555555555555").is_none());
        assert!(parse_user_id_d("{11111111-2222-3333-4444-555555555555}").is_none());
        assert!(parse_user_id_d("1111111-12222-3333-4444-555555555555").is_none());
        assert!(parse_user_id_d("").is_none());
    }

    #[test]
    fn token_has_the_agreed_shape_and_verifies() {
        use p256::ecdsa::signature::Verifier;
        use p256::ecdsa::{Signature, VerifyingKey};
        use p256::pkcs8::DecodePublicKey;

        let d: [u8; 32] = crate::build_inputs::sha256(b"dh-guard proof test key");
        let shares = Shares::from_bytes(&split_ecdsa::split(&d, 3));
        let payload = payload_v2(&user(), &vector_challenge(), "1.2.3");
        let token = token(&shares, &payload);

        let (p, s) = split_token(&token).unwrap();
        assert_eq!(p, payload);
        assert_eq!(s.len(), 64);
        assert!(!token.contains('='));
        let key = VerifyingKey::from_public_key_der(&split_ecdsa::public_key_spki(&shares)).unwrap();
        assert!(key.verify(&p, &Signature::from_slice(&s).unwrap()).is_ok());
    }
}
