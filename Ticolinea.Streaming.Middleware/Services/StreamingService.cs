using System.Text.Json;
using CliWrap.EventStream;
using CliWrap;
using CliWrap.Buffered;
using ticolinea.stream.service.Modelos;
using log4net;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net.Config;

namespace ticolinea.stream.service.Services
{
    public static class StreamingService
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamingService));
        private const string LOG_PATH = "/home/ticolineaplay/logs";
        
        static StreamingService()
        {
            try
            {
                // Ensure log directory exists
                Directory.CreateDirectory(LOG_PATH);
                
                // Initialize log4net
                var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
                var configFile = new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log4net.config"));
                
                if (!configFile.Exists)
                {
                    // Try to find the config file in the current directory
                    configFile = new FileInfo("log4net.config");
                }

                if (!configFile.Exists)
                {
                    Console.Error.WriteLine($"Warning: log4net.config not found at {configFile.FullName}");
                    BasicConfigurator.Configure(logRepository);
                }
                else
                {
                    XmlConfigurator.Configure(logRepository, configFile);
                }
                
                // Test logging
                _logger.Info("StreamingService logger initialized successfully");
                
                // Log the paths being used to help with debugging
                _logger.Info($"Log directory path: {LOG_PATH}");
                _logger.Info($"Config file path: {configFile.FullName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to initialize logger: {ex.Message}");
                Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Try to write to a fallback log file
                try
                {
                    File.AppendAllText(
                        Path.Combine(LOG_PATH, "streaming_fallback.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Logger initialization failed: {ex.Message}\n{ex.StackTrace}\n"
                    );
                }
                catch
                {
                    Console.Error.WriteLine("Failed to write to fallback log file");
                }
            }
        }

        private static readonly Dictionary<int, CancellationTokenSource> _tokens = new();

        private class ProbeInfo
        {
            public List<ProbeStream> Streams { get; set; } = new();
        }

        private class ProbeStream
        {
            public string CodecName { get; set; }
            public string CodecType { get; set; }
        }

        public class StreamHealth
        {
            public DateTime LastSuccessfulCheck { get; set; }
            public int SegmentCount { get; set; }
            public bool IsHealthy { get; set; }
        }

        public static void IniciarSupervision(StreamDb stream)
        {
            if (_tokens.ContainsKey(stream.StreamId))
            {
                LogWithFallback($"Stream {stream.StreamId} ya está siendo supervisado.");
                return;
            }

            var cts = new CancellationTokenSource();
            _tokens[stream.StreamId] = cts;

            _ = Task.Run(() => SupervisarStream(stream, cts.Token));
        }

        public static void DetenerSupervision(int streamId)
        {
            if (_tokens.TryGetValue(streamId, out var cts))
            {
                LogWithFallback($"Cancelando stream {streamId}...");
                cts.Cancel();
                _tokens.Remove(streamId);
            }
        }

        private static async Task SupervisarStream(StreamDb stream, CancellationToken cancellationToken)
        {
            using var cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            
            _ = Task.Run(async () =>
            {
                while (await cleanupTimer.WaitForNextTickAsync(cancellationToken))
                {
                    await CleanupOldSegments(stream.StreamId);
                }
            }, cancellationToken);

            const int initialRetryDelay = 5;
            const int maxRetryDelay = 30;
            int currentRetryDelay = initialRetryDelay;
            int consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    LogWithFallback($"Iniciando/reiniciando stream {stream.StreamId}...");
                    int exitCode = await LanzarProcesoFfmpeg(stream, cancellationToken);

                    if (exitCode == 0)
                    {
                        // Reset counters on success
                        consecutiveFailures = 0;
                        currentRetryDelay = initialRetryDelay;
                    }
                    else
                    {
                        consecutiveFailures++;
                        LogWithFallback($"FFmpeg falló (exitCode {exitCode}) para {stream.StreamId}. Intento {consecutiveFailures}");
                        Data.Streams.InsertaStreamError($"({stream.StreamId}): Proceso reiniciado por error {exitCode}");
                        
                        // Exponential backoff with max limit
                        currentRetryDelay = Math.Min(currentRetryDelay * 2, maxRetryDelay);
                    }
                }
                catch (Exception ex)
                {
                    LogWithFallback($"Error inesperado en stream {stream.StreamId}", ex, true);
                    consecutiveFailures++;
                    currentRetryDelay = Math.Min(currentRetryDelay * 2, maxRetryDelay);
                }

                // Notify if stream is consistently failing
                if (consecutiveFailures >= 5)
                {
                    LogWithFallback($"Stream {stream.StreamId} ha fallado {consecutiveFailures} veces consecutivas");
                    // Consider implementing notification system here
                }

                await Task.Delay(TimeSpan.FromSeconds(currentRetryDelay), cancellationToken)
                    .ContinueWith(_ => { });
            }

            LogWithFallback($"Supervisión detenida para stream {stream.StreamId}");
        }

        private static async Task<int> LanzarProcesoFfmpeg(StreamDb stream, CancellationToken cancellationToken)
        {
            int exitCode = -1;

            string? detectedAudioCodec = await GetAudioCodec(stream.Fuente);
            string transcodeAudio = " -acodec copy";

            // Si el codec detectado NO es AAC → transcodificar a AAC
            if (!string.Equals(detectedAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            {
                transcodeAudio = " -acodec aac -b:a 128k -ar 44100 -ac 2 -threads 2";
            }
            // Si hay un codec forzado en BD → sobrescribe lo anterior
            else if (!string.IsNullOrEmpty(stream.TranscodeAudio))
            {
                transcodeAudio = stream.TranscodeAudio.Equals("aac", StringComparison.OrdinalIgnoreCase)
                    ? " -acodec aac -b:a 128k -ar 44100 -ac 2 -threads 2"
                    : $" -acodec {stream.TranscodeAudio} -threads 2";
            }

            string frameRate = stream.Transcode == 2 ? $" -r {stream.Framerate}" : "";
            string pixFmt = stream.Transcode == 1 ? "-pix_fmt yuv420p -async 1" : "";

            int analyzeDuration = stream.ProbeSize;
            var parameters = stream.Bitrate.Split("+", StringSplitOptions.RemoveEmptyEntries);

            // 🌐 Agregar reconexión solo si no es SRT
            string reconnect = "";
            if (!stream.Fuente.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
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

            string processFilePath = stream.Fuente.StartsWith("srt://")
                ? Constantes.Global.FFMPEG_PATH_SRT
                : Constantes.Global.FFMPEG_PATH;

            var cmd = Cli.Wrap(processFilePath).WithValidation(CommandResultValidation.None)
                .WithArguments(a => a
                    // Basic settings
                    .Add("-y")
                    .Add("-hide_banner")
                    .Add("-nostdin")
                    .Add("-nostats")
                    .Add("-loglevel warning", false)
                    
                    // Error handling - important for live streams
                    .Add("-err_detect ignore_err", false)
                    .Add("-ignore_unknown", false)
                    
                    // Reconnection settings
                    .Add(reconnect, false)
                    
                    // Input settings
                    .Add("-thread_queue_size 512", false)
                    .Add($"-i \"{stream.Fuente}\"", false)
                    
                    // Stream analysis
                    .Add($"-analyzeduration {analyzeDuration}", false)
                    .Add($"-probesize {stream.ProbeSize}", false)
                    
                    // Copy video stream without transcoding
                    .Add("-c:v copy", false)
                    
                    // Audio handling - only transcode if needed
                    .Add($"{transcodeAudio}", false)
                    
                    // M3U8 settings
                    .Add("-f m3u8", false)
                    .Add("-hls_time {stream.Intervalo}", false)
                    .Add("-hls_list_size {stream.Segmentos}", false)
                    .Add("-hls_flags +discont_start+omit_endlist+append_list+delete_segments+temp_file+split_by_time", false)
                    .Add("-hls_delete_threshold 10", false)
                    .Add($"-hls_segment_filename {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_%d.ts {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_.m3u8", false)
                    
                    // Output settings
                    .Add("-movflags +faststart", false)
                    .Add("-flags +global_header", false)
                );

            try
            {
                await foreach (var cmdEvent in cmd.ListenAsync(cancellationToken))
                {
                    switch (cmdEvent)
                    {
                        case StartedCommandEvent started:
                            LogWithFallback($"Proceso iniciado: PID {started.ProcessId}");
                            await Jobs.ActualizaInfoCanal(started.ProcessId, stream.StreamId);
                            break;
                        case StandardOutputCommandEvent stdOut:
                            LogWithFallback($"Out-{stream.StreamId}> {stdOut.Text}");
                            break;
                        case StandardErrorCommandEvent stdErr:
                            LogWithFallback($"Err-{stream.StreamId}> {stdErr.Text}");
                            Data.Streams.InsertaStreamError(stdErr.Text);
                            
                            if (IsStreamCriticalError(stdErr.Text))
                            {
                                LogWithFallback($"Critical error detected for stream {stream.StreamId}");
                                return -1;
                            }
                            break;
                        case ExitedCommandEvent exited:
                            exitCode = exited.ExitCode;
                            LogWithFallback($"FFmpeg terminó para stream {stream.StreamId}; Código: {exitCode}");
                            Data.Streams.InsertaStreamError($"({stream.StreamId}): Finalizó {exitCode}");
                            await Jobs.ActualizarCanalEstado(stream.StreamId, true, -1);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogWithFallback($"Proceso cancelado manualmente para stream {stream.StreamId}");
            }

            return exitCode;
        }

        private static bool IsStreamCriticalError(string error)
        {
            var criticalErrors = new[]
            {
                "Connection refused",
                "Connection timed out",
                "Invalid data found",
                "Error opening input",
                "Server returned 4",
                "Server returned 5",
                "Cannot allocate memory"
            };
            
            return criticalErrors.Any(e => error.Contains(e, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string?> GetAudioCodec(string input, int timeoutSeconds = 5)
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

                var probeInfo = JsonSerializer.Deserialize<ProbeInfo>(json);

                var audioStream = probeInfo?.Streams.FirstOrDefault(s => s.CodecType == "audio");

                return audioStream?.CodecName;
            }
            catch (Exception ex)
            {
                LogWithFallback("ffprobe failed or timed out", ex, true);
                return null;
            }
        }

        private static async Task CleanupOldSegments(int streamId)
        {
            try
            {
                var directory = new DirectoryInfo(Constantes.Global.STREAMS_FOLDER);
                var oldSegments = directory.GetFiles($"{streamId}_*.ts")
                    .Where(f => f.CreationTime < DateTime.Now.AddHours(-1));
                    
                foreach (var file in oldSegments)
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                LogWithFallback($"Error limpiando segmentos antiguos para stream {streamId}", ex, true);
            }
        }

        public static async Task<StreamHealth> CheckStreamHealth(int streamId)
        {
            var health = new StreamHealth();
            var m3u8Path = Path.Combine(Constantes.Global.STREAMS_FOLDER, $"{streamId}_.m3u8");
            
            try
            {
                if (!File.Exists(m3u8Path))
                {
                    health.IsHealthy = false;
                    return health;
                }

                var directory = new DirectoryInfo(Constantes.Global.STREAMS_FOLDER);
                var segments = directory.GetFiles($"{streamId}_*.ts");
                
                health.SegmentCount = segments.Length;
                health.IsHealthy = segments.Any(s => s.LastWriteTime > DateTime.Now.AddSeconds(-30));
                health.LastSuccessfulCheck = DateTime.Now;
            }
            catch (Exception ex)
            {
                LogWithFallback($"Error checking stream health for {streamId}", ex, true);
                health.IsHealthy = false;
            }
            
            return health;
        }

        private static void LogWithFallback(string message, Exception? ex = null, bool isError = false)
        {
            try
            {
                if (isError)
                {
                    if (ex != null)
                        _logger.Error(message, ex);
                    else
                        _logger.Error(message);
                }
                else
                {
                    _logger.Info(message);
                }
            }
            catch
            {
                // Fallback to direct file writing if logger fails
                try
                {
                    var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {(isError ? "ERROR" : "INFO")} - {message}";
                    if (ex != null)
                        logMessage += $"\nException: {ex.Message}\n{ex.StackTrace}";
                    
                    File.AppendAllText(
                        Path.Combine(LOG_PATH, "streaming_fallback.log"),
                        logMessage + "\n"
                    );
                }
                catch
                {
                    // Last resort: console output
                    var output = isError ? Console.Error : Console.Out;
                    output.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
                    if (ex != null)
                        output.WriteLine($"Exception: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }
}