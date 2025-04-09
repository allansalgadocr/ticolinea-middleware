namespace ticolinea.stream.service.Modelos
{
    public class Serie
    {
        public int SerieId { get; set; } = 0;
        public string Imagen { get; set; } = "";
        public string Genero { get; set; } = "";
        public string Temporadas { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string ImagenGrande { get; set; } = "";
        public int StreamId { get; set; } = 0;
        public string URL { get; set; } = "";
        public string ResenaEpisodio { get; set; } = "";
        public string ResenaSerie { get; set; } = "";
        public string TituloEpisodio { get; set; } = "";
        public int TemporadaNum { get; set; } = 0;
        public string FechaLanzamiento { get; set; } = "";
    }

    public class Episodio
    {
        public int EpisodioNum { get; set; } = 0;
        public int SerieId { get; set; } = 0;
        public int Orden { get; set; } = 0;
        public int TemporadaNum { get; set; } = 0;
        public string Imagen { get; set; } = "";
        public string Contenedor { get; set; } = "";
        public string Resena { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string URL { get; set; } = "";
    }

    public class EpisodioRequest
    {
        public List<int> Temporadas { get; set; } = new();
        public List<Episodio> Episodios { get; set; }=new();
    }
}
