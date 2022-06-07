using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class TestController : ControllerBase
    {
        [HttpGet]
        public IActionResult TestRevisarStreams()
        {
            Jobs.RevisarStreams();
            return Ok();
        }

        [HttpGet]
        public IActionResult TestDetenerStreams()
        {
            Jobs.DetenerStreamsSinUso();
            return Ok();
        }

        [HttpGet]
        public IActionResult TestMataConexiones()
        {
            Jobs.MataConexionesSinUso();
            return Ok();
        }

        [HttpGet]
        public IActionResult TestVerificaStreams()
        {
            Jobs.VerificarStreamsCaidos();
            return Ok();
        }

        [HttpGet]
        public IActionResult ObtenerEPG()
        {
            Helpers.xmlHelper.Deserializar();
            return Ok();
        }
    }
}
