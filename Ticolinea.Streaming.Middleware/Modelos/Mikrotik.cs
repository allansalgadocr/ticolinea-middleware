namespace ticolinea.stream.service.Modelos
{
    public class Mikrotik
    {
        public string Usuario { get; set; }
        public string Password { get; set; }
        public string Nombre { get; set; }
        public string Email { get; set; }
        public string Telefono { get; set; }
        public string FechaNacimiento { get; set; }
    }

    public class MikrotikResponse
    {
        public bool success { get; set; }
        public string message { get; set; }
    }
}
