using MySqlConnector;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers
{
    public static class BatchDatabaseHelper
    {
        public class StreamStatusUpdate
        {
            public int StreamId { get; set; }
            public int ProcessId { get; set; }
            public bool IsExecuting { get; set; }
            public bool IsReportedDown { get; set; }
        }

        public class StreamErrorLog
        {
            public int StreamId { get; set; }
            public string Error { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        // 🚀 Batch update stream status to reduce database calls
        public static async Task BatchUpdateStreamStatus(List<StreamStatusUpdate> updates)
        {
            if (updates == null || updates.Count == 0)
                return;

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            await using var transaction = await cnn.BeginTransactionAsync();
            try
            {
                foreach (var update in updates)
                {
                    await using var cmd = cnn.CreateCommand();
                    cmd.CommandText = @"
                        UPDATE streams_info 
                        SET proceso_id = @pid, ejecutando = @exec, reportado_caido = @down 
                        WHERE stream_id = @id";
                    
                    cmd.Parameters.AddWithValue("@pid", update.ProcessId);
                    cmd.Parameters.AddWithValue("@exec", update.IsExecuting ? 1 : 0);
                    cmd.Parameters.AddWithValue("@down", update.IsReportedDown ? 1 : 0);
                    cmd.Parameters.AddWithValue("@id", update.StreamId);
                    
                    await cmd.ExecuteNonQueryAsync();
                }
                
                await transaction.CommitAsync();
                Console.WriteLine($"✅ Batch updated {updates.Count} stream statuses");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"❌ Batch update failed: {ex.Message}");
                throw;
            }
        }

        // 🚀 Batch insert stream errors to reduce database calls
        public static async Task BatchInsertStreamErrors(List<StreamErrorLog> errors)
        {
            if (errors == null || errors.Count == 0)
                return;

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            await using var transaction = await cnn.BeginTransactionAsync();
            try
            {
                foreach (var error in errors)
                {
                    await using var cmd = cnn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO streams_error (stream_id, error_message, timestamp) 
                        VALUES (@streamId, @error, @timestamp)";
                    
                    cmd.Parameters.AddWithValue("@streamId", error.StreamId);
                    cmd.Parameters.AddWithValue("@error", error.Error);
                    cmd.Parameters.AddWithValue("@timestamp", error.Timestamp);
                    
                    await cmd.ExecuteNonQueryAsync();
                }
                
                await transaction.CommitAsync();
                Console.WriteLine($"✅ Batch inserted {errors.Count} stream errors");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"❌ Batch insert failed: {ex.Message}");
                throw;
            }
        }

        // 🚀 Optimized query for active streams with better conditions
        public static async Task<List<StreamDb>> GetActiveStreamsOptimized()
        {
            var streams = new List<StreamDb>();

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            await using var cmd = cnn.CreateCommand();
            // 🚀 Optimized query with more specific conditions
            cmd.CommandText = @"
                SELECT fuente_stream, stream_id, probesize_ondemand, es_bajodemanda, 
                       transcode_audio, intervalo, segmentos, framerate, transcode, 
                       resolucion, bitrate, proceso_id, cgop, gop
                FROM streams_tl a
                INNER JOIN streams_info b ON a.id = b.stream_id
                WHERE a.habilitado = 1 
                  AND a.iniciado = 1 
                  AND a.es_bajodemanda = 0 
                  AND a.tipo = 1
                  AND b.ejecutando = 1
                ORDER BY a.id;"; // Added ORDER BY for consistent results

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                streams.Add(new StreamDb
                {
                    Fuente = reader.GetString(0),
                    StreamId = reader.GetInt32(1),
                    ProbeSize = reader.GetInt32(2),
                    EsBajoDemanda = reader.GetInt32(3),
                    TranscodeAudio = reader.GetString(4),
                    Intervalo = reader.GetInt16(5),
                    Segmentos = reader.GetInt16(6),
                    Framerate = reader.GetInt32(7),
                    Transcode = reader.GetInt32(8),
                    Resolucion = reader.GetString(9),
                    Bitrate = reader.GetString(10),
                    ProcesoId = reader.GetInt32(11),
                    CGOP = reader.GetInt32(12),
                    GOP = reader.GetInt32(13)
                });
            }

            return streams;
        }

        // 🚀 Get database connection pool statistics
        public static async Task<Dictionary<string, object>> GetDatabaseStats()
        {
            var stats = new Dictionary<string, object>();

            try
            {
                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync();

                // Get connection pool info
                stats["ConnectionString"] = cnn.ConnectionString;
                stats["Database"] = cnn.Database;
                stats["ServerVersion"] = cnn.ServerVersion;
                stats["State"] = cnn.State.ToString();
                stats["ConnectionTimeout"] = cnn.ConnectionTimeout;

                // Get cache stats
                stats["CacheSize"] = StreamDataCache.GetCacheSize();
                stats["CacheLastRefresh"] = StreamDataCache.GetLastRefreshTime();
                stats["CacheIsValid"] = StreamDataCache.IsCacheValid();

                await cnn.CloseAsync();
            }
            catch (Exception ex)
            {
                stats["Error"] = ex.Message;
            }

            return stats;
        }
    }
}
