using System.Collections.Concurrent;

namespace ticolinea.stream.service.Helpers;

// Lock por stream compartido entre el control plane del operador
// (AdminController start/stop/restart) y el OutputWatchdogService, para que una
// acción del operador y un reinicio del watchdog sobre el mismo canal nunca se
// pisen. El operador espera (WaitAsync); el watchdog usa try-acquire no
// bloqueante y simplemente salta el ciclo si hay una acción en vuelo.
// Los semáforos nunca se eliminan del registro: uno por stream conocido, coste
// despreciable, y así no hay carrera crear/eliminar.
public static class StreamLocks
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public static SemaphoreSlim For(int streamId) =>
        _locks.GetOrAdd(streamId, _ => new SemaphoreSlim(1, 1));
}
