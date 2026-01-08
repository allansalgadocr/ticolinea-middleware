using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using ticolinea.stream.service.Constantes;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Services;

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
            var process = Process.GetCurrentProcess();
            var processUptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

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
                    processUptimeSeconds = (long)processUptime.TotalSeconds,
                    processUptimeMs = (long)processUptime.TotalMilliseconds,
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

        [HttpGet("database-performance")]
        public IActionResult GetDatabasePerformance()
        {
            var performanceInfo = new
            {
                timestamp = DateTime.UtcNow,
                cache = new
                {
                    enabled = true,
                    cacheSize = Jobs._cachedStreams?.Count ?? 0,
                    lastRefresh = Jobs._lastCacheRefresh,
                    isCacheValid = Jobs._cachedStreams != null && DateTime.UtcNow - Jobs._lastCacheRefresh < TimeSpan.FromSeconds(30)
                },
                optimizations = new
                {
                    jobFrequencies = new
                    {
                        checkStreams = "Every 2 minutes (optimized from 1 minute)",
                        killConnections = "Every 10 minutes (optimized from 5 minutes)",
                        removeLargeFiles = "Every 15 minutes (optimized from 5 minutes)"
                    },
                    caching = new
                    {
                        enabled = true,
                        expirationSeconds = 15,
                        description = "Stream data cached to reduce database queries (optimized for 155+ streams)"
                    },
                    scaling = new
                    {
                        parallelStreams = "20 concurrent stream operations (was 5)",
                        parallelCodecs = "15 concurrent codec checks (was 5)",
                        targetScale = "Optimized for 155+ concurrent streams"
                    }
                }
            };

            return Ok(performanceInfo);
        }

        [HttpGet("streaming-performance")]
        public IActionResult GetStreamingPerformance()
        {
            var streamingStats = StreamingService.GetPerformanceStats();
            return Ok(streamingStats);
        }

        [HttpGet("batch-processing")]
        public IActionResult GetBatchProcessingStats()
        {
            var batchStats = Data.Streams.GetBatchProcessingStats();
            return Ok(batchStats);
        }
    }
}
