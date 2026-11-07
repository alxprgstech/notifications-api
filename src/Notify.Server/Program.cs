using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using Microsoft.OpenApi;
using Notify;
using static Notify.Database;

if (args.Length == 1 && args[0] == "health")
{
    try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; Environment.Exit((await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1); }
    catch { Environment.Exit(1); }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
// Registered URLs can contain private query values; delivery failures are logged by job ID instead.
builder.Logging.AddFilter("System.Net.Http.HttpClient.webhook", LogLevel.None);
var settings = builder.Configuration.GetSection("Notify").Get<Settings>() ?? new Settings(); settings.Validate(builder.Environment.IsDevelopment());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<AudioService>();
builder.Services.AddSingleton<CallService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<SsoClient>();
builder.Services.AddSingleton<IPushSender, GooglePushSender>();
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddDataProtection().SetApplicationName("alxprgs-notify-v1");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>().Configure<Settings, ILoggerFactory>((o, s, l) => { var path = Path.Combine(s.DataPath, "keys"); Directory.CreateDirectory(path); o.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(path), l); });
builder.Services.AddHttpClient("sso", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(WebhookPolicy.Handler);
builder.Services.AddHttpContextAccessor();
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectNullableAnnotations = true; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 11 * 1024 * 1024; o.Limits.MaxConcurrentConnections = 128; });
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "ALXPRGS Notify API"; document.Info.Version = "v1";
        document.Info.Description = "Messages, action-specific confirmations, structured forms, voice messages and live calls. Integration tokens cannot submit user decisions or start a call for the owner.";
        document.Components ??= new OpenApiComponents(); document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Integration/device token. Device enrollment requires an SSO Access Token; never an ID Token." };
        return Task.CompletedTask;
    });
    o.AddOperationTransformer((operation, context, _) =>
    {
        var path = context.Description.RelativePath ?? "";
        if (path.StartsWith("api/v1/", StringComparison.Ordinal) && !(path.StartsWith("api/v1/device-enrollments/", StringComparison.Ordinal) && context.Description.HttpMethod == "GET"))
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }];
        return Task.CompletedTask;
    });
});
builder.Services.AddMcpServer().WithHttpTransport().WithTools<NotifyTools>();
var secureCookies = new Uri(settings.PublicUrl).Scheme == "https";
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<Settings>((o, config) =>
{
    if (string.IsNullOrEmpty(config.TrustedProxyNetwork)) return;
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(config.TrustedProxyNetwork));
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-Token"; o.Cookie.Name = "notify.csrf"; o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddAuthentication(o => { o.DefaultScheme = "local"; o.DefaultChallengeScheme = "local"; })
    .AddScheme<AuthenticationSchemeOptions, LocalAuthentication>("local", _ => { })
    .AddCookie("web", o =>
    {
        o.Cookie.Name = secureCookies ? "__Host-notify.session" : "notify.session"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    })
    .AddOpenIdConnect("sso", o =>
    {
        o.Authority = settings.SsoIssuer; o.ClientId = settings.WebClientId; o.ClientSecret = settings.WebClientSecret; o.ResponseType = "code"; o.ResponseMode = "query"; o.UsePkce = true; o.SaveTokens = false; o.SignInScheme = "web"; o.MapInboundClaims = false;
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); o.Scope.Clear(); o.Scope.Add("openid");
        o.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = settings.SsoIssuer, ValidAudience = settings.WebClientId, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RequireExpirationTime = true, ValidAlgorithms = ["RS256"], ClockSkew = SsoClient.TokenClockSkew, NameClaimType = "sub" };
        o.CorrelationCookie.SameSite = SameSiteMode.Lax; o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = o.NonceCookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.Events.OnRedirectToIdentityProvider = c => { c.ProtocolMessage.RedirectUri = settings.PublicUrl + "/signin-oidc"; return Task.CompletedTask; };
        o.Events.OnTokenValidated = c =>
        {
            if (!settings.SsoConfigured || c.Principal?.FindFirstValue("sub") != settings.OwnerSub || c.Principal.FindFirstValue("token_use") != "id_token") c.Fail("Owner/profile mismatch");
            else ((ClaimsIdentity)c.Principal.Identity!).AddClaim(new Claim("notify_sid", Wire.Secret()));
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = c => { c.HandleResponse(); c.Response.StatusCode = 401; return c.Response.WriteAsJsonAsync(new { code = "sso_login_failed" }); };
    });
builder.Services.AddOptions<CookieAuthenticationOptions>("web").Configure<IDataProtectionProvider>((o, protection) =>
    o.TicketDataFormat = new TicketDataFormat(protection.CreateProtector("Notify.WebSession.v2")));
builder.Services.AddOptions<OpenIdConnectOptions>("sso").Configure<IHttpContextAccessor, IDataProtectionProvider>((o, http, protection) =>
    o.StateDataFormat = new WebLoginState(http, protection, o.CallbackPath, o.RemoteAuthenticationTimeout, secureCookies));
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
settings = app.Services.GetRequiredService<Settings>(); settings.Validate(app.Environment.IsDevelopment());
_ = app.Services.GetRequiredService<Database>();
_ = app.Services.GetRequiredService<CallService>();
if (args.Length == 2 && args[0] == "backup") { app.Services.GetRequiredService<Database>().Backup(args[1]); return; }
if (!string.IsNullOrEmpty(settings.TrustedProxyNetwork)) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/android")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Fault e) when (!context.Response.HasStarted) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { code = e.Code }); }
    catch (BadHttpRequestException) when (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request" }); }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        app.Logger.LogError("Request failed: {Type}", e.GetType().Name); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = "internal_error" });
    }
});
app.UseRateLimiter(); app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) }); app.UseAuthentication(); app.UseAuthorization();
app.Use(async (c, next) =>
{
    if (c.Request.Path.StartsWithSegments("/api/v1") && c.User.Identity?.IsAuthenticated == true && c.User.FindFirstValue("notify_kind") == "web" && HttpMethods.IsPost(c.Request.Method))
    {
        if (c.Request.Headers.Origin != settings.PublicUrl) throw new Fault(403, "invalid_origin");
        try { await c.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(c); }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { throw new Fault(403, "invalid_csrf"); }
    }
    await next();
});
app.MapGet("/health/live", () => new { status = "alive" });
app.MapGet("/health/ready", () => settings.SsoConfigured && settings.FirebaseCredentials != null && File.Exists(settings.FirebaseCredentials) ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "configuration_required", sso = settings.SsoConfigured, fcm = settings.FirebaseCredentials != null && File.Exists(settings.FirebaseCredentials) }, statusCode: 503));
app.MapGet("/api/config", () => new
{
    version = "0.5.0", settings.SsoIssuer, settings.AndroidClientId, settings.PublicUrl, ssoConfigured = settings.SsoConfigured,
    capabilities = new { message = true, confirmation = true, elevated = true, forms = true, voiceMessages = true, calls = true, agents = true },
    protocols = new { answer = "alxprgs.notify.answer.v1", form = "alxprgs.notify.form.v1", audio = "alxprgs.notify.audio.v1", agent = AgentHub.Protocol, call = CallService.Protocol },
    minClientVersion = new { forms = "0.2.0", voiceMessages = "0.3.0", calls = "0.5.0" }
});
app.MapGet("/.well-known/assetlinks.json", () => settings.AndroidSigningFingerprints.Length == 0 ? Results.Json(Array.Empty<object>(), statusCode: 503) : Results.Json(new[] { new { relation = new[] { "delegate_permission/common.handle_all_urls" }, target = new { @namespace = "android_app", package_name = "tech.alxprgs.notify", sha256_cert_fingerprints = settings.AndroidSigningFingerprints } } }));
app.MapGet("/auth/login", () => settings.SsoConfigured ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["sso"]) : Results.Problem(statusCode: 503, title: "SSO not configured"));
app.MapGet("/android/oauth2redirect", () => BrowserReturnPage.Render("Вернитесь в приложение Notify", "Если Android не открыл его автоматически, проверьте разрешение открытия ссылок для Notify в настройках Android.")).ExcludeFromDescription();
app.MapGet("/auth/step-up/callback", async (string state, string code, string iss, SsoClient sso, NotificationService service, CancellationToken ct) => { await sso.Complete(state, code, iss, service, ct); return BrowserReturnPage.Render("Устройство привязано", "Вернитесь в приложение Notify, чтобы завершить настройку."); });
app.MapPost("/api/v1/device-enrollments", async (EnrollDevice input, HttpContext c, SsoClient sso, NotificationService service, CancellationToken ct) =>
{
    var header = c.Request.Headers.Authorization.ToString(); if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) throw new Fault(401, "sso_access_token_required");
    return await sso.StartEnrollment(input, header[7..], service, ct);
});
app.MapGet("/api/v1/device-enrollments/{id}", (string id, NotificationService service) => { if (id.Length != 43) throw new Fault(404, "enrollment_not_found"); return service.EnrollmentResult(id); });
var api = app.MapGroup("/api/v1").RequireAuthorization();
((IEndpointConventionBuilder)api).Add(endpoint =>
{
    var path = (endpoint as Microsoft.AspNetCore.Routing.RouteEndpointBuilder)?.RoutePattern.RawText ?? "";
    var tag = path.Contains("/notifications", StringComparison.Ordinal) ? "Уведомления" : path.Contains("/icons", StringComparison.Ordinal) ? "Иконки" : path.Contains("/devices", StringComparison.Ordinal) ? "Устройства" : path.Contains("/management", StringComparison.Ordinal) || path.Contains("/sources", StringComparison.Ordinal) ? "Управление" : "Кабинет и синхронизация";
    endpoint.Metadata.Add(new TagsAttribute(tag));
});
api.MapGet("/session", (HttpContext c, Microsoft.AspNetCore.Antiforgery.IAntiforgery csrf) => new { actor = Auth.Actor(c), csrfToken = csrf.GetAndStoreTokens(c).RequestToken });
api.MapPost("/logout", async (HttpContext c) => { if (Auth.Actor(c).Kind != "web") throw new Fault(403, "web_required"); await c.SignOutAsync("web"); return Results.NoContent(); });
api.MapPost("/notifications", (CreateNotification input, HttpContext c, NotificationService service) => service.Create(Auth.Actor(c), input, c.Request.Headers["Idempotency-Key"].ToString()));
api.MapGet("/notifications", (HttpContext c, NotificationService service, long? before, string? beforeId, int? limit) => service.List(Auth.Actor(c), before ?? long.MaxValue, beforeId ?? "~", limit ?? 50));
api.MapGet("/notifications/{id}", (string id, HttpContext c, NotificationService service) => service.Read(Auth.Actor(c), id));
api.MapGet("/notifications/{id}/wait", async (string id, int? seconds, HttpContext c, NotificationService service, CancellationToken ct) =>
{
    var actor = Auth.Actor(c); actor.RequireScope("requests:read"); var count = seconds ?? 20; if (count is < 0 or > 25) throw new Fault(400, "invalid_wait_seconds");
    var end = Wire.Now + count * 1000; Notification n;
    while ((n = service.Read(actor, id)).Status == "pending" && Wire.Now < end) await Task.Delay(500, ct);
    return n;
});
api.MapPost("/notifications/{id}/challenge", (string id, Answer answer, HttpContext c, NotificationService service) => service.Challenge(Auth.Actor(c), id, answer));
api.MapPost("/notifications/{id}/form-challenge", (string id, FormValues values, HttpContext c, NotificationService service) => service.ChallengeForm(Auth.Actor(c), id, values));
api.MapPost("/notifications/{id}/audio-challenge", (string id, HttpContext c, NotificationService service) => service.AudioChallenge(Auth.Actor(c), id));
api.MapPost("/notifications/{id}/audio-grant", (string id, AudioGrantInput input, HttpContext c, NotificationService service) => service.GrantAudio(Auth.Actor(c), id, input));
api.MapPost("/audio/uploads", async (HttpContext c, AudioService audio, CancellationToken ct) =>
{
    var actor = Auth.Actor(c); if (actor.Kind == "integration") actor.RequireScope("notify:send");
    return await audio.UploadAsync(actor, c.Request, ct);
});
api.MapGet("/audio/{id}", (string id, HttpContext c, NotificationService service) => Results.File(service.AudioFile(Auth.Actor(c), id, c.Request.Headers["X-Notify-Audio-Grant"].ToString()), "audio/wav", enableRangeProcessing: true));
api.MapPost("/voice-messages", async (VoiceSend input, HttpContext c, NotificationService service, CancellationToken ct) => await service.SendVoiceAsync(Auth.Actor(c), input, ct));
api.MapGet("/voice-messages/{id}", (string id, HttpContext c, NotificationService service) => service.VoiceStatus(Auth.Actor(c), id));
api.MapDelete("/voice-messages/{id}", (string id, HttpContext c, NotificationService service) => { service.DeleteVoice(Auth.Actor(c), id); return Results.NoContent(); });
api.MapPost("/calls/challenge", (CallChallengeInput input, HttpContext c, CallService calls) => calls.Challenge(Auth.Actor(c), input));
api.MapPost("/calls", async (CallStart input, HttpContext c, CallService calls, CancellationToken ct) => await calls.StartAsync(Auth.Actor(c), input, ct));
api.MapGet("/calls", (HttpContext c, CallService calls) => calls.History(Auth.Actor(c)));
api.MapGet("/calls/incoming", (HttpContext c, CallService calls) => calls.Incoming(Auth.Actor(c)));
api.MapGet("/calls/{id}", (string id, HttpContext c, CallService calls) => calls.State(Auth.Actor(c), id));
api.MapPost("/calls/{id}/challenge", (string id, HttpContext c, CallService calls) => calls.ChallengeIncoming(Auth.Actor(c), id));
api.MapPost("/calls/{id}/accept", async (string id, CallAcceptInput input, HttpContext c, CallService calls) => await calls.AcceptAsync(Auth.Actor(c), id, input));
api.MapPost("/calls/{id}/reject", async (string id, HttpContext c, CallService calls) => { await calls.RejectAsync(Auth.Actor(c), id); return Results.NoContent(); });
api.MapPost("/calls/{id}/end", async (string id, HttpContext c, CallService calls) => { await calls.EndByDeviceAsync(Auth.Actor(c), id); return Results.NoContent(); });
api.Map("/calls/{id}/audio", async (string id, HttpContext c, CallService calls, CancellationToken ct) =>
{
    var actor = Auth.Actor(c); calls.RequirePhone(actor, id);
    if (!c.WebSockets.IsWebSocketRequest) throw new Fault(400, "websocket_required");
    var socket = await c.WebSockets.AcceptWebSocketAsync();
    await calls.AttachPhoneAsync(actor, id, socket, ct);
}).ExcludeFromDescription();
api.Map("/agents/calls/{id}/audio", async (string id, HttpContext c, CallService calls, CancellationToken ct) =>
{
    var actor = Auth.Actor(c); calls.RequireAgent(actor, id);
    if (!c.WebSockets.IsWebSocketRequest) throw new Fault(400, "websocket_required");
    var socket = await c.WebSockets.AcceptWebSocketAsync();
    await calls.AttachAgentAsync(actor, id, socket, ct);
}).ExcludeFromDescription();
api.MapPost("/notifications/{id}/response", (string id, SubmitAnswer answer, HttpContext c, NotificationService service) => service.Respond(Auth.Actor(c), id, answer));
api.MapPost("/notifications/{id}/cancel", (string id, HttpContext c, NotificationService service) => service.Cancel(Auth.Actor(c), id));
api.MapPost("/notifications/{id}/receipt", (string id, ReceiptInput input, HttpContext c, NotificationService service) => { service.Receipt(Auth.Actor(c), id, input.Status); return Results.NoContent(); });
api.MapGet("/notifications/{id}/deliveries", (string id, HttpContext c, NotificationService service) => service.Deliveries(Auth.Actor(c), id));
api.MapGet("/sync", (long? after, HttpContext c, NotificationService service) => service.Sync(Auth.Actor(c), after ?? 0));
api.MapGet("/devices", (HttpContext c, NotificationService service) => service.Devices(Auth.Actor(c)));
api.MapGet("/agents", (HttpContext c, AgentHub hub) => hub.List(Auth.Actor(c)));
api.Map("/agents/connect", async (HttpContext c, AgentHub hub, CancellationToken ct) =>
{
    var actor = Auth.Actor(c);
    if (actor.Kind != "integration" || !actor.Scopes.Contains("agents:connect")) throw new Fault(403, "agents_scope_required");
    var agent = hub.ForSource(actor.Id) ?? throw new Fault(403, "agent_not_registered");
    if (!c.WebSockets.IsWebSocketRequest) throw new Fault(400, "websocket_required");
    var socket = await c.WebSockets.AcceptWebSocketAsync();
    await hub.Run(agent, socket, ct);
}).ExcludeFromDescription();
api.MapPost("/devices/fcm", (FcmInput input, HttpContext c, NotificationService service) => { service.SetFcm(Auth.Actor(c), input.Token); return Results.NoContent(); });
api.MapGet("/sources", (HttpContext c, NotificationService service) => service.Sources(Auth.Actor(c)));
api.MapPost("/management", (ManagementInput input, HttpContext c, NotificationService service) => { if (input.WebhookUrl != null) WebhookPolicy.Validate(input.WebhookUrl, settings); return service.BeginManagement(Auth.Actor(c), input); });
api.MapGet("/management/{id}", (string id, HttpContext c, NotificationService service) => service.ManagementResult(Auth.Actor(c), id));
api.MapPost("/icons", async (HttpContext c, Database db, CancellationToken ct) =>
{
    var actor = Auth.Actor(c); if (!actor.IsOwner) actor.RequireScope("icons:write");
    using var data = new MemoryStream(); await c.Request.Body.CopyToAsync(data, ct); var bytes = data.ToArray(); var type = IconValidator.Validate(bytes);
    var id = Wire.Hash(Convert.ToBase64String(bytes) + (actor.IsOwner ? "owner" : actor.Id)); var file = id + (type == "image/png" ? ".png" : ".webp");
    var directory = Path.Combine(settings.DataPath, "icons"); Directory.CreateDirectory(directory);
    db.Run((cn, t) =>
    {
        var existing = Get<Icon>(cn, t, "icon", id);
        var target = Path.Combine(directory, file);
        if (existing != null && File.Exists(target)) return 0;
        if (existing == null && long.Parse(Rows(cn, t, "SELECT CAST(count(*) AS TEXT) FROM objects WHERE kind='icon'")[0], System.Globalization.CultureInfo.InvariantCulture) >= 256) throw new Fault(409, "icon_quota_exceeded");
        var temporary = target + "." + Wire.Id() + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
        Put(cn, t, "icon", id, new Icon(id, actor.IsOwner ? null : actor.Id, type, file)); return 0;
    }); return new { id };
});
api.MapGet("/icons/{id}", (string id, HttpContext c, Database db) =>
{
    var actor = Auth.Actor(c); var icon = db.Run((cn, t) => NotificationService.RequireIcon(cn, t, id, actor));
    return Results.File(Path.GetFullPath(Path.Combine(settings.DataPath, "icons", icon.FileName)), icon.ContentType);
});
app.MapMcp("/mcp").RequireAuthorization();
app.MapOpenApi("/openapi/{documentName}.json");
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallback(async c =>
{
    if (c.Request.Path.StartsWithSegments("/api") || c.Request.Path.StartsWithSegments("/auth") || c.Request.Path.StartsWithSegments("/mcp")) { c.Response.StatusCode = 404; return; }
    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (File.Exists(index)) { c.Response.ContentType = "text/html; charset=utf-8"; await c.Response.SendFileAsync(index); }
    else { c.Response.StatusCode = 503; await c.Response.WriteAsJsonAsync(new { code = "web_build_required" }); }
});
app.Run();

public partial class Program;
public sealed record ReceiptInput(string Status);
public sealed record FcmInput(string Token);
public static class Auth
{
    public static Actor Actor(HttpContext c) => c.User.Identity?.IsAuthenticated == true ? new Actor(c.User.FindFirstValue("notify_kind")!, c.User.FindFirstValue("notify_id")!, c.User.FindAll("notify_scope").Select(x => x.Value).ToArray(), c.User.FindFirstValue("notify_sid")) : throw new Fault(401, "unauthenticated");
}
public sealed class LocalAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString(); Actor? actor = null;
        if (header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            if (header.Length > 512) return AuthenticateResult.Fail("Invalid bearer");
            actor = Context.RequestServices.GetRequiredService<NotificationService>().Authenticate(header[7..]);
            if (actor == null) return AuthenticateResult.Fail("Invalid credential");
        }
        else
        {
            var web = await Context.AuthenticateAsync("web");
            if (web.Succeeded && web.Principal?.FindFirstValue("sub") == Context.RequestServices.GetRequiredService<Settings>().OwnerSub)
            {
                var identity = (ClaimsIdentity)web.Principal.Identity!;
                identity.AddClaim(new Claim("notify_kind", "web")); identity.AddClaim(new Claim("notify_id", web.Principal.FindFirstValue("sub")!));
                return AuthenticateResult.Success(new AuthenticationTicket(web.Principal, Scheme.Name));
            }
            return AuthenticateResult.NoResult();
        }
        var claims = new List<Claim> { new("notify_kind", actor.Kind), new("notify_id", actor.Id) }; claims.AddRange(actor.Scopes.Select(s => new Claim("notify_scope", s)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
[McpServerToolType]
public sealed class NotifyTools(NotificationService service, IHttpContextAccessor context)
{
    private Actor Actor => Auth.Actor(context.HttpContext ?? throw new Fault(401, "integration_required"));
    [McpServerTool, System.ComponentModel.Description("Send an informational notification. Requires an idempotency key. Does not request approval.")]
    public string SendNotification(string title, string body, string idempotencyKey, string? iconId = null, string? correlationId = null) => Wire.Encode(service.Create(Actor, new CreateNotification("message", title, body, IconId: iconId, CorrelationId: correlationId), idempotencyKey));
    [McpServerTool, System.ComponentModel.Description("Request yes/no/custom approval. Never treat pending/expired/cancelled as approval. Elevated requires Android verification.")]
    public string RequestConfirmation(string title, string body, DateTimeOffset expiresAt, string idempotencyKey, string securityLevel = "standard", bool allowCustom = true, string? iconId = null, string? correlationId = null) => Wire.Encode(service.Create(Actor, new CreateNotification("confirmation", title, body, securityLevel, expiresAt, allowCustom, iconId, correlationId), idempotencyKey));
    [McpServerTool, System.ComponentModel.Description("Request a structured form (1-20 fields: text, boolean, single_choice, multi_choice, number). fieldsJson must be a JSON array of field objects. Elevated forms require Android verification.")]
    public string RequestForm(string title, string body, DateTimeOffset expiresAt, string idempotencyKey, string fieldsJson, string securityLevel = "standard", string? iconId = null, string? correlationId = null)
    {
        List<FormField> fields;
        try { fields = Wire.Decode<List<FormField>>(fieldsJson); } catch (JsonException) { throw new Fault(400, "invalid_form"); }
        return Wire.Encode(service.Create(Actor, new CreateNotification("form", title, body, securityLevel, expiresAt, false, iconId, correlationId, new FormSpec(fields)), idempotencyKey));
    }
    [McpServerTool] public string GetRequest(string id) => Wire.Encode(service.Read(Actor, id));
    [McpServerTool] public string CancelRequest(string id) => Wire.Encode(service.Cancel(Actor, id));
    [McpServerTool]
    public async Task<string> WaitForResponse(string id, int seconds = 20, CancellationToken cancellationToken = default)
    {
        var actor = Actor; actor.RequireScope("requests:read"); if (seconds is < 0 or > 25) throw new Fault(400, "invalid_wait_seconds"); var end = Wire.Now + seconds * 1000; Notification n;
        while ((n = service.Read(actor, id)).Status == "pending" && Wire.Now < end) await Task.Delay(500, cancellationToken); return Wire.Encode(n);
    }
}
