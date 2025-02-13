using CliWrap.EventStream;
using CliWrap;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Services
{
    public static class StreamingService
    {
        public static async Task IniciarStream(StreamDb stream)
        {
            string transcodeAudio = " -acodec copy -threads 1";
            if (!string.IsNullOrEmpty(stream.TranscodeAudio))
                transcodeAudio = $" -acodec {stream.TranscodeAudio} -threads 1";

            string frameRate = stream.Transcode == 2 ? $" -r {stream.Framerate}" : "";
            string pixFmt = stream.Transcode == 1 ? "-pix_fmt yuv420p -async 1" : "";
            string reconnect = "";

            int analyzeDuration = stream.ProbeSize;
            var parameters = stream.Bitrate.Split("+", StringSplitOptions.RemoveEmptyEntries);
            if (parameters.Contains("reconnect"))
                reconnect = "-reconnect 1 ";
            if (parameters.Contains("reconnect_streamed"))
                reconnect = "-reconnect_streamed 1 ";
            if (parameters.Contains("reconnect_at_eof"))
                reconnect = reconnect + "-reconnect_at_eof 1 ";
            if (parameters.Contains("reconnect_on_http_error"))
                reconnect = reconnect + "-reconnect_on_http_error 1 ";
            if (parameters.Contains("reconnect_delay_max"))
                reconnect = reconnect + "-reconnect_delay_max 10 ";
            if (parameters.Contains("analyzeduration"))
                analyzeDuration=stream.GOP;

            string processFilePath = stream.Fuente.StartsWith("srt://") ? Constantes.Global.FFMPEG_PATH_SRT : Constantes.Global.FFMPEG_PATH;
            var cmd = Cli.Wrap(processFilePath).WithValidation(CommandResultValidation.None)
               .WithArguments(a => a
                   .Add("-y")
                   .Add("-hide_banner")
                   .Add("-nostdin")
                   .Add("-nostats")
                   .Add("-loglevel warning", false)
                   .Add("-err_detect ignore_err", false)
                   .Add($"{reconnect}", false)
                   .Add($"{frameRate}", false)
                   .Add($"-i \"{stream.Fuente}\"", false)
                   .Add("-c copy", false)
                   .Add($"-analyzeduration {stream.ProbeSize * 2}", false)
                   .Add($"-probesize {stream.ProbeSize}", false)
                   .Add($"{pixFmt}", false)
                   .Add($"{transcodeAudio}", false)
                   .Add("-movflags faststart", false)
                   .Add("-hls_flags +discont_start+omit_endlist+append_list+delete_segments+temp_file+split_by_time", false)
                   .Add($"-hls_time {stream.Intervalo}", false)
                   .Add($"-hls_list_size {stream.Segmentos}", false)
                   .Add("-hls_delete_threshold 15", false)
                   .Add($"-hls_segment_filename {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_%d.ts {Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_.m3u8", false)

               );

            await foreach (var cmdEvent in cmd.ListenAsync())
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent started:
                        Console.WriteLine($"Process started; ID: {started.ProcessId}");
                        await Jobs.ActualizaInfoCanal(started.ProcessId, stream.StreamId);
                        break;
                    case StandardOutputCommandEvent stdOut:
                        Console.WriteLine($"Out-{stream.StreamId}> {stdOut.Text}");
                        break;
                    case StandardErrorCommandEvent stdErr:
                        Data.Streams.InsertaStreamError(stdErr.Text);
                        Console.WriteLine($"Err-{stream.StreamId}> {stdErr.Text}");
                        break;
                    case ExitedCommandEvent exited:
                        Console.WriteLine($"Process exited; Code: {exited.ExitCode}");
                        Data.Streams.InsertaStreamError($"({stream.StreamId}): Finalizó {exited.ExitCode}");
                        await Jobs.ActualizarCanalEstado(stream.StreamId, true, -1);
                        break;
                }
            }
        }
    }
}
