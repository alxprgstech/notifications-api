using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static Notify.Database;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Notify;

internal sealed record ChatGptLink(string SourceId, string ClientId, string? PendingSecret, string? RedirectUri);
internal sealed record ChatGptCallback(string RedirectUri);
internal sealed class ChatGptDb(DbContextOptions<ChatGptDb> options) : DbContext(options);

internal sealed class ChatGptOAuth(Database db, Settings settings, NotificationService notify, IOpenIddictApplicationManager applications)
{
    public const string McpPath = "/chatgpt/mcp";
    private const string MetadataPath = "/.well-known/oauth-protected-resource/chatgpt/mcp";
    private const string AuthorizePath = "/chatgpt/oauth/authorize";
    private const string Validation = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private const string LinkId = "current";
    internal const string LinkAction = "chatgpt.link";
    internal static readonly string[] PermissionsForSource = ["notify:send", "requests:create", "requests:read", "requests:cancel"];

    private static string Resource(Settings settings) => settings.PublicUrl.TrimEnd('/') + McpPath;
    private static string Metadata(Settings settings) => settings.PublicUrl.TrimEnd('/') + MetadataPath;

    public static void Register(WebApplicationBuilder builder, Settings settings)
    {
        var issuer = new Uri(settings.PublicUrl);
        builder.Services.AddSingleton(services =>
        {
            var directory = Path.Combine(services.GetRequiredService<Settings>().DataPath, "chatgpt");
            Directory.CreateDirectory(directory);
            return new ChatGptKeys(directory);
        });
        builder.Services.AddDbContext<ChatGptDb>((services, options) =>
        {
            var path = Path.Combine(services.GetRequiredService<Settings>().DataPath, "chatgpt", "oauth.db");
            var connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            options.UseSqlite(connection).UseOpenIddict();
        });
        builder.Services.AddScoped<ChatGptOAuth>();
        builder.Services.AddSingleton<ChatGptVoice>();
        builder.Services.AddHttpClient("silero", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["ChatGpt:SileroUrl"] ?? "http://silero:8080");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<ChatGptDb>())
            .AddServer(options =>
            {
                options.SetIssuer(issuer)
                    .SetConfigurationEndpointUris(".well-known/oauth-authorization-server")
                    .SetAuthorizationEndpointUris(AuthorizePath)
                    .SetTokenEndpointUris("/chatgpt/oauth/token")
                    .SetRevocationEndpointUris("/chatgpt/oauth/revoke")
                    .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange()
                    .RegisterScopes(PermissionsForSource).RegisterResources(Resource(settings))
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2))
                    .SetAccessTokenLifetime(TimeSpan.FromMinutes(10))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(30))
                    .SetRefreshTokenReuseLeeway(TimeSpan.Zero)
                    .DisableSlidingRefreshTokenExpiration();
                options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (!HasExactResource(context.Request, Resource(settings)))
                            context.Reject(Errors.InvalidTarget);
                        return default;
                    }));
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (!(context.Request.IsRefreshTokenGrantType() && context.Request.GetResources().Length == 0) &&
                            !HasExactResource(context.Request, Resource(settings)))
                            context.Reject(Errors.InvalidTarget);
                        return default;
                    }));
                var aspNet = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                if (builder.Environment.IsDevelopment() && issuer.Scheme == "http" && issuer.IsLoopback)
                    aspNet.DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.AddAudiences(Resource(settings));
                options.EnableTokenEntryValidation();
                options.UseAspNetCore();
            });
        builder.Services.AddOptions<OpenIddictServerOptions>().Configure<ChatGptKeys>((options, keys) =>
        {
            options.SigningCredentials.Add(new SigningCredentials(keys.Signing, SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                keys.Encryption, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512));
        });
        builder.Services.AddAuthorizationBuilder().AddPolicy("chatgpt-mcp", policy =>
        {
            policy.AddAuthenticationSchemes(Validation);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.Resource is HttpContext http &&
                http.RequestServices.GetRequiredService<ChatGptOAuth>().GetActor(context.User) != null);
        });
        builder.Logging.AddFilter("OpenIddict", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
    }

    public static void Map(WebApplication app, RouteGroupBuilder api, Settings settings)
    {
        app.MapGet(MetadataPath, () => Results.Json(new
        {
            resource = Resource(settings),
            authorization_servers = new[] { new Uri(settings.PublicUrl).AbsoluteUri },
            scopes_supported = PermissionsForSource.Append(Scopes.OfflineAccess),
            bearer_methods_supported = new[] { "header" }
        })).ExcludeFromDescription();
        app.MapMethods(AuthorizePath, ["GET", "POST"], (HttpContext http, ChatGptOAuth oauth, IAntiforgery csrf) =>
            oauth.Authorize(http, csrf)).ExcludeFromDescription();
        app.MapPost("/chatgpt/oauth/token", (HttpContext http, ChatGptOAuth oauth) =>
            oauth.Exchange(http)).ExcludeFromDescription();
        app.MapGet(McpPath, (HttpContext http) =>
        {
            http.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).RequireAuthorization("chatgpt-mcp").ExcludeFromDescription();
        app.MapMcp(McpPath).RequireAuthorization("chatgpt-mcp").ExcludeFromDescription();

        api.MapGet("/chatgpt", (HttpContext http, ChatGptOAuth oauth) => oauth.Status(http)).ExcludeFromDescription();
        api.MapPost("/chatgpt/callback", (HttpContext http, ChatGptCallback input, ChatGptOAuth oauth, IWebHostEnvironment env) =>
            oauth.ConfigureCallback(http, input.RedirectUri, env.IsDevelopment())).ExcludeFromDescription();
        api.MapPost("/chatgpt/disconnect", (HttpContext http, ChatGptOAuth oauth) =>
            oauth.Disconnect(http)).ExcludeFromDescription();
    }

    public static void ProtectResponse(HttpContext http, Settings settings)
    {
        if (http.Request.Path.StartsWithSegments("/chatgpt") ||
            http.Request.Path == MetadataPath || http.Request.Path == "/.well-known/oauth-authorization-server")
            http.Response.Headers.CacheControl = "no-store";
        if (http.Request.Path != McpPath) return;
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode == 401)
            {
                var current = http.Response.Headers.WWWAuthenticate.ToString();
                var scopes = string.Join(' ', PermissionsForSource.Append(Scopes.OfflineAccess));
                http.Response.Headers.WWWAuthenticate =
                    (current.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? current + ", " : "Bearer ") +
                    $"resource_metadata=\"{Metadata(settings)}\", scope=\"{scopes}\"";
            }
            return Task.CompletedTask;
        });
    }

    internal static object ApproveLink(SqliteConnection c, SqliteTransaction t, Func<string, string> protect)
    {
        RevokeLink(c, t);
        var sourceId = Wire.Id();
        var clientId = "notify-chatgpt-" + Wire.Id();
        var clientSecret = Wire.Secret();
        Put(c, t, "source", sourceId, new Source(sourceId, "ChatGPT", PermissionsForSource));
        Put(c, t, "chatgpt_link", LinkId, new ChatGptLink(sourceId, clientId, protect(clientSecret), null));
        return new { sourceId, clientId, clientSecret };
    }

    private static void RevokeLink(SqliteConnection c, SqliteTransaction t)
    {
        if (Get<ChatGptLink>(c, t, "chatgpt_link", LinkId) is not { } previous) return;
        if (Get<Source>(c, t, "source", previous.SourceId) is { } source)
            Put(c, t, "source", source.Id, source with { Revoked = true });
        Exec(c, t, "DELETE FROM objects WHERE kind='chatgpt_link' AND id=$i", ("$i", LinkId));
    }

    private ChatGptLink? ActiveLink() => db.Run((c, t) =>
    {
        var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
        return link != null && Get<Source>(c, t, "source", link.SourceId) is { Revoked: false } ? link : null;
    });

    public Actor? GetActor(ClaimsPrincipal principal) => db.Run((c, t) =>
    {
        var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
        if (link?.RedirectUri == null || principal.GetClaim(Claims.Subject) != settings.OwnerSub ||
            !(principal.GetExpirationDate() > DateTimeOffset.UtcNow) ||
            !principal.GetPresenters().Contains(link.ClientId) ||
            Get<Source>(c, t, "source", link.SourceId) is not { Revoked: false } source)
            return null;
        return new Actor("integration", source.Id, principal.GetScopes().Intersect(source.Scopes, StringComparer.Ordinal).ToArray());
    });

    private object Status(HttpContext http)
    {
        RequireWeb(http);
        var link = ActiveLink();
        return new { url = Resource(settings), link?.ClientId, link?.RedirectUri };
    }

    private IResult Disconnect(HttpContext http)
    {
        RequireWeb(http);
        db.Run((c, t) => { RevokeLink(c, t); return 0; });
        return Results.NoContent();
    }

    private static readonly SemaphoreSlim ConfigureGate = new(1);
    private async Task<IResult> ConfigureCallback(HttpContext http, string value, bool development)
    {
        RequireWeb(http);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(development && uri.IsLoopback) &&
            (uri.Scheme != "https" || uri.Host != "chatgpt.com" || !uri.IsDefaultPort ||
                !(uri.AbsolutePath == "/connector_platform_oauth_redirect" ||
                  uri.AbsolutePath.StartsWith("/connector/oauth/", StringComparison.Ordinal))))
            throw new Fault(400, "invalid_chatgpt_callback");

        await ConfigureGate.WaitAsync(http.RequestAborted);
        try
        {
            var link = ActiveLink() ?? throw new Fault(404, "chatgpt_not_linked");
            var client = await applications.FindByClientIdAsync(link.ClientId, http.RequestAborted);
            var descriptor = new OpenIddictApplicationDescriptor();
            if (client is null)
            {
                if (link.PendingSecret == null) throw new Fault(409, "chatgpt_relink_required");
                descriptor.ClientId = link.ClientId;
                descriptor.ClientSecret = notify.Unprotect(link.PendingSecret);
                descriptor.ClientType = ClientTypes.Confidential;
                descriptor.ConsentType = ConsentTypes.Explicit;
                descriptor.DisplayName = "ChatGPT Notify";
                descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                    Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code]);
                descriptor.AddResourcePermissions(Resource(settings));
                foreach (var scope in PermissionsForSource) descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
                descriptor.RedirectUris.Add(uri);
                await applications.CreateAsync(descriptor, http.RequestAborted);
            }
            else
            {
                await applications.PopulateAsync(descriptor, client, http.RequestAborted);
                descriptor.RedirectUris.Clear();
                descriptor.RedirectUris.Add(uri);
                await applications.UpdateAsync(client, descriptor, http.RequestAborted);
            }
            db.Run((c, t) =>
            {
                if (Get<ChatGptLink>(c, t, "chatgpt_link", LinkId)?.ClientId != link.ClientId ||
                    Get<Source>(c, t, "source", link.SourceId) is not { Revoked: false })
                    throw new Fault(409, "chatgpt_link_changed");
                Put(c, t, "chatgpt_link", LinkId, link with { PendingSecret = null, RedirectUri = uri.AbsoluteUri });
                return 0;
            });
            return Results.NoContent();
        }
        finally { ConfigureGate.Release(); }
    }

    private async Task<IResult> Authorize(HttpContext http, IAntiforgery antiforgery)
    {
        var request = http.GetOpenIddictServerRequest()!;
        var link = ActiveLink();
        if (link?.RedirectUri == null || link.ClientId != request.ClientId || link.RedirectUri != request.RedirectUri)
            return Reject(Errors.InvalidClient);
        var scopes = request.GetScopes();
        if (!scopes.Any(scope => PermissionsForSource.Contains(scope))) return Reject(Errors.InvalidScope);

        var session = await http.AuthenticateAsync("web");
        if (session.Principal?.FindFirstValue("sub") != settings.OwnerSub)
            return HttpMethods.IsGet(http.Request.Method)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString }, ["sso"])
                : Reject(Errors.LoginRequired);

        if (HttpMethods.IsGet(http.Request.Method))
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            var html = new StringBuilder("<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Notify</title>" +
                "<h1>Подключить ChatGPT к Notify</h1><p>Разрешить следующие действия для выделенного источника?</p><ul>");
            foreach (var scope in scopes) html.Append($"<li>{HtmlEncoder.Default.Encode(scope)}</li>");
            html.Append($"</ul><form method=\"post\" action=\"{AuthorizePath}\">");
            foreach (var parameter in http.Request.Query)
                foreach (var value in parameter.Value)
                    html.Append(Hidden(parameter.Key, value ?? ""));
            html.Append(Hidden(tokens.FormFieldName, tokens.RequestToken!));
            html.Append("<button name=\"decision\" value=\"allow\">Разрешить</button> " +
                "<button name=\"decision\" value=\"deny\">Отклонить</button></form></html>");
            return Results.Content(html.ToString(), "text/html; charset=utf-8");
        }

        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "invalid_csrf" }); }
        if ((await http.Request.ReadFormAsync(http.RequestAborted))["decision"] != "allow")
            return Reject(Errors.AccessDenied);

        var identity = new ClaimsIdentity(Scheme);
        identity.SetClaim(Claims.Subject, settings.OwnerSub);
        identity.SetScopes(scopes);
        identity.SetResources(Resource(settings));
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: Scheme);
    }

    private async Task<IResult> Exchange(HttpContext http)
    {
        var authorization = await http.AuthenticateAsync(Scheme);
        var principal = authorization.Principal;
        var link = ActiveLink();
        if (link?.RedirectUri == null || principal?.GetClaim(Claims.Subject) != settings.OwnerSub ||
            !principal.GetResources().SequenceEqual([Resource(settings)]) ||
            !principal.GetPresenters().Contains(link.ClientId) ||
            (http.GetOpenIddictServerRequest() is { } request && request.IsAuthorizationCodeGrantType() &&
                request.RedirectUri != link.RedirectUri))
            return Reject(Errors.InvalidGrant);
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    private static void RequireWeb(HttpContext http)
    {
        if (Auth.Actor(http).Kind != "web") throw new Fault(403, "web_required");
    }

    private static bool HasExactResource(OpenIddictRequest request, string expected)
    {
        var resources = request.GetResources();
        return resources.Length == 1 && resources[0] == expected;
    }

    private static IResult Reject(string error) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error
    }), [Scheme]);

    private static string Hidden(string name, string value) =>
        $"<input type=\"hidden\" name=\"{HtmlEncoder.Default.Encode(name)}\" value=\"{HtmlEncoder.Default.Encode(value)}\">";

    private static string? ToolScope(string name) => name switch
    {
        "send_notification" or "send_voice" => "notify:send",
        "request_confirmation" or "request_form" => "requests:create",
        "get_request" or "wait_for_response" => "requests:read",
        "cancel_request" => "requests:cancel",
        _ => null
    };

    public static IMcpServerBuilder WithMcpFilters(IMcpServerBuilder builder, Settings settings) =>
        builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var path = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext?.Request.Path;
                return new ListToolsResult
                {
                    NextCursor = result.NextCursor,
                    Tools = path == McpPath
                        ? result.Tools.Where(tool => ToolScope(tool.Name) != null).Select(DescribeOAuth).ToArray()
                        : result.Tools.Where(tool => tool.Name != "send_voice").ToArray()
                };
            });
            filters.AddCallToolFilter(next => (context, ct) =>
            {
                var name = context.Params?.Name;
                var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (http?.Request.Path != McpPath)
                    return name == "send_voice" ? ValueTask.FromResult(ToolError("unknown_tool")) : next(context, ct);
                var actor = http.RequestServices.GetRequiredService<ChatGptOAuth>().GetActor(http.User);
                if (actor == null) return ValueTask.FromResult(ToolError("unauthenticated"));
                var scope = name == null ? null : ToolScope(name);
                if (scope == null) return ValueTask.FromResult(ToolError("unknown_tool"));
                if (!actor.Scopes.Contains(scope))
                {
                    var error = ToolError("insufficient_scope");
                    error.Meta = new JsonObject
                    {
                        ["mcp/www_authenticate"] = new JsonArray(
                            $"Bearer resource_metadata=\"{Metadata(settings)}\", scope=\"{scope}\", error=\"insufficient_scope\", " +
                            "error_description=\"Reconnect Notify to grant the required scope\"")
                    };
                    return ValueTask.FromResult(error);
                }
                return next(context, ct);
            });
        });

    private static CallToolResult ToolError(string code) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = code }] };

    private static Tool DescribeOAuth(Tool tool) => new()
    {
        Name = tool.Name, Title = tool.Title, Description = tool.Description, InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema, Annotations = tool.Annotations,
        Meta = new JsonObject
        {
            ["securitySchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauth2", ["scopes"] = new JsonArray(ToolScope(tool.Name))
            })
        }
    };
}
