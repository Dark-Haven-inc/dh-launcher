using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace DarkHaven.Launcher.Security;

/// <summary>
/// Signs launch proofs for the game this launcher started, one per login, when the game asks mid-handshake. Nothing
/// long-lived is handed to the game: a proof is bound to the server's nonce and the session's auth hash (see
/// <see cref="LaunchProof"/>), so lifting one from the process is worthless.
/// </summary>
/// <remarks>
/// <para>
/// The broker listens on a local endpoint named in <see cref="EnvVar"/> - a named pipe on Windows, a Unix socket in
/// <c>$XDG_RUNTIME_DIR</c> on Linux - and answers only the process <see cref="Admit"/> names, going by the kernel's
/// account of who is on the other end (<c>GetNamedPipeClientProcessId</c>, <c>SO_PEERCRED</c>), not anything the
/// caller says. It stops when disposed, which the launcher does when the game exits.
/// </para>
/// <para>
/// The exchange (the engine's <c>LaunchBrokerClient</c> is the other side):
/// <code>
/// request  = "dh-launch-broker/1\n" userId(guid, "D") "\n" base64url(challenge) "\n"
/// response = token "\n"        (an empty line if refused)
/// </code>
/// </para>
/// </remarks>
public sealed class LaunchBroker : IDisposable
{
    public const string EnvVar = "DH_LAUNCH_BROKER";
    public const string Magic = "dh-launch-broker/1";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxRequestLength = 1024;

    private readonly Guid _userId;
    private readonly Func<Guid, byte[], string?> _sign;
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket? _socket;
    private readonly string? _socketPath;
    private int _gamePid;

    /// <summary>What to put in <see cref="EnvVar"/> for the game.</summary>
    public string Endpoint { get; }

    /// <summary>Proofs signed so far.</summary>
    public int Signed { get; private set; }

    private LaunchBroker(Guid userId, Func<Guid, byte[], string?> sign, string endpoint, Socket? socket, string? socketPath)
    {
        _userId = userId;
        _sign = sign;
        Endpoint = endpoint;
        _socket = socket;
        _socketPath = socketPath;
    }

    /// <summary>
    /// Starts listening for <paramref name="userId"/>'s game, signing with <paramref name="sign"/>. Nobody is answered
    /// until <see cref="Admit"/> names the game process. Null where there is no way to tell who is calling.
    /// </summary>
    public static LaunchBroker? Start(Guid userId, Func<Guid, byte[], string?> sign)
    {
        var name = $"f15-launch-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

        if (OperatingSystem.IsWindows())
        {
            var broker = new LaunchBroker(userId, sign, $"pipe:{name}", null, null);
            _ = broker.ServePipeAsync(name);
            return broker;
        }

        if (OperatingSystem.IsLinux())
        {
            var path = Path.Combine(SocketDirectory(), $"{name}.sock");
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                socket.Listen(4);
            }
            catch
            {
                socket.Dispose();
                TryDelete(path);
                throw;
            }

            var broker = new LaunchBroker(userId, sign, $"unix:{path}", socket, path);
            _ = broker.ServeSocketAsync();
            return broker;
        }

        return null;
    }

    /// <summary>The game process; only it is answered from now on.</summary>
    public void Admit(int pid) => Volatile.Write(ref _gamePid, pid);

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
            return;

        _stop.Cancel();
        _socket?.Dispose();
        if (_socketPath != null)
            TryDelete(_socketPath);
    }

    [SupportedOSPlatform("linux")]
    private static string SocketDirectory()
    {
        // The per-user runtime dir is private to the user (0700) and cleared at logout.
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime))
            return runtime;

        var fallback = Path.Combine(Path.GetTempPath(), $"f15-launch-{Environment.UserName}");
        Directory.CreateDirectory(fallback, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return fallback;
    }

    private async Task ServeSocketAsync()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket!.AcceptAsync(stop);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (!stop.IsCancellationRequested)
                    Log.Warning(e, "Launch broker stopped accepting");
                return;
            }

            using (client)
            await using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                await ServeAsync(stream, PeerPid(client), stop);
            }
        }
    }

    private async Task ServePipeAsync(string name)
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // One instance at a time; CurrentUserOnly keeps other accounts on the machine out.
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, inBufferSize: 4096, outBufferSize: 4096);
                await pipe.WaitForConnectionAsync(stop);
                var pid = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) ? (int)clientPid : -1;
                await ServeAsync(pipe, pid, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning(e, "Launch broker stopped listening");
                return;
            }
        }
    }

    private async Task ServeAsync(Stream stream, int peerPid, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            string? proof = null;
            var gamePid = Volatile.Read(ref _gamePid);
            if (gamePid == 0 || peerPid != gamePid)
            {
                Log.Warning("Launch broker: refused process {Pid}, it is not the game ({Game})", peerPid, gamePid);
            }
            else if (await ReadRequestAsync(stream, timeout.Token) is { } challenge)
            {
                proof = _sign(_userId, challenge);
                if (proof != null)
                    Signed++;
            }

            await stream.WriteAsync(Encoding.UTF8.GetBytes((proof ?? "") + "\n"), timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
                Log.Debug(e, "Launch broker: request from {Pid} failed", peerPid);
        }
    }

    /// <summary>The challenge from a well-formed request for our account, or null.</summary>
    private async Task<byte[]?> ReadRequestAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxRequestLength];
        var length = 0;
        var newlines = 0;
        while (newlines < 3)
        {
            if (length == buffer.Length)
            {
                Log.Warning("Launch broker: oversized request");
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0)
                return null;

            for (var i = length; i < length + read; i++)
            {
                if (buffer[i] == '\n')
                    newlines++;
            }

            length += read;
        }

        var lines = Encoding.UTF8.GetString(buffer, 0, length).Split('\n');
        if (lines.Length != 4 || lines[3].Length != 0 || lines[0] != Magic ||
            !Guid.TryParseExact(lines[1], "D", out var userId) ||
            !LaunchProof.TryFromBase64Url(lines[2], out var challenge) ||
            challenge.Length != LaunchProof.ChallengeLength)
        {
            Log.Warning("Launch broker: malformed request");
            return null;
        }

        if (userId != _userId)
        {
            Log.Warning("Launch broker: the game asked for {Asked}, it was started for {User}", userId, _userId);
            return null;
        }

        return challenge;
    }

    private static int PeerPid(Socket socket)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; } from SO_PEERCRED (17) at SOL_SOCKET (1).
        Span<byte> credentials = stackalloc byte[12];
        try
        {
            return socket.GetRawSocketOption(1, 17, credentials) >= 4 ? BitConverter.ToInt32(credentials) : -1;
        }
        catch (SocketException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
