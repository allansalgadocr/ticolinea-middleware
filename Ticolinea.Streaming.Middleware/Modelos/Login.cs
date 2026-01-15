namespace ticolinea.stream.service.Modelos;

public class LoginAdmin
{
    public string IdLoginAdmin { get; set; }
    public string Usuario { get; set; }
    public string Password { get; set; }
    public DateTime FechaCreacion { get; set; }
    public short Habilitado { get; set; }
}

public class LoginTecnico
{
    public string IdLoginTecnico { get; set; }
    public string Usuario { get; set; }
    public string Password { get; set; }
    public DateTime FechaCreacion { get; set; }
    public short Habilitado { get; set; }
}