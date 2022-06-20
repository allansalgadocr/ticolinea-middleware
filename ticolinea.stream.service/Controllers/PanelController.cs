using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("panel/api/[controller]/[action]")]
    [ApiController]
    public class PanelController : ControllerBase
    {

        [HttpGet("{usuario}/{password}")]
        public IActionResult ObtenerStreams(string usuario, string password)
        {
            List<DataStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,reportado_caido, habilitado, iniciado, canal_epg FROM streams_tl a INNER JOIN " +
                                                          "streams_info b " +
                                                          "ON a.id = b.stream_id INNER JOIN " +
                                                          "stream_categories c " +
                                                          "ON a.id_categoria = c.id and tipo=1 order by orden;";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(streams);
        }

        [HttpGet("{usuario}/{password}/{chnId}")]
        public IActionResult ObtenerStream(string usuario, string password, int chnId)
        {
            List<PanelStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select nombre_stream,fuente_stream,imagen_stream,id_categoria,es_bajodemanda,transcode,habilitado, canal_epg from streams_tl " +
                                      "where id=@id;";
                    cmd.Parameters.AddWithValue("@id", chnId);

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                                CanalEPG = reader.GetString(7)
                            });
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
        public IActionResult AgregarStream([FromBody] PanelStream panelStream, string usuario, string password)
        {
            List<DataStream> streams = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    int maxId = 0;
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select max(id) from streams_tl;";
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            maxId = reader.GetInt32(0);
                        }

                    if (maxId == 0) throw new Exception("Error al obtener maxID");

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    cmd.CommandText = "INSERT INTO `streams_tl` " +
                                      "(`id`,`id_categoria`,`nombre_stream`,`fuente_stream`,`imagen_stream`,`orden`,`agregado`,`probesize_ondemand`,`es_bajodemanda`,`tipo`,`contenedor`,`habilitado`,`transcode_audio`,`video_info`," +
                                      "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`,`canal_epg`) " +
                                      "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,@es_bajodemanda,1,'',@habilitado,'aac','', " +
                                      "'',6,5,0,25,@transcode,'','1500k', @canal_epg); ";
                    cmd.Parameters.AddWithValue("@id", maxId + 1);
                    cmd.Parameters.AddWithValue("@id_categoria", panelStream.Categoria);
                    cmd.Parameters.AddWithValue("@nombre_stream", panelStream.NombreStream);
                    cmd.Parameters.AddWithValue("@fuente_stream", panelStream.UrlStream);
                    cmd.Parameters.AddWithValue("@imagen_stream", panelStream.UrlLogo);
                    cmd.Parameters.AddWithValue("@orden", maxId + 1);
                    cmd.Parameters.AddWithValue("@agregado", now);
                    cmd.Parameters.AddWithValue("@es_bajodemanda", panelStream.EsBajoDemanda);
                    cmd.Parameters.AddWithValue("@transcode", panelStream.Optimizar);
                    cmd.Parameters.AddWithValue("@habilitado", panelStream.Habilitado);
                    cmd.Parameters.AddWithValue("@canal_epg", panelStream.CanalEPG);

                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "INSERT INTO `streams_info` " +
                                      "(`stream_id`,`ejecutando`,`proceso_id`,`info_progreso`,`iniciado`) " +
                                      "VALUES(@stream_id,1,-1,'',1);";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@stream_id", maxId + 1);
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE stream_id = {maxId + 1};";

                    List<StreamDb> streamsdb = new();
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            streamsdb.Add(new StreamDb
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
                    var stream = streamsdb.FirstOrDefault();

                    if (stream != null)
                    {
                        Jobs.ReiniciarStream(stream);
                        Jobs.VerificarStream(stream);
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

        [HttpPost("{usuario}/{password}")]
        public IActionResult AgregarUsuario([FromBody] PanelUsuario panelUsuario, string usuario, string password)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();

                    long fechaVencimiento = 0;
                    int.TryParse(panelUsuario.FechaVencimiento, out int meses);
                    if (meses > 0)
                        fechaVencimiento = DateTimeOffset.Now.AddMonths(meses).ToUnixTimeSeconds();

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    cmd.CommandText = "INSERT INTO `usuarios_ticolinea` " +
                                      "(`usuario`,`clave`,`fecha_vencimiento`,`habilitado`,`conexiones_maximas`,`es_restreamer`,`fecha_creacion`,`creado_por`,`bouquet`,`notas`) " +
                                      "VALUES(@usuario,@clave,@fecha_vencimiento,@habilitado,1,0,@fecha_creacion,0,'',@notas); ";
                    cmd.Parameters.AddWithValue("@usuario", panelUsuario.Usuario);
                    cmd.Parameters.AddWithValue("@clave", panelUsuario.Clave);
                    cmd.Parameters.AddWithValue("@fecha_vencimiento", fechaVencimiento);
                    cmd.Parameters.AddWithValue("@habilitado", panelUsuario.Habilitado);
                    cmd.Parameters.AddWithValue("@fecha_creacion", now);
                    cmd.Parameters.AddWithValue("@notas", panelUsuario.Notas);

                    cmd.ExecuteNonQuery();
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
        public IActionResult ActualizarUsuario([FromBody] PanelUsuario panelUsuario, string usuario, string password, int usuarioId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();

                    long fechaVencimiento = -1;
                    int.TryParse(panelUsuario.FechaVencimiento, out int meses);
                    if (meses > 0)
                        fechaVencimiento = DateTimeOffset.Now.AddMonths(meses).ToUnixTimeSeconds();
                    else if (meses == 0) fechaVencimiento = 0;

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();

                    if (fechaVencimiento == -1)
                    {
                        cmd.CommandText = "UPDATE `usuarios_ticolinea` " +
                                          "SET usuario=@usuario, clave=@clave, habilitado=@habilitado,notas=@notas " +
                                          "WHERE id=@id; ";
                    }
                    else
                        cmd.CommandText = "UPDATE `usuarios_ticolinea` " +
                                          "SET usuario=@usuario, clave=@clave, habilitado=@habilitado, fecha_vencimiento=@fecha_vencimiento,notas=@notas " +
                                          "WHERE id=@id; ";

                    cmd.Parameters.AddWithValue("@usuario", panelUsuario.Usuario);
                    cmd.Parameters.AddWithValue("@clave", panelUsuario.Clave);
                    if (fechaVencimiento > -1)
                        cmd.Parameters.AddWithValue("@fecha_vencimiento", fechaVencimiento);

                    cmd.Parameters.AddWithValue("@habilitado", panelUsuario.Habilitado);
                    cmd.Parameters.AddWithValue("@notas", panelUsuario.Notas);
                    cmd.Parameters.AddWithValue("@id", usuarioId);

                    cmd.ExecuteNonQuery();
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
        public IActionResult ObtenerUsuarios(string usuario, string password)
        {
            List<PanelUsuario> usuarios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT id,usuario,clave,fecha_vencimiento,habilitado,notas FROM usuarios_ticolinea;";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            usuarios.Add(new PanelUsuario
                            {
                                Id = reader.GetInt32(0),
                                Usuario = reader.GetString(1),
                                Clave = reader.GetString(2),
                                FechaVencimiento = !reader.IsDBNull(3) ? UnixTimeStampToDateTime(reader.GetInt32(3)) : "",
                                Habilitado = reader.GetInt32(4),
                                Notas = reader.GetString(5),
                            });
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
        public IActionResult ObtenerProveedores(string usuario, string password)
        {
            List<String> fuentes = new();
            List<Proveedores> proveedores = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select fuente_stream from streams_tl where habilitado=1 and tipo=1;";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(proveedores);
        }

        [HttpGet("{usuario}/{password}/{usuarioId}")]
        public IActionResult ObtenerUsuario(string usuario, string password, int usuarioId)
        {
            List<PanelUsuario> usuarios = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT id,usuario,clave,fecha_vencimiento,habilitado,notas FROM usuarios_ticolinea WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@id", usuarioId);

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            usuarios.Add(new PanelUsuario
                            {
                                Id = reader.GetInt32(0),
                                Usuario = reader.GetString(1),
                                Clave = reader.GetString(2),
                                FechaVencimiento = !reader.IsDBNull(3) ? UnixTimeStampToDateTime(reader.GetInt32(3)) : "",
                                Habilitado = reader.GetInt32(4),
                                Notas = reader.GetString(5)
                            });
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
        public IActionResult ActualizarStream([FromBody] PanelStream panelStream, string usuario, string password, int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();

                    cmd.CommandText = "UPDATE `streams_tl` " +
                                      "SET id_categoria=@id_categoria,nombre_stream=@nombre_stream,fuente_stream=@fuente_stream,imagen_stream=@imagen_stream,es_bajodemanda=@es_bajodemanda,habilitado=@habilitado,transcode=@transcode,canal_epg=@canal_epg " +
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

                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE stream_id = {chnId};";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                    var stream = streams.FirstOrDefault();

                    if (stream != null)
                    {
                        if (panelStream.Habilitado == 0)
                        {
                            Jobs.DetenerProceso(stream.ProcesoId);
                        }
                        else
                        {
                            Jobs.ReiniciarStream(stream);
                            Jobs.VerificarStream(stream);
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

        [HttpGet("{usuario}/{password}")]
        public IActionResult ObtenerCategorias(string usuario, string password)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select id,category_name from stream_categories " +
                                      "where category_type = 'live';";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            categorias.Add(new PanelCategoria
                            {
                                Id = reader.GetInt32(0),
                                Texto = reader.GetString(1),
                            });
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
        public IActionResult DetenerStream(string usuario, string password, int chnId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE stream_id = {chnId};";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                    var stream = streams.FirstOrDefault();

                    if (stream != null)
                    {
                        Jobs.DetenerProceso(stream.ProcesoId);

                        cmd.CommandText = "UPDATE `streams_info` " +
                                      "SET iniciado=0 " +
                                      "WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.ExecuteNonQuery();
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
        public IActionResult IniciarStream(string usuario, string password, int chnId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE stream_id = {chnId};";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                    var stream = streams.FirstOrDefault();

                    if (stream != null)
                    {
                        Jobs.ReiniciarStream(stream);

                        cmd.CommandText = "UPDATE `streams_info` " +
                                      "SET iniciado=1 " +
                                      "WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "UPDATE `streams_tl` " +
                                      "SET habilitado=1 " +
                                      "WHERE id=@stream_id; ";
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        cmd.ExecuteNonQuery();

                        Jobs.VerificarStream(stream);
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
        public IActionResult EliminarStream(string usuario, string password, int chnId)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE stream_id = {chnId};";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                    var stream = streams.FirstOrDefault();

                    if (stream != null)
                    {
                        Jobs.DetenerProceso(stream.ProcesoId);

                        cmd.CommandText = "DELETE FROM streams_info " +
                                      "WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM streams_tl " +
                                      "WHERE id=@stream_id; ";
                        cmd.Parameters.AddWithValue("@stream_id", chnId);
                        cmd.ExecuteNonQuery();
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

        #region Peliculas
        [HttpGet("{usuario}/{password}")]
        public IActionResult ObtenerCategoriasPeliculas(string usuario, string password)
        {
            List<PanelCategoria> categorias = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select id,category_name from stream_categories " +
                                      "where category_type = 'movie';";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            categorias.Add(new PanelCategoria
                            {
                                Id = reader.GetInt32(0),
                                Texto = reader.GetString(1),
                            });
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
        public IActionResult ObtenerUsuariosLinea(string usuario, string password)
        {
            List<PanelUsuariosEnLinea> usuarioEnLinea = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select usuario,notas,nombre_stream from actividad_usuario_actualmente a " +
                                        "inner join usuarios_ticolinea b " +
                                        "on a.usuario_id = b.id " +
                                        "inner join streams_tl c " +
                                        "on a.stream_id = c.id; ";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            usuarioEnLinea.Add(new PanelUsuariosEnLinea
                            {
                                Usuario = reader.GetString(0),
                                Canal = reader.GetString(1),
                                Notas = reader.GetString(2),
                            });
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
        public IActionResult ObtenerPeliculas(string usuario, string password)
        {
            List<PanelMovies> movies = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,habilitado FROM streams_tl a " +
                                                          "INNER JOIN " +
                                                          "stream_categories c " +
                                                          "ON a.id_categoria = c.id and tipo=2 order by orden;";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(movies);
        }

        [HttpGet("{usuario}/{password}")]
        public IActionResult ObtenerArchivosPeliculas(string usuario, string password)
        {
            List<string> filePaths = new();

            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                filePaths = Directory.GetFiles(Constantes.Global.MOVIES_RAW, "*.*",
                                          SearchOption.TopDirectoryOnly).OrderByDescending(d => new FileInfo(d).CreationTime).ToList();


            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500);
            }

            return Ok(filePaths);
        }

        [HttpPost("{usuario}/{password}")]
        public IActionResult AgregarPelicula([FromBody] PanelPelicula panelPelicula, string usuario, string password)
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
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    int maxId = 0;
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "select max(id) from streams_tl;";
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            maxId = reader.GetInt32(0);
                        }

                    if (maxId == 0) throw new Exception("Error al obtener maxID");

                    //Convierte la película a un formato compatible para caja
                    Process process = new();
                    process.StartInfo.FileName = Constantes.Global.FFMPEG_PATH;

                    //string ffmpegOutput = $"-codec copy -c:a aac -b:a 128k -map 0 -threads 2";
                    string ffmpegOutput = "-vcodec libx264 -crf 23 -preset veryfast -b:v 3M -maxrate 4M -bufsize 4M -c:a aac -strict experimental -b:a 192k -c:s copy -map 0 -movflags faststart -map 0 -threads 2";
                    //string ffmpegOutput = $"-c copy {pixFmt} -analyzeduration [PROBESIZE] -probesize [PROBESIZE]{transcodeAudio} -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -sc_threshold 0 -hls_segment_filename";

                    process.StartInfo.Arguments = $"-y -i \"{panelPelicula.UrlPelicula}\" {ffmpegOutput} \"{Constantes.Global.MOVIES_FOLDER}{nombreArchivo}.{ext}\" ";
                    process.Start();

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    cmd.CommandText = "INSERT INTO `streams_tl` " +
                                      "(`id`,`id_categoria`,`nombre_stream`,`fuente_stream`,`imagen_stream`,`orden`,`agregado`,`probesize_ondemand`,`es_bajodemanda`,`tipo`,`contenedor`,`habilitado`,`transcode_audio`,`video_info`," +
                                      "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`) " +
                                      "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,0,2,@contenedor,@habilitado,'','', " +
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

                    cmd.ExecuteNonQuery();

                    //Agregar info pelicula
                    var cmdInfo = mariadb.Conexion.CreateCommand();

                    cmdInfo.CommandText = "INSERT INTO `pelicula_info` " +
                                      "(anno,resena,PG,stream_id,duracion) " +
                                      "VALUES(@anno,@resena,@PG,@stream_id,@duracion); ";
                    cmdInfo.Parameters.AddWithValue("@anno", panelPelicula.Anno);
                    cmdInfo.Parameters.AddWithValue("@resena", panelPelicula.Resena);
                    cmdInfo.Parameters.AddWithValue("@PG", panelPelicula.PG);
                    cmdInfo.Parameters.AddWithValue("@stream_id", maxId + 1);
                    cmdInfo.Parameters.AddWithValue("@duracion", panelPelicula.Duracion);

                    cmdInfo.ExecuteNonQuery();
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
        public IActionResult AgregarInfoPelicula([FromBody] PanelInfoPelicula panelInfoPelicula, string usuario, string password, int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();

                    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                    cmd.CommandText = "INSERT INTO `pelicula_info` " +
                                      "(anno,resena,PG,stream_id,duracion) " +
                                      "VALUES(@anno,@resena,@PG,@stream_id,@duracion); ";
                    cmd.Parameters.AddWithValue("@anno", panelInfoPelicula.Anno);
                    cmd.Parameters.AddWithValue("@resena", panelInfoPelicula.Resena);
                    cmd.Parameters.AddWithValue("@PG", panelInfoPelicula.PG);
                    cmd.Parameters.AddWithValue("@stream_id", chnId);
                    cmd.Parameters.AddWithValue("@duracion", panelInfoPelicula.Duracion);

                    cmd.ExecuteNonQuery();
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
        public IActionResult ActualizarPelicula([FromBody] PanelPelicula panelPelicula, string usuario, string password, int chnId)
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

                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();

                    //Convierte la película a un formato compatible para caja
                    Process process = new();
                    process.StartInfo.FileName = Constantes.Global.FFMPEG_PATH;

                    //string ffmpegOutput = $"-codec copy -c:a aac -b:a 128k -map 0 -threads 2";
                    string ffmpegOutput = "-vcodec libx264 -crf 23 -preset veryfast -b:v 3M -maxrate 4M -bufsize 4M -c:a aac -strict experimental -b:a 192k -c:s copy -map 0 -movflags faststart -map 0 -threads 2";
                    //string ffmpegOutput = $"-c copy {pixFmt} -analyzeduration [PROBESIZE] -probesize [PROBESIZE]{transcodeAudio} -movflags faststart -hls_flags +discont_start+delete_segments+omit_endlist -hls_time [INTERVALO] -hls_list_size [SEGMENTOS] -hls_delete_threshold 10 -sc_threshold 0 -hls_segment_filename";

                    process.StartInfo.Arguments = $"-y -i \"{panelPelicula.UrlPelicula}\" {ffmpegOutput} \"{Constantes.Global.MOVIES_FOLDER}{nombreArchivo}.{ext}\" ";
                    process.Start();

                    cmd.CommandText = "UPDATE `streams_tl` " +
                                      "SET id_categoria=@id_categoria,nombre_stream=@nombre_stream,fuente_stream=@fuente_stream,imagen_stream=@imagen_stream,habilitado=@habilitado,contenedor=@contenedor " +
                                      "WHERE id=@id; ";
                    cmd.Parameters.AddWithValue("@id", chnId);
                    cmd.Parameters.AddWithValue("@id_categoria", panelPelicula.Categoria);
                    cmd.Parameters.AddWithValue("@nombre_stream", panelPelicula.NombrePelicula);
                    cmd.Parameters.AddWithValue("@fuente_stream", $"{Constantes.Global.MOVIES_FOLDER}{nombreArchivo}.{ext}");
                    cmd.Parameters.AddWithValue("@imagen_stream", panelPelicula.UrlLogo);
                    cmd.Parameters.AddWithValue("@contenedor", ext);
                    cmd.Parameters.AddWithValue("@habilitado", panelPelicula.Habilitado);

                    cmd.ExecuteNonQuery();
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
