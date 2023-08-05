using MySqlConnector;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Helpers
{
    public class Usuario
    {
        public static async Task ActualizaInfoUsuario(int usuarioId, int chnId, string userAgent, string userIp, int conexionesMaximas)
        {
            List<int> actividades = new List<int>();

            using (var conn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                if (conn.State == System.Data.ConnectionState.Closed)
                    await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT actividad_id FROM actividad_usuario_actualmente WHERE usuario_id=@usuarioId ORDER BY fecha_inicio DESC LIMIT 1;";
                    cmd.Parameters.AddWithValue("@usuarioId", usuarioId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            actividades.Add(reader.GetInt32(0));
                        }
                    }
                }

                var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                if (actividades.Count == 0)
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO actividad_usuario_actualmente(usuario_id, stream_id, user_agent, usuario_ip, formato, fecha_inicio) " +
                                              "VALUES(@usuario_id, @stream_id, @user_agent, @usuario_ip, 'HLS', @fecha_inicio);";

                            cmd.Parameters.AddWithValue("@usuario_id", usuarioId);
                            cmd.Parameters.AddWithValue("@stream_id", chnId);
                            cmd.Parameters.AddWithValue("@user_agent", userAgent);
                            cmd.Parameters.AddWithValue("@usuario_ip", userIp);
                            cmd.Parameters.AddWithValue("@fecha_inicio", now);

                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error al insertar: " + ex.Message);
                        // Consider logging the exception details for better error reporting.
                    }
                }
                else
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE actividad_usuario_actualmente " +
                                          "SET stream_id = @streamid, usuario_ip = @usuarioip, user_agent = @usuarioagent, fecha_inicio = @fechaInicio " +
                                          "WHERE actividad_id = @actividadId;";

                        cmd.Parameters.AddWithValue("@streamid", chnId);
                        cmd.Parameters.AddWithValue("@usuarioip", userIp);
                        cmd.Parameters.AddWithValue("@usuarioagent", userAgent);
                        cmd.Parameters.AddWithValue("@fechaInicio", now);
                        cmd.Parameters.AddWithValue("@actividadId", actividades[0]);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
        }


        public static async Task<Modelos.Usuario> VerificarUsuario(string usuario, string password)
        {
            using (var memoryCacheService = new Services.MemoryCacheService())
            {
                var datos = memoryCacheService.ObtenerDatoEnCache<Modelos.Usuario>($"{usuario}:{password}");
                if (datos != null)
                {
                    return datos;
                }

                var usuarios = new List<Modelos.Usuario>();
                using (var mariadb = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    string sql = "SELECT id, conexiones_maximas, habilitado FROM usuarios_ticolinea " +
                                 "WHERE usuario = @usuario AND clave = @clave AND habilitado = 1 LIMIT 1;";

                    using (var cmd = mariadb.CreateCommand())
                    {
                        if (mariadb.State == System.Data.ConnectionState.Closed)
                            await mariadb.OpenAsync();

                        cmd.CommandText = sql;
                        cmd.Parameters.AddWithValue("@usuario", usuario);
                        cmd.Parameters.AddWithValue("@clave", password);

                        using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            usuarios.Add(new Modelos.Usuario
                            {
                                UsuarioId = reader.GetInt32(0),
                                ConexionesMaximas = reader.GetInt32(1),
                                Habilitado = reader.GetInt32(2),
                            });
                        }
                    }
                }

                var usuarioObj = usuarios.FirstOrDefault();
                if (usuarioObj != null)
                    memoryCacheService.GuardarEnCache($"{usuario}:{password}", usuarioObj, 10);

                return usuarioObj;
            }
        }

    }
}
