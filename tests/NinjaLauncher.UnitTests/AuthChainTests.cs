using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Infrastructure.Auth;

namespace NinjaLauncher.UnitTests;

/// <summary>Runs the Microsoft → Xbox → XSTS → Minecraft chain against a fake server and checks the exact wire format.</summary>
public sealed class AuthChainTests
{
    private sealed class NoBrowser : IUrlLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("no browser in tests");
        public void OpenDirectory(string path) { }
    }

    private sealed class FakeServices(HttpStatusCode loginStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Bodies = new();
        public string? ProfileAuth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies[request.RequestUri.Host + request.RequestUri.AbsolutePath] = body;
            return url switch
            {
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token" => Json("""{"access_token":"ms-at","refresh_token":"ms-rt-2"}"""),
                "https://user.auth.xboxlive.com/user/authenticate" => Json("""{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"uhs1"}]}}"""),
                "https://xsts.auth.xboxlive.com/xsts/authorize" => Json("""{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"uhs1","xid":"2535"}]}}"""),
                "https://api.minecraftservices.com/authentication/login_with_xbox" => loginStatus == HttpStatusCode.OK
                    ? Json("""{"access_token":"mc-token","expires_in":86400}""")
                    : new HttpResponseMessage(loginStatus) { Content = new StringContent("""{"error":"Invalid app registration"}""") },
                "https://api.minecraftservices.com/minecraft/profile" => Profile(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Profile(HttpRequestMessage request)
        {
            ProfileAuth = request.Headers.Authorization?.ToString();
            return Json("""{"id":"0123abcd","name":"Steve"}""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private static MicrosoftAuthenticator Create(FakeServices server) =>
        new(new HttpClient(server), new NoBrowser(), () => "db657a7e-8a69-4faa-ae7f-a607b8f9a30a", NullLogger<MicrosoftAuthenticator>.Instance);

    [Fact]
    public async Task Refresh_runs_the_full_chain_with_exact_request_formats()
    {
        var server = new FakeServices();
        var result = await Create(server).RefreshAsync("ms-rt-1", TestContext.Current.CancellationToken);

        Assert.Equal(("0123abcd", "Steve", "2535", "mc-token", "ms-rt-2"),
            (result.ProfileId, result.Username, result.Xuid, result.MinecraftAccessToken, result.MicrosoftRefreshToken));

        var xbl = JsonNode.Parse(server.Bodies["user.auth.xboxlive.com/user/authenticate"])!;
        Assert.Equal("http://auth.xboxlive.com", (string?)xbl["RelyingParty"]);
        Assert.Equal("JWT", (string?)xbl["TokenType"]);
        Assert.Equal("RPS", (string?)xbl["Properties"]!["AuthMethod"]);
        Assert.Equal("user.auth.xboxlive.com", (string?)xbl["Properties"]!["SiteName"]);
        Assert.Equal("d=ms-at", (string?)xbl["Properties"]!["RpsTicket"]);

        var xsts = JsonNode.Parse(server.Bodies["xsts.auth.xboxlive.com/xsts/authorize"])!;
        Assert.Equal("rp://api.minecraftservices.com/", (string?)xsts["RelyingParty"]);
        Assert.Equal("RETAIL", (string?)xsts["Properties"]!["SandboxId"]);
        Assert.Equal("xbl-token", (string?)xsts["Properties"]!["UserTokens"]![0]);

        var login = JsonNode.Parse(server.Bodies["api.minecraftservices.com/authentication/login_with_xbox"])!;
        Assert.Equal("XBL3.0 x=uhs1;xsts-token", (string?)login["identityToken"]);

        Assert.Equal("Bearer mc-token", server.ProfileAuth);
        Assert.Contains("grant_type=refresh_token", server.Bodies["login.microsoftonline.com/consumers/oauth2/v2.0/token"]);
    }

    [Fact]
    public async Task Unapproved_app_id_is_reported_clearly() =>
        Assert.Contains("approved by Mojang", (await Assert.ThrowsAsync<AuthenticationException>(() =>
            Create(new FakeServices(HttpStatusCode.Forbidden)).RefreshAsync("ms-rt-1", TestContext.Current.CancellationToken))).Message);
}
