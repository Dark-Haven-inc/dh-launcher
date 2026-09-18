using System.Diagnostics;
using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// Collects the game client's stdout/stderr into <c>logs/client.log</c> and keeps the last few
/// lines in memory, so that when the game dies right after launch the launcher can say why.
/// <para>
/// This exists because of 0.2.2/0.2.3: the loader failed on start with a FileLoadException for
/// every player, but its output went nowhere and the launcher showed nothing at all.
/// </para>
/// </summary>
public sealed class ClientLog : IDisposable
{
    public const int TailCapacity = 40;

    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly StreamWriter? _file;

    public string Path { get; }

    public ClientLog(string path, string? previousPath = null)
    {
        Path = path;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Only rotate a log that has something in it: a retry that fails before the game even
            // starts must not push the real crash output out of client.prev.log with an empty file.
            if (previousPath is not null && File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, previousPath, overwrite: true);
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception e)
        {
            // Still keep the in-memory tail — that's what the error screen actually shows.
            Log.Warning(e, "Could not open client log {Path}", path);
        }
    }

    /// <summary>
    /// Starts draining a process launched with redirected stdout/stderr. Both streams must be read
    /// continuously for the whole session — a full pipe buffer would stall the game.
    /// </summary>
    public void Attach(Process process)
    {
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public void Append(string line)
    {
        lock (_lock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailCapacity)
                _tail.Dequeue();

            try { _file?.WriteLine(line); }
            catch (Exception) { /* a log we can't write must never take the game down with it */ }
        }
    }

    /// <summary>The last <see cref="TailCapacity"/> lines, oldest first.</summary>
    public string Tail()
    {
        lock (_lock)
            return string.Join(Environment.NewLine, _tail);
    }

    /// <summary>
    /// Human-readable meaning of a game/loader exit code. Loader's own codes come from
    /// <c>DarkHaven.Loader/Program.cs</c>; the rest are the Windows/.NET ones that show up in practice.
    /// </summary>
    public static string DescribeExitCode(int code) => unchecked((uint)code) switch
    {
        1 => "лоадер запущен с неверными аргументами",
        2 => "движок не прошёл проверку подписи",
        3 => "движок не удалось запустить",
        0xE0434352 => "необработанное исключение .NET",
        0xC0000005 => "нарушение доступа к памяти",
        0xC0000409 => "аварийное завершение процесса",
        0xC0000135 => "не найдена нужная библиотека",
        _ => "неизвестная ошибка",
    };

    /// <summary>Exit code as players will quote it: small codes as-is, Windows NTSTATUS-style ones in hex.</summary>
    public static string FormatExitCode(int code) =>
        code is >= 0 and < 256 ? code.ToString() : $"0x{unchecked((uint)code):X8}";

    public void Dispose()
    {
        lock (_lock)
            _file?.Dispose();
    }
}
