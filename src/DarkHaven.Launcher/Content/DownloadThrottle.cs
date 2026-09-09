namespace DarkHaven.Launcher.Content;

/// <summary>
/// Optional global cap on download speed (content + engine). Set once from the
/// <c>DownloadLimitKbps</c> setting; 0 means unlimited.
/// </summary>
public static class DownloadThrottle
{
    /// <summary>Bytes per second. 0 = unlimited.</summary>
    public static long LimitBytesPerSecond;

    public static void SetKbps(int kbps) => LimitBytesPerSecond = kbps > 0 ? kbps * 1000L : 0;

    /// <summary>Wraps a read stream so it can't exceed the configured rate. No-op when unlimited.</summary>
    public static Stream Wrap(Stream inner)
        => LimitBytesPerSecond > 0 ? new ThrottledReadStream(inner, LimitBytesPerSecond) : inner;

    private sealed class ThrottledReadStream(Stream inner, long bytesPerSecond) : Stream
    {
        private readonly long _bps = Math.Max(1, bytesPerSecond);
        private long _windowBytes;
        private long _windowStartTicks = Environment.TickCount64;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await GateAsync(cancellationToken);
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Account(read);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            GateAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        private async ValueTask GateAsync(CancellationToken cancel)
        {
            var elapsed = Environment.TickCount64 - _windowStartTicks;
            if (elapsed >= 1000)
            {
                _windowStartTicks = Environment.TickCount64;
                _windowBytes = 0;
                return;
            }

            if (_windowBytes >= _bps)
                await Task.Delay((int)(1000 - elapsed), cancel);
        }

        private void Account(int read)
        {
            if (Environment.TickCount64 - _windowStartTicks >= 1000)
            {
                _windowStartTicks = Environment.TickCount64;
                _windowBytes = 0;
            }
            _windowBytes += read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
