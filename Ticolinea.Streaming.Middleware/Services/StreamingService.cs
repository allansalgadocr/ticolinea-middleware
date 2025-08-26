using System.Text.Json;
using CliWrap.EventStream;
using CliWrap;
using CliWrap.Buffered;
using ticolinea.stream.service.Modelos;
using log4net;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ticolinea.stream.service.Services
{
    public static class StreamingService
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamingService));
        // Fix 3: Thread-safe token management
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _tokens = new();
        private static readonly object _lockObject = new object();

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
            // Thread-safe check and add
            if (_tokens.ContainsKey(stream.StreamId))
            {
                _logger.Warn($"Stream {stream.StreamId} ya está siendo supervisado.");
                return;
            }

            var cts = new CancellationTokenSource();
            if (_tokens.TryAdd(stream.StreamId, cts))
            {
                _logger.Info($"Iniciando supervisión para stream {stream.StreamId}");
                _ = Task.Run(() => SupervisarStream(stream, cts.Token), cts.Token);
            }
            else
            {
                // Another thread added it, dispose our token
                cts.Dispose();
                _logger.Warn($"Stream {stream.StreamId} ya fue iniciado por otro hilo.");
            }
        }

        // Fix 1: Proper resource disposal
        public static void DetenerSupervision(int streamId)
        {
            if (_tokens.TryRemove(streamId, out var cts))
            {
                _logger.Info($"Cancelando stream {streamId}...");
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    _logger.Warn($"Token ya estaba cancelado para stream {streamId}");
                }
                finally
                {
                    cts.Dispose(); // ✅ Proper disposal
                }
            }
        }

        // Fix 2: Improved retry logic with exponential backoff
        private static async Task SupervisarStream(StreamDb stream, CancellationToken cancellationToken)
        {
            int retryCount = 0;
            const int maxRetries = 10;
            const int baseDelaySeconds = 5;
            const int maxDelaySeconds = 300; // 5 minutes max

            _logger.Info($"Iniciando supervisión para stream {stream.StreamId}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.Info($"Iniciando/reiniciando stream {stream.StreamId}... (intento {retryCount + 1})");

                    int exitCode = await LanzarProcesoFfmpeg(stream, cancellationToken);

                    if (exitCode != 0)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            _logger.Error($"Stream {stream.StreamId} falló {maxRetries} veces consecutivas. Deteniendo supervisión.");
                            break;
                        }

                        // Exponential backoff with jitter to prevent thundering herd
                        int delaySeconds = Math.Min(
                            baseDelaySeconds * (int)Math.Pow(2, Math.Min(retryCount - 1, 6)),
                            maxDelaySeconds
                        );
                        
                        // Add random jitter (±25%)
                        var random = new Random();
                        int jitter = (int)(delaySeconds * 0.25 * (random.NextDouble() - 0.5));
                        delaySeconds = Math.Max(1, delaySeconds + jitter);

                        _logger.Error($"FFmpeg falló (exitCode {exitCode}) para {stream.StreamId}. Reintentando en {delaySeconds}s...");
                        Data.Streams.InsertaStreamError($"({stream.StreamId}): Proceso reiniciado por error {exitCode} (intento {retryCount})");
                        
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    }
                    else
                    {
                        // Reset retry count on success
                        if (retryCount > 0)
                        {
                            _logger.Info($"Stream {stream.StreamId} recuperado exitosamente después de {retryCount} intentos");
                            retryCount = 0;
                        }
                        
                        // Small delay even on success to prevent immediate restart
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Info($"Supervisión cancelada para stream {stream.StreamId}");
                    break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.Error($"Error inesperado en supervisión del stream {stream.StreamId}: {ex.Message}", ex);
                    Data.Streams.InsertaStreamError($"({stream.StreamId}): Error inesperado: {ex.Message}");
                    
                    if (retryCount >= maxRetries)
                    {
                        _logger.Error($"Stream {stream.StreamId} falló {maxRetries} veces por errores inesperados. Deteniendo supervisión.");
                        break;
                    }
                    
                    await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds), cancellationToken);
                }
            }

            _logger.Info($"Supervisión detenida para stream {stream.StreamId}");
        }

        private static async Task<int> LanzarProcesoFfmpeg(StreamDb stream, CancellationToken cancellationToken)
        {
            int exitCode = -1;
            int processId = -1;

            // Enhanced audio codec detection and conversion
            string? detectedAudioCodec = await GetAudioCodec(stream.Fuente);
            string transcodeAudio = "-c:a copy";

            // Enhanced audio conversion logic
            if (!string.IsNullOrEmpty(stream.TranscodeAudio))
            {
                // User-specified audio codec takes priority
                if (stream.TranscodeAudio.Equals("aac", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(detectedAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
                    {
                        transcodeAudio = "-c:a copy -threads 2";
                    }
                    else
                    {
                        transcodeAudio = "-c:a aac -b:a 128k -ar 44100 -ac 2 -threads 2";
                    }
                }
                else
                {
                    transcodeAudio = $"-c:a {stream.TranscodeAudio} -b:a 128k -ar 44100 -ac 2 -threads 2";
                }
            }
            else if (!string.Equals(detectedAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            {
                // Auto-convert non-AAC to AAC for better compatibility
                transcodeAudio = "-c:a aac -b:a 128k -ar 44100 -ac 2 -threads 2";
                _logger.Info($"Stream {stream.StreamId}: Convirtiendo audio de {detectedAudioCodec} a AAC");
            }

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
                if (parameters.Contains("rw_timeout")) reconnectList.Add("-rw_timeout 15000000");

                reconnect = string.Join(' ', reconnectList);
            }

            if (parameters.Contains("analyzeduration"))
                analyzeDuration = stream.GOP;

            var processFilePath = Constantes.Global.FFMPEG_PATH;

            var cmd = Cli.Wrap(processFilePath).WithValidation(CommandResultValidation.None)
                .WithArguments(a => a
                    .Add("-y")
                    .Add("-hide_banner")
                    .Add("-nostdin")
                    .Add("-nostats")
                    .Add("-loglevel warning", false)
                    .Add("-err_detect ignore_err", false)
                    .Add(!isSrt ? "": "-ignore_unknown", false)
                    .Add(!isSrt ? "": "-fflags +genpts", false)
                    .Add(!isSrt ? "": "-avoid_negative_ts make_zero", false)
                    .Add($"{reconnect}", false)
                    .Add($"{frameRate}", false)
                    .Add("-thread_queue_size 2048", false) // 🧠 importante antes de -i
                    .Add($"-i \"{stream.Fuente}\"", false)
                    .Add("-c:v copy", false)
                    .Add($"{transcodeAudio}", false)
                    .Add($"-analyzeduration {analyzeDuration}", false)
                    .Add($"-probesize {stream.ProbeSize}", false)
                    .Add($"{pixFmt}", false)
                    .Add("-movflags +faststart", false)
                    .Add("-flags +global_header", false)
                    .Add("-hls_flags +discont_start+omit_endlist+append_list+delete_segments+temp_file+split_by_time",
                        false)
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
                            _logger.Info($"Proceso iniciado: PID {processId} para stream {stream.StreamId}");
                            await Jobs.ActualizaInfoCanal(processId, stream.StreamId);
                            break;
                        case StandardOutputCommandEvent stdOut:
                            _logger.Debug($"Out-{stream.StreamId}> {stdOut.Text}");
                            break;
                        case StandardErrorCommandEvent stdErr:
                            _logger.Error($"Err-{stream.StreamId}> {stdErr.Text}");
                            Data.Streams.InsertaStreamError($"({stream.StreamId}): {stdErr.Text}");
                            break;
                        case ExitedCommandEvent exited:
                            exitCode = exited.ExitCode;
                            _logger.Info($"FFmpeg terminó para stream {stream.StreamId}; Código: {exitCode}");
                            Data.Streams.InsertaStreamError($"({stream.StreamId}): Finalizó con código {exitCode}");
                            await Jobs.ActualizarCanalEstado(stream.StreamId, true, -1);
                            
                            // Fix 4: Add process cleanup
                            if (processId > 0)
                            {
                                try
                                {
                                    var process = Process.GetProcessById(processId);
                                    if (process != null && !process.HasExited)
                                    {
                                        _logger.Info($"Limpiando proceso {processId} para stream {stream.StreamId}");
                                        process.Kill();
                                        process.WaitForExit(5000); // Wait up to 5 seconds
                                    }
                                }
                                catch (ArgumentException)
                                {
                                    // Process already dead, this is normal
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn($"Error al limpiar proceso {processId}: {ex.Message}");
                                }
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"Proceso cancelado manualmente para stream {stream.StreamId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error inesperado en FFmpeg para stream {stream.StreamId}: {ex.Message}", ex);
                Data.Streams.InsertaStreamError($"({stream.StreamId}): Error FFmpeg: {ex.Message}");
            }

            return exitCode;
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
                    _logger.Warn($"ffprobe no devolvió datos para {input}");
                    return null;
                }

                var probeInfo = JsonSerializer.Deserialize<ProbeInfo>(json);

                var audioStream = probeInfo?.Streams?.FirstOrDefault(s => s.CodecType == "audio");

                if (audioStream == null)
                {
                    _logger.Warn($"No se encontró stream de audio en {input}");
                    return null;
                }

                _logger.Debug($"Codec de audio detectado: {audioStream.CodecName} para {input}");
                return audioStream.CodecName;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"ffprobe timeout para {input}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"ffprobe falló para {input}: {ex.Message}", ex);
                return null;
            }
        }
    }
}