using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ticolinea.stream.service.Controllers;

[Route("[controller]")]
[ApiController]
public class TVController : Controller
{
    [HttpGet("{macaddress}")]
    public async Task<IActionResult> Lista(string macaddress)
    {
        var dispositivo = await Data.Dispositivos.ObtenerDispositivoAsync(macaddress);
        if (dispositivo == null || dispositivo?.Activo == 0)
            return Unauthorized();

        var ip = "";
        if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
            ip = forwardedIps.First();

        string userAgent = Request.Headers["User-Agent"].ToString() ?? "";

        bool agenteValido = userAgent.Contains("Tvip", StringComparison.InvariantCultureIgnoreCase) ||
                            userAgent.Contains("Lav", StringComparison.InvariantCultureIgnoreCase);

        if (!agenteValido)
        {
            _ = Data.Dispositivos.LogActividadSospechosa(macaddress, userAgent, ip);
            return Unauthorized();
        }

        var bouquet = await Adapters.Bouquet.ObtenerLista();
        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U\r\n");

        int chNumber = 0;
        foreach (var chn in bouquet)
        {
            chNumber += 1;
            sb.AppendLine(
                $"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" channel-id=\"{chNumber}\" channel-number=\"{chNumber}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
            sb.AppendLine($"http://190.9.124.250:27701/envivo/streaming/{chn.Id}/{macaddress}.m3u8\r\n");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"fibraencasa_{macaddress}.m3u");
    }

    [HttpGet("{macaddress}.{extension}")]
    public async Task<IActionResult> ListaM3u(string macaddress)
    {
        var dispositivo = await Data.Dispositivos.ObtenerDispositivoAsync(macaddress);
        if (dispositivo == null || dispositivo?.Activo == 0)
            return Unauthorized();

        var ip = "";
        if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
            ip = forwardedIps.First();

        string userAgent = Request.Headers["User-Agent"].ToString() ?? "";

        bool agenteValido = userAgent.Contains("Tvip", StringComparison.InvariantCultureIgnoreCase) ||
                            userAgent.Contains("Lav", StringComparison.InvariantCultureIgnoreCase);

        if (!agenteValido)
        {
            _ = Data.Dispositivos.LogActividadSospechosa(macaddress, userAgent, ip);
            return Unauthorized();
        }

        var bouquet = await Adapters.Bouquet.ObtenerLista();
        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U\r\n");

        int chNumber = 0;
        foreach (var chn in bouquet)
        {
            chNumber += 1;
            sb.AppendLine(
                $"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" channel-id=\"{chNumber}\" channel-number=\"{chNumber}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
            sb.AppendLine($"http://190.9.124.250:27701/envivo/streaming/{chn.Id}/{macaddress}.m3u8\r\n");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"fibraencasa_{macaddress}.m3u");
    }
}