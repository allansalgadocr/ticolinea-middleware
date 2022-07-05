namespace ticolinea.stream.service.Constantes
{
    public static class Global
    {
#if !DEBUG
        public const string STREAMS_FOLDER = "/home/ticolineaplay/streams/";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "/home/ticolineaplay/tools/ffprobe";
        public const string MARIADB_CONN= "server=127.0.0.1;Port=7999;uid=ticolineadb;pwd=Qawsedrf7852!;database=xtream_iptvpro;Allow User Variables=True;SSLMode=None;Pooling=false;Max Pool Size=3000;";
        public const string MOVIES_FOLDER = "/home/ticolineapeliculas/stream/";
        public const string MOVIES_RAW = "/home/ticolineapeliculas/";
#endif
#if DEBUG
        public const string STREAMS_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\streams\\";
        public const string FFMPEG_PATH = "D:\\ticolineaTV\\FFMPEG\\ffmpeg.exe";
        public const string FFPROBE_PATH = "D:\\ticolineaTV\\FFMPEG\\ffprobe.exe";
        public const string MARIADB_CONN = "Server=localhost;User ID=root;Password=Qawsedrf7852!;Database=ticolineaplay;Allow User Variables=True;SSLMode=None";
        public const string MOVIES_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\stream\\";
        public const string MOVIES_RAW = "D:\\Mega 2\\";
#endif
    }
}
