namespace ticolinea.stream.service.Modelos
{
    public class Bouquet
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string Imagen { get; set; } = "";
        public string Categoria { get; set; } = "";
        public int Tipo { get; set; }
        public string Contenedor { get; set; } = "";
        public string CanalEPG { get; set; } = "";
        public int CanalId { get; set; } = 0;

        // True for synthetic slot-filler entries emitted by PlaylistOrdering so a
        // pinned channel can sit exactly on its number. Never persisted.
        public bool EsPlaceholder { get; set; } = false;
    }
}
