using MySqlConnector;
using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Helpers
{
    public class ClientValidation
    {
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
                    // Query to get client and provider info by MAC address
                    cmd.CommandText = @"
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

                    cmd.Parameters.AddWithValue("@normalizedMac", normalizedMac);

                    ClientValidationResult? result = null;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var expirationDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
                            
                            // Check expiration date if set
                            if (expirationDate.HasValue && expirationDate.Value < DateTime.UtcNow)
                                return null;

                            result = new ClientValidationResult
                            {
                                ClientId = reader.GetInt32(0),
                                ClientName = reader.GetString(1),
                                ProviderUrl = reader.GetString(4),
                                IsValid = true
                            };
                        }
                    }

                    // Get client packages if result is valid
                    if (result != null)
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

                    return result;
                }
            }

            return null;
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
                    // Query to get client and provider info by credentials
                    cmd.CommandText = @"
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

                    cmd.Parameters.AddWithValue("@username", username);
                    cmd.Parameters.AddWithValue("@password", password);

                    ClientValidationResult? result = null;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var expirationDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
                            
                            // Check expiration date if set
                            if (expirationDate.HasValue && expirationDate.Value < DateTime.UtcNow)
                                return null;

                            result = new ClientValidationResult
                            {
                                ClientId = reader.GetInt32(0),
                                ClientName = reader.GetString(1),
                                ProviderUrl = reader.GetString(4),
                                IsValid = true
                            };
                        }
                    }

                    // Get client packages if result is valid
                    if (result != null)
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

                    return result;
                }
            }

            return null;
        }
    }

    public class ClientValidationResult
    {
        public int ClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string ProviderUrl { get; set; } = string.Empty;
        public string PaqueteTvId { get; set; } = string.Empty;
        public bool IsValid { get; set; }
    }
}

