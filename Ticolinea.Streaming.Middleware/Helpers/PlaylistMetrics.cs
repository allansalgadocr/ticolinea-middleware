namespace ticolinea.stream.service.Helpers;

// Métricas de salud de salida de un stream leídas de su playlist HLS.
// Extraído de AdminController.ReadPlaylistMetricsAsync para que el control
// plane del operador y el OutputWatchdogService lean EXACTAMENTE el mismo
// archivo por la misma convención (STREAMS_FOLDER + "{id}_.m3u8" — lo que
// StreamingService pasa a ffmpeg y lo que sirve LiveController.Streaming).
// FFmpeg reescribe el archivo continuamente; el hls_flag temp_file hace el
// rename atómico, pero el archivo igual puede desaparecer o cambiar entre el
// Exists y la lectura, por eso todo va envuelto: cualquier fallo devuelve
// (null, null) — "sin datos", nunca una excepción hacia el caller.
public static class PlaylistMetrics
{
    public static async Task<(double? ageSeconds, HlsPlaylistInfo? playlist)> ReadAsync(int streamId)
    {
        try
        {
            var playlistFile = Path.Combine(Constantes.Global.STREAMS_FOLDER, $"{streamId}_.m3u8");
            var fileInfo = new FileInfo(playlistFile);
            if (!fileInfo.Exists) return (null, null);

            double age = Math.Max(0, (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalSeconds);
            var playlist = HlsPlaylistInfo.Parse(await File.ReadAllTextAsync(playlistFile));
            return (age, playlist);
        }
        catch
        {
            return (null, null);
        }
    }
}
