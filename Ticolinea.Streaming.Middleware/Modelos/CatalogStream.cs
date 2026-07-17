namespace ticolinea.stream.service.Modelos;

public class CatalogStream
{
    public int Id { get; set; }
    public string NombreStream { get; set; } = "";
    public string? FuenteStream { get; set; }
    public string? ImagenStream { get; set; }
    public int? IdCategoria { get; set; }
    public int Orden { get; set; }
    public int Agregado { get; set; }
    public int ProbesizeOndemand { get; set; }
    public sbyte EsBajodemanda { get; set; }
    public sbyte Tipo { get; set; }
    public string? Contenedor { get; set; }
    public sbyte Habilitado { get; set; }
    public string TranscodeAudio { get; set; } = "";
    public sbyte? Intervalo { get; set; }
    public sbyte? Segmentos { get; set; }
    public sbyte? Framerate { get; set; }
    public sbyte Transcode { get; set; }
    public string Resolucion { get; set; } = "";
    public string Bitrate { get; set; } = "";
    public string CanalEpg { get; set; } = "";
    public sbyte Cgop { get; set; }
    public int Gop { get; set; }
    public int CanalId { get; set; }
}
