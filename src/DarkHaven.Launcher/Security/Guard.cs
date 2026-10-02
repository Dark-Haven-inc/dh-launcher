using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace DarkHaven.Launcher.Security;

/// <summary>
/// The launcher's trust core, <c>dh_guard</c> (native/dh-guard, docs/GUARD.md): a Rust library loaded in-process
/// that starts the game and, in release builds, vouches for it. It builds the loader's command line and environment
/// from a <see cref="GuardLaunchRequest"/>, checks the loader and engine it is asked to start, runs the launch broker
/// the game asks for proofs and signs them with a key it never holds whole. Nothing in C# can sign: the only way to a
/// proof is to have the guard start the loader itself, and the broker answers only that process.
/// </summary>
public static partial class Guard
{
    public const int AbiVersion = 1;

    private const string Library = "dh_guard";

    private static readonly Lazy<GuardInfo> LazyInfo = new(ReadInfo);

    /// <summary>What this build's guard carries: key or not, pinned loader files, version, public key.</summary>
    /// <exception cref="GuardException">The library is missing or of another ABI version.</exception>
    public static GuardInfo Info() => LazyInfo.Value;

    /// <summary>Starts the game described by <paramref name="request"/>.</summary>
    /// <exception cref="GuardException">The guard refused or failed; <see cref="GuardException.Status"/> says why.</exception>
    public static GuardLaunch Launch(GuardLaunchRequest request)
    {
        if (Info().AbiVersion != AbiVersion)
            throw new GuardException(GuardStatus.Internal,
                $"dh_guard speaks ABI {Info().AbiVersion}, this launcher {AbiVersion}");

        var json = JsonSerializer.SerializeToUtf8Bytes(request, GuardJsonContext.Default.GuardLaunchRequest);
        int status, pid;
        nint stdout, stderr;
        GuardGameHandle game;
        unsafe
        {
            fixed (byte* p = json)
                status = Native.Launch(p, (nuint)json.Length, out game, out pid, out stdout, out stderr);
        }

        if (status != 0)
        {
            // Read on this thread, right after the call: the message is per thread.
            var message = LastError();
            game.Dispose();
            throw new GuardException((GuardStatus)status, message);
        }

        return new GuardLaunch(game, pid, WrapPipe(stdout), WrapPipe(stderr));
    }

    internal static GuardException Failure(int status) => new((GuardStatus)status, LastError());

    private static SafeFileHandle? WrapPipe(nint handle) => handle == -1 ? null : new SafeFileHandle(handle, ownsHandle: true);

    private static unsafe GuardInfo ReadInfo()
    {
        NativeInfo info;
        int status;
        try
        {
            status = Native.Info(&info);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new GuardException(GuardStatus.Internal,
                $"dh_guard could not be loaded ({e.Message}); the launcher was built without it", e);
        }

        if (status != 0)
            throw Failure(status);

        var publicKey = new ReadOnlySpan<byte>(info.PublicKey, Math.Clamp(info.PublicKeyLength, 0, 96)).ToArray();
        var version = new ReadOnlySpan<byte>(info.LauncherVersion, 64);
        var end = version.IndexOf((byte)0);
        return new GuardInfo(
            info.AbiVersion,
            info.HasKey != 0,
            info.ShareCount,
            info.PinnedFiles,
            publicKey,
            Encoding.UTF8.GetString(end < 0 ? version : version[..end]));
    }

    private static unsafe string LastError()
    {
        var length = Native.LastError(null, 0);
        if (length == 0)
            return "dh_guard failed without saying why";
        var buffer = new byte[(int)Math.Min(length, 64 * 1024)];
        fixed (byte* p = buffer)
            Native.LastError(p, (nuint)buffer.Length);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary><c>DhGuardInfo</c>: five int32, then 96 and 64 bytes, 180 in all.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeInfo
    {
        public int AbiVersion;
        public int HasKey;
        public int ShareCount;
        public int PinnedFiles;
        public int PublicKeyLength;
        public fixed byte PublicKey[96];
        public fixed byte LauncherVersion[64];
    }

    internal static unsafe partial class Native
    {
        [LibraryImport(Library, EntryPoint = "dh_guard_launch")]
        internal static partial int Launch(byte* json, nuint length, out GuardGameHandle game, out int pid,
            out nint stdoutHandle, out nint stderrHandle);

        [LibraryImport(Library, EntryPoint = "dh_guard_game_wait")]
        internal static partial int GameWait(GuardGameHandle game, int timeoutMs, out int exitCode);

        [LibraryImport(Library, EntryPoint = "dh_guard_game_kill")]
        internal static partial int GameKill(GuardGameHandle game);

        [LibraryImport(Library, EntryPoint = "dh_guard_game_signed")]
        internal static partial int GameSigned(GuardGameHandle game);

        [LibraryImport(Library, EntryPoint = "dh_guard_game_broker_state")]
        internal static partial int GameBrokerState(GuardGameHandle game);

        [LibraryImport(Library, EntryPoint = "dh_guard_game_free")]
        internal static partial void GameFree(nint game);

        [LibraryImport(Library, EntryPoint = "dh_guard_info")]
        internal static partial int Info(NativeInfo* info);

        [LibraryImport(Library, EntryPoint = "dh_guard_last_error")]
        internal static partial nuint LastError(byte* buffer, nuint capacity);
    }
}

/// <summary>The guard's status codes (<c>DH_GUARD_*</c>).</summary>
public enum GuardStatus
{
    Ok = 0,
    /// <summary><c>dh_guard_game_wait</c>: still running.</summary>
    Timeout = 1,
    /// <summary>A panic or another failure inside the guard.</summary>
    Internal = -1,
    /// <summary>A null pointer or bad timeout: a bug on this side.</summary>
    Argument = -2,
    /// <summary>The launch request is malformed or asks for something not allowed.</summary>
    Request = -3,
    /// <summary>A release guard refused: the loader or the engine is not the genuine one.</summary>
    Refused = -4,
    /// <summary>The OS would not start or control the process.</summary>
    Os = -5,
}

/// <summary>Where the launch broker ended up (<c>dh_guard_game_broker_state</c>).</summary>
public enum GuardBrokerState
{
    /// <summary>It could not start; the game runs without one, and Frontier 15 servers will not let it in.</summary>
    Failed = -1,
    /// <summary>Not asked for: a development build, or no account.</summary>
    NotRequested = 0,
    /// <summary>Running, or ran until the game exited.</summary>
    Running = 1,
}

public sealed class GuardException(GuardStatus status, string message, Exception? inner = null) : Exception(message, inner)
{
    public GuardStatus Status { get; } = status;
}

/// <param name="ShareCount">Always 0: the guard no longer says how many shares its key is split into (docs/GUARD.md).</param>
/// <param name="PublicKey">The P-256 SubjectPublicKeyInfo (DER) the proofs verify with; empty without a key.</param>
/// <param name="LauncherVersion">The version the proofs state.</param>
public sealed record GuardInfo(
    int AbiVersion, bool HasKey, int ShareCount, int PinnedFiles, byte[] PublicKey, string LauncherVersion);

/// <summary>A game the guard started: its handle, pid and, when redirected, the read ends of its stdout and stderr.</summary>
public sealed record GuardLaunch(GuardGameHandle Game, int Pid, SafeFileHandle? Stdout, SafeFileHandle? Stderr);

/// <summary>
/// A <c>DhGuardGame*</c>. Releasing it frees the handle only: the game keeps running, and its broker keeps answering it
/// until it exits.
/// </summary>
public sealed class GuardGameHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle()
    {
        Guard.Native.GameFree(handle);
        return true;
    }
}

/// <summary>
/// What the launcher asks the guard to start (docs/GUARD.md, "Launch request"). Structured facts only: the guard builds
/// the loader's command line and environment from them.
/// </summary>
public sealed record GuardLaunchRequest
{
    public required string LoaderPath { get; init; }
    public required string EnginePath { get; init; }
    public required string EngineSignature { get; init; }
    public required string EnginePublicKeyPath { get; init; }
    public required string ContentDbPath { get; init; }
    public required long ContentVersion { get; init; }
    public required IReadOnlyList<GuardModule> Modules { get; init; }
    public string? LauncherPath { get; init; }
    /// <summary>The <c>--username</c> when <see cref="Account"/> is null (the account's name wins otherwise).</summary>
    public string? Username { get; init; }
    public bool CompatMode { get; init; }
    public required string ConnectAddress { get; init; }
    public required string Ss14Address { get; init; }
    public GuardBuild? Build { get; init; }
    /// <summary>Only <c>net.logging=true|false</c>; the guard refuses anything else.</summary>
    public IReadOnlyList<string> ExtraCvars { get; init; } = [];
    /// <summary>Only when there is an account and the server's auth mode is not Disabled.</summary>
    public GuardAccount? Account { get; init; }
    public bool RedirectOutput { get; init; }
}

public sealed record GuardModule(string Name, string Version);

public sealed record GuardBuild
{
    public string? EngineVersion { get; init; }
    public string? Version { get; init; }
    public string? ForkId { get; init; }
    public string? Hash { get; init; }
    public string? ManifestHash { get; init; }
    public string? ManifestUrl { get; init; }
    public string? ManifestDownloadUrl { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed record GuardAccount(string Username, string Token, Guid UserId, string? AuthPublicKey);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuardLaunchRequest))]
internal sealed partial class GuardJsonContext : JsonSerializerContext;
