namespace ticolinea.stream.service.Helpers;

// Estado por canal del watchdog de progreso de salida (OutputWatchdogService).
// Mutable a propósito: una instancia por stream vive en el registro estático del
// servicio; TODAS las transiciones pasan por WatchdogPolicy.Apply para que la
// lógica quede en un solo lugar testeable.
public sealed class StreamProgress
{
    public long MediaSequence;
    public string LastSegment = "";
    public DateTime LastProgressUtc;             // default = aún sin observación base
    public DateTime LastRestartUtc;              // default = nunca reiniciado por watchdog
    public int ConsecutiveStaleChecks;
    public int WatchdogRestartsInWindow;         // presupuesto: ventana deslizante de 10 min
    public DateTime WindowStartUtc;
    public bool Degraded;                        // presupuesto agotado — sólo observar
    public int ProgressAdvancesWhileDegraded;    // avances vistos estando Degraded (2 lo limpian)
    public int TotalRestarts;                    // reinicios de watchdog desde el boot del servicio (observabilidad)
}

// Lo que el servicio observó de un canal en un ciclo. Inmutable; el reloj (NowUtc)
// viene de afuera para que la política sea determinista y unit-testeable.
public sealed record WatchdogObservation(
    DateTime NowUtc,
    double ProcessUptimeSeconds,   // StreamingService.GetStreamUptimeSeconds; <= 0 = desconocido
    bool HasPlaylist,              // false = playlist ilegible/ausente (cuenta como stale pasado el grace)
    long MediaSequence,
    string LastSegment,
    int TargetDuration,            // 0 si desconocido
    double? PlaylistAgeSeconds);   // null si no hay playlist

public enum WatchdogAction
{
    None,           // dentro del grace, o sin datos concluyentes, o no-stale
    Progress,       // hubo avance (o primera observación): resetear contador stale
    CountStale,     // stale confirmado pero sin reiniciar (falta 2do chequeo, cooldown, o Degraded)
    Restart,        // matar el ffmpeg colgado y dejar que la supervisión lo relance
    MarkDegraded,   // presupuesto agotado: marcar Degraded (log ERROR una sola vez)
    ClearDegraded   // 2do avance estando Degraded: limpiar y resetear presupuesto
}

// Núcleo de decisión PURO del watchdog de progreso (patrón PackageSyncPlan /
// FfmpegInputPolicy / HlsPlaylistInfo: sin I/O, sin reloj propio, sin estado global).
// Evaluate NO muta el estado — devuelve la acción; Apply ejecuta la transición.
// Esa separación es la que permite el double-read guard del servicio: evaluar,
// esperar 2s, re-evaluar con una observación fresca y recién entonces aplicar.
public static class WatchdogPolicy
{
    // Umbrales del spec aprobado. Constantes (no config): son invariantes de la
    // política, no perillas por proveedor.
    public const int GraceMultiplier = 4;                // grace = max(4×targetDuration, 30s)
    public const int GraceMinSeconds = 30;
    public const int StaleMultiplier = 3;                // stale = max(3×targetDuration, 20s)
    public const int StaleMinSeconds = 20;
    public const int MinConsecutiveStaleChecks = 2;      // exigir 2 chequeos stale seguidos
    public const int RestartCooldownSeconds = 30;        // entre reinicios de watchdog
    public const int MaxRestartsPerWindow = 3;           // presupuesto por ventana
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromMinutes(10);
    public const int AdvancesToClearDegraded = 2;        // avances que limpian Degraded

    public static double GraceSeconds(int targetDuration) =>
        Math.Max(GraceMultiplier * Math.Max(0, targetDuration), GraceMinSeconds);

    public static double StaleThresholdSeconds(int targetDuration) =>
        Math.Max(StaleMultiplier * Math.Max(0, targetDuration), StaleMinSeconds);

    public static WatchdogAction Evaluate(WatchdogObservation obs, StreamProgress state)
    {
        // Primera observación: fijar línea base, nunca stale (no hay contra qué comparar).
        bool firstObservation = state.LastProgressUtc == default;

        // Avance = cambió mediaSequence O cambió el último segmento.
        bool progressed = firstObservation ||
                          (obs.HasPlaylist &&
                           (obs.MediaSequence != state.MediaSequence ||
                            !string.Equals(obs.LastSegment, state.LastSegment, StringComparison.Ordinal)));

        if (progressed)
        {
            if (state.Degraded && state.ProgressAdvancesWhileDegraded + 1 >= AdvancesToClearDegraded)
                return WatchdogAction.ClearDegraded;
            return WatchdogAction.Progress;
        }

        // Grace de arranque: nunca stale dentro de max(4×targetDuration, 30s) del inicio
        // del proceso. Uptime <= 0 = _lastProcessStart no rastreado (p.ej. ffmpeg huérfano
        // de un boot anterior del servicio): sin fecha de inicio no se puede salir del
        // grace — el watchdog NO actúa sobre procesos que este servicio no lanzó.
        double grace = GraceSeconds(obs.TargetDuration);
        if (obs.ProcessUptimeSeconds <= 0 || obs.ProcessUptimeSeconds < grace)
            return WatchdogAction.None;

        // Stale: playlist vieja (mtime), o contenido sin cambio desde el último avance,
        // o playlist ausente/ilegible pasado el grace.
        double staleThreshold = StaleThresholdSeconds(obs.TargetDuration);
        double noChangeSeconds = (obs.NowUtc - state.LastProgressUtc).TotalSeconds;
        bool stale = !obs.HasPlaylist
                     || (obs.PlaylistAgeSeconds ?? double.MaxValue) >= staleThreshold
                     || noChangeSeconds >= staleThreshold;
        if (!stale)
            return WatchdogAction.None;

        // Stale confirmado en este chequeo (el conteo lo materializa Apply).
        int staleChecksIncludingThis = state.ConsecutiveStaleChecks + 1;
        if (staleChecksIncludingThis < MinConsecutiveStaleChecks)
            return WatchdogAction.CountStale;

        // Degraded: presupuesto agotado — observar solamente, jamás reiniciar.
        if (state.Degraded)
            return WatchdogAction.CountStale;

        // Cooldown entre reinicios de watchdog.
        if ((obs.NowUtc - state.LastRestartUtc).TotalSeconds < RestartCooldownSeconds)
            return WatchdogAction.CountStale;

        // Presupuesto: ventana deslizante de 10 min (expirada = cuenta 0).
        int restartsInWindow = obs.NowUtc - state.WindowStartUtc >= BudgetWindow
            ? 0
            : state.WatchdogRestartsInWindow;
        if (restartsInWindow >= MaxRestartsPerWindow)
            return WatchdogAction.MarkDegraded;

        return WatchdogAction.Restart;
    }

    // Ejecuta la transición de estado que corresponde a la acción evaluada.
    // Determinista: sólo usa obs + state (nada de DateTime.UtcNow aquí).
    public static void Apply(WatchdogAction action, WatchdogObservation obs, StreamProgress state)
    {
        switch (action)
        {
            case WatchdogAction.Progress:
                if (state.Degraded)
                    state.ProgressAdvancesWhileDegraded++;
                RecordProgress(obs, state);
                break;

            case WatchdogAction.ClearDegraded:
                state.Degraded = false;
                state.ProgressAdvancesWhileDegraded = 0;
                state.WatchdogRestartsInWindow = 0;
                state.WindowStartUtc = obs.NowUtc;
                RecordProgress(obs, state);
                break;

            case WatchdogAction.CountStale:
                state.ConsecutiveStaleChecks++;
                break;

            case WatchdogAction.Restart:
                if (obs.NowUtc - state.WindowStartUtc >= BudgetWindow)
                {
                    state.WindowStartUtc = obs.NowUtc;
                    state.WatchdogRestartsInWindow = 0;
                }
                state.WatchdogRestartsInWindow++;
                state.TotalRestarts++;
                state.LastRestartUtc = obs.NowUtc;
                state.ConsecutiveStaleChecks = 0;
                state.ProgressAdvancesWhileDegraded = 0;
                // Borrón y cuenta nueva: el proceso nuevo tiene su propio grace
                // (uptime se resetea en LanzarProcesoFfmpeg) y no debe heredar
                // el "sin cambio desde" del proceso colgado.
                state.LastProgressUtc = obs.NowUtc;
                break;

            case WatchdogAction.MarkDegraded:
                state.Degraded = true;
                state.ProgressAdvancesWhileDegraded = 0;
                state.ConsecutiveStaleChecks++;
                break;

            case WatchdogAction.None:
                break;
        }
    }

    private static void RecordProgress(WatchdogObservation obs, StreamProgress state)
    {
        if (obs.HasPlaylist)
        {
            state.MediaSequence = obs.MediaSequence;
            state.LastSegment = obs.LastSegment;
        }
        state.LastProgressUtc = obs.NowUtc;
        state.ConsecutiveStaleChecks = 0;
    }
}
