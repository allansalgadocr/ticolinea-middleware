# Provider Sync Toggle Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use `- [ ]`.

**Goal:** A per-provider "sync enabled" checkbox in the panel admin; when off, the catalog endpoint returns an empty catalog so the node's existing undersized-guard no-ops — main (unchecked) never runs package sync, with zero middleware changes.

**Architecture:** Add `sync_enabled` (bool, default true) to the `providers` table + Provider entity/DTOs; `CatalogService.GetCatalog` returns an empty list when the resolved provider has `SyncEnabled=false`; the frontend `ProviderForm` gets a checkbox. AutoMapper convention handles the DTO↔entity flow.

**Tech Stack:** panel net8 (EF Core + AutoMapper + xUnit), React 19 frontend.

## Global Constraints

- **DO NOT `git commit`/`git add`/`git push`.** User commits. No commit steps.
- **Additive; default `SyncEnabled = true`** (existing providers keep syncing; only main gets unchecked). No middleware change.
- Panel repo: `/Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel`. Frontend: `.../ticolinea.panel.Frontend`.
- AutoMapper is convention-based for Provider maps (`ApplicationMappingProfile.cs:129-140`) — NO profile change needed as long as `SyncEnabled` is named identically everywhere.
- Spec: `docs/superpowers/specs/2026-07-15-admin-channel-control-design.md` is unrelated; this toggle was agreed in conversation (per-provider sync control; main unchecked).

---

### Task 1: Backend — `SyncEnabled` field + catalog guard

**Files:**
- Modify: `ticolinea.panel.Domain/Entities/Provider.cs`
- Modify: `ticolinea.panel.Infrastructure/Data/Configurations/ProviderConfiguration.cs`
- Modify: `ticolinea.panel.Application/DTOs/Providers/ProviderDTO.cs`, `CreateProviderDto.cs`, `UpdateProviderDto.cs`
- Modify: `ticolinea.panel.Application/Services/ProviderService.cs` (audit snapshot only)
- Modify: `ticolinea.panel.Application/Services/CatalogService.cs`
- Create: migration `<ts>_AddSyncEnabledToProviders.cs` (via EF tooling)
- Test: `ticolinea.panel.Tests/Services/CatalogServiceTests.cs` (add one)

- [ ] **Step 1: Add the entity property**

In `Provider.cs`, after `IsExternal`:
```csharp
    // When false, package sync is disabled for this provider — the catalog
    // endpoint returns nothing so the node's sync no-ops (e.g. the main node).
    public bool SyncEnabled { get; set; } = true;
```

- [ ] **Step 2: Map the column**

In `ProviderConfiguration.cs`, after the `IsExternal` property block:
```csharp
        builder.Property(e => e.SyncEnabled)
            .HasColumnType("tinyint(1)")
            .HasDefaultValue(true)
            .HasColumnName("sync_enabled");
```

- [ ] **Step 3: Add to the three DTOs**

`ProviderDTO.cs`: `public bool SyncEnabled { get; set; }`
`CreateProviderDto.cs`: `public bool SyncEnabled { get; set; } = true;`
`UpdateProviderDto.cs`: `public bool SyncEnabled { get; set; } = true;`
(No AutoMapper profile change — convention handles it.)

- [ ] **Step 4: Fix the audit snapshot**

In `ProviderService.UpdateProvider`, the manual `oldProvider` snapshot (~line 57-66) copies each scalar. Add:
```csharp
                SyncEnabled = provider.SyncEnabled,
```
alongside the other copied fields (else every update shows a false diff for this field).

- [ ] **Step 5: Write the failing CatalogService test**

Add to `ticolinea.panel.Tests/Services/CatalogServiceTests.cs` (reuse its `NewDb()`/`LiveStream()`/`Stream` alias):
```csharp
    [Fact]
    public async Task Sync_disabled_provider_returns_empty_catalog()
    {
        using var db = NewDb();
        db.Providers.Add(new Provider { Id = 1, ProviderName = "main", ConnectionUrl = "http://x", IsExternal = true, SyncEnabled = false });
        db.Streams.AddRange(LiveStream(10, "A"), LiveStream(11, "B"));
        await db.SaveChangesAsync();
        var svc = new CatalogService(db);
        var result = await svc.GetCatalog("main");
        result.Should().NotBeNull();     // not 404 — provider exists
        result!.Should().BeEmpty();      // but nothing to sync
    }
```

- [ ] **Step 6: Run to verify failure**

Run: `cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel && dotnet test ticolinea.panel.Tests --filter "CatalogServiceTests.Sync_disabled_provider_returns_empty_catalog"`
Expected: FAIL (returns the 2 streams, not empty) — after adding `SyncEnabled` to the entity so it compiles.

- [ ] **Step 7: Add the catalog guard**

In `CatalogService.GetCatalog`, immediately after `if (provider == null) return null;`:
```csharp
        if (!provider.SyncEnabled) return new List<CatalogStreamDTO>();
```

- [ ] **Step 8: Run to verify pass**

Run: `dotnet test ticolinea.panel.Tests --filter CatalogServiceTests`
Expected: all CatalogServiceTests pass (existing + the new one).

- [ ] **Step 9: Generate the migration + build**

Run (from panel repo):
```bash
dotnet ef migrations add AddSyncEnabledToProviders --project ticolinea.panel.Infrastructure --startup-project ticolinea.panel.API
```
Confirm the `Up` is `AddColumn<bool>(name: "sync_enabled", table: "providers", type: "tinyint(1)", nullable: false, defaultValue: true)` and `Down` drops it. Then:
```bash
dotnet build ticolinea.panel.API/ticolinea.panel.API.csproj -v q --nologo
dotnet test ticolinea.panel.Tests
```
Expected: 0 errors; full suite green (was 61 → 62).

---

### Task 2: Frontend — the checkbox

**Files:**
- Modify: `ticolinea.panel.Frontend/src/types/index.ts` (Provider interface)
- Modify: `ticolinea.panel.Frontend/src/components/ProviderForm.tsx`

- [ ] **Step 1: Type**

In `src/types/index.ts`, in the `Provider` interface, add: `syncEnabled: boolean;`

- [ ] **Step 2: Form state (both blocks)**

In `ProviderForm.tsx`, add to BOTH the `useState` init (~line 21-26) and the reset-on-edit effect (~line 30-39):
```tsx
  syncEnabled: provider?.syncEnabled ?? true,
```
(Default true for a new provider.)

- [ ] **Step 3: Checkbox control**

Add, after the "Proveedor externo" checkbox block (mirror its markup):
```tsx
<div>
  <label className="flex items-center space-x-2">
    <input
      type="checkbox"
      checked={formData.syncEnabled}
      onChange={(e) => setFormData({ ...formData, syncEnabled: e.target.checked })}
      className="w-4 h-4 text-primary border-gray-300 rounded focus:ring-primary"
    />
    <span className="text-sm font-medium text-text">Sincronización de catálogo habilitada</span>
  </label>
  <p className="text-xs text-text-light mt-1">
    Si se desactiva, este nodo no sincroniza su catálogo desde el panel (p.ej. el nodo principal).
  </p>
</div>
```
The submit payload spreads `formData` (~line 47), so `syncEnabled` is included automatically — no payload wiring needed.

- [ ] **Step 4: Build**

Run:
```bash
cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel/ticolinea.panel.Frontend && npm run build
```
Expected: build succeeds. Fix any type error against the real `Provider` type / form state.

---

### Task 3: Completion gate

- [ ] Build panel API (0 err), build frontend (ok), `dotnet test ticolinea.panel.Tests` (62 green). The middleware is untouched — no rebuild needed, but confirm `bats deploy/tests/` + shellcheck still clean and the middleware still builds.

---

## Self-Review

- Coverage: `sync_enabled` column (T1 S1-2,9), DTOs+audit (T1 S3-4), catalog no-op when disabled (T1 S5-8, with test), UI checkbox (T2), gate (T3). ✓
- Additive: new column (default true → existing rows unaffected), new DTO field, one CatalogService line, UI checkbox. No middleware change. AutoMapper convention → no profile edit. ✓
- The disabled→empty-catalog path: node's `PackageSyncPlan` treats empty as undersized-guard-trips → zero mutations (never disables running channels). So toggling sync off freezes the catalog, never blacks out the provider. ✓
- Main: uncheck it → `/providers/main/catalog` → empty → node no-op; if no "main" provider row exists, node already 404-no-ops. ✓
