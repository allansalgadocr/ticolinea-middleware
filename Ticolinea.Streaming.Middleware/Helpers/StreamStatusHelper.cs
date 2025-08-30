using CliWrap;
using CliWrap.Buffered;
using System.Diagnostics;

namespace ticolinea.stream.service.Helpers
{
    public static class StreamStatusHelper
    {
        public class StreamStatus
        {
            public int StreamId { get; set; }
            public bool IsRunning { get; set; }
            public int? ProcessId { get; set; }
            public DateTime LastChecked { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        public static async Task<StreamStatus> GetRealTimeStreamStatusAsync(int streamId)
        {
            var status = new StreamStatus 
            { 
                StreamId = streamId, 
                LastChecked = DateTime.UtcNow 
            };

            try
            {
                // Check if process is actually running using pgrep
                var result = await Cli.Wrap("/bin/pgrep")
                    .WithArguments($"-f \"/{streamId}_.m3u\"")
                    .ExecuteBufferedAsync();

                if (!string.IsNullOrEmpty(result.StandardOutput))
                {
                    var processIds = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (processIds.Length > 0 && int.TryParse(processIds[0], out int pid))
                    {
                        // Verify the process is actually an ffmpeg process
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc.ProcessName.Contains("ffmpeg"))
                            {
                                status.IsRunning = true;
                                status.ProcessId = pid;
                                status.Status = "Running";
                            }
                            else
                            {
                                status.IsRunning = false;
                                status.Status = "Process found but not ffmpeg";
                            }
                        }
                        catch (ArgumentException)
                        {
                            status.IsRunning = false;
                            status.Status = "Process ID invalid";
                        }
                    }
                }
                else
                {
                    status.IsRunning = false;
                    status.Status = "No process found";
                }
            }
            catch (Exception ex)
            {
                // Only log if it's a significant error
                if (!ex.Message.Contains("No such process"))
                {
                    Console.WriteLine($"⚠️ Error checking status for stream {streamId}: {ex.Message}");
                }
                status.Status = "Error checking status";
            }

            return status;
        }

        public static async Task<bool> IsStreamActuallyRunningAsync(int streamId)
        {
            var status = await GetRealTimeStreamStatusAsync(streamId);
            return status.IsRunning;
        }

        public static async Task<int?> GetActualProcessIdAsync(int streamId)
        {
            var status = await GetRealTimeStreamStatusAsync(streamId);
            return status.ProcessId;
        }
    }
}
