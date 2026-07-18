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

        // Fix 2: Improved retry logic with exponential backoff
        private static async Task SupervisarStream(StreamDb stream, CancellationToken cancellationToken)
        {
            int retryCount = 0;
            const int maxRetries = 10;
            const int baseDelaySeconds = 5;
            const int maxDelaySeconds = 300; // 5 minutes max

            _logger.Debug($"Iniciando supervisión para stream {stream.StreamId}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.Debug($"Iniciando/reiniciando stream {stream.StreamId}... (intento {retryCount + 1})");

                    int exitCode = await LanzarProcesoFfmpeg(stream, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (exitCode != 0)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            _logger.Error($"Stream {stream.StreamId} falló {maxRetries} veces consecutivas. Deteniendo supervisión.");
                            _tokens.TryRemove(stream.StreamId, out _);
                            break;
                        }

                        // Exponential backoff with jitter to prevent thundering herd
                        int delaySeconds = Math.Min(
                            baseDelaySeconds * (int)Math.Pow(2, Math.Min(retryCount - 1, 6)),
                            maxDelaySeconds
                        );
                        
                        // Add random jitter (±25%)
                        int jitter = (int)(delaySeconds * 0.25 * (Random.Shared.NextDouble() - 0.5));
                        delaySeconds = Math.Max(1, delaySeconds + jitter);

                        _logger.Error($"FFmpeg falló (exitCode {exitCode}) para {stream.StreamId}. Reintentando en {delaySeconds}s...");
                        
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
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
                    retryCount++;
                    _logger.Error($"Error inesperado en supervisión del stream {stream.StreamId}: {ex.Message}", ex);
                    
                    if (retryCount >= maxRetries)
                    {
                        _logger.Error($"Stream {stream.StreamId} falló {maxRetries} veces por errores inesperados. Deteniendo supervisión.");
                        _tokens.TryRemove(stream.StreamId, out _);
                        break;
                    }
                    
                    await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds), cancellationToken);
                }
            }

            _logger.Debug($"Supervisión detenida para stream {stream.StreamId}");
            _tokens.TryRemove(stream.StreamId, out _);

            if (cancellationToken.IsCancellationRequested)
            {
                CleanupStreamState(stream.StreamId);
            }
        }

        private static async Task<int> LanzarProcesoFfmpeg(StreamDb stream, CancellationToken cancellationToken)
        {
            // Check if FFmpeg processes are allowed
            if (!StreamExecutionGuard.CanStartFFmpegProcesses())
            {
                _logger.Warn($"FFmpeg processes disabled - cannot launch FFmpeg for stream {stream.StreamId}");
                return -1; // Return error code to indicate failure
            }

            var now = DateTime.UtcNow;
            if (_failureTracker.TryGetValue(stream.StreamId, out var failureInfo))
            {
                if (failureInfo.failures >= _maxFailures && now - failureInfo.lastFailure < _circuitBreakerTimeout)
                {
                    var remainingTime = _circuitBreakerTimeout - (now - failureInfo.lastFailure);
                    _logger.Warn($"Stream {stream.StreamId}: Circuit breaker active, skipping restart for {remainingTime.TotalMinutes:F1} more minutes");
                    return -1;
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

            if (!wasCancelled && exitCode != 0)
            {
                var failureTime = DateTime.UtcNow;
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
            else if (!wasCancelled)
            {
                _failureTracker.TryRemove(stream.StreamId, out _);
            }

            return exitCode;
        }

        private static void CleanupStreamState(int streamId)
        {
            _lastProcessStart.TryRemove(streamId, out _);
            _failureTracker.TryRemove(streamId, out _);
            _lastLogTime.TryRemove(streamId, out _);
            _lastBufferAlert.TryRemove(streamId, out _);
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
