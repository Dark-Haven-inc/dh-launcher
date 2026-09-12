using System.IO.Pipes;
using System.Text;
using Serilog;

namespace DarkHaven.App;

/// <summary>
/// One launcher process at a time. A second launch (typically an <c>ss14://</c> link click) hands
/// its argument to the already-running instance over a named pipe and exits, so the link opens in
/// the existing window instead of spawning a duplicate.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\Frontier15Launcher.SingleInstance";
    private const string PipeName = "Frontier15Launcher.ipc";

    private static Mutex? _mutex;

    /// <summary>
    /// True if this is the primary instance (caller should keep running). False means the message
    /// was forwarded to the running instance and the caller should exit.
    /// </summary>
    public static bool TryAcquire(string? forwardMessage)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
            if (isNew)
                return true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Single-instance mutex failed — allowing this instance");
            return true;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(forwardMessage) ? "focus" : forwardMessage);
            client.Write(payload, 0, payload.Length);
            client.Flush();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not hand off to the running launcher");
        }
        return false;
    }

    /// <summary>Primary instance: listen for forwarded messages. <paramref name="onMessage"/> is invoked off the UI thread.</summary>
    public static void StartListening(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var msg = reader.ReadToEnd().Trim();
                    if (msg.Length > 0)
                        onMessage(msg);
                }
                catch (Exception e)
                {
                    Log.Debug(e, "IPC listener error");
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "dh-ipc",
        };
        thread.Start();
    }
}
