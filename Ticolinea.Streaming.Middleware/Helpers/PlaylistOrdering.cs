using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

// Builds the final channel order from a fixed-position rule: channels the
// operator pinned to a slot (canal_id != 0) versus everything else (canal_id == 0).
//
// The contract (max-effort, 2026-08): a pin with a unique canal_id in
// [1..MaxPosition] ALWAYS lands exactly on its number. Unpinned channels fill
// the holes in id order; holes nothing can fill become placeholder entries so
// later pins still sit on their numbers. Duplicate losers take the first open
// slot at/after their target; pins beyond MaxPosition (typo guard) lose
// exactness and flow to the end.
//
// One algorithm, callers split by shape: the device playlist wants the
// placeholder-padded form (MergeByFixedPosition); the console list and the
// JSON API want the compact form with the gaps removed (ByFixedPosition).
public static class PlaylistOrdering
{
    // Highest POSICIÓN the console accepts and the playlist pads to. Guards a
    // typo (e.g. 9999) from generating thousands of placeholder rows.
    public const int MaxPosition = 300;

    // Device-playlist entry point: pinned and unpinned already arrive as two
    // separate queries, so accept them pre-split. Holes become placeholders.
    public static List<Bouquet> MergeByFixedPosition(
        IReadOnlyList<Bouquet> unpinned,
        IReadOnlyList<Bouquet> pinned)
    {
        var all = new List<Bouquet>(unpinned.Count + pinned.Count);
        all.AddRange(unpinned);
        all.AddRange(pinned);
        return ToSlots(all, c => c.Id, c => c.CanalId)
            .Select(s => s ?? Placeholder())
            .ToList();
    }

    // The synthetic entry emitted into a hole. Tipo=1 so the device counts it
    // as a live row (that is the whole point); the category must never match
    // the app's VOD keyword filter or the slot would collapse again.
    public static Bouquet Placeholder() => new()
    {
        Id = 0,
        Nombre = "———",
        Categoria = "—",
        Tipo = 1,
        EsPlaceholder = true,
    };

    // Compact form: same relative order, gaps removed. Console channel list
    // and the legacy JSON API.
    public static List<T> ByFixedPosition<T>(
        IReadOnlyList<T> channels,
        Func<T, int> idOf,
        Func<T, int> canalIdOf) where T : class
        => ToSlots(channels, idOf, canalIdOf).Where(s => s is not null).Select(s => s!).ToList();

    // Core: exact-slot assignment. null entries are unfillable holes.
    private static List<T?> ToSlots<T>(
        IReadOnlyList<T> channels,
        Func<T, int> idOf,
        Func<T, int> canalIdOf) where T : class
    {
        var reserved = new Dictionary<int, T>();
        var dupes = new List<T>();    // reachable target already taken — cascade near it
        var overflow = new List<T>(); // target outside [1..MaxPosition] — append at end
        foreach (var c in channels.Where(c => canalIdOf(c) != 0)
                     .OrderBy(canalIdOf).ThenBy(idOf))
        {
            var target = canalIdOf(c);
            if (target < 1 || target > MaxPosition) overflow.Add(c);
            else if (!reserved.ContainsKey(target)) reserved[target] = c;
            else dupes.Add(c);
        }

        var free = new Queue<T>(channels.Where(c => canalIdOf(c) == 0).OrderBy(idOf));
        var floaters = new Queue<T>(dupes); // already in (canalId, id) order
        var maxReserved = reserved.Count > 0 ? reserved.Keys.Max() : 0;

        var result = new List<T?>();
        var pos = 1;
        while (pos <= maxReserved || floaters.Count > 0 || free.Count > 0)
        {
            if (reserved.TryGetValue(pos, out var exact))
                result.Add(exact);
            else if (floaters.Count > 0 && canalIdOf(floaters.Peek()) <= pos)
                result.Add(floaters.Dequeue());
            else if (free.Count > 0)
                result.Add(free.Dequeue());
            else
                result.Add(null); // hole: a reserved slot or floater target is still ahead
            pos++;
        }

        result.AddRange(overflow);
        return result;
    }
}
