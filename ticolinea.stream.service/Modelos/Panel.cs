namespace ticolinea.stream.service.Modelos
{
    public class Panel
    {
    }

    public class PanelStream
    {
        public string NombreStream { get; set; } = string.Empty;
        public string UrlStream { get; set; } = string.Empty;
        public string UrlLogo { get; set; } = string.Empty;
        public int Categoria { get; set; }
        public int EsBajoDemanda { get; set; } = 0;
        public int Optimizar { get; set; } = 0;
        public int Habilitado { get; set; } = 0;
    }

    public class PanelCategoria
    {
        public int Id { get; set; } = 0;
        public string Texto { get; set; } = "";
    }
}
