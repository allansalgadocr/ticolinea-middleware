using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Helpers
{
    public class Usuario
    {
        public static void ActualizaInfoUsuario(int usuarioId, int chnId, string userAgent, string userIp, int conexionesMaximas)
        {
            List<int> actividades = new();
            using(var mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT actividad_id from actividad_usuario_actualmente WHERE usuario_id=@usuarioId ORDER BY fecha_inicio desc;";

                cmd.Parameters.AddWithValue("@usuarioId", usuarioId);

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        actividades.Add(reader.GetInt32(0));
                    }

                if (actividades.Count >= conexionesMaximas)
                {
                    //Elimina ultima conexion
                    var cmdUpdate = mariadb.Conexion.CreateCommand();
                    cmdUpdate.CommandText = "DELETE FROM actividad_usuario_actualmente WHERE actividad_id=@actividadId;";
                    cmdUpdate.Parameters.AddWithValue("@actividadId", actividades.First());

                    cmdUpdate.ExecuteNonQuery();
                    cmdUpdate.Connection?.Close();
                }

                try
                {
                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    var cmdUpdate = mariadb.Conexion.CreateCommand();
                    cmdUpdate.CommandText = "INSERT INTO actividad_usuario_actualmente(usuario_id,stream_id,user_agent,usuario_ip,formato,fecha_inicio)" +
                                   "VALUES(@usuario_id,@stream_id,@user_agent,@usuario_ip,'HLS',@fecha_inicio);";

                    cmdUpdate.Parameters.AddWithValue("@usuario_id", usuarioId);
                    cmdUpdate.Parameters.AddWithValue("@stream_id", chnId);
                    cmdUpdate.Parameters.AddWithValue("@user_agent", userAgent);
                    cmdUpdate.Parameters.AddWithValue("@usuario_ip", userIp);
                    cmdUpdate.Parameters.AddWithValue("@fecha_inicio", now);

                    cmdUpdate.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error al insertar." + ex.Message);
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
                                                      "WHERE usuario = @usuario and clave = @clave;";

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
            }
               
            return usuarios.FirstOrDefault();
        }
    }
}
