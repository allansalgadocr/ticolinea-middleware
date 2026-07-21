using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.NodeConsole;
using ticolinea.stream.service.NodeConsole.Auth;

namespace ticolinea.stream.service.Controllers;

// User administration is owner-only. An operator can run the catalog but cannot
// mint themselves a second account or lock the owner out.
[ApiController]
[Route("api/console/users")]
[ConsoleAuth(RequireOwner = true)]
public class ConsoleUsersController : ControllerBase
{
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(ConsoleUsersController));

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await ConsoleUserStore.ListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NewUserInput input)
    {
        var error = ConsoleValidation.NewUser(input?.Username, input?.Password);
        if (error != null) return BadRequest(new { message = error });

        var created = await ConsoleUserStore.CreateAsync(input!);
        if (created == null) return Conflict(new { message = "Ese usuario ya existe." });

        _log.Info($"Console: user '{created.Username}' ({created.Role}) created by {Actor()}.");
        return Ok(created);
    }

    [HttpPost("{id:int}/enabled")]
    public async Task<IActionResult> SetEnabled(int id, [FromBody] bool enabled)
    {
        if (!await ConsoleUserStore.SetEnabledAsync(id, enabled))
            return BadRequest(new { message = "No se puede deshabilitar la cuenta inicial." });

        _log.Info($"Console: user {id} {(enabled ? "enabled" : "disabled")} by {Actor()}.");
        return NoContent();
    }

    [HttpPost("{id:int}/password")]
    public async Task<IActionResult> SetPassword(int id, [FromBody] PasswordInput input)
    {
        var error = ConsoleValidation.Password(input?.Password);
        if (error != null) return BadRequest(new { message = error });

        if (!await ConsoleUserStore.SetPasswordAsync(id, input!.Password!))
            return NotFound(new { message = "El usuario no existe." });

        _log.Info($"Console: password reset for user {id} by {Actor()}.");
        return NoContent();
    }

    private string Actor() => (HttpContext.Items[ConsoleAuthAttribute.UserItemKey] as ConsoleUser)?.Username ?? "?";
}
