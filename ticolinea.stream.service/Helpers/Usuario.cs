using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Helpers
{
    public class Usuario
    {
        public static void ActualizaInfoUsuario(int usuarioId, int chnId, string userAgent, string userIp, int conexionesMaximas)
        {
            List<int> actividades = new();
            using (var mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT actividad_id from actividad_usuario_actualmente WHERE usuario_id=@usuarioId ORDER BY fecha_inicio desc;";

                cmd.Parameters.AddWithValue("@usuarioId", usuarioId);

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        actividades.Add(reader.GetInt32(0));
                    }

                /*if (actividades.Count >= conexionesMaximas)
                {
                    cmd.CommandText = "DELETE FROM actividad_usuario_actualmente WHERE actividad_id=@actividadId;";
                    cmd.Parameters.AddWithValue("@actividadId", actividades.First());

                    cmd.ExecuteNonQuery();
                    cmd.Connection?.Close();
                }*/

                if (actividades.Count == 0)
                {
                    try
                    {
                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                        cmd.CommandText = "INSERT INTO actividad_usuario_actualmente(usuario_id,stream_id,user_agent,usuario_ip,formato,fecha_inicio)" +
                                       "VALUES(@usuario_id,@stream_id,@user_agent,@usuario_ip,'HLS',@fecha_inicio);";

                        cmd.Parameters.AddWithValue("@usuario_id", usuarioId);
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        cmd.Parameters.AddWithValue("@user_agent", userAgent);
                        cmd.Parameters.AddWithValue("@usuario_ip", userIp);
                        cmd.Parameters.AddWithValue("@fecha_inicio", now);

                        cmd.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error al insertar." + ex.Message);
                    }
                }
                else
                {
                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    cmd.CommandText = "UPDATE actividad_usuario_actualmente " +
                                   "SET stream_id=@streamid, usuario_ip=@usuarioip, user_agent=@usuarioagent, fecha_inicio=@fechaInicio"+
                                   " WHERE actividad_id=@actividadId;";

                    cmd.Parameters.AddWithValue("@streamid", chnId);
                    cmd.Parameters.AddWithValue("@usuarioip", userIp);
                    cmd.Parameters.AddWithValue("@usuarioagent", userAgent);
                    cmd.Parameters.AddWithValue("@fechaInicio", now);
                    cmd.Parameters.AddWithValue("@actividadId", actividades.First());

                    cmd.ExecuteNonQuery();
                }    
            }
        }

        public static Modelos.Usuario VerificarUsuario(string usuario, string password)
        {
            //Verifica si usuario existe
            List<Modelos.Usuario> usuarios = new();
            using (var mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT id,conexiones_maximas, habilitado FROM usuarios_ticolinea " +
                                                      "WHERE usuario = @usuario and clave = @clave and habilitado=1;";

                cmd.Parameters.AddWithValue("@usuario", usuario);
                cmd.Parameters.AddWithValue("@clave", password);

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        usuarios.Add(new Modelos.Usuario
                        {
                            UsuarioId = reader.GetInt32(0),
                            ConexionesMaximas = reader.GetInt32(1),
                            Habilitado = reader.GetInt32(2),
                        });
                    }

                mariadb.Conexion.Close();
            }

            return usuarios.FirstOrDefault();
        }
    }
}
