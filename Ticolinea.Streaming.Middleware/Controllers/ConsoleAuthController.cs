using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.NodeConsole;
using ticolinea.stream.service.NodeConsole.Auth;

namespace ticolinea.stream.service.Controllers;

[ApiController]
[Route("api/console/auth")]
public class ConsoleAuthController : ControllerBase
{
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(ConsoleAuthController));

    // Process-wide: one node, one throttle. See LoginThrottle for why it is not persisted.
    private static readonly LoginThrottle _throttle = new();

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginInput input)
    {
        if (string.IsNullOrWhiteSpace(input?.Username) || string.IsNullOrWhiteSpace(input?.Password))
            return BadRequest(new { message = "Usuario y contraseña son obligatorios." });

        var username = input.Username.Trim().ToLowerInvariant();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var key = LoginThrottle.KeyFor(username, ip);

        // Checked BEFORE touching the database: a locked-out caller must not be
        // able to keep driving PBKDF2 verifications, which is the expensive part.
        if (_throttle.IsLocked(key, DateTime.UtcNow, out var retryAfter))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            _log.Warn($"Console login BLOCKED (locked out) for '{username}' from {ip}; {minutes} min remaining.");
            Response.Headers["Retry-After"] = ((int)retryAfter.TotalSeconds).ToString();
            return StatusCode(429, new
            {
                message = $"Demasiados intentos fallidos. Intente de nuevo en {minutes} minuto{(minutes == 1 ? "" : "s")}.",
            });
        }

        var result = await ConsoleUserStore.LoginAsync(username, input.Password);
        if (result == null)
        {
            var justLocked = _throttle.RecordFailure(key, DateTime.UtcNow);
            if (justLocked)
            {
                _log.Warn($"Console login LOCKED OUT '{username}' from {ip} after {LoginThrottle.MaxAttempts} " +
                          $"failed attempts; blocked for {LoginThrottle.LockoutWindow.TotalMinutes:0} minutes.");
            }
            else
            {
                _log.Warn($"Console login rejected for '{username}' from {ip}.");
            }

            // One generic message for unknown user, wrong password and disabled
            // account — the login form must not enumerate valid usernames.
            return Unauthorized(new { message = "Usuario o contraseña incorrectos." });
        }

        _throttle.RecordSuccess(key);
        var (user, token) = result.Value;
        Response.Cookies.Append(
            ConsoleCookie.Name, token,
            ConsoleCookie.Options(Request.IsHttps, DateTimeOffset.UtcNow.Add(ConsoleUserStore.SessionLifetime)));

        return Ok(user);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var raw = Request.Cookies[ConsoleCookie.Name];
        if (!string.IsNullOrWhiteSpace(raw)) await ConsoleUserStore.LogoutAsync(raw!);
        Response.Cookies.Delete(ConsoleCookie.Name);
        return NoContent();
    }

    /// <summary>Who am I — the SPA calls this on load to decide login vs. app.</summary>
    [HttpGet("me")]
    [ConsoleAuth]
    public IActionResult Me() => Ok(HttpContext.Items[ConsoleAuthAttribute.UserItemKey]);

    /// <summary>
    /// Which node am I looking at. Anonymous on purpose: the login screen names
    /// the node so an operator with several tabs open cannot edit the wrong
    /// one. Deliberately limited to the display name — no version or build
    /// details are disclosed before authentication.
    /// </summary>
    [HttpGet("/api/console/node")]
    public IActionResult Node() => Ok(new
    {
        provider = Constantes.Global.PROVIDER_ID,
        displayName = Constantes.Global.PROVIDER_NAME,
    });
}
