namespace ticolinea.stream.service.Modelos
{
    public class DataStream
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
        public string Imagen { get; set; }
        public string Categoria { get; set; }
        public string Fuente { get; set; }
        public int Ejecutando { get; set; }
        public int Habilitado { get; set; } = 0;
        public int Iniciado { get; set; } = 0;
        public DataStream()
        {
            Nombre = "";
            Imagen = "";
            Categoria = "";
            Fuente = "";
        }
    }
}
