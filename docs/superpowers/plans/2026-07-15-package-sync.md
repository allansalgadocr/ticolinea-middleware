# Package Sync (Spec B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A provider streaming node pulls its assigned channel package from the panel every 6 hours (and on boot) and populates its local `streams_tl` / `streams_info` so its FFmpeg supervision runs the channels.

**Architecture:** Panel exposes `GET /api/v2/providers/{slug}/catalog` (reuses provider→package resolution, gated by the shared `X-Auth-API-Key`, returns full live stream rows). A new Hangfire job in the middleware fetches that catalog, upserts `streams_tl` keyed on the panel id, ensures a `streams_info` row exists, and disables channels no longer in the package. Two EF migrations add the `sincronizado` marker and create the `streams_info` table on fresh nodes.

**Tech Stack:** .NET (panel: ASP.NET + EF Core + xUnit/Moq/FluentAssertions; middleware: raw MySqlConnector + Hangfire, new xUnit test project).

## Global Constraints

- **DO NOT `git commit` or `git push`.** The user commits manually. End every task at "tests pass" — no commit step. (User standing preference.)
- **Panel endpoint:** `GET /api/v2/providers/{slug}/catalog`, `[CustomAuthorization(Global.Secret)]`, on the existing `ProvidersController` (`[Route("api/v2/providers")]`). `{slug}` matches `Provider.ProviderName` normalized / the node's `PROVIDER` value.
- **Resolution:** `Provider.IsExternal == true` → all enabled live streams (no package filter). Else → streams in `Provider.DefaultPaqueteTvId` via `paquete_tv_streams`. Filter always: `habilitado = 1 AND es_bajodemanda = 0 AND tipo = 1` (live only, v1). Unknown slug → 404.
- **`iniciado` is node-owned:** the sync ensures a `streams_info` row EXISTS (default `iniciado = 1`), and NEVER writes `iniciado` after creation. Package membership is expressed ONLY through `streams_tl.habilitado` (1 = in package, 0 = dropped).
- **`streams_info` has no unique on `stream_id`** (PK is `stream_info_id` auto-increment). "Ensure row exists" = conditional insert (`INSERT ... SELECT ... WHERE NOT EXISTS`), not `INSERT IGNORE`.
- **`streams_tl.id`** is the app-assigned PK — upsert by it with `INSERT ... ON DUPLICATE KEY UPDATE`.
- **Failure handling:** panel unreachable / non-2xx → zero DB mutations, keep serving. Undersized-catalog guard: if catalog count `< 0.5 ×` current `sincronizado=1` count → apply upserts but SKIP the disable step. Whole sync in one transaction. A run-lock prevents overlapping cycles.
- **Middleware DB access** uses raw `MySqlConnector` with `Constantes.Global.MARIADB_CONN` (see `Helpers/BatchDatabaseHelper.cs` for the transaction idiom). NOT EF.
- **Repos:** panel = `/Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel`; middleware = `/Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel/Ticolinea.Streaming.Middleware`.
- Spec: `docs/superpowers/specs/2026-07-15-package-sync-design.md`.

---

## File Structure

**Panel (`ticolinea.panel`):**
```
ticolinea.panel.Application/DTOs/CatalogStreamDTO.cs        # new — full stream row for the node
ticolinea.panel.Application/Services/ICatalogService.cs     # new — GetCatalog(slug)
ticolinea.panel.Application/Services/CatalogService.cs      # new — resolve + query + project
ticolinea.panel.API/Controllers/ProvidersController.cs      # modify — add GET {slug}/catalog
ticolinea.panel.API/Program.cs                              # modify — DI register ICatalogService
ticolinea.panel.Domain/Entities/Stream.cs                   # modify — add Sincronizado
ticolinea.panel.Infrastructure/Data/Configurations/StreamConfiguration.cs   # modify — map sincronizado
ticolinea.panel.Infrastructure/Migrations/<ts>_AddSincronizadoToStreams.cs  # new
ticolinea.panel.Infrastructure/Migrations/<ts>_CreateStreamsInfoIfNotExists.cs # new (raw SQL)
ticolinea.panel.Tests/Services/CatalogServiceTests.cs       # new
ticolinea.panel.Tests/Controllers/ProvidersControllerCatalogTests.cs  # new
```

**Middleware (`Ticolinea.Streaming.Middleware`):**
```
Modelos/CatalogStream.cs                        # new — DTO deserialized from the panel
Helpers/CatalogClient.cs                        # new — HTTP fetch of the catalog
Helpers/PackageSyncPlan.cs                      # new — PURE decision logic (upsert set, disable set, guard)
Services/PackageSyncService.cs                  # new — orchestrates fetch → plan → DB writes (transaction, run-lock)
Jobs.cs                                         # modify — add SyncPackageCatalog()
Program.cs                                      # modify — RecurringJob 6h + BackgroundJob.Enqueue on boot
../Ticolinea.Streaming.Middleware.Tests/        # new xUnit project (scaffold + add to solution)
  Ticolinea.Streaming.Middleware.Tests.csproj
  PackageSyncPlanTests.cs
  CatalogClientTests.cs
```

---

## PART A — Panel (do first; the node needs something to call)

### Task 1: `CatalogStreamDTO`

**Files:**
- Create: `ticolinea.panel.Application/DTOs/CatalogStreamDTO.cs`

**Interfaces:**
- Produces: `CatalogStreamDTO` with every field the node needs to run FFmpeg and build a channel list.

- [ ] **Step 1: Create the DTO**

`ticolinea.panel.Application/DTOs/CatalogStreamDTO.cs`:
```csharp
namespace ticolinea.panel.Application.DTOs;

// Full live-stream row shipped to a provider node's package sync. Field names
// mirror the streams_tl columns the node upserts.
public class CatalogStreamDTO
{
    public int Id { get; set; }
    public string NombreStream { get; set; } = null!;
    public string? FuenteStream { get; set; }
    public string? ImagenStream { get; set; }
    public int? IdCategoria { get; set; }
    public int Orden { get; set; }
    public int Agregado { get; set; }
    public int ProbesizeOndemand { get; set; }
    public sbyte EsBajodemanda { get; set; }
    public sbyte Tipo { get; set; }
    public string? Contenedor { get; set; }
    public sbyte Habilitado { get; set; }
    public string TranscodeAudio { get; set; } = "";
    public sbyte? Intervalo { get; set; }
    public sbyte? Segmentos { get; set; }
    public sbyte? Framerate { get; set; }
    public sbyte Transcode { get; set; }
    public string Resolucion { get; set; } = "";
    public string Bitrate { get; set; } = "";
    public string CanalEpg { get; set; } = "";
    public sbyte Cgop { get; set; }
    public int Gop { get; set; }
    public int CanalId { get; set; }
}
```

- [ ] **Step 2: Build the project**

Run: `dotnet build ticolinea.panel.Application/ticolinea.panel.Application.csproj`
Expected: Build succeeded.

---

### Task 2: `CatalogService` — resolve + query + project

**Files:**
- Create: `ticolinea.panel.Application/Services/ICatalogService.cs`
- Create: `ticolinea.panel.Application/Services/CatalogService.cs`
- Test: `ticolinea.panel.Tests/Services/CatalogServiceTests.cs`

**Interfaces:**
- Consumes: `TicolineaDbContext` (DbSets `Streams`, `PaqueteTvStreams`, `Providers`), `CatalogStreamDTO`.
- Produces: `ICatalogService.GetCatalog(string slug)` → `Task<List<CatalogStreamDTO>?>` (null = provider not found).

Note the resolution: match the provider by normalized name (lowercase, trim). External → all live streams; else → streams whose id is in `paquete_tv_streams` for the provider's `DefaultPaqueteTvId`. Always filter `Habilitado==1 && EsBajodemanda==0 && Tipo==1`.

- [ ] **Step 1: Write the failing tests**

`ticolinea.panel.Tests/Services/CatalogServiceTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ticolinea.panel.Application.Services;
using ticolinea.panel.Domain.Entities;
using ticolinea.panel.Infrastructure.Data;
using Xunit;

namespace ticolinea.panel.Tests.Services;

public class CatalogServiceTests
{
    private static TicolineaDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<TicolineaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new TicolineaDbContext(opts);
    }

    private static Stream LiveStream(int id, string name) => new()
    {
        Id = id, NombreStream = name, FuenteStream = $"http://src/{id}",
        Habilitado = 1, EsBajodemanda = 0, Tipo = 1,
        TranscodeAudio = "", Resolucion = "", Bitrate = "", CanalEpg = ""
    };

    [Fact]
    public async Task External_provider_returns_all_enabled_live_streams()
    {
        using var db = NewDb();
        db.Providers.Add(new Provider { Id = 1, ProviderName = "acme", IsExternal = true });
        db.Streams.AddRange(LiveStream(10, "A"), LiveStream(11, "B"));
        db.Streams.Add(new Stream { Id = 12, NombreStream = "vod", Habilitado = 1, EsBajodemanda = 1, Tipo = 1, TranscodeAudio="", Resolucion="", Bitrate="", CanalEpg="" }); // VOD excluded
        await db.SaveChangesAsync();

        var svc = new CatalogService(db);
        var result = await svc.GetCatalog("acme");

        result.Should().NotBeNull();
        result!.Select(s => s.Id).Should().BeEquivalentTo(new[] { 10, 11 });
    }

    [Fact]
    public async Task Non_external_provider_returns_only_its_package_streams()
    {
        using var db = NewDb();
        db.Providers.Add(new Provider { Id = 1, ProviderName = "acme", IsExternal = false, DefaultPaqueteTvId = "P1" });
        db.Streams.AddRange(LiveStream(10, "A"), LiveStream(11, "B"), LiveStream(12, "C"));
        db.PaqueteTvStreams.AddRange(
            new PaqueteTvStream { StreamId = 10, Tipo = 1, IdPaqueteTv = "P1" },
            new PaqueteTvStream { StreamId = 11, Tipo = 1, IdPaqueteTv = "P1" });
        await db.SaveChangesAsync();

        var svc = new CatalogService(db);
        var result = await svc.GetCatalog("acme");

        result!.Select(s => s.Id).Should().BeEquivalentTo(new[] { 10, 11 }); // 12 not in P1
    }

    [Fact]
    public async Task Unknown_slug_returns_null()
    {
        using var db = NewDb();
        var svc = new CatalogService(db);
        (await svc.GetCatalog("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Slug_match_is_case_insensitive_and_trimmed()
    {
        using var db = NewDb();
        db.Providers.Add(new Provider { Id = 1, ProviderName = "Acme", IsExternal = true });
        db.Streams.Add(LiveStream(10, "A"));
        await db.SaveChangesAsync();
        var svc = new CatalogService(db);
        (await svc.GetCatalog("  acme ")).Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test ticolinea.panel.Tests/ticolinea.panel.Tests.csproj --filter CatalogServiceTests`
Expected: FAIL — `CatalogService` / `ICatalogService` do not exist (compile error).

- [ ] **Step 3: Implement the interface + service**

`ticolinea.panel.Application/Services/ICatalogService.cs`:
```csharp
using ticolinea.panel.Application.DTOs;

namespace ticolinea.panel.Application.Services;

public interface ICatalogService
{
    // Returns the provider's live-channel catalog, or null if the provider slug is unknown.
    Task<List<CatalogStreamDTO>?> GetCatalog(string slug);
}
```

`ticolinea.panel.Application/Services/CatalogService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using ticolinea.panel.Application.DTOs;
using ticolinea.panel.Infrastructure.Data;

namespace ticolinea.panel.Application.Services;

public class CatalogService : ICatalogService
{
    private readonly TicolineaDbContext _context;
    public CatalogService(TicolineaDbContext context) => _context = context;

    public async Task<List<CatalogStreamDTO>?> GetCatalog(string slug)
    {
        var key = (slug ?? string.Empty).Trim().ToLowerInvariant();

        var provider = await _context.Providers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderName.Trim().ToLower() == key);
        if (provider == null) return null;

        // Base: enabled live channels only.
        var streams = _context.Streams.AsNoTracking()
            .Where(s => s.Habilitado == 1 && s.EsBajodemanda == 0 && s.Tipo == 1);

        if (!provider.IsExternal)
        {
            var pkg = (provider.DefaultPaqueteTvId ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(pkg))
                return new List<CatalogStreamDTO>(); // non-external, no package → empty catalog
            var ids = _context.PaqueteTvStreams.AsNoTracking()
                .Where(ps => ps.IdPaqueteTv == pkg)
                .Select(ps => ps.StreamId);
            streams = streams.Where(s => ids.Contains(s.Id));
        }

        return await streams
            .OrderBy(s => s.Orden)
            .Select(s => new CatalogStreamDTO
            {
                Id = s.Id, NombreStream = s.NombreStream, FuenteStream = s.FuenteStream,
                ImagenStream = s.ImagenStream, IdCategoria = s.IdCategoria, Orden = s.Orden,
                Agregado = s.Agregado, ProbesizeOndemand = s.ProbesizeOndemand,
                EsBajodemanda = s.EsBajodemanda, Tipo = s.Tipo, Contenedor = s.Contenedor,
                Habilitado = s.Habilitado, TranscodeAudio = s.TranscodeAudio,
                Intervalo = s.Intervalo, Segmentos = s.Segmentos, Framerate = s.Framerate,
                Transcode = s.Transcode, Resolucion = s.Resolucion, Bitrate = s.Bitrate,
                CanalEpg = s.CanalEpg, Cgop = s.Cgop, Gop = s.Gop, CanalId = s.CanalId
            })
            .ToListAsync();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test ticolinea.panel.Tests/ticolinea.panel.Tests.csproj --filter CatalogServiceTests`
Expected: 4 passing.

---

### Task 3: Controller action + DI registration

**Files:**
- Modify: `ticolinea.panel.API/Controllers/ProvidersController.cs`
- Modify: `ticolinea.panel.API/Program.cs` (register `ICatalogService`)
- Test: `ticolinea.panel.Tests/Controllers/ProvidersControllerCatalogTests.cs`

**Interfaces:**
- Consumes: `ICatalogService.GetCatalog(slug)`.
- Produces: `GET /api/v2/providers/{slug}/catalog` → 200 + `List<CatalogStreamDTO>`, 404 if unknown, 401/403 without the key.

- [ ] **Step 1: Write the failing controller test**

`ticolinea.panel.Tests/Controllers/ProvidersControllerCatalogTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using ticolinea.panel.API.Controllers;
using ticolinea.panel.Application.DTOs;
using ticolinea.panel.Application.Services;
using Xunit;

namespace ticolinea.panel.Tests.Controllers;

public class ProvidersControllerCatalogTests
{
    private static ProvidersController Build(Mock<ICatalogService> catalog)
        => new(Mock.Of<IProviderService>(), Mock.Of<ILogger<ProvidersController>>(), catalog.Object);

    [Fact]
    public async Task Catalog_returns_200_with_streams()
    {
        var catalog = new Mock<ICatalogService>();
        catalog.Setup(c => c.GetCatalog("acme"))
            .ReturnsAsync(new List<CatalogStreamDTO> { new() { Id = 10, NombreStream = "A" } });

        var result = await Build(catalog).GetCatalog("acme");

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<List<CatalogStreamDTO>>();
    }

    [Fact]
    public async Task Catalog_returns_404_for_unknown_provider()
    {
        var catalog = new Mock<ICatalogService>();
        catalog.Setup(c => c.GetCatalog("nope")).ReturnsAsync((List<CatalogStreamDTO>?)null);

        var result = await Build(catalog).GetCatalog("nope");

        result.Should().BeOfType<NotFoundResult>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test ticolinea.panel.Tests/ticolinea.panel.Tests.csproj --filter ProvidersControllerCatalogTests`
Expected: FAIL — `ProvidersController` has no 3-arg constructor / no `GetCatalog`.

- [ ] **Step 3: Add the constructor param + action**

In `ticolinea.panel.API/Controllers/ProvidersController.cs`, add `ICatalogService` to the constructor and a new action. The existing constructor injects `IProviderService` + `ILogger`; extend it:
```csharp
private readonly IProviderService _providerService;
private readonly ILogger<ProvidersController> _logger;
private readonly ICatalogService _catalogService;

public ProvidersController(IProviderService providerService, ILogger<ProvidersController> logger, ICatalogService catalogService)
{
    _providerService = providerService;
    _logger = logger;
    _catalogService = catalogService;
}
```
Add the action (mirror the try/catch + `[CustomAuthorization(Global.Secret)]` idiom used by the other actions):
```csharp
[CustomAuthorization(Global.Secret)]
[HttpGet("{slug}/catalog")]
public async Task<IActionResult> GetCatalog(string slug)
{
    try
    {
        var catalog = await _catalogService.GetCatalog(slug);
        if (catalog == null) return NotFound();
        return Ok(catalog);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error building catalog for provider {Slug}", slug);
        return StatusCode(500, ex.Message);
    }
}
```

- [ ] **Step 4: Register the service in DI**

In `ticolinea.panel.API/Program.cs`, next to the other `AddScoped` service registrations (near lines 116-131):
```csharp
builder.Services.AddScoped<ICatalogService, CatalogService>();
```

- [ ] **Step 5: Run tests + build API**

Run: `dotnet test ticolinea.panel.Tests/ticolinea.panel.Tests.csproj --filter ProvidersControllerCatalogTests`
Expected: 2 passing.
Run: `dotnet build ticolinea.panel.API/ticolinea.panel.API.csproj`
Expected: Build succeeded.

---

### Task 4: Migration — add `sincronizado` to `streams_tl`

**Files:**
- Modify: `ticolinea.panel.Domain/Entities/Stream.cs`
- Modify: `ticolinea.panel.Infrastructure/Data/Configurations/StreamConfiguration.cs`
- Create: `ticolinea.panel.Infrastructure/Migrations/<timestamp>_AddSincronizadoToStreams.cs` (via EF tooling)

**Interfaces:**
- Produces: `streams_tl.sincronizado` `TINYINT(1) NOT NULL DEFAULT 0`; `Stream.Sincronizado` (`bool`).

- [ ] **Step 1: Add the property**

In `ticolinea.panel.Domain/Entities/Stream.cs`, add:
```csharp
    public bool Sincronizado { get; set; }
```

- [ ] **Step 2: Map the column**

In `ticolinea.panel.Infrastructure/Data/Configurations/StreamConfiguration.cs`, add inside `Configure`:
```csharp
        builder.Property(e => e.Sincronizado)
            .IsRequired()
            .HasColumnName("sincronizado")
            .HasDefaultValue(false);
```

- [ ] **Step 3: Generate the migration**

Run (from the panel repo root):
```bash
dotnet ef migrations add AddSincronizadoToStreams \
  --project ticolinea.panel.Infrastructure \
  --startup-project ticolinea.panel.API
```
Expected: a new migration file appears under `ticolinea.panel.Infrastructure/Migrations/` whose `Up` calls `migrationBuilder.AddColumn<bool>(name: "sincronizado", table: "streams_tl", ... defaultValue: false)`.

- [ ] **Step 4: Verify the generated Up/Down**

Open the new migration; confirm `Up` adds `sincronizado` to `streams_tl` with `defaultValue: false` and `Down` drops it. Build:
```bash
dotnet build ticolinea.panel.Infrastructure/ticolinea.panel.Infrastructure.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Confirm it appears in the idempotent script**

Run:
```bash
dotnet ef migrations script --idempotent \
  --project ticolinea.panel.Infrastructure --startup-project ticolinea.panel.API \
  -o /tmp/schema-check.sql
grep -c "sincronizado" /tmp/schema-check.sql
```
Expected: ≥ 1 (the guarded `ALTER TABLE ... ADD sincronizado`).

---

### Task 5: Migration — `CREATE TABLE IF NOT EXISTS streams_info`

**Files:**
- Create: `ticolinea.panel.Infrastructure/Migrations/<timestamp>_CreateStreamsInfoIfNotExists.cs` (hand-written raw SQL)

**Why hand-written:** the `StreamInfo` entity is already in the model snapshot, so `dotnet ef migrations add` generates an EMPTY migration for it — EF thinks the table exists. But no prior migration ever `CreateTable`d it (existing nodes got it database-first). A fresh node built from the idempotent script therefore LACKS the table. We add a raw-SQL `CREATE TABLE IF NOT EXISTS` so the script creates it on fresh nodes and no-ops on existing ones.

**Interfaces:**
- Produces: `streams_info` table on any DB that lacks it. Columns must match `StreamInfoConfiguration`.

- [ ] **Step 1: Generate an empty migration to get correct timestamp/scaffolding**

Run:
```bash
dotnet ef migrations add CreateStreamsInfoIfNotExists \
  --project ticolinea.panel.Infrastructure --startup-project ticolinea.panel.API
```
Expected: a new (near-empty) migration file. You will replace its `Up`/`Down` bodies.

- [ ] **Step 2: Replace the migration body with raw idempotent SQL**

Edit the generated file so `Up` runs (column names/types from `StreamInfoConfiguration`: PK `stream_info_id` auto-increment, `stream_id` int default 0, `ejecutando` tinyint default 0, `proceso_id` int default -1, `info_progreso` text null, `iniciado` int default 0, `reportado_caido` tinyint default 0, charset utf8mb3):
```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS `streams_info` (
  `stream_info_id` INT NOT NULL AUTO_INCREMENT,
  `stream_id` INT NOT NULL DEFAULT 0,
  `ejecutando` TINYINT NOT NULL DEFAULT 0,
  `proceso_id` INT NOT NULL DEFAULT -1,
  `info_progreso` TEXT NULL,
  `iniciado` INT NULL DEFAULT 0,
  `reportado_caido` TINYINT NOT NULL DEFAULT 0,
  PRIMARY KEY (`stream_info_id`),
  KEY `stream_id` (`stream_id`),
  KEY `reportado_caido` (`reportado_caido`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_unicode_ci;");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    // No-op: streams_info predates EF management on existing nodes; never drop it here.
}
```

- [ ] **Step 3: Build + confirm the CREATE lands in the script**

Run:
```bash
dotnet build ticolinea.panel.Infrastructure/ticolinea.panel.Infrastructure.csproj
dotnet ef migrations script --idempotent \
  --project ticolinea.panel.Infrastructure --startup-project ticolinea.panel.API \
  -o /tmp/schema-check2.sql
grep -c "CREATE TABLE IF NOT EXISTS .streams_info." /tmp/schema-check2.sql
```
Expected: Build succeeded; grep ≥ 1.

- [ ] **Step 4: Sanity-check idempotency semantics**

Confirm by reading `/tmp/schema-check2.sql` that the streams_info block is `CREATE TABLE IF NOT EXISTS` (safe no-op on existing nodes) and that the migration's history-insert is guarded by EF's standard `IF NOT EXISTS(SELECT ... __EFMigrationsHistory)` wrapper. No live DB needed.

---

## PART B — Middleware node sync (depends on Part A)

### Task 6: Scaffold the middleware test project

**Files:**
- Create: `Ticolinea.Streaming.Middleware.Tests/Ticolinea.Streaming.Middleware.Tests.csproj`
- Modify: `TicolineaTV.sln` (add the project)

**Why:** `Ticolinea.Streaming.Middleware.Tests/` currently holds only stale `bin/obj` — no csproj, not in the solution. Parts 7-9 need a real xUnit project.

- [ ] **Step 1: Create the test csproj**

`Ticolinea.Streaming.Middleware.Tests/Ticolinea.Streaming.Middleware.Tests.csproj` (mirror the panel test stack — xUnit + Moq + FluentAssertions). **Target `net8.0`, NOT net6.0:** the dev/CI machine has SDK 8/9 and no net6.0 *runtime*, so a net6.0 test assembly builds but cannot RUN. A net8.0 test project validly references the net6.0 middleware library (net8 consumes net6 libs) and runs on the installed runtime. The library under test stays net6.0.
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Ticolinea.Streaming.Middleware\ticolinea.stream.service.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add a smoke test so the runner has something**

`Ticolinea.Streaming.Middleware.Tests/SmokeTest.cs`:
```csharp
using Xunit;
namespace Ticolinea.Streaming.Middleware.Tests;
public class SmokeTest { [Fact] public void Harness_runs() => Assert.True(true); }
```

- [ ] **Step 3: Add to the solution and run**

Run (from the middleware repo root that holds `TicolineaTV.sln`):
```bash
dotnet sln TicolineaTV.sln add Ticolinea.Streaming.Middleware.Tests/Ticolinea.Streaming.Middleware.Tests.csproj
dotnet test Ticolinea.Streaming.Middleware.Tests/Ticolinea.Streaming.Middleware.Tests.csproj
```
Expected: 1 passing.

---

### Task 7: `CatalogStream` model + `CatalogClient` (HTTP fetch)

**Files:**
- Create: `Ticolinea.Streaming.Middleware/Modelos/CatalogStream.cs`
- Create: `Ticolinea.Streaming.Middleware/Helpers/CatalogClient.cs`
- Test: `Ticolinea.Streaming.Middleware.Tests/CatalogClientTests.cs`

**Interfaces:**
- Consumes: `IHttpClientFactory` named client `"PanelApi"`, `Jwt:PanelApiUrl`, `Jwt:PanelApiKey`, `Constantes.Global.PROVIDER_ID`.
- Produces: `CatalogClient.FetchAsync()` → `Task<List<CatalogStream>?>` (null on any non-success / exception — signals "keep stale data"). `CatalogStream` has the same fields as the panel's `CatalogStreamDTO`.

- [ ] **Step 1: Create the model**

`Ticolinea.Streaming.Middleware/Modelos/CatalogStream.cs`:
```csharp
namespace ticolinea.stream.service.Modelos;

public class CatalogStream
{
    public int Id { get; set; }
    public string NombreStream { get; set; } = "";
    public string? FuenteStream { get; set; }
    public string? ImagenStream { get; set; }
    public int? IdCategoria { get; set; }
    public int Orden { get; set; }
    public int Agregado { get; set; }
    public int ProbesizeOndemand { get; set; }
    public sbyte EsBajodemanda { get; set; }
    public sbyte Tipo { get; set; }
    public string? Contenedor { get; set; }
    public sbyte Habilitado { get; set; }
    public string TranscodeAudio { get; set; } = "";
    public sbyte? Intervalo { get; set; }
    public sbyte? Segmentos { get; set; }
    public sbyte? Framerate { get; set; }
    public sbyte Transcode { get; set; }
    public string Resolucion { get; set; } = "";
    public string Bitrate { get; set; } = "";
    public string CanalEpg { get; set; } = "";
    public sbyte Cgop { get; set; }
    public int Gop { get; set; }
    public int CanalId { get; set; }
}
```

- [ ] **Step 2: Write the failing test (injectable HttpMessageHandler)**

`Ticolinea.Streaming.Middleware.Tests/CatalogClientTests.cs`:
```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class CatalogClientTests
{
    private class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code; private readonly string _body;
        public HttpRequestMessage? Seen;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken _)
        { Seen = r; return Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body, Encoding.UTF8, "application/json") }); }
    }

    private static CatalogClient Make(StubHandler h) =>
        new(new HttpClient(h), "http://panel/api/v2", "KEY", "acme");

    [Fact]
    public async Task Parses_ok_response_and_sends_key_and_slug()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[{\"Id\":10,\"NombreStream\":\"A\",\"FuenteStream\":\"u\"}]");
        var result = await Make(h).FetchAsync();
        result.Should().NotBeNull();
        result!.Single().Id.Should().Be(10);
        h.Seen!.RequestUri!.ToString().Should().Be("http://panel/api/v2/providers/acme/catalog");
        h.Seen.Headers.GetValues("X-Auth-API-Key").Single().Should().Be("KEY");
    }

    [Fact]
    public async Task Non_success_returns_null()
        => (await Make(new StubHandler(HttpStatusCode.InternalServerError, "")).FetchAsync()).Should().BeNull();

    [Fact]
    public async Task Exception_returns_null()
    {
        var client = new CatalogClient(new HttpClient(new ThrowingHandler()), "http://panel/api/v2", "KEY", "acme");
        (await client.FetchAsync()).Should().BeNull();
    }
    private class ThrowingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => throw new HttpRequestException("down"); }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test Ticolinea.Streaming.Middleware.Tests --filter CatalogClientTests`
Expected: FAIL — `CatalogClient` does not exist.

- [ ] **Step 4: Implement `CatalogClient`**

`Ticolinea.Streaming.Middleware/Helpers/CatalogClient.cs`:
```csharp
using System.Net.Http.Json;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

// Fetches the provider's catalog from the panel. Returns null on ANY failure
// (non-2xx, timeout, exception) — the caller treats null as "keep current data".
public class CatalogClient
{
    private readonly HttpClient _http;
    private readonly string _panelApiUrl;
    private readonly string _apiKey;
    private readonly string _slug;

    public CatalogClient(HttpClient http, string panelApiUrl, string apiKey, string slug)
    {
        _http = http;
        _panelApiUrl = panelApiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _slug = slug;
    }

    public async Task<List<CatalogStream>?> FetchAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_panelApiUrl}/providers/{_slug}/catalog");
            req.Headers.Add("X-Auth-API-Key", _apiKey);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<CatalogStream>>();
        }
        catch
        {
            return null; // network/parse failure → keep stale data
        }
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Ticolinea.Streaming.Middleware.Tests --filter CatalogClientTests`
Expected: 3 passing.

---

### Task 8: `PackageSyncPlan` — pure decision logic

**Files:**
- Create: `Ticolinea.Streaming.Middleware/Helpers/PackageSyncPlan.cs`
- Test: `Ticolinea.Streaming.Middleware.Tests/PackageSyncPlanTests.cs`

**Interfaces:**
- Produces:
  - `PackageSyncPlan.Build(IReadOnlyList<CatalogStream> catalog, IReadOnlyCollection<int> currentSyncedIds)` → `SyncDecision`.
  - `SyncDecision { List<CatalogStream> Upserts; List<int> IdsToDisable; bool SkipDisable; }`.
- Rules: `Upserts` = every catalog channel. `IdsToDisable` = `currentSyncedIds` not in the catalog. `SkipDisable = true` when `catalog.Count < 0.5 * currentSyncedIds.Count` (undersized-catalog guard) — in which case `IdsToDisable` is emptied.

This isolates the risky set-math + guard so it is fully unit-tested without a DB.

- [ ] **Step 1: Write the failing tests**

`Ticolinea.Streaming.Middleware.Tests/PackageSyncPlanTests.cs`:
```csharp
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
        var d = PackageSyncPlan.Build(catalog, current);
        d.Upserts.Select(x => x.Id).Should().BeEquivalentTo(new[] { 10, 11, 13 });
        d.IdsToDisable.Should().BeEquivalentTo(new[] { 12 });
        d.SkipDisable.Should().BeFalse();
    }

    [Fact]
    public void First_sync_no_current_disables_nothing()
    {
        var d = PackageSyncPlan.Build(new[] { C(10), C(11) }, System.Array.Empty<int>());
        d.IdsToDisable.Should().BeEmpty();
        d.SkipDisable.Should().BeFalse();
    }

    [Fact]
    public void Undersized_catalog_skips_disable_but_still_upserts()
    {
        // current 10 managed, catalog only 2 (< 50%) → suspect: upsert, do not disable
        var catalog = new[] { C(10), C(11) };
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current);
        d.Upserts.Should().HaveCount(2);
        d.SkipDisable.Should().BeTrue();
        d.IdsToDisable.Should().BeEmpty();
    }

    [Fact]
    public void Exactly_half_is_allowed_to_disable()
    {
        // 5 catalog vs 10 current = exactly 50% → NOT undersized (strict <)
        var catalog = Enumerable.Range(1, 5).Select(C).ToArray();
        var current = Enumerable.Range(1, 10).ToArray();
        var d = PackageSyncPlan.Build(catalog, current);
        d.SkipDisable.Should().BeFalse();
        d.IdsToDisable.Should().BeEquivalentTo(new[] { 6, 7, 8, 9, 10 });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Ticolinea.Streaming.Middleware.Tests --filter PackageSyncPlanTests`
Expected: FAIL — `PackageSyncPlan` / `SyncDecision` do not exist.

- [ ] **Step 3: Implement**

`Ticolinea.Streaming.Middleware/Helpers/PackageSyncPlan.cs`:
```csharp
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
    public static SyncDecision Build(IReadOnlyList<CatalogStream> catalog, IReadOnlyCollection<int> currentSyncedIds)
    {
        var catalogIds = new HashSet<int>(catalog.Select(c => c.Id));
        var undersized = currentSyncedIds.Count > 0
            && catalog.Count < 0.5 * currentSyncedIds.Count;

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
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Ticolinea.Streaming.Middleware.Tests --filter PackageSyncPlanTests`
Expected: 4 passing.

---

### Task 9: `PackageSyncService` — orchestrate fetch → plan → DB writes

**Files:**
- Create: `Ticolinea.Streaming.Middleware/Services/PackageSyncService.cs`

**Interfaces:**
- Consumes: `CatalogClient.FetchAsync()`, `PackageSyncPlan.Build(...)`, `Constantes.Global.MARIADB_CONN`.
- Produces: `PackageSyncService.SyncAsync()` — the full cycle. A static run-lock (`SemaphoreSlim(1,1)`) prevents overlap; returns immediately if a run is in progress.

**Testing note:** the DB writes require a live MySQL, which this project has no harness for. The risky logic (set-math, guard) is already unit-tested in Task 8; `CatalogClient` in Task 7. `SyncAsync`'s DB SQL is verified by the VM/integration path (add a step to `deploy/tests/integration.md` — see Step 3). Keep `SyncAsync` thin: fetch → guard on null → read current synced ids → `PackageSyncPlan.Build` → apply in one transaction.

- [ ] **Step 1: Implement the service**

`Ticolinea.Streaming.Middleware/Services/PackageSyncService.cs`:
```csharp
using MySqlConnector;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Services;

public class PackageSyncService
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CatalogClient _client;
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(PackageSyncService));

    public PackageSyncService(CatalogClient client) => _client = client;

    public async Task SyncAsync()
    {
        if (!await _lock.WaitAsync(0)) { _log.Warn("Package sync already running; skipping this tick."); return; }
        try
        {
            var catalog = await _client.FetchAsync();
            if (catalog == null) { _log.Warn("Catalog fetch failed; keeping current streams unchanged."); return; }

            await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
            await cnn.OpenAsync();

            var current = await ReadSyncedIdsAsync(cnn);
            var decision = PackageSyncPlan.Build(catalog, current);
            if (decision.SkipDisable)
                _log.Warn($"Undersized catalog ({catalog.Count} vs {current.Count} managed); applying upserts, skipping disables.");

            await using var tx = await cnn.BeginTransactionAsync();
            try
            {
                foreach (var s in decision.Upserts)
                {
                    await UpsertStreamAsync(cnn, tx, s);
                    await EnsureStreamInfoAsync(cnn, tx, s.Id);
                }
                foreach (var id in decision.IdsToDisable)
                    await DisableStreamAsync(cnn, tx, id);
                await tx.CommitAsync();
                _log.Info($"Package sync ok: {decision.Upserts.Count} upserted, {decision.IdsToDisable.Count} disabled.");
            }
            catch (Exception ex) { await tx.RollbackAsync(); _log.Error("Package sync failed; rolled back.", ex); }
        }
        finally { _lock.Release(); }
    }

    private static async Task<List<int>> ReadSyncedIdsAsync(MySqlConnection cnn)
    {
        var ids = new List<int>();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "SELECT id FROM streams_tl WHERE sincronizado = 1;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
        return ids;
    }

    private static async Task UpsertStreamAsync(MySqlConnection cnn, MySqlTransaction tx, CatalogStream s)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO streams_tl
  (id, nombre_stream, fuente_stream, imagen_stream, id_categoria, orden, agregado,
   probesize_ondemand, es_bajodemanda, tipo, contenedor, habilitado, transcode_audio,
   intervalo, segmentos, framerate, transcode, resolucion, bitrate, canal_epg, cgop, gop, canal_id, sincronizado)
VALUES
  (@id, @nombre, @fuente, @imagen, @cat, @orden, @agregado,
   @probe, @vod, @tipo, @cont, 1, @taudio,
   @interv, @segs, @fr, @trans, @res, @bitrate, @epg, @cgop, @gop, @canalid, 1)
ON DUPLICATE KEY UPDATE
  nombre_stream=VALUES(nombre_stream), fuente_stream=VALUES(fuente_stream),
  imagen_stream=VALUES(imagen_stream), id_categoria=VALUES(id_categoria), orden=VALUES(orden),
  probesize_ondemand=VALUES(probesize_ondemand), es_bajodemanda=VALUES(es_bajodemanda),
  tipo=VALUES(tipo), contenedor=VALUES(contenedor), habilitado=1, transcode_audio=VALUES(transcode_audio),
  intervalo=VALUES(intervalo), segmentos=VALUES(segmentos), framerate=VALUES(framerate),
  transcode=VALUES(transcode), resolucion=VALUES(resolucion), bitrate=VALUES(bitrate),
  canal_epg=VALUES(canal_epg), cgop=VALUES(cgop), gop=VALUES(gop), canal_id=VALUES(canal_id), sincronizado=1;";
        cmd.Parameters.AddWithValue("@id", s.Id);
        cmd.Parameters.AddWithValue("@nombre", s.NombreStream);
        cmd.Parameters.AddWithValue("@fuente", (object?)s.FuenteStream ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@imagen", (object?)s.ImagenStream ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat", (object?)s.IdCategoria ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@orden", s.Orden);
        cmd.Parameters.AddWithValue("@agregado", s.Agregado);
        cmd.Parameters.AddWithValue("@probe", s.ProbesizeOndemand);
        cmd.Parameters.AddWithValue("@vod", s.EsBajodemanda);
        cmd.Parameters.AddWithValue("@tipo", s.Tipo);
        cmd.Parameters.AddWithValue("@cont", (object?)s.Contenedor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taudio", s.TranscodeAudio ?? "");
        cmd.Parameters.AddWithValue("@interv", (object?)s.Intervalo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@segs", (object?)s.Segmentos ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fr", (object?)s.Framerate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@trans", s.Transcode);
        cmd.Parameters.AddWithValue("@res", s.Resolucion ?? "");
        cmd.Parameters.AddWithValue("@bitrate", s.Bitrate ?? "");
        cmd.Parameters.AddWithValue("@epg", s.CanalEpg ?? "");
        cmd.Parameters.AddWithValue("@cgop", s.Cgop);
        cmd.Parameters.AddWithValue("@gop", s.Gop);
        cmd.Parameters.AddWithValue("@canalid", s.CanalId);
        await cmd.ExecuteNonQueryAsync();
    }

    // Conditional insert — streams_info has NO unique on stream_id, so INSERT IGNORE won't work.
    // iniciado defaults to 1 here and is NEVER written again by the sync.
    private static async Task EnsureStreamInfoAsync(MySqlConnection cnn, MySqlTransaction tx, int streamId)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO streams_info (stream_id, ejecutando, proceso_id, info_progreso, iniciado, reportado_caido)
SELECT @id, 0, -1, '', 1, 0
WHERE NOT EXISTS (SELECT 1 FROM streams_info WHERE stream_id = @id);";
        cmd.Parameters.AddWithValue("@id", streamId);
        await cmd.ExecuteNonQueryAsync();
    }

    // Removal = habilitado=0 ONLY. iniciado is left untouched (node-owned).
    private static async Task DisableStreamAsync(MySqlConnection cnn, MySqlTransaction tx, int id)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE streams_tl SET habilitado = 0 WHERE id = @id AND sincronizado = 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 2: Build the middleware**

Run: `dotnet build Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Add a manual verification step to the integration checklist**

Append to `deploy/tests/integration.md` (Part B / package sync section) a manual check, since the DB writes have no unit harness:
```markdown
### Package sync (spec B)
- [ ] After bootstrap+deploy, trigger a sync (restart the node so the boot job runs).
- [ ] `mysql <db> -e "SELECT COUNT(*) FROM streams_tl WHERE sincronizado=1;"` → matches the package's channel count.
- [ ] `mysql <db> -e "SELECT COUNT(*) FROM streams_info si JOIN streams_tl s ON s.id=si.stream_id WHERE s.sincronizado=1;"` → same count (a streams_info row per synced channel).
- [ ] Set a channel's `iniciado=0` by hand; restart; confirm it stays 0 (sync does not overwrite iniciado).
- [ ] Remove a channel from the package in the panel; wait for / trigger a sync; confirm that channel is `habilitado=0` and its row remains.
```

---

### Task 10: Hangfire wiring — recurring + on-boot

**Files:**
- Modify: `Ticolinea.Streaming.Middleware/Jobs.cs` (add `SyncPackageCatalog`)
- Modify: `Ticolinea.Streaming.Middleware/Program.cs` (recurring 6h + boot enqueue + build the client)

**Interfaces:**
- Consumes: `PackageSyncService.SyncAsync()`, `IHttpClientFactory` `"PanelApi"`, config `Jwt:PanelApiUrl` / `Jwt:PanelApiKey`, `Constantes.Global.PROVIDER_ID`, `PackageSync:IntervalHours` (default 6).
- Produces: recurring Hangfire job `"sync_package_catalog"` + a one-shot boot run.

- [ ] **Step 1: Add the job method**

In `Ticolinea.Streaming.Middleware/Jobs.cs` (static `Jobs` class), add. Build the `CatalogClient` from the named HttpClient + config so the job is self-contained (mirrors how other static jobs pull dependencies from `Constantes.Global` / config):
```csharp
public static async Task SyncPackageCatalog()
{
    var http = Constantes.Global.HttpClientFactory.CreateClient("PanelApi");
    http.Timeout = TimeSpan.FromSeconds(30);
    var client = new Helpers.CatalogClient(
        http,
        Constantes.Global.PANEL_API_URL,
        Constantes.Global.PANEL_API_KEY,
        Constantes.Global.PROVIDER_ID);
    await new Services.PackageSyncService(client).SyncAsync();
}
```

- [ ] **Step 2: Expose the needed globals**

Confirm/add on `Constantes/Global.cs` static accessors used above: `HttpClientFactory` (`IHttpClientFactory`), `PANEL_API_URL`, `PANEL_API_KEY` (read from `Jwt:PanelApiUrl` / `Jwt:PanelApiKey`). If any is missing, add it following the existing `Global.Initialize(...)` pattern (Global is populated from config in `Program.cs:57`). Example additions:
```csharp
public static IHttpClientFactory HttpClientFactory { get; set; } = null!;
public static string PANEL_API_URL => _jwt?.PanelApiUrl?.TrimEnd('/') ?? "";
public static string PANEL_API_KEY => _jwt?.PanelApiKey ?? "";
```
Wire `HttpClientFactory` in `Program.cs` after the app is built (where the service provider is available), e.g. `Constantes.Global.HttpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();`. (Match the exact `Global.Initialize` mechanism already in use — read `Constantes/Global.cs` and `Program.cs:57` before editing.)

- [ ] **Step 3: Register the recurring job + boot run**

In `Ticolinea.Streaming.Middleware/Program.cs`, immediately after the existing `RecurringJob.AddOrUpdate(...)` block (~line 136):
```csharp
var syncHours = builder.Configuration.GetValue<int?>("PackageSync:IntervalHours") ?? 6;
RecurringJob.AddOrUpdate("sync_package_catalog", () => Jobs.SyncPackageCatalog(), $"0 */{syncHours} * * *");
BackgroundJob.Enqueue(() => Jobs.SyncPackageCatalog()); // run once on boot
```

- [ ] **Step 4: Add the config default**

In `Ticolinea.Streaming.Middleware/appsettings.json`, add:
```json
  "PackageSync": { "IntervalHours": 6 },
```

- [ ] **Step 5: Build + full test suite**

Run:
```bash
dotnet build Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj
dotnet test Ticolinea.Streaming.Middleware.Tests
```
Expected: Build succeeded; all tests pass (smoke + CatalogClient + PackageSyncPlan).

---

## Self-Review

**Spec coverage:**
- §5 panel endpoint (resolution, tipo=1 filter, DTO, auth, 404) → Tasks 1-3. ✓
- §6 node sync (upsert by id, sincronizado/habilitado, ensure streams_info w/ iniciado default 1 never re-written, reconcile habilitado=0) → Tasks 8-9. ✓
- §7 migrations (sincronizado; streams_info create) → Tasks 4-5, with the corrected framing (entity exists in model but no CreateTable migration → hand-written idempotent SQL). ✓
- §8 failure handling (panel-down zero mutations, undersized guard <50%, transaction, run-lock) → Tasks 7 (null on failure), 8 (guard), 9 (transaction + lock). ✓
- §10 testing → panel Tasks 2-3 (xUnit), middleware Tasks 7-8 (xUnit), Task 9 Step 3 (integration checklist for the DB writes). ✓
- Scheduling (6h + boot) → Task 10. ✓

**Corrections vs the spec captured here:** (1) `streams_info` entity already exists in the model — the migration must be hand-written raw `CREATE TABLE IF NOT EXISTS`, not an EF-generated `CreateTable`. (2) `streams_info` has no unique on `stream_id`, so "ensure row exists" is a conditional `INSERT ... WHERE NOT EXISTS`, not `INSERT IGNORE`. (3) The middleware test project does not exist and must be scaffolded (Task 6). (4) `PackageResolver` lives in the middleware, not the panel — the panel endpoint implements resolution directly from `Provider.IsExternal` / `DefaultPaqueteTvId` (Task 2). Update spec §7.2 wording to match (1).

**Placeholder scan:** none — every code step contains complete code.

**Type consistency:** `CatalogStreamDTO` (panel) ↔ `CatalogStream` (middleware) share field names; `ICatalogService.GetCatalog` returns `List<CatalogStreamDTO>?` used in Tasks 2-3; `PackageSyncPlan.Build`/`SyncDecision` used consistently in Tasks 8-9; `CatalogClient.FetchAsync()` returns `List<CatalogStream>?` used in Tasks 7, 9, 10.

**Open risk to flag at execution:** Task 9's DB SQL is not unit-tested (no MySQL harness); it is covered by the pure logic (Task 8) + the integration checklist (Task 9 Step 3). If stronger coverage is wanted, add a Testcontainers-MySQL integration test as a follow-up — not in this plan's scope.
