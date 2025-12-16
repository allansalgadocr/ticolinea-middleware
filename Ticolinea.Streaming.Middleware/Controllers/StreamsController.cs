using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using System.Text;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class StreamsController : ControllerBase
    {

        /*[HttpGet]
        public async Task<IActionResult> Reordenar()
        {
            string orden = "";
            var cmd = this.cnn.CreateCommand() as MySqlCommand;
            cmd.CommandText = "SELECT replace(replace(bouquet_channels,'[',''),']','') FROM bouquets WHERE Id=1;";

            using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    orden = reader.GetString(0);
                }

            var lista = orden.Split(',');
            var ord = 0;
            foreach (var lst in lista)
            {
                ord++;
                int streamId = 0;
                using (var command = new MySqlCommand($"SELECT id FROM streams_tl WHERE Id={lst};", connection))
                using (var reader = command.ExecuteReader())
                    while (await reader.ReadAsync())
                    {
                        streamId = reader.GetInt32(0);
                    }

                string queryUpdate = "UPDATE streams_tl " +
                               "SET orden=@ord " +
                               "WHERE id=@chnId;";

                using var cmdUpdate = new MySqlCommand(queryUpdate, connection);
                cmdUpdate.Parameters.AddWithValue("@ord", ord);
                cmdUpdate.Parameters.AddWithValue("@chnId", streamId);

                cmdUpdate.ExecuteNonQuery();
            }

            return Ok();
        }*/

        [HttpGet("{usuario}/{password}/{macAddress}")]
        public async Task<IActionResult> PlaylistMobile(string usuario, string password, string macAddress)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);

            if (usuariodb == null)
                return Unauthorized();


            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");

            Modelos.PaqueteTV paquete = null;
            string paquetetvId="";
            if (!string.IsNullOrEmpty(usuariodb.PaqueteTV))
            {
                paquete = await Data.PaqueteTV.ObtenerPaquete(usuariodb.PaqueteTV);
                paquetetvId= paquete.IdPaquete;
            }

            List<Modelos.Bouquet> bouquet = await Data.Streams.ObtenerCanalesSinOrdenAsync(paquetetvId);
            List<Modelos.Bouquet> bouquetCustom = await Data.Streams.ObtenerCanalesConOrdenAsync(paquetetvId);

            foreach (var canal in bouquetCustom)
            {
                if (canal.CanalId < bouquet.Count() - 1)
                {
                    bouquet.Insert(canal.CanalId - 1, canal);
                }
                else
                {
                    bouquet.Add(canal);
                }
            }

            //Peliculas
            if ((paquete != null && paquete.Activo == 1 && paquete.Peliculas == 1) || string.IsNullOrEmpty(usuariodb.PaqueteTV))
            {
                List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                bouquet.AddRange(bouquetPeliculas);
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                {
                    sb.AppendLine($"http://tv.play-latino.com:27701/Live/StreamingMovil/{chn.Id}/{usuario}/{password}/{macAddress}\r\n");
                }

                if (chn.Tipo == 2 || chn.Tipo == 3)
                {
                    sb.AppendLine($"http://tv.play-latino.com:27701/Peliculas/ReproducirMovil/{chn.Id}/{macAddress}/{usuario}/{password}.{chn.Contenedor}\r\n");
                }
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{usuario}.m3u");
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> Playlist(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);

            if (usuariodb == null) 
                return Unauthorized();

           
            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");

            Modelos.PaqueteTV paquete = null;
            string paquetetvId = "";
            if (!string.IsNullOrEmpty(usuariodb.PaqueteTV))
            {
                paquete = await Data.PaqueteTV.ObtenerPaquete(usuariodb.PaqueteTV);
                paquetetvId = paquete.IdPaquete;
            }

            List<Modelos.Bouquet> bouquet = await Data.Streams.ObtenerCanalesSinOrdenAsync(paquetetvId);
            List<Modelos.Bouquet> bouquetCustom = await Data.Streams.ObtenerCanalesConOrdenAsync(paquetetvId);

            foreach (var canal in bouquetCustom)
            {
                if (canal.CanalId < bouquet.Count() - 1)
                {
                    bouquet.Insert(canal.CanalId - 1, canal);
                }
                else
                {
                    bouquet.Add(canal);
                }
            }

            //Peliculas
            if ((paquete != null && paquete.Activo == 1 && paquete.Peliculas == 1) || string.IsNullOrEmpty(usuariodb.PaqueteTV))
            {
                List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                bouquet.AddRange(bouquetPeliculas);
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                {
                    sb.AppendLine($"http://tv.play-latino.com:27701/Live/Streaming/{chn.Id}/{usuario}/{password}.m3u8\r\n");
                }
                else if (chn.Tipo == 2 || chn.Tipo == 3)
                {
                    sb.AppendLine($"http://tv.play-latino.com:27701/Peliculas/Reproducir/{chn.Id}/{usuario}/{password}.{chn.Contenedor}\r\n");
                }
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{usuario}.m3u");
        }

        /// <summary>
        /// Get playlist by MAC address - validates MAC against ClientMacAddress table
        /// </summary>
        [HttpGet("PlaylistByMac/{macAddress}")]
        public async Task<IActionResult> PlaylistByMac(string macAddress)
        {
            // Validate MAC address and get client/provider info
            var validation = await Helpers.ClientValidation.ValidateMacAddress(macAddress);
            
            if (validation == null || !validation.IsValid)
                return Unauthorized("MAC address not found or not authorized");

            // Use provider URL for streaming URLs
            var providerBaseUrl = validation.ProviderUrl.TrimEnd('/');
            
            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");

            // Get client's channels based on their packages
            List<Modelos.Bouquet> bouquet = await Data.Streams.ObtenerCanalesSinOrdenAsync(validation.PaqueteTvId);
            List<Modelos.Bouquet> bouquetCustom = await Data.Streams.ObtenerCanalesConOrdenAsync(validation.PaqueteTvId);

            // Merge lists: custom ordered channels take priority, then fill with unordered channels
            // Assign high CanalId to unordered channels so they appear after ordered ones
            int maxCustomOrder = bouquetCustom.Any() ? bouquetCustom.Max(c => c.CanalId) : 0;
            int autoOrder = maxCustomOrder + 1;
            foreach (var canal in bouquet)
            {
                if (canal.CanalId == 0)
                    canal.CanalId = autoOrder++;
            }
            
            // Combine and sort by CanalId
            bouquet.AddRange(bouquetCustom);
            bouquet = bouquet.OrderBy(c => c.CanalId).ToList();

            // Include movies if client has access (check package for movies access)
            if (string.IsNullOrEmpty(validation.PaqueteTvId))
            {
                List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                bouquet.AddRange(bouquetPeliculas);
            }
            else
            {
                // Check if package includes movies
                var paquete = await Data.PaqueteTV.ObtenerPaquete(validation.PaqueteTvId);
                if (paquete != null && paquete.Activo == 1 && paquete.Peliculas == 1)
                {
                    List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                    bouquet.AddRange(bouquetPeliculas);
                }
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                {
                    // Use provider URL for live streaming
                    sb.AppendLine($"{providerBaseUrl}/Live/StreamingByMac/{chn.Id}/mac/{macAddress}.m3u8\r\n");
                }
                else if (chn.Tipo == 2 || chn.Tipo == 3)
                {
                    // Use provider URL for movies/series
                    sb.AppendLine($"{providerBaseUrl}/Peliculas/Reproducir/{chn.Id}/mac/{macAddress}.{chn.Contenedor}\r\n");
                }
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_mac_{macAddress}.m3u");
        }

        /// <summary>
        /// Get playlist by credentials - validates username/password against ClientCredentials table
        /// </summary>
        [HttpGet("PlaylistByCredentials/{username}/{password}")]
        public async Task<IActionResult> PlaylistByCredentials(string username, string password)
        {
            // Validate credentials and get client/provider info
            var validation = await Helpers.ClientValidation.ValidateCredentials(username, password);
            
            if (validation == null || !validation.IsValid)
                return Unauthorized("Invalid credentials");

            // Use provider URL for streaming URLs
            var providerBaseUrl = validation.ProviderUrl.TrimEnd('/');
            
            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");

            // Get client's channels based on their packages
            List<Modelos.Bouquet> bouquet = await Data.Streams.ObtenerCanalesSinOrdenAsync(validation.PaqueteTvId);
            List<Modelos.Bouquet> bouquetCustom = await Data.Streams.ObtenerCanalesConOrdenAsync(validation.PaqueteTvId);

            // Merge lists: custom ordered channels take priority, then fill with unordered channels
            int maxCustomOrder = bouquetCustom.Any() ? bouquetCustom.Max(c => c.CanalId) : 0;
            int autoOrder = maxCustomOrder + 1;
            foreach (var canal in bouquet)
            {
                if (canal.CanalId == 0)
                    canal.CanalId = autoOrder++;
            }
            
            // Combine and sort by CanalId
            bouquet.AddRange(bouquetCustom);
            bouquet = bouquet.OrderBy(c => c.CanalId).ToList();

            // Include movies if client has access (check package for movies access)
            if (string.IsNullOrEmpty(validation.PaqueteTvId))
            {
                List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                bouquet.AddRange(bouquetPeliculas);
            }
            else
            {
                // Check if package includes movies
                var paquete = await Data.PaqueteTV.ObtenerPaquete(validation.PaqueteTvId);
                if (paquete != null && paquete.Activo == 1 && paquete.Peliculas == 1)
                {
                    List<Modelos.Bouquet> bouquetPeliculas = await Data.Peliculas.ObtenerPeliculas();
                    bouquet.AddRange(bouquetPeliculas);
                }
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                {
                    // Use provider URL for live streaming
                    sb.AppendLine($"{providerBaseUrl}/Live/Streaming/{chn.Id}/{username}/{password}.m3u8\r\n");
                }
                else if (chn.Tipo == 2 || chn.Tipo == 3)
                {
                    // Use provider URL for movies/series
                    sb.AppendLine($"{providerBaseUrl}/Peliculas/Reproducir/{chn.Id}/{username}/{password}.{chn.Contenedor}\r\n");
                }
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{username}.m3u");
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> PlaylistApi(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            Modelos.Live contenido = new();
            List<Modelos.Canales> bouquet = new();
            List<Modelos.Canales> bouquetCustom = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name, canal_epg FROM streams_tl a " +
                                                            "INNER JOIN stream_categories b " +
                                                            "on a.id_categoria = b.id " +
                                                            "WHERE habilitado=1 and tipo=1 and canal_id=0 " +
                                                            "order by a.orden asc;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Modelos.Canales
                            {
                                StreamId = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                CanalEPG = reader.GetString(4),
                                URL = obtenerURL(reader.GetInt32(0), usuario, password)
                            });
                        }
                }

                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name, canal_epg, canal_id FROM streams_tl a " +
                                                        "INNER JOIN stream_categories b " +
                                                        "on a.id_categoria = b.id " +
                                                        "WHERE habilitado=1 and tipo=1 and canal_id != 0 " +
                                                        "order by a.canal_id asc;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquetCustom.Add(new Modelos.Canales
                            {
                                StreamId = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                CanalEPG = reader.GetString(4),
                                URL = obtenerURL(reader.GetInt32(0), usuario, password),
                                CanalId = reader.GetInt32(5)
                            });
                        }

                    foreach (var canal in bouquetCustom)
                    {
                        if (canal.CanalId < bouquet.Count() - 1)
                        {
                            bouquet.Insert(canal.CanalId - 1, canal);
                        }
                        else
                        {
                            bouquet.Add(canal);
                        }
                    }

                    contenido.Canales = bouquet;
                }

                int cont = 0;
                foreach (var canales in contenido.Canales)
                {
                    cont++;
                    canales.Chn = cont;
                }

                List<Modelos.InfoStream> infoStreams = new();
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    //202209212000
                    string horaFechaInicio = DateTime.Now.AddHours(-1).ToString("yyyyMMddHH00");
                    string horaFechaFin = DateTime.Now.AddHours(3).ToString("yyyyMMddHH00");
                    cmd.CommandText = "select a.canal_epg,titulo,anno,fecha_hora_inicio,fecha_hora_fin from epg_tl a " +
                                        "inner join streams_tl b " +
                                        "on a.canal_epg = b.canal_epg " +
                                        $"where fecha_hora_inicio >= {horaFechaInicio} and fecha_hora_inicio <= {horaFechaFin} and b.habilitado = 1; ";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            infoStreams.Add(new Modelos.InfoStream
                            {
                                CanalEpg = reader.GetString(0),
                                Titulo = reader.GetString(1),
                                Descripcion = "",
                                Anno = reader.GetString(2),
                                Inicio = reader.GetInt64(3),
                                Fin = reader.GetInt64(4)
                            });
                        }
                }

                contenido.EPG = infoStreams;
            }

            return Ok(contenido);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> Peliculas(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.Peliculas> bouquet = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    cmd.CommandText = "select a.id,a.id_categoria,b.category_name,nombre_stream,imagen_stream,agregado,contenedor,anno,resena,pg,duracion from streams_tl a " +
                                        "inner join stream_categories b " +
                                        "on a.id_categoria = b.id " +
                                        "inner join pelicula_info c " +
                                        "on a.id = c.stream_id " +
                                        "where tipo = 2 order by a.agregado desc;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Modelos.Peliculas
                            {
                                Id = reader.GetInt32(0),
                                IdCategoria = reader.GetInt32(1),
                                Categoria = reader.GetString(2).Replace("VOD/", ""),
                                Nombre = reader.GetString(3),
                                Imagen = reader.GetString(4),
                                Agregado = reader.GetInt32(5),
                                Contenedor = reader.GetString(6),
                                Anno = reader.GetString(7),
                                Resena = reader.GetString(8),
                                PG = reader.GetString(9),
                                Duracion = reader.GetString(10),
                                URL = obtenerURLPeliculas(reader.GetInt32(0), usuario, password, reader.GetString(6))
                            });
                        }
                }

            }

            return Ok(bouquet.OrderByDescending(o => o.Agregado));
        }

        private string obtenerURL(int streamId, string usuario, string password)
        {
            string url = string.Empty;
#if !DEBUG
            url = $"http://tv.play-latino.com:27701/Live/Streaming/{streamId}/{usuario}/{password}.m3u8";
#endif
#if DEBUG
            url = $"http://localhost:5002/Live/Streaming/{streamId}/{usuario}/{password}.m3u8";
#endif
            return url;
        }

        private string obtenerURLPeliculas(int streamId, string usuario, string password, string contenedor)
        {
            string url = string.Empty;
            url = $"http://tv.play-latino.com:27701/Peliculas/Reproducir/{streamId}/{usuario}/{password}.{contenedor}";

            return url;
        }

        [HttpGet("{usuario}/{password}.{extension}")]
        public async Task<IActionResult> STB(string usuario, string password, string extension)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.Bouquet> bouquet = new();
            List<Modelos.Bouquet> bouquetCustom = new();
            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg FROM streams_tl a " +
                                                            "INNER JOIN stream_categories b " +
                                                            "on a.id_categoria = b.id " +
                                                            "WHERE habilitado=1 and tipo=1 and canal_id=0 " +
                                                            "order by a.orden asc;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                            });
                        }
                }

                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg, canal_id FROM streams_tl a " +
                                                        "INNER JOIN stream_categories b " +
                                                        "on a.id_categoria = b.id " +
                                                        "WHERE habilitado=1 and tipo=1 and canal_id != 0 " +
                                                        "order by a.canal_id asc;";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquetCustom.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                                CanalId = reader.GetInt32(7)
                            });
                        }
                }

                foreach (var canal in bouquetCustom)
                {
                    if (canal.CanalId < bouquet.Count() - 1)
                    {
                        bouquet.Insert(canal.CanalId - 1, canal);
                    }
                    else
                    {
                        bouquet.Add(canal);
                    }
                }

                using (var cmdPeliculas = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmdPeliculas.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg FROM streams_tl a " +
                                                            "INNER JOIN stream_categories b " +
                                                            "on a.id_categoria = b.id " +
                                                            "WHERE habilitado=1 and tipo=2 " +
                                                            "order by a.id desc;";

                    using (var reader = await cmdPeliculas.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                            });
                        }
                }

            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                {
#if !DEBUG
                    sb.AppendLine($"http://tv.play-latino.com:27701/Live/Streaming/{chn.Id}/{usuario}/{password}.m3u8\r\n");
#endif
#if DEBUG
                    sb.AppendLine($"http://192.168.0.5:5002/Live/Streaming/{chn.Id}/{usuario}/{password}.m3u8\r\n");
#endif
                }

                if (chn.Tipo == 2)
                    sb.AppendLine($"http://tv.play-latino.com:27701/Peliculas/Reproducir/{chn.Id}/{usuario}/{password}.{chn.Contenedor}\r\n");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{usuario}.m3u");
        }

        [HttpGet("{usuario}/{password}/{anno}/{mes}/{dia}")]
        public async Task<IActionResult> Informacion(string usuario, string password, int anno = 0, int mes = 0, int dia = 0)
        {
            List<Modelos.InfoStream> infoStreams = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                string fechaHoraActual = new DateTime(anno, mes, dia).ToString("yyyyMMdd");
                string fechaHoraDiaSiguiente = new DateTime(anno, mes, dia).AddDays(1).ToString("yyyyMMdd");
                string fechaHoraInicio = fechaHoraActual + "0000";
                string fechaHoraFin = fechaHoraDiaSiguiente + "0000";
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                    //202209212000
                    string horaFechaInicio = DateTime.Now.AddHours(-2).ToString("yyyyMMddHH00");
                    string horaFechaFin = DateTime.Now.AddHours(5).ToString("yyyyMMddHH00");
                    cmd.CommandText = "select a.canal_epg,titulo,descripcion,anno,fecha_hora_inicio,fecha_hora_fin from epg_tl a " +
                                        "inner join streams_tl b " +
                                        "on a.canal_epg = b.canal_epg " +
                                        $"where fecha_hora_inicio >= {horaFechaInicio} and fecha_hora_inicio <= {horaFechaFin} and b.habilitado = 1; ";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            infoStreams.Add(new Modelos.InfoStream
                            {
                                CanalEpg = reader.GetString(0),
                                Titulo = reader.GetString(1),
                                Descripcion = reader.GetString(2),
                                Anno = reader.GetString(3),
                                Inicio = reader.GetInt64(4),
                                Fin = reader.GetInt64(5)
                            });
                        }
                }
            }

            return Ok(infoStreams);
        }
    }
}
