using ticolinea.stream.service.Config;

namespace ticolinea.stream.service.Constantes
{
    public static class Global
    {
        private static StreamingSettings? _streamingSettings;
        private static DatabaseSettings? _databaseSettings;

        /// <summary>
        /// Initialize settings from configuration (call from Program.cs)
        /// </summary>
        public static void Initialize(StreamingSettings streamingSettings, DatabaseSettings databaseSettings)
        {
            _streamingSettings = streamingSettings;
            _databaseSettings = databaseSettings;
        }

        // Provider Info
        public static string PROVIDER_ID => _streamingSettings?.ProviderId ?? "main";
        public static string PROVIDER_NAME => _streamingSettings?.ProviderName ?? "Main Provider";

        // Folders
        public static string STREAMS_FOLDER => _streamingSettings?.StreamsFolder ?? "/home/ticolineaplay/streams/";
        public static string EPG_FOLDER => _streamingSettings?.EpgFolder ?? "/home/ticolineaplay/EPG/";
        public static string MOVIES_FOLDER => _streamingSettings?.MoviesFolder ?? "/home/ticolineapeliculas/stream/";
        public static string SERIES_FOLDER => _streamingSettings?.SeriesFolder ?? "/home/ticolineapeliculas/series/";
        public static string MOVIES_RAW => _streamingSettings?.MoviesRawFolder ?? "/home/ticolineapeliculas/";

        // FFmpeg paths
        public static string FFMPEG_PATH => _streamingSettings?.FfmpegPath ?? "ffmpeg";
        public static string FFMPEG_PATH_SRT => _streamingSettings?.FfmpegPath ?? "ffmpeg";
        public static string FFPROBE_PATH => _streamingSettings?.FfprobePath ?? "ffprobe";

        // Feature flags
        public static bool ENABLE_STREAM_EXECUTION => _streamingSettings?.EnableStreamExecution ?? true;
        public static bool ENABLE_FFMPEG_PROCESSES => _streamingSettings?.EnableFfmpegProcesses ?? true;
        public static bool ENABLE_STREAM_MANAGEMENT => _streamingSettings?.EnableStreamManagement ?? true;

        // URLs
        public static string SEGMENT_BASE_URL => _streamingSettings?.SegmentBaseUrl ?? "http://tv.play-latino.com:27701";

        // Database
        public static string MARIADB_CONN => _databaseSettings?.ConnectionString ?? "";
    }
}
