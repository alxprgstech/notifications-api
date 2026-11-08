using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Notify;

// SSO accepts at most 128 characters of state. Keep the protected OIDC properties
// in a browser cookie and send only its random lookup key through SSO.
internal sealed class WebLoginState(
    IHttpContextAccessor http, IDataProtectionProvider protection, PathString callback, TimeSpan lifetime, bool secure)
    : ISecureDataFormat<AuthenticationProperties>
{
    private readonly IDataProtector protector = protection.CreateProtector("Notify.WebLoginState.v1");
    private const string CookiePrefix = "notify.oidc.state.";

    public string Protect(AuthenticationProperties data) => Protect(data, null);

    public string Protect(AuthenticationProperties data, string? purpose)
    {
        var context = http.HttpContext ?? throw new InvalidOperationException("OIDC state requires an HTTP request.");
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var properties = PropertiesSerializer.Default.Serialize(data);
        var value = Protector(state, purpose).Protect(properties, lifetime);
        var options = CookieSettings();
        options.MaxAge = lifetime;
        context.Response.Cookies.Append(CookiePrefix + state, WebEncoders.Base64UrlEncode(value), options);
        return state;
    }

    public AuthenticationProperties? Unprotect(string? state) => Unprotect(state, null);

    public AuthenticationProperties? Unprotect(string? state, string? purpose)
    {
        if (state is not { Length: 43 } || !state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return null;

        var context = http.HttpContext;
        var name = CookiePrefix + state;
        if (context is null || !context.Request.Cookies.TryGetValue(name, out var value))
            return null;

        context.Response.Cookies.Delete(name, CookieSettings());
        try
        {
            var properties = Protector(state, purpose).Unprotect(WebEncoders.Base64UrlDecode(value));
            return PropertiesSerializer.Default.Deserialize(properties);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private ITimeLimitedDataProtector Protector(string state, string? purpose)
    {
        var stateProtector = protector.CreateProtector(state);
        return (purpose is null ? stateProtector : stateProtector.CreateProtector(purpose)).ToTimeLimitedDataProtector();
    }

    private CookieOptions CookieSettings() => new()
    {
        Path = callback,
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        IsEssential = true
    };
}
