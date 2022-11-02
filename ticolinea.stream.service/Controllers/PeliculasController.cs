using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class PeliculasController : ControllerBase
    {

        [HttpGet("{chID}/{usuario}/{password}.{ext}")]
        public async Task<IActionResult> Reproducir(int chID, string usuario, string password, string ext)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            var peliculaData = await ObtieneDatosPelicula(chID);
            if (peliculaData == null)
                return NotFound();

            FileInfo fileInfo = new(peliculaData.Fuente);

            if (!fileInfo.Exists)
                return NoContent();

            var ip = "";
            if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
                ip = forwardedIps.First();

            var userAgent = Request.Headers["User-Agent"].ToString();

            await Helpers.Usuario.ActualizaInfoUsuario(usuariodb.UsuarioId, chID, userAgent, ip, usuariodb.ConexionesMaximas);

            return PhysicalFile(peliculaData.Fuente, ObtenerExtension(ext), enableRangeProcessing: true);
        }

        [HttpGet("{chID}/{usuario}/{password}")]
        public async Task<IActionResult> Informacion(int chID, string usuario, string password)
        {
            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            var peliculaData = await ObtieneInfoPelicula(chID);

            return Ok(peliculaData);
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

        private async Task<StreamDb> ObtieneDatosPelicula(int chnId)
        {
            List<StreamDb> streams = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT fuente_stream,id FROM streams_tl " +
                                        $"WHERE id = {chnId};";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            streams.Add(new StreamDb
                            {
                                Fuente = reader.GetString(0),
                                StreamId = reader.GetInt32(1),
                            });
                        }
                }
            }


            return streams.FirstOrDefault();
        }

        private async Task<PeliculaInfo> ObtieneInfoPelicula(int chnId)
        {
            List<PeliculaInfo> peliculaInfo = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT anno,resena,pg FROM pelicula_info " +
                                        $"WHERE stream_id = {chnId};";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            peliculaInfo.Add(new PeliculaInfo
                            {
                                anno = reader.GetString(0),
                                resena = reader.GetString(1),
                                PG = reader.GetString(2),
                            });
                        }
                }
            }

            return peliculaInfo.FirstOrDefault();
        }
    }
}
