using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class PeliculasController : ControllerBase
    {

        [HttpGet("{chID}/{usuario}/{password}.{ext}")]
        public IActionResult Reproducir(int chID, string usuario, string password, string ext)
        {
            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            var peliculaData = ObtieneDatosPelicula(chID);
            if (peliculaData == null)
                return NotFound();

            FileInfo fileInfo = new(peliculaData.Fuente);

            if (!fileInfo.Exists)
                return NoContent();

            var ip = "";
            if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
                ip = forwardedIps.First();

            var userAgent = Request.Headers["User-Agent"].ToString();

            Helpers.Usuario.ActualizaInfoUsuario(usuariodb.UsuarioId, chID, userAgent, ip, usuariodb.ConexionesMaximas);

            return PhysicalFile(peliculaData.Fuente, ObtenerExtension(ext), enableRangeProcessing: true);
        }

        private static string ObtenerExtension(string ext)
        {
            return ext switch
            {
                "mp4" => "video/mp4",
                "mkv" => "video/x-matroska",
                "avi" => "video/x-msvideo",
                "3gp" => "video/3gpp",
                "flv" => "video/x-flv",
                "wmv" => "video/x-ms-wmv",
                "mov" => "video/quicktime",
                "ts" => "video/mp2t",
                _ => "application/octet-stream",
            };
        }

        private StreamDb ObtieneDatosPelicula(int chnId)
        {
            List<StreamDb> streams = new();
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();

                cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id FROM streams_tl a " +
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
                        });
                    }
                cmd.Connection?.Close();
            }


            return streams.FirstOrDefault();
        }
    }
}
