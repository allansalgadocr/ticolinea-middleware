using MySqlConnector;
using System.Net.Mail;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data
{
    public static class ActividadPorUsuarios
    {
        public static async Task InsertarActividadPorUsuario(MySqlConnection conn, int usuarioId, int chnId, string userAgent, string userIp, string macAddress, int tipo)
        {
            try
            {
                var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                using (var cmd = conn.CreateCommand())
                {
                    if (conn.State == System.Data.ConnectionState.Closed)
                        await conn.OpenAsync();

                    cmd.CommandText = "INSERT INTO actividad_usuario_actualmente(usuario_id,stream_id,user_agent,usuario_ip,formato,fecha_inicio,esMovil,tipo,mac_address)" +
                                      "VALUES(@usuario_id,@stream_id,@user_agent,@usuario_ip,'HLS',@fecha_inicio,@esMovil,@tipo,@macAddress);";

                    cmd.Parameters.AddWithValue("@usuario_id", usuarioId);
                    cmd.Parameters.AddWithValue("@stream_id", chnId);
                    cmd.Parameters.AddWithValue("@user_agent", userAgent);
                    cmd.Parameters.AddWithValue("@usuario_ip", userIp);
                    cmd.Parameters.AddWithValue("@fecha_inicio", now);
                    cmd.Parameters.AddWithValue("@esMovil", 1);
                    cmd.Parameters.AddWithValue("@tipo", tipo);
                    cmd.Parameters.AddWithValue("@macAddress", macAddress);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al insertar actividad de usuario." + ex.Message);
            }
        }

        public static async Task ActualizarActividadPorUsuario(MySqlConnection conn, int chnId, string userAgent, string userIp, int actividadId)
        {
            try
            {
                var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                using (var cmd = conn.CreateCommand())
                {
                    if (conn.State == System.Data.ConnectionState.Closed) await conn.OpenAsync();
                    cmd.CommandText = "UPDATE actividad_usuario_actualmente " +
                               "SET stream_id=@streamid, usuario_ip=@usuarioip, user_agent=@usuarioagent, fecha_inicio=@fechaInicio" +
                               " WHERE actividad_id=@actividadId;";

                    cmd.Parameters.AddWithValue("@streamid", chnId);
                    cmd.Parameters.AddWithValue("@usuarioip", userIp);
                    cmd.Parameters.AddWithValue("@usuarioagent", userAgent);
                    cmd.Parameters.AddWithValue("@fechaInicio", now);
                    cmd.Parameters.AddWithValue("@actividadId", actividadId);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {

                throw;
            }
        }

        public static async Task<int> ObtenerCantidadConexionesActivas(int usuarioId,string macAddress)
        {
            try
            {
                using (var conn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        if (conn.State == System.Data.ConnectionState.Closed) 
                            await conn.OpenAsync();

                        cmd.CommandText = "SELECT COUNT(actividad_id) FROM actividad_usuario_actualmente WHERE usuario_id=@usuarioId and mac_address != @macAddress;";
                        cmd.Parameters.AddWithValue("@usuarioId", usuarioId);
                        cmd.Parameters.AddWithValue("@mac_address", macAddress);

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                return reader.GetInt32(0);
                            }
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al obtener cantidad de conexiones activas." + ex.Message);
                return 0;
            }
        }
    }
}
