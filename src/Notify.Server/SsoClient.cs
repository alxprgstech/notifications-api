using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;
using static Notify.Database;

namespace Notify;

public sealed class SsoClient
{
    private readonly Settings settings;
    private readonly Database db;
    private readonly IHttpClientFactory http;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> metadata;
    public SsoClient(Settings settings, Database db, IHttpClientFactory http, IHostEnvironment environment)
    {
        this.settings = settings; this.db = db; this.http = http;
        metadata = new ConfigurationManager<OpenIdConnectConfiguration>(settings.SsoIssuer + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http.CreateClient("sso")) { RequireHttps = !environment.IsDevelopment() });
    }
    public async Task<JwtSecurityToken> Validate(string token, string audience, string use, CancellationToken ct)
    {
        if (!settings.SsoConfigured || token.Length > 16384) throw new Fault(503, "sso_not_configured");
        OpenIdConnectConfiguration configuration;
        try { configuration = await metadata.GetConfigurationAsync(ct); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException) { throw new Fault(503, "sso_metadata_unavailable"); }
        var validation = new TokenValidationParameters { ValidIssuer = settings.SsoIssuer, ValidAudience = audience, IssuerSigningKeys = configuration.SigningKeys, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true, ValidAlgorithms = ["RS256"], ClockSkew = TimeSpan.Zero };
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            handler.ValidateToken(token, validation, out var validated); var jwt = (JwtSecurityToken)validated;
            if (jwt.Subject != settings.OwnerSub || Text(jwt, "token_use") != use || !jwt.Payload.ContainsKey("iat")) throw new Fault(403, "sso_owner_or_profile_mismatch");
            return jwt;
        }
        catch (SecurityTokenSignatureKeyNotFoundException) { metadata.RequestRefresh(); throw new Fault(401, "sso_signing_key_changed_retry"); }
        catch (SecurityTokenException) { throw new Fault(401, "invalid_sso_token"); }
    }
    private static string? Text(JwtSecurityToken jwt, string key) => jwt.Payload.TryGetValue(key, out var value) ? value as string : null;
    public async Task<object> StartEnrollment(EnrollDevice input, string accessToken, NotificationService service, CancellationToken ct)
    {
        _ = await Validate(accessToken, settings.AndroidClientId, "access_token", ct);
        var e = service.BeginEnrollment(input, settings.OwnerSub);
        var state = Wire.Secret(); var nonce = Wire.Secret(); var verifier = Wire.Secret();
        var hash = Wire.Hash(Wire.Encode(new { owner = settings.OwnerSub, enrollmentId = e.Id, device = input }));
        var action = input.Recovery ? "notify.recover_devices" : "notify.enroll_device";
        var saved = new StepUpState(state, "enrollment", e.Id, settings.OwnerSub, hash, state, nonce, verifier, e.ExpiresAt);
        db.Run((c, t) => { Put(c, t, "stepup", state, saved); return 0; });
        var request = new { subject = settings.OwnerSub, action, payload_hash = hash, description = input.Recovery ? "Восстановить Notify и отозвать прежние устройства" : "Привязать устройство Notify: " + input.Name, assurance = "reauth_password_mfa", redirect_uri = settings.PublicUrl + "/auth/step-up/callback", state, nonce, code_challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), code_challenge_method = "S256", ttl_seconds = 300 };
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.SsoIssuer + "/api/v1/step-up/requests") { Content = JsonContent.Create(request) }; Basic(message);
        using var response = await http.CreateClient("sso").SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw new Fault(503, "sso_step_up_unavailable");
        var document = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        var url = document.GetProperty("authorization_url").GetString() ?? throw new Fault(502, "invalid_sso_response");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != settings.SsoIssuer) throw new Fault(502, "invalid_sso_authorization_origin");
        return new { enrollmentId = e.Id, authorizationUrl = url, expiresAt = e.ExpiresAt };
    }
    private void Basic(HttpRequestMessage request) => request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.WebClientId + ":" + settings.WebClientSecret)));
    public async Task Complete(string state, string code, string issuer, NotificationService service, CancellationToken ct)
    {
        if (issuer != settings.SsoIssuer || state.Length > 128 || code.Length > 2048) throw new Fault(400, "invalid_sso_callback");
        var saved = db.Run((c, t) => Get<StepUpState>(c, t, "stepup", state)) ?? throw new Fault(400, "invalid_step_up_state");
        if (saved.ExpiresAt <= Wire.Now || saved.Owner != settings.OwnerSub || saved.State != state) throw new Fault(410, "step_up_expired");
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.SsoIssuer + "/api/v1/step-up/redeem") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "code", code }, { "code_verifier", saved.Verifier }, { "redirect_uri", settings.PublicUrl + "/auth/step-up/callback" } }) };
        // SSO's redeem endpoint consumes JSON, unlike the standard OAuth token endpoint.
        request.Content = JsonContent.Create(new { code, code_verifier = saved.Verifier, redirect_uri = settings.PublicUrl + "/auth/step-up/callback" }); Basic(request);
        using var response = await http.CreateClient("sso").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new Fault(403, "step_up_redemption_failed");
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        var proof = await Validate(json.GetProperty("proof").GetString() ?? "", settings.WebClientId, "external_step_up_proof", ct);
        var e = db.Run((c, t) => Get<Enrollment>(c, t, "enrollment", saved.TargetId)) ?? throw new Fault(404, "enrollment_not_found");
        var expectedAction = e.Device.Recovery ? "notify.recover_devices" : "notify.enroll_device";
        var amr = proof.Claims.Where(c => c.Type == "amr").Select(c => c.Value).ToHashSet();
        if (Text(proof, "action") != expectedAction || Text(proof, "payload_hash") != saved.PayloadHash || Text(proof, "nonce") != saved.Nonce || Text(proof, "acr") != "reauth_password_mfa" || !amr.Contains("pwd") || !(amr.Contains("otp") || amr.Contains("hwk")) || string.IsNullOrEmpty(Text(proof, "transaction_id")) || !proof.Payload.ContainsKey("security_revision")) throw new Fault(403, "step_up_binding_mismatch");
        db.Run((c, t) =>
        {
            var current = Get<StepUpState>(c, t, "stepup", state) ?? throw new Fault(409, "step_up_replayed");
            if (current != saved || current.ExpiresAt <= Wire.Now) throw new Fault(410, "step_up_expired");
            service.CompleteEnrollment(c, t, saved.TargetId);
            Exec(c, t, "DELETE FROM objects WHERE kind='stepup' AND id=$i", ("$i", state)); return 0;
        });
    }
}
