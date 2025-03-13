namespace ticolinea.stream.service.Constantes
{
    public static class Global
    {
#if !DEBUG
        public const string STREAMS_FOLDER = "/home/ticolineaplay/streams/";
        public const string EPG_FOLDER = "/home/ticolineaplay/EPG/";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "/home/ticolineaplay/tools/ffprobe";
        public const string MARIADB_CONN= "server=127.0.0.1;Port=3306;uid=iptvtl;pwd=Qawsedrf7852!;database=iptv;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=25;Max Pool Size=350;Connection Lifetime=0;AllowPublicKeyRetrieval=true";
        public const string MOVIES_FOLDER = "/home/ticolineapeliculas/stream/";
        public const string SERIES_FOLDER = "/home/ticolineapeliculas/series/";
        public const string MOVIES_RAW = "/home/ticolineapeliculas/";
#endif
#if DEBUG
        public const string STREAMS_FOLDER = @"C:\inetpub\wwwroot\iptv\streams\";
        public const string EPG_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\epg\\";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "ffmpeg";
        public const string MARIADB_CONN = "Server=localhost;User ID=root;Password=Qawsedrf7852!;Database=ticolineaplay;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=50;Max Pool Size=500;Connection Lifetime=60;AllowPublicKeyRetrieval=true";
        public const string MOVIES_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\stream\\";
        public const string SERIES_FOLDER = "D:\\serie\\";
        public const string MOVIES_RAW = "D:\\Mega 2\\";
#endif
    }
}
