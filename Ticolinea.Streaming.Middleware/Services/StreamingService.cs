using System.Text.Json;
using CliWrap.EventStream;
using CliWrap;
using CliWrap.Buffered;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Helpers;
using log4net;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ticolinea.stream.service.Services
{
    public static class StreamingService
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamingService));
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _tokens = new();
        
        private static readonly ConcurrentDictionary<int, DateTime> _lastProcessStart = new();
        private static readonly TimeSpan _minRestartInterval = TimeSpan.FromSeconds(12);
        private static readonly SemaphoreSlim _startupSemaphore = new SemaphoreSlim(20, 20);
        private static readonly int _maxConcurrentStartups = 20;
        
        private static readonly ConcurrentDictionary<int, (int failures, DateTime lastFailure)> _failureTracker = new();
        private static readonly int _maxFailures = 3;
        private static readonly TimeSpan _circuitBreakerTimeout = TimeSpan.FromMinutes(8);
        
        // Observability only (AdminController /api/admin/streams): FFmpeg launches per
        // stream since THIS SERVICE PROCESS BOOTED — not persisted, resets to 0 on
        // service restart. Incremented at the single launch choke point
        // (StartedCommandEvent in LanzarProcesoFfmpeg), so supervised restarts and
        // forced starts (ForzarInicioInmediato) both count. Never cleared on
        // stop/CleanupStreamState on purpose: the operator wants the history.
        private static readonly ConcurrentDictionary<int, int> _restartCounts = new();

        private static readonly ConcurrentDictionary<int, DateTime> _lastLogTime = new();
        private static readonly TimeSpan _logThrottleInterval = TimeSpan.FromSeconds(4);
        private static readonly ConcurrentDictionary<int, DateTime> _lastBufferAlert = new();
        private static readonly TimeSpan _bufferAlertThrottleInterval = TimeSpan.FromMinutes(10);

        // Marcas de kill intencional del watchdog (Jobs.MatarProcesoParaWatchdog las
        // pone justo ANTES de cada Kill() que realmente intenta). El exit != 0 que
        // ese kill provoca NO debe alimentar _failureTracker ni el presupuesto de
        // reintentos de SupervisarStream: el watchdog tiene su propio presupuesto
        // (WatchdogPolicy, 3 por ventana de 10 min) y sin esta marca 3 kills de
        // watchdog con menos de 8 min entre sí disparaban el circuit breaker en el
        // relanzo — canal caído 8 min por reinicios intencionales.
        //
        // La marca es un CONTADOR por stream, no un booleano: el watchdog puede
        // matar VARIOS PIDs en una pasada (pgrep) — una marca por kill entregado.
        // Si un Kill() falla o el PID ya había muerto, Jobs la retira
        // (RetractWatchdogKillMark): el invariante es marcas vivas == kills
        // realmente entregados, así una marca sin kill no puede enmascarar un
        // fallo real. Sólo el exit del proceso SUPERVISADO pasa por el manejador
        // de LanzarProcesoFfmpeg; las marcas de PIDs duplicados/rogue quedan
        // huérfanas y las purga el TTL de 60s (una entrada caducada se descarta
        // ENTERA, nunca se consume ni revive).
        private static readonly ConcurrentDictionary<int, (int count, DateTime markedAt)> _watchdogKillMarks = new();
        private static readonly TimeSpan _watchdogKillMarkTtl = TimeSpan.FromSeconds(60);

        public static void MarkWatchdogKill(int streamId) =>
            MarkWatchdogKill(streamId, DateTime.UtcNow);

        // Sobrecargas con reloj explícito (patrón WatchdogPolicy): públicas para que
        // la semántica marca→consumo→retiro sea unit-testeable de forma determinista.
        public static void MarkWatchdogKill(int streamId, DateTime nowUtc) =>
            _watchdogKillMarks.AddOrUpdate(
                streamId,
                (1, nowUtc),
                (_, existing) => nowUtc - existing.markedAt > _watchdogKillMarkTtl
                    ? (1, nowUtc)                     // lo caducado no revive: cuenta desde 1
                    : (existing.count + 1, nowUtc));  // acumula y refresca markedAt

        // Consumo de un solo uso POR KILL: decrementa (elimina al llegar a 0) y
        // devuelve true sólo dentro del TTL; una entrada caducada se elimina
        // ENTERA y devuelve false.
        public static bool TryConsumeWatchdogKillMark(int streamId, DateTime nowUtc)
        {
            while (_watchdogKillMarks.TryGetValue(streamId, out var mark))
            {
                if (nowUtc - mark.markedAt > _watchdogKillMarkTtl)
                {
                    _watchdogKillMarks.TryRemove(KeyValuePair.Create(streamId, mark));
                    return false;
                }

                if (mark.count <= 1
                        ? _watchdogKillMarks.TryRemove(KeyValuePair.Create(streamId, mark))
                        : _watchdogKillMarks.TryUpdate(streamId, (mark.count - 1, mark.markedAt), mark))
                {
                    return true;
                }
                // Carrera con otro Mark/Consume/Retract: reintentar sobre el valor fresco.
            }

            return false;
        }

        public static void RetractWatchdogKillMark(int streamId) =>
            RetractWatchdogKillMark(streamId, DateTime.UtcNow);

        // Retiro de una marca cuyo kill NO se entregó (Kill() lanzó, o el PID ya
        // había salido): decrementa sin "consumir". Mantiene el invariante
        // marcas vivas == kills entregados.
        public static void RetractWatchdogKillMark(int streamId, DateTime nowUtc)
        {
            while (_watchdogKillMarks.TryGetValue(streamId, out var mark))
            {
                if (nowUtc - mark.markedAt > _watchdogKillMarkTtl)
                {
                    _watchdogKillMarks.TryRemove(KeyValuePair.Create(streamId, mark));
                    return;
                }

                if (mark.count <= 1
                        ? _watchdogKillMarks.TryRemove(KeyValuePair.Create(streamId, mark))
                        : _watchdogKillMarks.TryUpdate(streamId, (mark.count - 1, mark.markedAt), mark))
                {
                    return;
                }
            }
        }

        // ---- Decisión de reintento de SupervisarStream (pura, unit-testeable) ----

        public const int MaxSupervisionRetries = 10;
        public const int RetryBaseDelaySeconds = 5;
        public const int RetryMaxDelaySeconds = 300;      // 5 minutos máximo de backoff
        // 5 min de ejecución sana antes de caer ⇒ el fallo cuenta como el PRIMERO.
        public const int StableRuntimeResetSeconds = 300;
        // Relanzo tras kill del watchdog: correctivo, queremos recuperación rápida.
        // El intervalo mínimo de 12s (_minRestartInterval) lo sigue aplicando
        // LanzarProcesoFfmpeg — este delay corto sólo evita un loop caliente.
        public const int WatchdogRelaunchDelaySeconds = 2;

        public enum RetryKind
        {
            // Kill intencional del watchdog: relanzar sin gastar presupuesto de reintentos.
            RelaunchAfterWatchdogKill,
            // Fallo genuino con presupuesto restante: backoff exponencial (el jitter lo aplica el caller).
            BackoffAndRetry,
            // MaxSupervisionRetries fallos genuinos consecutivos y rápidos: parada final de seguridad.
            StopSupervision
        }

        public readonly record struct RetryDecision(RetryKind Kind, int NewRetryCount, int BaseDelaySeconds);

        // Dado un exit != 0: ¿cuenta contra el presupuesto, cuánto esperar, o parar?
        // - watchdogKill: NO incrementa — el watchdog ya gasta su propio presupuesto
        //   (WatchdogPolicy) por cada kill; contarlo aquí también convertía reinicios
        //   correctivos en sentencia de muerte del canal.
        // - runtimeSeconds >= StableRuntimeResetSeconds: el proceso corrió sano un
        //   buen rato antes de caer ⇒ resetear ANTES de incrementar (es un primer
        //   fallo, no el N-ésimo). Sin esto retryCount se acumulaba de por vida —
        //   sólo exit 0 lo reseteaba y un canal en vivo sano nunca sale con 0 —
        //   así que 10 fallos orgánicos repartidos en semanas (cada uno recuperado)
        //   detenían la supervisión permanentemente.
        // El tope de MaxSupervisionRetries queda así reservado para fallos genuinos
        // RÁPIDOS y CONSECUTIVOS (con el circuit breaker también en juego): parada
        // final de seguridad aceptable.
        public static RetryDecision DecideRetry(bool watchdogKill, double runtimeSeconds, int currentRetryCount)
        {
            if (watchdogKill)
                return new RetryDecision(RetryKind.RelaunchAfterWatchdogKill, currentRetryCount, WatchdogRelaunchDelaySeconds);

            int newCount = (runtimeSeconds >= StableRuntimeResetSeconds ? 0 : currentRetryCount) + 1;
            if (newCount >= MaxSupervisionRetries)
                return new RetryDecision(RetryKind.StopSupervision, newCount, 0);

            int delay = Math.Min(
                RetryBaseDelaySeconds * (int)Math.Pow(2, Math.Min(newCount - 1, 6)),
                RetryMaxDelaySeconds);
            return new RetryDecision(RetryKind.BackoffAndRetry, newCount, delay);
        }

        // Resultado de una corrida completa de ffmpeg: el exit code y si ese exit
        // fue provocado por un kill intencional del watchdog. La clasificación sale
        // del ÚNICO consumo de la marca (TryConsumeWatchdogKillMark) en el manejo
        // del exit: ese consumo alimenta dos usos — saltar _failureTracker y esta
        // señal para que SupervisarStream no gaste presupuesto de reintentos.
        private readonly record struct FfmpegExitResult(int ExitCode, bool WatchdogKill);

        private class ProbeInfo
        {
            public List<ProbeStream> Streams { get; set; } = new();
        }

        private class ProbeStream
        {
            public string CodecName { get; set; }
            public string CodecType { get; set; }
        }

        public static void IniciarSupervision(StreamDb stream)
        {
            var cts = new CancellationTokenSource();
            if (_tokens.TryAdd(stream.StreamId, cts))
            {
                _logger.Debug($"Iniciando supervisión para stream {stream.StreamId}");
                _ = Task.Run(() => SupervisarStream(stream, cts.Token), cts.Token);
            }
            else
            {
                // Another thread added it, dispose our token
                cts.Dispose();
                _logger.Debug($"Stream {stream.StreamId} ya está siendo supervisado por otro hilo.");
            }
        }

        // Fix 1: Proper resource disposal
        public static void DetenerSupervision(int streamId)
        {
            if (_tokens.TryRemove(streamId, out var cts))
            {
                _logger.Debug($"Cancelando stream {streamId}...");
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    _logger.Debug($"Token ya estaba cancelado para stream {streamId}");
                }
                finally
                {
                    cts.Dispose(); // ✅ Proper disposal
                }

                CleanupStreamState(streamId);
            }
        }

        // Force immediate stream startup bypassing circuit breaker and delays
        public static async Task<bool> ForzarInicioInmediato(StreamDb stream)
        {
            try
            {
                // Clear any previous failures for this stream (bypass circuit breaker)
                _failureTracker.TryRemove(stream.StreamId, out _);
                _lastProcessStart.TryRemove(stream.StreamId, out _);
                
                _logger.Debug($"Forzando inicio inmediato para stream {stream.StreamId}");
                
                // Start supervision (this will trigger FFmpeg immediately)
                IniciarSupervision(stream);
                
                // Wait a reasonable time for startup
                await Task.Delay(TimeSpan.FromSeconds(3));
                
                // Check if process actually started
                var result = await Cli
                    .Wrap("/bin/pgrep")
                    .WithArguments(new[] { "-f", $"/{stream.StreamId}_.m3u8" })
                    .ExecuteBufferedAsync();
                
                bool started = !string.IsNullOrEmpty(result.StandardOutput.Trim());
                _logger.Debug($"Stream {stream.StreamId} inicio forzado: {(started ? "exitoso" : "falló")}");
                
                return started;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error en inicio forzado para stream {stream.StreamId}: {ex.Message}");
                return false;
            }
        }

        // Fix 2: Improved retry logic with exponential backoff.
        // La decisión de reintento (contador/delay/parada) vive en DecideRetry
        // (pura, unit-testeada); aquí sólo se mide el runtime y se aplica.
        private static async Task SupervisarStream(StreamDb stream, CancellationToken cancellationToken)
        {
            int retryCount = 0;

            _logger.Debug($"Iniciando supervisión para stream {stream.StreamId}");

            while (!cancellationToken.IsCancellationRequested)
            {
                // Fuera del try: el catch también mide cuánto corrió esta iteración.
                // Nota: incluye la espera de semáforo/intervalo mínimo dentro de
                // LanzarProcesoFfmpeg (<12s + espera de semáforo), ruido aceptable
                // frente al umbral de 300s.
                var launchedAtUtc = DateTime.UtcNow;
                try
                {
                    _logger.Debug($"Iniciando/reiniciando stream {stream.StreamId}... (intento {retryCount + 1})");

                    var result = await LanzarProcesoFfmpeg(stream, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (result.ExitCode != 0)
                    {
                        double runtimeSeconds = (DateTime.UtcNow - launchedAtUtc).TotalSeconds;
                        var decision = DecideRetry(result.WatchdogKill, runtimeSeconds, retryCount);
                        if (!result.WatchdogKill && decision.NewRetryCount == 1 && retryCount > 1)
                        {
                            _logger.Debug($"Stream {stream.StreamId}: contador de reintentos reseteado tras {runtimeSeconds:F0}s de ejecución estable.");
                        }
                        retryCount = decision.NewRetryCount;

                        if (decision.Kind == RetryKind.StopSupervision)
                        {
                            _logger.Error($"Stream {stream.StreamId} falló {MaxSupervisionRetries} veces consecutivas. Deteniendo supervisión.");
                            _tokens.TryRemove(stream.StreamId, out _);
                            break;
                        }

                        if (decision.Kind == RetryKind.RelaunchAfterWatchdogKill)
                        {
                            // Correctivo, no fallo: relanzo rápido sin backoff exponencial
                            // y sin gastar presupuesto. El intervalo mínimo de 12s lo
                            // sigue aplicando LanzarProcesoFfmpeg.
                            _logger.Info($"Stream {stream.StreamId}: relanzo por watchdog (exit {result.ExitCode}); no consume presupuesto de reintentos.");
                            await Task.Delay(TimeSpan.FromSeconds(decision.BaseDelaySeconds), cancellationToken);
                        }
                        else
                        {
                            // Exponential backoff with jitter to prevent thundering herd
                            int delaySeconds = decision.BaseDelaySeconds;

                            // Add random jitter (±25%)
                            int jitter = (int)(delaySeconds * 0.25 * (Random.Shared.NextDouble() - 0.5));
                            delaySeconds = Math.Max(1, delaySeconds + jitter);

                            _logger.Error($"FFmpeg falló (exitCode {result.ExitCode}) para {stream.StreamId}. Reintentando en {delaySeconds}s... (fallo {retryCount}/{MaxSupervisionRetries})");

                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                        }
                    }
                    else
                    {
                        // Reset retry count on success
                        if (retryCount > 0)
                        {
                            _logger.Debug($"Stream {stream.StreamId} recuperado exitosamente después de {retryCount} intentos");
                            retryCount = 0;
                        }

                        // Small delay even on success to prevent immediate restart
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug($"Supervisión cancelada para stream {stream.StreamId}");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error inesperado en supervisión del stream {stream.StreamId}: {ex.Message}", ex);

                    // Mismo presupuesto que un exit != 0 genuino (incluye el reset por
                    // corrida estable); el delay fijo corto es el histórico de esta ruta.
                    var decision = DecideRetry(
                        watchdogKill: false,
                        (DateTime.UtcNow - launchedAtUtc).TotalSeconds,
                        retryCount);
                    retryCount = decision.NewRetryCount;

                    if (decision.Kind == RetryKind.StopSupervision)
                    {
                        _logger.Error($"Stream {stream.StreamId} falló {MaxSupervisionRetries} veces por errores inesperados. Deteniendo supervisión.");
                        _tokens.TryRemove(stream.StreamId, out _);
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(RetryBaseDelaySeconds), cancellationToken);
                }
            }

            _logger.Debug($"Supervisión detenida para stream {stream.StreamId}");
            _tokens.TryRemove(stream.StreamId, out _);

            if (cancellationToken.IsCancellationRequested)
            {
                CleanupStreamState(stream.StreamId);
            }
        }

        private static async Task<FfmpegExitResult> LanzarProcesoFfmpeg(StreamDb stream, CancellationToken cancellationToken)
        {
            // Check if FFmpeg processes are allowed
            if (!StreamExecutionGuard.CanStartFFmpegProcesses())
            {
                _logger.Warn($"FFmpeg processes disabled - cannot launch FFmpeg for stream {stream.StreamId}");
                return new FfmpegExitResult(-1, false); // Return error code to indicate failure
            }

            var now = DateTime.UtcNow;
            if (_failureTracker.TryGetValue(stream.StreamId, out var failureInfo))
            {
                if (failureInfo.failures >= _maxFailures && now - failureInfo.lastFailure < _circuitBreakerTimeout)
                {
                    var remainingTime = _circuitBreakerTimeout - (now - failureInfo.lastFailure);
                    _logger.Warn($"Stream {stream.StreamId}: Circuit breaker active, skipping restart for {remainingTime.TotalMinutes:F1} more minutes");
                    return new FfmpegExitResult(-1, false);
                }
                else if (now - failureInfo.lastFailure >= _circuitBreakerTimeout)
                {
                    _failureTracker.TryRemove(stream.StreamId, out _);
                    _logger.Debug($"Stream {stream.StreamId}: Circuit breaker reset after timeout");
                }
            }

            if (_lastProcessStart.TryGetValue(stream.StreamId, out var lastStart))
            {
                var timeSinceLastStart = now - lastStart;
                if (timeSinceLastStart < _minRestartInterval)
                {
                    var waitTime = _minRestartInterval - timeSinceLastStart;
                    _logger.Debug($"Stream {stream.StreamId}: Waiting {waitTime.TotalSeconds:F1}s before restart");
                    await Task.Delay(waitTime, cancellationToken);
                }
            }
            _lastProcessStart.AddOrUpdate(stream.StreamId, now, (_, _) => now);

            await _startupSemaphore.WaitAsync(cancellationToken);
            
            int exitCode = -1;
            int processId = -1;
            bool processStarted = false;
            bool startupSemaphoreReleased = false;
            bool wasCancelled = false;
            
            try
            {
                _logger.Debug($"Stream {stream.StreamId}: Acquired startup semaphore (active startups: {_maxConcurrentStartups - _startupSemaphore.CurrentCount})");

            // Enhanced audio codec detection and conversion
            //string? detectedAudioCodec = await GetAudioCodec(stream.Fuente);
            string transcodeAudio = "-c:a aac -profile:a aac_low -b:a 128k -ar 48000 -ac 2";

            /*// Enhanced audio conversion logic
            if (!string.IsNullOrEmpty(detectedAudioCodec) && !detectedAudioCodec.Contains("aac", StringComparison.OrdinalIgnoreCase))
            {
                // Auto-convert non-AAC to AAC for better compatibility
                transcodeAudio = "-c:a aac -b:a 128k -ar 44100 -ac 2";
                _logger.Debug($"Stream {stream.StreamId}: Convirtiendo audio de {detectedAudioCodec} a AAC");
            }
            else if (!string.IsNullOrEmpty(stream.TranscodeAudio))
            {
                transcodeAudio = $"-c:a {stream.TranscodeAudio} -b:a 128k -ar 44100 -ac 2";
            }
            */

            string frameRate = stream.Transcode == 2 ? $" -r {stream.Framerate}" : "";
            string pixFmt = stream.Transcode == 1 ? "-pix_fmt yuv420p -async 1" : "";

            int analyzeDuration = stream.ProbeSize;
            var parameters = stream.Bitrate.Split("+", StringSplitOptions.RemoveEmptyEntries);

            // Enhanced reconnection logic
            string reconnect = "";
            var isSrt = stream.Fuente.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
            if (!isSrt)
            {
                var reconnectList = new List<string>();
                if (parameters.Contains("reconnect")) reconnectList.Add("-reconnect 1");
                if (parameters.Contains("reconnect_streamed")) reconnectList.Add("-reconnect_streamed 1");
                if (parameters.Contains("reconnect_on_network_error"))
                    reconnectList.Add("-reconnect_on_network_error 1");
                if (parameters.Contains("reconnect_on_http_error")) reconnectList.Add("-reconnect_on_http_error 1");
                if (parameters.Contains("reconnect_delay_max")) reconnectList.Add("-reconnect_delay_max 5");
                // -rw_timeout es DEFAULT para fuentes http(s) (opt-out: token "no_rw_timeout");
                // el resto de protocolos conserva el opt-in explícito "rw_timeout". Ver
                // FfmpegInputPolicy: fuente colgada → FFmpeg sale → la supervisión existente
                // lo reinicia (auto-recuperación sin watchdog).
                if (FfmpegInputPolicy.ShouldApplyRwTimeout(stream.Fuente, parameters))
                    reconnectList.Add("-rw_timeout 15000000");

                reconnect = string.Join(' ', reconnectList);
            }

            if (parameters.Contains("analyzeduration"))
                analyzeDuration = stream.GOP;

            var processFilePath = Constantes.Global.FFMPEG_PATH;

            var cmd = Cli.Wrap(processFilePath)
                .WithValidation(CommandResultValidation.None)
                .WithArguments(a => a
                    .Add("-y")
                    .Add("-hide_banner")
                    .Add("-nostdin")
                    .Add("-nostats")
                    .Add("-loglevel warning", false)
                    .Add("-err_detect ignore_err", false)
                    .Add("-threads 0", false) // Global threading for copy operations
                    //.Add(!isSrt ? "": "-ignore_unknown", false)
                    .Add(!isSrt ? "": "-fflags +genpts -avoid_negative_ts make_zero", false)
                    //.Add(isSrt ? "-fflags -avoid_negative_ts make_zero": "", false)
                    .Add($"{reconnect}", false)
                    .Add($"{frameRate}", false)
                    .Add("-thread_queue_size 4096", false) // 🧠 importante antes de -i (reduced from 8192 to balance memory/performance)
                    .Add($"-analyzeduration {analyzeDuration}", false) // ✅ Moved before -i
                    .Add($"-probesize {stream.ProbeSize}", false) // ✅ Moved before -i
                    .Add($"-i \"{stream.Fuente}\"", false)
                    .Add("-c:v copy", false)
                    .Add($"{transcodeAudio}", false)
                    .Add($"{pixFmt}", false)
                    .Add("-movflags +faststart", false)
                    .Add("-flags +global_header", false)
                    .Add(!isSrt ? "-mpegts_flags +resend_headers+initial_discontinuity+pat_pmt_at_frames" : "", false)
                    .Add("-hls_flags independent_segments+discont_start+append_list+omit_endlist+delete_segments+temp_file",
                        false)
                    // Piloto Streaming:FfmpegManagedDiscontinuities — apagado (default) devuelve "" (cero cambio).
                    .Add(FfmpegInputPolicy.ExtraHlsArgs(Constantes.Global.FFMPEG_MANAGED_DISCONTINUITIES), false)
                    .Add($"-hls_time {stream.Intervalo}", false)
                    .Add($"-hls_list_size {stream.Segmentos}", false)
                    .Add("-hls_delete_threshold 10", false)
                    .Add(
                        $"-hls_segment_filename {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_%d.ts {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_.m3u8",
                        false)
                );

            try
            {
                await foreach (var cmdEvent in cmd.ListenAsync(cancellationToken))
                {
                    switch (cmdEvent)
                    {
                        case StartedCommandEvent started:
                            processId = started.ProcessId;
                            processStarted = true;
                            _restartCounts.AddOrUpdate(stream.StreamId, 1, (_, n) => n + 1);
                            if (!startupSemaphoreReleased)
                            {
                                _startupSemaphore.Release();
                                startupSemaphoreReleased = true;
                                _logger.Debug($"Stream {stream.StreamId}: Released startup semaphore");
                            }

                            _logger.Debug($"Proceso iniciado: PID {processId} para stream {stream.StreamId}");
                            try
                            {
                                await Jobs.ActualizaInfoCanal(processId, stream.StreamId);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"Error actualizando info de canal para stream {stream.StreamId}: {ex.Message}", ex);
                            }
                            break;
                        case StandardOutputCommandEvent stdOut:
                            _logger.Debug($"Out-{stream.StreamId}> {stdOut.Text}");
                            break;
                        case StandardErrorCommandEvent stdErr:
                            if (IsLowBufferWarning(stdErr.Text) && ShouldSendBufferAlert(stream.StreamId))
                            {
                                _ = Jobs.AlertLowBufferAsync(stream.StreamId, stdErr.Text);
                            }

                            var shouldLog = ShouldLogError(stream.StreamId, stdErr.Text);
                            if (shouldLog)
                            {
                                _logger.Error($"Err-{stream.StreamId}> {stdErr.Text}");
                            }
                            break;
                        case ExitedCommandEvent exited:
                            exitCode = exited.ExitCode;
                            _logger.Debug($"FFmpeg terminó para stream {stream.StreamId}; Código: {exitCode}{(cancellationToken.IsCancellationRequested ? " (cancelado)" : "")}");
                            await Jobs.ActualizarCanalEstado(stream.StreamId, true, -1);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                _logger.Debug($"Proceso cancelado manualmente para stream {stream.StreamId}");
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug($"Error ignorado por cancelación para stream {stream.StreamId}: {ex.Message}");
                }
                else
                {
                    _logger.Error($"Error inesperado en FFmpeg para stream {stream.StreamId}: {ex.Message}", ex);
                }
            }

            }
            finally
            {
                if (!startupSemaphoreReleased)
                {
                    _startupSemaphore.Release();
                    startupSemaphoreReleased = true;
                    _logger.Debug($"Stream {stream.StreamId}: Released startup semaphore");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
            }

            if (wasCancelled)
            {
                exitCode = 0;
            }

            bool watchdogKill = false;
            if (!wasCancelled && exitCode != 0)
            {
                var failureTime = DateTime.UtcNow;
                // ÚNICO consumo de la marca: alimenta dos usos — saltar el circuit
                // breaker aquí y la clasificación que SupervisarStream usa para no
                // gastar presupuesto de reintentos (FfmpegExitResult.WatchdogKill).
                watchdogKill = TryConsumeWatchdogKillMark(stream.StreamId, failureTime);
                if (watchdogKill)
                {
                    // Kill intencional del watchdog: el relanzo supervisado sigue su
                    // curso normal (_minRestartInterval, BD), pero este exit NO
                    // cuenta como fallo para el circuit breaker ni para el contador
                    // de reintentos de la supervisión.
                    _logger.Debug($"Stream {stream.StreamId}: exit {exitCode} provocado por kill del watchdog; no cuenta como fallo.");
                }
                else
                {
                    _failureTracker.AddOrUpdate(stream.StreamId,
                        (1, failureTime),
                        (_, existing) => (existing.failures + 1, failureTime)
                    );

                    var currentFailures = _failureTracker[stream.StreamId].failures;
                    if (currentFailures >= _maxFailures)
                    {
                        _logger.Error($"Stream {stream.StreamId}: Circuit breaker triggered after {currentFailures} failures");
                    }
                }
            }
            else if (!wasCancelled)
            {
                _failureTracker.TryRemove(stream.StreamId, out _);
                // Salida limpia: descartar TODAS las marcas de watchdog que quedaran
                // vivas (carrera kill/exit-0) para que no enmascaren un fallo real
                // posterior.
                _watchdogKillMarks.TryRemove(stream.StreamId, out _);
            }

            return new FfmpegExitResult(exitCode, watchdogKill);
        }

        private static void CleanupStreamState(int streamId)
        {
            _lastProcessStart.TryRemove(streamId, out _);
            _failureTracker.TryRemove(streamId, out _);
            _lastLogTime.TryRemove(streamId, out _);
            _lastBufferAlert.TryRemove(streamId, out _);
            _watchdogKillMarks.TryRemove(streamId, out _);
        }

        private static bool ShouldLogError(int streamId, string errorText)
        {
            var now = DateTime.UtcNow;
            
            // Check if we should throttle this stream
            if (_lastLogTime.TryGetValue(streamId, out var lastLog))
            {
                if (now - lastLog < _logThrottleInterval)
                {
                    return false; // Skip this log entry
                }
            }
            
            // Update last log time
            _lastLogTime.AddOrUpdate(streamId, now, (_, _) => now);
            
            // Filter out "normal" FFmpeg output that's not really an error
            var normalPatterns = new[]
            {
                "frame=", "fps=", "bitrate=", "time=", "speed=", "size=",
                "Opening", "Closing", "Seeking", "Input", "Output",
                "Stream mapping:", "Press [q]", "Conversion failed!"
            };
            
            // Only log if it's not a normal pattern
            return !normalPatterns.Any(pattern => errorText.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsLowBufferWarning(string errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText))
            {
                return false;
            }

            var patterns = new[]
            {
                "Thread message queue blocking",
                "Input buffer full",
                "Queue overflow",
                "thread_queue_size"
            };

            return patterns.Any(pattern => errorText.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldSendBufferAlert(int streamId)
        {
            var now = DateTime.UtcNow;
            if (_lastBufferAlert.TryGetValue(streamId, out var lastAlert))
            {
                if (now - lastAlert < _bufferAlertThrottleInterval)
                {
                    return false;
                }
            }

            _lastBufferAlert.AddOrUpdate(streamId, now, (_, _) => now);
            return true;
        }

        // Enhanced audio codec detection with better error handling
        private static async Task<string?> GetAudioCodec(string input, int timeoutSeconds = 10)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                var result = await Cli
                    .Wrap("ffprobe")
                    .WithArguments($"-v error -show_entries stream=codec_name,codec_type -of json \"{input}\"")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cts.Token);

                var json = result.StandardOutput;

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.Debug($"ffprobe no devolvió datos para {input}");
                    return null;
                }

                var probeInfo = JsonSerializer.Deserialize<ProbeInfo>(json);

                var audioStream = probeInfo?.Streams?.FirstOrDefault(s => s.CodecType == "audio");

                if (audioStream == null)
                {
                    _logger.Debug($"No se encontró stream de audio en {input}");
                    return null;
                }

                _logger.Debug($"Codec de audio detectado: {audioStream.CodecName} para {input}");
                return audioStream.CodecName;
            }
            catch (OperationCanceledException)
            {
                _logger.Debug($"ffprobe timeout para {input}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"ffprobe falló para {input}: {ex.Message}", ex);
                return null;
            }
        }

        // FFmpeg launches for this stream since the service process booted (see
        // _restartCounts). 0 if the stream was never launched by this process.
        public static int GetRestartCount(int streamId)
        {
            return _restartCounts.TryGetValue(streamId, out var count) ? count : 0;
        }

        // Seconds since this stream's ffmpeg was last (re)started, or 0 if not tracked.
        public static double GetStreamUptimeSeconds(int streamId)
        {
            if (_lastProcessStart.TryGetValue(streamId, out var start))
                return Math.Max(0, (DateTime.UtcNow - start).TotalSeconds);
            return 0;
        }

        public static object GetPerformanceStats()
        {
            return new
            {
                timestamp = DateTime.UtcNow,
                activeStreams = _tokens.Count,
                startupSemaphore = new
                {
                    available = _startupSemaphore.CurrentCount,
                    maxConcurrent = _maxConcurrentStartups,
                    utilization = (_maxConcurrentStartups - _startupSemaphore.CurrentCount) * 100.0 / _maxConcurrentStartups
                },
                rapidRestartProtection = new
                {
                    enabled = true,
                    minRestartIntervalSeconds = _minRestartInterval.TotalSeconds,
                    trackedStreams = _lastProcessStart.Count
                },
                circuitBreaker = new
                {
                    enabled = true,
                    maxFailures = _maxFailures,
                    timeoutMinutes = _circuitBreakerTimeout.TotalMinutes,
                    activeBreakers = _failureTracker.Count(x => x.Value.failures >= _maxFailures)
                },
                loggingThrottle = new
                {
                    enabled = true,
                    throttleIntervalSeconds = _logThrottleInterval.TotalSeconds,
                    trackedStreams = _lastLogTime.Count
                },
                optimizations = new
                {
                    startupThrottling = $"Max {_maxConcurrentStartups} concurrent startups (allows 155+ runtime processes)",
                    restartStormProtection = "12-second minimum between restarts (prevents restart storms)",
                    circuitBreaker = "3 failures trigger 8-minute backoff (prevents endless retries)",
                    loggingThrottle = "4-second throttle per stream (prevents log spam)",
                    threadSafeTokenManagement = "ConcurrentDictionary for stream tokens",
                    exponentialBackoff = "Retry logic with jitter"
                }
            };
        }
    }
}
