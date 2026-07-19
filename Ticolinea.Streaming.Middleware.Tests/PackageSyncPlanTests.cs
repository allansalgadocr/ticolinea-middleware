using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class PackageSyncPlanTests
{
    private static CatalogStream C(int id, string? fuente = null) =>
        new() { Id = id, NombreStream = $"c{id}", FuenteStream = fuente };

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
        var catalog = Enumerable.Range(1, 5).Select(i => C(i)).ToArray();
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

    // ---- Forced sync: enforceGuard=false makes the catalog fully authoritative ----

    [Fact]
    public void Forced_guard_off_applies_deliberate_shrink()
    {
        // 2 catalog vs 10 enabled (< 50%): scheduled path would trip the guard,
        // but forced (enforceGuard=false) must apply the shrink — disables COMPUTED.
        var catalog = new[] { C(1), C(2) };
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10, enforceGuard: false);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().BeEquivalentTo(Enumerable.Range(3, 8));
        d.Upserts.Should().HaveCount(2);
    }

    [Fact]
    public void Forced_guard_off_empty_catalog_disables_everything()
    {
        var d = PackageSyncPlan.Build(System.Array.Empty<CatalogStream>(),
            Enumerable.Range(1, 10).ToArray(), enabledSyncedCount: 10, enforceGuard: false);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().BeEquivalentTo(Enumerable.Range(1, 10));
        d.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void Guard_enforced_still_noops_the_same_shrink()
    {
        // Identical input to the forced test, but enforceGuard=true (scheduled):
        // guard trips, no disables. Explicitly pins the flag's semantics.
        var catalog = new[] { C(1), C(2) };
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10, enforceGuard: true);
        d.SkipDisable.Should().BeTrue();
        d.IdsToDisable.Should().BeEmpty();
    }

    // ---- Source-change detection (currentFuentes) ----

    [Fact]
    public void Source_changes_list_changed_fuente_only()
    {
        var catalog = new[]
        {
            C(1, "http://origin/a.m3u8"),      // unchanged
            C(2, "http://origin/b-NEW.m3u8"),  // changed
            C(3, "http://origin/c.m3u8"),      // NEW channel (not synced yet)
        };
        var fuentes = new Dictionary<int, string?>
        {
            [1] = "http://origin/a.m3u8",
            [2] = "http://origin/b-old.m3u8",
        };
        var d = PackageSyncPlan.Build(catalog, fuentes.Keys.ToList(), enabledSyncedCount: 2,
            currentFuentes: fuentes);
        d.SourceChangedIds.Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void Source_change_null_and_empty_fuente_compare_equal()
    {
        // NULL in DB vs "" in catalog (or vice versa) must NOT read as a change —
        // a spurious mismatch here would mass-restart healthy channels.
        var catalog = new[] { C(1, null), C(2, "http://x") };
        var fuentes = new Dictionary<int, string?> { [1] = "", [2] = null };
        var d = PackageSyncPlan.Build(catalog, fuentes.Keys.ToList(), enabledSyncedCount: 2,
            currentFuentes: fuentes);
        d.SourceChangedIds.Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void Without_currentFuentes_no_source_changes_reported()
    {
        var d = PackageSyncPlan.Build(new[] { C(1, "http://x") }, new[] { 1 }, enabledSyncedCount: 1);
        d.SourceChangedIds.Should().BeEmpty();
    }

    [Fact]
    public void Source_changes_still_detected_when_guard_trips()
    {
        // Guard tripping only skips disables; upserts (and thus fuente rewrites)
        // still apply, so changed sources must still be reported for the bounce.
        var catalog = new[] { C(1, "http://new") };
        var fuentes = Enumerable.Range(1, 10).ToDictionary(i => i, _ => (string?)"http://old");
        var d = PackageSyncPlan.Build(catalog, fuentes.Keys.ToList(), enabledSyncedCount: 10,
            enforceGuard: true, currentFuentes: fuentes);
        d.SkipDisable.Should().BeTrue();
        d.SourceChangedIds.Should().BeEquivalentTo(new[] { 1 });
    }

    // ---- Result summary (pure counts from a plan) ----

    [Fact]
    public void Result_counts_come_pure_from_the_decision()
    {
        // catalog: 1 updated+changed, 2 updated, 5 added; current 1,2,3 -> 3 disabled.
        var catalog = new[] { C(1, "http://new"), C(2, "http://b"), C(5, "http://e") };
        var fuentes = new Dictionary<int, string?>
        {
            [1] = "http://old",
            [2] = "http://b",
            [3] = "http://c",
        };
        var d = PackageSyncPlan.Build(catalog, fuentes.Keys.ToList(), enabledSyncedCount: 3,
            enforceGuard: false, currentFuentes: fuentes);

        var r = PackageSyncResult.FromDecision(d);
        r.Added.Should().Be(1);          // id 5
        r.Updated.Should().Be(2);        // ids 1, 2
        r.Disabled.Should().Be(1);       // id 3
        r.SourcesChanged.Should().Be(1); // id 1
        r.GuardTripped.Should().BeFalse();
        r.Restarted.Should().Be(0);      // filled by the service, not the plan

        r.Restarted = 1;
        r.Summary().Should().Be("1 added, 2 updated, 1 disabled, 1 sources changed, 1 restarted");
    }

    [Fact]
    public void Result_reports_guard_tripped_in_summary()
    {
        var catalog = new[] { C(1) };
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 10, enforceGuard: true);

        var r = PackageSyncResult.FromDecision(d);
        r.GuardTripped.Should().BeTrue();
        r.Disabled.Should().Be(0);
        r.Summary().Should().EndWith("guard tripped, disables skipped");
    }

    [Fact]
    public void Disabled_kept_rows_do_not_inflate_the_undersized_guard()
    {
        // 300 ever-synced (sincronizado=1) but only 40 enabled now; catalog 45.
        // OLD behavior (denominator=300): 45 < 150 -> would wrongly skip disables forever.
        // NEW (denominator=enabled 40): 45 < 20 is false -> NOT undersized -> disables proceed.
        var catalog = System.Linq.Enumerable.Range(1, 45).Select(i => C(i)).ToArray();
        var current = System.Linq.Enumerable.Range(1, 300).ToArray(); // full synced set
        var d = PackageSyncPlan.Build(catalog, current, enabledSyncedCount: 40);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().NotBeEmpty(); // ids 46..300 dropped
    }
}
