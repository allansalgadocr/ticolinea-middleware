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
                    cmd.CommandText = "SELECT a.id,imagen_stream,nombre_stream,category_name,fuente_stream,ejecutando FROM streams_tl a INNER JOIN " +
                                                          "streams_info b " +
                                                          "ON a.id = b.stream_id INNER JOIN " +
                                                          "stream_categories c " +
                                                          "ON a.id_categoria = c.id and tipo=1;";

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
                                Ejecutando = reader.GetInt32(5)
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
    }
}
