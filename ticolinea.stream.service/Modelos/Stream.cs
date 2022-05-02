namespace ticolinea.stream.service.Modelos
{
    public class StreamDb
    {
        public string Fuente { get; set; }
        public int StreamId { get; set; }
        public int ProbeSize { get; set; }
        public int ProcesoId { get; set; }
        public int EsBajoDemanda { get; set; }
        public string TranscodeAudio { get; set; }
        public short Intervalo { get; set; }
        public short Segmentos { get; set; }
        public int Framerate { get; set; }
        public int Transcode { get; set; }
        public string Resolucion { get; set; }
        public string Bitrate { get; set; }
    }
}
