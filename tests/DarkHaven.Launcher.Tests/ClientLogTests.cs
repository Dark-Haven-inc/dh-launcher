using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class ClientLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dh-clientlog-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "client.log");
    private string PrevPath => Path.Combine(_dir, "client.prev.log");

    [Fact]
    public void Tail_keeps_only_the_last_lines_in_order()
    {
        using var log = new ClientLog(LogPath);
        for (var i = 1; i <= ClientLog.TailCapacity + 5; i++)
            log.Append($"line {i}");

        var lines = log.Tail().Split(Environment.NewLine);
        Assert.Equal(ClientLog.TailCapacity, lines.Length);
        Assert.Equal("line 6", lines[0]);
        Assert.Equal($"line {ClientLog.TailCapacity + 5}", lines[^1]);
    }

    [Fact]
    public void Writes_every_line_to_the_file_not_just_the_tail()
    {
        using (var log = new ClientLog(LogPath))
            for (var i = 0; i < ClientLog.TailCapacity * 2; i++)
                log.Append($"line {i}");

        Assert.Equal(ClientLog.TailCapacity * 2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void Rotates_the_previous_log_but_never_with_an_empty_one()
    {
        using (var crash = new ClientLog(LogPath, PrevPath))
            crash.Append("Unhandled exception. System.IO.FileLoadException");

        // A retry that fails before the game starts leaves an empty client.log...
        new ClientLog(LogPath, PrevPath).Dispose();
        // ...and the next attempt must not push the crash out of client.prev.log with it.
        new ClientLog(LogPath, PrevPath).Dispose();

        Assert.Contains("FileLoadException", File.ReadAllText(PrevPath));
    }

    [Theory]
    [InlineData(3, "3")]
    [InlineData(-532462766, "0xE0434352")] // .NET unhandled exception, as Process.ExitCode reports it
    public void Formats_exit_codes_the_way_players_will_quote_them(int code, string expected)
        => Assert.Equal(expected, ClientLog.FormatExitCode(code));

    [Fact]
    public void Describes_the_loaders_own_exit_codes()
    {
        Assert.Contains("подписи", ClientLog.DescribeExitCode(2));
        Assert.Contains("исключение", ClientLog.DescribeExitCode(-532462766));
        Assert.Equal("неизвестная ошибка", ClientLog.DescribeExitCode(42));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
