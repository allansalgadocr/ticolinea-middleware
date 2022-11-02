using MySqlConnector;

namespace ticolinea.stream.service.Db
{
    public class MariadbRemove : IDisposable
    {
        public MySqlConnection Conexion;

        public MariadbRemove(string connectionString)
        {
            if (Conexion == null)
                Conexion = new MySqlConnection(connectionString);

            this.Conexion.Open();
        }

        public void Dispose()
        {
            this.Conexion.Close();
        }
    }
}
