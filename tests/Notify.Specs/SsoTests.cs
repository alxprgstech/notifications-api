using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Notify;
using Xunit;

namespace NotifySpecs;

// A real local HTTP stand-in for SSO, not a bypass in the Notify application.
// Its signed proofs exercise the RP contract; it does not test SSO's password/TOTP implementation.
public sealed class FakeSso : IAsyncDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly Dictionary<string, JsonElement> requests = [];
    private readonly HashSet<string> redeemed = [];
    private WebApplication app = null!;
    public string Issuer { get; private set; } = "";
    public bool TamperHash { get; set; }
    public string LastState { get; private set; } = "";
    public List<(string body, string timestamp, string signature, string eventId)> Webhooks { get; } = [];
    public async Task Start()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); app = builder.Build();
        app.MapGet("/.well-known/openid-configuration", () => new { issuer = Issuer, jwks_uri = Issuer + "/keys", authorization_endpoint = Issuer + "/oauth/authorize", token_endpoint = Issuer + "/oauth/token", response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" } });
        app.MapGet("/keys", () => { var key = rsa.ExportParameters(false); return Results.Json(new { keys = new[] { new { kty = "RSA", kid = "fixture-key", use = "sig", alg = "RS256", n = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(key.Modulus!), e = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(key.Exponent!) } } }); });
        app.MapPost("/api/v1/step-up/requests", async (HttpContext c) =>
        {
            if (!c.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal)) return Results.Unauthorized();
            var input = await c.Request.ReadFromJsonAsync<JsonElement>(); LastState = input.GetProperty("state").GetString()!; requests[LastState] = input.Clone();
            return Results.Json(new { authorization_url = Issuer + "/confirm?state=" + LastState });
        });
        app.MapPost("/api/v1/step-up/redeem", async (HttpContext c) =>
        {
            var input = await c.Request.ReadFromJsonAsync<JsonElement>(); var state = input.GetProperty("code").GetString()!;
            if (!requests.TryGetValue(state, out var saved) || !redeemed.Add(state)) return Results.BadRequest();
            var expected = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(input.GetProperty("code_verifier").GetString()!)));
            if (expected != saved.GetProperty("code_challenge").GetString() || input.GetProperty("redirect_uri").GetString() != saved.GetProperty("redirect_uri").GetString()) return Results.BadRequest();
            var proof = Token("notify-web", "external_step_up_proof", new[] { new Claim("action", saved.GetProperty("action").GetString()!), new Claim("payload_hash", TamperHash ? new string('0', 64) : saved.GetProperty("payload_hash").GetString()!), new Claim("nonce", saved.GetProperty("nonce").GetString()!), new Claim("acr", "reauth_password_mfa"), new Claim("amr", "pwd"), new Claim("amr", "otp"), new Claim("transaction_id", Wire.Id()), new Claim("security_revision", "1", ClaimValueTypes.Integer64) });
            return Results.Json(new { proof });
        });
        app.MapPost("/events", async (HttpContext c) => { using var r = new StreamReader(c.Request.Body); Webhooks.Add((await r.ReadToEndAsync(), c.Request.Headers["X-Notify-Timestamp"].ToString(), c.Request.Headers["X-Notify-Signature"].ToString(), c.Request.Headers["X-Notify-Event-Id"].ToString())); return Results.StatusCode(Webhooks.Count == 1 ? 500 : 200); });
        await app.StartAsync(); Issuer = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }
    public string Token(string audience = "notify-android", string use = "access_token", IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim> { new("sub", AppFactory.Owner), new("token_use", use), new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64) };
        if (extra != null) claims.AddRange(extra);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience, claims, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "fixture-key" }, "RS256")));
    }
    public async ValueTask DisposeAsync() { await app.DisposeAsync(); rsa.Dispose(); }
}
public sealed class SsoTests
{
    [Fact]
    public async Task EnrollmentRecoveryUsesBoundRedeemedPasswordMfaProof()
    {
        await using var sso = new FakeSso(); await sso.Start(); using var app = new AppFactory { Issuer = sso.Issuer };
        var (old, _, oldKey) = app.Device(); using var oldClient = old; using var oldSigning = oldKey; using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = app.Client(sso.Token());
        _ = await app.Services.GetRequiredService<SsoClient>().Validate(sso.Token(), "notify-android", "access_token", CancellationToken.None);
        var start = await client.PostAsJsonAsync("/api/v1/device-enrollments", new EnrollDevice("Replacement", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Recovery: true)); start.EnsureSuccessStatusCode();
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentId").GetString()!;
        using var browser = app.Client(); var callback = $"/auth/step-up/callback?state={sso.LastState}&code={sso.LastState}&iss={Uri.EscapeDataString(sso.Issuer)}";
        (await browser.GetAsync(callback)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(callback)).StatusCode);
        var result = await browser.GetFromJsonAsync<JsonElement>("/api/v1/device-enrollments/" + id); Assert.True(result.TryGetProperty("token", out var token));
        using var replacement = app.Client(token.GetString()); (await replacement.GetAsync("/api/v1/devices")).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/v1/devices")).StatusCode);
        var consumed = await browser.GetFromJsonAsync<JsonElement>("/api/v1/device-enrollments/" + id); Assert.Equal("consumed", consumed.GetProperty("status").GetString());
    }
    [Fact]
    public async Task InvalidProfileOrProofCannotEnroll()
    {
        await using var sso = new FakeSso(); await sso.Start(); using var app = new AppFactory { Issuer = sso.Issuer }; using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var input = new EnrollDevice("Fixture", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        using var invalid = app.Client(sso.Token(use: "id_token")); Assert.Equal(HttpStatusCode.Forbidden, (await invalid.PostAsJsonAsync("/api/v1/device-enrollments", input)).StatusCode);
        using var valid = app.Client(sso.Token()); (await valid.PostAsJsonAsync("/api/v1/device-enrollments", input)).EnsureSuccessStatusCode(); sso.TamperHash = true;
        using var browser = app.Client(); Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync($"/auth/step-up/callback?state={sso.LastState}&code={sso.LastState}&iss={Uri.EscapeDataString(sso.Issuer)}")).StatusCode);
        Assert.Empty(app.Services.GetRequiredService<NotificationService>().Devices(new Actor("web", AppFactory.Owner, [])));
    }
}
