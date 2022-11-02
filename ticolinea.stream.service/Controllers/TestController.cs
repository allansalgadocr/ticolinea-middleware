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
        public async Task<IActionResult> TestRevisarStreams()
        {
            await Jobs.RevisarStreams();
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> TestDetenerStreams()
        {
            await Jobs.DetenerStreamsSinUso();
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> TestMataConexiones()
        {
            await Jobs.MataConexionesSinUso();
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> TestVerificaStreams()
        {
            await Jobs.VerificarStreamsCaidos();
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> EliminarArchivosGrandes()
        {
            await Jobs.EliminarArchivosGrandes();
            return Ok();
        }

        [HttpGet("{archivo}/{esLocal}")]
        public async Task<IActionResult> ObtenerEPG(string archivo, bool esLocal)
        {
            await Helpers.xmlHelper.Deserializar(archivo, esLocal);
            return Ok();
        }

        [HttpGet("{verificarSoloHabilitados}")]
        public async Task<IActionResult> TestVerificaCodecs(int verificarSoloHabilitados)
        {
            await Jobs.VerificarCodecsStreams(verificarSoloHabilitados == 1);
            return Ok();
        }

        [HttpGet("{verificarSoloHabilitados}")]
        public async Task<IActionResult> TestDetenerProceso(int streamId)
        {
            return Ok(await Jobs.RunCommandAsync(streamId));
        }
    }
}
