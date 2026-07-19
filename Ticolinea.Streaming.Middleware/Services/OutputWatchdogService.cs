using System.Collections.Concurrent;
using log4net;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Services;

// Watchdog de progreso de salida (config-gated, default OFF).
//
// La supervisión existente (StreamingService/RevisarStreams) sólo detecta ffmpeg
// MUERTO. Este servicio detecta el caso contrario: ffmpeg VIVO pero colgado — el
// proceso existe y no produce salida nueva (playlist HLS congelada). Toda la
// lógica de decisión está en WatchdogPolicy (pura, unit-testeada); aquí sólo hay
// I/O: leer playlist, verificar proceso, matar el colgado.
//
// Ruta de reinicio — invariante: un reinicio de watchdog NUNCA resetea el circuit
// breaker. Por eso NO se usa ForzarInicioInmediato (borra _failureTracker y
// _lastProcessStart, StreamingService.cs) ni Jobs.DetenerProceso (cancela la
// supervisión → CleanupStreamState borra lo mismo). En su lugar,
// Jobs.MatarProcesoParaWatchdog mata SOLO el PID: el loop SupervisarStream que
// sigue vivo ve el exit != 0 y relanza por el camino supervisado normal, con
// breaker, backoff e intervalo mínimo de 12s intactos. El kill tampoco ALIMENTA
// el breaker: se marca vía StreamingService.MarkWatchdogKill y el manejo del exit
// consume la marca sin incrementar _failureTracker — el watchdog ya gasta su
// propio presupuesto (WatchdogPolicy) por cada kill. Además la política exige
// ProcessUptimeSeconds > 0 (_lastProcessStart rastreado), que sólo es cierto
// mientras esa supervisión está viva — el kill no puede dejar huérfano a nadie.
//
// Gate interno (no en el registro DI): Watchdog:Enabled se lee en cada ciclo, así
// un toggle por reloadOnChange aplica sin reiniciar el servicio. Default: false.
public sealed class OutputWatchdogService : BackgroundService
{
    private static readonly ILog _logger = LogManager.GetLogger(typeof(OutputWatchdogService));
    private static readonly TimeSpan _cycleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _doubleReadDelay = TimeSpan.FromSeconds(2);

    // Estado por stream. Estático a propósito (idioma del codebase: StreamingService,
    // Jobs) para que AdminController pueda exponer watchdogRestarts/degraded sin DI.
    private static readonly ConcurrentDictionary<int, StreamProgress> _states = new();

    private readonly IConfiguration _configuration;

    public OutputWatchdogService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // Observabilidad para AdminController /api/admin/streams. Con el watchdog
    // deshabilitado el registro se vacía → 0/false.
    public static int GetRestartCount(int streamId) =>
        _states.TryGetValue(streamId, out var s) ? s.TotalRestarts : 0;

    public static bool IsDegraded(int streamId) =>
        _states.TryGetValue(streamId, out var s) && s.Degraded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Debug("OutputWatchdogService iniciado (gate interno: Watchdog:Enabled).");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue<bool>("Watchdog:Enabled")
                    && StreamExecutionGuard.CanExecuteStreams())
                {
                    await RunCycleAsync(stoppingToken);
                }
                else if (!_states.IsEmpty)
                {
                    // Deshabilitado: limpiar para que AdminController reporte 0/false.
                    _states.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"Watchdog: error en ciclo: {ex.Message}", ex);
            }

            try
            {
                await Task.Delay(_cycleInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunCycleAsync(CancellationToken ct)
    {
        // Candidatos = el mismo conjunto que reinicia RevisarStreams (habilitado=1,
        // iniciado=1, es_bajodemanda=0, tipo=1), vía el mismo helper cacheado.
        var streams = await Jobs.ObtenerStreamsActivos();

        // Podar estado de canales que ya no son candidatos (deshabilitados/detenidos
        // por el operador) para no mostrar degraded/restarts fantasma.
        var candidateIds = new HashSet<int>(streams.Select(s => s.StreamId));
        foreach (var staleId in _states.Keys.Where(id => !candidateIds.Contains(id)).ToList())
            _states.TryRemove(staleId, out _);

        foreach (var stream in streams)
        {
            ct.ThrowIfCancellationRequested();
            var state = _states.GetOrAdd(stream.StreamId, _ => new StreamProgress());
            var obs = await ObserveAsync(stream.StreamId);
            var action = WatchdogPolicy.Evaluate(obs, state);

            switch (action)
            {
                case WatchdogAction.Restart:
                case WatchdogAction.MarkDegraded:
                    // Confirmar "VIVO pero colgado" antes de gastar presupuesto o
                    // degradar: si el proceso está muerto, eso es problema de la
                    // supervisión/RevisarStreams, no del watchdog. (El pgrep se hace
                    // sólo aquí, no en cada ciclo por canal — 155 canales × 5s.)
                    var live = await StreamStatusHelper.GetRealTimeStreamStatusAsync(stream.StreamId);
                    if (!live.IsRunning)
                        break;

                    if (action == WatchdogAction.MarkDegraded)
                    {
                        WatchdogPolicy.Apply(action, obs, state);
                        // ERROR una sola vez: con Degraded=true la política pasa a
                        // devolver CountStale hasta que el progreso lo limpie.
                        _logger.Error(
                            $"Watchdog: stream {stream.StreamId} agotó el presupuesto " +
                            $"({WatchdogPolicy.MaxRestartsPerWindow} reinicios en {WatchdogPolicy.BudgetWindow.TotalMinutes:F0} min) " +
                            "y sigue sin producir salida. Marcado DEGRADED: sólo observación hasta que el output avance.");
                    }
                    else
                    {
                        await TryRestartAsync(stream.StreamId, state, ct);
                    }
                    break;

                default:
                    WatchdogPolicy.Apply(action, obs, state);
                    break;
            }
        }
    }

    private static async Task<WatchdogObservation> ObserveAsync(int streamId)
    {
        var (ageSeconds, playlist) = await PlaylistMetrics.ReadAsync(streamId);
        return new WatchdogObservation(
            NowUtc: DateTime.UtcNow,
            ProcessUptimeSeconds: StreamingService.GetStreamUptimeSeconds(streamId),
            HasPlaylist: playlist != null,
            MediaSequence: playlist?.MediaSequence ?? 0,
            LastSegment: playlist?.LastSegment ?? "",
            TargetDuration: playlist?.TargetDuration ?? 0,
            PlaylistAgeSeconds: ageSeconds);
    }

    private static async Task TryRestartAsync(int streamId, StreamProgress state, CancellationToken ct)
    {
        // Lock por stream compartido con AdminController: try-acquire no bloqueante;
        // si hay una acción del operador en vuelo, saltar este ciclo (sin mutar estado).
        var gate = StreamLocks.For(streamId);
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            _logger.Debug($"Watchdog: stream {streamId} con acción de operador en vuelo; ciclo saltado.");
            return;
        }

        try
        {
            // Double-read guard: re-leer la playlist 2s después. Si hubo avance en
            // ese lapso (o el estado ya no justifica reiniciar), aplicar ESA acción
            // y abortar — el presupuesto sólo se gasta en reinicios confirmados.
            await Task.Delay(_doubleReadDelay, ct);
            var obs = await ObserveAsync(streamId);
            var action = WatchdogPolicy.Evaluate(obs, state);

            if (action != WatchdogAction.Restart)
            {
                WatchdogPolicy.Apply(action, obs, state);
                _logger.Debug($"Watchdog: reinicio de stream {streamId} abortado por double-read (acción: {action}).");
                return;
            }

            // WARN de intención (aún sin presupuesto gastado): el kill viene ahora.
            _logger.Warn(
                $"Watchdog: reiniciando stream {streamId} — ffmpeg vivo sin progreso de salida " +
                $"(playlist age {(obs.PlaylistAgeSeconds.HasValue ? obs.PlaylistAgeSeconds.Value.ToString("F0") + "s" : "sin playlist")}, " +
                $"mediaSequence {obs.MediaSequence}, uptime {obs.ProcessUptimeSeconds:F0}s).");

            // Matar SOLO el proceso: la supervisión viva lo relanza por el camino
            // normal, preservando circuit breaker / backoff / intervalo mínimo.
            // El presupuesto se gasta DESPUÉS y sólo si el kill encontró y mató un
            // proceso: un kill fallido/en carrera aplica CountStale, no Restart —
            // tres kills fallidos no pueden degradar un canal que nunca se reinició.
            var killed = await Jobs.MatarProcesoParaWatchdog(streamId);
            WatchdogPolicy.Apply(killed ? WatchdogAction.Restart : WatchdogAction.CountStale, obs, state);

            if (!killed)
            {
                _logger.Warn($"Watchdog: no se encontró proceso vivo que matar para stream {streamId}; presupuesto no consumido.");
            }
            else if (state.Degraded)
            {
                _logger.Warn(
                    $"Watchdog: stream {streamId} reiniciado (sonda DEGRADED, 1 cada " +
                    $"{WatchdogPolicy.DegradedRetryInterval.TotalMinutes:F0} min; no consume presupuesto de ventana).");
            }
            else
            {
                _logger.Warn(
                    $"Watchdog: stream {streamId} reiniciado. " +
                    $"Reinicio {state.WatchdogRestartsInWindow}/{WatchdogPolicy.MaxRestartsPerWindow} en la ventana de {WatchdogPolicy.BudgetWindow.TotalMinutes:F0} min.");
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
