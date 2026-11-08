using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Notify;
using Notify.ChatGpt;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
var settings = AdapterSettings.Read(builder.Configuration, builder.Environment.IsDevelopment());
Directory.CreateDirectory(settings.DataPath);
using var keys = new AdapterKeys(settings.DataPath);

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
builder.Logging.AddFilter("OpenIddict", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Services.AddSingleton(settings);
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection().SetApplicationName("Notify.ChatGpt")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataPath, "browser-keys")));
builder.Services.AddDbContext<AdapterDb>(options => options.UseSqlite(settings.Database).UseOpenIddict());
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = settings.LocalHttp ? "notify.mcp.csrf" : "__Host-notify.mcp.csrf";
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = settings.LocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddAuthentication()
    .AddCookie("owner", options =>
    {
        options.Cookie.Name = settings.LocalHttp ? "notify.mcp.owner" : "__Host-notify.mcp.owner";
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = settings.LocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = false;
    })
    .AddOpenIdConnect("sso", options =>
    {
        options.Authority = settings.SsoIssuer.GetLeftPart(UriPartial.Authority);
        options.ClientId = settings.SsoClientId;
        options.ClientSecret = settings.SsoClientSecret;
        options.SignInScheme = "owner";
        options.CallbackPath = "/signin-oidc";
        options.ResponseType = "code";
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = settings.SsoIssuer.Scheme == "https";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = options.Authority,
            ValidAudience = settings.SsoClientId,
            ValidAlgorithms = ["RS256"],
            ClockSkew = TimeSpan.FromSeconds(5)
        };
        options.NonceCookie.SameSite = options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        if (settings.LocalHttp)
            options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = new Uri(settings.PublicUrl, "/signin-oidc").AbsoluteUri;
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            if (context.SecurityToken.Issuer != options.Authority ||
                context.Principal?.FindFirstValue("sub") != settings.OwnerSub ||
                context.Principal.FindFirstValue("token_use") != "id_token")
                context.Fail("sso_owner_required");
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { code = "sso_login_failed" });
        };
    });
builder.Services.AddOptions<OpenIdConnectOptions>("sso").Configure<IHttpContextAccessor, IDataProtectionProvider>(
    (options, http, protection) => options.StateDataFormat =
        new WebLoginState(http, protection, options.CallbackPath, options.RemoteAuthenticationTimeout, !settings.LocalHttp));

builder.Services.AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AdapterDb>())
    .AddServer(options =>
    {
        options.SetIssuer(settings.PublicUrl)
            .SetConfigurationEndpointUris(".well-known/oauth-authorization-server", ".well-known/openid-configuration")
            .SetAuthorizationEndpointUris("oauth/authorize")
            .SetTokenEndpointUris("oauth/token")
            .SetRevocationEndpointUris("oauth/revoke")
            .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange()
            .RegisterScopes(AdapterOAuth.Scopes)
            .RegisterResources(settings.Resource)
            .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2))
            .SetAccessTokenLifetime(TimeSpan.FromMinutes(10))
            .SetRefreshTokenLifetime(TimeSpan.FromDays(30))
            .SetRefreshTokenReuseLeeway(TimeSpan.Zero)
            .DisableSlidingRefreshTokenExpiration()
            .AddSigningKey(keys.Signing).AddEncryptionKey(keys.Encryption);
        options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
        options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler =>
            handler.UseInlineHandler(context =>
            {
                if (context.Request.ClientId != settings.ClientId)
                    context.Reject(Errors.InvalidClient);
                else if (!AdapterOAuth.HasExactResource(context.Request, settings.Resource))
                    context.Reject(Errors.InvalidTarget);
                else if (context.Request.CodeChallengeMethod != CodeChallengeMethods.Sha256)
                    context.Reject(Errors.InvalidRequest);
                return default;
            }));
        options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
            handler.UseInlineHandler(context =>
            {
                var resources = context.Request.GetResources();
                if (!(context.Request.IsRefreshTokenGrantType() && resources.Length == 0) &&
                    !AdapterOAuth.HasExactResource(context.Request, settings.Resource))
                    context.Reject(Errors.InvalidTarget);
                return default;
            }));
        var aspNet = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
        if (settings.LocalHttp) aspNet.DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.AddAudiences(settings.Resource);
        options.EnableTokenEntryValidation();
        options.UseAspNetCore();
    });

builder.Services.AddAuthorizationBuilder().AddPolicy("mcp", policy =>
{
    policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
    policy.RequireAuthenticatedUser();
    policy.RequireClaim(Claims.Subject, settings.OwnerSub);
    policy.RequireAssertion(context => context.User.GetExpirationDate() > DateTimeOffset.UtcNow &&
        context.User.GetPresenters().Contains(settings.ClientId));
});
builder.Services.AddHttpClient<NotifyApi>((_, client) =>
{
    client.BaseAddress = settings.NotifyUrl;
    client.DefaultRequestHeaders.Authorization = new("Bearer", settings.NotifyToken);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("silero", client =>
{
    client.BaseAddress = settings.SileroUrl;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<VoiceLocks>();
builder.Services.AddScoped<VoiceSender>();
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<ChatGptTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, ct) =>
        AdapterOAuth.ChallengeTool(context, settings) is { } challenge ? ValueTask.FromResult(challenge) : next(context, ct)));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdapterDb>();
    await db.Database.EnsureCreatedAsync();
    await AdapterOAuth.RegisterClient(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), settings);
}

if (settings.TrustedProxy is { } proxy)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    forwarded.KnownProxies.Add(proxy);
    app.UseForwardedHeaders(forwarded);
}
app.Use(async (context, next) =>
{
    if ((!settings.LocalHttp && !context.Request.IsHttps) ||
        !string.Equals(context.Request.Host.Value, settings.PublicUrl.Authority, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'";
    if (context.Request.Path == "/mcp")
    {
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == 401)
            {
                var challenge = context.Response.Headers.WWWAuthenticate.ToString();
                var scopes = string.Join(' ', AdapterOAuth.Scopes.Append(Scopes.OfflineAccess));
                context.Response.Headers.WWWAuthenticate =
                    (challenge.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? challenge + ", " : "Bearer ") +
                    $"resource_metadata=\"{settings.Metadata}\", scope=\"{scopes}\"";
            }
            return Task.CompletedTask;
        });
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new
{
    resource = settings.Resource,
    authorization_servers = new[] { settings.PublicUrl.AbsoluteUri },
    scopes_supported = AdapterOAuth.Scopes.Append(Scopes.OfflineAccess),
    bearer_methods_supported = new[] { "header" }
}));
app.MapMethods("/oauth/authorize", ["GET", "POST"], AdapterOAuth.Authorize);
app.MapPost("/oauth/token", AdapterOAuth.Exchange);
// Stateless MCP has no standalone SSE stream. Authenticate discovery probes before returning 405.
app.MapGet("/mcp", (HttpContext context) =>
{
    context.Response.Headers.Allow = "POST";
    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
}).RequireAuthorization("mcp");
app.MapMcp("/mcp").RequireAuthorization("mcp");

app.Run();
