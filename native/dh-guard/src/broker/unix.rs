//! The broker on Linux: a Unix socket in a directory only this user can enter, answering the peer `SO_PEERCRED`
//! names.

use super::{Connection, Shared, serve};
use std::ffi::OsString;
use std::fs::{self, DirBuilder};
use std::io;
use std::mem;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::ffi::{OsStrExt, OsStringExt};
use std::os::unix::fs::{DirBuilderExt, MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::Instant;

pub(crate) struct Server {
    stop_write: OwnedFd,
    thread: JoinHandle<()>,
    path: PathBuf,
    /// A directory made for this broker alone, removed with the socket.
    own_dir: Option<PathBuf>,
}

impl Server {
    pub(crate) fn start(name: &str, shared: Arc<Shared>) -> io::Result<(String, Server)> {
        let (dir, own_dir) = socket_directory()?;
        let path = dir.join(format!("{name}.sock"));
        let clean_up = |path: &Path| {
            let _ = fs::remove_file(path);
            if let Some(dir) = &own_dir {
                let _ = fs::remove_dir(dir);
            }
        };
        let listener = match bind(&path) {
            Ok(listener) => listener,
            Err(e) => {
                clean_up(&path);
                return Err(e);
            }
        };
        let (stop_read, stop_write) = match pipe() {
            Ok(fds) => fds,
            Err(e) => {
                clean_up(&path);
                return Err(e);
            }
        };

        let thread = std::thread::Builder::new()
            .name("dh-guard-broker".into())
            .spawn(move || accept_loop(listener, stop_read, &shared));
        let thread = match thread {
            Ok(thread) => thread,
            Err(e) => {
                clean_up(&path);
                return Err(e);
            }
        };

        let endpoint = format!("unix:{}", path.display());
        Ok((
            endpoint,
            Server {
                stop_write,
                thread,
                path,
                own_dir,
            },
        ))
    }

    pub(crate) fn stop(self) {
        // One byte on the stop pipe wakes every poll the thread is in.
        let _ = unsafe { libc::write(self.stop_write.as_raw_fd(), [1u8].as_ptr().cast(), 1) };
        let _ = self.thread.join();
        let _ = fs::remove_file(&self.path);
        if let Some(dir) = &self.own_dir {
            let _ = fs::remove_dir(dir);
        }
    }
}

/// Where the socket goes: the per-user runtime directory (0700, cleared at logout) if there is one, else a private
/// directory of our own in the temp directory. That is `f15-launch-<user>` if it really is ours; if someone else made
/// it first (or it was opened up), a fresh one with a random name, made for this broker alone (the second value) and
/// removed when it stops. So nobody can keep the broker from starting by squatting on the fixed name.
fn socket_directory() -> io::Result<(PathBuf, Option<PathBuf>)> {
    socket_directory_in(std::env::var_os("XDG_RUNTIME_DIR"), &std::env::temp_dir(), &user_name())
}

fn socket_directory_in(runtime: Option<OsString>, temp: &Path, user: &str) -> io::Result<(PathBuf, Option<PathBuf>)> {
    if let Some(runtime) = runtime
        && !runtime.is_empty()
        && Path::new(&runtime).is_dir()
    {
        return Ok((PathBuf::from(runtime), None));
    }
    let prefix = format!("f15-launch-{user}");
    let fixed = temp.join(&prefix);
    if ensure_private_dir(&fixed).is_ok() {
        return Ok((fixed, None));
    }
    let fresh = make_temp_dir(&temp.join(format!("{prefix}-")))?;
    Ok((fresh.clone(), Some(fresh)))
}

/// `mkdtemp`: a new directory named `<prefix>XXXXXX` (random), created 0700 and never one that already existed.
fn make_temp_dir(prefix: &Path) -> io::Result<PathBuf> {
    let mut template = prefix.as_os_str().as_bytes().to_vec();
    template.extend_from_slice(b"XXXXXX\0");
    if template[..template.len() - 1].contains(&0) {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "the temp path has a NUL"));
    }
    let made = unsafe { libc::mkdtemp(template.as_mut_ptr().cast()) };
    if made.is_null() {
        return Err(io::Error::last_os_error());
    }
    template.pop();
    let dir = PathBuf::from(OsString::from_vec(template));
    // mkdtemp makes it 0700 and ours; check anyway, as for the fixed name.
    ensure_private_dir(&dir)?;
    Ok(dir)
}

/// Creates `dir` as 0700, or accepts it if it already is: a real directory (not a link), owned by us, with no group or
/// other permissions.
pub(crate) fn ensure_private_dir(dir: &Path) -> io::Result<()> {
    match DirBuilder::new().mode(0o700).create(dir) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
        Err(e) => return Err(e),
    }
    let meta = fs::symlink_metadata(dir)?;
    let uid = unsafe { libc::geteuid() };
    if !meta.file_type().is_dir() || meta.uid() != uid || meta.mode() & 0o077 != 0 {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            format!("{} is not a private directory of this user", dir.display()),
        ));
    }
    Ok(())
}

/// The user's login name, as .NET's `Environment.UserName` reports it; the uid if there is none usable.
fn user_name() -> String {
    let uid = unsafe { libc::geteuid() };
    let mut pwd: libc::passwd = unsafe { mem::zeroed() };
    let mut buffer = vec![0 as libc::c_char; 4096];
    let mut result: *mut libc::passwd = std::ptr::null_mut();
    let rc = unsafe { libc::getpwuid_r(uid, &mut pwd, buffer.as_mut_ptr(), buffer.len(), &mut result) };
    if rc == 0 && !result.is_null() && !pwd.pw_name.is_null() {
        let name = unsafe { std::ffi::CStr::from_ptr(pwd.pw_name) }.to_bytes().to_vec();
        let name = OsString::from_vec(name);
        if let Some(name) = name.to_str()
            && !name.is_empty()
            && !name.contains('/')
        {
            return name.to_string();
        }
    }
    uid.to_string()
}

fn cvt(rc: libc::c_int) -> io::Result<libc::c_int> {
    if rc < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(rc)
    }
}

/// Binds and listens at `path`: bound, made 0600, then listening, as the C# broker did. Close-on-exec, so no game the
/// launcher starts meanwhile inherits it.
fn bind(path: &Path) -> io::Result<OwnedFd> {
    let fd = cvt(unsafe {
        libc::socket(
            libc::AF_UNIX,
            libc::SOCK_STREAM | libc::SOCK_CLOEXEC | libc::SOCK_NONBLOCK,
            0,
        )
    })?;
    let socket = unsafe { OwnedFd::from_raw_fd(fd) };

    let mut addr: libc::sockaddr_un = unsafe { mem::zeroed() };
    addr.sun_family = libc::AF_UNIX as libc::sa_family_t;
    let bytes = path.as_os_str().as_bytes();
    if bytes.len() >= addr.sun_path.len() || bytes.contains(&0) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the socket path is too long",
        ));
    }
    for (dst, &src) in addr.sun_path.iter_mut().zip(bytes) {
        *dst = src as libc::c_char;
    }
    let len = (mem::size_of::<libc::sa_family_t>() + bytes.len() + 1) as libc::socklen_t;
    cvt(unsafe { libc::bind(fd, (&raw const addr).cast(), len) })?;

    let listening = fs::set_permissions(path, fs::Permissions::from_mode(0o600))
        .and_then(|()| cvt(unsafe { libc::listen(fd, 4) }).map(|_| ()));
    if let Err(e) = listening {
        let _ = fs::remove_file(path);
        return Err(e);
    }
    Ok(socket)
}

fn pipe() -> io::Result<(OwnedFd, OwnedFd)> {
    let mut fds = [0 as RawFd; 2];
    cvt(unsafe { libc::pipe2(fds.as_mut_ptr(), libc::O_CLOEXEC | libc::O_NONBLOCK) })?;
    Ok(unsafe { (OwnedFd::from_raw_fd(fds[0]), OwnedFd::from_raw_fd(fds[1])) })
}

/// What a wait ended with.
enum Ready {
    Fd,
    Stop,
    Timeout,
}

/// Waits for `events` on `fd` or for the stop pipe, until `deadline` (None = forever).
fn wait(fd: RawFd, events: libc::c_short, stop: RawFd, deadline: Option<Instant>) -> io::Result<Ready> {
    loop {
        let timeout = match deadline {
            None => -1,
            Some(deadline) => {
                let left = deadline.saturating_duration_since(Instant::now());
                if left.is_zero() {
                    return Ok(Ready::Timeout);
                }
                left.as_millis().clamp(1, i32::MAX as u128) as libc::c_int
            }
        };
        let mut fds = [
            libc::pollfd { fd, events, revents: 0 },
            libc::pollfd {
                fd: stop,
                events: libc::POLLIN,
                revents: 0,
            },
        ];
        let rc = unsafe { libc::poll(fds.as_mut_ptr(), 2, timeout) };
        if rc < 0 {
            let e = io::Error::last_os_error();
            if e.kind() == io::ErrorKind::Interrupted {
                continue;
            }
            return Err(e);
        }
        if fds[1].revents != 0 {
            return Ok(Ready::Stop);
        }
        if fds[0].revents != 0 {
            return Ok(Ready::Fd);
        }
    }
}

fn accept_loop(listener: OwnedFd, stop: OwnedFd, shared: &Shared) {
    loop {
        match wait(listener.as_raw_fd(), libc::POLLIN, stop.as_raw_fd(), None) {
            Ok(Ready::Fd) => {}
            Ok(_) | Err(_) => return,
        }
        let fd = unsafe {
            libc::accept4(
                listener.as_raw_fd(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                libc::SOCK_CLOEXEC | libc::SOCK_NONBLOCK,
            )
        };
        if fd < 0 {
            let e = io::Error::last_os_error();
            match e.raw_os_error() {
                Some(libc::EAGAIN | libc::EINTR | libc::ECONNABORTED | libc::EPROTO) => continue,
                // Out of descriptors and the like: stop, as the C# broker stopped accepting.
                _ => return,
            }
        }
        let client = unsafe { OwnedFd::from_raw_fd(fd) };
        let mut conn = Client {
            fd: client,
            stop: stop.as_raw_fd(),
        };
        let pid = peer_pid(conn.fd.as_raw_fd());
        serve(&mut conn, pid, shared);
        // Dropping the connection closes it; the answer stays readable on the other end.
    }
}

/// The pid of the process that connected, from the kernel.
fn peer_pid(fd: RawFd) -> Option<u32> {
    let mut cred: libc::ucred = unsafe { mem::zeroed() };
    let mut len = mem::size_of::<libc::ucred>() as libc::socklen_t;
    let rc = unsafe {
        libc::getsockopt(
            fd,
            libc::SOL_SOCKET,
            libc::SO_PEERCRED,
            (&raw mut cred).cast(),
            &mut len,
        )
    };
    if rc != 0 || (len as usize) < mem::size_of::<libc::pid_t>() || cred.pid <= 0 {
        return None;
    }
    Some(cred.pid as u32)
}

struct Client {
    fd: OwnedFd,
    stop: RawFd,
}

fn stopped_or_timed_out(ready: Ready) -> io::Error {
    match ready {
        Ready::Stop => io::Error::new(io::ErrorKind::Interrupted, "the broker is stopping"),
        _ => io::Error::new(io::ErrorKind::TimedOut, "the client took too long"),
    }
}

impl Connection for Client {
    fn read(&mut self, buf: &mut [u8], deadline: Instant) -> io::Result<usize> {
        loop {
            let n = unsafe { libc::recv(self.fd.as_raw_fd(), buf.as_mut_ptr().cast(), buf.len(), 0) };
            if n >= 0 {
                return Ok(n as usize);
            }
            let e = io::Error::last_os_error();
            match e.kind() {
                io::ErrorKind::Interrupted => continue,
                io::ErrorKind::WouldBlock => {
                    match wait(self.fd.as_raw_fd(), libc::POLLIN, self.stop, Some(deadline))? {
                        Ready::Fd => continue,
                        other => return Err(stopped_or_timed_out(other)),
                    }
                }
                _ => return Err(e),
            }
        }
    }

    fn write_all(&mut self, mut buf: &[u8], deadline: Instant) -> io::Result<()> {
        while !buf.is_empty() {
            // MSG_NOSIGNAL: a client that hung up must not take the host down with SIGPIPE.
            let n = unsafe { libc::send(self.fd.as_raw_fd(), buf.as_ptr().cast(), buf.len(), libc::MSG_NOSIGNAL) };
            if n >= 0 {
                buf = &buf[n as usize..];
                continue;
            }
            let e = io::Error::last_os_error();
            match e.kind() {
                io::ErrorKind::Interrupted => continue,
                io::ErrorKind::WouldBlock => match wait(self.fd.as_raw_fd(), libc::POLLOUT, self.stop, Some(deadline))?
                {
                    Ready::Fd => continue,
                    other => return Err(stopped_or_timed_out(other)),
                },
                _ => return Err(e),
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::super::{Broker, MAGIC, Signer};
    use super::*;
    use crate::proof;
    use crate::split_ecdsa::{self, Shares};
    use std::io::{Read, Write};
    use std::os::unix::net::UnixStream;
    use std::time::Duration;
    use uuid::Uuid;

    fn user() -> Uuid {
        Uuid::parse_str("11111111-2222-3333-4444-555555555555").unwrap()
    }

    fn challenge() -> [u8; 64] {
        let mut c = [0u8; 64];
        getrandom::fill(&mut c).unwrap();
        c
    }

    fn request(user: &Uuid, challenge: &[u8]) -> String {
        format!("{MAGIC}\n{user}\n{}\n", proof::to_base64url(challenge))
    }

    fn fixed(answer: &'static str) -> Signer {
        Arc::new(move |_, _| Some(answer.to_string()))
    }

    /// What the engine's LaunchBrokerClient does.
    fn ask(endpoint: &str, request: &str) -> io::Result<String> {
        let path = endpoint.strip_prefix("unix:").unwrap();
        let mut stream = UnixStream::connect(path)?;
        stream.set_read_timeout(Some(Duration::from_secs(5)))?;
        // A refusal may close before reading; the answer is still there to read.
        let _ = stream.write_all(request.as_bytes());
        let mut answer = Vec::new();
        let mut buf = [0u8; 4096];
        loop {
            match stream.read(&mut buf) {
                Ok(0) => return Ok("<closed>".into()),
                Ok(n) => {
                    answer.extend_from_slice(&buf[..n]);
                    if let Some(i) = answer.iter().position(|&b| b == b'\n') {
                        return Ok(String::from_utf8(answer[..i].to_vec()).unwrap());
                    }
                }
                Err(e) if e.kind() == io::ErrorKind::ConnectionReset && !answer.is_empty() => {
                    return Ok(String::from_utf8(answer).unwrap());
                }
                Err(e) => return Err(e),
            }
        }
    }

    #[test]
    fn the_game_gets_a_proof_for_its_challenge() {
        use p256::ecdsa::signature::Verifier;
        use p256::ecdsa::{Signature, VerifyingKey};
        use p256::pkcs8::DecodePublicKey;

        let d = crate::build_inputs::sha256(b"broker test key");
        let shares = Arc::new(Shares::from_bytes(&split_ecdsa::split(&d, 3)));
        let key = VerifyingKey::from_public_key_der(&split_ecdsa::public_key_spki(&shares)).unwrap();
        let signing = shares.clone();
        let signer: Signer =
            Arc::new(move |user, challenge| Some(proof::token(&signing, &proof::payload_v2(user, challenge, "1.0.0"))));

        let broker = Broker::start(user(), signer).unwrap();
        broker.admit(std::process::id()); // this test process plays the game

        let c = challenge();
        let token = ask(broker.endpoint(), &request(&user(), &c)).unwrap();
        assert_eq!(broker.signed(), 1);
        let (payload, signature) = proof::split_token(&token).unwrap();
        assert_eq!(
            String::from_utf8(payload.clone()).unwrap(),
            format!("dh-launch/2\n{}\n{}\n1.0.0", user(), proof::to_base64url(&c))
        );
        assert!(
            key.verify(&payload, &Signature::from_slice(&signature).unwrap())
                .is_ok()
        );

        // Every login asks again and gets its own.
        let second = ask(broker.endpoint(), &request(&user(), &challenge())).unwrap();
        assert_ne!(token, second);
        assert_eq!(broker.signed(), 2);
    }

    #[test]
    fn other_processes_are_refused() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();

        // Before the game is known nobody is answered...
        assert_eq!(ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(), "");

        // ...and afterwards only the game is.
        broker.admit(std::process::id() + 1);
        assert_eq!(ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(), "");
        assert_eq!(broker.signed(), 0);
    }

    #[test]
    fn a_game_that_asks_before_it_is_admitted_is_answered() {
        let broker = Arc::new(Broker::start(user(), fixed("proof")).unwrap());
        let admitting = broker.clone();
        let admit = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(300));
            admitting.admit(std::process::id());
        });
        assert_eq!(
            ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
            "proof"
        );
        admit.join().unwrap();
    }

    #[test]
    fn an_exited_or_withdrawn_game_is_not_signed_for() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();

        // Connected as the game, but the game has exited by the time the request is in.
        broker.admit_child(std::process::id(), Arc::new(|| false));
        assert_eq!(ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(), "");
        broker.admit_child(std::process::id(), Arc::new(|| true));
        assert_eq!(
            ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
            "proof"
        );

        // Withdrawn: refused at once, not after waiting for an admission that will not come.
        broker.withdraw();
        let asked = Instant::now();
        assert_eq!(ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(), "");
        assert!(asked.elapsed() < Duration::from_secs(2));
        assert_eq!(broker.signed(), 1);
    }

    #[test]
    fn bad_requests_are_refused() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();
        broker.admit(std::process::id());

        for bad in [
            request(&Uuid::from_u128(7), &challenge()), // another user's login
            request(&user(), &challenge()).replace(MAGIC, "dh-launch-broker/0"),
            format!("{MAGIC}\n{}\n{}\n", user(), proof::to_base64url(&[0u8; 32])),
            "\n\n\n".to_string(),
        ] {
            assert_eq!(ask(broker.endpoint(), &bad).unwrap(), "", "{bad:?}");
        }
        assert_eq!(broker.signed(), 0);
    }

    #[test]
    fn an_oversized_or_silent_request_is_refused() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();
        broker.admit(std::process::id());
        assert_eq!(ask(broker.endpoint(), &"x".repeat(2000)).unwrap(), "");

        // A client that sends nothing is cut off with a refusal at the timeout, and the broker serves the next one.
        let path = broker.endpoint().strip_prefix("unix:").unwrap().to_string();
        let mut silent = UnixStream::connect(&path).unwrap();
        silent.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut answer = [0u8; 8];
        assert_eq!(silent.read(&mut answer).unwrap(), 1);
        assert_eq!(answer[0], b'\n');
        assert_eq!(
            ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
            "proof"
        );
    }

    #[test]
    fn stops_and_removes_its_socket() {
        let mut broker = Broker::start(user(), fixed("proof")).unwrap();
        broker.admit(std::process::id());
        assert_eq!(
            ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
            "proof"
        );

        let path = PathBuf::from(broker.endpoint().strip_prefix("unix:").unwrap());
        assert_eq!(fs::metadata(&path).unwrap().mode() & 0o777, 0o600);
        broker.stop();
        assert!(!path.exists());
        assert!(ask(broker.endpoint(), &request(&user(), &challenge())).is_err());
    }

    #[test]
    fn the_endpoint_has_the_agreed_name() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();
        let path = PathBuf::from(broker.endpoint().strip_prefix("unix:").unwrap());
        let name = path.file_name().unwrap().to_str().unwrap();
        let hex = name.strip_prefix("f15-launch-").unwrap().strip_suffix(".sock").unwrap();
        assert_eq!(hex.len(), 32);
        assert!(hex.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)));
    }

    #[test]
    fn a_private_directory_is_made_and_checked() {
        let base = tempfile::tempdir().unwrap();

        let fresh = base.path().join("fresh");
        ensure_private_dir(&fresh).unwrap();
        assert_eq!(fs::metadata(&fresh).unwrap().mode() & 0o777, 0o700);
        ensure_private_dir(&fresh).unwrap();

        let open = base.path().join("open");
        fs::create_dir(&open).unwrap();
        fs::set_permissions(&open, fs::Permissions::from_mode(0o755)).unwrap();
        assert!(ensure_private_dir(&open).is_err());

        let link = base.path().join("link");
        std::os::unix::fs::symlink(&fresh, &link).unwrap();
        assert!(ensure_private_dir(&link).is_err());

        let file = base.path().join("file");
        fs::write(&file, b"").unwrap();
        assert!(ensure_private_dir(&file).is_err());
    }

    #[test]
    fn a_squatted_fallback_directory_is_sidestepped() {
        let temp = tempfile::tempdir().unwrap();

        // The runtime directory when there is one.
        let (dir, own) = socket_directory_in(Some(temp.path().into()), Path::new("/nonexistent"), "u").unwrap();
        assert_eq!((dir.as_path(), own), (temp.path(), None));

        // Else the fixed name, when it is ours (made here, and kept).
        let (dir, own) = socket_directory_in(None, temp.path(), "alice").unwrap();
        assert_eq!((dir, own), (temp.path().join("f15-launch-alice"), None));

        // Someone opened it up (or made it first): a fresh private one instead, never the squatted one.
        let squatted = temp.path().join("f15-launch-bob");
        fs::create_dir(&squatted).unwrap();
        fs::set_permissions(&squatted, fs::Permissions::from_mode(0o755)).unwrap();
        let (dir, own) = socket_directory_in(None, temp.path(), "bob").unwrap();
        assert_eq!(own.as_deref(), Some(dir.as_path()));
        assert_ne!(dir, squatted);
        let name = dir.file_name().unwrap().to_str().unwrap();
        assert!(
            name.starts_with("f15-launch-bob-") && name.len() == "f15-launch-bob-".len() + 6,
            "{name}"
        );
        assert_eq!(fs::symlink_metadata(&dir).unwrap().mode() & 0o777, 0o700);
        let (again, _) = socket_directory_in(None, temp.path(), "bob").unwrap();
        assert_ne!(again, dir);

        // Nowhere to make one: an error, and the broker does not start.
        assert!(socket_directory_in(None, &temp.path().join("missing"), "carol").is_err());
    }
}
