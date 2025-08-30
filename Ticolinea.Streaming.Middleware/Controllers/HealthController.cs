using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Constantes;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetHealth()
        {
            var dbStats = await ConnectionPoolMonitor.GetPoolStatsAsync();
            var connectionInfo = await ConnectionPoolMonitor.GetConnectionInfoAsync();
            var isHealthy = await ConnectionPoolMonitor.TestConnectionAsync();

            var healthStatus = new
            {
                timestamp = DateTime.UtcNow,
                status = isHealthy ? "healthy" : "unhealthy",
                database = new
                {
                    connected = dbStats.IsConnected,
                    server = dbStats.ServerVersion,
                    database = dbStats.Database,
                    activeConnections = dbStats.ActiveConnections,
                    connectionTimeout = dbStats.ConnectionTimeout,
                    connectionInfo = connectionInfo,
                    error = dbStats.Error
                },
                system = new
                {
                    uptime = Environment.TickCount64,
                    memoryUsage = GC.GetTotalMemory(false),
                    processorCount = Environment.ProcessorCount,
                    osVersion = Environment.OSVersion.ToString()
                }
            };

            return isHealthy ? Ok(healthStatus) : StatusCode(503, healthStatus);
        }

        [HttpGet("database")]
        public async Task<IActionResult> GetDatabaseHealth()
        {
            var stats = await ConnectionPoolMonitor.GetPoolStatsAsync();
            var isHealthy = await ConnectionPoolMonitor.TestConnectionAsync();

            return isHealthy ? Ok(stats) : StatusCode(503, stats);
        }

        [HttpGet("connection-info")]
        public async Task<IActionResult> GetConnectionInfo()
        {
            var info = await ConnectionPoolMonitor.GetConnectionInfoAsync();
            return Ok(new { connectionInfo = info, timestamp = DateTime.UtcNow });
        }

        [HttpGet("pool-stats")]
        public async Task<IActionResult> GetPoolStats()
        {
            var stats = await ConnectionPoolMonitor.GetPoolStatsAsync();
            return Ok(stats);
        }

        [HttpGet("environment")]
        public IActionResult GetEnvironmentInfo()
        {
            var environmentInfo = new
            {
                timestamp = DateTime.UtcNow,
                environment = StreamExecutionGuard.GetEnvironmentInfo(),
                streamExecution = new
                {
                    enabled = Global.ENABLE_STREAM_EXECUTION,
                    ffmpegProcesses = Global.ENABLE_FFMPEG_PROCESSES
                },
                buildConfiguration = new
                {
#if DEBUG
                    isDebug = true,
                    configuration = "Debug"
#else
                    isDebug = false,
                    configuration = "Release"
#endif
                }
            };

            return Ok(environmentInfo);
        }
    }
}
