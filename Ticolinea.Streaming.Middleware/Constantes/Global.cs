using Microsoft.Extensions.Http;
using ticolinea.stream.service.Config;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service.Constantes
{
    public static class Global
    {
        private static StreamingSettings? _streamingSettings;
        private static DatabaseSettings? _databaseSettings;

        // Set from Program.cs once the app's service provider is available.
        // Used by static Hangfire jobs (e.g. Jobs.SyncPackageCatalog) that have
        // no DI container of their own to pull the named "PanelApi" HttpClient from.
        public static IHttpClientFactory HttpClientFactory { get; set; } = null!;

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
        public static string STREAMS_FOLDER => _streamingSettings?.StreamsFolder ?? "/home/fibraencasatv/streams/";
        public static string EPG_FOLDER => _streamingSettings?.EpgFolder ?? "/home/fibraencasatv/EPG/";
        public static string MOVIES_FOLDER => _streamingSettings?.MoviesFolder ?? "/home/ticolineapeliculas/stream/";
        public static string SERIES_FOLDER => _streamingSettings?.SeriesFolder ?? "/home/ticolineapeliculas/series/";
        public static string MOVIES_RAW => _streamingSettings?.MoviesRawFolder ?? "/home/ticolineapeliculas/";

        // FFmpeg paths
        public static string FFMPEG_PATH => _streamingSettings?.FfmpegPath ?? "ffmpeg";
        public static string FFMPEG_PATH_SRT => _streamingSettings?.FfmpegPath ?? "/home/fibraencasatv/tools/ffmpeg-srt";
        public static string FFPROBE_PATH => _streamingSettings?.FfprobePath ?? "ffprobe";

        // Feature flags
        public static bool ENABLE_STREAM_EXECUTION => _streamingSettings?.EnableStreamExecution ?? true;
        public static bool ENABLE_FFMPEG_PROCESSES => _streamingSettings?.EnableFfmpegProcesses ?? true;
        public static bool ENABLE_STREAM_MANAGEMENT => _streamingSettings?.EnableStreamManagement ?? true;
        // Pilot: FFmpeg-managed HLS discontinuities (see StreamingSettings.FfmpegManagedDiscontinuities).
        // Default false = today's behavior (app-side discontinuity injection, no epoch sequence numbers).
        public static bool FFMPEG_MANAGED_DISCONTINUITIES => _streamingSettings?.FfmpegManagedDiscontinuities ?? false;

        // URLs
        public static string SEGMENT_BASE_URL => _streamingSettings?.SegmentBaseUrl ?? "http://190.106.68.6:27701/";
        public static string STREAMS_BASE_URL => _streamingSettings?.StreamsBaseUrl ?? "http://tv.play-latino.com:27703";

        // Database
        public static string MARIADB_CONN => _databaseSettings?.ConnectionString ?? "server=127.0.0.1;Port=4447;uid=streamingservice;pwd=PASSWORD;database=fibraencasa-streaming;Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=50;Max Pool Size=500;Connection Lifetime=0;AllowPublicKeyRetrieval=true";

        // Panel API (JWT section). JwtSettings is already held by TokenValidation's
        // backing field via TokenValidation.Initialize(jwtSettings) in Program.cs —
        // these accessors read off that same instance rather than duplicating storage.
        public static string PANEL_API_URL => TokenValidation.GetSettings()?.PanelApiUrl?.TrimEnd('/') ?? "";

        // test seam: lets unit tests set the key without full Initialize()/TokenValidation setup.
        // Production path (TokenValidation.GetSettings()?.PanelApiKey) is unchanged when this is null.
        internal static string? _testPanelApiKey;
        public static void TestSetPanelApiKey(string v) => _testPanelApiKey = v;

        public static string PANEL_API_KEY => _testPanelApiKey ?? (TokenValidation.GetSettings()?.PanelApiKey ?? "");
    }
}
