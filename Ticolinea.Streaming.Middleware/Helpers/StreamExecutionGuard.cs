using log4net;
using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Helpers
{
    public static class StreamExecutionGuard
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamExecutionGuard));

        public static bool CanExecuteStreams()
        {
            return Global.ENABLE_STREAM_EXECUTION;
        }

        public static bool CanStartFFmpegProcesses()
        {
            return Global.ENABLE_FFMPEG_PROCESSES;
        }

        public static string GetEnvironmentInfo()
        {
#if DEBUG
            return "Development Environment - Stream execution DISABLED for safety";
#else
            return "Production Environment - Stream execution ENABLED";
#endif
        }

        public static void LogStreamExecutionAttempt(string operation)
        {
            if (!CanExecuteStreams())
            {
                _logger.Warn($"⚠️  STREAM EXECUTION BLOCKED: {operation}. {GetEnvironmentInfo()}. To enable: Build in Release mode or set ENABLE_STREAM_EXECUTION=true");
            }
            else
            {
                _logger.Debug($"✅ Stream execution allowed: {operation}");
            }
        }
    }
}
