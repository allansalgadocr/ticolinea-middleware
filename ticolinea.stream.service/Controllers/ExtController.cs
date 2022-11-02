using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class ExtController : ControllerBase
    {
        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> Playlist(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            return Ok();
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ParametrosVideo(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.Configuracion> configuracion = new();
            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "SELECT configuracion_key,numero FROM configuracion_apk " +
                                      "where configuracion_key in ('minBufferMs', 'maxBufferMs', 'bufferForPlaybackMs', 'bufferForPlaybackAfterRebufferMs')";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            configuracion.Add(new Modelos.Configuracion
                            {
                                Config = reader.GetString(0),
                                Valor = reader.GetInt32(1)
                            });
                        }

                }
            }

            return Ok(configuracion);
        }
    }


    public class IptvApk
    {
        public string versionName { get; set; } = "";
        public string versionCode { get; set; } = "";
        public string apkUrl { get; set; } = "";
        public bool forceUpdate { get; set; } = true;
    }
}
