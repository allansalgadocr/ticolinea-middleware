using MySqlConnector;

namespace ticolinea.stream.service.Data
{
    public static class Streams
    {
        public static async Task InsertaStreamError(int streamId,string error)
        {
            if(error.Contains("Last message repeated") || 
                error.Contains("no frame!") || 
                error.Contains("decode_slice_header error") || 
                error.Contains("Increasing reorder buffer") || 
                error.Contains("unref short failure") ||
                error.Contains("non-existing PPS 0 referenced") ||
                error.Contains("Multiple -c"))
                return;

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                await cnn.OpenAsync();
                using (var cmd = cnn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO streams_error(id_streams_error,stream_id,error,fecha_hora) " +
                                      "VALUES(@id_streams_error,@stream_id,@error,@fecha_hora)";

                    string idStreamError = Guid.NewGuid().ToString();
                    cmd.Parameters.AddWithValue("@id_streams_error", idStreamError);
                    cmd.Parameters.AddWithValue("@stream_id", streamId);
                    cmd.Parameters.AddWithValue("@error", error);
                    cmd.Parameters.AddWithValue("@fecha_hora", DateTime.Now);

                    await cmd.ExecuteNonQueryAsync();
                } // The connection will be returned to the pool when this using block is exited
            }
        }
    }
}
