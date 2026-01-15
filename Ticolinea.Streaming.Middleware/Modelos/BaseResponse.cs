namespace ticolinea.stream.service.Modelos;

public class BaseResponse
{
    public bool success { get; set; }
    public string error { get; set; } = "";
    public string mensaje { get; set; } = "";
    public object data { get; set; } = "";
}