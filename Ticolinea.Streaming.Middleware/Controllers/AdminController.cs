using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ticolinea.stream.service.Attributes;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;

namespace ticolinea.stream.service.Controllers;

// Node-side operator control plane (Spec C). Gated by the shared panel key
// (X-Auth-API-Key) via [NodeApiKey] — the panel's ProviderControlService is
// the only intended caller. Wraps existing FFmpeg supervision primitives;
// does not reimplement start/stop logic.
[Route("api/admin")]
[ApiController]
[NodeApiKey]
public class AdminController : ControllerBase
{
    [HttpGet("streams")]
    public async Task<IActionResult> GetStreams()
    {
        try
        {
            var items = new List<(int id, string nombre, int proceso)>();

            await using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                await cnn.OpenAsync();
                await using var cmd = cnn.CreateCommand();
                cmd.CommandText = @"SELECT a.id, a.nombre_stream, b.proceso_id
                                    FROM streams_tl a INNER JOIN streams_info b ON a.id = b.stream_id
                                    WHERE a.habilitado = 1 AND a.es_bajodemanda = 0 AND a.tipo = 1
                                    ORDER BY a.orden;";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? -1 : r.GetInt32(2)));
            }

            // Each status check shells out to pgrep; the admin UI polls this every ~4s. Run the
            // per-stream checks concurrently instead of sequentially. Task.WhenAll preserves the
            // input order, so statuses[i] corresponds to items[i].
            var statuses = await Task.WhenAll(items.Select(it => StreamStatusHelper.GetRealTimeStreamStatusAsync(it.id)));

            var rows = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var status = statuses[i];
                var (playlistAgeSeconds, playlist) = await PlaylistMetrics.ReadAsync(it.id);
                rows.Add(new
                {
                    id = it.id,
                    nombre = it.nombre,
                    running = status.IsRunning,
                    uptimeSec = status.IsRunning ? StreamingService.GetStreamUptimeSeconds(it.id) : 0,
                    procesoId = status.ProcessId ?? it.proceso,
                    // Output-health metrics (observation only). Nulls mean the playlist
                    // file is missing/unreadable, NOT that the endpoint failed.
                    playlistAgeSeconds,
                    mediaSequence = playlist?.MediaSequence,
                    targetDuration = playlist?.TargetDuration,
                    lastSegment = playlist?.LastSegment,
                    // FFmpeg launches since the service process booted (not persisted).
                    restartCount = StreamingService.GetRestartCount(it.id),
                    // Output-progress watchdog state (OutputWatchdogService). 0/false
                    // when Watchdog:Enabled is off (the state registry stays empty).
                    watchdogRestarts = OutputWatchdogService.GetRestartCount(it.id),
                    degraded = OutputWatchdogService.IsDegraded(it.id)
                });
            }
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpGet("system")]
    public async Task<IActionResult> GetSystem()
    {
        try
        {
            var (cpu, ram, disk) = await Jobs.ObtenerMetricasSaludAsync();
            // Uptime del SERVICIO, no del host: TickCount64 es el uptime del SO
            // (meses) — no coincide con el uptime de middleware del dashboard y
            // esconde deploys/reinicios, que es lo que el operador necesita ver.
            var uptime = (long)(DateTime.UtcNow -
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
            // Last catalog sync (scheduled or forced). Nulls/"" until the first
            // sync of this service process completes (the record is in-memory).
            var lastSync = PackageSyncService.LastResult;
            return Ok(new
            {
                uptimeSec = uptime, cpuPct = cpu, ramPct = ram, diskPct = disk,
                lastSyncUtc = lastSync?.CompletedUtc,
                lastSyncForced = lastSync?.Forced,
                lastSyncSummary = lastSync?.Summary() ?? ""
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Operator-FORCED catalog sync. Unlike the scheduled Jobs.SyncPackageCatalog,
    // the undersized-catalog guard is OFF: whatever the panel returns is applied
    // in full — adds, updates, AND mass-disables (a deliberate 50% shrink is
    // operator intent). Runs INLINE (not fire-and-forget): DB upserts plus a few
    // changed-source restarts — seconds, bounded by the CatalogClient 30s fetch
    // timeout and the service's 60s sync-lock wait.
    [HttpPost("sync")]
    public async Task<IActionResult> ForceSync()
    {
        try
        {
            // Same client wiring as Jobs.SyncPackageCatalog — one catalog contract.
            var http = Constantes.Global.HttpClientFactory.CreateClient("PanelApi");
            http.Timeout = TimeSpan.FromSeconds(30);
            var client = new CatalogClient(
                http,
                Constantes.Global.PANEL_API_URL,
                Constantes.Global.PANEL_API_KEY,
                Constantes.Global.PROVIDER_ID);

            var result = await new PackageSyncService(client).SyncAsync(forced: true);
            if (result == null)
                return StatusCode(502, new { message = "catalog fetch failed: panel unreachable or returned an error; nothing was changed" });

            return Ok(new
            {
                added = result.Added,
                updated = result.Updated,
                disabled = result.Disabled,
                sourcesChanged = result.SourcesChanged,
                restarted = result.Restarted,
                completedUtc = result.CompletedUtc,
                message = result.Summary()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Parámetro de ruta 'command', NO 'action': en attribute routing 'action'
    // es el token reservado del nombre del método — la ruta sólo casaba cuando
    // el segmento era literalmente "Control", así que start/stop/restart
    // devolvían 404. La forma de la URL no cambia.
    [HttpPost("streams/{id:int}/{command}")]
    public async Task<IActionResult> Control(int id, string command)
    {
        var action = command;
        // Per-stream lock shared with OutputWatchdogService: serializes operator
        // actions against watchdog restarts on the same stream. The operator side
        // WAITS (the watchdog holds it for ~3s max: 2s double-read + kill); the
        // watchdog side is non-blocking and skips its cycle if we hold it.
        // Behavior of the action bodies is unchanged — just serialized.
        var gate = StreamLocks.For(id);
        await gate.WaitAsync();
        try
        {
            var stream = await StreamRestartHelper.LoadStream(id);
            if (stream == null) return NotFound(new { success = false, message = $"stream {id} not found" });

            switch (action.ToLowerInvariant())
            {
                case "stop":
                    await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
                    await SetStreamInfo(id, iniciado: 0, ejecutando: 0, procesoId: -1);
                    return Ok(new { success = true, message = "stopped" });

                case "start":
                case "restart":
                    // Restart body (live-status refresh, pre-kill via DetenerProceso,
                    // ForzarInicioInmediato, streams_info/streams_tl updates) extracted
                    // verbatim to StreamRestartHelper.RestartAsync so PackageSyncService
                    // can bounce changed-source channels with the exact same semantics.
                    var started = await StreamRestartHelper.RestartAsync(stream);
                    return Ok(new { success = started, message = started ? action + "ed" : "process did not come up" });

                default:
                    return BadRequest(new { success = false, message = $"unknown action '{action}'" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
        finally
        {
            gate.Release();
        }
    }

    // Playlist output-health metrics moved to Helpers/PlaylistMetrics.ReadAsync so
    // this controller and OutputWatchdogService read the exact same file by the
    // exact same convention.

    // LoadStream / SetStreamStarted / SetHabilitado moved to
    // Helpers/StreamRestartHelper so the restart body is shared with
    // PackageSyncService (changed-source bounces) without duplication.

    // Mirrors PanelController.DetenerStream's DB update exactly.
    private static async Task SetStreamInfo(int id, int iniciado, int ejecutando, int procesoId)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE `streams_info` " +
                          "SET iniciado=@i, ejecutando=@e, proceso_id=@p " +
                          "WHERE stream_id=@id; ";
        cmd.Parameters.AddWithValue("@i", iniciado);
        cmd.Parameters.AddWithValue("@e", ejecutando);
        cmd.Parameters.AddWithValue("@p", procesoId);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

}
