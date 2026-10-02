//! `dh_guard`: the Dark Haven launcher's trust core, moved out of the launcher's IL (which anyone can decompile and
//! patch) into native code the launcher loads in-process.
//!
//! It starts the genuine loader for the launcher and signs launch proofs for that one process, from a signing key it
//! never holds whole. See docs/GUARD.md for the trust model, the build modes and the C ABI ([`ffi`]).
//!
//! The modules below the FFI take their keys, pins and hashes as parameters, so tests can drive them with their own;
//! only [`ffi`] wires in what the build embedded.

pub mod broker;
pub mod build_inputs;
#[cfg(unix)]
mod child;
mod embedded;
pub mod env;
pub mod ffi;
pub mod launch;
pub mod proof;
pub mod request;
pub mod split_ecdsa;
pub mod verify;
