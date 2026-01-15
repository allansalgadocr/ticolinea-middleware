using MySqlConnector;

namespace ticolinea.stream.service.Data;

public class Errores
{
    public static async Task InsertarErrorLog(Exception exception, string metodo)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "INSERT INTO `log_error` " +
                                  "(`id_log_error`,`message`,`stack_trace`,`fecha_hora`,`metodo`) " +
                                  "VALUES(@id_log_error,@message,@stack_trace,@fecha_hora,@metodo); ";

                string idLog = Guid.NewGuid().ToString();
                cmd.Parameters.AddWithValue("@id_log_error", idLog);
                cmd.Parameters.AddWithValue("@message", exception.Message);
                cmd.Parameters.AddWithValue("@stack_trace", exception.StackTrace);
                cmd.Parameters.AddWithValue("@fecha_hora", DateTime.Now);
                cmd.Parameters.AddWithValue("@metodo", metodo);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}