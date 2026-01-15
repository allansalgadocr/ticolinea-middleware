using System.Text;
using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers;

[Route("panel/api/[controller]/[action]")]
[ApiController]
public class UsuariosController : ControllerBase
{
    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> CrearLoginAdmin([FromBody] Modelos.LoginAdmin loginAdmin, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            List<string> camposRequeridos = new List<string>();
            StringBuilder sb = new StringBuilder();

            //Valida valores requeridos
            if (string.IsNullOrEmpty(loginAdmin.Usuario))
            {
                camposRequeridos.Add("Campo Usuario");
            }

            if (string.IsNullOrEmpty(loginAdmin.Password))
            {
                camposRequeridos.Add("Campo Clave");
            }

            if (camposRequeridos.Any())
            {
                baseResponse.success = false;
                baseResponse.error = "Debe verificar los siguientes campos: " +
                                     sb.AppendJoin(',', camposRequeridos).ToString();

                return Ok(baseResponse);
            }


            //verifica si existe el usuario
            var loginBd = await Adapters.Login.ObtenerLoginAdmin(loginAdmin.Usuario);
            if (loginBd != null)
            {
                baseResponse.success = false;
                baseResponse.error = $"Usuario {loginBd.Usuario} ya existe.";

                return Ok(baseResponse);
            }


            await Adapters.Login.AgregarUsuarioAsync(loginAdmin);
            baseResponse.success = true;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al crear login {loginAdmin.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/CrearLoginUsuario");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> CrearLoginTecnico([FromBody] Modelos.LoginTecnico loginTecnico, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            List<string> camposRequeridos = new List<string>();
            StringBuilder sb = new StringBuilder();

            //Valida valores requeridos
            if (string.IsNullOrEmpty(loginTecnico.Usuario))
            {
                camposRequeridos.Add("Campo Usuario");
            }

            if (string.IsNullOrEmpty(loginTecnico.Password))
            {
                camposRequeridos.Add("Campo Clave");
            }

            if (camposRequeridos.Any())
            {
                baseResponse.success = false;
                baseResponse.error = "Debe verificar los siguientes campos: " +
                                     sb.AppendJoin(',', camposRequeridos).ToString();

                return Ok(baseResponse);
            }


            //verifica si existe el usuario
            var loginBd = await Adapters.Login.ObtenerLoginTecnico(loginTecnico.Usuario);
            if (loginBd != null)
            {
                baseResponse.success = false;
                baseResponse.error = $"Usuario {loginBd.Usuario} ya existe.";

                return Ok(baseResponse);
            }


            await Adapters.Login.AgregarLoginTecnicoAsync(loginTecnico);
            baseResponse.success = true;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al crear login {loginTecnico.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/CrearLoginTecnico");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerListaAdmin(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            var data = await Adapters.Login.ObtenerListaAdmin();
            baseResponse.success = true;
            baseResponse.data = data;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener lista de usuarios:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerListaAdmin");

            return Ok(baseResponse);
        }
    }

    [HttpGet("{usuario}/{password}")]
    public async Task<IActionResult> ObtenerListaTecnicos(string usuario, string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            var data = await Adapters.Login.ObtenerListaTecnicos();
            baseResponse.success = true;
            baseResponse.data = data;

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al obtener lista de usuarios:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ObtenerListaTecnicos");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> ActivarAdmin([FromBody] Modelos.LoginAdmin loginAdmin, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var loginAdmindb = await Adapters.Login.ObtenerLoginAdmin(loginAdmin.Usuario);
            if (loginAdmindb != null)
            {
                await Adapters.Login.ActivarUsuarioAsync(loginAdmindb.IdLoginAdmin);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario {loginAdmin.Usuario} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al activar usuario {loginAdmin.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ActivarAdmin");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> InactivarAdmin([FromBody] Modelos.LoginAdmin loginAdmin, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var loginAdmindb = await Adapters.Login.ObtenerLoginAdmin(loginAdmin.Usuario);
            if (loginAdmindb != null)
            {
                await Adapters.Login.InactivarUsuarioAsync(loginAdmindb.IdLoginAdmin);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario {loginAdmin.Usuario} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al inactivar usuario {loginAdmin.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/InactivarAdmin");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> ActivarTecnico([FromBody] Modelos.LoginTecnico loginTecnico, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var loginTecnicodb = await Adapters.Login.ObtenerLoginTecnico(loginTecnico.Usuario);
            if (loginTecnicodb != null)
            {
                await Adapters.Login.ActivarLoginTecnicoAsync(loginTecnicodb.IdLoginTecnico);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario {loginTecnico.Usuario} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al activar usuario {loginTecnico.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/ActivarTecnico");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> InactivarTecnico([FromBody] Modelos.LoginTecnico loginTecnico, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            //verifica si existe la MacAddress
            var loginTecnicodb = await Adapters.Login.ObtenerLoginTecnico(loginTecnico.Usuario);
            if (loginTecnicodb != null)
            {
                await Adapters.Login.InactivarLoginTecnicoAsync(loginTecnicodb.IdLoginTecnico);

                baseResponse.success = true;

                return Ok(baseResponse);
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario {loginTecnico.Usuario} no existe.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al inactivar usuario {loginTecnico.Usuario}:{ex.Message}";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/InactivarTecnico");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> VerificarLoginAdmin([FromBody] Modelos.LoginAdmin loginAdmin, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            var loginAdmindb = await Adapters.Login.ObtenerLoginAdmin(loginAdmin.Usuario);
            if (loginAdmindb != null)
            {
                if (loginAdmindb.Password == loginAdmin.Password)
                {
                    baseResponse.success = true;

                    return Ok(baseResponse);
                }
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario y/o clave inválido.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al verificar credenciales";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/VerificarLoginAdmin");

            return Ok(baseResponse);
        }
    }

    [HttpPost("{usuario}/{password}")]
    public async Task<IActionResult> VerificarLoginTecnico([FromBody] Modelos.LoginTecnico loginTecnico, string usuario,
        string password)
    {
        if (usuario != "fibraencasa" || password != "81Yg0") return Unauthorized();

        BaseResponse baseResponse = new BaseResponse();

        try
        {
            var loginTecnicodb = await Adapters.Login.ObtenerLoginTecnico(loginTecnico.Usuario);
            if (loginTecnicodb != null)
            {
                if (loginTecnicodb.Password == loginTecnico.Password)
                {
                    baseResponse.success = true;

                    return Ok(baseResponse);
                }
            }

            baseResponse.success = false;
            baseResponse.error = $"Usuario y/o clave inválido.";

            return Ok(baseResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            baseResponse.success = false;
            baseResponse.error = $"Error al verificar credenciales";

            await Data.Errores.InsertarErrorLog(ex, "Dispositivos/VerificarLoginTecnico");

            return Ok(baseResponse);
        }
    }
}