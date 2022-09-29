namespace ticolinea.stream.service.Constantes
{
    public static class Global_backup
    {
#if !DEBUG
         public const string STREAMS_FOLDER = "/home/ticolineaplay/streams/";
        public const string EPG_FOLDER = "/home/ticolineaplay/EPG/";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "/home/ticolineaplay/tools/ffprobe";
        public const string MARIADB_CONN= "server=127.0.0.1;Port=3306;uid=iptvtl;pwd=Qawsedrf7852!;database=iptv;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=10;Max Pool Size=200;Connection Lifetime=60;";
        public const string MOVIES_FOLDER = "/home/ticolineapeliculas/stream/";
        public const string MOVIES_RAW = "/home/ticolineapeliculas/";
#endif
#if DEBUG
        public const string STREAMS_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\streams\\";
        public const string FFMPEG_PATH = "D:\\ticolineaTV\\FFMPEG\\ffmpeg.exe";
        public const string FFMPEG_PATH_SRT = "D:\\ticolineaTV\\FFMPEG\\ffmpeg.exe";
        public const string FFPROBE_PATH = "D:\\ticolineaTV\\FFMPEG\\ffprobe.exe";
        public const string MARIADB_CONN = "Server=localhost;User ID=root;Password=Qawsedrf7852!;Database=ticolineaplay;Allow User Variables=True;SSLMode=None";
        public const string MOVIES_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\stream\\";
        public const string MOVIES_RAW = "D:\\Mega 2\\";
#endif
    }
}
