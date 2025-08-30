using System.Collections.Concurrent;
using log4net;
using MySqlConnector;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data
{
    public static class Streams
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Streams));
        
        // Thread-safe collections and counters
        private static readonly ConcurrentQueue<(int streamId, int processId, bool isStart)> _pendingUpdates = new();
        private static readonly ConcurrentQueue<(int streamId, bool isCaido)> _pendingStatusUpdates = new();
        private static readonly object _batchLock = new object();
        private static readonly object _statusBatchLock = new object();
        
        // Atomic counters for efficient batch size checking
        private static volatile int _updateCount = 0;
        private static volatile int _statusUpdateCount = 0;
        
        // Configuration
        private const int _batchSize = 20;
        private static readonly TimeSpan _batchInterval = TimeSpan.FromSeconds(5);
        private static readonly Timer _batchTimer;
        private static volatile int _isProcessing = 0;
        private static volatile int _isProcessingStatus = 0;

        static Streams()
        {
            _batchTimer = new Timer(TimerCallback, null, _batchInterval, _batchInterval);
        }

        private static void TimerCallback(object? state)
        {
            // Only trigger if not already processing and there are items
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0 && _updateCount > 0)
            {
                _ = Task.Run(ProcessBatchUpdates);
            }
            
            if (Interlocked.CompareExchange(ref _isProcessingStatus, 1, 0) == 0 && _statusUpdateCount > 0)
            {
                _ = Task.Run(ProcessStatusBatchUpdates);
            }
        }

        public static void InsertaStreamError(string error)
        {
            log.Error($"[StreamError] {error}");
        }

        // Optimized batch processing for stream updates
        public static void QueueStreamUpdate(int streamId, int processId, bool isStart)
        {
            _pendingUpdates.Enqueue((streamId, processId, isStart));
            Interlocked.Increment(ref _updateCount);
            
            // Process immediately if batch is full (atomic check)
            if (_updateCount >= _batchSize && Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0)
            {
                _ = Task.Run(ProcessBatchUpdates);
            }
        }

        public static void QueueStatusUpdate(int streamId, bool isCaido)
        {
            _pendingStatusUpdates.Enqueue((streamId, isCaido));
            Interlocked.Increment(ref _statusUpdateCount);
            
            // Process immediately if batch is full (atomic check)
            if (_statusUpdateCount >= _batchSize && Interlocked.CompareExchange(ref _isProcessingStatus, 1, 0) == 0)
            {
                _ = Task.Run(ProcessStatusBatchUpdates);
            }
        }

        private static async Task ProcessBatchUpdates()
        {
            try
            {
                var updates = new List<(int streamId, int processId, bool isStart)>();
                
                // Collect all available updates atomically
                lock (_batchLock)
                {
                    while (updates.Count < _batchSize && _pendingUpdates.TryDequeue(out var update))
                    {
                        updates.Add(update);
                    }
                    Interlocked.Add(ref _updateCount, -updates.Count);
                }

                if (updates.Count == 0) return;

                await ProcessBatchUpdatesInternal(updates).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        private static async Task ProcessBatchUpdatesInternal(List<(int streamId, int processId, bool isStart)> updates)
        {
            try
            {
                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync().ConfigureAwait(false);
                await using var transaction = await cnn.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    // Optimized batch update using CASE + IN for starts
                    var startUpdates = updates.Where(u => u.isStart).ToList();
                    if (startUpdates.Count > 0)
                    {
                        var startSql = @"
                            UPDATE streams_info 
                            SET proceso_id = CASE stream_id 
                                " + string.Join("\n", startUpdates.Select((u, i) => $"WHEN {u.streamId} THEN {u.processId}")) + @"
                            END,
                            ejecutando = 1,
                            reportado_caido = 0
                            WHERE stream_id IN (" + string.Join(",", startUpdates.Select(u => u.streamId)) + ")";
                        
                        await using var startCmd = cnn.CreateCommand();
                        startCmd.CommandText = startSql;
                        startCmd.CommandTimeout = 30;
                        startCmd.Transaction = transaction;
                        await startCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Optimized batch update using IN for stops
                    var stopUpdates = updates.Where(u => !u.isStart).ToList();
                    if (stopUpdates.Count > 0)
                    {
                        var stopSql = @"
                            UPDATE streams_info 
                            SET proceso_id = -1, 
                                ejecutando = 0 
                            WHERE stream_id IN (" + string.Join(",", stopUpdates.Select(u => u.streamId)) + ")";
                        
                        await using var stopCmd = cnn.CreateCommand();
                        stopCmd.CommandText = stopSql;
                        stopCmd.CommandTimeout = 30;
                        stopCmd.Transaction = transaction;
                        await stopCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    await transaction.CommitAsync().ConfigureAwait(false);
                    log.Debug($"Processed {updates.Count} stream updates in batch");
                }
                catch
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error processing batch updates: {ex.Message}");
            }
        }

        private static async Task ProcessStatusBatchUpdates()
        {
            try
            {
                var updates = new List<(int streamId, bool isCaido)>();
                
                // Collect all available updates atomically
                lock (_statusBatchLock)
                {
                    while (updates.Count < _batchSize && _pendingStatusUpdates.TryDequeue(out var update))
                    {
                        updates.Add(update);
                    }
                    Interlocked.Add(ref _statusUpdateCount, -updates.Count);
                }

                if (updates.Count == 0) return;

                await ProcessStatusBatchUpdatesInternal(updates).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessingStatus, 0);
            }
        }

        private static async Task ProcessStatusBatchUpdatesInternal(List<(int streamId, bool isCaido)> updates)
        {
            try
            {
                await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
                await cnn.OpenAsync().ConfigureAwait(false);
                await using var transaction = await cnn.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    // Optimized batch update using CASE + IN
                    var sql = @"
                        UPDATE streams_info 
                        SET reportado_caido = CASE stream_id 
                            " + string.Join("\n", updates.Select((u, i) => $"WHEN {u.streamId} THEN {(u.isCaido ? "1" : "0")}")) + @"
                        END
                        WHERE stream_id IN (" + string.Join(",", updates.Select(u => u.streamId)) + ")";
                    
                    await using var cmd = cnn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 30;
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    await transaction.CommitAsync().ConfigureAwait(false);
                    log.Debug($"Processed {updates.Count} status updates in batch");
                }
                catch
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error processing status batch updates: {ex.Message}");
            }
        }

        public static (int pendingUpdates, int pendingStatusUpdates, bool isProcessing, bool isProcessingStatus) GetBatchProcessingStats()
        {
            return (_updateCount, _statusUpdateCount, _isProcessing == 1, _isProcessingStatus == 1);
        }

        // Cleanup method for proper resource disposal
        public static void Dispose()
        {
            _batchTimer?.Dispose();
        }

        public static async Task<List<Bouquet>> ObtenerCanalesSinOrdenAsync(string? idPaqueteId="")
        {
            List<Bouquet> bouquet = new List<Bouquet>();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed)
                        await cnn.OpenAsync();

                    string sql = @"SELECT a.id, a.nombre_stream, a.imagen_stream, b.category_name, a.tipo, a.contenedor, a.canal_epg
                                        FROM streams_tl a
                                        INNER JOIN stream_categories b ON a.id_categoria = b.id
                                        WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id = 0
                                        ORDER BY a.orden ASC; ";

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        sql = @"SELECT a.id, a.nombre_stream, a.imagen_stream, b.category_name, a.tipo, a.contenedor, a.canal_epg,pp.activo
                                    FROM streams_tl a
                                    INNER JOIN stream_categories b ON a.id_categoria = b.id
                                    INNER JOIN paquete_tv_streams p ON a.id = p.stream_id
                                    INNER JOIN paquete_tv pp ON p.id_paquete_tv = pp.id_paquete_tv
                                    WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id = 0
                                    AND p.id_paquete_tv = @IdPaqueteTV and pp.activo = 1
                                    ORDER BY a.orden ASC; ";
                    }


                    cmd.CommandText = sql;

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        cmd.Parameters.AddWithValue("@IdPaqueteTV", idPaqueteId);
                    }

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                            });
                        }
                }
            }

            return bouquet;
        }

        public static async Task<List<Bouquet>> ObtenerCanalesConOrdenAsync(string? idPaqueteId="")
        {
            List<Modelos.Bouquet> bouquetCustom = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed)
                        await cnn.OpenAsync();

                    string sql = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg, canal_id FROM streams_tl a " +
                                                        "INNER JOIN stream_categories b " +
                                                        "on a.id_categoria = b.id " +
                                                        "WHERE habilitado=1 and tipo=1 and canal_id != 0 " +
                                                        "order by a.canal_id asc;";

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        sql = @"SELECT a.id,a.nombre_stream,a.imagen_stream,b.category_name,a.tipo,a.contenedor, a.canal_epg, a.canal_id
                                    FROM streams_tl a
                                    INNER JOIN stream_categories b ON a.id_categoria = b.id
                                    INNER JOIN paquete_tv_streams p ON a.id = p.stream_id
                                    INNER JOIN paquete_tv pp ON p.id_paquete_tv = pp.id_paquete_tv
                                    WHERE a.habilitado = 1 AND a.tipo = 1 AND a.canal_id != 0
                                    AND p.id_paquete_tv = @IdPaqueteTV and pp.activo = 1
                                    order by a.canal_id asc; ";
                    }


                    cmd.CommandText = sql;

                    if (!string.IsNullOrEmpty(idPaqueteId))
                    {
                        cmd.Parameters.AddWithValue("@IdPaqueteTV", idPaqueteId);
                    }

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            bouquetCustom.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                                CanalId = reader.GetInt32(7)
                            });
                        }

                }
            }

            return bouquetCustom;
        }
    }
}
