using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

public class SyncDecision
{
    public List<CatalogStream> Upserts { get; set; } = new();
    public List<int> IdsToDisable { get; set; } = new();
    public bool SkipDisable { get; set; }
}

// Pure set-math + undersized-catalog guard. No I/O.
public static class PackageSyncPlan
{
    // enabledSyncedCount = COUNT(sincronizado=1 AND habilitado=1) — "channels we're actually serving now".
    // currentSyncedIds is the FULL sincronizado=1 set, which only ever accumulates (dropped channels keep
    // sincronizado=1 with habilitado=0 by design). The guard must use the enabled count as its denominator,
    // not the ever-growing full set, or a healthy catalog eventually looks "undersized" forever and disables
    // never run again.
    public static SyncDecision Build(
        IReadOnlyList<CatalogStream> catalog,
        IReadOnlyCollection<int> currentSyncedIds,
        int enabledSyncedCount)
    {
        var catalogIds = new HashSet<int>(catalog.Select(c => c.Id));
        var undersized = enabledSyncedCount > 0
            && catalog.Count < 0.5 * enabledSyncedCount;

        var decision = new SyncDecision
        {
            Upserts = catalog.ToList(),
            SkipDisable = undersized
        };
        if (!undersized)
            decision.IdsToDisable = currentSyncedIds.Where(id => !catalogIds.Contains(id)).ToList();
        return decision;
    }
}
