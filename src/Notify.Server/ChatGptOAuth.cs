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

internal sealed record ChatGptLink(string SourceId, string ClientId, string? PendingSecret, string? RedirectUri, long PermissionsRevision = 0);
internal sealed record ChatGptCallback(string RedirectUri);
internal sealed class ChatGptDb(DbContextOptions<ChatGptDb> options) : DbContext(options);

internal sealed class ChatGptOAuth(Database db, Settings settings, NotificationService notify, IOpenIddictApplicationManager applications, CallService calls)
{
    public const string McpPath = "/chatgpt/mcp";
    private const string MetadataPath = "/.well-known/oauth-protected-resource/chatgpt/mcp";
    private const string AuthorizePath = "/chatgpt/oauth/authorize";
    private const string DefaultCallback = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string Validation = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private const string LinkId = "current";
    internal const string LinkAction = "chatgpt.link";
    internal const string PermissionsAction = "chatgpt.permissions.update";
    internal static readonly string[] PermissionsForSource = ["notify:send", "requests:create", "requests:read", "requests:cancel", ChatGptCall.Scope];

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
        builder.Services.AddSingleton<ChatGptCall>();
        builder.Services.AddSingleton<SileroSpeech>();
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

    internal static object ApproveLink(SqliteConnection c, SqliteTransaction t, Func<string, string> protect, out string? revokedSource)
    {
        revokedSource = RevokeLink(c, t);
        var sourceId = Wire.Id();
        var clientId = "notify-chatgpt-" + Wire.Id();
        var clientSecret = Wire.Secret();
        Put(c, t, "source", sourceId, new Source(sourceId, "ChatGPT", PermissionsForSource));
        Put(c, t, "chatgpt_link", LinkId, new ChatGptLink(sourceId, clientId, protect(clientSecret), null));
        return new { sourceId, clientId, clientSecret };
    }

    private static string? RevokeLink(SqliteConnection c, SqliteTransaction t)
    {
        if (Get<ChatGptLink>(c, t, "chatgpt_link", LinkId) is not { } previous) return null;
        if (Get<Source>(c, t, "source", previous.SourceId) is { } source)
            Put(c, t, "source", source.Id, source with { Revoked = true });
        Exec(c, t, "DELETE FROM objects WHERE kind='chatgpt_link' AND id=$i", ("$i", LinkId));
        return previous.SourceId;
    }

    private ChatGptLink? ActiveLink() => db.Run((c, t) =>
    {
        var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
        return link != null && Get<Source>(c, t, "source", link.SourceId) is { Revoked: false } ? link : null;
    });

    public Actor? GetActor(ClaimsPrincipal principal) => GetAccess(principal).Actor;

    private (Actor? Actor, bool Managed) GetAccess(ClaimsPrincipal principal) => db.Run<(Actor?, bool)>((c, t) =>
    {
        var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
        if (link?.RedirectUri == null || principal.GetClaim(Claims.Subject) != settings.OwnerSub ||
            !(principal.GetExpirationDate() > DateTimeOffset.UtcNow) ||
            !principal.GetPresenters().Contains(link.ClientId) ||
            Get<Source>(c, t, "source", link.SourceId) is not { Revoked: false } source)
            return (null, false);
        var managed = link.PermissionsRevision > 0;
        var allowed = source.Scopes.Intersect(PermissionsForSource, StringComparer.Ordinal);
        var scopes = managed ? allowed : principal.GetScopes().Intersect(allowed, StringComparer.Ordinal);
        return (new Actor("integration", source.Id, scopes.ToArray()), managed);
    });

    private object Status(HttpContext http)
    {
        RequireWeb(http);
        return db.Run((c, t) =>
        {
            var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
            var source = link == null ? null : Get<Source>(c, t, "source", link.SourceId);
            if (source is not { Revoked: false }) { link = null; source = null; }
            return new
            {
                url = Resource(settings), link?.ClientId, link?.RedirectUri, defaultRedirectUri = DefaultCallback,
                sourceId = source?.Id, scopes = source?.Scopes ?? [], permissionsRevision = link?.PermissionsRevision ?? 0,
                availablePermissions = PermissionsForSource.Select(scope => new { scope, label = PermissionLabel(scope) }).ToArray()
            };
        });
    }

    internal static (ChatGptLink Link, Source Source)? PermissionTarget(SqliteConnection c, SqliteTransaction t, ManagementInput input)
    {
        var link = Get<ChatGptLink>(c, t, "chatgpt_link", LinkId);
        if (link == null || link.SourceId != input.TargetId || link.PermissionsRevision != input.ExpectedRevision ||
            Get<Source>(c, t, "source", link.SourceId) is not { Revoked: false } source)
            return null;
        return (link, source);
    }

    internal static string PermissionChangeDescription(string[] current, string[] proposed)
    {
        var added = proposed.Except(current, StringComparer.Ordinal).Select(PermissionLabel);
        var removed = current.Except(proposed, StringComparer.Ordinal).Select(PermissionLabel);
        var changes = new[] { (Label: "Добавить", Rights: string.Join(", ", added)), (Label: "Снять", Rights: string.Join(", ", removed)) };
        var summary = string.Join(" ", changes.Where(change => change.Rights.Length > 0).Select(change => $"{change.Label}: {change.Rights}."));
        if (summary.Length == 0) summary = "Применить текущий набор: " + string.Join(", ", proposed.Select(PermissionLabel)) + ".";
        return summary +
            " Новый набор прав будет действовать для всех действующих токенов этого подключения ChatGPT, в том числе ранее выданных с урезанным доступом.";
    }

    internal static bool ApplyPermissions(SqliteConnection c, SqliteTransaction t, ManagementInput input, out string? stopCallSource)
    {
        stopCallSource = null;
        if (PermissionTarget(c, t, input) is not { } target) return false;
        var scopes = input.Scopes!;
        Put(c, t, "source", target.Source.Id, target.Source with { Scopes = scopes });
        Put(c, t, "chatgpt_link", LinkId, target.Link with { PermissionsRevision = target.Link.PermissionsRevision + 1 });
        if (target.Source.Scopes.Contains(ChatGptCall.Scope) && !scopes.Contains(ChatGptCall.Scope))
            stopCallSource = target.Source.Id;
        return true;
    }

    private static string PermissionLabel(string scope) => scope switch
    {
        "notify:send" => "Отправка текстовых и голосовых уведомлений",
        "requests:create" => "Создание подтверждений и форм",
        "requests:read" => "Чтение своих запросов и ответов",
        "requests:cancel" => "Отмена своих запросов",
        ChatGptCall.Scope => "Звонки с озвучкой",
        _ => scope
    };

    private async Task<IResult> Disconnect(HttpContext http)
    {
        RequireWeb(http);
        var revokedSource = db.Run(RevokeLink);
        if (revokedSource != null) await calls.SourceRevokedAsync(revokedSource);
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

        await RegisterCallback(uri, http.RequestAborted);
        return Results.NoContent();
    }

    internal Task EnsureDefaultCallback(CancellationToken cancellationToken = default) =>
        RegisterCallback(new Uri(DefaultCallback), cancellationToken, onlyIfPending: true);

    internal async Task EnsureRegisteredPermissions()
    {
        var link = ActiveLink();
        if (link?.RedirectUri == null || await applications.FindByClientIdAsync(link.ClientId) is not { } client) return;
        var required = PermissionsForSource.Select(scope => Permissions.Prefixes.Scope + scope).ToArray();
        var current = await applications.GetPermissionsAsync(client);
        if (required.All(current.Contains)) return;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, client);
        descriptor.Permissions.UnionWith(required);
        await applications.UpdateAsync(client, descriptor);
    }

    private async Task RegisterCallback(Uri uri, CancellationToken cancellationToken, bool onlyIfPending = false)
    {
        await ConfigureGate.WaitAsync(cancellationToken);
        try
        {
            var active = ActiveLink();
            if (onlyIfPending && (active?.PendingSecret == null || active.RedirectUri != null)) return;
            var link = active ?? throw new Fault(404, "chatgpt_not_linked");
            var client = await applications.FindByClientIdAsync(link.ClientId, cancellationToken);
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
                await applications.CreateAsync(descriptor, cancellationToken);
            }
            else
            {
                await applications.PopulateAsync(descriptor, client, cancellationToken);
                descriptor.RedirectUris.Clear();
                descriptor.RedirectUris.Add(uri);
                await applications.UpdateAsync(client, descriptor, cancellationToken);
            }
            db.Run((c, t) =>
            {
                if (Get<ChatGptLink>(c, t, "chatgpt_link", LinkId)?.ClientId != link.ClientId ||
                    Get<Source>(c, t, "source", link.SourceId) is not { Revoked: false })
                    throw new Fault(409, "chatgpt_link_changed");
                Put(c, t, "chatgpt_link", LinkId, link with { PendingSecret = null, RedirectUri = uri.AbsoluteUri });
                return 0;
            });
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
            // Chromium checks form-action on the redirect from the consent POST too.
            var callbackOrigin = new Uri(link.RedirectUri).GetLeftPart(UriPartial.Authority);
            http.Response.Headers.ContentSecurityPolicy = BrowserReturnPage.ContentSecurityPolicy(callbackOrigin);
            var tokens = antiforgery.GetAndStoreTokens(http);
            var html = new StringBuilder("<ul class=\"consent-scopes\">");
            var managed = link.PermissionsRevision > 0;
            var displayedScopes = managed
                ? db.Run((c, t) => Get<Source>(c, t, "source", link.SourceId)?.Scopes ?? []).Concat(scopes.Where(s => s == Scopes.OfflineAccess))
                : scopes;
            foreach (var scope in displayedScopes)
            {
                var description = scope switch
                {
                    "notify:send" => "Отправлять вам текстовые и голосовые уведомления.",
                    ChatGptCall.Scope => "Звонить на ваш телефон и зачитывать текст после принятия звонка.",
                    "requests:create" => "Запрашивать у вас подтверждения и отправлять формы для заполнения.",
                    "requests:read" => "Читать статус и ваши ответы на запросы, созданные этим подключением.",
                    "requests:cancel" => "Отменять незавершённые запросы, созданные этим подключением.",
                    Scopes.OfflineAccess => "Продлевать доступ без повторного входа в течение 30 дней.",
                    _ => "Дополнительное право, запрошенное ChatGPT."
                };
                html.Append($"<li><code>{HtmlEncoder.Default.Encode(scope)}</code><p>{description}</p></li>");
            }
            html.Append($"</ul><form method=\"post\" action=\"{AuthorizePath}\">");
            foreach (var parameter in http.Request.Query)
                foreach (var value in parameter.Value)
                    html.Append(Hidden(parameter.Key, value ?? ""));
            html.Append(Hidden(tokens.FormFieldName, tokens.RequestToken!));
            html.Append("<div class=\"consent-actions\"><button name=\"decision\" value=\"allow\">Разрешить</button>" +
                "<button name=\"decision\" value=\"deny\">Отклонить</button></div></form>");
            return BrowserReturnPage.Render("Подключить ChatGPT к Notify", managed
                ? "Подключение получит текущие права из кабинета Notify. Владелец может изменить их для всех токенов этого подключения."
                : "Разрешить следующие действия для выделенного источника?", html.ToString());
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
        "start_call" => ChatGptCall.Scope,
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
                var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                var isChatGpt = http?.Request.Path == McpPath;
                var managed = isChatGpt && http!.RequestServices.GetRequiredService<ChatGptOAuth>().GetAccess(http.User).Managed;
                return new ListToolsResult
                {
                    NextCursor = result.NextCursor,
                    Tools = isChatGpt
                        ? result.Tools.Where(tool => ToolScope(tool.Name) != null).Select(tool => DescribeOAuth(tool, managed)).ToArray()
                        : result.Tools.Where(tool => tool.Name is not ("send_voice" or "start_call")).ToArray()
                };
            });
            filters.AddCallToolFilter(next => (context, ct) =>
            {
                var name = context.Params?.Name;
                var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (http?.Request.Path != McpPath)
                    return name is "send_voice" or "start_call" ? ValueTask.FromResult(ToolError("unknown_tool")) : next(context, ct);
                var (actor, managed) = http.RequestServices.GetRequiredService<ChatGptOAuth>().GetAccess(http.User);
                if (actor == null) return ValueTask.FromResult(ToolError("unauthenticated"));
                var scope = name == null ? null : ToolScope(name);
                if (scope == null) return ValueTask.FromResult(ToolError("unknown_tool"));
                if (!actor.Scopes.Contains(scope))
                {
                    if (managed) return ValueTask.FromResult(ToolError("permission_disabled"));
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

    private static Tool DescribeOAuth(Tool tool, bool managed) => new()
    {
        Name = tool.Name, Title = tool.Title, Description = tool.Description, InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema, Annotations = tool.Annotations,
        Meta = new JsonObject
        {
            ["securitySchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauth2", ["scopes"] = managed ? new JsonArray() : new JsonArray(ToolScope(tool.Name))
            })
        }
    };
}
