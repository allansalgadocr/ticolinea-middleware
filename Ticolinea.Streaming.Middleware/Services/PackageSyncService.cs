using MySqlConnector;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Services;

public class PackageSyncService
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CatalogClient _client;
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(PackageSyncService));

    // Last completed sync (timestamp + counts + forced flag), for GET /api/admin/system.
    // Reference assignment is atomic; readers only ever see a whole result.
    public static PackageSyncResult? LastResult { get; private set; }

    // Bound on how long a FORCED (inline, operator-facing) sync waits for an
    // in-flight scheduled sync before giving up instead of hanging the request.
    private static readonly TimeSpan _forcedLockWait = TimeSpan.FromSeconds(60);

    public PackageSyncService(CatalogClient client) => _client = client;

    // forced=false: the unattended path (boot + recurring Hangfire job). Guard ON,
    //   non-blocking lock (a concurrent sync just skips this tick), failures are
    //   logged and swallowed.
    // forced=true: the operator path (POST /api/admin/sync). Guard OFF — the
    //   catalog is fully authoritative, including mass-disables. Waits (bounded)
    //   for an in-flight sync, and rethrows DB failures so the caller can report.
    // Returns null when the catalog fetch failed (panel unreachable / non-2xx) —
    // nothing was mutated — or when a scheduled run skipped/failed.
    public async Task<PackageSyncResult?> SyncAsync(bool forced)
    {
        if (forced)
        {
            if (!await _lock.WaitAsync(_forcedLockWait))
                throw new InvalidOperationException("Another package sync is already running; try again shortly.");
        }
        else if (!await _lock.WaitAsync(0)) { _log.Warn("Package sync already running; skipping this tick."); return null; }
        try
        {
            var catalog = await _client.FetchAsync();
            if (catalog == null) { _log.Warn("Catalog fetch failed; keeping current streams unchanged."); return null; }

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            var current = await ReadSyncedStreamsAsync(cnn);
            var enabledCount = await ReadEnabledSyncedCountAsync(cnn);
            var decision = PackageSyncPlan.Build(catalog, current.Keys.ToList(), enabledCount,
                enforceGuard: !forced, currentFuentes: current);
            if (decision.SkipDisable)
                _log.Warn($"Undersized catalog ({catalog.Count} vs {enabledCount} enabled); applying upserts, skipping disables.");

            await using var tx = await cnn.BeginTransactionAsync();
            try
            {
                foreach (var s in decision.Upserts)
                {
                    await UpsertStreamAsync(cnn, tx, s);
                    await EnsureStreamInfoAsync(cnn, tx, s.Id);
                }
                foreach (var id in decision.IdsToDisable)
                    await DisableStreamAsync(cnn, tx, id);
                await tx.CommitAsync();
                _log.Info($"Package sync ok ({(forced ? "forced" : "scheduled")}): {decision.Upserts.Count} upserted, {decision.IdsToDisable.Count} disabled.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _log.Error("Package sync failed; rolled back.", ex);
                if (forced) throw;
                return null;
            }

            // Post-commit: bounce RUNNING channels whose fuente changed, on BOTH
            // paths — the DB now holds the new source but each live channel's
            // SupervisarStream loop captured the OLD StreamDb, so without an
            // operator-style restart it would keep (re)launching the stale fuente.
            var restarted = await RestartChangedSourceStreamsAsync(decision.SourceChangedIds);

            var result = PackageSyncResult.FromDecision(decision);
            result.Restarted = restarted;
            result.Forced = forced;
            result.CompletedUtc = DateTime.UtcNow;
            LastResult = result;
            _log.Info($"Package sync summary: {result.Summary()}");
            return result;
        }
        finally { _lock.Release(); }
    }

    // One channel at a time, mirroring AdminController's operator-restart body via
    // StreamRestartHelper. Per-stream lock is a NON-BLOCKING try-acquire from this
    // sync context: if an operator action (or watchdog restart) is in flight on the
    // channel, its bounce is skipped and it counts as not-restarted — it converges
    // on the new fuente on its next operator/watchdog cycle or process start.
    private static async Task<int> RestartChangedSourceStreamsAsync(IReadOnlyList<int> changedIds)
    {
        var restarted = 0;
        foreach (var id in changedIds)
        {
            var gate = StreamLocks.For(id);
            if (!await gate.WaitAsync(0))
            {
                _log.Warn($"Stream {id}: fuente cambió pero hay una acción en vuelo; se omite el reinicio.");
                continue;
            }
            try
            {
                var live = await StreamStatusHelper.GetRealTimeStreamStatusAsync(id);
                if (!live.IsRunning) continue; // not running: picks up the new fuente on next start

                var stream = await StreamRestartHelper.LoadStream(id); // fresh row = new fuente
                if (stream == null) continue;

                if (await StreamRestartHelper.RestartAsync(stream)) restarted++;
                else _log.Warn($"Stream {id}: reinicio por cambio de fuente no levantó el proceso; la supervisión reintentará.");
            }
            catch (Exception ex)
            {
                _log.Error($"Stream {id}: error al reiniciar por cambio de fuente.", ex);
            }
            finally { gate.Release(); }
        }
        return restarted;
    }

    // id → fuente_stream for the FULL sincronizado=1 set. Keys feed the disable
    // set-math (as before); values feed source-change detection.
    private static async Task<Dictionary<int, string?>> ReadSyncedStreamsAsync(MySqlConnection cnn)
    {
        var rows = new Dictionary<int, string?>();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "SELECT id, fuente_stream FROM streams_tl WHERE sincronizado = 1;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows[r.GetInt32(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return rows;
    }

    // Denominator for the undersized-catalog guard: "channels we're actually serving now",
    // not the ever-accumulating full sincronizado=1 set (which keeps dropped channels forever
    // with habilitado=0 by design).
    private static async Task<int> ReadEnabledSyncedCountAsync(MySqlConnection cnn)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM streams_tl WHERE sincronizado = 1 AND habilitado = 1;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task UpsertStreamAsync(MySqlConnection cnn, MySqlTransaction tx, CatalogStream s)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO streams_tl
  (id, nombre_stream, fuente_stream, imagen_stream, id_categoria, orden, agregado,
   probesize_ondemand, es_bajodemanda, tipo, contenedor, habilitado, transcode_audio,
   intervalo, segmentos, framerate, transcode, resolucion, bitrate, canal_epg, cgop, gop, canal_id, sincronizado)
VALUES
  (@id, @nombre, @fuente, @imagen, @cat, @orden, @agregado,
   @probe, @vod, @tipo, @cont, 1, @taudio,
   @interv, @segs, @fr, @trans, @res, @bitrate, @epg, @cgop, @gop, @canalid, 1)
ON DUPLICATE KEY UPDATE
  nombre_stream=VALUES(nombre_stream), fuente_stream=VALUES(fuente_stream),
  imagen_stream=VALUES(imagen_stream), id_categoria=VALUES(id_categoria), orden=VALUES(orden),
  agregado=VALUES(agregado), probesize_ondemand=VALUES(probesize_ondemand), es_bajodemanda=VALUES(es_bajodemanda),
  tipo=VALUES(tipo), contenedor=VALUES(contenedor), habilitado=1, transcode_audio=VALUES(transcode_audio),
  intervalo=VALUES(intervalo), segmentos=VALUES(segmentos), framerate=VALUES(framerate),
  transcode=VALUES(transcode), resolucion=VALUES(resolucion), bitrate=VALUES(bitrate),
  canal_epg=VALUES(canal_epg), cgop=VALUES(cgop), gop=VALUES(gop), canal_id=VALUES(canal_id), sincronizado=1;";
        cmd.Parameters.AddWithValue("@id", s.Id);
        cmd.Parameters.AddWithValue("@nombre", s.NombreStream);
        cmd.Parameters.AddWithValue("@fuente", (object?)s.FuenteStream ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@imagen", (object?)s.ImagenStream ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat", (object?)s.IdCategoria ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@orden", s.Orden);
        cmd.Parameters.AddWithValue("@agregado", s.Agregado);
        cmd.Parameters.AddWithValue("@probe", s.ProbesizeOndemand);
        cmd.Parameters.AddWithValue("@vod", s.EsBajodemanda);
        cmd.Parameters.AddWithValue("@tipo", s.Tipo);
        cmd.Parameters.AddWithValue("@cont", (object?)s.Contenedor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taudio", s.TranscodeAudio ?? "");
        cmd.Parameters.AddWithValue("@interv", (object?)s.Intervalo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@segs", (object?)s.Segmentos ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fr", (object?)s.Framerate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@trans", s.Transcode);
        cmd.Parameters.AddWithValue("@res", s.Resolucion ?? "");
        cmd.Parameters.AddWithValue("@bitrate", s.Bitrate ?? "");
        cmd.Parameters.AddWithValue("@epg", s.CanalEpg ?? "");
        cmd.Parameters.AddWithValue("@cgop", s.Cgop);
        cmd.Parameters.AddWithValue("@gop", s.Gop);
        cmd.Parameters.AddWithValue("@canalid", s.CanalId);
        await cmd.ExecuteNonQueryAsync();
    }

    // Conditional insert — streams_info has NO unique on stream_id, so INSERT IGNORE won't work.
    // iniciado defaults to 1 here and is NEVER written again by the sync.
    private static async Task EnsureStreamInfoAsync(MySqlConnection cnn, MySqlTransaction tx, int streamId)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO streams_info (stream_id, ejecutando, proceso_id, info_progreso, iniciado, reportado_caido)
SELECT @id, 0, -1, '', 1, 0
WHERE NOT EXISTS (SELECT 1 FROM streams_info WHERE stream_id = @id);";
        cmd.Parameters.AddWithValue("@id", streamId);
        await cmd.ExecuteNonQueryAsync();
    }

    // Removal = habilitado=0 ONLY. iniciado is left untouched (node-owned).
    private static async Task DisableStreamAsync(MySqlConnection cnn, MySqlTransaction tx, int id)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE streams_tl SET habilitado = 0 WHERE id = @id AND sincronizado = 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
