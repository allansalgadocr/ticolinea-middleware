using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ticolinea.stream.service.NodeConsole.Auth;

public static class ConsoleCookie
{
    public const string Name = "tl_console";

    // HttpOnly: the token is never readable from JS, so an XSS in the console
    // cannot exfiltrate a session. SameSite=Strict is the CSRF control — the
    // API is cookie-authenticated and otherwise has no anti-forgery token.
    // Secure is set only when the request arrived over HTTPS, so a LAN-only
    // http deployment still works rather than silently dropping the cookie.
    public static CookieOptions Options(bool secure, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
    };
}

/// <summary>Requires a live console session. Set RequireOwner for user administration.</summary>
public class ConsoleAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    public bool RequireOwner { get; set; }

    public const string UserItemKey = "ConsoleUser";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var raw = context.HttpContext.Request.Cookies[ConsoleCookie.Name];
        if (string.IsNullOrWhiteSpace(raw))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var user = await ConsoleUserStore.ResolveSessionAsync(raw!);
        if (user == null)
        {
            // Clear the dead cookie so the SPA stops presenting it every request.
            context.HttpContext.Response.Cookies.Delete(ConsoleCookie.Name);
            context.Result = new UnauthorizedResult();
            return;
        }

        if (RequireOwner && !user.IsOwner)
        {
            context.Result = new ObjectResult(new { message = "Requiere permisos de propietario." }) { StatusCode = 403 };
            return;
        }

        context.HttpContext.Items[UserItemKey] = user;
    }
}
