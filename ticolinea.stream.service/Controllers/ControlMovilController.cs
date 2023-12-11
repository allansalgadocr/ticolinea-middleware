using Microsoft.AspNetCore.Mvc;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class ControlMovilController : ControllerBase
    {
        [HttpGet("{usuario}/{password}/{macAddress}")]
        public async Task<IActionResult> ValidarConexiones(string usuario, string password, string macAddress)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null)
                return Unauthorized();

            var conexionesActuales = await Data.ActividadPorUsuarios.ObtenerCantidadConexionesActivas(usuariodb.UsuarioId,macAddress);
            if (conexionesActuales >= usuariodb.ConexionesMaximas)
                return StatusCode(429);

            return Ok();
        }
    }
}
