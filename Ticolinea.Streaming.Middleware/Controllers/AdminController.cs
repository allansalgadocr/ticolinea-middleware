using CliWrap;
using CliWrap.Buffered;
using log4net;
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
    private static readonly ILog _logger = LogManager.GetLogger(typeof(AdminController));

    private readonly IConfiguration _configuration;

    public AdminController(IConfiguration configuration) => _configuration = configuration;

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

    // Full SERVICE restart (not a stream restart). A process cannot restart its
    // own systemd unit — but the unit runs Restart=always, so the trick is to
    // answer FIRST and then exit cleanly: systemd relaunches the service, which
    // then does its normal boot work (catalog sync on boot, channel ramp-up).
    // The 1.5s delay exists only so the 200 leaves the socket before we die.
    [HttpPost("service/restart")]
    public IActionResult RestartService()
    {
        _logger.Warn("operator-requested service restart — responding 200, then exiting so systemd relaunches");
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Environment.Exit(0);
        });
        return Ok(new { success = true, message = "service exiting; systemd will relaunch" });
    }

    // Mass operations. Target sets:
    //   restart-all / stop-all → channels CURRENTLY running (ObtenerStreamsActivos
    //                            + live pgrep check, same as the per-channel path);
    //   start-all              → enabled channels (habilitado=1) NOT running.
    // The endpoint answers immediately with the target count; the work runs in a
    // background task, SEQUENTIALLY (see RunMassOperation). Only one mass
    // operation may run at a time — MassOperationGate — a second request gets 409.
    [HttpPost("streams/restart-all")]
    public Task<IActionResult> RestartAll() => MassOperation("restart-all");

    [HttpPost("streams/stop-all")]
    public Task<IActionResult> StopAll() => MassOperation("stop-all");

    [HttpPost("streams/start-all")]
    public Task<IActionResult> StartAll() => MassOperation("start-all");

    private async Task<IActionResult> MassOperation(string op)
    {
        if (!MassOperationGate.TryEnter())
            return Conflict(new { message = "mass operation already in progress" });

        List<StreamDb> targets;
        try
        {
            targets = await ResolveMassTargets(op);
        }
        catch (Exception ex)
        {
            MassOperationGate.Exit();
            return StatusCode(500, new { success = false, message = ex.Message });
        }

        if (targets.Count == 0)
        {
            MassOperationGate.Exit();
            return Ok(new { accepted = 0, message = $"{op}: no target streams" });
        }

        _logger.Warn($"mass operation '{op}' accepted: {targets.Count} streams, running in background");
        _ = Task.Run(() => RunMassOperation(op, targets));
        return Ok(new { accepted = targets.Count, message = $"{op} accepted for {targets.Count} streams; running in background" });
    }

    private static async Task<List<StreamDb>> ResolveMassTargets(string op)
    {
        // start-all considers ALL enabled channels (iniciado irrelevant); the
        // other two only channels that should be producing (ObtenerStreamsActivos).
        var candidates = op == "start-all"
            ? await StreamRestartHelper.LoadEnabledStreams()
            : await Jobs.ObtenerStreamsActivos();

        // Live pgrep check, concurrent like GetStreams. WhenAll preserves order.
        var statuses = await Task.WhenAll(
            candidates.Select(s => StreamStatusHelper.GetRealTimeStreamStatusAsync(s.StreamId)));

        var targets = new List<StreamDb>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var running = statuses[i].IsRunning;
            if (op == "start-all" ? !running : running)
                targets.Add(candidates[i]);
        }
        return targets;
    }

    // The mass-op loop. SEQUENTIAL on purpose: ForzarInicioInmediato already
    // paces launches through the 20-slot startup semaphore, and one-at-a-time
    // keeps the DB/pgrep load flat — this is the stagger, not a new mechanism.
    // Per stream it reuses the SINGLE-channel primitives verbatim:
    //   stop      → Jobs.DetenerProceso + streams_info iniciado=0/ejecutando=0/proceso_id=-1
    //               (same as the Control "stop" case)
    //   start/restart → StreamRestartHelper.RestartAsync (same as Control "start"/"restart")
    // Per-stream lock: TRY-acquire (like the watchdog), not wait — if an
    // operator/watchdog action is in flight on that channel, skip it and log.
    private static async Task RunMassOperation(string op, List<StreamDb> targets)
    {
        int ok = 0, failed = 0, skipped = 0;
        try
        {
            foreach (var stream in targets)
            {
                var gate = StreamLocks.For(stream.StreamId);
                if (!await gate.WaitAsync(0))
                {
                    skipped++;
                    _logger.Warn($"mass '{op}': stream {stream.StreamId} skipped (operator/watchdog action in flight)");
                    continue;
                }
                try
                {
                    if (op == "stop-all")
                    {
                        await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
                        await SetStreamInfo(stream.StreamId, iniciado: 0, ejecutando: 0, procesoId: -1);
                        ok++;
                    }
                    else // restart-all / start-all
                    {
                        var started = await StreamRestartHelper.RestartAsync(stream);
                        if (started) ok++;
                        else
                        {
                            failed++;
                            _logger.Warn($"mass '{op}': stream {stream.StreamId} process did not come up");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Error($"mass '{op}': stream {stream.StreamId} failed: {ex.Message}", ex);
                }
                finally
                {
                    gate.Release();
                }
            }
            _logger.Warn($"mass operation '{op}' finished: ok={ok} failed={failed} skipped={skipped} of {targets.Count}");
        }
        finally
        {
            MassOperationGate.Exit();
        }
    }

    // Tail of TODAY's log for remote support. Never throws: any failure is a 200
    // with file=null and a message — the panel shows it as "log unavailable",
    // it must not read as a node outage. Path/tail logic lives in LogTailHelper
    // (shared resolution with Log4netExtensions, unit-tested).
    [HttpGet("logs")]
    public IActionResult GetLogs([FromQuery] int? lines)
    {
        try
        {
            var count = LogTailHelper.ClampLineCount(lines);
            var dir = LogTailHelper.ResolveLogDirectory(_configuration["Logging:Directory"]);
            // Local time, not UTC: log4net's RollingFileAppender rolls the
            // datePattern on LOCAL dates — this must name the same file.
            var file = LogTailHelper.LogFileName(DateTime.Now);
            var path = Path.Combine(dir, file);

            if (!System.IO.File.Exists(path))
                return Ok(new { file = (string?)null, lines = Array.Empty<string>(), message = "log file not found" });

            return Ok(new { file, lines = LogTailHelper.TailLines(path, count) });
        }
        catch (Exception ex)
        {
            return Ok(new { file = (string?)null, lines = Array.Empty<string>(), message = $"log read failed: {ex.Message}" });
        }
    }

    // Node identity/environment card for the panel. Every field is individually
    // best-effort: a failing probe yields null for that field only — this
    // endpoint never 500s (it is what support reaches for when things are wrong).
    [HttpGet("info")]
    public async Task<IActionResult> GetInfo()
    {
        string? os = null, ffmpeg = null, runtime = null;
        DateTime? serviceStartUtc = null;
        double? rootPct = null, streamsPct = null;

        try { os = ReadOsPrettyName(); } catch { /* best-effort */ }
        try { ffmpeg = await ReadFfmpegVersionAsync(); } catch { /* best-effort */ }
        try { runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription; } catch { /* best-effort */ }
        try { serviceStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); } catch { /* best-effort */ }
        try { rootPct = await Jobs.ObtenerUsoDisco(); } catch { /* best-effort */ }
        try { streamsPct = await Jobs.ObtenerUsoDiscoCarpeta(Constantes.Global.STREAMS_FOLDER); } catch { /* best-effort */ }

        return Ok(new
        {
            os,
            ffmpeg,
            runtime,
            serviceStartUtc,
            provider = Constantes.Global.PROVIDER_ID,
            disks = new { rootPct, streamsPct }
        });
    }

    private static string? ReadOsPrettyName()
    {
        const string path = "/etc/os-release";
        if (!System.IO.File.Exists(path)) return null;
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                return line.Substring("PRETTY_NAME=".Length).Trim().Trim('"');
        }
        return null;
    }

    private static async Task<string?> ReadFfmpegVersionAsync()
    {
        // Same binary the supervision loop launches (Global.FFMPEG_PATH).
        var result = await Cli.Wrap(Constantes.Global.FFMPEG_PATH)
            .WithArguments(new[] { "-version" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        var first = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
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
