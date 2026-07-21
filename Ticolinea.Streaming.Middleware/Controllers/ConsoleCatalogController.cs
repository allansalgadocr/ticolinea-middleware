using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.NodeConsole;
using ticolinea.stream.service.NodeConsole.Auth;

namespace ticolinea.stream.service.Controllers;

[ApiController]
[Route("api/console")]
[ConsoleAuth]
public class ConsoleCatalogController : ControllerBase
{
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(ConsoleCatalogController));

    // ---------- categories ----------

    [HttpGet("categories")]
    public async Task<IActionResult> Categories() => Ok(await ConsoleCatalogStore.ListCategoriesAsync());

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory([FromBody] CategoryInput input)
    {
        var error = ConsoleValidation.Category(input?.Name);
        if (error != null) return BadRequest(new { message = error });

        return Ok(await ConsoleCatalogStore.CreateCategoryAsync(input!.Name!));
    }

    [HttpPut("categories/{id:int}")]
    public async Task<IActionResult> RenameCategory(int id, [FromBody] CategoryInput input)
    {
        var error = ConsoleValidation.Category(input?.Name);
        if (error != null) return BadRequest(new { message = error });

        return await ConsoleCatalogStore.RenameCategoryAsync(id, input!.Name!)
            ? NoContent()
            : NotFound(new { message = "La categoría no existe." });
    }

    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id)
        => await ConsoleCatalogStore.DeleteCategoryAsync(id)
            ? NoContent()
            : NotFound(new { message = "La categoría no existe." });

    // ---------- channels ----------

    [HttpGet("channels")]
    public async Task<IActionResult> Channels() => Ok(await ConsoleCatalogStore.ListChannelsAsync());

    [HttpPost("channels")]
    public async Task<IActionResult> CreateChannel([FromBody] ChannelInput input)
    {
        var error = ConsoleValidation.Channel(input?.Name, input?.Source);
        if (error != null) return BadRequest(new { message = error });

        var created = await ConsoleCatalogStore.CreateChannelAsync(input!);
        _log.Info($"Console: channel {created.Id} '{created.Name}' created by {Actor()}.");
        return Ok(created);
    }

    [HttpPut("channels/{id:int}")]
    public async Task<IActionResult> UpdateChannel(int id, [FromBody] ChannelInput input)
    {
        var error = ConsoleValidation.Channel(input?.Name, input?.Source);
        if (error != null) return BadRequest(new { message = error });

        var (found, previousSource) = await ConsoleCatalogStore.UpdateChannelAsync(id, input!);
        if (!found) return NotFound(new { message = "El canal no existe." });

        // A source change only reaches the DB; the running FFmpeg process was
        // launched from the OLD fuente and keeps serving it until restarted.
        // Same reasoning as PackageSyncService.RestartChangedSourceStreamsAsync.
        var restarted = false;
        if (previousSource != null)
        {
            restarted = await RestartAsync(id);
            _log.Info($"Console: channel {id} source changed by {Actor()}; restart {(restarted ? "ok" : "skipped/failed")}.");
        }

        return Ok(new { sourceChanged = previousSource != null, restarted });
    }

    [HttpDelete("channels/{id:int}")]
    public async Task<IActionResult> DeleteChannel(int id)
    {
        if (!await ConsoleCatalogStore.DeleteChannelAsync(id))
            return BadRequest(new { message = "Solo se pueden eliminar canales creados en este nodo. Deshabilite los demás." });

        _log.Info($"Console: channel {id} deleted by {Actor()}.");
        return NoContent();
    }

    // Mirrors the operator-restart path so a console edit behaves exactly like an
    // admin restart. Non-blocking lock: if another action holds the channel, the
    // supervision loop converges on the new source on its next cycle.
    private static async Task<bool> RestartAsync(int id)
    {
        var gate = StreamLocks.For(id);
        if (!await gate.WaitAsync(0)) return false;
        try
        {
            var live = await StreamStatusHelper.GetRealTimeStreamStatusAsync(id);
            if (!live.IsRunning) return false; // picks up the new source when it next starts

            var stream = await StreamRestartHelper.LoadStream(id);
            if (stream == null) return false;
            return await StreamRestartHelper.RestartAsync(stream);
        }
        catch (Exception ex)
        {
            _log.Error($"Console: restart of channel {id} failed.", ex);
            return false;
        }
        finally { gate.Release(); }
    }

    private string Actor() => (HttpContext.Items[ConsoleAuthAttribute.UserItemKey] as ConsoleUser)?.Username ?? "?";
}
