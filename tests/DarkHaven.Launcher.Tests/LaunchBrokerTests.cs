using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public sealed class LaunchBrokerTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(LaunchProof.ChallengeLength);

    private static string Request(Guid user, byte[] challenge) =>
        $"{LaunchBroker.Magic}\n{user:D}\n{LaunchProof.ToBase64Url(challenge)}\n";

    [Fact]
    public async Task TheGameGetsAProofForItsChallenge()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var broker = LaunchBroker.Start(User, (user, challenge) => LaunchProof.Create(key, user, challenge, "1.0.0"))!;
        broker.Admit(Environment.ProcessId); // this test process plays the game

        var challenge = NewChallenge();
        var proof = await AskAsync(broker.Endpoint, Request(User, challenge));

        Assert.Equal(1, broker.Signed);
        var dot = proof.IndexOf('.');
        Assert.True(dot > 0);
        Assert.True(LaunchProof.TryFromBase64Url(proof[..dot], out var payload));
        Assert.True(LaunchProof.TryFromBase64Url(proof[(dot + 1)..], out var signature));
        Assert.Equal($"dh-launch/2\n{User:D}\n{LaunchProof.ToBase64Url(challenge)}\n1.0.0", Encoding.UTF8.GetString(payload));
        Assert.True(key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // Every login asks again and gets its own.
        var second = await AskAsync(broker.Endpoint, Request(User, NewChallenge()));
        Assert.NotEqual(proof, second);
        Assert.Equal(2, broker.Signed);
    }

    [Fact]
    public async Task OtherProcessesAreRefused()
    {
        using var broker = LaunchBroker.Start(User, (_, _) => "proof")!;

        // Before the game is known nobody is answered...
        Assert.Equal("", await AskAsync(broker.Endpoint, Request(User, NewChallenge())));

        // ...and afterwards only the game is.
        broker.Admit(Environment.ProcessId + 1);
        Assert.Equal("", await AskAsync(broker.Endpoint, Request(User, NewChallenge())));
        Assert.Equal(0, broker.Signed);
    }

    [Theory]
    [InlineData("wrong-user")]
    [InlineData("bad-magic")]
    [InlineData("short-challenge")]
    [InlineData("garbage")]
    public async Task BadRequestsAreRefused(string kind)
    {
        using var broker = LaunchBroker.Start(User, (_, _) => "proof")!;
        broker.Admit(Environment.ProcessId);

        var request = kind switch
        {
            "wrong-user" => Request(Guid.NewGuid(), NewChallenge()),
            "bad-magic" => Request(User, NewChallenge()).Replace(LaunchBroker.Magic, "dh-launch-broker/0"),
            "short-challenge" => $"{LaunchBroker.Magic}\n{User:D}\n{LaunchProof.ToBase64Url(new byte[32])}\n",
            _ => "\n\n\n",
        };

        Assert.Equal("", await AskAsync(broker.Endpoint, request));
        Assert.Equal(0, broker.Signed);
    }

    [Fact]
    public async Task StopsWhenDisposed()
    {
        var broker = LaunchBroker.Start(User, (_, _) => "proof")!;
        broker.Admit(Environment.ProcessId);
        Assert.Equal("proof", await AskAsync(broker.Endpoint, Request(User, NewChallenge())));

        broker.Dispose();

        if (broker.Endpoint.StartsWith("unix:"))
            Assert.False(File.Exists(broker.Endpoint["unix:".Length..]));
        await Assert.ThrowsAnyAsync<Exception>(() => AskAsync(broker.Endpoint, Request(User, NewChallenge()), TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>What the engine's LaunchBrokerClient does.</summary>
    private static async Task<string> AskAsync(string endpoint, string request, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        Stream stream;
        if (endpoint.StartsWith("pipe:"))
        {
            var pipe = new NamedPipeClientStream(".", endpoint["pipe:".Length..], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token);
            stream = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint["unix:".Length..]), cts.Token);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        await using (stream)
        {
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cts.Token);
                await stream.FlushAsync(cts.Token);
            }
            catch (IOException)
            {
                // A refusal may close before reading; the answer is still there to read.
            }

            var buffer = new byte[4096];
            var length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cts.Token);
                if (read == 0)
                    return "<closed>";
                length += read;
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, length);
                if (newline >= 0)
                    return Encoding.UTF8.GetString(buffer, 0, newline);
            }
        }
    }
}
