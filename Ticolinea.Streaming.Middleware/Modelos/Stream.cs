namespace ticolinea.stream.service.Modelos
{
    public class StreamDb
    {
        public string Fuente { get; set; } = "";
        public int StreamId { get; set; }
        public int ProbeSize { get; set; }
        public int ProcesoId { get; set; }
        public int EsBajoDemanda { get; set; }
        public string TranscodeAudio { get; set; } = "";
        public short Intervalo { get; set; }
        public short Segmentos { get; set; }
        public int Framerate { get; set; }
        public int Transcode { get; set; }
        public string Resolucion { get; set; } = "";
        public string Bitrate { get; set; } = "";
        public int CGOP { get; set; }
        public int GOP { get; set; }

    }

    public class InfoStream
    {
        public string CanalEpg { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Anno { get; set; } = "";
        public long Inicio { get; set; } = 0;
        public long Fin { get; set; } = 0;
    }
}
