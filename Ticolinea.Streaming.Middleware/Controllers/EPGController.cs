using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.IO;
using System.Xml.Linq;
using MySqlConnector;
using ticolinea.stream.service.Constantes;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class EPGController : ControllerBase
    {
        /// <summary>
        /// Get EPG data by channel ID using JWT token authentication
        /// Returns XMLTV format EPG data for the specified channel
        /// </summary>
        [HttpGet("{epgId}")]
        public async Task<IActionResult> GetEPGByToken(string epgId, [FromQuery] string? token = null)
        {
            // Extract and validate token
            var extractedToken = token ?? TokenValidation.ExtractToken(Request);
            var validation = TokenValidation.ValidateToken(extractedToken);
            
            if (validation == null || !validation.IsValid)
                return Unauthorized("Invalid or expired token");

            try
            {
                // Query EPG data from database by canal_epg (channel EPG ID)
                var epgData = new List<EPGEntry>();
                
                using (var cnn = new MySqlConnection(Global.MARIADB_CONN))
                {
                    await cnn.OpenAsync();
                    
                using (var cmd = cnn.CreateCommand())
                {
                    // Get current and upcoming programs for this channel
                    // Date format matches database: yyyyMMddHH00 stored as long (e.g., 202209212000)
                    var horaFechaInicio = DateTime.Now.AddHours(-1).ToString("yyyyMMddHH00");
                    var horaFechaFin = DateTime.Now.AddHours(24).ToString("yyyyMMddHH00");
                    // Convert to long for comparison (database stores as BIGINT)
                    long horaFechaInicioLong = long.Parse(horaFechaInicio);
                    long horaFechaFinLong = long.Parse(horaFechaFin);
                    
                    cmd.CommandText = @"
                            SELECT canal_epg, titulo, descripcion, anno, fecha_hora_inicio, fecha_hora_fin, icono, inicio, fin
                            FROM epg_tl
                            WHERE canal_epg = @canal_epg
                            AND fecha_hora_inicio >= @hora_fecha_inicio
                            AND fecha_hora_inicio <= @hora_fecha_fin
                            ORDER BY fecha_hora_inicio ASC
                            LIMIT 100;";
                    
                    cmd.Parameters.AddWithValue("@canal_epg", epgId);
                    cmd.Parameters.AddWithValue("@hora_fecha_inicio", horaFechaInicioLong);
                    cmd.Parameters.AddWithValue("@hora_fecha_fin", horaFechaFinLong);
                        
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                epgData.Add(new EPGEntry
                                {
                                    CanalEPG = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                    Titulo = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                    Descripcion = reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    Anno = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                    FechaHoraInicio = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                                    FechaHoraFin = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                                    Icono = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                    Inicio = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                    Fin = reader.IsDBNull(8) ? "" : reader.GetString(8)
                                });
                            }
                        }
                    }
                }

                if (epgData.Count == 0)
                {
                    return NotFound(new { error = "EPG data not found for channel", epgId });
                }

                // Build XMLTV format response
                var xml = new XDocument(
                    new XElement("tv",
                        new XAttribute("generator-info-name", "Ticolinea Streaming"),
                        new XAttribute("generator-info-url", "https://ticolinea.com"),
                        epgData.Select(entry => new XElement("programme",
                            new XAttribute("start", entry.Inicio ?? ""),
                            new XAttribute("stop", entry.Fin ?? ""),
                            new XAttribute("channel", entry.CanalEPG ?? ""),
                            new XElement("title", entry.Titulo ?? ""),
                            !string.IsNullOrEmpty(entry.Descripcion) ? new XElement("desc", entry.Descripcion) : null,
                            !string.IsNullOrEmpty(entry.Icono) ? new XElement("icon", new XAttribute("src", entry.Icono)) : null,
                            entry.Anno > 0 ? new XElement("date", entry.Anno.ToString("0000-00-00")) : null
                        ))
                    )
                );

                return Content(xml.ToString(), "application/xml", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to read EPG data", message = ex.Message });
            }
        }

        /// <summary>
        /// Get EPG data by channel ID using credentials (legacy - for backward compatibility)
        /// </summary>
        [HttpGet("{epgId}/{usuario}/{password}")]
        public async Task<IActionResult> GetEPGByCredentials(string epgId, string usuario, string password)
        {
            // Validate credentials using legacy method
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) 
                return Unauthorized("Invalid credentials");

            try
            {
                // Query EPG data from database by canal_epg (channel EPG ID)
                var epgData = new List<EPGEntry>();
                
                using (var cnn = new MySqlConnection(Global.MARIADB_CONN))
                {
                    await cnn.OpenAsync();
                    
                    using (var cmd = cnn.CreateCommand())
                    {
                        // Date format matches database: yyyyMMddHH00 stored as long (e.g., 202209212000)
                        var horaFechaInicio = DateTime.Now.AddHours(-1).ToString("yyyyMMddHH00");
                        var horaFechaFin = DateTime.Now.AddHours(24).ToString("yyyyMMddHH00");
                        // Convert to long for comparison (database stores as BIGINT)
                        long horaFechaInicioLong = long.Parse(horaFechaInicio);
                        long horaFechaFinLong = long.Parse(horaFechaFin);
                        
                        cmd.CommandText = @"
                            SELECT canal_epg, titulo, descripcion, anno, fecha_hora_inicio, fecha_hora_fin, icono, inicio, fin
                            FROM epg_tl
                            WHERE canal_epg = @canal_epg
                            AND fecha_hora_inicio >= @hora_fecha_inicio
                            AND fecha_hora_inicio <= @hora_fecha_fin
                            ORDER BY fecha_hora_inicio ASC
                            LIMIT 100;";
                        
                        cmd.Parameters.AddWithValue("@canal_epg", epgId);
                        cmd.Parameters.AddWithValue("@hora_fecha_inicio", horaFechaInicioLong);
                        cmd.Parameters.AddWithValue("@hora_fecha_fin", horaFechaFinLong);
                        
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                epgData.Add(new EPGEntry
                                {
                                    CanalEPG = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                    Titulo = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                    Descripcion = reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    Anno = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                    FechaHoraInicio = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                                    FechaHoraFin = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                                    Icono = reader.IsDBNull(6) ? "" : reader.GetString(6),
                                    Inicio = reader.IsDBNull(7) ? "" : reader.GetString(7),
                                    Fin = reader.IsDBNull(8) ? "" : reader.GetString(8)
                                });
                            }
                        }
                    }
                }

                if (epgData.Count == 0)
                {
                    return NotFound(new { error = "EPG data not found for channel", epgId });
                }

                // Build XMLTV format response
                var xml = new XDocument(
                    new XElement("tv",
                        new XAttribute("generator-info-name", "Ticolinea Streaming"),
                        new XAttribute("generator-info-url", "https://ticolinea.com"),
                        epgData.Select(entry => new XElement("programme",
                            new XAttribute("start", entry.Inicio ?? ""),
                            new XAttribute("stop", entry.Fin ?? ""),
                            new XAttribute("channel", entry.CanalEPG ?? ""),
                            new XElement("title", entry.Titulo ?? ""),
                            !string.IsNullOrEmpty(entry.Descripcion) ? new XElement("desc", entry.Descripcion) : null,
                            !string.IsNullOrEmpty(entry.Icono) ? new XElement("icon", new XAttribute("src", entry.Icono)) : null,
                            entry.Anno > 0 ? new XElement("date", entry.Anno.ToString("0000-00-00")) : null
                        ))
                    )
                );

                return Content(xml.ToString(), "application/xml", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to read EPG data", message = ex.Message });
            }
        }
    }

    // Helper class for EPG data
    public class EPGEntry
    {
        public string CanalEPG { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public int Anno { get; set; }
        public long FechaHoraInicio { get; set; }
        public long FechaHoraFin { get; set; }
        public string Icono { get; set; } = "";
        public string? Inicio { get; set; }
        public string? Fin { get; set; }
    }
}

