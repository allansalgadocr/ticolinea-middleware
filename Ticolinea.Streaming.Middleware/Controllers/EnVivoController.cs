using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace ticolinea.stream.service.Controllers;

[Route("[controller]/[action]")]
[ApiController]
public class EnVivoController : Controller
{
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/{macaddress}.{ext}")]
        public async Task<IActionResult> Streaming(int chID, string macaddress, string ext)
        {
            if (!ext.Equals("m3u8")) 
                return Unauthorized();

            var dispositivo = await Data.Dispositivos.ObtenerDispositivoAsync(macaddress);
            if (dispositivo== null || dispositivo?.Activo==0)
                return Unauthorized();

            bool existeInfo = await Adapters.Streams.ExisteInformacionStream(chID);
            if (!existeInfo)
                return Unauthorized();

            var playlistFile = $"{Constantes.Global.STREAMS_FOLDER}{chID}_.m3u8";

            var archivoExiste = System.IO.File.Exists(playlistFile);
            if (!archivoExiste)
                return NotFound();

            string playlistOutput = await System.IO.File.ReadAllTextAsync(playlistFile);

            string pattern = @"(.*?).ts";
            Regex rg = new(pattern);
            var matches = rg.Matches(playlistOutput);

            // Same pilot gate as LiveController.AddDiscontinuityTags: when ffmpeg
            // manages discontinuities (discont_start+append_list), skip app-side injection.
            if (!Constantes.Global.FFMPEG_MANAGED_DISCONTINUITIES
                && !playlistOutput.Contains("EXT-X-DISCONTINUITY"))
            {
                string patternTest = @"(EXT-X-MEDIA-SEQUENCE:[0-9]*\n)";
                Regex rgtest = new(patternTest);
                var matchestest = rgtest.Matches(playlistOutput);
                foreach (var match in matchestest)
                {
                    playlistOutput = playlistOutput.Replace(match.ToString(), $@"{match}#EXT-X-DISCONTINUITY{Environment.NewLine}");
                }
            }

            foreach (var match in matches)
            {
                string token = Helpers.MD5.Encriptar($"{macaddress}zxcvbnm7852{match}");
                playlistOutput = playlistOutput.Replace(match.ToString(), $@"/Live/chunks/{macaddress}/{token}/{match}");
            }

            byte[] byteArray = Encoding.UTF8.GetBytes(playlistOutput);
            MemoryStream stream = new(byteArray);

            var ip = "";
            if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
                ip = forwardedIps.First();

            var userAgent = Request.Headers["User-Agent"].ToString();

            bool agenteValido = false;

            if (userAgent.Contains("Tvip", StringComparison.InvariantCultureIgnoreCase) ||
                userAgent.Contains("Lav", StringComparison.InvariantCultureIgnoreCase))
                agenteValido = true;

            if (!agenteValido)
            {
                await Data.Dispositivos.LogActividadSospechosa(macaddress, userAgent, ip);
                //return Unauthorized();
            }

            Data.Dispositivos.LogActividadDispositivo(macaddress, chID, userAgent, ip);

            return File(stream, "application/x-mpegurl", $"{chID}.m3u8");
        }
}