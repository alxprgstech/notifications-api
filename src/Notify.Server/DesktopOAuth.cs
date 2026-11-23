using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static Notify.Database;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Notify;

internal sealed record DesktopGrant(string Id, string AgentId, string SourceId, long ExpiresAt, bool Established);

internal sealed class DesktopOAuth(Database db, Settings settings, AgentHub agents, IOpenIddictApplicationManager applications)
{
    internal const string Codex = "notify-codex-desktop";
    internal const string Harness = "notify-harness-desktop";
    internal const string Scope = "agents:connect";
    internal const string AuthorizePath = "/desktop/oauth/authorize";
    internal const string TokenPath = "/desktop/oauth/token";
    internal const string GrantClaim = "notify_desktop_grant";
    internal const string Validation = OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private const string CallbackPath = "/notify/oauth/callback";
    private const string GrantKind = "desktop_grant";
    private static readonly string[] AllowedScopes = [Scope, Scopes.OfflineAccess];
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan GrantLifetime = TimeSpan.FromDays(30);

    internal static bool IsClient(string? clientId) => clientId is Codex or Harness;
    internal static bool Configured(Settings settings) => settings.SsoConfigured &&
        (settings.DesktopCodexAgentId.Length != 0 || settings.DesktopHarnessAgentId.Length != 0);
    internal static string Resource(Settings settings) => settings.PublicUrl.TrimEnd('/') + "/api/v1/agents";
    internal static bool IsResourceRequest(PathString path) =>
        path == "/api/v1/agents/self" || path == "/api/v1/agents/connect" ||
        path == "/api/v1/telephony/assistant" || path == "/api/v1/telephony/infrastructure" ||
        path == "/api/v1/desktop/" + Codex + "/disconnect" || path == "/api/v1/desktop/" + Harness + "/disconnect" ||
        (path.StartsWithSegments("/api/v1/agents/calls") && path.Value!.EndsWith("/audio", StringComparison.Ordinal));

    private string BoundAgent(string? clientId) => clientId switch
    {
        Codex => settings.DesktopCodexAgentId,
        Harness => settings.DesktopHarnessAgentId,
        _ => ""
    };

    internal async Task RegisterClients()
    {
        foreach (var client in new[] { Codex, Harness })
        {
            if (BoundAgent(client).Length == 0) continue;
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ApplicationType = ApplicationTypes.Native,
                ClientId = client,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Explicit,
                DisplayName = client == Codex ? "Codex Desktop" : "DeepSeek Harness"
            };
            descriptor.RedirectUris.Add(new Uri("http://127.0.0.1" + CallbackPath));
            descriptor.Permissions.UnionWith([
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + Scope
            ]);
            descriptor.AddResourcePermissions(Resource(settings));
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            if (await applications.FindByClientIdAsync(client) is { } existing)
                await applications.UpdateAsync(existing, descriptor);
            else
                await applications.CreateAsync(descriptor);
        }
    }

    internal static void Map(WebApplication app, RouteGroupBuilder api)
    {
        app.MapMethods(AuthorizePath, ["GET", "POST"], (HttpContext http, DesktopOAuth oauth, IAntiforgery csrf) =>
            oauth.Authorize(http, csrf)).ExcludeFromDescription();
        app.MapPost(TokenPath, (HttpContext http, DesktopOAuth oauth) => oauth.Exchange(http)).ExcludeFromDescription();
        api.MapPost("/desktop/{client}/disconnect", (string client, HttpContext http, DesktopOAuth oauth) =>
            oauth.Disconnect(http, client)).ExcludeFromDescription();
    }

    private async Task<IResult> Authorize(HttpContext http, IAntiforgery csrf)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new Fault(400, "invalid_oauth_request");
        var client = request.ClientId;
        if (!IsClient(client) || BoundAgent(client).Length == 0 || !Callback(request.RedirectUri, out var callbackOrigin))
            return Reject(Errors.InvalidClient);
        if (!ExactScopes(request.GetScopes())) return Reject(Errors.InvalidScope);
        if (string.IsNullOrEmpty(request.State)) return Reject(Errors.InvalidRequest);

        var session = await http.AuthenticateAsync("web");
        if (session.Principal?.FindFirstValue("sub") != settings.OwnerSub)
            return HttpMethods.IsGet(http.Request.Method)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString }, ["sso"])
                : Reject(Errors.LoginRequired);

        if (HttpMethods.IsGet(http.Request.Method))
        {
            var tokens = csrf.GetAndStoreTokens(http);
            http.Response.Headers.ContentSecurityPolicy = BrowserReturnPage.ContentSecurityPolicy(callbackOrigin);
            var form = new StringBuilder($"<form method=\"post\" action=\"{AuthorizePath}\">");
            foreach (var parameter in http.Request.Query)
                foreach (var value in parameter.Value)
                    form.Append(Hidden(parameter.Key, value ?? ""));
            form.Append(Hidden(tokens.FormFieldName, tokens.RequestToken!));
            form.Append("<div class=\"consent-actions\"><button name=\"decision\" value=\"allow\">Разрешить</button><button name=\"decision\" value=\"deny\">Отклонить</button></div></form>");
            var name = client == Codex ? "Codex Desktop" : "DeepSeek Harness";
            return BrowserReturnPage.Render($"Подключить {name} к Notify",
                "Разрешить доступ к назначенному телефонному агенту и обновление доступа в течение 30 дней?", form.ToString());
        }

        try { await csrf.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "invalid_csrf" }); }
        if ((await http.Request.ReadFormAsync(http.RequestAborted))["decision"] != "allow") return Reject(Errors.AccessDenied);
        var grant = db.Run((c, t) =>
        {
            if (ActiveAgent(c, t, client!) is not { } agent ||
                Get<DesktopGrant>(c, t, GrantKind, client!) is { } saved && saved.ExpiresAt > Wire.Now)
                return null;
            var pending = new DesktopGrant(Wire.Id(), agent.Id, agent.SourceId, Wire.Now + (long)CodeLifetime.TotalMilliseconds, false);
            Put(c, t, GrantKind, client!, pending);
            return pending;
        });
        if (grant is null) return Reject(Errors.AccessDenied);

        var identity = new ClaimsIdentity(Scheme);
        identity.SetClaim(Claims.Subject, settings.OwnerSub);
        identity.SetClaim(GrantClaim, grant.Id);
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        identity.SetScopes(AllowedScopes);
        identity.SetResources(Resource(settings));
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: Scheme);
    }

    private async Task<IResult> Exchange(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new Fault(400, "invalid_oauth_request");
        var auth = await http.AuthenticateAsync(Scheme);
        var principal = auth.Principal;
        var grantId = principal?.GetClaim(GrantClaim);
        if (request.ClientId is not { } client || !IsClient(client) || principal?.GetClaim(Claims.Subject) != settings.OwnerSub ||
            grantId is null || !principal.GetPresenters().SequenceEqual([client]) || !ExactScopes(principal.GetScopes()) ||
            !principal.GetResources().SequenceEqual([Resource(settings)]))
            return Reject(Errors.InvalidGrant);

        var accepted = db.Run((c, t) =>
        {
            var saved = FindGrant(c, t, client, grantId);
            if (saved is null) return false;
            if (request.IsRefreshTokenGrantType()) return saved.Established;
            if (!request.IsAuthorizationCodeGrantType() || saved.Established || !Callback(request.RedirectUri, out _)) return false;
            Put(c, t, GrantKind, client, saved with { Established = true, ExpiresAt = Wire.Now + (long)GrantLifetime.TotalMilliseconds });
            return true;
        });
        return accepted ? Results.SignIn(principal, authenticationScheme: Scheme) : Reject(Errors.InvalidGrant);
    }

    internal (string SourceId, string GrantId)? GetAccess(ClaimsPrincipal principal)
    {
        var presenters = principal.GetPresenters();
        var client = presenters.Length == 1 ? presenters[0] : null;
        var grant = principal.GetClaim(GrantClaim);
        if (!IsClient(client) || grant is null || principal.GetClaim(Claims.Subject) != settings.OwnerSub ||
            !principal.GetAudiences().SequenceEqual([Resource(settings)]) || !ExactScopes(principal.GetScopes()) ||
            principal.GetExpirationDate() is not { } expires || expires <= DateTimeOffset.UtcNow) return null;
        var target = db.Run((c, t) => FindGrant(c, t, client!, grant));
        return target is not { Established: true } ? null : (target.SourceId, grant);
    }

    internal bool IsActive(string sourceId, string grant) => db.Run((c, t) =>
        new[] { Codex, Harness }.Any(client => FindGrant(c, t, client, grant) is { Established: true } saved && saved.SourceId == sourceId));

    private IResult Disconnect(HttpContext http, string client)
    {
        if (!IsClient(client) || BoundAgent(client).Length == 0) return Results.NotFound();
        var actor = Auth.Actor(http);
        var grant = http.User.FindFirstValue(GrantClaim);
        if (actor.Kind != "web" && grant is null) throw new Fault(403, "desktop_grant_required");
        var removed = db.Run((c, t) =>
        {
            var saved = Get<DesktopGrant>(c, t, GrantKind, client);
            if (saved is null) return null;
            if (actor.Kind != "web" && (saved.Id != grant || saved.SourceId != actor.Id))
                throw new Fault(403, "desktop_grant_required");
            Exec(c, t, "DELETE FROM objects WHERE kind=$kind AND id=$client", ("$kind", GrantKind), ("$client", client));
            return saved;
        });
        if (removed is not null) agents.DisconnectDesktopGrant(removed.AgentId, removed.Id);
        return Results.NoContent();
    }

    private Agent? ActiveAgent(SqliteConnection c, SqliteTransaction t, string client)
    {
        var id = BoundAgent(client);
        if (id.Length == 0 || Get<Agent>(c, t, "agent", id) is not { Revoked: false } agent ||
            Get<Source>(c, t, "source", agent.SourceId) is not { Revoked: false } source ||
            !source.Scopes.Contains(Scope) ||
            All<Agent>(c, t, "agent").Any(other => !other.Revoked && other.SourceId == agent.SourceId && other.Id != id))
            return null;
        return agent;
    }

    private DesktopGrant? FindGrant(SqliteConnection c, SqliteTransaction t, string client, string grant)
    {
        var stored = Get<DesktopGrant>(c, t, GrantKind, client);
        if (stored?.Id != grant || stored.ExpiresAt <= Wire.Now || ActiveAgent(c, t, client) is not { } agent ||
            stored.AgentId != agent.Id || stored.SourceId != agent.SourceId) return null;
        return stored;
    }

    private static bool ExactScopes(IEnumerable<string> scopes) => AllowedScopes.ToHashSet(StringComparer.Ordinal).SetEquals(scopes);

    private static bool Callback(string? value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Port is < 1024 or > 65535) return false;
        origin = "http://127.0.0.1:" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return value == origin + CallbackPath;
    }

    private static string Hidden(string name, string value) =>
        $"<input type=\"hidden\" name=\"{HtmlEncoder.Default.Encode(name)}\" value=\"{HtmlEncoder.Default.Encode(value)}\">";

    private static IResult Reject(string error) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error
    }), [Scheme]);
}
