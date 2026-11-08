using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Notify.ChatGpt;

internal static class AdapterOAuth
{
    public const string Send = "notify:send";
    public const string Create = "requests:create";
    public const string Read = "requests:read";
    public const string Cancel = "requests:cancel";
    public static readonly string[] Scopes = [Send, Create, Read, Cancel];
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static CallToolResult? ChallengeTool(RequestContext<CallToolRequestParams> context, AdapterSettings settings)
    {
        if (context.MatchedPrimitive is not McpServerTool tool ||
            tool.ProtocolTool.Meta?["securitySchemes"]?[0]?["scopes"]?[0]?.GetValue<string>() is not { } scope ||
            context.User?.HasScope(scope) == true)
            return null;
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "insufficient_scope" }],
            Meta = new JsonObject
            {
                ["mcp/www_authenticate"] = new JsonArray(
                    $"Bearer resource_metadata=\"{settings.Metadata}\", scope=\"{scope}\", error=\"insufficient_scope\", " +
                    "error_description=\"Reconnect Notify to grant the required scope\"")
            }
        };
    }

    public static bool HasExactResource(OpenIddictRequest request, string expected)
    {
        var resources = request.GetResources();
        return resources.Length == 1 && resources[0] == expected;
    }

    public static async Task RegisterClient(IOpenIddictApplicationManager manager, AdapterSettings settings)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "ChatGPT Notify",
            RedirectUris = { settings.ClientRedirect },
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code
            }
        };
        descriptor.AddResourcePermissions(settings.Resource);
        foreach (var scope in Scopes) descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        var client = await manager.FindByClientIdAsync(settings.ClientId);
        if (client is null)
            await manager.CreateAsync(descriptor);
        else if (!await manager.ValidateClientSecretAsync(client, settings.ClientSecret) ||
                 !(await manager.GetRedirectUrisAsync(client)).SequenceEqual([settings.ClientRedirect.AbsoluteUri]) ||
                 !descriptor.Permissions.SetEquals(await manager.GetPermissionsAsync(client)))
            await manager.UpdateAsync(client, descriptor);
    }

    public static async Task<IResult> Authorize(HttpContext context, AdapterSettings settings, IAntiforgery antiforgery)
    {
        var request = context.GetOpenIddictServerRequest()!;
        var scopes = request.GetScopes();
        if (scopes.Length == 0 || !scopes.Any(scope => Scopes.Contains(scope)))
            return Reject(Errors.InvalidScope);

        var session = await context.AuthenticateAsync("owner");
        if (session.Principal?.FindFirstValue("sub") != settings.OwnerSub)
            return HttpMethods.IsGet(context.Request.Method)
                ? Results.Challenge(new AuthenticationProperties
                {
                    RedirectUri = context.Request.Path + context.Request.QueryString
                }, ["sso"])
                : Reject(Errors.LoginRequired);

        if (HttpMethods.IsGet(context.Request.Method))
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var html = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>Notify</title>" +
                "<h1>Connect ChatGPT to Notify</h1><p>Allow these permissions for the dedicated Notify source?</p><ul>");
            foreach (var scope in scopes) html.Append($"<li>{Encode(scope)}</li>");
            html.Append("</ul><form method=\"post\" action=\"/oauth/authorize\">");
            foreach (var parameter in context.Request.Query)
                foreach (var value in parameter.Value)
                    html.Append(Hidden(parameter.Key, value ?? ""));
            html.Append(Hidden(tokens.FormFieldName, tokens.RequestToken!));
            html.Append("<button name=\"decision\" value=\"allow\">Allow</button> " +
                "<button name=\"decision\" value=\"deny\">Deny</button></form>");
            return Results.Content(html.ToString(), "text/html; charset=utf-8");
        }

        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "invalid_csrf" }); }
        if ((await context.Request.ReadFormAsync(context.RequestAborted))["decision"] != "allow")
            return Reject(Errors.AccessDenied);

        var identity = new ClaimsIdentity(Scheme);
        identity.SetClaim(Claims.Subject, settings.OwnerSub);
        identity.SetScopes(scopes);
        identity.SetResources(settings.Resource);
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: Scheme);
    }

    public static async Task<IResult> Exchange(HttpContext context, AdapterSettings settings)
    {
        var authorization = await context.AuthenticateAsync(Scheme);
        var principal = authorization.Principal;
        if (principal?.GetClaim(Claims.Subject) != settings.OwnerSub ||
            !principal.GetResources().SequenceEqual([settings.Resource]) ||
            !principal.GetPresenters().Contains(settings.ClientId))
            return Reject(Errors.InvalidGrant);
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    private static IResult Reject(string error) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error
    }), [Scheme]);

    private static string Hidden(string name, string value) =>
        $"<input type=\"hidden\" name=\"{Encode(name)}\" value=\"{Encode(value)}\">";

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}
