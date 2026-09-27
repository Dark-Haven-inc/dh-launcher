using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using DarkHaven.Launcher.Api;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// Signing in to the platform: the SS14 token goes to Wizard's Den only - the platform gets a challenge back, never
/// the token - unless the platform predates that and knows only the old way.
/// </summary>
public sealed class PlatformSignInTests
{
    private const string Token = "wizards-token-secret";
    private static readonly Guid User = Guid.Parse("6f5c7c64-3f7e-4b5a-9b1e-0d6a8f0f3c21");

    /// <summary>The platform and Wizard's Den on one fake network, recording every request.</summary>
    private sealed class Network(bool platformHasChallenge) : HttpMessageHandler
    {
        public readonly List<(string Url, string? Auth, string Body)> Requests = [];
        public readonly string Challenge = Base64Url.EncodeToString(Enumerable.Range(1, 32).Select(i => (byte) i).ToArray());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel);
            Requests.Add((url, request.Headers.Authorization?.ToString(), body));

            return url switch
            {
                "https://platform.test/api/session/challenge" when platformHasChallenge =>
                    Json($$"""{"challenge":"{{Challenge}}"}"""),
                "https://platform.test/api/session/challenge" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "https://auth.test/api/session/join" => new HttpResponseMessage(HttpStatusCode.OK),
                "https://platform.test/api/session/joined" or "https://platform.test/api/session" =>
                    Json($$"""{"token":"jwt","expiresIn":3600,"userId":"{{User}}","username":"Tester","roles":["news"]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task The_token_goes_to_wizards_den_and_never_to_the_platform()
    {
        var net = new Network(platformHasChallenge: true);
        var platform = new PlatformApi(new HttpClient(net), "https://platform.test", "https://auth.test");

        Assert.True(await platform.SignInAsync(User, "Tester", Token));
        Assert.Equal(["news"], platform.Roles);

        Assert.DoesNotContain(net.Requests, r => r.Url.StartsWith("https://platform.test") &&
                                                 ((r.Auth ?? "").Contains(Token) || r.Body.Contains(Token)));

        var join = Assert.Single(net.Requests, r => r.Url == "https://auth.test/api/session/join");
        Assert.Equal($"SS14Auth {Token}", join.Auth);
        // The engine's format: the hash in standard base64 (the server asks Wizard's Den with base64url).
        var hash = JsonDocument.Parse(join.Body).RootElement.GetProperty("hash").GetString();
        Assert.Equal(Convert.ToBase64String(Base64Url.DecodeFromChars(net.Challenge)), hash);

        var joined = Assert.Single(net.Requests, r => r.Url == "https://platform.test/api/session/joined");
        Assert.Contains(net.Challenge, joined.Body);
        Assert.Contains(User.ToString(), joined.Body);
    }

    [Fact]
    public async Task A_platform_from_before_the_challenge_gets_the_old_way()
    {
        var net = new Network(platformHasChallenge: false);
        var platform = new PlatformApi(new HttpClient(net), "https://platform.test", "https://auth.test");

        Assert.True(await platform.SignInAsync(User, "Tester", Token));
        var legacy = Assert.Single(net.Requests, r => r.Url == "https://platform.test/api/session");
        Assert.Equal($"SS14Auth {Token}", legacy.Auth);
        Assert.DoesNotContain(net.Requests, r => r.Url.StartsWith("https://auth.test"));
    }
}
