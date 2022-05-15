using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    }
}
