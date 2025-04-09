namespace ticolinea.stream.service.Modelos
{
    public class PaqueteTV
    {
        public string Usuario { get; set; }
        public string Clave { get; set; }
        public string IdPaquete { get; set; }
        public string NombrePaquete { get; set; }
        public int Activo { get; set; }
        public int Peliculas { get; set; }
        public int Series { get; set; }
        public List<PaqueteTVStream> ListaStreams { get; set; }
    }

    public class PaqueteTVStream
    {
        public int StreamId { get; set; }
        public int Tipo { get; set; }
    }

    public class PaqueteResponse
    {
        public bool success { get; set; }
        public string mensaje { get; set; }
    }

    public class PaqueteFullResponse
    {
        public bool success { get; set; }
        public string mensaje { get; set; }
        public PaqueteTV response { get; set; }
    }
}
