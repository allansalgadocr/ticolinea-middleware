using MySqlConnector;
using System.Data;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data
{
    public static class PaqueteTV
    {
        public static async Task CrearPaqueteAsync(Modelos.PaqueteTV paquete)
        {
            try
            {
                string idPaquete = Guid.NewGuid().ToString();

                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed)
                            await cnn.OpenAsync();

                        cmd.CommandText = "INSERT INTO paquete_tv (id_paquete_tv,nombre_paquete,fecha_creacion,activo) " +
                                          "VALUES (@id_paquete_tv,@nombre_paquete,@fecha_creacion,@activo);";

                        cmd.Parameters.AddWithValue("@id_paquete_tv", idPaquete);
                        cmd.Parameters.AddWithValue("@nombre_paquete", paquete.NombrePaquete);
                        cmd.Parameters.AddWithValue("@fecha_creacion", DateTime.Now);
                        cmd.Parameters.AddWithValue("@activo", paquete.Activo);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    foreach (var stream in paquete.ListaStreams)
                    {
                        using (var cmd = cnn.CreateCommand())
                        {
                            if (cnn.State == System.Data.ConnectionState.Closed)
                                await cnn.OpenAsync();

                            cmd.CommandText = "INSERT INTO paquete_tv_streams (id_paquete_tv,stream_id,tipo) " +
                                              "VALUES (@id_paquete_tv,@stream_id,@tipo);";

                            cmd.Parameters.AddWithValue("@id_paquete_tv", idPaquete);
                            cmd.Parameters.AddWithValue("@stream_id", stream.StreamId);
                            cmd.Parameters.AddWithValue("@tipo", stream.Tipo);

                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public static async Task<Modelos.PaqueteTV> ObtenerPaquete(string IdPaquete)
        {
            Modelos.PaqueteTV paqueteTV = new Modelos.PaqueteTV();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed)
                            await cnn.OpenAsync();

                        string idPaquete = Guid.NewGuid().ToString();

                        cmd.CommandText = "SELECT id_paquete_tv,nombre_paquete,fecha_creacion,activo,peliculas,series FROM paquete_tv " +
                                          "WHERE id_paquete_tv=@IdPaqueteTV;";

                        cmd.Parameters.AddWithValue("@IdPaqueteTV", IdPaquete);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                paqueteTV.IdPaquete = reader.GetString(0);
                                paqueteTV.NombrePaquete = reader.GetString(1);
                                paqueteTV.Activo = reader.GetInt32(2);
                                paqueteTV.Peliculas = reader.GetInt32(3);
                                paqueteTV.Series= reader.GetInt32(4);
                            }
                        }
                    }
                    using (var cmd2 = cnn.CreateCommand())
                    {
                        cmd2.CommandText = "SELECT id_paquete_tv,stream_id,tipo FROM paquete_tv_streams " +
                                           "WHERE id_paquete_tv=@IdPaqueteTV;";

                        cmd2.Parameters.AddWithValue("@IdPaqueteTV", IdPaquete);

                        using (var reader = await cmd2.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                paqueteTV.IdPaquete = reader.GetString(0);
                                paqueteTV.NombrePaquete = reader.GetString(1);
                                paqueteTV.Activo = reader.GetInt32(2);
                            }
                        }
                    }
                }

                return paqueteTV;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public static async Task<Modelos.PaqueteTV> ExistePaquete(string IdPaquete)
        {
            Modelos.PaqueteTV paqueteTV = null;

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed)
                            await cnn.OpenAsync();

                        string idPaquete = Guid.NewGuid().ToString();

                        cmd.CommandText = "SELECT id_paquete_tv,nombre_paquete,fecha_creacion,activo FROM paquete_tv " +
                                          "WHERE id_paquete_tv=@IdPaqueteTV;";

                        cmd.Parameters.AddWithValue("@IdPaqueteTV", IdPaquete);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                paqueteTV = new Modelos.PaqueteTV
                                {
                                    IdPaquete = reader.GetString(0),
                                    NombrePaquete = reader.GetString(1),
                                    Activo = reader.GetInt32(2)
                                };
                            }
                        }
                    }
                }

                return paqueteTV;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public static async Task EliminarPaqueteAsync(string IdPaquete)
        {
            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed)
                            await cnn.OpenAsync();

                        string idPaquete = Guid.NewGuid().ToString();

                        cmd.CommandText = "DELETE FROM paquete_tv " +
                                          "WHERE id_paquete_tv=@id_paquete_tv;";

                        cmd.Parameters.AddWithValue("@id_paquete_tv", IdPaquete);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmd2 = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed)
                            await cnn.OpenAsync();

                        string idPaquete = Guid.NewGuid().ToString();

                        cmd2.CommandText = "DELETE FROM paquete_tv_streams " +
                                          "WHERE id_paquete_tv=@id_paquete_tv;";

                        cmd2.Parameters.AddWithValue("@id_paquete_tv", idPaquete);

                        await cmd2.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }
    }
}
