using MySqlConnector;
using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Helpers
{
    public static class ConnectionPoolMonitor
    {
        public static async Task<ConnectionPoolStats> GetPoolStatsAsync()
        {
            try
            {
                using var connection = new MySqlConnection(Global.MARIADB_CONN);
                await connection.OpenAsync();

                var stats = new ConnectionPoolStats
                {
                    Timestamp = DateTime.UtcNow,
                    IsConnected = connection.State == System.Data.ConnectionState.Open,
                    ServerVersion = connection.ServerVersion,
                    Database = connection.Database,
                    ConnectionTimeout = connection.ConnectionTimeout
                };

                // Get pool statistics if available
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SHOW STATUS LIKE 'Threads_connected'";
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        stats.ActiveConnections = Convert.ToInt32(reader.GetString(1));
                    }
                }
                catch
                {
                    // Pool stats not available, continue
                }

                return stats;
            }
            catch (Exception ex)
            {
                return new ConnectionPoolStats
                {
                    Timestamp = DateTime.UtcNow,
                    IsConnected = false,
                    Error = ex.Message
                };
            }
        }

        public static async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var connection = new MySqlConnection(Global.MARIADB_CONN);
                await connection.OpenAsync();
                
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<string> GetConnectionInfoAsync()
        {
            try
            {
                using var connection = new MySqlConnection(Global.MARIADB_CONN);
                await connection.OpenAsync();

                var info = new List<string>
                {
                    $"Server: {connection.DataSource}",
                    $"Database: {connection.Database}",
                    $"Server Version: {connection.ServerVersion}",
                    $"Connection Timeout: {connection.ConnectionTimeout}s",
                    $"State: {connection.State}"
                };

                return string.Join(" | ", info);
            }
            catch (Exception ex)
            {
                return $"Connection Error: {ex.Message}";
            }
        }
    }

    public class ConnectionPoolStats
    {
        public DateTime Timestamp { get; set; }
        public bool IsConnected { get; set; }
        public string? ServerVersion { get; set; }
        public string? Database { get; set; }
        public int ConnectionTimeout { get; set; }
        public int ActiveConnections { get; set; }
        public string? Error { get; set; }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Error))
            {
                return $"❌ Connection Error: {Error}";
            }

            return $"✅ Connected to {Database} | Server: {ServerVersion} | Active: {ActiveConnections} | Timeout: {ConnectionTimeout}s";
        }
    }
}
