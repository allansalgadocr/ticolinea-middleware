using System.Text.Json;
using CliWrap.EventStream;
using CliWrap;
using CliWrap.Buffered;
using ticolinea.stream.service.Modelos;
using log4net;

namespace ticolinea.stream.service.Services
{
    public static class StreamingService
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamingService));
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

        public static void IniciarSupervision(StreamDb stream)
        {
            // Si ya está en ejecución, no lanzar otro
            if (_tokens.ContainsKey(stream.StreamId))
            {
                _logger.Warn($"Stream {stream.StreamId} ya está siendo supervisado.");
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
                _logger.Info($"Cancelando stream {streamId}...");
                cts.Cancel();
                _tokens.Remove(streamId);
            }
        }

        private static async Task SupervisarStream(StreamDb stream, CancellationToken cancellationToken)
        {
            const int retryDelaySeconds = 5;

            while (!cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"Iniciando/reiniciando stream {stream.StreamId}...");

                int exitCode = await LanzarProcesoFfmpeg(stream, cancellationToken);

                if (exitCode != 0)
                {
                    _logger.Error($"FFmpeg falló (exitCode {exitCode}) para {stream.StreamId}. Reintentando en {retryDelaySeconds}s...");
                    Data.Streams.InsertaStreamError($"({stream.StreamId}): Proceso reiniciado por error {exitCode}");
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken).ContinueWith(_ => { });
            }

            _logger.Info($"Supervisión detenida para stream {stream.StreamId}");
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
                    .Add("-y")
                    .Add("-hide_banner")
                    .Add("-nostdin")
                    .Add("-nostats")
                    .Add("-loglevel warning", false)
                    .Add("-err_detect ignore_err", false)
                    .Add("-ignore_unknown", false)
                    .Add("-fflags +genpts", false)
                    .Add("-avoid_negative_ts make_zero", false)
                    .Add($"{reconnect}", false)
                    .Add($"{frameRate}", false)
                    .Add("-thread_queue_size 512", false) // 🧠 importante antes de -i
                    .Add($"-i \"{stream.Fuente}\"", false)
                    .Add("-c copy", false)
                    .Add($"-analyzeduration {analyzeDuration}", false)
                    .Add($"-probesize {stream.ProbeSize}", false)
                    .Add($"{pixFmt}", false)
                    .Add($"{transcodeAudio}", false)
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
                            _logger.Info($"Proceso iniciado: PID {started.ProcessId}");
                            await Jobs.ActualizaInfoCanal(started.ProcessId, stream.StreamId);
                            break;
                        case StandardOutputCommandEvent stdOut:
                            _logger.Info($"Out-{stream.StreamId}> {stdOut.Text}");
                            break;
                        case StandardErrorCommandEvent stdErr:
                            _logger.Error($"Err-{stream.StreamId}> {stdErr.Text}");
                            Data.Streams.InsertaStreamError(stdErr.Text);
                            break;
                        case ExitedCommandEvent exited:
                            exitCode = exited.ExitCode;
                            _logger.Info($"FFmpeg terminó para stream {stream.StreamId}; Código: {exitCode}");
                            Data.Streams.InsertaStreamError($"({stream.StreamId}): Finalizó {exitCode}");
                            await Jobs.ActualizarCanalEstado(stream.StreamId, true, -1);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"Proceso cancelado manualmente para stream {stream.StreamId}");
            }

            return exitCode;
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
                _logger.Error("ffprobe failed or timed out", ex);
                return null;
            }
        }
    }
}