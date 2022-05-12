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

    public class PanelUsuario
    {
        public int Id { get; set; }
        public string Usuario { get; set; } = "";
        public string Clave { get; set; } = "";
        public string FechaVencimiento { get; set; } = "";
        public int Habilitado { get; set; }
    }

    public class PanelCategoria
    {
        public int Id { get; set; } = 0;
        public string Texto { get; set; } = "";
    }
}
