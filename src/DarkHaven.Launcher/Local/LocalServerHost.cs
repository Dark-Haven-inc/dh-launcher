using System.Diagnostics;
using System.Text;
using Serilog;

namespace DarkHaven.Launcher.Local;

public enum LocalServerState { Stopped, Preparing, Starting, Running, Stopping, Crashed }

/// <summary>What it takes to start one local server process.</summary>
public sealed record LocalServerLaunch(
    string Executable,
    string ConfigPath,
    string DataDir,
    string? DotnetRoot,
    IReadOnlyList<string> Cvars);

/// <summary>
/// One running local server: the process, its console output, and whether it's up yet ("up" = its
/// own <c>/status</c> answers). Stopping asks the server to shut down through its console first and
/// only kills it if it doesn't within the grace period.
/// </summary>
public sealed class LocalServerHost(int port, HttpClient http)
{
    private const int MaxLines = 1000;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private readonly LinkedList<string> _lines = new();
    private Process? _process;
    private bool _stopRequested;

    public LocalServerState State { get; private set; } = LocalServerState.Stopped;

    /// <summary>What is going on in words (preparing: which download; crashed: the exit code).</summary>
    public string? Detail { get; private set; }

    public int? ExitCode { get; private set; }

    public event Action? Changed;
    public event Action<string>? LineAdded;

    public int Port => port;

    public string Address => $"ss14://127.0.0.1:{port}";

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }

    public bool IsBusy => State is LocalServerState.Preparing or LocalServerState.Starting
        or LocalServerState.Running or LocalServerState.Stopping;

    /// <summary>Downloads and the like before the process exists — shown on the card as they happen.</summary>
    public void Preparing(string detail) => Set(LocalServerState.Preparing, detail);

    /// <summary>Preparation failed before the server ever started.</summary>
    public void Failed(string reason)
    {
        AddLine($"[лаунчер] {reason}");
        Set(LocalServerState.Crashed, reason);
    }

    public async Task StartAsync(LocalServerLaunch launch, CancellationToken cancel = default)
    {
        if (_process is { HasExited: false })
            throw new InvalidOperationException("Сервер уже запущен.");

        _stopRequested = false;
        ExitCode = null;
        lock (_lock) _lines.Clear();

        var psi = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = Path.GetDirectoryName(launch.Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config-file");
        psi.ArgumentList.Add(launch.ConfigPath);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(launch.DataDir);
        foreach (var cvar in launch.Cvars)
        {
            psi.ArgumentList.Add("--cvar");
            psi.ArgumentList.Add(cvar);
        }
        if (launch.DotnetRoot is { } root)
        {
            psi.Environment["DOTNET_ROOT"] = root;
            psi.Environment["DOTNET_ROOT_X64"] = root;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) AddLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddLine(e.Data); };
        process.Exited += (_, _) => OnExited(process);

        Set(LocalServerState.Starting, "сервер загружается…");
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить процесс сервера.");
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.Information("Local server started: {Exe} on port {Port} (pid {Pid})", launch.Executable, port, process.Id);

        // Up = /status answers. A first start builds its database and loads every prototype — minutes, not seconds.
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline && !process.HasExited && !cancel.IsCancellationRequested)
        {
            try
            {
                using var ping = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/status", ping.Token);
                if (response.IsSuccessStatusCode)
                {
                    Set(LocalServerState.Running, null);
                    return;
                }
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                // not listening yet
            }
            await Task.Delay(1000, cancel);
        }

        if (!process.HasExited && State == LocalServerState.Starting)
            Set(LocalServerState.Starting, "сервер долго не отвечает — смотрите консоль");
    }

    /// <summary>A console command, as if typed into the server's window (<c>savemap</c>, <c>restartround</c>…).</summary>
    public void Send(string command)
    {
        var p = _process;
        if (p is null || p.HasExited || string.IsNullOrWhiteSpace(command))
            return;
        AddLine($"> {command}");
        try
        {
            p.StandardInput.WriteLine(command);
            p.StandardInput.Flush();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Couldn't write to the local server console");
        }
    }

    public async Task StopAsync()
    {
        var p = _process;
        if (p is null || p.HasExited)
        {
            Set(LocalServerState.Stopped, null);
            return;
        }

        _stopRequested = true;
        Set(LocalServerState.Stopping, "сервер сохраняется и закрывается…");
        Send("shutdown");
        using var grace = new CancellationTokenSource(StopGrace);
        try
        {
            await p.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            AddLine("[лаунчер] Сервер не закрылся сам — останавливаю принудительно.");
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await p.WaitForExitAsync();
        }
    }

    /// <summary>Launcher is closing: ask the server to stop, don't wait on it for long.</summary>
    public void StopOnExit()
    {
        var p = _process;
        if (p is null || p.HasExited)
            return;
        _stopRequested = true;
        Send("shutdown");
        if (!p.WaitForExit(5000))
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private void OnExited(Process p)
    {
        int? code = null;
        try { code = p.ExitCode; } catch { /* no code */ }
        ExitCode = code;
        if (_stopRequested)
        {
            Set(LocalServerState.Stopped, null);
            return;
        }
        AddLine($"[лаунчер] Сервер завершился сам (код {code?.ToString() ?? "?"}).");
        Set(LocalServerState.Crashed, $"сервер упал (код {code?.ToString() ?? "?"}) — смотрите консоль");
    }

    private void AddLine(string line)
    {
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveFirst();
        }
        LineAdded?.Invoke(line);
    }

    private void Set(LocalServerState state, string? detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
