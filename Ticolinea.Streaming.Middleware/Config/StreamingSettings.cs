namespace ticolinea.stream.service.Config
{
    public class StreamingSettings
    {
        public const string SectionName = "Streaming";

        /// <summary>
        /// Provider identifier (e.g., "main", "fibraencasa")
        /// </summary>
        public string ProviderId { get; set; } = "main";

        /// <summary>
        /// Provider display name
        /// </summary>
        public string ProviderName { get; set; } = "Main Provider";

        /// <summary>
        /// Folder where HLS streams are stored
        /// </summary>
        public string StreamsFolder { get; set; } = "/home/ticolineaplay/streams/";

        /// <summary>
        /// EPG data folder
        /// </summary>
        public string EpgFolder { get; set; } = "/home/ticolineaplay/EPG/";

        /// <summary>
        /// Movies folder
        /// </summary>
        public string MoviesFolder { get; set; } = "/home/ticolineapeliculas/stream/";

        /// <summary>
        /// Series folder
        /// </summary>
        public string SeriesFolder { get; set; } = "/home/ticolineapeliculas/series/";

        /// <summary>
        /// Raw movies folder
        /// </summary>
        public string MoviesRawFolder { get; set; } = "/home/ticolineapeliculas/";

        /// <summary>
        /// Path to ffmpeg binary
        /// </summary>
        public string FfmpegPath { get; set; } = "ffmpeg";

        /// <summary>
        /// Path to ffprobe binary
        /// </summary>
        public string FfprobePath { get; set; } = "ffprobe";

        /// <summary>
        /// Enable stream execution (set false for dev/testing)
        /// </summary>
        public bool EnableStreamExecution { get; set; } = true;

        /// <summary>
        /// Enable ffmpeg processes
        /// </summary>
        public bool EnableFfmpegProcesses { get; set; } = true;

        /// <summary>
        /// Enable stream management jobs
        /// </summary>
        public bool EnableStreamManagement { get; set; } = true;

        /// <summary>
        /// Base URL for streaming node (e.g., http://tv.play-latino.com:27701)
        /// </summary>
        public string SegmentBaseUrl { get; set; } = "http://tv.play-latino.com:27701";

        /// <summary>
        /// Base URL for streams server (e.g., http://tv.play-latino.com:27703)
        /// Used for fetching stream segments from another server
        /// </summary>
        public string StreamsBaseUrl { get; set; } = "http://tv.play-latino.com:27703";
    }

    public class DatabaseSettings
    {
        public const string SectionName = "Database";

        /// <summary>
        /// MariaDB/MySQL connection string
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;
    }
}

