using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Application.Services;

namespace NinjaLauncher.Infrastructure.Auth;

/// <summary>
/// Microsoft identity platform authorization-code + PKCE in the system browser with a loopback redirect,
/// followed by Xbox Live, XSTS and Minecraft Services. See docs/research/AUTHENTICATION.md.
/// No passwords are ever seen by the launcher, and no token is logged.
/// </summary>
public sealed class MicrosoftAuthenticator(HttpClient http, IUrlLauncher browser, Func<string?> clientId, ILogger<MicrosoftAuthenticator> logger) : IMinecraftAuthenticator
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string Scope = "XboxLive.signin offline_access";
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(5);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(clientId());

    public async Task<MinecraftSignIn> SignInInteractiveAsync(CancellationToken ct)
    {
        var id = RequireClientId();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var port = FreeLoopbackPort();
        var redirect = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var query = HttpUtility.ParseQueryString("");
        query["client_id"] = id;
        query["response_type"] = "code";
        query["redirect_uri"] = redirect;
        query["response_mode"] = "query";
        query["scope"] = Scope;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        query["prompt"] = "select_account";
        browser.Open(new Uri($"{AuthorizeEndpoint}?{query}"));

        var code = await WaitForCodeAsync(listener, state, ct);
        var tokens = await PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = id, ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["scope"] = Scope,
        }, ct);
        return await CompleteAsync(tokens, ct);
    }

    public async Task<MinecraftSignIn> RefreshAsync(string microsoftRefreshToken, CancellationToken ct)
    {
        var tokens = await PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = RequireClientId(), ["grant_type"] = "refresh_token", ["refresh_token"] = microsoftRefreshToken, ["scope"] = Scope,
        }, ct);
        return await CompleteAsync(tokens, ct);
    }

    private string RequireClientId() => clientId() is { Length: > 0 } id ? id
        : throw new AuthenticationException("Microsoft sign-in is not configured: this build has no approved Azure application (client) ID. See docs/research/AUTHENTICATION.md.");

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BrowserTimeout);
        await using var reg = timeout.Token.Register(listener.Stop);
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                ct.ThrowIfCancellationRequested();
                throw new AuthenticationException("Sign-in timed out waiting for the browser.");
            }
            var q = ctx.Request.QueryString;
            if (q["state"] is null && q["code"] is null && q["error"] is null) { Respond(ctx, 404, "Not found"); continue; } // e.g. favicon
            if (q["state"] != expectedState)
            {
                Respond(ctx, 400, "Sign-in failed: invalid state. You can close this window.");
                throw new AuthenticationException("Sign-in response had an invalid state parameter; it was rejected.");
            }
            if (q["error"] is { } error)
            {
                Respond(ctx, 400, "Sign-in was cancelled or failed. You can close this window.");
                throw new AuthenticationException($"Microsoft sign-in failed: {error} {q["error_description"]}");
            }
            Respond(ctx, 200, "Signed in to Ninja Launcher. You can close this window and return to the launcher.");
            return q["code"] ?? throw new AuthenticationException("Microsoft did not return an authorization code.");
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>Ninja Launcher</title><body style=\"font-family:sans-serif;background:#16121f;color:#f3eafd;display:grid;place-items:center;height:100vh\"><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private async Task<JsonNode> PostFormAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new JsonObject();
        if (!response.IsSuccessStatusCode)
        {
            var error = body["error"]?.GetValue<string>();
            throw new AuthenticationException(error == "invalid_grant"
                ? "Your Microsoft session expired or was revoked. Please sign in again."
                : $"Microsoft token request failed ({(int)response.StatusCode} {error}).");
        }
        return body;
    }

    private async Task<MinecraftSignIn> CompleteAsync(JsonNode msTokens, CancellationToken ct)
    {
        var msAccess = msTokens["access_token"]!.GetValue<string>();
        var msRefresh = msTokens["refresh_token"]?.GetValue<string>() ?? throw new AuthenticationException("Microsoft did not return a refresh token.");

        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msAccess },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT",
        }, null, ct);
        var xblToken = xbl["Token"]!.GetValue<string>();

        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT",
        }, null, ct);
        var xstsToken = xsts["Token"]!.GetValue<string>();
        var claims = xsts["DisplayClaims"]?["xui"]?[0];
        var uhs = claims?["uhs"]?.GetValue<string>() ?? throw new AuthenticationException("Xbox Live did not return a user hash.");
        var xuid = claims?["xid"]?.GetValue<string>() ?? await XuidAsync(xblToken, ct);

        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" }, null, ct);
        var mcToken = mc["access_token"]!.GetValue<string>();
        var expires = DateTimeOffset.UtcNow.AddSeconds(mc["expires_in"]?.GetValue<int>() ?? 86400);

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new("Bearer", mcToken);
        using var profileResponse = await http.SendAsync(profileRequest, ct);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
            throw new AuthenticationException("This Microsoft account does not own Minecraft: Java Edition (or has not created a profile yet). Buy the game or set up your profile at minecraft.net.");
        if (!profileResponse.IsSuccessStatusCode) throw new AuthenticationException($"Minecraft profile request failed ({(int)profileResponse.StatusCode}).");
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new AuthenticationException("Empty Minecraft profile.");

        logger.LogInformation("Microsoft sign-in completed for profile {Name}", profile["name"]?.GetValue<string>());
        return new MinecraftSignIn(profile["id"]!.GetValue<string>(), profile["name"]!.GetValue<string>(), xuid, mcToken, expires, msRefresh,
            ClientId: RequireClientId());
    }

    /// <summary>
    /// The Minecraft relying party's XSTS reply has no Xbox user ID (seen live); the Xbox Live one does. Best effort:
    /// the game runs without it, so a failure here never fails the sign-in.
    /// </summary>
    private async Task<string> XuidAsync(string xblToken, CancellationToken ct)
    {
        try
        {
            var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "http://xboxlive.com", TokenType = "JWT",
            }, null, ct);
            return xsts["DisplayClaims"]?["xui"]?[0]?["xid"]?.GetValue<string>() ?? "0";
        }
        catch (Exception e) when (e is AuthenticationException or HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("Could not read the Xbox user ID: {Message}", e.Message);
            return "0";
        }
    }

    private async Task<JsonNode> PostJsonAsync(string url, object payload, string? bearer, CancellationToken ct)
    {
        // Xbox endpoints require the exact PascalCase names (RelyingParty, Properties…); JsonContent's default is camelCase.
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: ExactNames) };
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return JsonNode.Parse(text) ?? new JsonObject();
        logger.LogWarning("Sign-in step {Host} returned {Status}: {Body}", new Uri(url).Host, (int)response.StatusCode,
            SecretRedactor.Redact(text.Length > 600 ? text[..600] : text));
        throw MapError(url, response.StatusCode, text);
    }

    private static readonly JsonSerializerOptions ExactNames = new() { PropertyNamingPolicy = null };

    internal static AuthenticationException MapError(string url, HttpStatusCode status, string body)
    {
        if (url.Contains("xsts", StringComparison.Ordinal))
        {
            long xerr = 0;
            try { xerr = JsonDocument.Parse(body).RootElement.GetProperty("XErr").GetInt64(); } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return new AuthenticationException(xerr switch
            {
                2148916233 => "This Microsoft account has no Xbox profile. Sign in once at xbox.com to create one, then try again.",
                2148916235 => "Xbox Live is not available in your country or region.",
                2148916236 or 2148916237 => "This account needs adult verification on xbox.com before it can sign in.",
                2148916238 => "This is a child account. An adult must add it to a Microsoft family group before it can play.",
                2148916227 => "This Xbox account has been banned.",
                _ => $"Xbox Live authorization failed ({(int)status}{(xerr != 0 ? $", XErr {xerr}" : "")}).",
            });
        }
        if (url.Contains("login_with_xbox", StringComparison.Ordinal) && status == HttpStatusCode.Forbidden)
            return new AuthenticationException("Minecraft Services rejected this launcher's Azure application ID (403). The app ID must be approved by Mojang: https://aka.ms/AppRegInfo");
        if (status == HttpStatusCode.TooManyRequests) return new AuthenticationException("Too many sign-in attempts. Please wait a few minutes.");
        return new AuthenticationException($"Sign-in step failed: {new Uri(url).Host} returned {(int)status}.");
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
