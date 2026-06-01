using MySqlConnector;
using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Helpers
{
    public class ClientValidation
    {
        // SELECT including the provider priority columns (is_external, default_paquete_tv_id).
        // Used first; falls back to the legacy SELECT when those columns are absent
        // (e.g. on external-node databases that have not been migrated).
        private const string SelectByMacWithProviderCols = @"
            SELECT
                c.id,
                c.name,
                c.is_active,
                c.expiration_date,
                p.connection_url,
                p.is_external,
                p.default_paquete_tv_id
            FROM client_mac_addresses cma
            INNER JOIN clients c ON cma.client_id = c.id
            INNER JOIN providers p ON c.provider_id = p.id
            WHERE REPLACE(REPLACE(UPPER(cma.mac_address), ':', ''), '-', '') = @normalizedMac
            AND c.is_active = 1
            LIMIT 1;";

        private const string SelectByMacLegacy = @"
            SELECT
                c.id,
                c.name,
                c.is_active,
                c.expiration_date,
                p.connection_url
            FROM client_mac_addresses cma
            INNER JOIN clients c ON cma.client_id = c.id
            INNER JOIN providers p ON c.provider_id = p.id
            WHERE REPLACE(REPLACE(UPPER(cma.mac_address), ':', ''), '-', '') = @normalizedMac
            AND c.is_active = 1
            LIMIT 1;";

        private const string SelectByCredentialsWithProviderCols = @"
            SELECT
                c.id,
                c.name,
                c.is_active,
                c.expiration_date,
                p.connection_url,
                p.is_external,
                p.default_paquete_tv_id
            FROM client_credentials cc
            INNER JOIN clients c ON cc.client_id = c.id
            INNER JOIN providers p ON c.provider_id = p.id
            WHERE cc.username = @username
            AND cc.password = @password
            AND cc.is_active = 1
            AND c.is_active = 1
            LIMIT 1;";

        private const string SelectByCredentialsLegacy = @"
            SELECT
                c.id,
                c.name,
                c.is_active,
                c.expiration_date,
                p.connection_url
            FROM client_credentials cc
            INNER JOIN clients c ON cc.client_id = c.id
            INNER JOIN providers p ON c.provider_id = p.id
            WHERE cc.username = @username
            AND cc.password = @password
            AND cc.is_active = 1
            AND c.is_active = 1
            LIMIT 1;";

        /// <summary>
        /// Validates MAC address and returns client/provider information
        /// </summary>
        public static async Task<ClientValidationResult?> ValidateMacAddress(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress))
                return null;

            // Normalize MAC address (remove colons/dashes, uppercase)
            var normalizedMac = macAddress.Replace(":", "").Replace("-", "").ToUpperInvariant();

            using (var conn = new MySqlConnection(Global.MARIADB_CONN))
            {
                if (conn.State == System.Data.ConnectionState.Closed)
                    await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Parameters.AddWithValue("@normalizedMac", normalizedMac);

                    var result = await ExecuteClientQueryAsync(
                        cmd, SelectByMacWithProviderCols, SelectByMacLegacy);

                    if (result != null)
                        await PopulateClientPackagesAsync(conn, result);

                    return result;
                }
            }
        }

        /// <summary>
        /// Validates credentials and returns client/provider information
        /// </summary>
        public static async Task<ClientValidationResult?> ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            using (var conn = new MySqlConnection(Global.MARIADB_CONN))
            {
                if (conn.State == System.Data.ConnectionState.Closed)
                    await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Parameters.AddWithValue("@username", username);
                    cmd.Parameters.AddWithValue("@password", password);

                    var result = await ExecuteClientQueryAsync(
                        cmd, SelectByCredentialsWithProviderCols, SelectByCredentialsLegacy);

                    if (result != null)
                        await PopulateClientPackagesAsync(conn, result);

                    return result;
                }
            }
        }

        /// <summary>
        /// Runs the client lookup, preferring the query that selects the provider
        /// priority columns. If those columns don't exist on this node's database
        /// (MySqlException), it falls back to the legacy query and leaves
        /// IsExternal/ProviderPackageId at their defaults (degrades to "all streams").
        /// </summary>
        private static async Task<ClientValidationResult?> ExecuteClientQueryAsync(
            MySqlCommand cmd, string sqlWithProviderCols, string sqlLegacy)
        {
            ClientValidationResult? result = null;
            var hasProviderCols = true;

            cmd.CommandText = sqlWithProviderCols;
            try
            {
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                        result = BuildResult(reader, hasProviderCols: true);
                }
            }
            catch (MySqlException)
            {
                // Columns is_external / default_paquete_tv_id not present on this DB.
                hasProviderCols = false;
            }

            if (result == null && !hasProviderCols)
            {
                cmd.CommandText = sqlLegacy;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                        result = BuildResult(reader, hasProviderCols: false);
                }
            }

            return result;
        }

        private static ClientValidationResult? BuildResult(MySqlDataReader reader, bool hasProviderCols)
        {
            var expirationDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);

            // Check expiration date if set
            if (expirationDate.HasValue && expirationDate.Value < DateTime.UtcNow)
                return null;

            return new ClientValidationResult
            {
                ClientId = reader.GetInt32(0),
                ClientName = reader.GetString(1),
                ProviderUrl = reader.GetString(4),
                IsExternal = hasProviderCols && !reader.IsDBNull(5) && reader.GetBoolean(5),
                ProviderPackageId = hasProviderCols && !reader.IsDBNull(6) ? reader.GetString(6) : string.Empty,
                IsValid = true
            };
        }

        private static async Task PopulateClientPackagesAsync(MySqlConnection conn, ClientValidationResult result)
        {
            using (var cmdPackages = conn.CreateCommand())
            {
                cmdPackages.CommandText = @"
                    SELECT DISTINCT cp.id_paquete_tv
                    FROM client_package cp
                    INNER JOIN paquete_tv pt ON cp.id_paquete_tv = pt.id_paquete_tv
                    WHERE cp.client_id = @clientId
                    AND pt.activo = 1;";

                cmdPackages.Parameters.AddWithValue("@clientId", result.ClientId);

                var packageIds = new List<string>();
                using (var reader = await cmdPackages.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        packageIds.Add(reader.GetString(0));
                    }
                }

                // Use first package ID if available, or empty string for all channels
                result.PaqueteTvId = packageIds.FirstOrDefault() ?? "";
            }
        }
    }

    public class ClientValidationResult
    {
        public int ClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string ProviderUrl { get; set; } = string.Empty;
        public string PaqueteTvId { get; set; } = string.Empty;

        /// <summary>
        /// True if the provider is external — always serve all streams, never filter by package.
        /// </summary>
        public bool IsExternal { get; set; } = false;

        /// <summary>
        /// Provider's default package, used when the client has no package of their own.
        /// </summary>
        public string ProviderPackageId { get; set; } = string.Empty;

        public bool IsValid { get; set; }
    }
}
