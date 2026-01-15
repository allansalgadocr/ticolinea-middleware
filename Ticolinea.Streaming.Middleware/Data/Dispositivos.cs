using MySqlConnector;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data;

public static class Dispositivos
    {
        private static readonly object activityLock = new object();
        public static List<ActividadUsuario> ActivityBatch = new List<ActividadUsuario>();

        public enum Estado
        {
            ACTIVO,
            INACTIVO
        }

        public static async Task<Dispositivo> ObtenerDispositivoAsync(string macAddress)
        {
            Dispositivo dispositivo = null;
            macAddress = macAddress.ToLower().Trim();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "SELECT MacAddress,pin,activo,fecha_creacion,fecha_activacion,creado_por,notas,lista,activado_por,numero_contrato,nombre_contrato " +
                                      "from dispositivos where MacAddress=@macAddress;";

                    cmd.Parameters.AddWithValue("@macAddress", macAddress);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (reader.Read())
                        {
                            dispositivo = new Dispositivo
                            {
                                MacAddress = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                Pin = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Activo = reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2),
                                FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                                FechaActivacion = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                                CreadoPor = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                Notas = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                Lista = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                ActivadoPor = reader.IsDBNull(8) ? "" : reader.GetString(8),
                                NumeroContrato = reader.IsDBNull(9) ? "" : reader.GetString(9),
                                NombreContrato = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            };
                        }
                    }
                }
            }

            return dispositivo;
        }

        public static async Task<List<Dispositivo>> ObtenerDispositivosAsync()
        {
            List<Dispositivo> dispositivos = new List<Dispositivo>();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "SELECT MacAddress,pin,activo,fecha_creacion,fecha_activacion,creado_por,notas,lista,activado_por,numero_contrato,nombre_contrato " +
                                      "from dispositivos;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            dispositivos.Add(new Modelos.Dispositivo
                            {
                                MacAddress = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                Pin = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Activo = reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2),
                                FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                                FechaActivacion = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                                CreadoPor = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                Notas = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                Lista = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                ActivadoPor = reader.IsDBNull(8) ? "" : reader.GetString(8),
                                NumeroContrato = reader.IsDBNull(9) ? "" : reader.GetString(9),
                                NombreContrato = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            });
                        }
                    }
                }
            }

            return dispositivos;
        }

        public static async Task<List<HistorialDispositivoResponse>> ObtenerDispositivosHistorialAsync()
        {
            List<HistorialDispositivoResponse> historial = new List<HistorialDispositivoResponse>();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "SELECT id_historial_activaciones,macaddress,estado,fecha_cambio_estado,usuario FROM dispositivos_historial;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            historial.Add(new Modelos.HistorialDispositivoResponse
                            {
                                IdHistorialActivaciones = reader.GetString(0),
                                MacAddress = reader.GetString(1),
                                Estado = reader.GetString(2),
                                FechaCambioEstado = reader.IsDBNull(3) ? "" : reader.GetDateTime(3).ToString("dd/MM/yyyy"),
                                Usuario = reader.GetString(4)
                            });
                        }
                    }
                }
            }

            return historial;
        }

        public static async Task AgregarDispositivoAsync(Dispositivo dispositivo, string lista)
        {
            dispositivo.MacAddress = dispositivo.MacAddress.ToLower().Trim();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "INSERT INTO `dispositivos` " +
                                      "(`MacAddress`,`pin`,`activo`,`fecha_creacion`,`creado_por`,`notas`,`lista`,`numero_contrato`,`nombre_contrato`) " +
                                      "VALUES(@MacAddress,@pin,@activo,@fecha_creacion,@creado_por,@notas,@lista,@numeroContrato,@nombreContrato); ";

                    cmd.Parameters.AddWithValue("@MacAddress", dispositivo.MacAddress);
                    cmd.Parameters.AddWithValue("@pin", dispositivo.Pin);
                    cmd.Parameters.AddWithValue("@activo", 0);
                    cmd.Parameters.AddWithValue("@fecha_creacion", dispositivo.FechaCreacion);
                    cmd.Parameters.AddWithValue("@creado_por", dispositivo.CreadoPor);
                    cmd.Parameters.AddWithValue("@notas", dispositivo.Notas);
                    cmd.Parameters.AddWithValue("@lista", lista);
                    cmd.Parameters.AddWithValue("@numeroContrato", dispositivo.NumeroContrato);
                    cmd.Parameters.AddWithValue("@nombreContrato", dispositivo.NombreContrato);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task ActualizarispositivoAsync(Dispositivo dispositivo)
        {
            dispositivo.MacAddress = dispositivo.MacAddress.ToLower().Trim();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "UPDATE `dispositivos` " +
                                      "SET numero_contrato=@numeroContrato,nombre_contrato=@nombreContrato " +
                                      "WHERE MacAddress=@MacAddress; ";

                    cmd.Parameters.AddWithValue("@MacAddress", dispositivo.MacAddress);
                    cmd.Parameters.AddWithValue("@numeroContrato", dispositivo.NumeroContrato);
                    cmd.Parameters.AddWithValue("@nombreContrato", dispositivo.NombreContrato);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task EliminarDispositivoAsync(Dispositivo dispositivo)
        {
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "DELETE FROM `dispositivos` " +
                                      " WHERE MacAddress=@MacAddress;";

                    cmd.Parameters.AddWithValue("@MacAddress", dispositivo.MacAddress);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task InactivarDispositivoAsync(Dispositivo dispositivo)
        {
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "UPDATE `dispositivos` " +
                                      "SET Activo=0,fecha_ultima_inactivo=@fecha_ultima_inactivo " +
                                      "WHERE MacAddress=@MacAddress; ";

                    cmd.Parameters.AddWithValue("@MacAddress", dispositivo.MacAddress);
                    cmd.Parameters.AddWithValue("@fecha_ultima_inactivo", DateTime.Now);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task ActivarDispositivoAsync(Dispositivo dispositivo)
        {

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "UPDATE `dispositivos` " +
                                      "SET activo=1,fecha_ultima_activo=@fecha_ultima_activo, fecha_ultima_inactivo=@fecha_ultima_inactivo,activado_por=@activadoPor " +
                                      "WHERE MacAddress=@MacAddress; ";

                    cmd.Parameters.AddWithValue("@MacAddress", dispositivo.MacAddress);
                    cmd.Parameters.AddWithValue("@fecha_ultima_activo", DateTime.Now);
                    cmd.Parameters.AddWithValue("@fecha_ultima_inactivo", null);
                    cmd.Parameters.AddWithValue("@activadoPor", dispositivo.ActivadoPor);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task InsertarHistorialAsync(string MacAddress, Estado estado, string usuario)
        {
            Modelos.HistorialDispositivo historialDispositivo = new HistorialDispositivo
            {
                Estado = estado.ToString(),
                FechaCambioEstado = DateTime.Now,
                MacAddress = MacAddress,
                Usuario = usuario
            };

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "INSERT INTO `dispositivos_historial` " +
                                      "(`id_historial_activaciones`,`macaddress`,`estado`,`fecha_cambio_estado`,`usuario`) " +
                                      "VALUES(@id_historial_activaciones,@macaddress,@estado,@fecha_cambio_estado,@usuario); ";

                    string idHistorialActivaciones = Guid.NewGuid().ToString();
                    cmd.Parameters.AddWithValue("@id_historial_activaciones", idHistorialActivaciones);
                    cmd.Parameters.AddWithValue("@macaddress", historialDispositivo.MacAddress);
                    cmd.Parameters.AddWithValue("@estado", historialDispositivo.Estado);
                    cmd.Parameters.AddWithValue("@fecha_cambio_estado", historialDispositivo.FechaCambioEstado);
                    cmd.Parameters.AddWithValue("@usuario", historialDispositivo.Usuario);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static void LogActividadDispositivo(string macAddress, int chnId, string userAgent, string userIp)
        {
            try
            {
                lock (activityLock)
                {
                    var actividad = ActivityBatch.FirstOrDefault(x => x.MacAddress.Equals(macAddress,StringComparison.InvariantCultureIgnoreCase));

                    if (actividad == null)
                    {
                        ActivityBatch.Add(new ActividadUsuario
                        {
                            ChnId= chnId,
                            MacAddress = macAddress,
                            Ip = userIp,
                            UserAgent= userAgent,
                            UltimaActualizacion = DateTime.Now
                        });
                    }
                    else
                    {
                        actividad.ChnId = chnId;
                        actividad.Ip = userIp;
                        actividad.UserAgent = userAgent;
                    }
                }
            }
            catch (Exception ex)
            {
               Console.WriteLine("Error al guardar actividad." + ex.Message);
            }
        }

        public static async Task LogActividadSospechosa(string macAddress, string userAgent, string userIp)
        {
            try
            {
                using (var conn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    try
                    {
                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                        using (var cmd = conn.CreateCommand())
                        {
                            if (conn.State == System.Data.ConnectionState.Closed) await conn.OpenAsync();

                            cmd.CommandText = "INSERT INTO solicitudes_sospechosas(id_solicitud_sospechosa,mac_address,fecha_hora,ip_cliente,agente)" +
                                              "VALUES(@id_solicitud_sospechosa,@mac_address,@fecha_hora,@ip_cliente,@agente);";

                            string idSolicitudSospechosa = Guid.NewGuid().ToString();
                            cmd.Parameters.AddWithValue("@id_solicitud_sospechosa", idSolicitudSospechosa);
                            cmd.Parameters.AddWithValue("@mac_address", macAddress);
                            cmd.Parameters.AddWithValue("@fecha_hora", DateTime.Now);
                            cmd.Parameters.AddWithValue("@ip_cliente", userIp);
                            cmd.Parameters.AddWithValue("@agente", userAgent);

                            await cmd.ExecuteNonQueryAsync();
                        }

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error al insertar." + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al actualizar actividad sospechosa." + ex.Message);
            }

        }

        public static async Task<List<Dispositivo>> ObtenerDispositivosPorEstadoAsync(Estado estado)
        {
            List<Dispositivo> dispositivos = new List<Dispositivo>();
            int activo = estado == Estado.ACTIVO ? 1 : 0;

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "SELECT MacAddress,pin,activo,fecha_creacion,fecha_activacion,creado_por,notas,lista,activado_por " +
                                      "from dispositivos where activo=@activo;";

                    cmd.Parameters.AddWithValue("@activo", activo);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            dispositivos.Add(new Modelos.Dispositivo
                            {
                                MacAddress = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                Pin = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Activo = reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2),
                                FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                                FechaActivacion = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                                CreadoPor = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                Notas = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                Lista = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                ActivadoPor = reader.IsDBNull(8) ? "" : reader.GetString(8)
                            });
                        }
                    }
                }
            }

            return dispositivos;
        }
    }

public class ActividadUsuario
    {
        public string MacAddress { get; set; } = "";
        public string Ip { get; set; } = "";
        public int ChnId { get; set; }
        public DateTime UltimaActualizacion { get; set; }
        public string UserAgent { get; set; } = "";
    }