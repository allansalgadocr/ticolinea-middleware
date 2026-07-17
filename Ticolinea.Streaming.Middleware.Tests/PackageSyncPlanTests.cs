using System.Linq;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class PackageSyncPlanTests
{
    private static CatalogStream C(int id) => new() { Id = id, NombreStream = $"c{id}" };

    [Fact]
    public void Upserts_all_catalog_and_disables_dropped()
    {
        var catalog = new[] { C(10), C(11), C(13) };
        var current = new[] { 10, 11, 12 };
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: current.Length);
        d.Upserts.Select(x => x.Id).Should().BeEquivalentTo(new[] { 10, 11, 13 });
        d.IdsToDisable.Should().BeEquivalentTo(new[] { 12 });
        d.SkipDisable.Should().BeFalse();
    }

    [Fact]
    public void First_sync_no_current_disables_nothing()
    {
        var d = PackageSyncPlan.Build(new[] { C(10), C(11) }, System.Array.Empty<int>(), enabledSyncedCount: 0);
        d.IdsToDisable.Should().BeEmpty();
        d.SkipDisable.Should().BeFalse();
    }

    [Fact]
    public void Undersized_catalog_skips_disable_but_still_upserts()
    {
        // 10 enabled now, catalog only 2 (< 50%) → suspect: upsert, do not disable
        var catalog = new[] { C(10), C(11) };
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10);
        d.Upserts.Should().HaveCount(2);
        d.SkipDisable.Should().BeTrue();
        d.IdsToDisable.Should().BeEmpty();
    }

    [Fact]
    public void Exactly_half_is_allowed_to_disable()
    {
        // 5 catalog vs 10 enabled = exactly 50% → NOT undersized (strict <)
        var catalog = Enumerable.Range(1, 5).Select(C).ToArray();
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().BeEquivalentTo(new[] { 6, 7, 8, 9, 10 });
    }

    [Fact]
    public void Empty_catalog_with_managed_channels_skips_disable_and_upserts_nothing()
    {
        // The headline safety case: panel returns [] but 10 channels are enabled/managed.
        // Guard must trip (0 < 5) -> SkipDisable, disable nothing, upsert nothing. Channels stay enabled.
        var catalog = System.Array.Empty<CatalogStream>();
        var current = System.Linq.Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10);
        d.SkipDisable.Should().BeTrue();
        d.IdsToDisable.Should().BeEmpty();
        d.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_kept_rows_do_not_inflate_the_undersized_guard()
    {
        // 300 ever-synced (sincronizado=1) but only 40 enabled now; catalog 45.
        // OLD behavior (denominator=300): 45 < 150 -> would wrongly skip disables forever.
        // NEW (denominator=enabled 40): 45 < 20 is false -> NOT undersized -> disables proceed.
        var catalog = System.Linq.Enumerable.Range(1, 45).Select(C).ToArray();
        var current = System.Linq.Enumerable.Range(1, 300).ToArray(); // full synced set
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 40);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().NotBeEmpty(); // ids 46..300 dropped
    }
}
