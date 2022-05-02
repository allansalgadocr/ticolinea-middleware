using MySqlConnector;

namespace ticolinea.stream.service.Db
{
    public class Mariadb : IDisposable
    {
        public MySqlConnection Conexion;

        public Mariadb(string connectionString)
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
