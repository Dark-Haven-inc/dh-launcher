//! The broker on Windows: a local-only named pipe, owned by and open only to this user, answering the process
//! `GetNamedPipeClientProcessId` names.
//!
//! The game connects with `PipeOptions.CurrentUserOnly`, which refuses a pipe whose owner is not its token's default
//! owner (`WindowsIdentity.Owner`), so the pipe's descriptor names exactly that SID as owner and as the only one
//! allowed in: the user's SID normally, the Administrators group in an elevated session. It is what .NET's own
//! `CurrentUserOnly` server does.

use super::{Connection, Shared, serve};
use std::ffi::OsStr;
use std::io;
use std::mem;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr;
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::Instant;
use windows_sys::Win32::Foundation::{
    ERROR_BROKEN_PIPE, ERROR_IO_PENDING, ERROR_PIPE_CONNECTED, GetLastError, HANDLE, INVALID_HANDLE_VALUE, LocalFree,
    WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES, TOKEN_OWNER, TOKEN_QUERY, TokenOwner,
};
use windows_sys::Win32::Storage::FileSystem::{
    FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX, ReadFile, WriteFile,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, GetNamedPipeClientProcessId, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS,
    PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, GetCurrentProcess, INFINITE, OpenProcessToken, SetEvent, WaitForMultipleObjects, WaitForSingleObject,
};

const BUFFER_SIZE: u32 = 4096;

pub(crate) struct Server {
    stop: Arc<OwnedHandle>,
    thread: JoinHandle<()>,
}

impl Server {
    pub(crate) fn start(name: &str, shared: Arc<Shared>) -> io::Result<(String, Server)> {
        let descriptor = SecurityDescriptor::for_token_owner()?;
        let path = wide(&format!(r"\\.\pipe\{name}"));
        // The first instance claims the name: if anyone already holds it, this fails rather than joining theirs.
        let first = create_instance(&path, &descriptor, true)?;
        let stop = Arc::new(event()?);

        let thread_stop = stop.clone();
        let thread = std::thread::Builder::new()
            .name("dh-guard-broker".into())
            .spawn(move || serve_pipe(&path, &descriptor, first, &thread_stop, &shared))?;
        Ok((format!("pipe:{name}"), Server { stop, thread }))
    }

    pub(crate) fn stop(self) {
        // Wakes the thread from a pending ConnectNamedPipe or read; it cancels the I/O and returns.
        unsafe { SetEvent(self.stop.as_raw_handle() as HANDLE) };
        let _ = self.thread.join();
    }
}

fn serve_pipe(path: &[u16], descriptor: &SecurityDescriptor, first: OwnedHandle, stop: &OwnedHandle, shared: &Shared) {
    let mut pipe = first;
    let mut failures = 0u32;
    loop {
        // A client already queued on the next instance must not keep a stopped broker serving.
        if stopped(stop, 0) {
            return;
        }
        // A failure here belongs to this instance alone (ERROR_NO_DATA: a client opened it and left before this call;
        // or a broken connection): it is replaced like a served one, and the broker carries on.
        let connected = match connect(&pipe, stop) {
            Ok(true) => true,
            Ok(false) => return,
            Err(_) => false,
        };

        // The next instance exists before this one closes, so the name never lapses for someone else to take.
        let next = create_instance(path, descriptor, false);
        if connected {
            failures = 0;
            serve_client(&pipe, stop, shared);
        }
        // Closing (not DisconnectNamedPipe) leaves the answer readable on the client's end.
        drop(pipe);

        match next {
            Ok(instance) => pipe = instance,
            // Nothing left listening under the name: this is the end of the broker.
            Err(_) => return,
        }
        if !connected {
            // Back off a little if instances keep failing, without missing a stop.
            failures = failures.saturating_add(1);
            if stopped(stop, (10u32 << failures.min(7)).min(1000)) {
                return;
            }
        }
    }
}

/// Whether the stop event is set, waiting up to `timeout_ms` for it.
fn stopped(stop: &OwnedHandle, timeout_ms: u32) -> bool {
    let woke = unsafe { WaitForSingleObject(raw(stop), timeout_ms) };
    woke == WAIT_OBJECT_0
}

/// Answers the client connected to `pipe`; any failure is this connection's alone.
fn serve_client(pipe: &OwnedHandle, stop: &OwnedHandle, shared: &Shared) {
    let mut pid = 0u32;
    let peer = if unsafe { GetNamedPipeClientProcessId(raw(pipe), &mut pid) } != 0 && pid != 0 {
        Some(pid)
    } else {
        None
    };
    if let Ok(event) = event() {
        let mut conn = PipeClient { pipe, event, stop };
        serve(&mut conn, peer, shared);
    }
}

fn raw(handle: &OwnedHandle) -> HANDLE {
    handle.as_raw_handle() as HANDLE
}

fn wide(text: &str) -> Vec<u16> {
    OsStr::new(text).encode_wide().chain(Some(0)).collect()
}

fn last_error() -> io::Error {
    io::Error::last_os_error()
}

fn event() -> io::Result<OwnedHandle> {
    // Manual reset: ReadFile, WriteFile and ConnectNamedPipe reset it when they start.
    let handle = unsafe { CreateEventW(ptr::null(), 1, 0, ptr::null()) };
    if handle.is_null() {
        return Err(last_error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(handle as _) })
}

fn create_instance(path: &[u16], descriptor: &SecurityDescriptor, first: bool) -> io::Result<OwnedHandle> {
    let attributes = SECURITY_ATTRIBUTES {
        nLength: mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: descriptor.0,
        bInheritHandle: 0,
    };
    let open_mode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | if first { FILE_FLAG_FIRST_PIPE_INSTANCE } else { 0 };
    let pipe_mode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
    let handle = unsafe {
        CreateNamedPipeW(
            path.as_ptr(),
            open_mode,
            pipe_mode,
            PIPE_UNLIMITED_INSTANCES,
            BUFFER_SIZE,
            BUFFER_SIZE,
            0,
            &attributes,
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(handle as _) })
}

/// Waits for a client. Ok(false) when stopped first.
fn connect(pipe: &OwnedHandle, stop: &OwnedHandle) -> io::Result<bool> {
    let event = event()?;
    let mut overlapped: OVERLAPPED = unsafe { mem::zeroed() };
    overlapped.hEvent = raw(&event);
    if unsafe { ConnectNamedPipe(raw(pipe), &mut overlapped) } != 0 {
        return Ok(true);
    }
    match unsafe { GetLastError() } {
        // A client got in between creating the instance and this call.
        ERROR_PIPE_CONNECTED => Ok(true),
        ERROR_IO_PENDING => match complete(pipe, &overlapped, &event, stop, None) {
            Ok(_) => Ok(true),
            Err(e) if e.kind() == io::ErrorKind::Interrupted => Ok(false),
            Err(e) => Err(e),
        },
        code => Err(io::Error::from_raw_os_error(code as i32)),
    }
}

/// Waits for the overlapped operation to finish, the broker to stop, or the deadline. It never returns while the
/// operation could still write into `overlapped` or its buffer: a stopped or late one is cancelled and waited out.
fn complete(
    pipe: &OwnedHandle,
    overlapped: &OVERLAPPED,
    event: &OwnedHandle,
    stop: &OwnedHandle,
    deadline: Option<Instant>,
) -> io::Result<u32> {
    let timeout = match deadline {
        None => INFINITE,
        Some(deadline) => deadline
            .saturating_duration_since(Instant::now())
            .as_millis()
            .min(u128::from(INFINITE - 1)) as u32,
    };
    let handles = [raw(event), raw(stop)];
    let woke = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, timeout) };

    let mut transferred = 0u32;
    if woke == WAIT_OBJECT_0 {
        if unsafe { GetOverlappedResult(raw(pipe), overlapped, &mut transferred, 0) } == 0 {
            return Err(last_error());
        }
        return Ok(transferred);
    }

    unsafe {
        CancelIoEx(raw(pipe), overlapped);
        GetOverlappedResult(raw(pipe), overlapped, &mut transferred, 1);
    }
    Err(match woke {
        w if w == WAIT_OBJECT_0 + 1 => io::Error::new(io::ErrorKind::Interrupted, "the broker is stopping"),
        WAIT_TIMEOUT => io::Error::new(io::ErrorKind::TimedOut, "the client took too long"),
        _ => last_error(),
    })
}

struct PipeClient<'a> {
    pipe: &'a OwnedHandle,
    event: OwnedHandle,
    stop: &'a OwnedHandle,
}

impl PipeClient<'_> {
    fn is_broken(e: &io::Error) -> bool {
        e.raw_os_error() == Some(ERROR_BROKEN_PIPE as i32)
    }
}

impl Connection for PipeClient<'_> {
    fn read(&mut self, buf: &mut [u8], deadline: Instant) -> io::Result<usize> {
        let len = buf.len().min(u32::MAX as usize) as u32;
        let mut overlapped: OVERLAPPED = unsafe { mem::zeroed() };
        overlapped.hEvent = raw(&self.event);
        let ok = unsafe { ReadFile(raw(self.pipe), buf.as_mut_ptr(), len, ptr::null_mut(), &mut overlapped) };
        if ok == 0 {
            match unsafe { GetLastError() } {
                ERROR_IO_PENDING => {}
                ERROR_BROKEN_PIPE => return Ok(0),
                code => return Err(io::Error::from_raw_os_error(code as i32)),
            }
        }
        match complete(self.pipe, &overlapped, &self.event, self.stop, Some(deadline)) {
            Ok(n) => Ok(n as usize),
            Err(e) if Self::is_broken(&e) => Ok(0),
            Err(e) => Err(e),
        }
    }

    fn write_all(&mut self, mut buf: &[u8], deadline: Instant) -> io::Result<()> {
        while !buf.is_empty() {
            let len = buf.len().min(u32::MAX as usize) as u32;
            let mut overlapped: OVERLAPPED = unsafe { mem::zeroed() };
            overlapped.hEvent = raw(&self.event);
            let ok = unsafe { WriteFile(raw(self.pipe), buf.as_ptr(), len, ptr::null_mut(), &mut overlapped) };
            if ok == 0 {
                match unsafe { GetLastError() } {
                    ERROR_IO_PENDING => {}
                    code => return Err(io::Error::from_raw_os_error(code as i32)),
                }
            }
            let written = complete(self.pipe, &overlapped, &self.event, self.stop, Some(deadline))?;
            if written == 0 {
                return Err(io::Error::new(io::ErrorKind::WriteZero, "the pipe took nothing"));
            }
            buf = &buf[written as usize..];
        }
        Ok(())
    }
}

/// A self-relative security descriptor from `ConvertStringSecurityDescriptorToSecurityDescriptorW`.
struct SecurityDescriptor(PSECURITY_DESCRIPTOR);

// SAFETY: it is plain memory owned by this value; nothing else refers to it.
unsafe impl Send for SecurityDescriptor {}

impl Drop for SecurityDescriptor {
    fn drop(&mut self) {
        unsafe { LocalFree(self.0) };
    }
}

impl SecurityDescriptor {
    /// Owner and sole grantee: this process token's default owner.
    fn for_token_owner() -> io::Result<SecurityDescriptor> {
        let sid = token_owner_sid()?;
        let sddl = wide(&format!("O:{sid}D:P(A;;FA;;;{sid})"));
        let mut descriptor: PSECURITY_DESCRIPTOR = ptr::null_mut();
        let ok = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(last_error());
        }
        Ok(SecurityDescriptor(descriptor))
    }
}

/// The token's default owner SID as a string (`S-1-5-21-...`), what `WindowsIdentity.GetCurrent().Owner` is.
fn token_owner_sid() -> io::Result<String> {
    let mut token: HANDLE = ptr::null_mut();
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(last_error());
    }
    let token = unsafe { OwnedHandle::from_raw_handle(token as _) };

    let mut length = 0u32;
    unsafe { GetTokenInformation(raw(&token), TokenOwner, ptr::null_mut(), 0, &mut length) };
    if length == 0 {
        return Err(last_error());
    }
    // u64 elements keep TOKEN_OWNER's pointer aligned.
    let mut buffer = vec![0u64; (length as usize).div_ceil(8)];
    if unsafe { GetTokenInformation(raw(&token), TokenOwner, buffer.as_mut_ptr().cast(), length, &mut length) } == 0 {
        return Err(last_error());
    }
    let owner = unsafe { (*(buffer.as_ptr() as *const TOKEN_OWNER)).Owner };
    sid_string(owner)
}

/// A SID as a string (`S-1-5-21-...`).
fn sid_string(sid: PSID) -> io::Result<String> {
    let mut text: *mut u16 = ptr::null_mut();
    if unsafe { ConvertSidToStringSidW(sid, &mut text) } == 0 {
        return Err(last_error());
    }
    let mut len = 0;
    while unsafe { *text.add(len) } != 0 {
        len += 1;
    }
    let sid = String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(text, len) });
    unsafe { LocalFree(text.cast()) };
    Ok(sid)
}

#[cfg(test)]
mod tests {
    use super::super::{Broker, MAGIC, Signer};
    use super::*;
    use crate::proof;
    use crate::split_ecdsa::{self, Shares};
    use std::time::Duration;
    use uuid::Uuid;
    use windows_sys::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY, GENERIC_READ, GENERIC_WRITE};
    use windows_sys::Win32::Security::Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT};
    use windows_sys::Win32::Security::OWNER_SECURITY_INFORMATION;
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, OPEN_EXISTING};
    use windows_sys::Win32::System::Pipes::WaitNamedPipeW;

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

    fn pipe_path(endpoint: &str) -> Vec<u16> {
        wide(&format!(r"\\.\pipe\{}", endpoint.strip_prefix("pipe:").unwrap()))
    }

    /// The owner of a pipe's security descriptor, as a string SID.
    fn pipe_owner(pipe: &OwnedHandle) -> io::Result<String> {
        let mut owner: PSID = ptr::null_mut();
        let mut descriptor: PSECURITY_DESCRIPTOR = ptr::null_mut();
        let rc = unsafe {
            GetSecurityInfo(
                raw(pipe),
                SE_KERNEL_OBJECT,
                OWNER_SECURITY_INFORMATION,
                &mut owner,
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                &mut descriptor,
            )
        };
        if rc != 0 {
            return Err(io::Error::from_raw_os_error(rc as i32));
        }
        let sid = sid_string(owner);
        unsafe { LocalFree(descriptor) };
        sid
    }

    /// Opens the client end the way .NET's `NamedPipeClientStream` with `PipeOptions.CurrentUserOnly` does (what the
    /// engine's LaunchBrokerClient uses): retried while the name is missing or every instance is busy, and refused
    /// unless the pipe's owner is this token's default owner.
    fn open(endpoint: &str, timeout: Duration) -> io::Result<OwnedHandle> {
        let path = pipe_path(endpoint);
        let deadline = Instant::now() + timeout;
        let pipe = loop {
            let handle = unsafe {
                CreateFileW(
                    path.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    ptr::null(),
                    OPEN_EXISTING,
                    0,
                    ptr::null_mut(),
                )
            };
            if handle != INVALID_HANDLE_VALUE {
                break unsafe { OwnedHandle::from_raw_handle(handle as _) };
            }
            let e = last_error();
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return Err(e);
            }
            match e.raw_os_error() {
                Some(code) if code == ERROR_PIPE_BUSY as i32 => unsafe {
                    WaitNamedPipeW(path.as_ptr(), left.as_millis().clamp(1, 60_000) as u32);
                },
                Some(code) if code == ERROR_FILE_NOT_FOUND as i32 => std::thread::sleep(Duration::from_millis(10)),
                _ => return Err(e),
            }
        };
        // CurrentUserOnly's check.
        let owner = pipe_owner(&pipe)?;
        if owner != token_owner_sid()? {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                format!("the pipe is owned by {owner}"),
            ));
        }
        Ok(pipe)
    }

    fn write(pipe: &OwnedHandle, mut data: &[u8]) -> io::Result<()> {
        while !data.is_empty() {
            let mut written = 0u32;
            let ok = unsafe {
                WriteFile(
                    raw(pipe),
                    data.as_ptr(),
                    data.len() as u32,
                    &mut written,
                    ptr::null_mut(),
                )
            };
            if ok == 0 {
                return Err(last_error());
            }
            data = &data[written as usize..];
        }
        Ok(())
    }

    /// What the engine's LaunchBrokerClient does: the first line of the answer, or "<closed>".
    fn answer(pipe: &OwnedHandle) -> io::Result<String> {
        let mut answer = Vec::new();
        let mut buf = [0u8; 4096];
        loop {
            let mut read = 0u32;
            let ok = unsafe {
                ReadFile(
                    raw(pipe),
                    buf.as_mut_ptr(),
                    buf.len() as u32,
                    &mut read,
                    ptr::null_mut(),
                )
            };
            if ok == 0 {
                let e = last_error();
                if e.raw_os_error() == Some(ERROR_BROKEN_PIPE as i32) {
                    return Ok("<closed>".into());
                }
                return Err(e);
            }
            if read == 0 {
                return Ok("<closed>".into());
            }
            answer.extend_from_slice(&buf[..read as usize]);
            if let Some(i) = answer.iter().position(|&b| b == b'\n') {
                return Ok(String::from_utf8(answer[..i].to_vec()).unwrap());
            }
        }
    }

    fn ask(endpoint: &str, request: &str) -> io::Result<String> {
        let pipe = open(endpoint, Duration::from_secs(5))?;
        // A refusal may close before reading; the answer is still there to read.
        let _ = write(&pipe, request.as_bytes());
        answer(&pipe)
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
    fn the_pipe_belongs_to_the_token_owner_and_its_name_to_the_broker() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();
        // What CurrentUserOnly compares with WindowsIdentity.Owner (open() refuses anything else).
        let pipe = open(broker.endpoint(), Duration::from_secs(5)).unwrap();
        assert_eq!(pipe_owner(&pipe).unwrap(), token_owner_sid().unwrap());
        drop(pipe);

        // Nobody can claim the name as a first instance while the broker holds it.
        let descriptor = SecurityDescriptor::for_token_owner().unwrap();
        assert!(create_instance(&pipe_path(broker.endpoint()), &descriptor, true).is_err());
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
    fn a_client_that_leaves_early_does_not_end_the_broker() {
        let broker = Broker::start(user(), fixed("proof")).unwrap();
        broker.admit(std::process::id());

        // One client is being served (the broker waits for its request) while another opens the next instance and
        // leaves before the broker gets to it: ConnectNamedPipe on that instance fails with ERROR_NO_DATA.
        let served = open(broker.endpoint(), Duration::from_secs(5)).unwrap();
        let early = open(broker.endpoint(), Duration::from_secs(5)).unwrap();
        drop(early);
        write(&served, request(&user(), &challenge()).as_bytes()).unwrap();
        assert_eq!(answer(&served).unwrap(), "proof");

        // Still listening, under the same name.
        for _ in 0..3 {
            assert_eq!(
                ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
                "proof"
            );
        }
        assert_eq!(broker.signed(), 4);
    }

    #[test]
    fn stops_promptly_and_its_name_disappears() {
        let mut broker = Broker::start(user(), fixed("proof")).unwrap();
        broker.admit(std::process::id());
        assert_eq!(
            ask(broker.endpoint(), &request(&user(), &challenge())).unwrap(),
            "proof"
        );

        // The thread is waiting in ConnectNamedPipe; stopping wakes it.
        let stopping = Instant::now();
        broker.stop();
        assert!(stopping.elapsed() < Duration::from_secs(2), "{:?}", stopping.elapsed());

        let err = open(broker.endpoint(), Duration::from_millis(500)).unwrap_err();
        assert_eq!(err.raw_os_error(), Some(ERROR_FILE_NOT_FOUND as i32), "{err}");
    }
}
