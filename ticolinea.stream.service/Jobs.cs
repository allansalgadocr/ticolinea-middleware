using CliWrap;
using CliWrap.Buffered;
using MySqlConnector;
using System.Diagnostics;
using System.Net;
using System.Text;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;

namespace ticolinea.stream.service
{
    public class Jobs
    {
        public static async Task RevisarStreams()
        {
            List<StreamDb> streams = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate, proceso_id, cgop, gop FROM streams_tl a INNER JOIN " +
                                                          "streams_info b " +
                                                          "ON a.id = b.stream_id " +
                                                          "WHERE habilitado = 1 and iniciado = 1 and es_bajodemanda=0 and tipo=1;";

                    using (var reader = await cmd.ExecuteReaderAsync())
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
                }
            }

            foreach (StreamDb stream in streams)
            {
                //ObtenerInfoCodec(stream.StreamId, stream.Fuente);
                if (stream.ProcesoId == -1)
                    await IniciarStream(stream);
                else
                {
                    //Verifica si existe el proceso
                    bool EstaCorriendoStream = await ObtenerProcesoFFMPEG(stream.ProcesoId, stream.StreamId);
                    if (!EstaCorriendoStream)
                        await IniciarStream(stream);
                }
            }
        }

        public static async Task VerificarCodecsStreams(bool verificaSoloHabilitados = true)
        {
            var extraCommand = verificaSoloHabilitados ? " and habilitado = 1" : "";
            List<StreamDb> streams = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT fuente_stream,id FROM streams_tl " +
                                                          $"WHERE tipo=1 {extraCommand};";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            streams.Add(new StreamDb
                            {
                                Fuente = reader.GetString(0),
                                StreamId = reader.GetInt32(1),

                            });
                        }
                }
            }

            foreach (StreamDb stream in streams)
            {
                await ObtenerInfoCodec(stream.StreamId, stream.Fuente);
            }
        }

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
                                                        "WHERE es_bajodemanda = 1 AND proceso_id != -1 AND actividad_id is null and tipo=1;";

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
                 .WithArguments($"-f \"/{streamId}_.m3u\"")
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
                Console.Write("ERROR AL OBTENER PROCESO.", ex.Message);
            }

            return false;
        }

        public static async Task<Process> ObtenerProcesoEjecutando(int procesoId, int streamId)
        {
            try
            {

                var result = await Cli
                 .Wrap("/bin/pgrep")
                 .WithArguments($"-f \"/{streamId}_.m3u\"")
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
                                return cmdProc;
                            }
                        }
                    }
                }

                /*var proceso = Process.GetProcessById(procesoId);
                if (proceso != null)
                {
                    if (proceso.ProcessName.Contains("ffmpeg"))
                    {
                        return proceso;
                    }
                }*/
            }
            catch (Exception ex)
            {
                Console.Write("ERROR AL OBTENER PROCESO.", ex.Message);
            }

            return null;
        }

        public static async Task ReiniciarStream(StreamDb stream)
        {
            await DetenerProceso(stream.ProcesoId, stream.StreamId);
            //IniciarStream(stream);
        }

        public static async Task DetenerProceso(int procesoId, int streamId)
        {
            try
            {
                var result = await Cli
                  .Wrap("/bin/pgrep")
                  .WithArguments($"-f \"/{streamId}_.m3u\"")
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
                            cmdProc.Kill(true);
                        }
                    }
                }

            }
            catch (Exception ex) { }
        }

        public static async Task<string> RunCommandAsync(int streamId)
        {
            var result = await Cli
                  .Wrap("/bin/pgrep")
                  .WithArguments($"-f \"/{streamId}_.m3u\"")
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
            var proc = await ObtenerProcesoEjecutando(0, stream.StreamId);
            if (proc != null)
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "UPDATE streams_info SET proceso_id=@id_proceso, ejecutando=1 " +
                                   "WHERE stream_id=@id";

                        cmd.Parameters.AddWithValue("@id_proceso", proc.Id);
                        cmd.Parameters.AddWithValue("@id", stream.StreamId);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                return;
            }

            _ = StreamingService.IniciarStream(stream).ConfigureAwait(false);
        }

        public static async Task ActualizaInfoCanal(int procesoId, int streamId)
        {
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "UPDATE streams_info SET proceso_id=@id_proceso, ejecutando=1,reportado_caido=0 " +
                                      "WHERE stream_id=@id";

                    cmd.Parameters.AddWithValue("@id_proceso", procesoId);
                    cmd.Parameters.AddWithValue("@id", streamId);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task ActualizarCanalEstado(int streamId, bool estaCaido, int procesoId)
        {
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    if (estaCaido)
                    {
                        cmd.CommandText = "UPDATE streams_info SET reportado_caido=1 " +
                                   "WHERE stream_id=@id";
                    }
                    else
                        cmd.CommandText = "UPDATE streams_info SET reportado_caido=0 " +
                                  "WHERE stream_id=@id";

                    cmd.Parameters.AddWithValue("@id", streamId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }


        public static async Task VerificarStreamsCaidos()
        {

            try
            {
                StringBuilder sb = new();
                List<StreamDb> streams = new();
                int streamsCaidos = 0;
                sb.AppendLine("[CANT] streams se reportaron como caídos:");
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


                foreach (StreamDb stream in streams)
                {
                    try
                    {
                        var args = $"-i http://localhost:27701/Live/Streaming/{stream.StreamId}/test/test.m3u8 -analyzeduration 1000000 -probesize 1000000 -v quiet -print_format json -show_streams -show_format";
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
                        if (probeData?.Streams == null)
                        {
                            streamsCaidos++;
                            estaCaido = true;
                            sb.AppendLine($"• {stream.StreamId} - {stream.TranscodeAudio}");
                        }

                        await ActualizarCanalEstado(stream.StreamId, estaCaido, stream.ProcesoId);
                        await DetenerProceso(stream.ProcesoId, stream.StreamId);
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

        public static async Task VerificarStream(StreamDb stream)
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

                        await ActualizarCanalEstado(stream.StreamId, estaCaido, stream.ProcesoId);
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

        public static async Task ObtenerInfoCodec(int streamId, string fuente)
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
                Console.WriteLine("ERROR AL OBTENER INFO CODEC" + ex.Message);
            }
        }


        public static async Task MataConexionesSinUso()
        {
            try
            {
                var fechaFinMaxima = DateTimeOffset.Now.AddMinutes(-25).ToUnixTimeSeconds();
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM actividad_usuario_actualmente where fecha_inicio < @fechaFinMaxima;";
                        cmd.Parameters.AddWithValue("@fechaFinMaxima", fechaFinMaxima);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR AL MATAR CONEXIONES.{ex.Message}");
            }
        }

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

        public static async Task EliminarArchivosGrandes()
        {
            try
            {
                var files = Directory.GetFiles("/home/ticolineaplay/streams")
                        .Select(f => new FileInfo(f))
                        .Where(f => f.Length > 15000000)
                        .ToList();

                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    foreach (var file in files)
                    {
                        StreamDb stream = new StreamDb();
                        var nombreArchivo = file.Name.Replace("/home/ticolineaplay/streams", "");
                        var split = nombreArchivo.Split("_");
                        var chnId = split[0];
                        file.Delete();
                        if (!string.IsNullOrEmpty(chnId))
                        {
                            using (var cmd = cnn.CreateCommand())
                            {
                                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                                cmd.CommandText = "SELECT a.id,b.proceso_id,c.actividad_id FROM streams_tl a " +
                                                                    "INNER JOIN streams_info b on a.id = b.stream_id " +
                                                                    "LEFT JOIN actividad_usuario_actualmente c " +
                                                                    "on a.id = c.stream_id " +
                                                                    $"WHERE stream_id = {chnId} AND proceso_id != -1 and tipo=1;";

                                using (var reader = await cmd.ExecuteReaderAsync())
                                    while (await reader.ReadAsync())
                                    {
                                        stream = new StreamDb
                                        {
                                            StreamId = reader.GetInt32(0),
                                            ProcesoId = reader.GetInt32(1)
                                        };
                                    }
                            }

                            if (stream.ProcesoId > 0)
                            {
                                await DetenerProceso(stream.ProcesoId, stream.StreamId);
                            }
                        }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR AL ELIMINAR ARCHIVOS VIEJOS" + ex.Message);
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
                Console.WriteLine(ex.Message);
            }
        }

        private static void Watcher_Error(object sender, ErrorEventArgs e)
        {
            Console.WriteLine(e.GetException());
        }

        private static void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            Console.WriteLine($"Renamed:");
            Console.WriteLine($"    Old: {e.OldFullPath}");
            Console.WriteLine($"    New: {e.FullPath}");
        }

        private static void Watcher_Deleted(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"Deleted: {e.FullPath}");
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
            Console.WriteLine($"Changed: {e.FullPath}");
        }
    }
}
