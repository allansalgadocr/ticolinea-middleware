using Hangfire.Server;
using MySqlConnector;
using System;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data
{
    public static class Streams
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);

        public static void InsertaStreamError(string error)
        {
            log.Error($"[StreamError] {error}");
        }

        public static async Task<List<Bouquet>> ObtenerCanalesSinOrdenAsync(string? idPaqueteId="")
        {
            List<Bouquet> bouquet = new List<Bouquet>();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed)
                        await cnn.OpenAsync();

                    string sql = @"SELECT a.id, a.nombre_stream, a.imagen_stream, b.category_name, a.tipo, a.contenedor, a.canal_epg
                                        FROM streams_tl a
                                        INNER JOIN stream_categories b ON a.id_categoria = b.id
                                        WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id = 0
                                        ORDER BY a.orden ASC; ";

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        sql = @"SELECT a.id, a.nombre_stream, a.imagen_stream, b.category_name, a.tipo, a.contenedor, a.canal_epg,pp.activo
                                    FROM streams_tl a
                                    INNER JOIN stream_categories b ON a.id_categoria = b.id
                                    INNER JOIN paquete_tv_streams p ON a.id = p.stream_id
                                    INNER JOIN paquete_tv pp ON p.id_paquete_tv = pp.id_paquete_tv
                                    WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id = 0
                                    AND p.id_paquete_tv = @IdPaqueteTV and pp.activo = 1
                                    ORDER BY a.orden ASC; ";
                    }


                    cmd.CommandText = sql;

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        cmd.Parameters.AddWithValue("@IdPaqueteTV", idPaqueteId);
                    }

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                            });
                        }
                }
            }

            return bouquet;
        }

        public static async Task<List<Bouquet>> ObtenerCanalesConOrdenAsync(string? idPaqueteId="")
        {
            List<Modelos.Bouquet> bouquetCustom = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed)
                        await cnn.OpenAsync();

                    string sql = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg, canal_id FROM streams_tl a " +
                                                        "INNER JOIN stream_categories b " +
                                                        "on a.id_categoria = b.id " +
                                                        "WHERE habilitado=1 and tipo=1 and canal_id != 0 " +
                                                        "order by a.canal_id asc;";

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        sql = @"SELECT a.id,a.nombre_stream,a.imagen_stream,b.category_name,a.tipo,a.contenedor, a.canal_epg, a.canal_id
                                    FROM streams_tl a
                                    INNER JOIN stream_categories b ON a.id_categoria = b.id
                                    INNER JOIN paquete_tv_streams p ON a.id = p.stream_id
                                    INNER JOIN paquete_tv pp ON p.id_paquete_tv = pp.id_paquete_tv
                                    WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id != 0
                                    AND p.id_paquete_tv = @IdPaqueteTV and pp.activo = 1
                                    order by a.canal_id asc; ";
                    }


                    cmd.CommandText = sql;

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        cmd.Parameters.AddWithValue("@IdPaqueteTV", idPaqueteId);
                    }

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquetCustom.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                                CanalId = reader.GetInt32(7)
                            });
                        }

                }
            }

            return bouquetCustom;
        }
    }
}
