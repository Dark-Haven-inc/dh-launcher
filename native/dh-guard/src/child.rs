//! Watching the game process on Unix without ever acting on a pid that may have been reused.
//!
//! A pid stays the child's only until the child is reaped; after that the kernel can hand it to anyone. So the waiter
//! waits for the exit without reaping (`waitid(WNOWAIT)`), withdraws the broker's admission, and reaps only then; a
//! signature is only made while the child has not exited; and a kill never goes out after the reap. Where the kernel
//! has pidfds (Linux 5.3+ for signalling, 5.4+ for waiting on one) they name the process itself, which also holds
//! when something else in the host (it is the child's parent) reaps it behind our back. Without them the checks go by
//! pid, and only a reap by someone else between a check and the signal can slip through.
//!
//! No SIGCHLD handler is installed: the .NET host owns SIGCHLD.

use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, MutexGuard};

pub(crate) struct ChildWatch {
    pid: libc::pid_t,
    /// A pidfd for the child, when the kernel can both signal and wait on one.
    pidfd: Option<OwnedFd>,
    /// Set as soon as the child is known to have exited; it may not be reaped yet.
    exited: AtomicBool,
    /// Held while signalling and while reaping, so the two never overlap.
    reaping: Mutex<()>,
}

impl ChildWatch {
    /// Watches `pid`, which must be a child of this process that nothing has waited for yet. Call it right after the
    /// spawn.
    pub(crate) fn new(pid: u32) -> ChildWatch {
        let pid = pid as libc::pid_t;
        let pidfd = open_pidfd(pid);
        ChildWatch {
            pid,
            pidfd,
            exited: AtomicBool::new(false),
            reaping: Mutex::new(()),
        }
    }

    /// Blocks until the child has exited (or is gone, reaped by someone else), without reaping it.
    pub(crate) fn wait_exited(&self) {
        loop {
            let (idtype, id) = self.target();
            let mut info: libc::siginfo_t = unsafe { std::mem::zeroed() };
            let rc = unsafe { libc::waitid(idtype, id, &mut info, libc::WEXITED | libc::WNOWAIT) };
            if rc == 0 {
                break;
            }
            let e = io::Error::last_os_error();
            if e.kind() == io::ErrorKind::Interrupted {
                continue;
            }
            // ECHILD: someone else reaped it. Anything else: nothing more to learn by waiting. Either way the pid is
            // no longer to be trusted.
            break;
        }
        self.exited.store(true, Ordering::SeqCst);
    }

    /// Whether the child has exited (reaped or not). Never blocks.
    pub(crate) fn has_exited(&self) -> bool {
        if self.exited.load(Ordering::SeqCst) {
            return true;
        }
        let (idtype, id) = self.target();
        // A zeroed siginfo stays zeroed when the child is still running (WNOHANG).
        let mut info: libc::siginfo_t = unsafe { std::mem::zeroed() };
        let rc = unsafe { libc::waitid(idtype, id, &mut info, libc::WEXITED | libc::WNOHANG | libc::WNOWAIT) };
        // An error is ECHILD (gone): as good as exited.
        rc != 0 || info.si_signo != 0
    }

    /// SIGKILL, unless the child has exited already. Never signals a reaped child's pid.
    pub(crate) fn kill(&self) -> io::Result<()> {
        let _reaping = self.lock();
        if self.has_exited() {
            return Ok(());
        }
        let rc = match &self.pidfd {
            Some(fd) => unsafe {
                libc::syscall(
                    libc::SYS_pidfd_send_signal,
                    fd.as_raw_fd(),
                    libc::SIGKILL,
                    std::ptr::null::<libc::siginfo_t>(),
                    0,
                ) as libc::c_int
            },
            // Checked unreaped just above; only a reap by someone else in between could make this miss.
            None => unsafe { libc::kill(self.pid, libc::SIGKILL) },
        };
        if rc == 0 {
            return Ok(());
        }
        let e = io::Error::last_os_error();
        match e.raw_os_error() {
            // It exited meanwhile.
            Some(libc::ESRCH) => Ok(()),
            _ => Err(e),
        }
    }

    /// Runs `reap` (which reaps the child) with signalling locked out. Call it only after [`ChildWatch::wait_exited`].
    pub(crate) fn reap<T>(&self, reap: impl FnOnce() -> T) -> T {
        let _reaping = self.lock();
        reap()
    }

    fn lock(&self) -> MutexGuard<'_, ()> {
        self.reaping.lock().unwrap_or_else(|p| p.into_inner())
    }

    fn target(&self) -> (libc::idtype_t, libc::id_t) {
        match &self.pidfd {
            Some(fd) => (P_PIDFD, fd.as_raw_fd() as libc::id_t),
            None => (libc::P_PID, self.pid as libc::id_t),
        }
    }
}

#[cfg(target_os = "linux")]
const P_PIDFD: libc::idtype_t = libc::P_PIDFD;
// Never used: there are no pidfds off Linux.
#[cfg(not(target_os = "linux"))]
const P_PIDFD: libc::idtype_t = libc::P_PID;

/// A pidfd for our child `pid`, if the kernel can open one and wait on it; None falls back to the pid.
#[cfg(target_os = "linux")]
fn open_pidfd(pid: libc::pid_t) -> Option<OwnedFd> {
    // Close-on-exec by default.
    let fd = unsafe { libc::syscall(libc::SYS_pidfd_open, pid, 0) } as libc::c_int;
    if fd < 0 {
        return None;
    }
    let fd = unsafe { OwnedFd::from_raw_fd(fd) };
    // It must name our child: waitid on it works only then (ECHILD if the pid had already been reaped by someone else
    // and reused), and only on kernels that can wait on pidfds at all (EINVAL before 5.4).
    let mut info: libc::siginfo_t = unsafe { std::mem::zeroed() };
    let rc = unsafe {
        libc::waitid(
            libc::P_PIDFD,
            fd.as_raw_fd() as libc::id_t,
            &mut info,
            libc::WEXITED | libc::WNOHANG | libc::WNOWAIT,
        )
    };
    (rc == 0).then_some(fd)
}

#[cfg(not(target_os = "linux"))]
fn open_pidfd(_pid: libc::pid_t) -> Option<OwnedFd> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;
    use std::time::Duration;

    #[test]
    fn sees_the_exit_without_reaping() {
        let mut child = Command::new("/bin/sh").args(["-c", "exit 3"]).spawn().unwrap();
        let watch = ChildWatch::new(child.id());
        watch.wait_exited();
        assert!(watch.has_exited());
        // Still ours to reap, with its status.
        assert_eq!(watch.reap(|| child.wait()).unwrap().code(), Some(3));
        // And nothing is signalled afterwards.
        watch.kill().unwrap();
    }

    #[test]
    fn kills_a_running_child() {
        let mut child = Command::new("/bin/sh").args(["-c", "sleep 60"]).spawn().unwrap();
        let watch = ChildWatch::new(child.id());
        assert!(!watch.has_exited());
        watch.kill().unwrap();
        watch.wait_exited();
        let status = watch.reap(|| child.wait()).unwrap();
        use std::os::unix::process::ExitStatusExt;
        assert_eq!(status.signal(), Some(libc::SIGKILL));
    }

    #[test]
    fn a_child_reaped_elsewhere_counts_as_exited_and_is_not_signalled() {
        let mut child = Command::new("/bin/sh").args(["-c", "exit 0"]).spawn().unwrap();
        let watch = ChildWatch::new(child.id());
        // Someone else (the host) reaps it.
        child.wait().unwrap();
        std::thread::sleep(Duration::from_millis(10));
        assert!(watch.has_exited());
        watch.kill().unwrap();
        watch.wait_exited();
    }
}
