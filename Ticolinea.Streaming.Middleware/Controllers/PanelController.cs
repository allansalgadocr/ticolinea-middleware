using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MikrotikDotNet;
using MySqlConnector;
using Newtonsoft.Json;
using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Controllers
{
    [Route("panel/api/[controller]/[action]")]
    [ApiController]
    public class PanelController : ControllerBase
    {
        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerStreams(string usuario, string password)
        {
            List<DataStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,reportado_caido, habilitado, iniciado, canal_epg FROM streams_tl a INNER JOIN " +
                                                              "streams_info b " +
                                                              "ON a.id = b.stream_id INNER JOIN " +
                                                              "stream_categories c " +
                                                              "ON a.id_categoria = c.id and tipo=1 order by orden;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new DataStream
                                {
                                    Id = reader.GetInt32(0),
                                    Imagen = reader.GetString(1),
                                    Nombre = reader.GetString(2),
                                    Categoria = reader.GetString(3),
                                    Fuente = reader.GetString(4),
                                    Ejecutando = reader.GetInt32(5),
                                    Habilitado = reader.GetInt32(6),
                                    Iniciado = reader.GetInt32(7),
                                    CanalEPG = reader.GetString(8)
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(streams);
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> ObtenerStream(string usuario, string password, int chnId)
        {
            List<PanelStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select nombre_stream,fuente_stream,imagen_stream,id_categoria,es_bajodemanda,transcode,habilitado, canal_epg, canal_id from streams_tl " +
                                          "where id=@id;";
                        cmd.Parameters.AddWithValue("@id", chnId);

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new PanelStream
                                {
                                    NombreStream = reader.GetString(0),
                                    UrlStream = reader.GetString(1),
                                    UrlLogo = reader.GetString(2),
                                    Categoria = reader.GetInt32(3),
                                    EsBajoDemanda = reader.GetInt32(4),
                                    Optimizar = reader.GetInt32(5),
                                    Habilitado = reader.GetInt32(6),
                                    CanalEPG = reader.GetString(7),
                                    CanalId = reader.GetInt32(8)
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(streams.FirstOrDefault());
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarStream([FromBody] PanelStream panelStream, string usuario, string password)
        {
            var startTime = DateTime.UtcNow;

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            // 🔍 Validate input parameters
            if (panelStream == null)
            {
                return BadRequest(new { success = false, message = "PanelStream no puede ser null" });
            }

            try
            {
                Console.WriteLine($"➕ Agregando nuevo stream...");
                
                int newStreamId = 0;
                
                // 💾 Get max ID and insert new stream in parallel
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    await cnn.OpenAsync();
                    
                    // Get max ID
                    using (var cmd = cnn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT MAX(id) FROM streams_tl;";
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                newStreamId = reader.GetInt32(0) + 1;
                            }
                            else
                            {
                                newStreamId = 1; // First stream
                            }
                        }
                    }

                    if (newStreamId == 0) 
                    {
                        throw new Exception("Error al obtener maxID");
                    }

                    // 💾 Insert into database in parallel for better performance
                    var insertTasks = new List<Task>();
                    
                    // Task 1: Insert into streams_tl table
                    insertTasks.Add(Task.Run(async () =>
                    {
                        using var insertCnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await insertCnn.OpenAsync();
                        
                        using var cmd = insertCnn.CreateCommand();
                        cmd.CommandText = "INSERT INTO `streams_tl` " +
                                          "(`id`,`id_categoria`,`nombre_stream`,`fuente_stream`,`imagen_stream`,`orden`,`agregado`,`probesize_ondemand`,`es_bajodemanda`,`tipo`,`contenedor`,`habilitado`,`transcode_audio`,`video_info`," +
                                          "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`,`canal_epg`,`canal_id`) " +
                                          "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,@es_bajodemanda,1,'',@habilitado,'aac','', " +
                                          "'',6,5,0,25,@transcode,'','1500k', @canal_epg, @canal_id); ";
                        
                        cmd.Parameters.AddWithValue("@id", newStreamId);
                        cmd.Parameters.AddWithValue("@id_categoria", panelStream.Categoria);
                        cmd.Parameters.AddWithValue("@nombre_stream", panelStream.NombreStream);
                        cmd.Parameters.AddWithValue("@fuente_stream", panelStream.UrlStream);
                        cmd.Parameters.AddWithValue("@imagen_stream", panelStream.UrlLogo);
                        cmd.Parameters.AddWithValue("@orden", newStreamId);
                        cmd.Parameters.AddWithValue("@agregado", DateTimeOffset.Now.ToUnixTimeSeconds());
                        cmd.Parameters.AddWithValue("@es_bajodemanda", panelStream.EsBajoDemanda);
                        cmd.Parameters.AddWithValue("@transcode", panelStream.Optimizar);
                        cmd.Parameters.AddWithValue("@habilitado", panelStream.Habilitado);
                        cmd.Parameters.AddWithValue("@canal_epg", panelStream.CanalEPG);
                        cmd.Parameters.AddWithValue("@canal_id", panelStream.CanalId);

                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Task 2: Insert into streams_info table
                    insertTasks.Add(Task.Run(async () =>
                    {
                        using var insertCnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await insertCnn.OpenAsync();
                        
                        using var cmd = insertCnn.CreateCommand();
                        cmd.CommandText = "INSERT INTO `streams_info` " +
                                          "(`stream_id`,`ejecutando`,`proceso_id`,`info_progreso`,`iniciado`) " +
                                          "VALUES(@stream_id,1,-1,'',1);";
                        cmd.Parameters.AddWithValue("@stream_id", newStreamId);
                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Wait for both database inserts to complete
                    await Task.WhenAll(insertTasks);
                }

                // 🔍 Create StreamDb object for the new stream
                var newStream = new StreamDb
                {
                    Fuente = panelStream.UrlStream,
                    StreamId = newStreamId,
                    ProbeSize = 512000,
                    EsBajoDemanda = panelStream.EsBajoDemanda,
                    ProcesoId = -1,
                    TranscodeAudio = "aac",
                    Intervalo = 6,
                    Segmentos = 5,
                    Framerate = 25,
                    Transcode = panelStream.Optimizar,
                    Resolucion = "",
                    Bitrate = "1500k"
                };

                // Cache the new stream configuration
                StreamCacheHelper.SetCachedStream(newStreamId, newStream);

                // 🚀 Start stream management in background (non-blocking)
                if (panelStream.Habilitado == 1)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Start the stream
                            Jobs.IniciarStream(newStream);
                            
                            // Verify the stream is working (with delay to allow startup)
                            await Task.Delay(TimeSpan.FromSeconds(3));
                            await Jobs.VerificarStream(newStream);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Error en gestión de procesos para stream {newStreamId}: {ex.Message}");
                        }
                    });
                }

                var duration = DateTime.UtcNow - startTime;
                Console.WriteLine($"✅ Stream {newStreamId} agregado exitosamente en {duration.TotalMilliseconds:F0}ms");
                
                return Ok(new { 
                    success = true, 
                    message = $"Stream {newStreamId} agregado exitosamente", 
                    streamId = newStreamId,
                    duration = duration.TotalMilliseconds 
                });
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Console.WriteLine($"❌ Error al agregar stream después de {duration.TotalMilliseconds:F0}ms: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"Error interno: {ex.Message}", duration = duration.TotalMilliseconds });
            }
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarUsuario([FromBody] PanelUsuario panelUsuario, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        long fechaVencimiento = 0;
                        int.TryParse(panelUsuario.FechaVencimiento, out int meses);
                        if (meses > 0)
                            fechaVencimiento = DateTimeOffset.Now.AddMonths(meses).ToUnixTimeSeconds();

                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                        cmd.CommandText = "INSERT INTO `usuarios_ticolinea` " +
                                          "(`usuario`,`clave`,`fecha_vencimiento`,`habilitado`,`conexiones_maximas`,`es_restreamer`,`fecha_creacion`,`creado_por`,`bouquet`,`notas`,`es_movil`) " +
                                          "VALUES(@usuario,@clave,@fecha_vencimiento,@habilitado,@conexionesMaximas,0,@fecha_creacion,0,'',@notas,@esMovil); ";

                        cmd.Parameters.AddWithValue("@usuario", panelUsuario.Usuario);
                        cmd.Parameters.AddWithValue("@clave", panelUsuario.Clave);
                        cmd.Parameters.AddWithValue("@fecha_vencimiento", fechaVencimiento);
                        cmd.Parameters.AddWithValue("@habilitado", panelUsuario.Habilitado);
                        cmd.Parameters.AddWithValue("@conexionesMaximas", panelUsuario.ConexionesMaximas);
                        cmd.Parameters.AddWithValue("@fecha_creacion", now);
                        cmd.Parameters.AddWithValue("@notas", panelUsuario.Notas);
                        cmd.Parameters.AddWithValue("@esMovil", panelUsuario.EsMovil);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarCategoria([FromBody] PanelCategoria panelCategoria, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    int maxId = 0;
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select max(id) from stream_categories;";
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                maxId = reader.GetInt32(0);
                            }
                    }

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                        cmd.CommandText = "INSERT INTO `stream_categories` " +
                                          "(`id`,`category_type`,`category_name`,`parent_id`,`cat_order`) " +
                                          "VALUES(@id,'live',@category_name,0,@id); ";
                        cmd.Parameters.AddWithValue("@id", maxId + 1);
                        cmd.Parameters.AddWithValue("@category_name", panelCategoria.Texto);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> EliminarCategoriasSinUso(string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();


                        cmd.CommandText = "DELETE FROM stream_categories " +
                                          "where id not in (Select id_categoria from streams_tl); ";

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}/{usuarioId}")]
        public async Task<IActionResult> ActualizarCategoria([FromBody] PanelCategoria panelCategoria, string usuario, string password, int categoriaId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        cmd.CommandText = "UPDATE `stream_categories` " +
                                              "SET category_name=@category_name " +
                                              "WHERE id=@id; ";

                        cmd.Parameters.AddWithValue("@category_name", panelCategoria.Texto);
                        cmd.Parameters.AddWithValue("@id", categoriaId);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}/{usuarioId}")]
        public async Task<IActionResult> ActualizarUsuario([FromBody] PanelUsuario panelUsuario, string usuario, string password, int usuarioId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        long fechaVencimiento = -1;
                        int.TryParse(panelUsuario.FechaVencimiento, out int meses);
                        if (meses > 0)
                            fechaVencimiento = DateTimeOffset.Now.AddMonths(meses).ToUnixTimeSeconds();
                        else if (meses == 0) fechaVencimiento = 0;

                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                        if (fechaVencimiento == -1)
                        {
                            cmd.CommandText = "UPDATE `usuarios_ticolinea` " +
                                              "SET usuario=@usuario, clave=@clave, habilitado=@habilitado,notas=@notas,conexiones_maximas=@conexionesMaximas,es_movil=@esMovil " +
                                              "WHERE id=@id; ";
                        }
                        else
                            cmd.CommandText = "UPDATE `usuarios_ticolinea` " +
                                              "SET usuario=@usuario, clave=@clave, habilitado=@habilitado, fecha_vencimiento=@fecha_vencimiento,notas=@notas,conexiones_maximas=@conexionesMaximas,es_movil=@esMovil " +
                                              "WHERE id=@id; ";

                        cmd.Parameters.AddWithValue("@usuario", panelUsuario.Usuario);
                        cmd.Parameters.AddWithValue("@clave", panelUsuario.Clave);
                        if (fechaVencimiento > -1)
                            cmd.Parameters.AddWithValue("@fecha_vencimiento", fechaVencimiento);

                        cmd.Parameters.AddWithValue("@habilitado", panelUsuario.Habilitado);
                        cmd.Parameters.AddWithValue("@notas", panelUsuario.Notas);
                        cmd.Parameters.AddWithValue("@conexionesMaximas", panelUsuario.ConexionesMaximas);
                        cmd.Parameters.AddWithValue("@esMovil", panelUsuario.EsMovil);
                        cmd.Parameters.AddWithValue("@id", usuarioId);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> EliminarUsuario(string usuario, string password, int usuarioId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM usuarios_ticolinea " +
                                      "WHERE id=@usuario_id; ";
                        cmd.Parameters.AddWithValue("@usuario_id", usuarioId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(categorias);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerUsuarios(string usuario, string password)
        {
            List<PanelUsuario> usuarios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT id,usuario,clave,fecha_vencimiento,habilitado,notas,conexiones_maximas,es_movil FROM usuarios_ticolinea;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                usuarios.Add(new PanelUsuario
                                {
                                    Id = reader.GetInt32(0),
                                    Usuario = reader.GetString(1),
                                    Clave = reader.GetString(2),
                                    FechaVencimiento = !reader.IsDBNull(3) ? UnixTimeStampToDateTime(reader.GetInt32(3)) : "",
                                    Habilitado = reader.GetInt32(4),
                                    Notas = reader.GetString(5),
                                    ConexionesMaximas = reader.GetInt32(6),
                                    EsMovil=reader.GetInt32(7)
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(usuarios);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerProveedores(string usuario, string password)
        {
            List<String> fuentes = new();
            List<Proveedores> proveedores = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        cmd.CommandText = "select fuente_stream from streams_tl where habilitado=1 and tipo=1;";
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                string host = ObtenerHost(reader.GetString(0));
                                var proveedor = proveedores.Where(x => x.Fuente == host).FirstOrDefault();
                                if (proveedor != null)
                                {
                                    proveedor.Cantidad++;
                                }
                                else
                                {
                                    proveedores.Add(new Proveedores
                                    {
                                        Fuente = host,
                                        Cantidad = 1
                                    });
                                }
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(proveedores);
        }

        [HttpGet("{usuario}/{password}/{usuarioId}")]
        public async Task<IActionResult> ObtenerUsuario(string usuario, string password, int usuarioId)
        {
            List<PanelUsuario> usuarios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT id,usuario,clave,fecha_vencimiento,habilitado,notas,conexiones_maximas,es_movil FROM usuarios_ticolinea WHERE id=@id;";
                        cmd.Parameters.AddWithValue("@id", usuarioId);

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                usuarios.Add(new PanelUsuario
                                {
                                    Id = reader.GetInt32(0),
                                    Usuario = reader.GetString(1),
                                    Clave = reader.GetString(2),
                                    FechaVencimiento = !reader.IsDBNull(3) ? UnixTimeStampToDateTime(reader.GetInt32(3)) : "",
                                    Habilitado = reader.GetInt32(4),
                                    Notas = reader.GetString(5),
                                    ConexionesMaximas = reader.GetInt32(6),
                                    EsMovil = reader.GetInt32(7)
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(usuarios.FirstOrDefault());
        }

        [HttpPost("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> ActualizarStream([FromBody] PanelStream panelStream, string usuario, string password, int chnId)
        {
            var startTime = DateTime.UtcNow;
            
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            // 🔍 Validate input parameters
            if (panelStream == null)
            {
                return BadRequest(new { success = false, message = "PanelStream no puede ser null" });
            }

            if (chnId <= 0)
            {
                return BadRequest(new { success = false, message = "ID de stream inválido" });
            }

            try
            {
                Console.WriteLine($"🔄 Actualizando stream {chnId}...");
                
                // 🔍 Check cache first for better performance
                StreamDb? currentStream = Helpers.StreamCacheHelper.GetCachedStream(chnId);
                
                if (currentStream == null)
                {
                    // Cache miss - load from database
                    using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                    {
                        await cnn.OpenAsync();
                        
                        using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                            "INNER JOIN streams_info b " +
                                            "on a.id = b.stream_id " +
                                            $"WHERE stream_id = {chnId};";

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    currentStream = new StreamDb
                                    {
                                        Fuente = reader.GetString(0),
                                        StreamId = reader.GetInt32(1),
                                        ProbeSize = reader.GetInt32(2),
                                        EsBajoDemanda = reader.GetInt32(3),
                                        ProcesoId = reader.GetInt32(4),
                                        TranscodeAudio = reader.GetString(5),
                                        Intervalo = reader.GetInt16(6),
                                        Segmentos = reader.GetInt16(7),
                                        Framerate = reader.GetInt32(8),
                                        Transcode = reader.GetInt32(9),
                                        Resolucion = reader.GetString(10),
                                        Bitrate = reader.GetString(11)
                                    };
                                    
                                    // Cache the result for future requests
                                    Helpers.StreamCacheHelper.SetCachedStream(chnId, currentStream);
                                }
                                else
                                {
                                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                                }
                            }
                        }
                    }
                }

                // 🔍 Check real-time status to get accurate process information
                var realTimeStatus = await Helpers.StreamStatusHelper.GetRealTimeStreamStatusAsync(chnId);
                if (realTimeStatus.IsRunning && realTimeStatus.ProcessId.HasValue)
                {
                    // Update the cached stream with real-time process ID
                    currentStream.ProcesoId = realTimeStatus.ProcessId.Value;
                }
                else if (currentStream.ProcesoId > 0)
                {
                    currentStream.ProcesoId = -1; // Mark as not running
                }

                // 💾 Update database in parallel for better performance
                var updateTasks = new List<Task>();
                
                // Task 1: Update streams_tl table
                updateTasks.Add(Task.Run(async () =>
                {
                    using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                    await cnn.OpenAsync();
                    
                    using var cmd = cnn.CreateCommand();
                    cmd.CommandText = "UPDATE `streams_tl` " +
                                      "SET id_categoria=@id_categoria,nombre_stream=@nombre_stream,fuente_stream=@fuente_stream,imagen_stream=@imagen_stream,es_bajodemanda=@es_bajodemanda,habilitado=@habilitado,transcode=@transcode,canal_epg=@canal_epg,canal_id=@canal_id " +
                                      "WHERE id=@id; ";
                    cmd.Parameters.AddWithValue("@id", chnId);
                    cmd.Parameters.AddWithValue("@id_categoria", panelStream.Categoria);
                    cmd.Parameters.AddWithValue("@nombre_stream", panelStream.NombreStream);
                    cmd.Parameters.AddWithValue("@fuente_stream", panelStream.UrlStream);
                    cmd.Parameters.AddWithValue("@imagen_stream", panelStream.UrlLogo);
                    cmd.Parameters.AddWithValue("@es_bajodemanda", panelStream.EsBajoDemanda);
                    cmd.Parameters.AddWithValue("@transcode", panelStream.Optimizar);
                    cmd.Parameters.AddWithValue("@habilitado", panelStream.Habilitado);
                    cmd.Parameters.AddWithValue("@canal_epg", panelStream.CanalEPG);
                    cmd.Parameters.AddWithValue("@canal_id", panelStream.CanalId);

                    await cmd.ExecuteNonQueryAsync();
                }));

                // Task 2: Update streams_info table
                updateTasks.Add(Task.Run(async () =>
                {
                    using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                    await cnn.OpenAsync();
                    
                    using var cmd = cnn.CreateCommand();
                    cmd.CommandText = "UPDATE `streams_info` " +
                                      "SET iniciado=@iniciado, ejecutando=@ejecutando, reportado_caido=0 " +
                                      "WHERE stream_id=@id; ";
                    cmd.Parameters.AddWithValue("@id", chnId);
                    cmd.Parameters.AddWithValue("@iniciado", panelStream.Habilitado);
                    cmd.Parameters.AddWithValue("@ejecutando", panelStream.Habilitado);

                    await cmd.ExecuteNonQueryAsync();
                }));

                // Wait for both database updates to complete
                await Task.WhenAll(updateTasks);

                // 🚦 Handle stream state changes based on habilitado flag
                if (panelStream.Habilitado == 0)
                {
                    // 🛑 Stream is being disabled - stop it completely (non-blocking)
                    if (currentStream.ProcesoId > 0)
                    {
                        // Run process stopping in background (non-blocking)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Jobs.DetenerProceso(currentStream.ProcesoId, currentStream.StreamId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ Error al detener procesos para stream {chnId}: {ex.Message}");
                            }
                        });
                    }
                    
                    // Invalidate cache since stream is disabled
                    Helpers.StreamCacheHelper.InvalidateStream(chnId);
                    
                    var duration = DateTime.UtcNow - startTime;
                    Console.WriteLine($"✅ Stream {chnId} deshabilitado en {duration.TotalMilliseconds:F0}ms");
                    return Ok(new { success = true, message = $"Stream {chnId} deshabilitado exitosamente", duration = duration.TotalMilliseconds });
                }
                else
                {
                    // 🚀 Stream is being enabled - restart it with new configuration (non-blocking)
                    // Run all process management in background (non-blocking)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 🔄 First, ensure any existing processes are stopped
                            if (currentStream.ProcesoId > 0)
                            {
                                await Jobs.DetenerProceso(currentStream.ProcesoId, currentStream.StreamId);
                            }
                            
                            // 🚀 Start the stream with new configuration
                            Jobs.IniciarStream(currentStream);
                            
                            // 🔍 Verify the stream is working (with delay to allow startup)
                            await Task.Delay(TimeSpan.FromSeconds(3));
                            await Jobs.VerificarStream(currentStream);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Error en gestión de procesos para stream {chnId}: {ex.Message}");
                        }
                    });
                    
                    // Invalidate cache since stream configuration changed
                    Helpers.StreamCacheHelper.InvalidateStream(chnId);
                    
                    var duration = DateTime.UtcNow - startTime;
                    Console.WriteLine($"✅ Stream {chnId} actualizado y habilitado en {duration.TotalMilliseconds:F0}ms");
                    return Ok(new { success = true, message = $"Stream {chnId} actualizado y habilitado exitosamente", duration = duration.TotalMilliseconds });
                }
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Console.WriteLine($"❌ Error al actualizar stream {chnId} después de {duration.TotalMilliseconds:F0}ms: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"Error interno: {ex.Message}", duration = duration.TotalMilliseconds });
            }
        }

        [HttpGet("{usuario}/{password}/cache/status")]
        public async Task<IActionResult> GetCacheStatus(string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                // This is a simple endpoint to check cache status
                // In a real implementation, you might want to add more detailed cache statistics
                return Ok(new { 
                    success = true, 
                    message = "Cache helper is working",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking cache status: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerCategorias(string usuario, string password)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        cmd.CommandText = "select id,category_name from stream_categories " +
                                            "where category_name not like 'VOD/%' and category_name not like 'SERIE/%' " +
                                            "order by category_name; ";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                categorias.Add(new PanelCategoria
                                {
                                    Id = reader.GetInt32(0),
                                    Texto = reader.GetString(1),
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(categorias);
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> DetenerStream(string usuario, string password, int chnId)
        {
            var startTime = DateTime.UtcNow;
            
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                Console.WriteLine($"🛑 Deteniendo stream {chnId}...");
                
                // 🔍 Check cache first for better performance
                StreamDb? stream = StreamCacheHelper.GetCachedStream(chnId);
                
                if (stream == null)
                {
                    // Cache miss - load from database
                    using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                    {
                        await cnn.OpenAsync();
                        
                        using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                            "INNER JOIN streams_info b " +
                                            "on a.id = b.stream_id " +
                                            $"WHERE stream_id = {chnId};";

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    stream = new StreamDb
                                    {
                                        Fuente = reader.GetString(0),
                                        StreamId = reader.GetInt32(1),
                                        ProbeSize = reader.GetInt32(2),
                                        EsBajoDemanda = reader.GetInt32(3),
                                        ProcesoId = reader.GetInt32(4),
                                        TranscodeAudio = reader.GetString(5),
                                        Intervalo = reader.GetInt16(6),
                                        Segmentos = reader.GetInt16(7),
                                        Framerate = reader.GetInt32(8),
                                        Transcode = reader.GetInt32(9),
                                        Resolucion = reader.GetString(10),
                                        Bitrate = reader.GetString(11)
                                    };
                                    
                                    // Cache the result for future requests
                                    StreamCacheHelper.SetCachedStream(chnId, stream);
                                }
                                else
                                {
                                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                                }
                            }
                        }
                    }
                }

                // 🔍 Check real-time status to get accurate process information
                var realTimeStatus = await StreamStatusHelper.GetRealTimeStreamStatusAsync(chnId);
                if (realTimeStatus.IsRunning && realTimeStatus.ProcessId.HasValue)
                {
                    // Update the cached stream with real-time process ID
                    stream.ProcesoId = realTimeStatus.ProcessId.Value;
                }
                else if (stream.ProcesoId > 0)
                {
                    stream.ProcesoId = -1; // Mark as not running
                }

                if (stream != null)
                {
                    // 🛑 Stop the background supervision first
                    StreamingService.DetenerSupervision(stream.StreamId);
                    
                    // 🔍 Kill any running FFmpeg processes for this stream (non-blocking)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await Cli
                                .Wrap("/bin/pgrep")
                                .WithArguments($"-f \"/{stream.StreamId}_.m3u\"")
                                .ExecuteBufferedAsync();

                            string output = result.StandardOutput;
                            string[] procesos = output
                                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (var proceso in procesos)
                            {
                                if (int.TryParse(proceso, out var pid))
                                {
                                    try
                                    {
                                        var proc = Process.GetProcessById(pid);
                                        if (!proc.HasExited)
                                        {
                                            proc.Kill(true);
                                        }
                                    }
                                    catch (ArgumentException)
                                    {
                                        // Process already doesn't exist
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"⚠️ Error al detener proceso {pid}: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Error al buscar procesos FFmpeg: {ex.Message}");
                        }
                    });

                    // 📝 Update database to mark stream as stopped
                    using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                    {
                        await cnn.OpenAsync();
                        using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE `streams_info` " +
                                          "SET iniciado=0, ejecutando=0, proceso_id=-1 " +
                                          "WHERE stream_id=@id; ";
                            cmd.Parameters.AddWithValue("@id", chnId);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    
                    // Invalidate cache since stream is stopped
                    StreamCacheHelper.InvalidateStream(chnId);
                    
                    var duration = DateTime.UtcNow - startTime;
                    Console.WriteLine($"✅ Stream {stream.StreamId} detenido en {duration.TotalMilliseconds:F0}ms");
                    return Ok(new { success = true, message = $"Stream {stream.StreamId} detenido exitosamente", duration = duration.TotalMilliseconds });
                }
                else
                {
                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error al detener stream {chnId}: {ex.Message}");
                Console.WriteLine($"📋 Stack trace: {ex.StackTrace}");
                return StatusCode(500, new { success = false, message = $"Error interno: {ex.Message}" });
            }
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> IniciarStream(string usuario, string password, int chnId)
        {
            var startTime = DateTime.UtcNow;
            
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            // Check if stream execution is allowed
            if (!Global.ENABLE_STREAM_EXECUTION)
            {
                var duration = DateTime.UtcNow - startTime;
                return Ok(new { 
                    success = false, 
                    message = "Stream execution is disabled in development environment", 
                    environment = StreamExecutionGuard.GetEnvironmentInfo(),
                    duration = duration.TotalMilliseconds 
                });
            }

            try
            {
                Console.WriteLine($"🚀 Iniciando stream {chnId}...");
                
                // 🔍 Check cache first for better performance
                StreamDb? stream = StreamCacheHelper.GetCachedStream(chnId);
                
                if (stream == null)
                {
                    // Cache miss - load from database
                    using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                    {
                        await cnn.OpenAsync();
                        
                        using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                            "INNER JOIN streams_info b " +
                                            "on a.id = b.stream_id " +
                                            $"WHERE stream_id = {chnId};";

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    stream = new StreamDb
                                    {
                                        Fuente = reader.GetString(0),
                                        StreamId = reader.GetInt32(1),
                                        ProbeSize = reader.GetInt32(2),
                                        EsBajoDemanda = reader.GetInt32(3),
                                        ProcesoId = reader.GetInt32(4),
                                        TranscodeAudio = reader.GetString(5),
                                        Intervalo = reader.GetInt16(6),
                                        Segmentos = reader.GetInt16(7),
                                        Framerate = reader.GetInt32(8),
                                        Transcode = reader.GetInt32(9),
                                        Resolucion = reader.GetString(10),
                                        Bitrate = reader.GetString(11)
                                    };
                                    
                                    // Cache the result for future requests
                                    StreamCacheHelper.SetCachedStream(chnId, stream);
                                }
                                else
                                {
                                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                                }
                            }
                        }
                    }
                }

                // 🔍 Check real-time status to get accurate process information
                var realTimeStatus = await StreamStatusHelper.GetRealTimeStreamStatusAsync(chnId);
                if (realTimeStatus.IsRunning && realTimeStatus.ProcessId.HasValue)
                {
                    // Update the cached stream with real-time process ID
                    stream.ProcesoId = realTimeStatus.ProcessId.Value;
                }
                else if (stream.ProcesoId > 0)
                {
                    stream.ProcesoId = -1; // Mark as not running
                }

                if (stream != null)
                {
                    // 🔄 First, ensure any existing processes are stopped (non-blocking)
                    if (stream.ProcesoId > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ Error al detener procesos para stream {chnId}: {ex.Message}");
                            }
                        });
                    }
                    
                    // 🚀 Start the stream using the proper service (non-blocking)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Jobs.IniciarStream(stream);
                            
                            // 🔍 Verify the stream is working (with delay to allow startup)
                            await Task.Delay(TimeSpan.FromSeconds(3));
                            await Jobs.VerificarStream(stream);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Error en gestión de procesos para stream {chnId}: {ex.Message}");
                        }
                    });
                    
                    // 💾 Update database in parallel for better performance
                    var updateTasks = new List<Task>();
                    
                    // Task 1: Update streams_info table
                    updateTasks.Add(Task.Run(async () =>
                    {
                        using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await cnn.OpenAsync();
                        
                        using var cmd = cnn.CreateCommand();
                        cmd.CommandText = "UPDATE `streams_info` " +
                                          "SET iniciado=1, ejecutando=1, reportado_caido=0 " +
                                          "WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Task 2: Update streams_tl table
                    updateTasks.Add(Task.Run(async () =>
                    {
                        using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await cnn.OpenAsync();
                        
                        using var cmd = cnn.CreateCommand();
                        cmd.CommandText = "UPDATE `streams_tl` " +
                                          "SET habilitado=1 " +
                                          "WHERE id=@stream_id; ";
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Wait for both database updates to complete
                    await Task.WhenAll(updateTasks);
                    
                    // Invalidate cache since stream configuration changed
                    StreamCacheHelper.InvalidateStream(chnId);
                    
                    var duration = DateTime.UtcNow - startTime;
                    Console.WriteLine($"✅ Stream {chnId} iniciado en {duration.TotalMilliseconds:F0}ms");
                    return Ok(new { success = true, message = $"Stream {chnId} iniciado exitosamente", duration = duration.TotalMilliseconds });
                }
                else
                {
                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                }
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Console.WriteLine($"❌ Error al iniciar stream {chnId} después de {duration.TotalMilliseconds:F0}ms: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"Error interno: {ex.Message}", duration = duration.TotalMilliseconds });
            }
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> EliminarStream(string usuario, string password, int chnId)
        {
            var startTime = DateTime.UtcNow;

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                Console.WriteLine($"🗑️ Eliminando stream {chnId}...");
                
                // 🔍 Check cache first for better performance
                StreamDb? stream = StreamCacheHelper.GetCachedStream(chnId);
                
                if (stream == null)
                {
                    // Cache miss - load from database
                    using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                    {
                        await cnn.OpenAsync();
                        
                        using (var cmd = cnn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                            "INNER JOIN streams_info b " +
                                            "on a.id = b.stream_id " +
                                            $"WHERE stream_id = {chnId};";

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    stream = new StreamDb
                                    {
                                        Fuente = reader.GetString(0),
                                        StreamId = reader.GetInt32(1),
                                        ProbeSize = reader.GetInt32(2),
                                        EsBajoDemanda = reader.GetInt32(3),
                                        ProcesoId = reader.GetInt32(4),
                                        TranscodeAudio = reader.GetString(5),
                                        Intervalo = reader.GetInt16(6),
                                        Segmentos = reader.GetInt16(7),
                                        Framerate = reader.GetInt32(8),
                                        Transcode = reader.GetInt32(9),
                                        Resolucion = reader.GetString(10),
                                        Bitrate = reader.GetString(11)
                                    };
                                }
                                else
                                {
                                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                                }
                            }
                        }
                    }
                }

                // 🔍 Check real-time status to get accurate process information
                var realTimeStatus = await StreamStatusHelper.GetRealTimeStreamStatusAsync(chnId);
                if (realTimeStatus.IsRunning && realTimeStatus.ProcessId.HasValue)
                {
                    // Update the cached stream with real-time process ID
                    stream.ProcesoId = realTimeStatus.ProcessId.Value;
                }
                else if (stream.ProcesoId > 0)
                {
                    stream.ProcesoId = -1; // Mark as not running
                }

                if (stream != null)
                {
                    // 🛑 Stop processes first (non-blocking)
                    if (stream.ProcesoId > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ Error al detener procesos para stream {chnId}: {ex.Message}");
                            }
                        });
                    }
                    
                    // 💾 Delete from database in parallel for better performance
                    var deleteTasks = new List<Task>();
                    
                    // Task 1: Delete from streams_info table
                    deleteTasks.Add(Task.Run(async () =>
                    {
                        using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await cnn.OpenAsync();
                        
                        using var cmd = cnn.CreateCommand();
                        cmd.CommandText = "DELETE FROM streams_info WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Task 2: Delete from streams_tl table
                    deleteTasks.Add(Task.Run(async () =>
                    {
                        using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                        await cnn.OpenAsync();
                        
                        using var cmd = cnn.CreateCommand();
                        cmd.CommandText = "DELETE FROM streams_tl WHERE id=@stream_id; ";
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }));

                    // Wait for both database deletions to complete
                    await Task.WhenAll(deleteTasks);
                    
                    // Invalidate cache since stream is deleted
                    StreamCacheHelper.InvalidateStream(chnId);
                    
                    var duration = DateTime.UtcNow - startTime;
                    Console.WriteLine($"✅ Stream {chnId} eliminado en {duration.TotalMilliseconds:F0}ms");
                    return Ok(new { success = true, message = $"Stream {chnId} eliminado exitosamente", duration = duration.TotalMilliseconds });
                }
                else
                {
                    return NotFound(new { success = false, message = $"Stream {chnId} no encontrado" });
                }
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Console.WriteLine($"❌ Error al eliminar stream {chnId} después de {duration.TotalMilliseconds:F0}ms: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"Error interno: {ex.Message}", duration = duration.TotalMilliseconds });
            }
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> EliminarPelicula(string usuario, string password, int chnId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM streams_tl " +
                                      "WHERE id=@stream_id; ";
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM pelicula_info " +
                                      "WHERE stream_id=@stream_id; ";

                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpGet("{usuario}/{password}/{serieId}")]
        public async Task<IActionResult> EliminarSerie(string usuario, string password, int serieId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM series_info " +
                                      "WHERE id=@serieId; ";
                        cmd.Parameters.AddWithValue("@serieId", serieId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> EliminarEpisodio(string usuario, string password, int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM series_episodios " +
                                      "WHERE stream_id=@chnId; ";
                        cmd.Parameters.AddWithValue("@chnId", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "DELETE FROM streams_tl " +
                                      "WHERE id=@chnId; ";
                        cmd.Parameters.AddWithValue("@chnId", chnId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerStreamsCaidos(string usuario, string password)
        {
            List<DataStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,reportado_caido, habilitado, iniciado, canal_epg FROM streams_tl a INNER JOIN " +
                                                              "streams_info b " +
                                                              "ON a.id = b.stream_id INNER JOIN " +
                                                              "stream_categories c " +
                                                              "ON a.id_categoria = c.id and tipo=1 WHERE reportado_caido=1 and iniciado=1 and habilitado=1 AND a.es_bajodemanda=0 order by orden;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new DataStream
                                {
                                    Id = reader.GetInt32(0),
                                    Imagen = reader.GetString(1),
                                    Nombre = reader.GetString(2),
                                    Categoria = reader.GetString(3),
                                    Fuente = reader.GetString(4),
                                    Ejecutando = reader.GetInt32(5),
                                    Habilitado = reader.GetInt32(6),
                                    Iniciado = reader.GetInt32(7),
                                    CanalEPG = reader.GetString(8)
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(streams);
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> ReiniciarStream(string usuario, string password, int chnId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                        "INNER JOIN streams_info b " +
                                        "on a.id = b.stream_id " +
                                        $"WHERE stream_id = {chnId};";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new StreamDb
                                {
                                    Fuente = reader.GetString(0),
                                    StreamId = reader.GetInt32(1),
                                    ProbeSize = reader.GetInt32(2),
                                    EsBajoDemanda = reader.GetInt32(3),
                                    ProcesoId = reader.GetInt32(4),
                                    TranscodeAudio = reader.GetString(5),
                                    Intervalo = reader.GetInt16(6),
                                    Segmentos = reader.GetInt16(7),
                                    Framerate = reader.GetInt32(8),
                                    Transcode = reader.GetInt32(9),
                                    Resolucion = reader.GetString(10),
                                    Bitrate = reader.GetString(11)
                                });
                            }
                    }

                    var stream = streams.FirstOrDefault();

                    if (stream != null)
                    {
                        await Jobs.ReiniciarStream(stream);
                        await Task.Delay(200);
                        await Jobs.IniciarStream(stream);
                        await Task.Delay(400);
                        await Jobs.VerificarStream(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(categorias);
        }


        [HttpGet("{usuario}/{password}/")]
        public async Task<IActionResult> ReiniciarStreamsCaidos(string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                        "INNER JOIN streams_info b " +
                                        "on a.id = b.stream_id " +
                                        $"WHERE reportado_caido=1 and habilitado=1 and iniciado=1;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new StreamDb
                                {
                                    Fuente = reader.GetString(0),
                                    StreamId = reader.GetInt32(1),
                                    ProbeSize = reader.GetInt32(2),
                                    EsBajoDemanda = reader.GetInt32(3),
                                    ProcesoId = reader.GetInt32(4),
                                    TranscodeAudio = reader.GetString(5),
                                    Intervalo = reader.GetInt16(6),
                                    Segmentos = reader.GetInt16(7),
                                    Framerate = reader.GetInt32(8),
                                    Transcode = reader.GetInt32(9),
                                    Resolucion = reader.GetString(10),
                                    Bitrate = reader.GetString(11)
                                });
                            }
                    }
                    foreach (var stream in streams)
                    {
                        if (stream != null)
                        {
                            await Jobs.DetenerProceso(0, stream.ProcesoId);
                            await Task.Delay(100);
                            await Jobs.IniciarStream(stream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        #region Peliculas
        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerCategoriasPeliculas(string usuario, string password)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        cmd.CommandText = "select id,category_name from stream_categories " +
                                          "where category_type = 'movie';";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                categorias.Add(new PanelCategoria
                                {
                                    Id = reader.GetInt32(0),
                                    Texto = reader.GetString(1),
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(categorias);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerUsuariosLinea(string usuario, string password)
        {
            List<PanelUsuariosEnLinea> usuarioEnLinea = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select usuario,notas,nombre_stream,a.stream_id from actividad_usuario_actualmente a " +
                                            "inner join usuarios_ticolinea b " +
                                            "on a.usuario_id = b.id " +
                                            "inner join streams_tl c " +
                                            "on a.stream_id = c.id; ";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                usuarioEnLinea.Add(new PanelUsuariosEnLinea
                                {
                                    Usuario = reader.GetString(0),
                                    Canal = reader.GetString(1),
                                    Notas = $"{reader.GetInt32(3)}-{reader.GetString(2)}",
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(usuarioEnLinea);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerPeliculas(string usuario, string password)
        {
            List<PanelMovies> movies = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,habilitado FROM streams_tl a " +
                                                              "INNER JOIN " +
                                                              "stream_categories c " +
                                                              "ON a.id_categoria = c.id and tipo=2 order by orden;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                movies.Add(new PanelMovies
                                {
                                    Id = reader.GetInt32(0),
                                    Imagen = reader.GetString(1),
                                    Nombre = reader.GetString(2),
                                    Categoria = reader.GetString(3),
                                    Fuente = reader.GetString(4),
                                    Habilitado = reader.GetInt32(5),
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(movies);
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> ObtenerPelicula(string usuario, string password, int chnId)
        {
            List<PanelPelicula> pelicula = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.nombre_stream,d.anno,a.id_categoria,d.duracion,a.habilitado,d.PG,d.resena,a.imagen_stream,a.fuente_stream FROM streams_tl a " +
                                                              "INNER JOIN " +
                                                              "stream_categories c ON a.id_categoria = c.id and tipo=2 " +
                                                              "lEFT JOIN " +
                                                              "pelicula_info d " +
                                                              " ON a.id=d.stream_id " +
                                                              $"WHERE a.id={chnId};";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                pelicula.Add(new PanelPelicula
                                {
                                    NombrePelicula = reader.GetString(0),
                                    Anno = reader.GetString(1),
                                    Categoria = reader.GetInt32(2),
                                    Duracion = reader.GetString(3),
                                    Habilitado = reader.GetInt16(4),
                                    PG = reader.GetString(5),
                                    Resena = reader.GetString(6),
                                    UrlLogo = reader.GetString(7),
                                    UrlPelicula = reader.GetString(8)
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(pelicula.FirstOrDefault());
        }

        [HttpGet("{usuario}/{password}")]
        public IActionResult ObtenerArchivosPeliculas(string usuario, string password)
        {
            List<string> filePaths = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                filePaths = Directory.GetFiles(Constantes.Global.MOVIES_RAW, "*.*",
                                          SearchOption.TopDirectoryOnly)
                                     .Where(file => file.ToLower().EndsWith("mkv") || file.ToLower().EndsWith("mp4"))
                                     .OrderBy(d => new FileInfo(d).Name).ToList();


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(filePaths);
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarPelicula([FromBody] PanelPelicula panelPelicula, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                string nombreArchivo = RemoveSpecialCharacters(panelPelicula.NombrePelicula.Trim());
                string ext = panelPelicula.UrlPelicula.Split('.').ToList().Last();
                if (!string.IsNullOrEmpty(ext))
                {
                    ext = ext.ToLower();
                }
                else
                {
                    throw new Exception($"Extensión {ext} no valida");
                }
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    int maxId = 0;
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select max(id) from streams_tl;";
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                maxId = reader.GetInt32(0);
                            }
                    }
                    if (maxId == 0) throw new Exception("Error al obtener maxID");

                    //Convierte la película a un formato compatible para caja
                    Process process = new();
                    process.StartInfo.FileName = Constantes.Global.FFMPEG_PATH;

                    //string ffmpegOutput = $"-codec copy -c:a aac -b:a 128k -map 0 -threads 2";
                    string ffmpegOutput = "-c:v libx264 -b:v 5M -maxrate:v 5M -minrate:v 5M -bufsize:v 10M -crf 23 -preset veryfast -g 48 -sc_threshold 0 -keyint_min 48 -c:a aac -b:a 96k -ac 2 -c:s copy -map 0 -movflags faststart  -map 0 -threads 4";
                    //string ffmpegOutput = $"-c copy {pixFmt} -analyzeduration [PROBESIZE] -probesize [PROBESIZE]{transcodeAudio} -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -sc_threshold 0 -hls_segment_filename";

                    process.StartInfo.Arguments = $"-y -i \"{panelPelicula.UrlPelicula}\" -progress {Constantes.Global.MOVIES_FOLDER}{nombreArchivo}_progress.txt {ffmpegOutput} \"{Constantes.Global.MOVIES_FOLDER}{nombreArchivo}.{ext}\" ";
                    process.Start();

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "INSERT INTO `streams_tl` " +
                                      "(`id`,`id_categoria`,`nombre_stream`,`fuente_stream`,`imagen_stream`,`orden`,`agregado`,`probesize_ondemand`,`es_bajodemanda`,`tipo`,`contenedor`,`habilitado`,`transcode_audio`,`video_info`," +
                                      "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`) " +
                                      "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,0,2,@contenedor,@habilitado,'',@process, " +
                                      "'',0,0,0,0,0,'',''); ";

                        cmd.Parameters.AddWithValue("@id", maxId + 1);
                        cmd.Parameters.AddWithValue("@id_categoria", panelPelicula.Categoria);
                        cmd.Parameters.AddWithValue("@nombre_stream", panelPelicula.NombrePelicula);
                        cmd.Parameters.AddWithValue("@fuente_stream", $"{Constantes.Global.MOVIES_FOLDER}{nombreArchivo}.{ext}");
                        cmd.Parameters.AddWithValue("@imagen_stream", panelPelicula.UrlLogo);
                        cmd.Parameters.AddWithValue("@orden", maxId + 1);
                        cmd.Parameters.AddWithValue("@agregado", now);
                        cmd.Parameters.AddWithValue("@habilitado", panelPelicula.Habilitado);
                        cmd.Parameters.AddWithValue("@contenedor", ext);
                        cmd.Parameters.AddWithValue("@process", process.Id.ToString());

                        await cmd.ExecuteNonQueryAsync();
                    }

                    //Agregar info pelicula
                    using (var cmdInfo = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdInfo.CommandText = "INSERT INTO `pelicula_info` " +
                                          "(anno,resena,PG,stream_id,duracion) " +
                                          "VALUES(@anno,@resena,@PG,@stream_id,@duracion); ";
                        cmdInfo.Parameters.AddWithValue("@anno", panelPelicula.Anno);
                        cmdInfo.Parameters.AddWithValue("@resena", panelPelicula.Resena);
                        cmdInfo.Parameters.AddWithValue("@PG", panelPelicula.PG);
                        cmdInfo.Parameters.AddWithValue("@stream_id", maxId + 1);
                        cmdInfo.Parameters.AddWithValue("@duracion", panelPelicula.Duracion);

                        await cmdInfo.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> AgregarInfoPelicula([FromBody] PanelInfoPelicula panelInfoPelicula, string usuario, string password, int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                        cmd.CommandText = "INSERT INTO `pelicula_info` " +
                                          "(anno,resena,PG,stream_id,duracion) " +
                                          "VALUES(@anno,@resena,@PG,@stream_id,@duracion); ";
                        cmd.Parameters.AddWithValue("@anno", panelInfoPelicula.Anno);
                        cmd.Parameters.AddWithValue("@resena", panelInfoPelicula.Resena);
                        cmd.Parameters.AddWithValue("@PG", panelInfoPelicula.PG);
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        cmd.Parameters.AddWithValue("@duracion", panelInfoPelicula.Duracion);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}/{chnId}")]
        public async Task<IActionResult> ActualizarPelicula([FromBody] PanelPelicula panelPelicula, string usuario, string password, int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {

                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        cmd.CommandText = "UPDATE `streams_tl` " +
                                          "SET id_categoria=@id_categoria,nombre_stream=@nombre_stream,imagen_stream=@imagen_stream,habilitado=@habilitado " +
                                          "WHERE id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.Parameters.AddWithValue("@id_categoria", panelPelicula.Categoria);
                        cmd.Parameters.AddWithValue("@nombre_stream", panelPelicula.NombrePelicula);
                        cmd.Parameters.AddWithValue("@imagen_stream", panelPelicula.UrlLogo);
                        cmd.Parameters.AddWithValue("@habilitado", panelPelicula.Habilitado);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                        cmd.CommandText = "UPDATE `pelicula_info` " +
                                          "SET anno=@anno,resena=@resena,pg=@pg,duracion=@duracion " +
                                          "WHERE stream_id=@id; ";

                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.Parameters.AddWithValue("@anno", panelPelicula.Anno);
                        cmd.Parameters.AddWithValue("@resena", panelPelicula.Resena);
                        cmd.Parameters.AddWithValue("@pg", panelPelicula.PG);
                        cmd.Parameters.AddWithValue("@duracion", panelPelicula.Duracion);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }
        #endregion

        #region Series
        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarEpisodio([FromBody] PanelEpisodioInfo panelSerie, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                string nombreArchivo = RemoveSpecialCharacters(panelSerie.Titulo.Trim());
                string ext = panelSerie.URLSerie.Split('.').ToList().Last();
                if (!string.IsNullOrEmpty(ext))
                {
                    ext = ext.ToLower();
                }
                else
                {
                    throw new Exception($"Extensión {ext} no valida");
                }
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    int maxId = 0;
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select max(id) from streams_tl;";
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                maxId = reader.GetInt32(0);
                            }
                    }

                    if (maxId == 0) throw new Exception("Error al obtener maxID");

                    //Convierte la película a un formato compatible para caja
                    Process process = new();
                    process.StartInfo.FileName = Constantes.Global.FFMPEG_PATH;

                    string ffmpegOutput = "-c:v libx264 -x264-params \"nal-hrd=cbr:force-cfr=1\" -b:v 5M -maxrate:v 5M -minrate:v 5M -bufsize:v 10M -crf 23 -preset veryfast -g 48 -sc_threshold 0 -keyint_min 48 -c:a aac -b:a 96k -ac 2 -c:s copy -map 0 -movflags faststart";

                    var nombreSerie = $"{Constantes.Global.SERIES_FOLDER}{panelSerie.SerieId}.{panelSerie.EpisodioNumero}.{nombreArchivo}.{ext}";
                    process.StartInfo.Arguments = $"-y -loglevel quiet -err_detect ignore_err -nostdin -nostats -i \"{panelSerie.URLSerie}\" -progress {Constantes.Global.SERIES_FOLDER}{panelSerie.SerieId}.{panelSerie.EpisodioNumero}.{nombreArchivo}_progress.txt {ffmpegOutput} \"{nombreSerie}\" ";
                    process.Start();
                    process.StartInfo.RedirectStandardInput = true;
                    process.StartInfo.RedirectStandardOutput = true;

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "INSERT INTO `streams_tl` " +
                                      "(`id`,`id_categoria`,`nombre_stream`,`fuente_stream`,`imagen_stream`,`orden`,`agregado`,`probesize_ondemand`,`es_bajodemanda`,`tipo`,`contenedor`,`habilitado`,`transcode_audio`,`video_info`," +
                                      "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`) " +
                                      "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,0,3,@contenedor,@habilitado,'',@process, " +
                                      "'',0,0,0,0,0,'',''); ";

                        cmd.Parameters.AddWithValue("@id", maxId + 1);
                        cmd.Parameters.AddWithValue("@id_categoria", panelSerie.Categoria);
                        cmd.Parameters.AddWithValue("@nombre_stream", panelSerie.Titulo);
                        cmd.Parameters.AddWithValue("@fuente_stream", $"{nombreSerie}");
                        cmd.Parameters.AddWithValue("@imagen_stream", panelSerie.URLCaratula);
                        cmd.Parameters.AddWithValue("@orden", maxId + 1);
                        cmd.Parameters.AddWithValue("@agregado", now);
                        cmd.Parameters.AddWithValue("@habilitado", panelSerie.Habilitado);
                        cmd.Parameters.AddWithValue("@contenedor", ext);
                        cmd.Parameters.AddWithValue("@process", process.Id.ToString());

                        await cmd.ExecuteNonQueryAsync();
                    }

                    //Agregar info pelicula
                    using (var cmdInfo = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdInfo.CommandText = "INSERT INTO `series_episodios` " +
                                          "(episodio_num,serie_id,stream_id,orden,resena,temporada_num,server_path) " +
                                          "VALUES(@episodioNum,@serieId,@stream_id,@episodioNum,@resena,@temporadaNum,@serverPath); ";

                        cmdInfo.Parameters.AddWithValue("@episodioNum", panelSerie.EpisodioNumero);
                        cmdInfo.Parameters.AddWithValue("@serieId", panelSerie.SerieId);
                        cmdInfo.Parameters.AddWithValue("@stream_id", maxId + 1);
                        cmdInfo.Parameters.AddWithValue("@resena", panelSerie.Resena);
                        cmdInfo.Parameters.AddWithValue("@temporadaNum", panelSerie.TemporadaNum);
                        cmdInfo.Parameters.AddWithValue("@serverPath", panelSerie.URLSerie);

                        await cmdInfo.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> ActualizarEpisodio([FromBody] PanelEpisodioInfo panelSerie, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "UPDATE `streams_tl` " +
                                          "SET nombre_stream=@nombre_stream,imagen_stream=@imagen_stream,habilitado=@habilitado " +
                                          "WHERE id=@id; ";

                        cmd.Parameters.AddWithValue("@id", panelSerie.StreamId);
                        cmd.Parameters.AddWithValue("@nombre_stream", panelSerie.Titulo);
                        cmd.Parameters.AddWithValue("@imagen_stream", panelSerie.URLCaratula);
                        cmd.Parameters.AddWithValue("@habilitado", panelSerie.Habilitado);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    //Agregar info pelicula
                    using (var cmdInfo = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdInfo.CommandText = "UPDATE series_episodios " +
                                          " SET episodio_num=@episodio_num,serie_id=@serie_id, resena=@resena " +
                                          "WHERE stream_id=@id; ";

                        cmdInfo.Parameters.AddWithValue("@id", panelSerie.StreamId);
                        cmdInfo.Parameters.AddWithValue("@episodio_num", panelSerie.EpisodioNumero);
                        cmdInfo.Parameters.AddWithValue("@serie_id", panelSerie.SerieId);
                        cmdInfo.Parameters.AddWithValue("@resena", panelSerie.Resena);

                        await cmdInfo.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> AgregarSerie([FromBody] PanelSerieInfo panelSerie, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    //Agregar info pelicula
                    using (var cmdInfo = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdInfo.CommandText = "INSERT INTO `series_info` " +
                                          "(caratula,caratula_grande,genero,director,fechaLanzamiento,temporadas,youtube_trailer,titulo,categoria_id,rating,resena) " +
                                          "VALUES(@caratula,@caratula_grande,@genero,@director,@fechaLanzamiento,@temporadas,@youtube_trailer,@titulo,@categoria_id,@rating,@resena); ";
                        cmdInfo.Parameters.AddWithValue("@caratula", panelSerie.URLCaratula);
                        cmdInfo.Parameters.AddWithValue("@caratula_grande", panelSerie.URLCaratulaGrande);
                        cmdInfo.Parameters.AddWithValue("@genero", panelSerie.Genero);
                        cmdInfo.Parameters.AddWithValue("@director", panelSerie.Director);
                        cmdInfo.Parameters.AddWithValue("@fechaLanzamiento", panelSerie.FechaLanzamiento);
                        cmdInfo.Parameters.AddWithValue("@temporadas", panelSerie.Temporadas);
                        cmdInfo.Parameters.AddWithValue("@youtube_trailer", panelSerie.URLYoutube);
                        cmdInfo.Parameters.AddWithValue("@titulo", panelSerie.Titulo);
                        cmdInfo.Parameters.AddWithValue("@categoria_id", panelSerie.Categoria);
                        cmdInfo.Parameters.AddWithValue("@rating", panelSerie.Rating);
                        cmdInfo.Parameters.AddWithValue("@resena", panelSerie.Resena);
                        cmdInfo.Parameters.AddWithValue("@habilitado", panelSerie.Resena);

                        await cmdInfo.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok();
        }

        [HttpPost("{usuario}/{password}")]
        public async Task<IActionResult> ActualizarSerie([FromBody] PanelSerieInfo panelSerie, string usuario, string password)
        {
            string error;
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    //Agregar info pelicula
                    using (var cmdInfo = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmdInfo.CommandText = "UPDATE `series_info` " +
                                          "SET caratula=@caratula,caratula_grande=@caratula_grande,genero=@genero,director=@director,fechaLanzamiento=@fechaLanzamiento,temporadas=@temporadas,youtube_trailer=@youtube_trailer,titulo=@titulo,categoria_id=@categoria_id,rating=@rating " +
                                          "WHERE id=@id; ";

                        cmdInfo.Parameters.AddWithValue("@id", panelSerie.Id);
                        cmdInfo.Parameters.AddWithValue("@caratula", panelSerie.URLCaratula);
                        cmdInfo.Parameters.AddWithValue("@caratula_grande", panelSerie.URLCaratulaGrande);
                        cmdInfo.Parameters.AddWithValue("@genero", panelSerie.Genero);
                        cmdInfo.Parameters.AddWithValue("@director", panelSerie.Director);
                        cmdInfo.Parameters.AddWithValue("@fechaLanzamiento", panelSerie.FechaLanzamiento);
                        cmdInfo.Parameters.AddWithValue("@temporadas", panelSerie.Temporadas);
                        cmdInfo.Parameters.AddWithValue("@youtube_trailer", panelSerie.URLYoutube);
                        cmdInfo.Parameters.AddWithValue("@titulo", panelSerie.Titulo);
                        cmdInfo.Parameters.AddWithValue("@categoria_id", panelSerie.Categoria);
                        cmdInfo.Parameters.AddWithValue("@rating", panelSerie.Rating);

                        await cmdInfo.ExecuteNonQueryAsync();
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Ok("ERROR:" + ex.GetBaseException().Message);
            }

            return Ok();
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerSeries(string usuario, string password)
        {
            List<PanelSerieInfo> movies = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT id, caratula, genero, fechaLanzamiento, temporadas, categoria_id, Rating, resena, titulo, habilitado " +
                                          "FROM series_info order by titulo;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                movies.Add(new PanelSerieInfo
                                {
                                    Id = reader.GetInt32(0),
                                    URLCaratula = reader.GetString(1),
                                    Genero = reader.GetString(2),
                                    FechaLanzamiento = reader.GetString(3),
                                    Temporadas = reader.GetString(4),
                                    Categoria = reader.GetInt32(5),
                                    Rating = reader.GetString(6),
                                    Resena = reader.GetString(7),
                                    Titulo = reader.GetString(8),
                                    Habilitado = reader.GetInt32(9),
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(movies);
        }

        [HttpGet("{usuario}/{password}/{serieId}")]
        public async Task<IActionResult> ObtenerInfoSerie(string usuario, string password, int serieId)
        {
            List<PanelSerieInfo> movies = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT id, caratula, genero, fechaLanzamiento, temporadas, categoria_id, Rating, resena, titulo, habilitado " +
                                          $"FROM series_info WHERE id={serieId} order by titulo;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                movies.Add(new PanelSerieInfo
                                {
                                    Id = reader.GetInt32(0),
                                    URLCaratula = reader.GetString(1),
                                    Genero = reader.GetString(2),
                                    FechaLanzamiento = reader.GetString(3),
                                    Temporadas = reader.GetString(4),
                                    Categoria = reader.GetInt32(5),
                                    Rating = reader.GetString(6),
                                    Resena = reader.GetString(7),
                                    Titulo = reader.GetString(8),
                                    Habilitado = reader.GetInt32(9),
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(movies?.FirstOrDefault());
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerCategoriasSeries(string usuario, string password)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "select id,category_name from stream_categories " +
                                          "where category_type = 'serie';";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                categorias.Add(new PanelCategoria
                                {
                                    Id = reader.GetInt32(0),
                                    Texto = reader.GetString(1),
                                });
                            }
                    }
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(categorias);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerEpisodios(string usuario, string password, int serieId)
        {
            List<PanelEpisodio> episodios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.id, episodio_num, serie_id, a.stream_id, a.orden, resena, temporada_num, b.nombre_stream, b.imagen_stream, b.habilitado " +
                                          $" FROM series_episodios a INNER JOIN streams_tl b " +
                                          $" ON a.stream_id=b.id " +
                                          $" WHERE serie_id={serieId};";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                episodios.Add(new PanelEpisodio
                                {
                                    Id = reader.GetInt32(0),
                                    EpisodioNum = reader.GetInt32(1),
                                    SerieId = reader.GetInt32(2),
                                    StreamId = reader.GetInt32(3),
                                    Orden = reader.GetInt32(4),
                                    Resena = reader.GetString(5),
                                    TemporadaNum = reader.GetInt32(6),
                                    Nombre = reader.GetString(7),
                                    Imagen = reader.GetString(8),
                                    Habilitado = reader.GetInt32(9),
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(episodios);
        }

        [HttpGet("{usuario}/{password}/{serieId}/{episodioId}")]
        public async Task<IActionResult> ObtenerInfoEpisodio(string usuario, string password, int serieId, int episodioId)
        {
            List<PanelEpisodio> episodios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT a.id, episodio_num, serie_id, a.stream_id, a.orden, resena, temporada_num, b.nombre_stream, b.imagen_stream, b.habilitado " +
                                          $" FROM series_episodios a INNER JOIN streams_tl b " +
                                          $" ON a.stream_id=b.id " +
                                          $" WHERE serie_id={serieId} and a.id={episodioId} ;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                episodios.Add(new PanelEpisodio
                                {
                                    Id = reader.GetInt32(0),
                                    EpisodioNum = reader.GetInt32(1),
                                    SerieId = reader.GetInt32(2),
                                    StreamId = reader.GetInt32(3),
                                    Orden = reader.GetInt32(4),
                                    Resena = reader.GetString(5),
                                    TemporadaNum = reader.GetInt32(6),
                                    Nombre = reader.GetString(7),
                                    Imagen = reader.GetString(8),
                                    Habilitado = reader.GetInt32(9),
                                });
                            }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(episodios?.FirstOrDefault());
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerArchivosSeries(string usuario, string password)
        {
            List<string> filePaths = new();
            List<string> processedServerPaths = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                filePaths = Directory.GetFiles(Constantes.Global.MOVIES_RAW + "series/", "*.*",
                                          SearchOption.TopDirectoryOnly)
                                      .Where(file => file.ToLower().EndsWith("mkv") || file.ToLower().EndsWith("mp4"))
                                      .OrderBy(d => new FileInfo(d).Name).ToList();

                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        cmd.CommandText = "SELECT server_path " +
                                          $" FROM series_episodios";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                string serverPath = reader.IsDBNull(0) ? "" : reader.GetString(0);
                                processedServerPaths.Add(serverPath);
                            }
                    }
                }

                filePaths = filePaths.Except(processedServerPaths).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(filePaths);
        }

        #endregion

        public static string UnixTimeStampToDateTime(double unixTimeStamp)
        {
            if (unixTimeStamp == 0) return "";

            // Unix timestamp is seconds past epoch
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dateTime.ToString("dd/MM/yyyy");
        }

        private static string ObtenerHost(string fuente)
        {
            if (!fuente.Contains("://")) return fuente;

            System.Uri url = new System.Uri(fuente);
            return url.Host;
        }

        public static string RemoveSpecialCharacters(string str)
        {
            return System.Text.RegularExpressions.Regex.Replace(str, @"[^0-9a-zA-Z\._]", string.Empty);
        }
    }
}
