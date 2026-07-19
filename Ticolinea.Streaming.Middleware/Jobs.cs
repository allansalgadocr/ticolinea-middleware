using CliWrap;
using CliWrap.Buffered;
using Hangfire;
using log4net;
using MySqlConnector;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service
{
    public class Jobs
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(Jobs));
        // 🚀 SIMPLE CACHE FOR STREAM DATA - Reduces database queries
        public static List<StreamDb>? _cachedStreams = null;
        public static DateTime _lastCacheRefresh = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(15); // Reduced for faster updates with 155+ streams

        // Package sync (Spec B): pulls this node's channel catalog from the panel
        // and upserts streams_tl/streams_info. Runs on a Hangfire recurring
        // schedule (see Program.cs) plus once on boot.
        public static async Task SyncPackageCatalog()
        {
            var http = Constantes.Global.HttpClientFactory.CreateClient("PanelApi");
            http.Timeout = TimeSpan.FromSeconds(30);
            var client = new Helpers.CatalogClient(
                http,
                Constantes.Global.PANEL_API_URL,
                Constantes.Global.PANEL_API_KEY,
                Constantes.Global.PROVIDER_ID);
            await new Services.PackageSyncService(client).SyncAsync();
        }

        [DisableConcurrentExecution(60)]
        public static async Task RevisarStreams()
        {
            // Check if stream execution is allowed
            StreamExecutionGuard.LogStreamExecutionAttempt("RevisarStreams");
            if (!StreamExecutionGuard.CanExecuteStreams())
            {
                _logger.Warn("⚠️  Stream execution disabled - skipping stream review");
                return;
            }

            var streams = await ObtenerStreamsActivos();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 20 // 🚀 Increased for 155+ streams (was 5)
            };

            await Parallel.ForEachAsync(streams, parallelOptions, async (stream, ct) =>
            {
                try
                {
                    if (stream.ProcesoId == -1 || !await EstaProcesoFfmpegVivo(stream.ProcesoId, stream.StreamId))
                    {
                        _logger.Debug($"🔄 Reiniciando stream {stream.StreamId}...");
                        bool started = await StreamingService.ForzarInicioInmediato(stream);
                        if (started)
                        {
                            _logger.Info($"🚀 Stream {stream.StreamId} reinicio automático: EXITOSO");
                        }
                        else
                        {
                            _logger.Warn($"🚀 Stream {stream.StreamId} reinicio automático: FALLÓ");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"❌ Error al revisar stream {stream.StreamId}: {ex.Message}", ex);
                }
            });
        }

        private static async Task<bool> EstaProcesoFfmpegVivo(int procesoId, int streamId)
        {
            return await ObtenerProcesoFFMPEG(procesoId, streamId);
        }

        // Público: además de RevisarStreams, el OutputWatchdogService usa este mismo
        // conjunto ("streams que DEBERÍAN estar produciendo": habilitado=1, iniciado=1,
        // es_bajodemanda=0, tipo=1) como candidatos. La caché de 15s amortigua el
        // ciclo de 5s del watchdog — no agrega consultas nuevas a la BD.
        public static async Task<List<StreamDb>> ObtenerStreamsActivos()
        {
            // 🚀 CACHE CHECK: Use cached data if available and recent
            lock (_cacheLock)
            {
                if (_cachedStreams != null && DateTime.UtcNow - _lastCacheRefresh < _cacheExpiration)
                {
                    _logger.Debug($"📦 Using cached streams data ({_cachedStreams.Count} streams)");
                    return _cachedStreams.ToList(); // Return a copy to prevent mutation
                }
            }

            // 🔍 Load from database
            var streams = new List<StreamDb>();

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            await using var cmd = cnn.CreateCommand();
            cmd.CommandText = @"
            SELECT fuente_stream, stream_id, probesize_ondemand, es_bajodemanda, 
                   transcode_audio, intervalo, segmentos, framerate, transcode, 
                   resolucion, bitrate, proceso_id, cgop, gop
            FROM streams_tl a
            INNER JOIN streams_info b ON a.id = b.stream_id
            WHERE habilitado = 1 AND iniciado = 1 AND es_bajodemanda = 0 AND tipo = 1;";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                streams.Add(new StreamDb
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
                });
            }

            // 🚀 Update cache with fresh data
            lock (_cacheLock)
            {
                _cachedStreams = streams;
                _lastCacheRefresh = DateTime.UtcNow;
                _logger.Debug($"📦 Updated cache with {streams.Count} streams");
            }

            return streams;
        }

        // 🚀 Method to invalidate cache when needed
        public static void InvalidateStreamCache()
        {
            lock (_cacheLock)
            {
                _cachedStreams = null;
                _lastCacheRefresh = DateTime.MinValue;
                _logger.Debug("🗑️ Stream cache invalidated");
            }
        }


        public static async Task VerificarCodecsStreams(bool verificaSoloHabilitados = true)
        {
            var streams = new List<StreamDb>();

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            await using (var cmd = cnn.CreateCommand())
            {
                var sql = "SELECT fuente_stream, id FROM streams_tl WHERE tipo = 1";
                if (verificaSoloHabilitados)
                    sql += " AND habilitado = 1";

                cmd.CommandText = sql;

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    streams.Add(new StreamDb
                    {
                        Fuente = reader.GetString(0),
                        StreamId = reader.GetInt32(1),
                    });
                }
            }

            // 🚀 Optimized for large scale (155+ streams)
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 15 };

            await Parallel.ForEachAsync(streams, parallelOptions, async (stream, ct) =>
            {
                try
                {
                    _logger.Debug($"🔍 Verificando codec de stream {stream.StreamId}...");
                    await ObtenerInfoCodec(stream.StreamId, stream.Fuente);
                }
                catch (Exception ex)
                {
                    _logger.Error($"❌ Error al verificar codec del stream {stream.StreamId}: {ex.Message}", ex);
                }
            });
        }

        [DisableConcurrentExecution(60)]
        public static async Task DetenerStreamsSinUso()
        {
            List<StreamDb> streams = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT a.id,b.proceso_id,c.actividad_id FROM streams_tl a " +
                                      "INNER JOIN streams_info b on a.id = b.stream_id " +
                                      "LEFT JOIN actividad_usuario_actualmente c " +
                                      "on a.id = c.stream_id " +
                                      "WHERE es_bajodemanda = 1 AND proceso_id != -1 AND actividad_id is null and a.tipo=1;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            streams.Add(new StreamDb
                            {
                                StreamId = reader.GetInt32(0),
                                ProcesoId = reader.GetInt32(1)
                            });
                        }
                }

                foreach (StreamDb stream in streams)
                {
                    var proc = await ObtenerProcesoEjecutando(stream.ProcesoId, stream.StreamId);
                    if (proc != null)
                    {
                        proc.Kill();
                    }

                    using (var cmdStreams = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdStreams.CommandText = "UPDATE streams_info SET proceso_id=-1 " +
                                                 "WHERE stream_id=@id";
                        cmdStreams.Parameters.AddWithValue("@id", stream.StreamId);
                        await cmdStreams.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        public static async Task<bool> ObtenerProcesoFFMPEG(int procesoId, int streamId)
        {
            try
            {
                var result = await Cli
                    .Wrap("/bin/pgrep")
                    .WithArguments(new[] { "-f", $"/{streamId}_.m3u8" })
                    .ExecuteBufferedAsync();
                string output = result.StandardOutput;
                string[] procesos = output.Split(
                    new string[] { Environment.NewLine },
                    StringSplitOptions.None
                );

                foreach (string proceso in procesos)
                {
                    if (!string.IsNullOrWhiteSpace(proceso))
                    {
                        int.TryParse(proceso, out int proc);
                        var cmdProc = Process.GetProcessById(proc);
                        if (cmdProc != null)
                        {
                            if (cmdProc.ProcessName.Contains("ffmpeg"))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ERROR AL OBTENER PROCESO.", ex);
            }

            return false;
        }

        private static async Task<Process?> ObtenerProcesoEjecutando(int procesoId, int streamId)
        {
            try
            {
                var result = await Cli
                    .Wrap("/bin/pgrep")
                    .WithArguments(new[] { "-f", $"/{streamId}_.m3u8" })
                    .ExecuteBufferedAsync();
                string output = result.StandardOutput;
                string[] procesos = output.Split(
                    new string[] { Environment.NewLine },
                    StringSplitOptions.None
                );

                foreach (string proceso in procesos)
                {
                    if (!string.IsNullOrWhiteSpace(proceso))
                    {
                        int.TryParse(proceso, out int proc);
                        var cmdProc = Process.GetProcessById(proc);
                        if (cmdProc != null && cmdProc.ProcessName.Contains("ffmpeg")) return cmdProc;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ERROR AL OBTENER PROCESO.", ex);
            }

            return null;
        }

        public static async Task ReiniciarStream(StreamDb stream)
        {
            await DetenerProceso(stream.ProcesoId, stream.StreamId);
        }

        public static async Task DetenerProceso(int procesoId, int streamId)
        {
            try
            {
                // 🛑 Detener supervisión (evita que se reinicie automáticamente)
                StreamingService.DetenerSupervision(streamId);

                // 🔍 Buscar procesos relacionados con el stream
                var result = await Cli
                    .Wrap("/bin/pgrep")
                    .WithArguments(new[] { "-f", $"/{streamId}_.m3u8" })
                    .ExecuteBufferedAsync();

                string output = result.StandardOutput;
                string[] procesos = output
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var proceso in procesos)
                {
                    if (int.TryParse(proceso, out var pid))
                    {
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc.HasExited) continue;

                            proc.Kill(true);
                            _logger.Info($"✅ Proceso {pid} detenido.");
                        }
                        catch (ArgumentException)
                        {
                            _logger.Warn($"⚠️ Proceso con PID {pid} ya no existe.");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"❌ Error al detener proceso {pid}: {ex.Message}", ex);
                        }
                    }
                }

                // 📝 Actualizar BD: marcar como detenido
                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync();

                await using var cmd = cnn.CreateCommand();
                cmd.CommandText = @"
                            UPDATE streams_info
                            SET ejecutando = 0, proceso_id = -1
                            WHERE stream_id = @id";

                cmd.Parameters.AddWithValue("@id", streamId);
                await cmd.ExecuteNonQueryAsync();

                _logger.Info($"🗂 Stream {streamId} marcado como detenido en la base de datos.");
            }
            catch (Exception ex)
            {
                _logger.Error($"❌ Error general al detener el stream {streamId}: {ex.Message}", ex);
            }
        }


        // Reinicio del watchdog (OutputWatchdogService): mata SOLO el proceso ffmpeg,
        // SIN tocar la supervisión. A diferencia de DetenerProceso, aquí NO se llama
        // StreamingService.DetenerSupervision — esa ruta termina en CleanupStreamState,
        // que borra _failureTracker (circuit breaker) y _lastProcessStart (intervalo
        // mínimo de 12s). Al matar únicamente el PID, el loop SupervisarStream que ya
        // está vivo recibe el ExitedCommandEvent (exit != 0) y relanza por el camino
        // supervisado normal (LanzarProcesoFfmpeg): el circuit breaker conserva su
        // historial y aplican backoff + intervalo mínimo. El kill en sí NO cuenta
        // como fallo del breaker ni del contador de reintentos: se marca vía
        // StreamingService.MarkWatchdogKill justo antes de CADA Kill() que
        // realmente se intenta (la marca es un contador — pgrep puede devolver
        // varios PIDs), y el manejo del exit consume UNA marca por exit. Si el
        // Kill() lanza (o el PID resultó ya muerto), la marca se retira
        // (RetractWatchdogKillMark): marcas vivas == kills entregados. Sólo el
        // exit del proceso SUPERVISADO pasa por ese manejador, así que las marcas
        // de PIDs duplicados/rogue quedan huérfanas — el TTL de 60s las purga sin
        // que puedan consumirse. El watchdog ya tiene su propio presupuesto
        // (WatchdogPolicy) y sin la marca 3 kills en <8 min disparaban el breaker
        // y dejaban el canal caído. Tampoco se toca la BD: el ExitedCommandEvent
        // ya dispara ActualizarCanalEstado y el relanzo ActualizaInfoCanal, igual
        // que en cualquier caída de ffmpeg.
        public static async Task<bool> MatarProcesoParaWatchdog(int streamId)
        {
            bool killedAny = false;
            try
            {
                var result = await Cli
                    .Wrap("/bin/pgrep")
                    .WithArguments(new[] { "-f", $"/{streamId}_.m3u8" })
                    .ExecuteBufferedAsync();

                string[] procesos = result.StandardOutput
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var proceso in procesos)
                {
                    if (int.TryParse(proceso, out var pid))
                    {
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc.HasExited) continue;

                            // Marca inmediatamente ANTES de cada kill intentado:
                            // el exit != 0 resultante no debe contar como fallo.
                            StreamingService.MarkWatchdogKill(streamId);
                            try
                            {
                                proc.Kill(true);
                            }
                            catch
                            {
                                // El kill NO se entregó: retirar la marca para que
                                // no enmascare un fallo real dentro del TTL.
                                StreamingService.RetractWatchdogKillMark(streamId);
                                throw;
                            }
                            killedAny = true;
                            _logger.Info($"✅ Watchdog: proceso {pid} del stream {streamId} eliminado (la supervisión lo relanzará).");
                        }
                        catch (ArgumentException)
                        {
                            _logger.Warn($"⚠️ Watchdog: proceso con PID {pid} ya no existe.");
                        }
                        catch (InvalidOperationException)
                        {
                            // Kill() sobre un proceso que salió entre el HasExited y
                            // el kill: no hubo kill entregado (marca ya retirada).
                            _logger.Warn($"⚠️ Watchdog: proceso {pid} salió antes del kill.");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"❌ Watchdog: error al eliminar proceso {pid}: {ex.Message}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"❌ Watchdog: error al buscar procesos del stream {streamId}: {ex.Message}", ex);
            }

            return killedAny;
        }

        public static async Task<string> RunCommandAsync(int streamId)
        {
            var result = await Cli
                .Wrap("/bin/pgrep")
                .WithArguments(new[] { "-f", $"/{streamId}_.m3u8" })
                .ExecuteBufferedAsync();
            string output = result.StandardOutput;
            string[] lines = output.Split(
                new string[] { Environment.NewLine },
                StringSplitOptions.None
            );

            string outs = "";
            foreach (var line in lines)
            {
                outs += line + ",";
            }

            return outs;
        }

        public static async Task IniciarStream(StreamDb stream)
        {
            // Check if stream execution is allowed
            StreamExecutionGuard.LogStreamExecutionAttempt($"IniciarStream({stream.StreamId})");
            if (!StreamExecutionGuard.CanExecuteStreams())
            {
                _logger.Warn($"⚠️  Stream execution disabled - cannot start stream {stream.StreamId}");
                return;
            }

            var proc = await ObtenerProcesoEjecutando(0, stream.StreamId);

            if (proc != null)
            {
                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync();

                await using var cmd = cnn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE streams_info 
                    SET proceso_id = @id_proceso, ejecutando = 1 
                    WHERE stream_id = @id";

                cmd.Parameters.AddWithValue("@id_proceso", proc.Id);
                cmd.Parameters.AddWithValue("@id", stream.StreamId);

                await cmd.ExecuteNonQueryAsync();
                return;
            }

            // Inicia la supervisión 24/7 del stream en un hilo separado
            // The StreamingService will handle FFmpeg process protection internally
            StreamingService.IniciarSupervision(stream);
        }


        public static async Task ActualizaInfoCanal(int procesoId, int streamId)
        {
            // Use optimized batch processing
            Data.Streams.QueueStreamUpdate(streamId, procesoId, true);
        }

        public static async Task ActualizarCanalEstado(int streamId, bool estaCaido, int procesoId)
        {
            // Use optimized batch processing
            Data.Streams.QueueStatusUpdate(streamId, estaCaido);
        }

        [DisableConcurrentExecution(60)]
        public static async Task VerificarStreamsCaidos()
        {
            try
            {
                StringBuilder sb = new();
                List<StreamDb> streams = new();
                int streamsCaidos = 0;
                sb.AppendLine($"{Constantes.Global.PROVIDER_NAME}: [CANT] streams se reportaron como caídos:");
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT fuente_stream, id, nombre_stream,proceso_id FROM streams_tl a " +
                                          "inner join streams_info b " +
                                          "on a.id = b.stream_id " +
                                          "WHERE habilitado = 1 AND iniciado = 1 AND omitir_verificacion = 0 and tipo = 1 and es_bajodemanda=0; ";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new StreamDb
                                {
                                    Fuente = reader.GetString(0),
                                    StreamId = reader.GetInt32(1),
                                    TranscodeAudio = reader.GetString(2),
                                    ProcesoId = reader.GetInt32(3)
                                });
                            }
                    }
                }


                // Optimized parallel processing with batched database updates
                var statusUpdates = new List<(int streamId, bool isCaido)>();
                
                await Parallel.ForEachAsync(streams, new ParallelOptions { MaxDegreeOfParallelism = 10 }, async (stream, ct) =>
                {
                    try
                    {
                        Process probe = new();
                        probe.StartInfo.FileName = Constantes.Global.FFPROBE_PATH;
                        probe.StartInfo.Arguments = $"-i \"http://localhost:27701/Live/Streaming/{stream.StreamId}/test/test.m3u8\" -analyzeduration 1000000 -probesize 1000000 -v quiet -print_format json -show_streams -show_format";
                        probe.StartInfo.UseShellExecute = false;
                        probe.StartInfo.RedirectStandardOutput = true;
                        probe.StartInfo.RedirectStandardError = true;
                        probe.Start();

                        string output = probe.StandardOutput.ReadToEnd();
                        var probeData = Modelos.ProbeData.FromJson(output);
                        bool estaCaido = false;
                        if (probeData?.Streams == null)
                        {
                            Interlocked.Increment(ref streamsCaidos);
                            estaCaido = true;
                            lock (sb)
                            {
                                sb.AppendLine($"• {stream.StreamId} - {stream.TranscodeAudio}");
                            }
                            await DetenerProceso(stream.ProcesoId, stream.StreamId);
                        }

                        lock (statusUpdates)
                        {
                            statusUpdates.Add((stream.StreamId, estaCaido));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"ERROR AL OBTENER INFO DE CANAL.{ex.Message}", ex);
                    }
                });

                // Batch all status updates at once
                foreach (var update in statusUpdates)
                {
                    Data.Streams.QueueStatusUpdate(update.streamId, update.isCaido);
                }

                sb.Replace("[CANT]", streamsCaidos.ToString());
                sb.AppendLine("Por favor verificar.");

                if (streamsCaidos > 0)
                {
                    // Get health information
                    var healthInfo = await ObtenerInfoSalud();
                    
                    // Build improved message with provider identifier and health info
                    var message = $"🚨 ALERTA: {Constantes.Global.PROVIDER_NAME} 🚨\n\n" +
                                 $"📺 STREAMS CAÍDOS: {streamsCaidos}\n\n" +
                                 sb.ToString() + "\n" +
                                 $"{healthInfo}\n\n" +
                                 $"⏰ {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    await EnviarAlertaTelegram(message);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
            }
        }

        public static async Task VerificarStream(StreamDb stream)
        {
            try
            {
                if (stream != null)
                {
                    try
                    {
                        var args =
                            $"-i {stream.Fuente} -analyzeduration 1000000 -probesize 1000000 -v quiet -print_format json -show_streams -show_format";
                        Process probe = new();
                        probe.StartInfo.FileName = Constantes.Global.FFPROBE_PATH;
                        probe.StartInfo.Arguments = args;
                        probe.StartInfo.UseShellExecute = false;
                        probe.StartInfo.RedirectStandardOutput = true;
                        probe.StartInfo.RedirectStandardError = true;
                        probe.Start();

                        string output = probe.StandardOutput.ReadToEnd();
                        var probeData = Modelos.ProbeData.FromJson(output);
                        bool estaCaido = false;
                        if (probeData.Streams == null)
                        {
                            estaCaido = true;
                        }

                        await ActualizarCanalEstado(stream.StreamId, estaCaido, stream.ProcesoId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"ERROR AL OBTENER INFO DE CANAL.{ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
            }
        }

        public static async Task ObtenerInfoCodec(int streamId, string fuente)
        {
            try
            {
                var args =
                    $"-i {fuente} -analyzeduration 512000 -probesize 512000 -v quiet -print_format json -show_streams -show_format";
                Process probe = new();
                probe.StartInfo.FileName = Constantes.Global.FFPROBE_PATH;
                probe.StartInfo.Arguments = args;
                probe.StartInfo.UseShellExecute = false;
                probe.StartInfo.RedirectStandardOutput = true;
                probe.StartInfo.RedirectStandardError = true;
                probe.Start();

                string output = probe.StandardOutput.ReadToEnd();
                var probeData = ProbeData.FromJson(output);
                var videoInfo = probeData.Streams.Where(x => x.CodecType == "video").Select(s => new
                {
                    s.CodecName,
                    s.CodedHeight,
                    s.CodedWidth,
                    s.AvgFrameRate
                }).FirstOrDefault();
                string videodbInfo =
                    $"{videoInfo?.CodecName}|height:{videoInfo?.CodedHeight}|width:{videoInfo?.CodedWidth}|fr={videoInfo?.AvgFrameRate}";
                var audioInfo = probeData.Streams.Where(x => x.CodecType == "audio").Select(s => new
                {
                    s.CodecName
                }).FirstOrDefault();
                string audiodbInfo = $"{audioInfo?.CodecName}";

                probe.WaitForExit();

                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "UPDATE streams_tl SET audio_info=@audio_info, video_info=@video_info " +
                                          "WHERE id=@id";

                        cmd.Parameters.AddWithValue("@audio_info", audiodbInfo);
                        cmd.Parameters.AddWithValue("@video_info", videodbInfo);
                        cmd.Parameters.AddWithValue("@id", streamId);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"ERROR AL OBTENER INFO CODEC{ex.Message}", ex);
            }
        }


        [DisableConcurrentExecution(60)]
        public static async Task MataConexionesSinUso()
        {
            try
            {
                var fechaFinMaxima = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeSeconds();
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText =
                            "DELETE FROM actividad_usuario_actualmente where fecha_inicio < @fechaFinMaxima;";
                        cmd.Parameters.AddWithValue("@fechaFinMaxima", fechaFinMaxima);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"ERROR AL MATAR CONEXIONES.{ex.Message}", ex);
            }
        }

        [DisableConcurrentExecution(60)]
        public static async Task LimpiaErrores()
        {
            try
            {
                var fechaFinMaxima = DateTimeOffset.Now.AddMinutes(-25).ToUnixTimeSeconds();
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "TRUNCATE TABLE streams_error;";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"ERROR AL MATAR CONEXIONES.{ex.Message}", ex);
            }
        }

        [DisableConcurrentExecution(60)]
        public static void EliminarArchivosViejos()
        {
            try
            {
                var streamsFolder = Constantes.Global.STREAMS_FOLDER;
                if (!Directory.Exists(streamsFolder))
                {
                    _logger.Warn($"⚠️ Streams folder does not exist: {streamsFolder}");
                    return;
                }

                var files = Directory.GetFiles(streamsFolder)
                    .Select(f => new FileInfo(f))
                    .Where(f => 
                        // Delete files older than 20 minutes using LastWriteTime (more reliable than CreationTime)
                        f.LastWriteTime < DateTime.Now.AddMinutes(-20) ||
                        // Also delete files that are too old (more than 1 hour) regardless
                        f.LastWriteTime < DateTime.Now.AddHours(-1))
                    .ToList();

                int deletedCount = 0;
                long freedSpace = 0;
                
                foreach (var file in files)
                {
                    try
                    {
                        long fileSize = file.Length;
                        file.Delete();
                        deletedCount++;
                        freedSpace += fileSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"⚠️ Error deleting file {file.Name}: {ex.Message}", ex);
                    }
                }

                if (deletedCount > 0)
                {
                    _logger.Info($"✅ Cleaned up {deletedCount} old files, freed {freedSpace / 1024 / 1024} MB from {streamsFolder}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"❌ ERROR AL ELIMINAR ARCHIVOS VIEJOS: {ex.Message}", ex);
            }
        }

        [DisableConcurrentExecution(60)]
        public static async Task EliminarArchivosGrandes()
        {
            try
            {
                var streamsFolder = Constantes.Global.STREAMS_FOLDER;
                if (!Directory.Exists(streamsFolder))
                {
                    _logger.Warn($"⚠️ Streams folder does not exist: {streamsFolder}");
                    return;
                }

                // Find files that are:
                // 1. Larger than 15MB AND older than 10 minutes (normal cleanup)
                // 2. OR larger than 100MB regardless of age (emergency cleanup for stuck streams)
                var now = DateTime.Now;
                var files = Directory.GetFiles(streamsFolder)
                    .Select(f => new FileInfo(f))
                    .Where(f => 
                        f.Length > 15000000 && // Larger than 15MB
                        (
                            (f.Length > 15000000 && f.LastWriteTime < now.AddMinutes(-10)) || // Old large files
                            f.Length > 100000000 // Very large files (>100MB) regardless of age
                        ))
                    .ToList();

                if (files.Count == 0)
                {
                    _logger.Debug("✅ No large files to clean up");
                    return;
                }

                int deletedCount = 0;
                long freedSpace = 0;
                int stoppedStreams = 0;

                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync();

                foreach (var file in files)
                {
                    try
                    {
                        // Extract stream ID from filename (format: STREAMID_SEGMENT.ts)
                        var fileName = Path.GetFileName(file.Name);
                        var split = fileName.Split("_");
                        
                        if (split.Length < 1 || !int.TryParse(split[0], out int streamId))
                        {
                            // If we can't parse stream ID, delete if it's very old or very large
                            if (file.Length > 100000000 || file.LastWriteTime < now.AddHours(-1))
                            {
                                long unparsedFileSize = file.Length;
                                file.Delete();
                                deletedCount++;
                                freedSpace += unparsedFileSize;
                                _logger.Warn($"🗑️ Deleted unparseable large file: {fileName} ({unparsedFileSize / 1024 / 1024} MB)");
                            }
                            continue;
                        }

                        // Check if stream is active before deleting
                        StreamDb? activeStream = null;
                        await using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT a.id, b.proceso_id, c.actividad_id 
                                                FROM streams_tl a 
                                                INNER JOIN streams_info b ON a.id = b.stream_id 
                                                LEFT JOIN actividad_usuario_actualmente c ON a.id = c.stream_id 
                                                WHERE a.id = @streamId AND b.proceso_id != -1 AND a.tipo = 1";
                            cmd.Parameters.AddWithValue("@streamId", streamId);

                            await using var reader = await cmd.ExecuteReaderAsync();
                            if (await reader.ReadAsync())
                            {
                                activeStream = new StreamDb
                                {
                                    StreamId = reader.GetInt32(0),
                                    ProcesoId = reader.GetInt32(1)
                                };
                            }
                        }

                        // Delete the file
                        long fileSize = file.Length;
                        file.Delete();
                        deletedCount++;
                        freedSpace += fileSize;
                        _logger.Info($"🗑️ Deleted large file: {fileName} ({fileSize / 1024 / 1024} MB) - Stream {streamId}");

                        // If stream was active, stop it (file was corrupted or stuck)
                        if (activeStream != null && activeStream.ProcesoId > 0)
                        {
                            _logger.Warn($"🛑 Stopping stream {streamId} due to large file deletion");
                            await DetenerProceso(activeStream.ProcesoId, activeStream.StreamId);
                            stoppedStreams++;
                        }
                    }
                    catch (IOException ex)
                    {
                        // File might be in use, skip it
                        _logger.Warn($"⚠️ Cannot delete {file.Name} (file in use): {ex.Message}", ex);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.Warn($"⚠️ Access denied deleting {file.Name}: {ex.Message}", ex);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"⚠️ Error processing large file {file.Name}: {ex.Message}", ex);
                    }
                }

                if (deletedCount > 0)
                {
                    _logger.Info($"✅ Large file cleanup: Deleted {deletedCount} files, freed {freedSpace / 1024 / 1024} MB, stopped {stoppedStreams} streams");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"❌ ERROR AL ELIMINAR ARCHIVOS GRANDES: {ex.Message}", ex);
            }
        }

        [DisableConcurrentExecution(60)]
        public static void CleanUpOldJobs()
        {
            try
            {
                using (var connection = JobStorage.Current.GetConnection())
                {
                    var monitoringApi = JobStorage.Current.GetMonitoringApi();
                    var failedJobs = monitoringApi.FailedJobs(0, 1000 /* limit */);

                    while (failedJobs.Count > 0)
                    {
                        foreach (var job in failedJobs)
                        {
                            BackgroundJob.Delete(job.Key);
                        }

                        failedJobs = monitoringApi.FailedJobs(0, 1000 /* limit */);
                    }

                    var successJobs = monitoringApi.SucceededJobs(0, 1000 /* limit */);

                    while (successJobs.Count > 0)
                    {
                        foreach (var job in successJobs)
                        {
                            BackgroundJob.Delete(job.Key);
                        }

                        successJobs = monitoringApi.SucceededJobs(0, 1000 /* limit */);
                    }
                }
            }
            catch (Exception ex)
            {
                // Optionally, implement retry mechanisms or alerting here
            }
        }

        public static void SincronizarS3()
        {
            try
            {
                var watcher = new FileSystemWatcher(@"C:\inetpub\wwwroot\iptv\streams");

                watcher.NotifyFilter = NotifyFilters.Attributes
                                       | NotifyFilters.CreationTime
                                       | NotifyFilters.DirectoryName
                                       | NotifyFilters.FileName
                                       | NotifyFilters.LastAccess
                                       | NotifyFilters.LastWrite
                                       | NotifyFilters.Security
                                       | NotifyFilters.Size;

                watcher.Changed += Watcher_Changed;
                watcher.Created += Watcher_Created;
                watcher.Deleted += Watcher_Deleted;
                watcher.Renamed += Watcher_Renamed;
                watcher.Error += Watcher_Error;

                watcher.Filter = "*.*";
                watcher.IncludeSubdirectories = false;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
            }
        }

        private static void Watcher_Error(object sender, ErrorEventArgs e)
        {
            _logger.Error("Error en FileSystemWatcher.", e.GetException());
        }

        private static void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            _logger.Debug($"Renamed: Old: {e.OldFullPath} New: {e.FullPath}");
        }

        private static void Watcher_Deleted(object sender, FileSystemEventArgs e)
        {
            _logger.Debug($"Deleted: {e.FullPath}");
        }

        private static void Watcher_Created(object sender, FileSystemEventArgs e)
        {
            string value = $"Created: {e.FullPath}";
        }

        private static void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
            {
                return;
            }

            _logger.Debug($"Changed: {e.FullPath}");
        }

        [DisableConcurrentExecution(60)]
        public static async Task MonitorearRecursosSistema()
        {
            try
            {
                var alerts = new List<string>();
                
                // Check CPU usage
                var cpuUsage = await ObtenerUsoCPU();
                if (cpuUsage > 80)
                {
                    alerts.Add($"🔥 CPU: {cpuUsage:F1}% (CRÍTICO)");
                }

                // Check RAM usage
                var ramUsage = await ObtenerUsoRAM();
                if (ramUsage > 80)
                {
                    alerts.Add($"🧠 RAM: {ramUsage:F1}% (CRÍTICO)");
                }

                // Check Disk usage
                var diskUsage = await ObtenerUsoDisco();
                if (diskUsage > 80)
                {
                    alerts.Add($"💾 DISCO: {diskUsage:F1}% (CRÍTICO)");
                }

                // Check Streams folder usage
                var streamsFolderUsage = await ObtenerUsoDiscoCarpeta(Constantes.Global.STREAMS_FOLDER);
                if (streamsFolderUsage > 80)
                {
                    alerts.Add($"📁 STREAMS ({Path.GetFileName(Constantes.Global.STREAMS_FOLDER.TrimEnd('/'))}): {streamsFolderUsage:F1}% (CRÍTICO)");
                }

                // Send alert if any resource is over 80%
                if (alerts.Count > 0)
                {
                    // Get full health info for context
                    var healthInfo = await ObtenerInfoSalud();
                    
                    var message = $"🚨 ALERTA DE RECURSOS: {Constantes.Global.PROVIDER_NAME} 🚨\n\n" +
                                 string.Join("\n", alerts) +
                                 $"\n\n{healthInfo}\n\n" +
                                 $"⏰ {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    await EnviarAlertaTelegram(message);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al monitorear recursos del sistema: {ex.Message}", ex);
            }
        }

        public static async Task<double> ObtenerUsoCPU()
        {
            try
            {
                // Try vmstat first (more reliable)
                // On Ubuntu, idle column can be $15 (no virtualization) or $16 (with st/stolen time)
                try
                {
                    // Try $16 first (Ubuntu with virtualization/VPS - most common case)
                    var result = await Cli
                        .Wrap("sh")
                        .WithArguments(new[] { "-c", "vmstat 1 2 | tail -1 | awk '{idle=$16; if(idle==\"\" || idle==0) idle=$15; print 100 - idle}'" })
                        .ExecuteBufferedAsync();

                    var output = result.StandardOutput.Trim().Replace(',', '.');
                    if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cpuUsage))
                    {
                        return Math.Max(0, Math.Min(100, cpuUsage)); // Clamp between 0-100
                    }
                }
                catch
                {
                    // vmstat not available or failed, fall back to /proc/stat
                }

                // Fallback to /proc/stat parsing with proper two-sample measurement
                var statResult = await Cli
                    .Wrap("sh")
                    .WithArguments(new[] { "-c", "awk '/^cpu / {idle1=$5+$6; total1=idle1+$2+$3+$4+$7+$8; print total1,idle1}' /proc/stat; sleep 1; awk '/^cpu / {idle2=$5+$6; total2=idle2+$2+$3+$4+$7+$8; print total2,idle2}' /proc/stat" })
                    .ExecuteBufferedAsync();

                var lines = statResult.StandardOutput.Trim().Split('\n');
                if (lines.Length >= 2)
                {
                    var firstLine = lines[0].Split(' ');
                    var secondLine = lines[1].Split(' ');
                    
                    if (firstLine.Length >= 2 && secondLine.Length >= 2 &&
                        double.TryParse(firstLine[0], out double total1) &&
                        double.TryParse(firstLine[1], out double idle1) &&
                        double.TryParse(secondLine[0], out double total2) &&
                        double.TryParse(secondLine[1], out double idle2))
                    {
                        var totalDiff = total2 - total1;
                        var idleDiff = idle2 - idle1;
                        
                        if (totalDiff > 0)
                        {
                            var cpuUsageFallback = (totalDiff - idleDiff) * 100 / totalDiff;
                            return Math.Max(0, Math.Min(100, cpuUsageFallback)); // Clamp between 0-100
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al obtener uso de CPU: {ex.Message}", ex);
            }

            return 0;
        }

        public static async Task<double> ObtenerUsoRAM()
        {
            try
            {
                // Use MemAvailable for more accurate memory usage (excludes caches)
                var result = await Cli
                    .Wrap("sh")
                    .WithArguments(new[] { "-c", "free | awk 'NR==2{total=$2; used=$3; available=$7} END {if(available>0) printf \"%.1f\", (total-available)*100/total; else printf \"%.1f\", used*100/total}'" })
                    .ExecuteBufferedAsync();

                var output = result.StandardOutput.Trim().Replace(',', '.');
                if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ramUsage))
                {
                    return Math.Max(0, Math.Min(100, ramUsage)); // Clamp between 0-100
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al obtener uso de RAM: {ex.Message}", ex);
            }

            return 0;
        }

        public static async Task<double> ObtenerUsoDisco()
        {
            try
            {
                // Scan all mount points using portable df -P, exclude pseudo filesystems
                var result = await Cli
                    .Wrap("sh")
                    .WithArguments(new[] { "-c", "df -P -x tmpfs -x devtmpfs | awk 'NR>1 {gsub(/%/, \"\", $5); if($5+0 > max) max=$5+0} END {print max+0}'" })
                    .ExecuteBufferedAsync();

                var output = result.StandardOutput.Trim().Replace(',', '.');
                if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double diskUsage))
                {
                    return Math.Max(0, Math.Min(100, diskUsage)); // Clamp between 0-100
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al obtener uso de disco: {ex.Message}", ex);
            }

            return 0;
        }

        public static async Task<(double cpu, double ram, double disk)> ObtenerMetricasSaludAsync()
        {
            var cpu = await ObtenerUsoCPU();
            var ram = await ObtenerUsoRAM();
            var disk = await ObtenerUsoDisco();
            return (cpu, ram, disk);
        }

        private static async Task<double> ObtenerUsoDiscoCarpeta(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    return 0;
                }

                // Get disk usage for specific folder
                var result = await Cli
                    .Wrap("sh")
                    .WithArguments(new[] { "-c", $"df -P \"{folderPath}\" | awk 'NR==2 {{gsub(/%/, \"\", $5); print $5+0}}'" })
                    .ExecuteBufferedAsync();

                var output = result.StandardOutput.Trim().Replace(',', '.');
                if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double diskUsage))
                {
                    return Math.Max(0, Math.Min(100, diskUsage)); // Clamp between 0-100
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al obtener uso de disco para carpeta {folderPath}: {ex.Message}", ex);
            }

            return 0;
        }

        private static async Task<string> ObtenerInfoSalud()
        {
            try
            {
                var cpuUsage = await ObtenerUsoCPU();
                var ramUsage = await ObtenerUsoRAM();
                var diskUsage = await ObtenerUsoDisco();
                var streamsFolderUsage = await ObtenerUsoDiscoCarpeta(Constantes.Global.STREAMS_FOLDER);

                var cpuEmoji = cpuUsage > 80 ? "🔥" : cpuUsage > 60 ? "⚠️" : "✅";
                var ramEmoji = ramUsage > 80 ? "🔥" : ramUsage > 60 ? "⚠️" : "✅";
                var diskEmoji = diskUsage > 80 ? "🔥" : diskUsage > 60 ? "⚠️" : "✅";
                var streamsEmoji = streamsFolderUsage > 80 ? "🔥" : streamsFolderUsage > 60 ? "⚠️" : "✅";

                return $"📊 SALUD DEL SERVIDOR:\n" +
                       $"{cpuEmoji} CPU: {cpuUsage:F1}%\n" +
                       $"{ramEmoji} RAM: {ramUsage:F1}%\n" +
                       $"{diskEmoji} Disco: {diskUsage:F1}%\n" +
                       $"{streamsEmoji} Streams ({Path.GetFileName(Constantes.Global.STREAMS_FOLDER.TrimEnd('/'))}): {streamsFolderUsage:F1}%";
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al obtener información de salud: {ex.Message}", ex);
                return "⚠️ No se pudo obtener información de salud";
            }
        }

        private static async Task EnviarAlertaTelegram(string message)
        {
            try
            {
                string token = "5334506189:AAG-OX79_IGuBFIzgSWF6WecoRt4AH3W4kM";
                string chatId = "-1001723468137";
                string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

                using (var webClient = new WebClient())
                {
                    await Task.Run(() => webClient.DownloadString(url));
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al enviar alerta de Telegram: {ex.Message}", ex);
            }
        }

        public static async Task AlertLowBufferAsync(int streamId, string details)
        {
            try
            {
                var envInfo = StreamExecutionGuard.GetEnvironmentInfo();
                var trimmed = details.Length > 200 ? details.Substring(0, 200) : details;
                var message = $"⚠️ BUFFER BAJO EN STREAM\nStream: {streamId}\nEnv: {envInfo}\nDetalle: {trimmed}";

                await EnviarAlertaTelegram(message);
                _logger.Info($"Buffer alert sent for stream {streamId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error al enviar alerta de buffer para stream {streamId}: {ex.Message}", ex);
            }
        }
    }
}
