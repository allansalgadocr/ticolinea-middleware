using System.Diagnostics;
using System.Net;
using System.Text;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service
{
    public class Jobs
    {
        public static void RevisarStreams()
        {
            List<StreamDb> streams = new();
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate, proceso_id, cgop, gop FROM streams_tl a INNER JOIN " +
                                                      "streams_info b " +
                                                      "ON a.id = b.stream_id " +
                                                      "WHERE habilitado = 1 and iniciado = 1 and es_bajodemanda=0 and tipo=1;";

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
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
                cmd.Connection?.Close();
            }

            foreach (StreamDb stream in streams)
            {
                //ObtenerInfoCodec(stream.StreamId, stream.Fuente);
                if (stream.ProcesoId == -1)
                    IniciarStream(stream);
                else
                {
                    //Verifica si existe el proceso
                    bool EstaCorriendoStream = ObtenerProcesoFFMPEG(stream.ProcesoId);
                    if (!EstaCorriendoStream)
                        IniciarStream(stream);
                }
            }
        }

        public static void VerificarCodecsStreams(bool verificaSoloHabilitados = true)
        {
            var extraCommand = verificaSoloHabilitados ? " and habilitado = 1" : "";
            List<StreamDb> streams = new();
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT fuente_stream,id FROM streams_tl " +
                                                      $"WHERE tipo=1 {extraCommand};";

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        streams.Add(new StreamDb
                        {
                            Fuente = reader.GetString(0),
                            StreamId = reader.GetInt32(1),

                        });
                    }
                cmd.Connection?.Close();
            }

            foreach (StreamDb stream in streams)
            {
                ObtenerInfoCodec(stream.StreamId, stream.Fuente);
            }
        }

        public static void DetenerStreamsSinUso()
        {
            List<StreamDb> streams = new();
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT a.id,b.proceso_id,c.actividad_id FROM streams_tl a " +
                                                    "INNER JOIN streams_info b on a.id = b.stream_id " +
                                                    "LEFT JOIN actividad_usuario_actualmente c " +
                                                    "on a.id = c.stream_id " +
                                                    "WHERE es_bajodemanda = 1 AND proceso_id != -1 AND actividad_id is null and tipo=1;";

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        streams.Add(new StreamDb
                        {
                            StreamId = reader.GetInt32(0),
                            ProcesoId = reader.GetInt32(1)
                        });
                    }
                cmd.Connection?.Close();
            }


            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                foreach (StreamDb stream in streams)
                {
                    var proc = ObtenerProcesoEjecutando(stream.ProcesoId);
                    if (proc != null)
                    {
                        proc.Kill();
                    }

                    cmd.CommandText = "UPDATE streams_info SET proceso_id=-1 " +
                                         "WHERE stream_id=@id";
                    cmd.Parameters.AddWithValue("@id", stream.StreamId);
                    cmd.ExecuteNonQuery();
                }

                cmd.Connection?.Close();
            }
        }

        public static bool ObtenerProcesoFFMPEG(int procesoId)
        {
            try
            {
                var process = Process.GetProcessById(procesoId);
                if (process != null)
                {
                    if (process.ProcessName.Contains("ffmpeg"))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Write("ERROR AL OBTENER PROCESO.", ex.Message);
            }

            return false;
        }

        public static Process ObtenerProcesoEjecutando(int procesoId)
        {
            try
            {
                var proceso = Process.GetProcessById(procesoId);
                if (proceso != null)
                {
                    if (proceso.ProcessName.Contains("ffmpeg"))
                    {
                        return proceso;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Write("ERROR AL OBTENER PROCESO.", ex.Message);
            }

            return null;
        }

        public static void ReiniciarStream(StreamDb stream)
        {
            DetenerProceso(stream.ProcesoId);
            //IniciarStream(stream);
        }

        public static void DetenerProceso(int procesoId)
        {
            try
            {
                if (procesoId > -1)
                {
                    var proceso = ObtenerProcesoEjecutando(procesoId);
                    if (proceso != null)
                        proceso.Kill();
                }

            }
            catch (Exception ex) { }
        }

        public static void IniciarStream(StreamDb stream)
        {
            string ubicacionStreams = Constantes.Global.STREAMS_FOLDER;
            Process process = new();
            process.StartInfo.FileName = Constantes.Global.FFMPEG_PATH;

            //if (stream.Transcode == 1)
            //{
            //string transcodeStreamCmd = "-y -nostdin -loglevel quiet -err_detect ignore_err -i \"[INPUT]\" -codec:v libx264 -r [FRAMERATE] -pix_fmt yuv420p -profile:v baseline -level 3 -b:v [BITRATE] -s [RESOLUCION] -codec:a aac -strict experimental -ac 2 -b:a 128k -movflags faststart -flags -global_header -hls_allow_cache 0 -sc_threshold 0 -hls_flags delete_segments -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_segment_filename [UBICACIONSTREAM][STREAMID]_%d.ts [UBICACIONSTREAM][STREAMID]_.m3u8";
            /*string transcodeStreamCmd = "-y -nostdin -loglevel quiet -err_detect ignore_err -i \"[INPUT]\" -pix_fmt yuv420p -vsync 1 -vcodec libx264 -r 23.976 -threads 0 -b:v: 1024k -bufsize 1216k -maxrate 1280k -preset medium -profile:v high -tune film -g 48 -x264opts no-scenecut -pass 1 -acodec aac -b:a 128k -ac 2 -ar 48000 -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_segment_filename [UBICACIONSTREAM][STREAMID]_%d.ts [UBICACIONSTREAM][STREAMID]_.m3u8";
            transcodeStreamCmd = transcodeStreamCmd.Replace("[INPUT]", stream.Fuente)
                                .Replace("[FRAMERATE]", stream.Framerate == 0 ? "29.97" : stream.Framerate.ToString())
                                .Replace("[BITRATE]", string.IsNullOrWhiteSpace(stream.Bitrate) ? "500K" : stream.Bitrate)
                                .Replace("[RESOLUCION]", string.IsNullOrWhiteSpace(stream.Resolucion) ? "1280:720" : stream.Resolucion)
                                .Replace("[INTERVALO]", stream.Intervalo.ToString())
                                .Replace("[UBICACIONSTREAM]", ubicacionStreams)
                                .Replace("[SEGMENTOS]", stream.Segmentos.ToString())
                                .Replace("[STREAMID]", stream.StreamId.ToString());

            process.StartInfo.Arguments = transcodeStreamCmd;
            process.Start();*/
            //}

            string gcop = stream.CGOP == 1 ? $" -flags +cgop -g {stream.GOP} -keyint_min {stream.GOP} -sc_threshold 0 " : "";
            string transcodeAudio = " -acodec copy";
            if (!string.IsNullOrEmpty(stream.TranscodeAudio))
                transcodeAudio = $" -acodec {stream.TranscodeAudio} -threads 2";

            string frameRate = stream.Transcode == 1 ? $" -r {stream.Framerate}" : "";
            string pixFmt = "";
            //string pixFmt = stream.Transcode == 1 ? "-pix_fmt yuv420p" : "";
            //string ffmpegOutput = $"-c copy {pixFmt} -analyzeduration [PROBESIZE] -probesize [PROBESIZE]{transcodeAudio} -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -sc_threshold 0 -hls_segment_filename";
            //string ffmpegOutput = $"-c copy {pixFmt} -map 0 -map -0:s -analyzeduration [PROBESIZE] -probesize [PROBESIZE]{transcodeAudio} -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -hls_segment_filename";
            string ffmpegOutput = $" -c copy {pixFmt} {transcodeAudio} -movflags faststart {gcop} -hls_flags +discont_start+omit_endlist+append_list+delete_segments+temp_file+split_by_time -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 20 -hls_segment_filename";
            //string ffmpegOutput = $" -c copy {pixFmt} -map 0 -map -0:s {transcodeAudio} -movflags faststart -hls_flags +discont_start+omit_endlist+second_level_segment_duration+second_level_segment_index+temp_file -strftime 1 -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -strftime_mkdir 1 -hls_segment_filename";
            //string ffmpegOutput = $"{pixFmt} -vcodec copy {transcodeAudio} -map 0 -map -0:s -movflags faststart -b:v 5M -individual_header_trailer 0 -f segment -segment_format mpegts -segment_time [INTERVALO] -segment_list_size [SEGMENTOS] -segment_format_options mpegts_flags=+initial_discontinuity:mpegts_copyts=1 -segment_list_type m3u8 -segment_list_flags +live -segment_list";
            if (stream.Transcode == 1)
            {
                //frameRate = " -r 25";
                //pixFmt = "-pix_fmt yuv420p -vsync 1 -threads 2";
                //ffmpegOutput = $"-c copy -bufsize 4000k{transcodeAudio} -movflags faststart  -threads 2 -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -hls_segment_filename";
            }

            ffmpegOutput = ffmpegOutput.Replace("[PROBESIZE]", stream.ProbeSize.ToString());
            ffmpegOutput = ffmpegOutput.Replace("[INTERVALO]", stream.Intervalo.ToString());
            ffmpegOutput = ffmpegOutput.Replace("[SEGMENTOS]", stream.Segmentos.ToString());

            process.StartInfo.Arguments = $"-y -nostdin -loglevel quiet -err_detect ignore_err -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 10 {frameRate} -i {stream.Fuente} {ffmpegOutput} {ubicacionStreams}{stream.StreamId}_%d.ts {ubicacionStreams}{stream.StreamId}_.m3u8";
            process.Start();

            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "UPDATE streams_info SET proceso_id=@id_proceso, ejecutando=1 " +
                           "WHERE stream_id=@id";

                cmd.Parameters.AddWithValue("@id_proceso", process.Id);
                cmd.Parameters.AddWithValue("@id", stream.StreamId);

                cmd.ExecuteNonQuery();
                cmd.Connection?.Close();
            }
            //ObtenerInfoCodec(stream.StreamId, stream.Fuente);
        }

        public static void ActualizarCanalEstado(int streamId, bool estaCaido, int procesoId)
        {
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                if (estaCaido)
                {
                    if (procesoId > -1)
                    {
                        try
                        {
                            var proceso = ObtenerProcesoEjecutando(procesoId);
                            if (proceso != null)
                                proceso.Kill();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    }

                    cmd.CommandText = "UPDATE streams_info SET ejecutando=0,proceso_id=-1,reportado_caido=1 " +
                               "WHERE stream_id=@id";
                }
                else
                    cmd.CommandText = "UPDATE streams_info SET ejecutando=1,reportado_caido=0 " +
                              "WHERE stream_id=@id";

                cmd.Parameters.AddWithValue("@id", streamId);
                cmd.ExecuteNonQuery();
                cmd.Connection?.Close();
            }
        }

        public static void VerificarStreamsCaidos()
        {

            try
            {
                StringBuilder sb = new();
                List<StreamDb> streams = new();
                int streamsCaidos = 0;
                sb.AppendLine("[CANT] streams se reportaron como caídos:");
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT fuente_stream, id, nombre_stream,proceso_id FROM streams_tl a " +
                                        "inner join streams_info b " +
                                        "on a.id = b.stream_id " +
                                        "WHERE habilitado = 1 AND iniciado = 1 AND omitir_verificacion = 0 and tipo = 1; ";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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


                foreach (StreamDb stream in streams)
                {
                    try
                    {
                        var args = $"-i {stream.Fuente} -analyzeduration 1000000 -probesize 1000000 -v quiet -print_format json -show_streams -show_format";
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
                            streamsCaidos++;
                            estaCaido = true;
                            sb.AppendLine($"• {stream.StreamId} - {stream.TranscodeAudio}");
                        }

                        ActualizarCanalEstado(stream.StreamId, estaCaido, stream.ProcesoId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERROR AL OBTENER INFO DE CANAL." + ex.Message);
                    }
                }
                sb.Replace("[CANT]", streamsCaidos.ToString());
                sb.AppendLine("Por favor verificar.");

                if (streamsCaidos > 0)
                {
                    string token = "5334506189:AAG-OX79_IGuBFIzgSWF6WecoRt4AH3W4kM";
                    string chatId = "-1001723468137";
                    string message = sb.ToString();
                    string retval = string.Empty;
                    string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={message}";

                    using (var webClient = new WebClient())
                    {
                        retval = webClient.DownloadString(url);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public static void VerificarStream(StreamDb stream)
        {
            try
            {
                if (stream != null)
                {
                    try
                    {
                        var args = $"-i {stream.Fuente} -analyzeduration 1000000 -probesize 1000000 -v quiet -print_format json -show_streams -show_format";
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

                        ActualizarCanalEstado(stream.StreamId, estaCaido, stream.ProcesoId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERROR AL OBTENER INFO DE CANAL." + ex.Message);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public static void ObtenerInfoCodec(int streamId, string fuente)
        {
            try
            {
                var args = $"-i {fuente} -analyzeduration 512000 -probesize 512000 -v quiet -print_format json -show_streams -show_format";
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
                string videodbInfo = $"{videoInfo?.CodecName}|height:{videoInfo?.CodedHeight}|width:{videoInfo?.CodedWidth}|fr={videoInfo?.AvgFrameRate}";
                var audioInfo = probeData.Streams.Where(x => x.CodecType == "audio").Select(s => new
                {
                    s.CodecName
                }).FirstOrDefault();
                string audiodbInfo = $"{audioInfo?.CodecName}";

                probe.WaitForExit();

                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "UPDATE streams_tl SET audio_info=@audio_info, video_info=@video_info " +
                                   "WHERE id=@id";

                    cmd.Parameters.AddWithValue("@audio_info", audiodbInfo);
                    cmd.Parameters.AddWithValue("@video_info", videodbInfo);
                    cmd.Parameters.AddWithValue("@id", streamId);

                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR AL OBTENER INFO CODEC" + ex.Message);
            }
        }


        public static void MataConexionesSinUso()
        {
            try
            {
                var fechaFinMaxima = DateTimeOffset.Now.AddMinutes(-25).ToUnixTimeSeconds();
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "DELETE FROM actividad_usuario_actualmente where fecha_inicio < @fechaFinMaxima;";
                    cmd.Parameters.AddWithValue("@fechaFinMaxima", fechaFinMaxima);
                    cmd.ExecuteNonQuery();
                    cmd.Connection?.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR AL MATAR CONEXIONES.{ex.Message}");
            }
        }

        public static void EliminarArchivosViejos()
        {
            try
            {
                Directory.GetFiles("/home/ticolineaplay/streams")
                        .Select(f => new FileInfo(f))
                        .Where(f => f.CreationTime < DateTime.Now.AddMinutes(-20))
                        .ToList()
                        .ForEach(f => f.Delete());

            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR AL ELIMINAR ARCHIVOS VIEJOS" + ex.Message);
            }
        }
    }
}
