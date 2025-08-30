using ticolinea.stream.service.Constantes;

namespace ticolinea.stream.service.Helpers
{
    public static class StreamExecutionGuard
    {
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
                Console.WriteLine($"⚠️  STREAM EXECUTION BLOCKED: {operation}");
                Console.WriteLine($"   Environment: {GetEnvironmentInfo()}");
                Console.WriteLine($"   To enable: Build in Release mode or set ENABLE_STREAM_EXECUTION=true");
            }
            else
            {
                Console.WriteLine($"✅ Stream execution allowed: {operation}");
            }
        }
    }
}
