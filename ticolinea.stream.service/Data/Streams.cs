using MySqlConnector;

namespace ticolinea.stream.service.Data
{
    public static class Streams
    {
        public static async Task InsertaStreamError(int streamId,string error)
        {
            if(error.Contains("Last message repeated") || error.Contains("no frame!") || error.Contains("decode_slice_header error") || error.Contains("Increasing reorder buffer") || error.Contains("unref short failure"))
                return;

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "INSERT INTO streams_error(id_streams_error,stream_id,error,fecha_hora) " +
                                      "VALUES(@id_streams_error,@stream_id,@error,@fecha_hora)";

                    string idStreamError = Guid.NewGuid().ToString();
                    cmd.Parameters.AddWithValue("@id_streams_error", idStreamError);
                    cmd.Parameters.AddWithValue("@stream_id", streamId);
                    cmd.Parameters.AddWithValue("@error", error);
                    cmd.Parameters.AddWithValue("@fecha_hora", DateTime.Now);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
