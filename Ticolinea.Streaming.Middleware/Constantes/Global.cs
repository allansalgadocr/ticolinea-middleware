namespace ticolinea.stream.service.Constantes
{
    public static class Global
    {
#if !DEBUG
        public const string STREAMS_FOLDER = "/home/ticolineaplay/streams/";
        public const string EPG_FOLDER = "/home/ticolineaplay/EPG/";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "ffprobe";
        public const string MARIADB_CONN= "server=tl-stream-db.c0bgkuywomcr.us-east-1.rds.amazonaws.com;Port=6606;uid=admin;pwd=TC0hGxPQ5IxcIHAkxqsB;database=streamdb;Allow User Variables=True;SSLMode=Required;Pooling=true;Min Pool Size=10;Max Pool Size=200;Connection Lifetime=300;Connection Reset=true;Default Command Timeout=30;AllowPublicKeyRetrieval=True";
        public const string MOVIES_FOLDER = "/home/ticolineapeliculas/stream/";
        public const string SERIES_FOLDER = "/home/ticolineapeliculas/series/";
        public const string MOVIES_RAW = "/home/ticolineapeliculas/";
        
        // Production: Allow stream execution
        public const bool ENABLE_STREAM_EXECUTION = true;
        public const bool ENABLE_FFMPEG_PROCESSES = true;
        public const bool ENABLE_STREAM_MANAGEMENT = true;
#endif
#if DEBUG
        public const string STREAMS_FOLDER = @"C:\inetpub\wwwroot\iptv\streams\";
        public const string EPG_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\epg\\";
        public const string FFMPEG_PATH_SRT = "ffmpeg";
        public const string FFMPEG_PATH = "ffmpeg";
        public const string FFPROBE_PATH = "ffmpeg";
        public const string MARIADB_CONN = "Server=localhost;User ID=root;Password=Qawsedrf7852!;Database=ticolineaplay;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=10;Max Pool Size=100;Connection Lifetime=300;Connection Reset=true;Default Command Timeout=30;AllowPublicKeyRetrieval=true";
        public const string MOVIES_FOLDER = "C:\\inetpub\\wwwroot\\iptv\\vod\\stream\\";
        public const string SERIES_FOLDER = "D:\\serie\\";
        public const string MOVIES_RAW = "D:\\Mega 2\\";
        
        // Development: Disable stream execution for safety
        public const bool ENABLE_STREAM_EXECUTION = false;
        public const bool ENABLE_FFMPEG_PROCESSES = false;
        public const bool ENABLE_STREAM_MANAGEMENT = false;
#endif
    }
}
