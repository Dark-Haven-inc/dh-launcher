//! The golden proof the C# tests check the Rust signer against
//! (tests/DarkHaven.Launcher.Tests/fixtures/rust-launch-proof.json): a v2 token for a fixed login, signed from the
//! shares a release build would lay out for a fixed test key.
//!
//! The file is committed. This test writes it when it is missing or DH_GUARD_WRITE_FIXTURE=1 is set (a new
//! signature each time: ECDSA is randomised), and otherwise checks that the committed one still holds.

use dh_guard::build_inputs::{self, sha256};
use dh_guard::proof;
use dh_guard::split_ecdsa::{self, Shares};
use p256::ecdsa::signature::Verifier;
use p256::ecdsa::{Signature, VerifyingKey};
use p256::pkcs8::DecodePublicKey;
use std::path::PathBuf;
use uuid::Uuid;

const USER: &str = "11111111-2222-3333-4444-555555555555";
const VERSION: &str = "9.9.9";

fn challenge() -> proof::Challenge {
    // LaunchProofTests.VectorChallenge
    let mut c = [0u8; 64];
    for (i, b) in c.iter_mut().enumerate() {
        *b = (i * 3 + 1) as u8;
    }
    c
}

fn path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/DarkHaven.Launcher.Tests/fixtures/rust-launch-proof.json")
}

fn shares() -> Shares {
    let d = build_inputs::unseal_scalar(&build_inputs::seal_scalar(&sha256(b"dh-guard golden fixture key"))).unwrap();
    let values = build_inputs::share_values(&d);
    Shares::from_bytes(&values)
}

#[test]
fn the_golden_proof_holds() {
    let shares = shares();
    let spki = split_ecdsa::public_key_spki(&shares);
    let user = Uuid::parse_str(USER).unwrap();
    let payload = proof::payload_v2(&user, &challenge(), VERSION);

    let path = path();
    if std::env::var_os("DH_GUARD_WRITE_FIXTURE").is_some_and(|v| v == "1") || !path.exists() {
        let fixture = serde_json::json!({
            "comment": "Written by native/dh-guard/tests/fixture.rs: a v2 launch proof from the Rust split signer.",
            "publicKey": proof::to_base64(&spki),
            "userId": USER,
            "challenge": proof::to_base64url(&challenge()),
            "launcherVersion": VERSION,
            "payload": String::from_utf8(payload.clone()).unwrap(),
            "token": proof::token(&shares, &payload),
        });
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, serde_json::to_string_pretty(&fixture).unwrap() + "\n").unwrap();
    }

    let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    let field = |name: &str| fixture[name].as_str().unwrap().to_string();
    assert_eq!(field("publicKey"), proof::to_base64(&spki));
    assert_eq!(field("userId"), USER);
    assert_eq!(field("challenge"), proof::to_base64url(&challenge()));
    assert_eq!(field("launcherVersion"), VERSION);
    assert_eq!(field("payload").as_bytes(), payload.as_slice());

    let (signed, signature) = proof::split_token(&field("token")).unwrap();
    assert_eq!(signed, payload);
    let key = VerifyingKey::from_public_key_der(&spki).unwrap();
    key.verify(&signed, &Signature::from_slice(&signature).unwrap())
        .unwrap();
}
