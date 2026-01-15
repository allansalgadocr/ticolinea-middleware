using MySqlConnector;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Adapters;

public class Streams
{
    public static async Task ActualizarEstadoStream(int procesoId, int streamId)
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

        public static async Task ReportaFallaEnStream(int streamId, bool estaCaido)
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

        public static async Task ManejaErroresCanal(int streamId, string mensajeError)
        {
            if (mensajeError.Contains("expired from playlists") || mensajeError.Contains("Cannot reuse HTTP connection for different host") ||
               mensajeError.Contains("HTTP error 504 Gateway Time-out") || mensajeError.Contains("Stream ends prematurely"))
            {
                await Jobs.DetenerProceso(0,streamId);
            }

            return;
        }

        public static async Task<bool> ExisteInformacionStream(int chnId)
        {
            try
            {
                StreamDb stream = new();

                using (var conn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        if (conn.State == System.Data.ConnectionState.Closed) 
                            await conn.OpenAsync();

                        cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate, cgop, gop FROM streams a " +
                                        "INNER JOIN streams_info b " +
                                        "on a.id = b.stream_id " +
                                        $"WHERE iniciado = 1 AND stream_id = {chnId} and Habilitado=1;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                stream = new StreamDb
                                {
                                    Fuente = reader.GetString(0),
                                    StreamId = reader.GetInt32(1),
                                    ProbeSize = reader.GetInt32(2),
                                    EsBajoDemanda = reader.GetInt32(3),
                                    ProcesoId = reader.GetInt32(4),
                                    TranscodeAudio = reader.GetString(5),
                                    Intervalo = reader.GetInt16(6),
                                    Segmentos = reader.GetInt16(7),
                                    Framerate = reader.GetInt32(8),
                                    Transcode = reader.GetInt32(9),
                                    Resolucion = reader.GetString(10),
                                    Bitrate = reader.GetString(11),
                                    CGOP = reader.GetInt32(12),
                                    GOP = reader.GetInt32(13),
                                };
                            }
                        }
                    }
                }


                if (stream == null)
                {
                    Console.WriteLine($"Canal {chnId} no encontrado en BD.");
                    return false;
                };


                if (stream.ProcesoId > -1)
                {
                    bool EstaCorriendoStream = await Jobs.ObtenerProcesoFFMPEG(stream.ProcesoId, stream.StreamId);
                    if ((!EstaCorriendoStream && stream.EsBajoDemanda == 1) || (!EstaCorriendoStream && stream.EsBajoDemanda == 0))
                    {
                        Console.WriteLine($"Canal {chnId} sin proceso, iniciando stream");
                        //Inicia stream
                        await Jobs.IniciarStream(stream);
                        await Task.Delay(400);
                        bool archivoExiste = false;
                        int ciclo = 0;
                        while (archivoExiste == false && ciclo < 35)
                        {
                            archivoExiste = System.IO.File.Exists($"{Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_.m3u8");
                            ciclo++;
                            await Task.Delay(400);

                            return false;
                        }

                        return true;
                    }

                    else 
                        return true;
                }
                else
                {
                    Console.WriteLine($"Canal {chnId} sin proceso, iniciando stream...");

                    //Inicia stream
                    await Jobs.IniciarStream(stream);
                    await Task.Delay(200);
                    bool archivoExiste = false;
                    int ciclo = 0;
                    while (archivoExiste == false && ciclo < 35)
                    {
                        archivoExiste = System.IO.File.Exists($"{Constantes.Global.STREAMS_FOLDER}{stream.StreamId}_.m3u8");
                        ciclo++;
                        await Task.Delay(400);
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ObtieneDatosCanal] ERROR AL OBTENER DATA STREAM {chnId}.{ex.Message}.{ex.StackTrace}");
            }

            return false;
        }
}