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
        public string CanalEPG { get; set; } = "";
    }

    public class PanelPelicula
    {
        public string NombrePelicula { get; set; } = string.Empty;
        public string UrlPelicula { get; set; } = string.Empty;
        public string UrlLogo { get; set; } = string.Empty;
        public int Categoria { get; set; }
        public string Anno { get; set; } = "";
        public string Resena { get; set; } = "";
        public string PG { get; set; } = "";
        public string Duracion { get; set; } = "";
        public int Habilitado { get; set; } = 0;
    }

    public class PanelInfoPelicula
    {
        public string Anno { get; set; } = "";
        public string Resena { get; set; } = "";
        public string PG { get; set; } = "";
        public string Duracion { get; set; } = "";
    }

    public class PanelUsuario
    {
        public int Id { get; set; }
        public string Usuario { get; set; } = "";
        public string Clave { get; set; } = "";
        public string FechaVencimiento { get; set; } = "";
        public int Habilitado { get; set; }
        public string Notas { get; set; } = "";
    }

    public class Proveedores
    {
        public string Fuente { get; set; } = "";
        public int Cantidad { get; set; } = 0;
    }

    public class PanelCategoria
    {
        public int Id { get; set; } = 0;
        public string Texto { get; set; } = "";
    }

    public class PanelUsuariosEnLinea
    {
        public string Usuario { get; set; } = "";
        public string Notas { get; set; } = "";
        public string Canal { get; set; } = "";
    }

    public class PanelMovies
    {
        public int Id { get; set; } = 0;
        public string Nombre { get; set; } = "";
        public string Imagen { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Fuente { get; set; } = "";
        public int Habilitado { get; set; } = 0;
    }
}
