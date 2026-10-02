//! Signs launch proofs for the game the guard started, one per login, when the game asks mid-handshake. Nothing
//! long-lived is handed to the game: a proof is bound to the server's nonce and the session's auth hash (see
//! [`crate::proof`]), so lifting one from the process is worthless.
//!
//! The broker listens on a local endpoint named in `DH_LAUNCH_BROKER` (a named pipe on Windows, a Unix socket on
//! Linux) and answers only the process [`Broker::admit`] names, going by the kernel's account of who is on the other
//! end (`GetNamedPipeClientProcessId`, `SO_PEERCRED`), not anything the caller says. The exchange (the engine's
//! `LaunchBrokerClient` is the other side):
//!
//! ```text
//! request  = "dh-launch-broker/1\n" userId(guid, "D") "\n" base64url(challenge) "\n"
//! response = token "\n"        (an empty line if refused)
//! ```

use crate::proof::{self, CHALLENGE_LEN, Challenge};
use std::io;
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};
use uuid::Uuid;

#[cfg(target_os = "linux")]
mod unix;
#[cfg(target_os = "linux")]
use unix as platform;

#[cfg(windows)]
mod windows;
#[cfg(windows)]
use windows as platform;

#[cfg(not(any(target_os = "linux", windows)))]
mod unsupported;
#[cfg(not(any(target_os = "linux", windows)))]
use unsupported as platform;

pub const ENV_VAR: &str = "DH_LAUNCH_BROKER";
pub const MAGIC: &str = "dh-launch-broker/1";

/// How long one connection may take, request and answer together.
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(5);
const MAX_REQUEST_LENGTH: usize = 1024;

/// Makes the token for a login's challenge, or None to refuse.
pub type Signer = Arc<dyn Fn(&Uuid, &Challenge) -> Option<String> + Send + Sync>;

/// Whether the admitted process is still the one the guard started: false once it has exited, even if it has not
/// been reaped yet (so its pid is still its own). Asked right before signing.
pub type Liveness = Arc<dyn Fn() -> bool + Send + Sync>;

/// Who the broker answers.
#[derive(Clone)]
enum Admission {
    /// Not yet known: a connection waits (up to its deadline) for the game to be admitted.
    Pending,
    Admitted {
        pid: u32,
        alive: Option<Liveness>,
    },
    /// The game has exited: nobody is answered again.
    Withdrawn,
}

/// What the serving thread and the broker's owner share.
pub(crate) struct Shared {
    user_id: Uuid,
    signer: Signer,
    admission: Mutex<Admission>,
    admitted: Condvar,
    signed: Arc<AtomicI32>,
}

impl Shared {
    fn admission(&self) -> MutexGuard<'_, Admission> {
        self.admission.lock().unwrap_or_else(|p| p.into_inner())
    }

    fn set_admission(&self, admission: Admission) {
        *self.admission() = admission;
        self.admitted.notify_all();
    }

    /// Who is admitted, waiting until `deadline` while nobody is yet.
    fn wait_for_admission(&self, deadline: Instant) -> Admission {
        let mut admission = self.admission();
        while matches!(*admission, Admission::Pending) {
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                break;
            }
            admission = self
                .admitted
                .wait_timeout(admission, left)
                .unwrap_or_else(|p| p.into_inner())
                .0;
        }
        admission.clone()
    }
}

/// A running broker. Stopped (and on Linux its socket removed) by [`Broker::stop`] or when dropped.
pub struct Broker {
    endpoint: String,
    shared: Arc<Shared>,
    server: Option<platform::Server>,
}

impl Broker {
    /// Starts listening for `user_id`'s game, signing with `signer`. Nobody is answered until [`Broker::admit`] names
    /// the game process.
    pub fn start(user_id: Uuid, signer: Signer) -> io::Result<Broker> {
        let shared = Arc::new(Shared {
            user_id,
            signer,
            admission: Mutex::new(Admission::Pending),
            admitted: Condvar::new(),
            signed: Arc::new(AtomicI32::new(0)),
        });
        let name = format!("f15-launch-{}", random_hex(16)?);
        let (endpoint, server) = platform::Server::start(&name, shared.clone())?;
        Ok(Broker {
            endpoint,
            shared,
            server: Some(server),
        })
    }

    /// What to put in `DH_LAUNCH_BROKER` for the game.
    pub fn endpoint(&self) -> &str {
        &self.endpoint
    }

    /// The game process; only it is answered from now on.
    pub fn admit(&self, pid: u32) {
        self.shared.set_admission(Admission::Admitted { pid, alive: None });
    }

    /// Like [`Broker::admit`], and `alive` is asked right before each signature, so a peer that connected as the game
    /// is not signed for once the game has exited.
    pub fn admit_child(&self, pid: u32, alive: Liveness) {
        self.shared.set_admission(Admission::Admitted {
            pid,
            alive: Some(alive),
        });
    }

    /// Nobody is answered from now on. Called as soon as the game has exited, before its pid can be reused.
    pub fn withdraw(&self) {
        self.shared.set_admission(Admission::Withdrawn);
    }

    /// Proofs signed so far.
    pub fn signed(&self) -> i32 {
        self.shared.signed.load(Ordering::SeqCst)
    }

    /// The live count behind [`Broker::signed`], for whoever outlives the broker.
    pub fn signed_counter(&self) -> Arc<AtomicI32> {
        self.shared.signed.clone()
    }

    /// Withdraws admission, stops listening and waits for the serving thread; a connection in progress is cut off.
    pub fn stop(&mut self) {
        self.withdraw();
        if let Some(server) = self.server.take() {
            server.stop();
        }
    }
}

impl Drop for Broker {
    fn drop(&mut self) {
        self.stop();
    }
}

/// A connected client, as the serving code needs it: reads and writes that give up at a deadline or when the broker
/// is stopped.
pub(crate) trait Connection {
    /// Ok(0) at end of stream; an error on timeout or stop.
    fn read(&mut self, buf: &mut [u8], deadline: Instant) -> io::Result<usize>;
    fn write_all(&mut self, buf: &[u8], deadline: Instant) -> io::Result<()>;
}

/// Answers one connection from `peer_pid` (None if the kernel would not say).
pub(crate) fn serve(conn: &mut dyn Connection, peer_pid: Option<u32>, shared: &Shared) {
    let deadline = Instant::now() + REQUEST_TIMEOUT;

    let mut proof = None;
    // The game can connect before its spawn has returned and it is admitted; it is given until the deadline.
    let admitted = match shared.wait_for_admission(deadline) {
        Admission::Admitted { pid, alive } if peer_pid == Some(pid) => Some(alive),
        _ => None,
    };
    // Anyone but the game gets an empty line without being heard out.
    if let Some(alive) = admitted
        && let Some(challenge) = read_request(conn, deadline, &shared.user_id)
        // The peer was the game when it connected; it still is if the game has not exited (an exited child keeps its
        // pid until reaped, and it is only reaped after the broker stops).
        && alive.is_none_or(|alive| alive())
    {
        proof = (shared.signer)(&shared.user_id, &challenge);
        if proof.is_some() {
            shared.signed.fetch_add(1, Ordering::SeqCst);
        }
    }

    let mut response = proof.unwrap_or_default();
    response.push('\n');
    let _ = conn.write_all(response.as_bytes(), deadline);
}

/// The challenge from a well-formed request for our account, or None. Reads until three newlines have arrived, as the
/// C# broker did, so a request split across writes still gets through.
fn read_request(conn: &mut dyn Connection, deadline: Instant, user_id: &Uuid) -> Option<Challenge> {
    let mut buffer = [0u8; MAX_REQUEST_LENGTH];
    let mut length = 0;
    let mut newlines = 0;
    while newlines < 3 {
        if length == buffer.len() {
            return None;
        }
        let read = conn.read(&mut buffer[length..], deadline).ok()?;
        if read == 0 {
            return None;
        }
        newlines += buffer[length..length + read].iter().filter(|&&b| b == b'\n').count();
        length += read;
    }
    parse_request(&buffer[..length], user_id)
}

/// `"dh-launch-broker/1\n" userId "\n" base64url(challenge) "\n"` and nothing after it, for `user_id`.
pub fn parse_request(bytes: &[u8], user_id: &Uuid) -> Option<Challenge> {
    let text = std::str::from_utf8(bytes).ok()?;
    let lines: Vec<&str> = text.split('\n').collect();
    if lines.len() != 4 || !lines[3].is_empty() || lines[0] != MAGIC {
        return None;
    }
    let asked = proof::parse_user_id_d(lines[1])?;
    let challenge: Challenge = proof::from_base64url(lines[2])?.try_into().ok()?;
    if asked != *user_id {
        return None;
    }
    debug_assert_eq!(challenge.len(), CHALLENGE_LEN);
    Some(challenge)
}

fn random_hex(bytes: usize) -> io::Result<String> {
    let mut raw = vec![0u8; bytes];
    getrandom::fill(&mut raw).map_err(|e| io::Error::other(format!("no randomness for the broker name: {e}")))?;
    Ok(crate::build_inputs::encode_hex(&raw))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn user() -> Uuid {
        Uuid::parse_str("11111111-2222-3333-4444-555555555555").unwrap()
    }

    fn request(user: &str, challenge: &[u8]) -> String {
        format!("{MAGIC}\n{user}\n{}\n", proof::to_base64url(challenge))
    }

    #[test]
    fn a_well_formed_request_parses() {
        let c = [7u8; 64];
        assert_eq!(
            parse_request(request(&user().to_string(), &c).as_bytes(), &user()),
            Some(c)
        );
        // Guid "D" parsing takes either case.
        let upper = user().to_string().to_uppercase();
        assert_eq!(parse_request(request(&upper, &c).as_bytes(), &user()), Some(c));
    }

    #[test]
    fn malformed_requests_are_refused() {
        let c = [7u8; 64];
        let u = user().to_string();
        let good = request(&u, &c);
        for bad in [
            request(&Uuid::from_u128(1).to_string(), &c),
            good.replace(MAGIC, "dh-launch-broker/0"),
            request(&u, &[0u8; 32]),
            "\n\n\n".to_string(),
            format!("{good}x"),
            format!("{good}\n"),
            good.trim_end().to_string(),
            request("11111111222233334444555555555555", &c),
        ] {
            assert_eq!(parse_request(bad.as_bytes(), &user()), None, "{bad:?}");
        }
        assert_eq!(parse_request(b"dh-launch-broker/1\n\xff\n\n", &user()), None);
    }
}
