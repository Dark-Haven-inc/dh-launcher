using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using DarkHaven.ContentDb;
using NSec.Cryptography;
using Robust.LoaderApi;

namespace DarkHaven.Loader;

/// <summary>
/// In-process engine loader. Verifies the RobustToolbox build's Ed25519 signature, then loads
/// <c>Robust.Client</c> out of the (zip) build into the default <see cref="AssemblyLoadContext"/>
/// and hands control to its <see cref="ILoaderEntryPoint"/>, mounting the launcher's content DB
/// as the engine's virtual filesystem.
///
/// Usage: <c>DarkHaven.Loader &lt;engineZip&gt; &lt;signatureHex&gt; &lt;publicKeyFile&gt; [engineArg...]</c>
/// </summary>
internal sealed class Program
{
    private const string RobustAssemblyName = "Robust.Client";

    private readonly string[] _engineArgs;
    private readonly IFileApi _engineFiles;

    private Program(string enginePath, string[] engineArgs)
    {
        _engineArgs = engineArgs;

        var zip = new ZipArchive(File.OpenRead(enginePath), ZipArchiveMode.Read);

        // macOS engine builds nest everything inside an .app bundle.
        var prefix = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "Space Station 14.app/Contents/Resources/"
            : "";

        _engineFiles = new ZipFileApi(zip, prefix);

        AssemblyLoadContext.Default.Resolving += OnResolving;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanaged;
    }

    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage: DarkHaven.Loader <engineZip> <signatureHex> <publicKeyFile> [engineArg...]");
            return 1;
        }

        var enginePath = args[0];
        var signatureArg = args[1];
        var publicKeyPath = args[2];

        var engineBytes = File.ReadAllBytes(enginePath);

        if (!VerifyEngine(engineBytes, signatureArg, publicKeyPath, out var reason))
        {
            var disable = Environment.GetEnvironmentVariable("SS14_DISABLE_SIGNING");
            var allowDisable =
#if RELEASE
                false;
#else
                true;
#endif
            if (allowDisable && !string.IsNullOrEmpty(disable) && bool.TryParse(disable, out var b) && b)
            {
                Console.Error.WriteLine($"WARNING: engine verification failed ({reason}), continuing (SS14_DISABLE_SIGNING).");
            }
            else
            {
                Console.Error.WriteLine($"Engine verification failed: {reason}");
                return 2;
            }
        }

        var program = new Program(enginePath, args[3..]);
        return program.Run() ? 0 : 3;
    }

    /// <summary>
    /// Verifies the engine build. Two schemes:
    /// <list type="bullet">
    /// <item><c>sha256:&lt;hex&gt;</c> — a bundled engine the launcher vouches for by content hash.</item>
    /// <item>otherwise — hex Ed25519 signature over the zip, checked against <paramref name="publicKeyPath"/>.</item>
    /// </list>
    /// </summary>
    private static bool VerifyEngine(byte[] engineBytes, string signatureArg, string publicKeyPath, out string reason)
    {
        if (signatureArg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = signatureArg["sha256:".Length..];
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(engineBytes));
            if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                reason = "";
                return true;
            }
            reason = $"bundled engine SHA-256 mismatch (expected {expected}, got {actual})";
            return false;
        }

        try
        {
            var publicKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                File.ReadAllBytes(publicKeyPath),
                KeyBlobFormat.PkixPublicKeyText);

            if (SignatureAlgorithm.Ed25519.Verify(publicKey, engineBytes, Convert.FromHexString(signatureArg)))
            {
                reason = "";
                return true;
            }
            reason = "Ed25519 signature does not match";
            return false;
        }
        catch (Exception e)
        {
            reason = $"signature check errored: {e.Message}";
            return false;
        }
    }

    private bool Run()
    {
        if (!TryOpenAssembly(RobustAssemblyName, out var clientAssembly))
        {
            Console.Error.WriteLine($"Unable to locate {RobustAssemblyName}.dll in the engine build!");
            return false;
        }

        var attrib = clientAssembly.GetCustomAttribute<LoaderEntryPointAttribute>();
        if (attrib == null)
        {
            Console.Error.WriteLine($"No LoaderEntryPointAttribute on {RobustAssemblyName}!");
            return false;
        }

        if (!attrib.LoaderEntryPointType.IsAssignableTo(typeof(ILoaderEntryPoint)))
        {
            Console.Error.WriteLine($"Loader type {attrib.LoaderEntryPointType} does not implement ILoaderEntryPoint!");
            return false;
        }

        var loader = (ILoaderEntryPoint)Activator.CreateInstance(attrib.LoaderEntryPointType)!;

        var launcherPath = Environment.GetEnvironmentVariable("SS14_LAUNCHER_PATH");
        var redialApi = launcherPath != null ? new RedialApi(launcherPath) : null;

        var contentDb = Environment.GetEnvironmentVariable("SS14_LOADER_CONTENT_DB");
        var contentVersion = Environment.GetEnvironmentVariable("SS14_LOADER_CONTENT_VERSION");
        var overlayZip = Environment.GetEnvironmentVariable("SS14_LOADER_OVERLAY_ZIP");

        ContentDbFileApi? contentApi = null;
        ZipFileApi? overlayApi = null;
        List<ApiMount>? mounts = null;

        if (!string.IsNullOrEmpty(contentDb) && !string.IsNullOrEmpty(contentVersion))
        {
            contentApi = new ContentDbFileApi(contentDb, long.Parse(contentVersion));
            mounts = [new ApiMount(contentApi, "/")];
        }

        if (!string.IsNullOrEmpty(overlayZip))
        {
            overlayApi = new ZipFileApi(new ZipArchive(File.OpenRead(overlayZip), ZipArchiveMode.Read));
            // Overlay masks the base install, so it goes first.
            mounts = [new ApiMount(overlayApi, "/"), .. mounts ?? []];
        }

        var mainArgs = new MainArgs(_engineArgs, _engineFiles, redialApi, mounts);

        try
        {
            loader.Main(mainArgs);
        }
        finally
        {
            contentApi?.Dispose();
            overlayApi?.Dispose();
        }

        return true;
    }

    private Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
        => TryOpenAssembly(name.Name!, out var asm) ? asm : null;

    private IntPtr OnResolvingUnmanaged(Assembly assembly, string unmanaged)
    {
        var ourDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var candidate = Path.Combine(ourDir, unmanaged);
        return NativeLibrary.TryLoad(candidate, out var handle) ? handle : IntPtr.Zero;
    }

    private bool TryOpenAssembly(string name, out Assembly assembly)
    {
        assembly = null!;
        if (!_engineFiles.TryOpen($"{name}.dll", out var asmStream))
            return false;

        _engineFiles.TryOpen($"{name}.pdb", out var pdbStream);
        assembly = AssemblyLoadContext.Default.LoadFromStream(asmStream, pdbStream);
        return true;
    }
}
