namespace ticolinea.stream.service.Constantes
{
    public static class Global_backup
    {
#if !DEBUG
        public const string STREAMS_FOLDER = "/home/fibraencasatv/streams/";
        public const string EPG_FOLDER = "/home/fibraencasatv/EPG/";
        public const string FFMPEG_PATH_SRT = "/home/fibraencasatv/tools/ffmpeg-srt";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "ffprobe";
        public const string MARIADB_CONN= "server=127.0.0.1;Port=4447;uid=streamingservice;pwd=PASSWORD;database=fibraencasa-streaming;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=50;Max Pool Size=500;Connection Lifetime=0;AllowPublicKeyRetrieval=true";
        public const string MOVIES_FOLDER = "/home/ticolineapeliculas/stream/";
        public const string SERIES_FOLDER = "/home/ticolineapeliculas/series/";
        public const string MOVIES_RAW = "/home/ticolineapeliculas/";
        public const string URL_BASE = "http://190.106.68.6:27701/";
#endif
#if DEBUG
        public const string STREAMS_FOLDER = @"C:\inetpub\wwwroot\iptv\streams\";
        public const string EPG_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\epg\\";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "ffmpeg";
        public const string MARIADB_CONN = "Server=localhost;User ID=root;Password=Qawsedrf7852!;Database=fibra_en_casa;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=50;Max Pool Size=500;Connection Lifetime=60;AllowPublicKeyRetrieval=true";
        public const string MOVIES_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\stream\\";
        public const string SERIES_FOLDER = "D:\\serie\\";
        public const string MOVIES_RAW = "D:\\Mega 2\\";
        public const string URL_BASE = "http://190.106.68.6:27701/";
#endif
    }
}
