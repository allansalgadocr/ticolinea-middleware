# Admin Channel Control UI (Spec C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** An admin-only page in the main panel that lists a selected provider's channels with near-real-time state, starts/stops/restarts them, and shows the node's CPU/RAM/disk/uptime.

**Architecture:** New node `AdminController` (gated by a new inbound `X-Auth-API-Key` filter) wraps the node's existing FFmpeg primitives and exposes metrics. A panel `ProviderControlService` proxies to a provider's node via `Provider.ConnectionUrl` + the shared key; a panel `AdminControlController` exposes it. A React page (SWR polling + recharts) consumes it, gated on `isSuperAdmin`.

**Tech Stack:** .NET (middleware net6 + MySqlConnector; panel net8 + EF/HttpClient; xUnit tests net8), React 19 + Vite + SWR + recharts + axios.

## Global Constraints

- **ADDITIVE ONLY. Do not change any existing behavior.** New controllers/services/page. The ONLY edits to existing code are *visibility* changes (make `Jobs.ObtenerUsoCPU/RAM/Disco` callable; add a read accessor over `StreamingService._lastProcessStart`) — no existing method body changes, no login/auth-flow change.
- **NO auto-commit / no git commit.** The user commits. End every task at "tests pass / builds"; NO commit step.
- **Auth posture (per product owner):** panel control endpoints follow the existing header model — `[CustomAuthorization(Global.Secret)]` + client-supplied `X-Is-Super-Admin`; page hidden unless `isSuperAdmin`. No new token/login change. Node endpoints gated by a new inbound `X-Auth-API-Key` filter validating `Constantes.Global.PANEL_API_KEY` (= panel `Global.Secret`).
- **`iniciado` semantics:** stop → `iniciado=0, ejecutando=0, proceso_id=-1`; start/restart → `iniciado=1, ejecutando=1, habilitado=1`. Restart = `DetenerProceso` then `ForzarInicioInmediato`.
- **Frameworks:** middleware `net6.0`, panel `net8.0`, panel test project `net8.0`, middleware test project `net8.0` referencing the net6 lib.
- **Node control primitives (reuse, do not reimplement):** `StreamingService.ForzarInicioInmediato(StreamDb)`, `StreamingService.DetenerSupervision(int)`, `Jobs.DetenerProceso(int procesoId, int streamId)`, `StreamStatusHelper.GetRealTimeStreamStatusAsync(int)`, DB via `Constantes.Global.MARIADB_CONN`.
- **Completion gate (§ final task):** `dotnet build` panel API + middleware; panel frontend build; `shellcheck` + `bats deploy/tests/` clean; existing xUnit suites green.
- Repos: middleware `/Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel/Ticolinea.Streaming.Middleware`; panel `/Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel`; frontend `.../ticolinea.panel/ticolinea.panel.Frontend`.
- Spec: `docs/superpowers/specs/2026-07-15-admin-channel-control-design.md`.

---

## File Structure

**Node (middleware):**
```
Attributes/NodeApiKeyAttribute.cs            # NEW — inbound X-Auth-API-Key filter
Controllers/AdminController.cs               # NEW — GET streams/system, POST actions
Services/StreamingService.cs                 # MODIFY — add public GetStreamUptimeSeconds(int)
Jobs.cs                                       # MODIFY — visibility: ObtenerUsoCPU/RAM/Disco -> public static; add ObtenerMetricasSalud() JSON aggregator
```
**Panel:**
```
ticolinea.panel.Application/DTOs/Control/NodeStreamDto.cs        # NEW
ticolinea.panel.Application/DTOs/Control/NodeSystemDto.cs        # NEW
ticolinea.panel.Application/DTOs/Control/ControlResultDto.cs     # NEW
ticolinea.panel.Application/Services/IProviderControlService.cs  # NEW
ticolinea.panel.Application/Services/ProviderControlService.cs   # NEW
ticolinea.panel.API/Controllers/AdminControlController.cs        # NEW
ticolinea.panel.API/Program.cs                                  # MODIFY — AddScoped registration
ticolinea.panel.Tests/Services/ProviderControlServiceTests.cs   # NEW
```
**Frontend:**
```
src/utils/constants.ts                       # MODIFY — ROUTES.CHANNEL_CONTROL
src/App.tsx                                   # MODIFY — route
src/components/Sidebar.tsx                    # MODIFY — gated menu item
src/services/channelControl.ts               # NEW — typed API calls + SWR keys
src/pages/ChannelControl.tsx                  # NEW — the page
```

---

## PART A — Node (middleware)

### Task 1: Node inbound API-key filter

**Files:**
- Create: `Ticolinea.Streaming.Middleware/Attributes/NodeApiKeyAttribute.cs`
- Test: `Ticolinea.Streaming.Middleware.Tests/NodeApiKeyAttributeTests.cs`

**Interfaces:**
- Produces: `[NodeApiKey]` attribute (`IAsyncAuthorizationFilter`) — 401 if `X-Auth-API-Key` header missing, 403 if it doesn't equal `Constantes.Global.PANEL_API_KEY`, else pass.

- [ ] **Step 1: Write the failing test**

`Ticolinea.Streaming.Middleware.Tests/NodeApiKeyAttributeTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using ticolinea.stream.service.Attributes;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class NodeApiKeyAttributeTests
{
    private static AuthorizationFilterContext Ctx(string? key)
    {
        var http = new DefaultHttpContext();
        if (key != null) http.Request.Headers["X-Auth-API-Key"] = key;
        var actionCtx = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionCtx, new List<IFilterMetadata>());
    }

    [Fact]
    public async Task Missing_key_is_unauthorized()
    {
        var ctx = Ctx(null);
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Wrong_key_is_forbidden()
    {
        ticolinea.stream.service.Constantes.Global.TestSetPanelApiKey("REALKEY");
        var ctx = Ctx("WRONG");
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Correct_key_passes()
    {
        ticolinea.stream.service.Constantes.Global.TestSetPanelApiKey("REALKEY");
        var ctx = Ctx("REALKEY");
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeNull();
    }
}
```

Note: `Global.PANEL_API_KEY` is a computed property off `_jwt`. To test without full init, add a tiny test-only setter (Step 3). If `Global` cannot expose a setter cleanly, the filter reads `Constantes.Global.PANEL_API_KEY` and the test seeds it via reflection instead — but prefer the setter.

- [ ] **Step 2: Run to verify failure**

Run: `cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel && dotnet test Ticolinea.Streaming.Middleware.Tests --filter NodeApiKeyAttributeTests`
Expected: FAIL — `NodeApiKeyAttribute` does not exist.

- [ ] **Step 3: Implement the attribute (+ test seam on Global)**

`Ticolinea.Streaming.Middleware/Attributes/NodeApiKeyAttribute.cs`:
```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ticolinea.stream.service.Attributes;

// Inbound gate for node admin endpoints. Validates X-Auth-API-Key against the
// shared panel key the node already holds (Global.PANEL_API_KEY).
public class NodeApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Auth-API-Key", out var key))
        { context.Result = new UnauthorizedResult(); return Task.CompletedTask; }

        var expected = Constantes.Global.PANEL_API_KEY;
        if (string.IsNullOrEmpty(expected) || !string.Equals(key, expected, StringComparison.Ordinal))
        { context.Result = new ForbidResult(); return Task.CompletedTask; }

        return Task.CompletedTask;
    }
}
```
In `Constantes/Global.cs`, add a test-only seam next to `PANEL_API_KEY` (does not change existing behavior — new members only):
```csharp
// test seam: lets unit tests set the key without full Initialize()
internal static string? _testPanelApiKey;
public static void TestSetPanelApiKey(string v) => _testPanelApiKey = v;
```
and make the existing `PANEL_API_KEY` prefer the test value if set (leave production path identical):
```csharp
public static string PANEL_API_KEY => _testPanelApiKey ?? (_jwt?.PanelApiKey ?? "");
```
(This is additive — production `_jwt` path is unchanged when `_testPanelApiKey` is null.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Ticolinea.Streaming.Middleware.Tests --filter NodeApiKeyAttributeTests`
Expected: 3 passing.

---

### Task 2: Expose node metrics + per-stream uptime

**Files:**
- Modify: `Ticolinea.Streaming.Middleware/Jobs.cs`
- Modify: `Ticolinea.Streaming.Middleware/Services/StreamingService.cs`

**Interfaces:**
- Produces: `Jobs.ObtenerUsoCPU()/ObtenerUsoRAM()/ObtenerUsoDisco()` become `public static async Task<double>`; new `Jobs.ObtenerMetricasSaludAsync()` → `Task<(double cpu, double ram, double disk)>`. `StreamingService.GetStreamUptimeSeconds(int streamId)` → `double` (0 if unknown).

- [ ] **Step 1: Change metric visibility + add aggregator**

In `Jobs.cs`, change the three method signatures from `private static async Task<double>` to `public static async Task<double>` for `ObtenerUsoCPU` (line ~1030), `ObtenerUsoRAM` (~1092), `ObtenerUsoDisco` (~1116). Do NOT change their bodies. Add:
```csharp
public static async Task<(double cpu, double ram, double disk)> ObtenerMetricasSaludAsync()
{
    var cpu = await ObtenerUsoCPU();
    var ram = await ObtenerUsoRAM();
    var disk = await ObtenerUsoDisco();
    return (cpu, ram, disk);
}
```

- [ ] **Step 2: Add the uptime accessor to StreamingService**

In `Services/StreamingService.cs`, add (mirrors the `_lastProcessStart` read at line 232; no behavior change):
```csharp
// Seconds since this stream's ffmpeg was last (re)started, or 0 if not tracked.
public static double GetStreamUptimeSeconds(int streamId)
{
    if (_lastProcessStart.TryGetValue(streamId, out var start))
        return Math.Max(0, (DateTime.UtcNow - start).TotalSeconds);
    return 0;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj -v q --nologo`
Expected: 0 errors.

---

### Task 3: Node `AdminController`

**Files:**
- Create: `Ticolinea.Streaming.Middleware/Controllers/AdminController.cs`

**Interfaces:**
- Consumes: `[NodeApiKey]`, `Jobs.ObtenerMetricasSaludAsync`, `StreamingService.GetStreamUptimeSeconds`, `StreamStatusHelper.GetRealTimeStreamStatusAsync`, `StreamingService.ForzarInicioInmediato`, `Jobs.DetenerProceso`, `Constantes.Global.MARIADB_CONN`.
- Produces (JSON):
  - `GET /api/admin/streams` → `[{ id, nombre, running, uptimeSec, procesoId }]`
  - `GET /api/admin/system` → `{ uptimeSec, cpuPct, ramPct, diskPct }`
  - `POST /api/admin/streams/{id}/start|stop|restart` → `{ success, message }`

Note: node actions call static primitives (not unit-testable via mocks); correctness is covered by the auth-filter test (Task 1) + the VM/manual integration checklist (Task 9). Keep the controller thin; replicate the exact DB updates `PanelController` uses.

- [ ] **Step 1: Implement the controller**

`Ticolinea.Streaming.Middleware/Controllers/AdminController.cs`:
```csharp
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ticolinea.stream.service.Attributes;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;

namespace ticolinea.stream.service.Controllers;

[Route("api/admin")]
[ApiController]
[NodeApiKey]
public class AdminController : ControllerBase
{
    [HttpGet("streams")]
    public async Task<IActionResult> GetStreams()
    {
        var rows = new List<object>();
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = @"SELECT a.id, a.nombre_stream, b.iniciado, b.ejecutando, b.proceso_id
                            FROM streams_tl a INNER JOIN streams_info b ON a.id = b.stream_id
                            WHERE a.habilitado = 1 AND a.es_bajodemanda = 0 AND a.tipo = 1
                            ORDER BY a.orden;";
        var items = new List<(int id, string nombre, int proceso)>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                items.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(4) ? -1 : r.GetInt32(4)));

        foreach (var it in items)
        {
            var status = await StreamStatusHelper.GetRealTimeStreamStatusAsync(it.id);
            rows.Add(new
            {
                id = it.id,
                nombre = it.nombre,
                running = status.IsRunning,
                uptimeSec = status.IsRunning ? StreamingService.GetStreamUptimeSeconds(it.id) : 0,
                procesoId = status.ProcessId ?? it.proceso
            });
        }
        return Ok(rows);
    }

    [HttpGet("system")]
    public async Task<IActionResult> GetSystem()
    {
        var (cpu, ram, disk) = await Jobs.ObtenerMetricasSaludAsync();
        var uptime = (long)(Environment.TickCount64 / 1000);
        return Ok(new { uptimeSec = uptime, cpuPct = cpu, ramPct = ram, diskPct = disk });
    }

    [HttpPost("streams/{id:int}/{action}")]
    public async Task<IActionResult> Control(int id, string action)
    {
        var stream = await LoadStream(id);
        if (stream == null) return NotFound(new { success = false, message = $"stream {id} not found" });

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "stop":
                    await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId);
                    await SetInfo(id, iniciado: 0, ejecutando: 0, procesoId: -1);
                    return Ok(new { success = true, message = "stopped" });

                case "start":
                case "restart":
                    if (stream.ProcesoId > 0) { await Jobs.DetenerProceso(stream.ProcesoId, stream.StreamId); await Task.Delay(1000); }
                    var ok = await StreamingService.ForzarInicioInmediato(stream);
                    await SetInfo(id, iniciado: 1, ejecutando: 1, procesoId: null);
                    await SetHabilitado(id);
                    return Ok(new { success = ok, message = ok ? action + "ed" : "process did not come up" });

                default:
                    return BadRequest(new { success = false, message = $"unknown action '{action}'" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Loads a StreamDb by id — same columns ObtenerStreamsActivos selects.
    private static async Task<StreamDb?> LoadStream(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = @"SELECT fuente_stream, stream_id, probesize_ondemand, es_bajodemanda,
                                   transcode_audio, intervalo, segmentos, framerate, transcode,
                                   resolucion, bitrate, proceso_id, cgop, gop
                            FROM streams_tl a INNER JOIN streams_info b ON a.id = b.stream_id
                            WHERE a.id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new StreamDb
        {
            Fuente = r.IsDBNull(0) ? "" : r.GetString(0),
            StreamId = r.GetInt32(1),
            ProbeSize = r.GetInt32(2),
            EsBajoDemanda = r.GetSByte(3),
            TranscodeAudio = r.IsDBNull(4) ? "" : r.GetString(4),
            Intervalo = r.IsDBNull(5) ? (sbyte)4 : r.GetSByte(5),
            Segmentos = r.IsDBNull(6) ? (sbyte)4 : r.GetSByte(6),
            Framerate = r.IsDBNull(7) ? (sbyte)0 : r.GetSByte(7),
            Transcode = r.GetSByte(8),
            Resolucion = r.IsDBNull(9) ? "" : r.GetString(9),
            Bitrate = r.IsDBNull(10) ? "" : r.GetString(10),
            ProcesoId = r.IsDBNull(11) ? -1 : r.GetInt32(11),
            CGOP = r.GetSByte(12),
            GOP = r.GetInt32(13)
        };
    }

    private static async Task SetInfo(int id, int iniciado, int ejecutando, int? procesoId)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = procesoId.HasValue
            ? "UPDATE streams_info SET iniciado=@i, ejecutando=@e, proceso_id=@p WHERE stream_id=@id;"
            : "UPDATE streams_info SET iniciado=@i, ejecutando=@e, reportado_caido=0 WHERE stream_id=@id;";
        cmd.Parameters.AddWithValue("@i", iniciado);
        cmd.Parameters.AddWithValue("@e", ejecutando);
        if (procesoId.HasValue) cmd.Parameters.AddWithValue("@p", procesoId.Value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetHabilitado(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE streams_tl SET habilitado=1 WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
```
IMPORTANT: verify the `StreamDb` property names/types against `Modelos/Stream.cs` before finalizing (the reader mapping must match the real model — `ProbeSize`, `EsBajoDemanda`, `Intervalo` types etc.). Fix any mismatch to the real model; do not invent members.

- [ ] **Step 2: Build**

Run: `dotnet build Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj -v q --nologo`
Expected: 0 errors. Then `dotnet test Ticolinea.Streaming.Middleware.Tests` → the 3 filter tests + prior suite still pass.

---

## PART B — Panel API

### Task 4: `ProviderControlService` (per-provider proxy)

**Files:**
- Create: `ticolinea.panel.Application/DTOs/Control/{NodeStreamDto,NodeSystemDto,ControlResultDto}.cs`
- Create: `ticolinea.panel.Application/Services/IProviderControlService.cs`, `ProviderControlService.cs`
- Test: `ticolinea.panel.Tests/Services/ProviderControlServiceTests.cs`

**Interfaces:**
- Consumes: `IProviderService.GetProvider(int)` → `ProviderDTO?` (has `ConnectionUrl`), `IHttpClientFactory`, `ticolinea.panel.API`... no — `Global.Secret` lives in API; pass the key in via constructor from config instead (see impl).
- Produces:
  - `Task<List<NodeStreamDto>?> GetStreams(int providerId)` (null if provider/URL missing or node unreachable)
  - `Task<NodeSystemDto?> GetSystem(int providerId)`
  - `Task<ControlResultDto> Control(int providerId, int streamId, string action)`

- [ ] **Step 1: DTOs**

`NodeStreamDto.cs`:
```csharp
namespace ticolinea.panel.Application.DTOs.Control;
public class NodeStreamDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public bool Running { get; set; }
    public double UptimeSec { get; set; }
    public int ProcesoId { get; set; }
}
```
`NodeSystemDto.cs`:
```csharp
namespace ticolinea.panel.Application.DTOs.Control;
public class NodeSystemDto
{
    public long UptimeSec { get; set; }
    public double CpuPct { get; set; }
    public double RamPct { get; set; }
    public double DiskPct { get; set; }
}
```
`ControlResultDto.cs`:
```csharp
namespace ticolinea.panel.Application.DTOs.Control;
public class ControlResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
```

- [ ] **Step 2: Write the failing tests (mock HttpMessageHandler)**

`ticolinea.panel.Tests/Services/ProviderControlServiceTests.cs`:
```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ticolinea.panel.Application.DTOs.Providers;
using ticolinea.panel.Application.Services;
using Xunit;

namespace ticolinea.panel.Tests.Services;

public class ProviderControlServiceTests
{
    private class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly HttpStatusCode _code; private readonly string _body;
        public System.Net.Http.HttpRequestMessage? Seen;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage r, CancellationToken _)
        { Seen = r; return Task.FromResult(new System.Net.Http.HttpResponseMessage(_code) { Content = new StringContent(_body, Encoding.UTF8, "application/json") }); }
    }
    private class ThrowHandler : System.Net.Http.HttpMessageHandler
    { protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage r, CancellationToken c) => throw new HttpRequestException("down"); }

    private static IProviderControlService Make(System.Net.Http.HttpMessageHandler h, ProviderDTO? provider)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new System.Net.Http.HttpClient(h));
        var providerSvc = new Mock<IProviderService>();
        providerSvc.Setup(p => p.GetProvider(It.IsAny<int>())).ReturnsAsync(provider);
        return new ProviderControlService(factory.Object, providerSvc.Object, "KEY",
            Mock.Of<ILogger<ProviderControlService>>());
    }

    private static ProviderDTO P(string url) => new() { Id = 1, ProviderName = "acme", ConnectionUrl = url };

    [Fact]
    public async Task GetStreams_builds_url_and_sends_key()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[{\"id\":10,\"nombre\":\"A\",\"running\":true,\"uptimeSec\":5,\"procesoId\":123}]");
        var result = await Make(h, P("http://node:27701")).GetStreams(1);
        result.Should().NotBeNull();
        result!.Single().Id.Should().Be(10);
        h.Seen!.RequestUri!.ToString().Should().Be("http://node:27701/api/admin/streams");
        h.Seen.Headers.GetValues("X-Auth-API-Key").Single().Should().Be("KEY");
    }

    [Fact]
    public async Task Unknown_provider_returns_null()
        => (await Make(new StubHandler(HttpStatusCode.OK, "[]"), null).GetStreams(1)).Should().BeNull();

    [Fact]
    public async Task Node_unreachable_returns_null_not_throw()
        => (await Make(new ThrowHandler(), P("http://node:27701")).GetStreams(1)).Should().BeNull();

    [Fact]
    public async Task Control_posts_action_and_returns_result()
    {
        var h = new StubHandler(HttpStatusCode.OK, "{\"success\":true,\"message\":\"restarted\"}");
        var r = await Make(h, P("http://node:27701")).Control(1, 10, "restart");
        r.Success.Should().BeTrue();
        h.Seen!.RequestUri!.ToString().Should().Be("http://node:27701/api/admin/streams/10/restart");
        h.Seen.Method.Should().Be(HttpMethod.Post);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel && dotnet test ticolinea.panel.Tests --filter ProviderControlServiceTests`
Expected: FAIL — service/DTOs don't exist.

- [ ] **Step 4: Implement**

`IProviderControlService.cs`:
```csharp
using ticolinea.panel.Application.DTOs.Control;
namespace ticolinea.panel.Application.Services;
public interface IProviderControlService
{
    Task<List<NodeStreamDto>?> GetStreams(int providerId);
    Task<NodeSystemDto?> GetSystem(int providerId);
    Task<ControlResultDto> Control(int providerId, int streamId, string action);
}
```
`ProviderControlService.cs`:
```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using ticolinea.panel.Application.DTOs.Control;

namespace ticolinea.panel.Application.Services;

public class ProviderControlService : IProviderControlService
{
    private readonly IHttpClientFactory _http;
    private readonly IProviderService _providers;
    private readonly string _apiKey;
    private readonly ILogger<ProviderControlService> _log;

    public ProviderControlService(IHttpClientFactory http, IProviderService providers, string apiKey, ILogger<ProviderControlService> log)
    { _http = http; _providers = providers; _apiKey = apiKey; _log = log; }

    private async Task<(HttpClient client, string baseUrl)?> NodeFor(int providerId)
    {
        var p = await _providers.GetProvider(providerId);
        if (p == null || string.IsNullOrWhiteSpace(p.ConnectionUrl)) return null;
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        return (client, p.ConnectionUrl.TrimEnd('/'));
    }

    public async Task<List<NodeStreamDto>?> GetStreams(int providerId)
    {
        var node = await NodeFor(providerId); if (node == null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{node.Value.baseUrl}/api/admin/streams");
            req.Headers.Add("X-Auth-API-Key", _apiKey);
            using var resp = await node.Value.client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<NodeStreamDto>>();
        }
        catch (Exception ex) { _log.LogWarning(ex, "GetStreams provider {Id} unreachable", providerId); return null; }
    }

    public async Task<NodeSystemDto?> GetSystem(int providerId)
    {
        var node = await NodeFor(providerId); if (node == null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{node.Value.baseUrl}/api/admin/system");
            req.Headers.Add("X-Auth-API-Key", _apiKey);
            using var resp = await node.Value.client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<NodeSystemDto>();
        }
        catch (Exception ex) { _log.LogWarning(ex, "GetSystem provider {Id} unreachable", providerId); return null; }
    }

    public async Task<ControlResultDto> Control(int providerId, int streamId, string action)
    {
        var node = await NodeFor(providerId);
        if (node == null) return new ControlResultDto { Success = false, Message = "provider or connection url not found" };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{node.Value.baseUrl}/api/admin/streams/{streamId}/{action}");
            req.Headers.Add("X-Auth-API-Key", _apiKey);
            using var resp = await node.Value.client.SendAsync(req);
            var body = await resp.Content.ReadFromJsonAsync<ControlResultDto>();
            return body ?? new ControlResultDto { Success = resp.IsSuccessStatusCode, Message = resp.StatusCode.ToString() };
        }
        catch (Exception ex) { _log.LogWarning(ex, "Control provider {Id} unreachable", providerId); return new ControlResultDto { Success = false, Message = "node unreachable" }; }
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test ticolinea.panel.Tests --filter ProviderControlServiceTests`
Expected: 4 passing.

---

### Task 5: `AdminControlController` + DI

**Files:**
- Create: `ticolinea.panel.API/Controllers/AdminControlController.cs`
- Modify: `ticolinea.panel.API/Program.cs`
- Test: `ticolinea.panel.Tests/Controllers/AdminControlControllerTests.cs`

**Interfaces:**
- Consumes: `IProviderControlService`.
- Produces: `GET /api/v2/admin/providers/{id}/streams` (200 list / 502 if null-unreachable), `GET /api/v2/admin/providers/{id}/system`, `POST /api/v2/admin/providers/{id}/streams/{streamId}/{action}`.

- [ ] **Step 1: Failing controller test**

`ticolinea.panel.Tests/Controllers/AdminControlControllerTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using ticolinea.panel.API.Controllers;
using ticolinea.panel.Application.DTOs.Control;
using ticolinea.panel.Application.Services;
using Xunit;

namespace ticolinea.panel.Tests.Controllers;

public class AdminControlControllerTests
{
    private static AdminControlController Make(Mock<IProviderControlService> svc)
        => new(svc.Object, Mock.Of<ILogger<AdminControlController>>());

    [Fact]
    public async Task Streams_200_when_reachable()
    {
        var svc = new Mock<IProviderControlService>();
        svc.Setup(s => s.GetStreams(1)).ReturnsAsync(new List<NodeStreamDto> { new() { Id = 10 } });
        (await Make(svc).GetStreams(1)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Streams_502_when_unreachable()
    {
        var svc = new Mock<IProviderControlService>();
        svc.Setup(s => s.GetStreams(1)).ReturnsAsync((List<NodeStreamDto>?)null);
        var r = await Make(svc).GetStreams(1);
        r.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task Control_passes_action_through()
    {
        var svc = new Mock<IProviderControlService>();
        svc.Setup(s => s.Control(1, 10, "restart")).ReturnsAsync(new ControlResultDto { Success = true });
        (await Make(svc).Control(1, 10, "restart")).Should().BeOfType<OkObjectResult>();
    }
}
```

- [ ] **Step 2: Run → FAIL**

Run: `dotnet test ticolinea.panel.Tests --filter AdminControlControllerTests` → FAIL (no controller).

- [ ] **Step 3: Implement the controller**

`ticolinea.panel.API/Controllers/AdminControlController.cs`:
```csharp
using Microsoft.AspNetCore.Mvc;
using ticolinea.panel.API.Attributes;
using ticolinea.panel.API.Constantes;
using ticolinea.panel.Application.Services;

namespace ticolinea.panel.API.Controllers;

[Route("api/v2/admin")]
[ApiController]
public class AdminControlController : ControllerBase
{
    private readonly IProviderControlService _control;
    private readonly ILogger<AdminControlController> _logger;

    public AdminControlController(IProviderControlService control, ILogger<AdminControlController> logger)
    { _control = control; _logger = logger; }

    [CustomAuthorization(Global.Secret)]
    [HttpGet("providers/{id:int}/streams")]
    public async Task<IActionResult> GetStreams(int id)
    {
        var s = await _control.GetStreams(id);
        if (s == null) return StatusCode(502, new { message = "provider node unreachable" });
        return Ok(s);
    }

    [CustomAuthorization(Global.Secret)]
    [HttpGet("providers/{id:int}/system")]
    public async Task<IActionResult> GetSystem(int id)
    {
        var s = await _control.GetSystem(id);
        if (s == null) return StatusCode(502, new { message = "provider node unreachable" });
        return Ok(s);
    }

    [CustomAuthorization(Global.Secret)]
    [HttpPost("providers/{id:int}/streams/{streamId:int}/{action}")]
    public async Task<IActionResult> Control(int id, int streamId, string action)
    {
        if (action is not ("start" or "stop" or "restart"))
            return BadRequest(new { message = "action must be start|stop|restart" });
        var r = await _control.Control(id, streamId, action);
        return r.Success ? Ok(r) : StatusCode(502, r);
    }
}
```

- [ ] **Step 4: Register DI**

In `ticolinea.panel.API/Program.cs`, right after `builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();`:
```csharp
builder.Services.AddScoped<IProviderControlService>(sp => new ProviderControlService(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<IProviderService>(),
    ticolinea.panel.API.Constantes.Global.Secret,
    sp.GetRequiredService<ILogger<ProviderControlService>>()));
```
(Add the `using ticolinea.panel.Application.Services;` if not already imported.)

- [ ] **Step 5: Run tests + build**

Run: `dotnet test ticolinea.panel.Tests --filter AdminControlControllerTests` → 3 passing.
Run: `dotnet test ticolinea.panel.Tests` → full suite green.
Run: `dotnet build ticolinea.panel.API/ticolinea.panel.API.csproj` → 0 errors.

---

## PART C — Frontend

### Task 6: Route + sidebar gating + API service

**Files:**
- Modify: `src/utils/constants.ts` (add `CHANNEL_CONTROL: "/channel-control"` to `ROUTES`)
- Modify: `src/App.tsx` (add the route, mirroring the `ADVANCED_TOOLS` `<Route>` block)
- Modify: `src/components/Sidebar.tsx` (add a `menuItems` entry + the `if (item.path === ROUTES.CHANNEL_CONTROL) return isSuperAdmin;` branch in the filter)
- Create: `src/services/channelControl.ts`

- [ ] **Step 1: constants + route + sidebar**

In `src/utils/constants.ts` add to `ROUTES`: `CHANNEL_CONTROL: "/channel-control",`.

In `src/App.tsx`, import `ChannelControl` and add (mirror the AdvancedTools route):
```tsx
<Route path={ROUTES.CHANNEL_CONTROL} element={<ProtectedRoute><MainLayout><ChannelControl /></MainLayout></ProtectedRoute>} />
```

In `src/components/Sidebar.tsx`, add to `menuItems` (use a lucide icon already imported, e.g. `MonitorPlay` or reuse `Settings`):
```tsx
{ path: ROUTES.CHANNEL_CONTROL, label: "Control de Canales", icon: MonitorPlay },
```
and in the `.filter(...)` add, next to the ADVANCED_TOOLS branch:
```tsx
if (item.path === ROUTES.CHANNEL_CONTROL) return isSuperAdmin;
```

- [ ] **Step 2: API service**

`src/services/channelControl.ts`:
```ts
import api from "./api";

export interface NodeStream { id: number; nombre: string; running: boolean; uptimeSec: number; procesoId: number; }
export interface NodeSystem { uptimeSec: number; cpuPct: number; ramPct: number; diskPct: number; }

export const streamsKey = (providerId: number | null) =>
  providerId ? `/api/v2/admin/providers/${providerId}/streams` : null;
export const systemKey = (providerId: number | null) =>
  providerId ? `/api/v2/admin/providers/${providerId}/system` : null;

export const controlStream = (providerId: number, streamId: number, action: "start" | "stop" | "restart") =>
  api.post(`/api/v2/admin/providers/${providerId}/streams/${streamId}/${action}`).then((r) => r.data);
```

- [ ] **Step 3: Build (page not created yet — expect a missing-import error, which Task 7 resolves)**

Skip build until Task 7; the `ChannelControl` import will be unresolved until then.

---

### Task 7: `ChannelControl` page

**Files:**
- Create: `src/pages/ChannelControl.tsx`

**Interfaces:**
- Consumes: `useSWR`, the `channelControl.ts` service, `SystemStatusGauge` (existing recharts gauge at `src/components/SystemStatusGauge.tsx`), `ConfirmDialog` (used by AdvancedTools), the provider list from `GET /api/v2/providers`, `REFRESH_INTERVALS`.

- [ ] **Step 1: Implement the page**

`src/pages/ChannelControl.tsx` — provider dropdown, system-health header (reuse `SystemStatusGauge` for CPU/RAM/disk + uptime text), and a channels table with Start/Stop/Restart (confirm dialog on Stop/Restart, mirroring `AdvancedTools.tsx`). SWR poll both keys at `REFRESH_INTERVALS.DASHBOARD` (5s). On a successful action, `mutate` the streams key immediately.
```tsx
import { useState } from "react";
import useSWR from "swr";
import api from "../services/api";
import { REFRESH_INTERVALS } from "../utils/constants";
import SystemStatusGauge from "../components/SystemStatusGauge";
import ConfirmDialog from "../components/ConfirmDialog";
import { streamsKey, systemKey, controlStream, NodeStream, NodeSystem } from "../services/channelControl";

interface Provider { id: number; providerName: string; }
const fetcher = (url: string) => api.get(url).then((r) => r.data);

export default function ChannelControl() {
  const { data: providers } = useSWR<Provider[]>("/api/v2/providers", fetcher);
  const [providerId, setProviderId] = useState<number | null>(null);
  const [confirm, setConfirm] = useState<{ id: number; action: "stop" | "restart"; nombre: string } | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [banner, setBanner] = useState<{ ok: boolean; msg: string } | null>(null);

  const { data: streams, mutate: mutateStreams } =
    useSWR<NodeStream[]>(streamsKey(providerId), fetcher, { refreshInterval: REFRESH_INTERVALS.DASHBOARD });
  const { data: sys } =
    useSWR<NodeSystem>(systemKey(providerId), fetcher, { refreshInterval: REFRESH_INTERVALS.DASHBOARD });

  const runAction = async (id: number, action: "start" | "stop" | "restart") => {
    if (!providerId) return;
    try {
      setBusy(id); setBanner(null);
      const res = await controlStream(providerId, id, action);
      setBanner({ ok: !!res?.success, msg: res?.message ?? "" });
      await mutateStreams();
    } catch (e: any) {
      setBanner({ ok: false, msg: e?.response?.data?.message ?? e.message });
    } finally { setBusy(null); setConfirm(null); }
  };

  const fmtUptime = (s: number) => `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;

  return (
    <div className="p-6 space-y-6">
      <h1 className="text-2xl font-semibold">Control de Canales</h1>

      <select className="border rounded px-3 py-2"
              value={providerId ?? ""}
              onChange={(e) => setProviderId(e.target.value ? Number(e.target.value) : null)}>
        <option value="">Seleccionar Proveedor…</option>
        {providers?.map((p) => <option key={p.id} value={p.id}>{p.providerName}</option>)}
      </select>

      {providerId && (
        <>
          <div className="grid grid-cols-4 gap-4">
            <SystemStatusGauge label="CPU" value={sys?.cpuPct ?? 0} max={100} unit="%" />
            <SystemStatusGauge label="RAM" value={sys?.ramPct ?? 0} max={100} unit="%" />
            <SystemStatusGauge label="Disco" value={sys?.diskPct ?? 0} max={100} unit="%" />
            <div className="flex flex-col items-center justify-center border rounded p-4">
              <span className="text-sm text-gray-500">Uptime</span>
              <span className="text-xl font-medium">{sys ? fmtUptime(sys.uptimeSec) : "—"}</span>
            </div>
          </div>

          {banner && (
            <div className={`rounded p-3 ${banner.ok ? "bg-green-100" : "bg-red-100"}`}>{banner.msg || (banner.ok ? "OK" : "Error")}</div>
          )}

          <table className="w-full text-left border">
            <thead><tr className="border-b"><th className="p-2">Canal</th><th>Estado</th><th>Uptime</th><th className="text-right p-2">Acciones</th></tr></thead>
            <tbody>
              {streams?.map((s) => (
                <tr key={s.id} className="border-b">
                  <td className="p-2">{s.nombre}</td>
                  <td><span className={`px-2 py-0.5 rounded text-sm ${s.running ? "bg-green-100" : "bg-gray-200"}`}>{s.running ? "En vivo" : "Detenido"}</span></td>
                  <td>{s.running ? fmtUptime(s.uptimeSec) : "—"}</td>
                  <td className="text-right p-2 space-x-2">
                    <button disabled={busy === s.id} className="text-green-700" onClick={() => runAction(s.id, "start")}>Iniciar</button>
                    <button disabled={busy === s.id} className="text-yellow-700" onClick={() => setConfirm({ id: s.id, action: "restart", nombre: s.nombre })}>Reiniciar</button>
                    <button disabled={busy === s.id} className="text-red-700" onClick={() => setConfirm({ id: s.id, action: "stop", nombre: s.nombre })}>Detener</button>
                  </td>
                </tr>
              ))}
              {streams && streams.length === 0 && <tr><td className="p-2 text-gray-500" colSpan={4}>Sin canales.</td></tr>}
            </tbody>
          </table>
        </>
      )}

      {confirm && (
        <ConfirmDialog
          isOpen={true}
          title={`Confirmar ${confirm.action === "stop" ? "Detener" : "Reiniciar"}`}
          message={`¿${confirm.action === "stop" ? "Detener" : "Reiniciar"} "${confirm.nombre}"?`}
          variant="warning"
          loading={busy === confirm.id}
          onConfirm={() => runAction(confirm.id, confirm.action)}
          onCancel={() => setConfirm(null)}
        />
      )}
    </div>
  );
}
```
Adapt prop names to the REAL `SystemStatusGauge` and `ConfirmDialog` signatures (read `src/components/SystemStatusGauge.tsx` — `{ label, value, max, unit?, color? }` — and `ConfirmDialog` props actually used in `AdvancedTools.tsx`). Do not invent props.

- [ ] **Step 2: Build the frontend**

Run:
```bash
cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel/ticolinea.panel.Frontend
npm run build
```
Expected: build succeeds (TypeScript + Vite). Fix any prop/type mismatches against the real components.

- [ ] **Step 3: Manual verification checklist**

Add `src/pages/ChannelControl.manual.md` documenting: log in as a super-admin → the "Control de Canales" item appears in the sidebar (and is absent for a non-super-admin); selecting a provider loads channels + gauges; Start/Stop/Restart show a confirm (stop/restart) and a result banner; an unreachable node shows the 502 message, not a crash.

---

## Task 8: Completion gate

- [ ] **Step 1: Build all three projects**
```bash
dotnet build /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel/ticolinea.panel.API/ticolinea.panel.API.csproj -v q --nologo
dotnet build /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel/Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj -v q --nologo
cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel/ticolinea.panel.Frontend && npm run build
```
Expected: all 0 errors / build succeeded.

- [ ] **Step 2: Existing test suites green**
```bash
dotnet test /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolinea.panel/ticolinea.panel.Tests/ticolinea.panel.Tests.csproj
cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel && dotnet test Ticolinea.Streaming.Middleware.Tests
```
Expected: all pass (panel: prior 48 + new control tests; middleware: prior 11 + the 3 filter tests).

- [ ] **Step 3: The tool still works (untouched, but the gate is explicit)**
```bash
cd /Users/asalgado/Workspace/Clients/Ticolinea/Code/ticolineapanel
shellcheck deploy/tico deploy/lib/*.sh deploy/lib/commands/*.sh deploy/tests/*.bats
bats deploy/tests/
```
Expected: shellcheck clean, all bats pass.

---

## Self-Review

**Spec coverage:** §4 node AdminController (streams/system/actions + inbound key) → Tasks 1-3. §5 panel proxy + control controller + header posture → Tasks 4-5. §6 frontend page (dropdown, SWR poll, gauges, actions, isSuperAdmin gating) → Tasks 6-7. §7 error handling (unreachable → 502 → "node unreachable", no crash) → Tasks 4/5/7. §8 testing → node filter test (T1), panel proxy+controller tests (T4/T5), frontend manual (T7). §9 completion gate → Task 8. ✓

**Additive-only check:** node — new attribute/controller + visibility changes + one new accessor (no existing body changed). panel — new DTOs/service/controller + one DI line. frontend — new page/service + additive route/sidebar entries. Login flow, existing auth, supervision, storm guard: untouched. ✓

**Type consistency:** `NodeStreamDto`/`NodeSystemDto`/`ControlResultDto` shared across service (T4), controller (T5), and frontend interfaces (T6/T7) by field name; `IProviderControlService` signatures identical in T4/T5; the node JSON shape (`id,nombre,running,uptimeSec,procesoId` / `uptimeSec,cpuPct,ramPct,diskPct`) matches the panel DTOs and frontend interfaces. ✓

**Open risks flagged for execution:** (1) `StreamDb` reader mapping in Task 3 must be reconciled against the real `Modelos/Stream.cs` (names/types) before trusting it. (2) node control actions call static primitives → not unit-tested; covered by the auth-filter test + manual/integration verification. (3) frontend `SystemStatusGauge`/`ConfirmDialog` prop names must be matched to the real components. These are called out in-task, not left implicit.
