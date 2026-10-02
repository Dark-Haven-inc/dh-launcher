using System.Text;
using DarkHaven.Launcher.Security;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// The game client the guard started (<see cref="Guard"/>), in place of a <see cref="System.Diagnostics.Process"/>:
/// the launcher never starts the loader itself any more, so it gets the guard's handle instead. It behaves the way the
/// launcher used <c>Process</c>: <see cref="WaitForExitAsync"/> also waits for redirected output to drain, and
/// <see cref="ExitCode"/> is what <c>Process.ExitCode</c> reported.
/// </summary>
/// <remarks>
/// One thread waits on the guard for the game to exit; everything else reads what it saw. Disposing releases the
/// handle, not the game: it keeps running, and the guard's broker keeps answering it until it exits.
/// </remarks>
public sealed class GameProcess : IDisposable
{
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(500);

    private readonly GuardGameHandle _game;
    private readonly SafeFileHandle? _stdout;
    private readonly SafeFileHandle? _stderr;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _output;
    private int _readingOutput;
    private int _exitCode;
    private volatile bool _hasExited;
    private volatile bool _disposed;

    /// <summary>The loader's pid.</summary>
    public int Id { get; }

    /// <summary>Where the launch broker ended up; <see cref="GuardBrokerState.NotRequested"/> in a development build.</summary>
    public GuardBrokerState BrokerState { get; }

    public bool HasExited => _hasExited;

    /// <exception cref="InvalidOperationException">The game is still running.</exception>
    public int ExitCode => _hasExited ? _exitCode : throw new InvalidOperationException("The game has not exited");

    /// <summary>Whether stdout and stderr were redirected (see <see cref="BeginOutputReadLine"/>).</summary>
    public bool RedirectsOutput => _stdout is not null;

    /// <summary>Raised once, on a background thread, when the game has exited and output being read has drained.</summary>
    public event EventHandler? Exited;

    private GameProcess(GuardLaunch launch)
    {
        _game = launch.Game;
        _stdout = launch.Stdout;
        _stderr = launch.Stderr;
        Id = launch.Pid;
        var state = Guard.Native.GameBrokerState(_game);
        BrokerState = Enum.IsDefined((GuardBrokerState)state) ? (GuardBrokerState)state : GuardBrokerState.Failed;

        new Thread(WaitForGame) { IsBackground = true, Name = $"game {Id}" }.Start();
    }

    /// <summary>Has the guard start the game.</summary>
    /// <exception cref="GuardException">It refused or failed.</exception>
    public static GameProcess Start(GuardLaunchRequest request) => new(Guard.Launch(request));

    /// <summary>Proofs the broker has signed for this game so far.</summary>
    public int Signed
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var signed = Guard.Native.GameSigned(_game);
            return signed < 0 ? throw Guard.Failure(signed) : signed;
        }
    }

    /// <summary>
    /// Starts reading stdout and stderr line by line into <paramref name="onLine"/>, each on its own background thread
    /// (possibly at the same time), until the game and everything it started have closed them. Both must be read for
    /// the whole session: a full pipe stalls the game.
    /// </summary>
    public void BeginOutputReadLine(Action<string> onLine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stdout is null || _stderr is null)
            throw new InvalidOperationException("The game's output was not redirected");
        if (Interlocked.Exchange(ref _readingOutput, 1) != 0)
            throw new InvalidOperationException("The game's output is already being read");

        Volatile.Write(ref _output, Task.WhenAll(ReadLines(_stdout, "stdout", onLine), ReadLines(_stderr, "stderr", onLine)));
    }

    /// <summary>Completes when the game has exited and output being read has drained.</summary>
    public async Task WaitForExitAsync(CancellationToken cancel = default)
    {
        await _exited.Task.WaitAsync(cancel);
        if (Volatile.Read(ref _output) is { } output)
            await output.WaitAsync(cancel);
    }

    /// <summary>Terminates the game; nothing happens if it already exited.</summary>
    public void Kill()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasExited)
            return;
        var status = Guard.Native.GameKill(_game);
        if (status != 0)
            throw Guard.Failure(status);
    }

    private void WaitForGame()
    {
        var slice = (int)WaitSlice.TotalMilliseconds;
        while (!_disposed)
        {
            int status, code;
            try
            {
                status = Guard.Native.GameWait(_game, slice, out code);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (status == (int)GuardStatus.Timeout)
                continue;
            if (status != 0)
            {
                Log.Warning("Waiting for the game failed: {Error}", Guard.Failure(status).Message);
                code = -1;
            }

            _exitCode = code;
            _hasExited = true;
            _exited.TrySetResult();

            try
            {
                Volatile.Read(ref _output)?.Wait();
            }
            catch (AggregateException)
            {
                // The readers never fail; nothing to wait for.
            }

            Exited?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Disposed first: nobody will learn how it ended.
        _exited.TrySetCanceled();
    }

    private static Task ReadLines(SafeFileHandle handle, string name, Action<string> onLine)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: handle.IsAsync);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        onLine(line);
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "Game {Stream} line handler failed", name);
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Log.Debug(e, "Stopped reading the game's {Stream}", name);
            }
            finally
            {
                done.TrySetResult();
            }
        }) { IsBackground = true, Name = $"game {name}" }.Start();
        return done.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // A wait in progress holds the handle until its slice ends; the release follows it.
        _game.Dispose();

        // Output being read belongs to its readers, which close it at the end of the stream.
        if (Volatile.Read(ref _readingOutput) == 0)
        {
            _stdout?.Dispose();
            _stderr?.Dispose();
        }
    }
}
