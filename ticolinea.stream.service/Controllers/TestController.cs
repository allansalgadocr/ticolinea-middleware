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
        public IActionResult EliminarArchivosGrandes()
        {
            Jobs.EliminarArchivosGrandes();
            return Ok();
        }

        [HttpGet("{archivo}/{esLocal}")]
        public IActionResult ObtenerEPG(string archivo, bool esLocal)
        {
            Helpers.xmlHelper.Deserializar(archivo, esLocal);
            return Ok();
        }

        [HttpGet("{verificarSoloHabilitados}")]
        public IActionResult TestVerificaCodecs(int verificarSoloHabilitados)
        {
            Jobs.VerificarCodecsStreams(verificarSoloHabilitados == 1);
            return Ok();
        }

        [HttpGet("{verificarSoloHabilitados}")]
        public async Task<IActionResult> TestDetenerProceso(int streamId)
        {
            return Ok(await Jobs.RunCommandAsync(streamId));
        }
    }
}
