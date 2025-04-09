namespace ticolinea.stream.service.Modelos
{
    public class Live
    {
        public List<Canales> Canales { get; set; } = new();
        public LiveParametros Parametros { get; set; }=new();
        public List<InfoStream> EPG { get; set; } = new();
    }

    public class Canales
    {
        public int StreamId { get; set; } = 0;
        public string Nombre { get; set; } = "";
        public string Imagen { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string URL { get; set; } = "";
        public string CanalEPG { get; set; } = "";
        public int CanalId { get; set; } = 0;
        public int Chn { get; set; } = 0;
    }

    public class Peliculas
    {
        public int Id { get; set; }
        public int IdCategoria { get; set; }
        public string Categoria { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Imagen { get; set; } = "";
        public int Agregado { get; set; }
        public string Contenedor { get; set; } = "";
        public string Anno { get; set; } = "";
        public string Resena { get; set; } = "";
        public string PG { get; set; } = "";
        public string Duracion { get; set; } = "";
        public string URL { get; set; } = "";
    }

    public class LiveParametros
    {
        public int MinBufferMs { get; set; } = 4000;
        public int MaxBufferMs { get; set; } = 4000;
        public int bufferForPlaybackMs { get; set; } = 1000;
        public int bufferForPlaybackAfterRebufferMs { get; set; } = 3000;
    }
}
