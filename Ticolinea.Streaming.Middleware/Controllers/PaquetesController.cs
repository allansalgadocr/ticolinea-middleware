using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MikrotikDotNet;
using MySqlConnector;
using Newtonsoft.Json;
using ticolinea.stream.service.Data;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class PaquetesController : ControllerBase
    {
        [HttpPost]
        public async Task<IActionResult> CrearPaquete([FromBody] Modelos.PaqueteTV paquete)
        {
            PaqueteResponse result = new PaqueteResponse();

            if (paquete.Usuario != "tl" || paquete.Clave != "paquete@1234") return Unauthorized();

            try
            {
                if (string.IsNullOrEmpty(paquete.IdPaquete))
                {
                    await Data.PaqueteTV.CrearPaqueteAsync(paquete);
                }
                else
                {
                    await Data.PaqueteTV.EliminarPaqueteAsync(paquete.IdPaquete);
                    await Data.PaqueteTV.CrearPaqueteAsync(paquete);
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                result.mensaje = $"ERROR. {ex.Message}";
                return Ok(result);
            }

            result.success = true;
            return Ok(result);
        }

        [HttpGet("{usuario}/{password}/{idPaquete}")]
        public async Task<IActionResult> ObtenerPaquete(string usuario, string password, string idPaquete)
        {
            PaqueteFullResponse result = new PaqueteFullResponse();

            if (usuario != "tl" || password != "paquete@1234")
                return Unauthorized();

            try
            {
                var paquete = await Data.PaqueteTV.ObtenerPaquete(idPaquete);
                result.response = paquete;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                result.success = false;
                result.mensaje = $"ERROR. {ex.Message}";
                return Ok(result);
            }

            result.success = true;
            return Ok(result);
        }

        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> ObtenerAccesos(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);

            if (usuariodb == null)
                return Unauthorized();

            PaqueteFullResponse result = new PaqueteFullResponse();
            try
            {

                var paquete = await Data.PaqueteTV.ObtenerPaquete(usuariodb.PaqueteTV);
                if (paquete != null)
                {
                    result.response = paquete;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                result.success = false;
                result.mensaje = $"ERROR al obtener accesos.";
            }

            return Ok(result);
        }
    }
}
