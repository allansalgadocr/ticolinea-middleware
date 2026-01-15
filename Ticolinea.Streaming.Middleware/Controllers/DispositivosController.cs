using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers;

[Route("panel/api/[controller]/[action]")]
[ApiController]
public class DispositivosController : Controller
{
    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> AgregarDispositivo([FromBody] Modelos.Dispositivo dispositivo, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();
        string lista = "";
        Random random = new Random();
        int pin = random.Next(1000, 10000);

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd = await Data.Dispositivos.ObtenerDispositivoAsync(dispositivo.MacAddress);
            if (dispositivoBd != null &&
                dispositivoBd.MacAddress.Equals(dispositivo.MacAddress, StringComparison.InvariantCultureIgnoreCase))
            {
                baseResponse.success = false;
                baseResponse.error = $"MacAddress {dispositivo.MacAddress} ya existe.";

                return Ok(baseResponse);
            }

            dispositivo.Pin = pin.ToString();
            lista = $"http://iptv.fibraencasa.cr:27701/TV/{dispositivo.MacAddress}";
            dispositivo.FechaCreacion = DateTime.Now;
            await Data.Dispositivos.AgregarDispositivoAsync(dispositivo, lista);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al agregar dispositivo {dispositivo.MacAddress}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/AgregarDispositivo");

            return Ok(baseResponse);
        }

        baseResponse.success = true;
        baseResponse.mensaje = lista;

        return Ok(baseResponse);
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> ActualizarDispositivo([FromBody] Modelos.Dispositivo dispositivo, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();
        string lista = "";
        Random random = new Random();
        int pin = random.Next(1000, 10000);

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd = await Data.Dispositivos.ObtenerDispositivoAsync(dispositivo.MacAddress);
            if (dispositivoBd != null &&
                dispositivoBd.MacAddress.Equals(dispositivo.MacAddress, StringComparison.InvariantCultureIgnoreCase))
            {
                await Data.Dispositivos.ActualizarispositivoAsync(dispositivo);

                baseResponse.success = true;

                return Ok(baseResponse);
            }
            else
            {
                baseResponse.success = false;
                baseResponse.error = $"No se encontró registo de MAC Address {dispositivo.MacAddress}";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al actualizar dispositivo {dispositivo.MacAddress}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ActualizarDispositivo");

            return Ok(baseResponse);
        }

        baseResponse.success = true;
        baseResponse.mensaje = lista;

        return Ok(baseResponse);
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> EliminarDispositivo([FromBody] Modelos.Dispositivo dispositivo, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            var dispositivoDb = await Data.Dispositivos.ObtenerDispositivoAsync(dispositivo.MacAddress);
            if (dispositivoDb != null && dispositivoDb.Activo == 0)
            {
                await Data.Dispositivos.EliminarDispositivoAsync(dispositivo);
            }
            else
            {
                baseResponse.success = false;
                baseResponse.error =
                    "No se puede eliminar un dispositivo activo. Proceda a inactivar el disposito y luego eliminarlo.";

                return Ok(baseResponse);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/EliminarDispositivo");
            baseResponse.success = false;
            baseResponse.error = $"Error al eliminar dispositivo:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/EliminarDispositivo");

            return Ok(baseResponse);
        }

        baseResponse.success = true;

        return Ok(baseResponse);
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> ActivarDispositivo([FromBody] Modelos.Dispositivo dispositivo, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd = await Data.Dispositivos.ObtenerDispositivoAsync(dispositivo.MacAddress);
            if (dispositivoBd != null && dispositivoBd.Activo != 1)
            {
                dispositivo.FechaActivacion = DateTime.Now;
                await Data.Dispositivos.ActivarDispositivoAsync(dispositivo);
                await Data.Dispositivos.InsertarHistorialAsync(dispositivo.MacAddress, Data.Dispositivos.Estado.ACTIVO,
                    dispositivo.CreadoPor);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Dispositivo con MacAddress {dispositivo.MacAddress} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al activar dispositivo {dispositivo.MacAddress}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/AgregarDispositivo");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> InactivarDispositivo([FromBody] Modelos.Dispositivo dispositivo, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd = await Data.Dispositivos.ObtenerDispositivoAsync(dispositivo.MacAddress);
            if (dispositivoBd != null && dispositivoBd.Activo != 0)
            {
                await Data.Dispositivos.InactivarDispositivoAsync(dispositivo);
                await Data.Dispositivos.InsertarHistorialAsync(dispositivo.MacAddress,
                    Data.Dispositivos.Estado.INACTIVO, dispositivo.CreadoPor);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Dispositivo con MacAddress {dispositivo.MacAddress} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al inactivar dispositivo {dispositivo.MacAddress}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/AgregarDispositivo");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerDispositivos(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivosBd = await Data.Dispositivos.ObtenerDispositivosAsync();
            baseResponse.success = true;
            baseResponse.data = dispositivosBd;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener dispositivos.{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerDispositivos");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerHistorial(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivosBd = await Data.Dispositivos.ObtenerDispositivosHistorialAsync();
            baseResponse.success = true;
            baseResponse.data = dispositivosBd;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener historial de dispositivos.{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerHistorial");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}/{macAddress}")]
    public async Task<IActionResult> ObtenerDispositivo(string usuario, string password, string macAddress)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd = await Data.Dispositivos.ObtenerDispositivoAsync(macAddress);
            baseResponse.success = true;
            baseResponse.data = dispositivoBd;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al inactivar dispositivo {macAddress}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerDispositivo");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerDispositivosActivos(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd =
                await Data.Dispositivos.ObtenerDispositivosPorEstadoAsync(Data.Dispositivos.Estado.ACTIVO);
            baseResponse.success = true;
            baseResponse.data = dispositivoBd;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener dispositivos activos:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerDispositivo");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerDispositivosInactivos(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var dispositivoBd =
                await Data.Dispositivos.ObtenerDispositivosPorEstadoAsync(Data.Dispositivos.Estado.INACTIVO);
            baseResponse.success = true;
            baseResponse.data = dispositivoBd;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener dispositivos inactivos:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerDispositivosInactivos");

            return Ok(baseResponse);
        }
    }
}