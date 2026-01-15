namespace ticolinea.stream.service.Modelos;

public class Dispositivo
{
    public string MacAddress { get; set; } = "";
    public string Pin { get; set; } = "";
    public short Activo { get; set; } = 0;
    public DateTime? FechaCreacion { get; set; } = new DateTime();
    public DateTime? FechaActivacion { get; set; } = new DateTime();
    public string CreadoPor { get; set; } = "";
    public string Notas { get; set; } = "";
    public string Lista { get; set; } = "";
    public string ActivadoPor { get; set; } = "";
    public string NumeroContrato { get; set; } = "";
    public string NombreContrato { get; set; } = "";
}

public class HistorialDispositivo
{
    public string IdHistorialActivaciones { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Estado { get; set; } = "";
    public DateTime FechaCambioEstado { get; set; }
    public string Usuario { get; set; } = "";
}

public class HistorialDispositivoResponse
{
    public string IdHistorialActivaciones { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Estado { get; set; } = "";
    public string FechaCambioEstado { get; set; } = "";
    public string Usuario { get; set; } = "";
}