using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The device playlist places operator-pinned channels (canal_id != 0) at their
// exact 1-based slot, and lets every other channel flow around them in id order.
// This is the pure core of that ordering, isolated so the placement rules —
// exact slot, collision cascade, overflow append — can be pinned down here
// instead of against a live playlist.
public class PlaylistOrderingTests
{
    private static Bouquet Ch(int id, int canalId = 0) =>
        new() { Id = id, Nombre = $"Canal {id}", CanalId = canalId };

    private static List<Bouquet> Unpinned(params int[] ids) =>
        ids.Select(id => Ch(id)).ToList();

    [Fact]
    public void With_no_pins_channels_keep_pure_id_order()
    {
        var result = PlaylistOrdering.MergeByFixedPosition(Unpinned(103, 101, 102), new List<Bouquet>());

        result.Select(c => c.Id).Should().Equal(101, 102, 103);
    }

    [Fact]
    public void A_pinned_channel_lands_at_its_absolute_position()
    {
        var unpinned = Unpinned(101, 102, 103, 104, 105, 106, 107, 108);
        var pinned = new List<Bouquet> { Ch(150, canalId: 7) };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        // 1-based position 7 == index 6
        result[6].Id.Should().Be(150);
    }

    [Fact]
    public void Several_pins_each_hold_their_slot_while_the_rest_flow_by_id()
    {
        var unpinned = Unpinned(101, 102, 103, 104, 105, 106, 107, 108);
        var pinned = new List<Bouquet>
        {
            Ch(150, canalId: 7), // Teletica
            Ch(151, canalId: 1), // Repretel
            Ch(152, canalId: 3), // Telenoticias
        };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        result.Select(c => c.Id).Should()
            .Equal(151, 101, 152, 102, 103, 104, 150, 105, 106, 107, 108);
    }

    [Fact]
    public void When_two_channels_claim_the_same_slot_the_lower_id_keeps_it()
    {
        var unpinned = Unpinned(1, 2, 4);
        var pinned = new List<Bouquet>
        {
            Ch(153, canalId: 3),
            Ch(152, canalId: 3),
        };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        // Slot 3 goes to the lower id (152); the other cascades to slot 4.
        result.Select(c => c.Id).Should().Equal(1, 2, 152, 153, 4);
    }

    [Fact]
    public void A_position_past_the_end_is_appended_rather_than_dropped()
    {
        var unpinned = Unpinned(1, 2);
        var pinned = new List<Bouquet> { Ch(200, canalId: 99) };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        result.Select(c => c.Id).Should().Equal(1, 2, 200);
    }

    [Fact]
    public void Unpinned_channels_are_sorted_by_id_regardless_of_input_order()
    {
        var result = PlaylistOrdering.MergeByFixedPosition(Unpinned(108, 102, 105), new List<Bouquet>());

        result.Select(c => c.Id).Should().Equal(102, 105, 108);
    }
}
