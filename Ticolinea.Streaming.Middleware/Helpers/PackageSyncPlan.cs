using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

public class SyncDecision
{
    public List<CatalogStream> Upserts { get; set; } = new();
    public List<int> IdsToDisable { get; set; } = new();
    public bool SkipDisable { get; set; }
    // Existing synced channels whose fuente_stream in the catalog differs from the
    // current DB row. New channels (not synced yet) are NEVER listed here — there is
    // no running process to bounce. Used post-commit to restart running channels so
    // their SupervisarStream loop (which captured the OLD StreamDb at start) picks
    // up the new source. Kill-only is not enough: the supervised relaunch would
    // reuse the stale fuente.
    public List<int> SourceChangedIds { get; set; } = new();
    // Upserts split by whether the node already had the row (currentSyncedIds).
    public int Added { get; set; }
    public int Updated { get; set; }
}

// Outcome of one sync run. Counts come pure from the plan (FromDecision);
// Restarted/CompletedUtc/Forced are filled in by PackageSyncService.
public class PackageSyncResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Disabled { get; set; }
    public int SourcesChanged { get; set; }
    public int Restarted { get; set; }
    public bool GuardTripped { get; set; }
    public bool Forced { get; set; }
    public DateTime CompletedUtc { get; set; }

    public static PackageSyncResult FromDecision(SyncDecision d) => new()
    {
        Added = d.Added,
        Updated = d.Updated,
        Disabled = d.IdsToDisable.Count,
        SourcesChanged = d.SourceChangedIds.Count,
        GuardTripped = d.SkipDisable
    };

    public string Summary() =>
        $"{Added} added, {Updated} updated, {Disabled} disabled, " +
        $"{SourcesChanged} sources changed, {Restarted} restarted" +
        (GuardTripped ? "; guard tripped, disables skipped" : "");
}

// Pure set-math + undersized-catalog guard. No I/O.
public static class PackageSyncPlan
{
    // enabledSyncedCount = COUNT(sincronizado=1 AND habilitado=1) — "channels we're actually serving now".
    // currentSyncedIds is the FULL sincronizado=1 set, which only ever accumulates (dropped channels keep
    // sincronizado=1 with habilitado=0 by design). The guard must use the enabled count as its denominator,
    // not the ever-growing full set, or a healthy catalog eventually looks "undersized" forever and disables
    // never run again.
    //
    // enforceGuard: the undersized guard applies ONLY when true. The UNATTENDED path
    // (Jobs.SyncPackageCatalog, boot + every 6h) keeps it on — a suspect shrunken
    // catalog must not mass-disable a healthy node. The operator-FORCED path passes
    // false: whatever the panel returns is applied in full — adds, updates, AND
    // mass-disables. A deliberate 50% shrink is operator intent and must apply.
    //
    // currentFuentes: id → fuente_stream of the current synced DB rows, used to
    // detect source changes (SourceChangedIds). Null/empty fuentes compare equal so
    // a NULL-vs-"" mismatch never triggers a spurious mass restart.
    public static SyncDecision Build(
        IReadOnlyList<CatalogStream> catalog,
        IReadOnlyCollection<int> currentSyncedIds,
        int enabledSyncedCount,
        bool enforceGuard = true,
        IReadOnlyDictionary<int, string?>? currentFuentes = null)
    {
        var catalogIds = new HashSet<int>(catalog.Select(c => c.Id));
        var currentIds = currentSyncedIds as ISet<int> ?? new HashSet<int>(currentSyncedIds);
        var undersized = enforceGuard
            && enabledSyncedCount > 0
            && catalog.Count < 0.5 * enabledSyncedCount;

        var decision = new SyncDecision
        {
            Upserts = catalog.ToList(),
            SkipDisable = undersized,
            Added = catalog.Count(c => !currentIds.Contains(c.Id)),
        };
        decision.Updated = catalog.Count - decision.Added;

        if (!undersized)
            decision.IdsToDisable = currentSyncedIds.Where(id => !catalogIds.Contains(id)).ToList();

        if (currentFuentes != null)
            decision.SourceChangedIds = catalog
                .Where(c => currentFuentes.TryGetValue(c.Id, out var fuenteActual)
                            && !SameFuente(c.FuenteStream, fuenteActual))
                .Select(c => c.Id)
                .ToList();

        return decision;
    }

    private static bool SameFuente(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
}
