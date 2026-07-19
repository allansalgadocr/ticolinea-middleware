using MySqlConnector;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;

namespace ticolinea.stream.service.Helpers;

// Operator-restart semantics, extracted VERBATIM from AdminController.Control's
// "start"/"restart" case so there is exactly one implementation. Callers:
//   - AdminController.Control (start/restart) — behavior unchanged.
//   - PackageSyncService — bounces RUNNING channels whose fuente_stream changed
//     after a sync commit (the SupervisarStream loop captured the old StreamDb,
//     so a plain kill would relaunch with the STALE source).
// Callers are responsible for holding StreamLocks.For(id) around the call.
// ForzarInicioInmediato clears breaker/backoff history — acceptable: this is an
// operator path, not the unattended supervision path.
public static class StreamRestartHelper
{
    // Loads a single StreamDb by streams_tl.id. Column list and reader mapping
    // are copied verbatim from Jobs.ObtenerStreamsActivos (Jobs.cs) so the
    // types match the real StreamDb model exactly — only the WHERE clause
    // differs (filtered to one id instead of the "active streams" set).
    public static async Task<StreamDb?> LoadStream(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = @"SELECT fuente_stream, stream_id, probesize_ondemand, es_bajodemanda,
                                   transcode_audio, intervalo, segmentos, framerate, transcode,
                                   resolucion, bitrate, proceso_id, cgop, gop
                            FROM streams_tl a
                            INNER JOIN streams_info b ON a.id = b.stream_id
                            WHERE a.id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new StreamDb
        {
            Fuente = reader.GetString(0),
            StreamId = reader.GetInt32(1),
            ProbeSize = reader.GetInt32(2),
            EsBajoDemanda = reader.GetInt32(3),
            TranscodeAudio = reader.GetString(4),
            Intervalo = reader.GetInt16(5),
            Segmentos = reader.GetInt16(6),
            Framerate = reader.GetInt32(7),
            Transcode = reader.GetInt32(8),
            Resolucion = reader.GetString(9),
            Bitrate = reader.GetString(10),
            ProcesoId = reader.GetInt32(11),
            CGOP = reader.GetInt32(12),
            GOP = reader.GetInt32(13)
        };
    }

    // The restart body itself: live-status refresh, pre-kill, forced start,
    // streams_info/streams_tl updates. Returns whether the process came up.
    public static async Task<bool> RestartAsync(StreamDb stream)
    {
        // Refresh ProcesoId from a LIVE pgrep before deciding whether to pre-kill,
        // mirroring PanelController.IniciarStream (~lines 1150-1160). Without this, an
        // orphaned ffmpeg whose DB proceso_id is stale (<= 0) would NOT be pre-killed
        // and ForzarInicioInmediato would spawn a SECOND ffmpeg writing the same HLS
        // segments -> corrupted output.
        var live = await StreamStatusHelper.GetRealTimeStreamStatusAsync(stream.StreamId);
        if (live.ProcessId.HasValue && live.ProcessId.Value > 0) stream.ProcesoId = live.ProcessId.Value;

        if (stream.ProcesoId > 0)
        {
            await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var started = await StreamingService.ForzarInicioInmediato(stream);
        await SetStreamStarted(stream.StreamId);
        await SetHabilitado(stream.StreamId);
        return started;
    }

    // Mirrors PanelController.IniciarStream's streams_info update exactly.
    private static async Task SetStreamStarted(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE `streams_info` " +
                          "SET iniciado=1, ejecutando=1, reportado_caido=0 " +
                          "WHERE stream_id=@id; ";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // Mirrors PanelController.IniciarStream's streams_tl update exactly.
    private static async Task SetHabilitado(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE `streams_tl` " +
                          "SET habilitado=1 " +
                          "WHERE id=@stream_id; ";
        cmd.Parameters.AddWithValue("@stream_id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
