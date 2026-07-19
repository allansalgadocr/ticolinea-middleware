namespace ticolinea.stream.service.Helpers;

// Node-wide gate: sólo UNA operación masiva de streams (restart-all / stop-all /
// start-all) puede correr a la vez. Las masivas iteran secuencialmente durante
// minutos; dos en paralelo se pisarían entre sí (y con el semáforo de arranque
// de 20 slots producirían una tormenta de reinicios). Un segundo request
// mientras hay una en curso recibe 409 del AdminController.
// Interlocked (no lock/SemaphoreSlim): la entrada es try-only, nunca se espera.
public static class MassOperationGate
{
    private static int _held; // 0 = libre, 1 = operación en curso

    // true = gate acquired; caller MUST Exit() when the operation finishes.
    public static bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public static void Exit() => Interlocked.Exchange(ref _held, 0);

    public static bool IsHeld => Volatile.Read(ref _held) == 1;
}
