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
    public void A_position_past_the_end_is_honored_with_placeholder_padding()
    {
        // Contract change (2026-08): a reachable pin is ALWAYS honored exactly.
        // With no filler channels the gap is padded with placeholder entries so
        // the device still shows the channel at its number.
        var unpinned = Unpinned(1, 2);
        var pinned = new List<Bouquet> { Ch(200, canalId: 99) };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        result.Should().HaveCount(99);
        result[0].Id.Should().Be(1);
        result[1].Id.Should().Be(2);
        result[98].Id.Should().Be(200);
        result.Skip(2).Take(96).Should().OnlyContain(c => c.EsPlaceholder);
    }

    [Fact]
    public void Dense_all_pinned_lineup_keeps_its_exact_order_with_no_placeholders()
    {
        // The post-renumber LogicSphere state in miniature: every channel pinned
        // 1..N with no gaps must reproduce exactly, with nothing synthesized.
        var pinned = new List<Bouquet>
        {
            Ch(150, canalId: 1), Ch(151, canalId: 2), Ch(152, canalId: 3), Ch(153, canalId: 4),
        };

        var result = PlaylistOrdering.MergeByFixedPosition(new List<Bouquet>(), pinned);

        result.Select(c => c.Id).Should().Equal(150, 151, 152, 153);
        result.Should().OnlyContain(c => !c.EsPlaceholder);
    }

    [Fact]
    public void Sparse_all_pinned_lineup_lands_every_pin_exactly_with_placeholder_gaps()
    {
        // Cable-style block numbering: pins at 1,2 and 5,6 with nothing to fill
        // 3,4. Every pin sits on its number; the holes become placeholders.
        var pinned = new List<Bouquet>
        {
            Ch(150, canalId: 1), Ch(151, canalId: 2), Ch(152, canalId: 5), Ch(153, canalId: 6),
        };

        var result = PlaylistOrdering.MergeByFixedPosition(new List<Bouquet>(), pinned);

        result.Select(c => c.Id).Should().Equal(150, 151, 0, 0, 152, 153);
        result[2].EsPlaceholder.Should().BeTrue();
        result[3].EsPlaceholder.Should().BeTrue();
    }

    [Fact]
    public void Unpinned_channels_fill_holes_before_any_placeholder_is_used()
    {
        // Hybrid mode: ESPN pinned at 5, three free channels. The free channels
        // pack below the pin; only the slot they cannot reach gets a placeholder.
        var unpinned = Unpinned(101, 102, 103);
        var pinned = new List<Bouquet> { Ch(150, canalId: 5) };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        result.Select(c => c.Id).Should().Equal(101, 102, 103, 0, 150);
        result[3].EsPlaceholder.Should().BeTrue();
    }

    [Fact]
    public void A_pin_beyond_MaxPosition_is_appended_without_padding()
    {
        // Typo guard: POSICIÓN 9999 must not generate thousands of placeholder
        // rows. Beyond the cap the pin loses exactness and flows to the end.
        var unpinned = Unpinned(1, 2);
        var pinned = new List<Bouquet> { Ch(200, canalId: PlaylistOrdering.MaxPosition + 1) };

        var result = PlaylistOrdering.MergeByFixedPosition(unpinned, pinned);

        result.Select(c => c.Id).Should().Equal(1, 2, 200);
        result.Should().OnlyContain(c => !c.EsPlaceholder);
    }

    [Fact]
    public void Duplicate_losers_take_the_next_hole_after_their_target_before_placeholders()
    {
        // Two channels claim 3, another pins 5, no filler: winner exact at 3,
        // loser lands at 4 (the first hole at/after its target), 5 exact.
        var pinned = new List<Bouquet>
        {
            Ch(152, canalId: 3), Ch(153, canalId: 3), Ch(154, canalId: 5),
        };

        var result = PlaylistOrdering.MergeByFixedPosition(new List<Bouquet>(), pinned);

        result.Select(c => c.Id).Should().Equal(0, 0, 152, 153, 154);
        result[0].EsPlaceholder.Should().BeTrue();
        result[1].EsPlaceholder.Should().BeTrue();
    }

    [Fact]
    public void Placeholder_entries_are_marked_inert_live_rows()
    {
        var ph = PlaylistOrdering.Placeholder();

        ph.EsPlaceholder.Should().BeTrue();
        ph.Id.Should().Be(0);
        ph.Tipo.Should().Be(1); // must count as a live row so the device numbers past it
        ph.Categoria.Should().NotContainAny("serie", "Serie", "SERIE"); // must not be app-hidden
    }

    [Fact]
    public void Compact_ordering_is_the_placeholder_ordering_with_the_gaps_removed()
    {
        // Console list and JSON API use the compact form: same relative order,
        // no placeholder rows.
        var pinned = new List<Bouquet>
        {
            Ch(150, canalId: 1), Ch(151, canalId: 2), Ch(152, canalId: 5), Ch(153, canalId: 6),
        };

        var compact = PlaylistOrdering.ByFixedPosition(pinned, c => c.Id, c => c.CanalId);

        compact.Select(c => c.Id).Should().Equal(150, 151, 152, 153);
    }

    [Fact]
    public void Unpinned_channels_are_sorted_by_id_regardless_of_input_order()
    {
        var result = PlaylistOrdering.MergeByFixedPosition(Unpinned(108, 102, 105), new List<Bouquet>());

        result.Select(c => c.Id).Should().Equal(102, 105, 108);
    }
}
