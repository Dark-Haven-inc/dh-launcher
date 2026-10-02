//! Platforms with no way to tell who is on the other end of a local connection: no broker, so no proofs.

use super::Shared;
use std::io;
use std::sync::Arc;

pub(crate) struct Server;

impl Server {
    pub(crate) fn start(_name: &str, _shared: Arc<Shared>) -> io::Result<(String, Server)> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "the launch broker needs Windows or Linux",
        ))
    }

    pub(crate) fn stop(self) {}
}
