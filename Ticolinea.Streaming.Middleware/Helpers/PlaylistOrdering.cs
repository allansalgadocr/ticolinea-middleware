using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

// Builds the final channel order from a fixed-position rule: channels the
// operator pinned to a slot (canal_id != 0) versus everything else (canal_id == 0).
// The contract, in one line: a pin lands at its 1-based position when it can,
// unpinned channels fill the gaps in id order, and pins that can't fit exactly
// (a taken slot, a slot past the end) cascade to the next free slot.
//
// One algorithm, two callers: the device playlist (StreamsController) and the
// console channel list, so both show the operator the exact same order.
public static class PlaylistOrdering
{
    // Device-playlist entry point: pinned and unpinned already arrive as two
    // separate queries, so accept them pre-split.
    public static List<Bouquet> MergeByFixedPosition(
        IReadOnlyList<Bouquet> unpinned,
        IReadOnlyList<Bouquet> pinned)
    {
        var all = new List<Bouquet>(unpinned.Count + pinned.Count);
        all.AddRange(unpinned);
        all.AddRange(pinned);
        return ByFixedPosition(all, c => c.Id, c => c.CanalId);
    }

    // Single-list core. Splits on canalIdOf == 0 itself, so callers can hand it a
    // flat list. Stable and deterministic: same input always yields same order.
    public static List<T> ByFixedPosition<T>(
        IReadOnlyList<T> channels,
        Func<T, int> idOf,
        Func<T, int> canalIdOf)
    {
        // Unpinned fill the gaps in id order; pins are consumed lowest-slot-first
        // (id breaking ties) so that when two want the same slot the lower id
        // takes it and the rest cascade downward.
        var free = new Queue<T>(channels.Where(c => canalIdOf(c) == 0).OrderBy(idOf));
        var pins = new Queue<T>(channels.Where(c => canalIdOf(c) != 0)
            .OrderBy(canalIdOf).ThenBy(idOf));

        var result = new List<T>(channels.Count);
        var position = 1; // 1-based, matches canal_id

        while (free.Count > 0 || pins.Count > 0)
        {
            // A pin claims this slot when its position has arrived (==) or is
            // already behind us because an earlier slot was taken (<, the cascade).
            if (pins.Count > 0 && canalIdOf(pins.Peek()) <= position)
                result.Add(pins.Dequeue());
            else if (free.Count > 0)
                result.Add(free.Dequeue());
            else
                // Only pins left, all aimed past the end — append in slot order.
                result.Add(pins.Dequeue());

            position++;
        }

        return result;
    }
}
