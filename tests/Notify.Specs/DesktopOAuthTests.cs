using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notify;
using OpenIddict.Server;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class DesktopOAuthTests
{
    private const string Audience = "https://localhost/api/v1/agents";
    private const string Redirect = "http://127.0.0.1:53147/notify/oauth/callback";
    private const string AuthorizePath = "/desktop/oauth/authorize";
    private const string TokenPath = "/desktop/oauth/token";
    private const string Codex = "notify-codex-desktop";
    private const string Harness = "notify-harness-desktop";

    [Fact]
    public async Task OidcConsentIssuesIndependentRenewableGrantsAndDisconnectClosesOnlyItsControlSocket()
    {
        await using var issuer = new FakeSso();
        await issuer.Start();
        var codexAgent = Wire.Id();
        var harnessAgent = Wire.Id();
        using var app = new AppFactory { Issuer = issuer.Issuer, DesktopCodexAgentId = codexAgent, DesktopHarnessAgentId = harnessAgent };
        using var browser = app.Client();
        using var tokenClient = app.Client();
        Seed(app, codexAgent);
        Seed(app, harnessAgent);
        var oauthOptions = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        Assert.Contains(oauthOptions.AuthorizationEndpointUris, uri => uri.OriginalString.EndsWith(AuthorizePath, StringComparison.Ordinal));

        var codex = Flow(Codex);
        using var login = await browser.GetAsync(QueryHelpers.AddQueryString(AuthorizePath, codex.Fields));
        await Expect(login, HttpStatusCode.Redirect);
        Assert.Equal(issuer.Issuer, login.Headers.Location!.GetLeftPart(UriPartial.Authority));
        var upstream = QueryHelpers.ParseQuery(login.Headers.Location.Query);
        issuer.WebCodeChallenge = upstream["code_challenge"].ToString();
        issuer.WebNonce = upstream["nonce"].ToString();
        using var completed = await browser.GetAsync(QueryHelpers.AddQueryString("/signin-oidc?code=fixture-code", "state", upstream["state"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);
        Assert.StartsWith(AuthorizePath, completed.Headers.Location!.OriginalString);

        var wrongAudience = new Dictionary<string, string?>(codex.Fields) { ["resource"] = "https://localhost/chatgpt/mcp" };
        var wrongScope = new Dictionary<string, string?>(codex.Fields) { ["scope"] = "notify:send offline_access" };
        var wrongRedirect = new Dictionary<string, string?>(codex.Fields) { ["redirect_uri"] = "https://fixture.test/notify/oauth/callback" };
        await AssertAuthorizationError(browser, wrongAudience, "invalid_target", "invalid_request");
        await AssertAuthorizationError(browser, wrongScope, "invalid_scope", "invalid_request");
        await AssertAuthorizationError(browser, wrongRedirect, "invalid_request");

        var expired = Flow(Codex);
        var expiredCode = await Consent(browser, expired.Fields);
        app.Services.GetRequiredService<Database>().Run((c, t) =>
            Exec(c, t, "UPDATE objects SET data=json_set(data,'$.expiresAt',$expires) WHERE kind='desktop_grant' AND id=$client",
                ("$expires", Wire.Now - 1), ("$client", Codex)));
        using var expiredExchange = await Exchange(tokenClient, Codex, expiredCode, expired.Verifier);
        Assert.Equal(HttpStatusCode.BadRequest, expiredExchange.StatusCode);

        var code = await Consent(browser, codex.Fields);
        var harness = Flow(Harness);
        var harnessCode = await Consent(browser, harness.Fields);
        using var wrongClient = await Exchange(tokenClient, Harness, code, codex.Verifier);
        Assert.Equal(HttpStatusCode.BadRequest, wrongClient.StatusCode);
        using var wrongPkce = await Exchange(tokenClient, Codex, code, Wire.Secret());
        Assert.Equal(HttpStatusCode.BadRequest, wrongPkce.StatusCode);

        using var codexTokens = await Exchange(tokenClient, Codex, code, codex.Verifier);
        using var harnessTokens = await Exchange(tokenClient, Harness, harnessCode, harness.Verifier);
        var codexGrant = await Tokens(codexTokens);
        var harnessGrant = await Tokens(harnessTokens);
        using var codexApi = app.Client(codexGrant.AccessToken);
        using var harnessApi = app.Client(harnessGrant.AccessToken);
        Assert.Equal(codexAgent, await OwnAgent(codexApi));
        Assert.Equal(harnessAgent, await OwnAgent(harnessApi));
        using var unauthorizedOperation = await codexApi.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedOperation.StatusCode);
        using var otherClient = await codexApi.PostAsync("/api/v1/desktop/" + Harness + "/disconnect", null);
        Assert.Equal(HttpStatusCode.Forbidden, otherClient.StatusCode);

        await Consent(browser, Flow(Codex).Fields, "access_denied");
        app.Dispose();
        using var restarted = new AppFactory
        {
            Root = app.Root, Issuer = issuer.Issuer, DesktopCodexAgentId = codexAgent, DesktopHarnessAgentId = harnessAgent
        };
        using var restartedTokenClient = restarted.Client();
        using var refreshed = await Renew(restartedTokenClient, Codex, codexGrant.RefreshToken);
        var renewed = await Tokens(refreshed);
        Assert.False(codexGrant.RefreshToken == renewed.RefreshToken, "The refresh token must rotate");
        using var newCodexApi = restarted.Client(renewed.AccessToken);
        using var restartedHarness = restarted.Client(harnessGrant.AccessToken);
        Assert.Equal(codexAgent, await OwnAgent(newCodexApi));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var foreign = await OpenSocket(restarted, renewed.AccessToken, harnessAgent, timeout.Token))
        {
            var rejection = await foreign.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, rejection.MessageType);
            Assert.Equal("agent_mismatch", rejection.CloseStatusDescription);
        }
        using var codexSocket = await Connect(restarted, renewed.AccessToken, codexAgent, timeout.Token);
        using var harnessSocket = await Connect(restarted, harnessGrant.AccessToken, harnessAgent, timeout.Token);
        using var revoked = await newCodexApi.PostAsync("/api/v1/desktop/" + Codex + "/disconnect", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(WebSocketMessageType.Close, (await codexSocket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token)).MessageType);
        Assert.True(restarted.Services.GetRequiredService<AgentHub>().IsOnline(harnessAgent));
        using var reconnect = await newCodexApi.GetAsync("/api/v1/agents/connect");
        Assert.Equal(HttpStatusCode.Unauthorized, reconnect.StatusCode);
        using var rejectedRenewal = await Renew(restartedTokenClient, Codex, renewed.RefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedRenewal.StatusCode);
        using var oldCodexApi = restarted.Client(codexGrant.AccessToken);
        using var rejectedAccess = await oldCodexApi.GetAsync("/api/v1/agents/self");
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedAccess.StatusCode);
        Assert.Equal(harnessAgent, await OwnAgent(restartedHarness));
    }

    private static void Seed(AppFactory app, string agent)
    {
        var source = Wire.Id();
        app.Services.GetRequiredService<Database>().Run((c, t) =>
        {
            Put(c, t, "source", source, new Source(source, "Synthetic desktop source", ["agents:connect", "notify:send", "calls:start"]));
            Put(c, t, "agent", agent, new Agent(agent, source, "Synthetic desktop agent", ["text_tasks"], Wire.Now));
            return 0;
        });
    }

    private static (Dictionary<string, string?> Fields, string Verifier) Flow(string client)
    {
        var verifier = Wire.Secret();
        return (new Dictionary<string, string?>
        {
            ["client_id"] = client,
            ["redirect_uri"] = Redirect,
            ["resource"] = Audience,
            ["scope"] = "agents:connect offline_access",
            ["response_type"] = "code",
            ["state"] = Wire.Secret(),
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        }, verifier);
    }

    private static async Task<string> Consent(HttpClient browser, Dictionary<string, string?> fields, string? error = null)
    {
        using var view = await browser.GetAsync(QueryHelpers.AddQueryString(AuthorizePath, fields));
        await Expect(view, HttpStatusCode.OK);
        var csrf = Regex.Match(await view.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(csrf.Success);
        using var form = new FormUrlEncodedContent(fields.Append(new("decision", "allow")).Append(
            new("__RequestVerificationToken", WebUtility.HtmlDecode(csrf.Groups[1].Value))));
        using var consent = await browser.PostAsync(AuthorizePath, form);
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var callback = consent.Headers.Location!;
        Assert.Equal(Redirect, callback.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.True(fields["state"] == query["state"].ToString(), "The desktop callback must return its own state");
        if (error is not null)
        {
            Assert.Equal(error, query["error"].ToString());
            Assert.False(query.ContainsKey("code"));
            return "";
        }
        Assert.True(query.ContainsKey("code"), "The callback did not contain an authorization code");
        return query["code"].ToString();
    }

    private static async Task AssertAuthorizationError(HttpClient browser, Dictionary<string, string?> fields, params string[] errors)
    {
        using var response = await browser.GetAsync(QueryHelpers.AddQueryString(AuthorizePath, fields));
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.Equal(Redirect, response.Headers.Location!.GetLeftPart(UriPartial.Path));
            Assert.Contains(QueryHelpers.ParseQuery(response.Headers.Location.Query)["error"].ToString(), errors);
            return;
        }
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var match = Regex.Match(await response.Content.ReadAsStringAsync(), @"(?:^|[\r\n])error:\s*([a-z_]+)");
        Assert.Contains(match.Groups[1].Value, errors);
    }

    private static Task<HttpResponseMessage> Exchange(HttpClient client, string id, string code, string verifier) =>
        TokenRequest(client, id, "authorization_code", new()
        {
            ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect
        });

    private static Task<HttpResponseMessage> Renew(HttpClient client, string id, string refresh) =>
        TokenRequest(client, id, "refresh_token", new() { ["refresh_token"] = refresh });

    private static async Task<HttpResponseMessage> TokenRequest(HttpClient client, string id, string type, Dictionary<string, string> fields)
    {
        fields["client_id"] = id;
        fields["resource"] = Audience;
        fields["grant_type"] = type;
        using var form = new FormUrlEncodedContent(fields);
        return await client.PostAsync(TokenPath, form);
    }

    private static async Task<(string AccessToken, string RefreshToken)> Tokens(HttpResponseMessage response)
    {
        await Expect(response, HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString(), ignoreCase: true);
        return (body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    private static async Task<string> OwnAgent(HttpClient client)
    {
        using var res = await client.GetAsync("/api/v1/agents/self");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("agentId").GetString()!;
    }

    private static async Task Expect(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        var code = "unknown";
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("error", out var error)) code = error.GetString() ?? "unknown";
            if (json.RootElement.TryGetProperty("code", out var fault)) code = fault.GetString() ?? "unknown";
            if (json.RootElement.TryGetProperty("error_uri", out var uri) && Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var reference))
                code += " " + reference.Segments[^1];
        }
        else
        {
            var text = await response.Content.ReadAsStringAsync();
            var error = Regex.Match(text, @"(?:^|[\r\n])error:\s*([a-z_]+)");
            var reference = Regex.Match(text, @"\bID[0-9]{4}\b");
            if (error.Success) code = error.Groups[1].Value + " " + reference.Value;
        }
        Assert.Fail($"Expected {expected}, got {response.StatusCode} ({code}).");
    }

    private static async Task<WebSocket> OpenSocket(AppFactory app, string token, string agent, CancellationToken ct)
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        var socket = await client.ConnectAsync(new Uri("wss://localhost/api/v1/agents/connect"), ct);
        try
        {
            var payload = Encoding.UTF8.GetBytes(Wire.Encode(new
            {
                type = "hello", protocolVersion = AgentHub.Protocol, agentId = agent, eventId = Wire.Id(),
                name = "Synthetic desktop", capabilities = new[] { "text_tasks" }, sessions = Array.Empty<object>()
            }));
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<WebSocket> Connect(AppFactory app, string token, string agent, CancellationToken ct)
    {
        var socket = await OpenSocket(app, token, agent, ct);
        try
        {
            var buffer = new byte[4096];
            var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            using var hello = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
            Assert.Equal("hello_ack", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(agent, hello.RootElement.GetProperty("agent").GetProperty("id").GetString());
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
