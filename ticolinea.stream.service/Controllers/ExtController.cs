using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class ExtController : ControllerBase
    {
        [HttpGet("{usuario}/{password}")]
        public IActionResult Playlist(string usuario, string password)
        {
            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            return Ok();
        }

        /*[HttpGet("{tipo}")]
        public IActionResult apk(string tipo)
        {
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

            return Ok();
        }*/
    }

    public class IptvApk
    {
        public string versionName { get; set; } = "";
        public string versionCode { get; set; } = "";
        public string apkUrl { get; set; } = "";
        public bool forceUpdate { get; set; } = true;
    }
}
