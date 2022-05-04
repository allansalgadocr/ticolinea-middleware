using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
                    cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,reportado_caido, habilitado, iniciado FROM streams_tl a INNER JOIN " +
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
                                Habilitado= reader.GetInt32(6),
                                Iniciado= reader.GetInt32(7)
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
                    cmd.CommandText = "select nombre_stream,fuente_stream,imagen_stream,id_categoria,es_bajodemanda,transcode,habilitado from streams_tl " +
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
                                      "`audio_info`,`intervalo`,`segmentos`,`omitir_verificacion`,`framerate`,`transcode`,`resolucion`,`bitrate`) " +
                                      "VALUES(@id,@id_categoria,@nombre_stream,@fuente_stream,@imagen_stream,@orden,@agregado,512000,@es_bajodemanda,@habilitado,'',1,'','', " +
                                      "'',6,5,0,25,@transcode,'','1500k'); ";
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

        [HttpPost("{usuario}/{password}/{chnId}")]
        public IActionResult ActualizarStream([FromBody] PanelStream panelStream, string usuario, string password,int chnId)
        {
            if (usuario != "ticolineapanel" || password != "e&9QzbF2DB7tg5&s") return Unauthorized();

            try
            {
                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    List<StreamDb> streams = new();
                    var cmd = mariadb.Conexion.CreateCommand();

                    cmd.CommandText = "UPDATE `streams_tl` " +
                                      "SET id_categoria=@id_categoria,nombre_stream=@nombre_stream,fuente_stream=@fuente_stream,imagen_stream=@imagen_stream,es_bajodemanda=@es_bajodemanda,habilitado=@habilitado,transcode=@transcode " +
                                      "WHERE id=@id; ";
                    cmd.Parameters.AddWithValue("@id", chnId);
                    cmd.Parameters.AddWithValue("@id_categoria", panelStream.Categoria);
                    cmd.Parameters.AddWithValue("@nombre_stream", panelStream.NombreStream);
                    cmd.Parameters.AddWithValue("@fuente_stream", panelStream.UrlStream);
                    cmd.Parameters.AddWithValue("@imagen_stream", panelStream.UrlLogo);
                    cmd.Parameters.AddWithValue("@es_bajodemanda", panelStream.EsBajoDemanda);
                    cmd.Parameters.AddWithValue("@transcode", panelStream.Optimizar);
                    cmd.Parameters.AddWithValue("@habilitado", panelStream.Habilitado);

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
        public IActionResult DetenerStream(string usuario, string password,int chnId)
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

                        cmd.CommandText = "DELETE `streams_info` " +
                                      "WHERE stream_id=@id; ";
                        cmd.Parameters.AddWithValue("@id", chnId);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE `streams_tl` " +
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
    }
}
