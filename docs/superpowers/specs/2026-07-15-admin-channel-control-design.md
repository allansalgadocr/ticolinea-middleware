# Admin Channel Control UI (Spec C) — Design

**Date:** 2026-07-15
**Status:** Approved (design); implementation plan pending
**Spans:** `Ticolinea.Streaming.Middleware` (node — new AdminController), `ticolinea.panel.API`/`.Application` (proxy + control endpoints), `ticolinea.panel.Frontend` (admin page).

**Guiding constraints (from the product owner):**
- **Do not change any existing behavior.** Every change here is additive — new controllers, services, a new page, and a couple of visibility changes on already-existing private methods. No existing endpoint, auth flow, login response, supervision loop, or storm guard is modified.
- **Follow the panel's existing header-based auth model** — no new token/RBAC infrastructure.
- **Completion gate:** all three projects build, and the `./deploy/tico` provisioning tool still passes its checks.

---

## 1. Problem

Operators have no per-channel visibility or control on a provider node. `./deploy/tico status` gives a bare count over the CLI; there is no way to see which channels are running, restart a stuck one, pause one, or read the node's live CPU/RAM/disk. This spec adds a small **admin-only page in the main panel**: pick a provider, see its channels with near-real-time state, start/stop/restart them, and view server health.

## 2. What already exists (this is mostly wiring)

The node already has the hard parts; they just aren't exposed to the panel:
- **`StreamingService.ForzarInicioInmediato(stream)`** — forced restart that already resets the failure tracker + `_lastProcessStart`, i.e. cleanly bypasses the 12s storm guard and circuit breaker. This is the operator-restart primitive; no new bypass logic is needed.
- **`Jobs.DetenerProceso(procesoId, streamId)`** — real stop (kills FFmpeg, sets `streams_info.ejecutando=0, proceso_id=-1`).
- **Running state** — `streams_info` (`ejecutando`, `proceso_id`, `iniciado`) plus live `pgrep` verification via `StreamStatusHelper.GetRealTimeStreamStatusAsync`. Per-stream start time lives in the in-memory `_lastProcessStart` dictionary (private static, not yet exposed).
- **Host metrics** — `Jobs.ObtenerUsoCPU()`, `ObtenerUsoRAM()`, `ObtenerUsoDisco()`, `ObtenerUsoDiscoCarpeta()` all exist and compute real host figures; they are `private static` and unexposed.
- **`iniciado` semantics** — the active-streams query requires `habilitado=1 AND iniciado=1`; `iniciado` is the node-owned pause flag (deliberately not sync-managed). Stop → `iniciado=0`; start/restart → `iniciado=1`.

Panel/frontend has the tools too: React 19 + SWR (polling), recharts (gauges), axios; `AdvancedTools.tsx` (restart buttons + confirm dialog + result banner) and `Providers.tsx` (SWR provider list) are direct templates; `Sidebar` already gates `AdvancedTools` on `isSuperAdmin`.

## 3. Architecture

```
Admin browser ──▶ Panel API ─────────▶ Provider node
 admin page       AdminControlController   AdminController (NEW)
 (SWR polling)    + ProviderControlService  inbound X-Auth-API-Key check
                  (per-provider proxy)      wraps existing FFmpeg primitives
```

Near-real-time = **SWR polling** (~4 s `refreshInterval`), reusing the frontend's existing pattern. No SignalR/WebSockets (the node has no hub; polling is the small, sufficient choice).

## 4. Node — new `AdminController` (additive)

`[Route("api/admin")]`, gated by a **new inbound `X-Auth-API-Key` filter** that compares the header to `Constantes.Global.PANEL_API_KEY` (the shared key the node already holds for outbound calls). This is transport auth between panel and node — essential because 27701 is public; without it these control actions would be internet-callable. It is a new, self-contained authorization filter on the node (the node has no reusable inbound-auth today except `PanelController`'s hardcoded plaintext creds, which we deliberately do **not** copy). `PanelController` is left untouched.

Endpoints (all reuse existing logic; none alter supervision or the storm guard):

| Endpoint | Behavior |
|---|---|
| `GET /api/admin/streams` | Returns `[{id, nombre, running, uptimeSec, procesoId}]`. `running`/`procesoId` from `streams_info` + live `StreamStatusHelper`; `uptimeSec` from a **new read-only accessor** exposing `_lastProcessStart[id]` (add a public getter to `StreamingService`; no behavior change). |
| `POST /api/admin/streams/{id}/start` | Set `iniciado=1`, `habilitado=1`; call `ForzarInicioInmediato`. Mirrors what `PanelController.IniciarStream` already does, via the same primitives. |
| `POST /api/admin/streams/{id}/stop` | `DetenerProceso` (kill), set `iniciado=0, ejecutando=0, proceso_id=-1`. |
| `POST /api/admin/streams/{id}/restart` | `DetenerProceso` then `ForzarInicioInmediato` (storm-guard-bypassing); `iniciado` stays 1. |
| `GET /api/admin/system` | `{uptimeSec, cpuPct, ramPct, diskPct, streamsDiskPct}` from the existing `Jobs.ObtenerUso*` methods (change their visibility to `internal`/`public`; no logic change). |

**Only existing-code change on the node:** the visibility of `_lastProcessStart` (via a new accessor) and the four `Jobs.ObtenerUso*` methods. No existing method body changes.

## 5. Panel — proxy + control endpoints (additive)

- **`IProviderControlService` / `ProviderControlService`** (new, Application layer): given a `providerId`, load the `Provider`, take `ConnectionUrl`, and call `{ConnectionUrl}/api/admin/...` with header `X-Auth-API-Key: <Global.Secret>` via `IHttpClientFactory`. Generic per-provider — fills the gap that today's only node calls (`StreamService.ToggleStreamStatus`, `SystemStatusService.GetMiddlewareUptimeAsync`) are hardcoded to a single node. Those existing services are **left untouched**.
- **`AdminControlController`** (new): `[Route("api/v2/admin")]`, `[CustomAuthorization(Global.Secret)]` (same as every other endpoint).
  - `GET /api/v2/admin/providers/{id}/streams` → proxy to node `GET /api/admin/streams`.
  - `POST /api/v2/admin/providers/{id}/streams/{streamId}/{action}` (`action ∈ start|stop|restart`) → proxy.
  - `GET /api/v2/admin/providers/{id}/system` → proxy to node `GET /api/admin/system`.
- **Auth posture (per product-owner decision):** these endpoints follow the panel's **existing header model** — `[CustomAuthorization(Global.Secret)]` plus the client-supplied `X-Is-Super-Admin` header, enforced the same way (and to the same degree) as the rest of the panel. **No admin token, no login change, no new secret.** Enforcement is UI-level, consistent with `AdvancedTools` and all current admin CRUD.
  - **Documented risk (accepted):** `X-Is-Super-Admin` is client-supplied and forgeable, and the shared `X-Auth-API-Key` ships in the frontend bundle — so these control actions are no better protected than the panel's existing CRUD. This is an accepted, app-consistent posture; closing it would require panel-wide server-side RBAC, out of scope here. Recorded so it is a conscious choice, not an inherited surprise.
- **Provider dropdown:** reuse the existing `GET /api/v2/providers`.

## 6. Frontend — admin page (additive)

- New route + page component; add to `Sidebar` gated on `isSuperAdmin` (mirror the `ADVANCED_TOOLS` special-case). `api.ts` already sends `X-Auth-API-Key` + `X-Is-Super-Admin` — no interceptor change needed.
- **Layout:** a "Proveedor / Consumidor" `<select>` (SWR `/api/v2/providers`). On selection:
  - **Server-health header** — SWR-poll `…/system` (~4 s) → recharts gauges for CPU%/RAM%/disk% + uptime text.
  - **Channel table** — SWR-poll `…/streams` (~4 s) → rows `{nombre, running badge, uptime}` with **Start / Stop / Restart** buttons. Confirm dialog on Stop and Restart (channel-affecting), like `AdvancedTools`; a result banner on success/failure. On action success, revalidate the streams SWR key immediately (don't wait for the next poll).
- **Empty/edge states:** provider with no `ConnectionUrl`, node unreachable (proxy returns an error → show "node unreachable", don't crash the page), empty channel list.

## 7. Error handling

- Node unreachable / non-2xx from the proxy → the panel endpoint returns a clear error; the page shows "provider node unreachable," never a blank/crashed view. Read polls degrade gracefully (keep last data, show a stale indicator).
- A control action that fails on the node → surfaced in the result banner with the node's message; no optimistic UI that lies about state (re-poll to confirm).
- The node's inbound filter returns 401 on a missing/wrong key (panel misconfig) — logged.

## 8. Testing

- **Node (xUnit):** the inbound `X-Auth-API-Key` filter (accept correct key, reject missing/wrong); the streams-list mapping and the start/stop/restart action dispatch (mock the FFmpeg primitives — assert stop calls `DetenerProceso` + sets `iniciado=0`, restart calls the force-restart path, start sets `iniciado=1`). Do not exercise real FFmpeg.
- **Panel (xUnit):** `ProviderControlService` builds the right URL from `Provider.ConnectionUrl` and attaches the key (mock `HttpMessageHandler`); node-unreachable → error result, not throw; `AdminControlController` maps proxy results to responses.
- **Frontend:** manual/visual verification against a running node (no test harness exists for the panel frontend); document the check steps.

## 9. Completion gate (explicit, per product owner)

Before this is "done":
1. `dotnet build` succeeds for **`ticolinea.panel.API`** and **`Ticolinea.Streaming.Middleware`**.
2. `npm run build` (or the frontend's build) succeeds for **`ticolinea.panel.Frontend`**.
3. The provisioning tool still works: `shellcheck` clean + `bats deploy/tests/` all pass (this feature touches none of it, but the gate is explicit).
4. Existing test suites still green (panel xUnit, middleware xUnit).

## 10. Non-goals / deferred

- Server-side RBAC / real admin identity verification (accepted app-consistent header posture instead; §5).
- Editing channel *data* (name/logo/source) — that is panel/package-sync-owned; this page is operational control only (start/stop/restart/observe), so it never fights the 6-hourly sync.
- VOD/movies/series control (live channels, `tipo=1`, only).
- Real-time push (SignalR) — polling is the chosen mechanism.
- Multi-provider "all nodes at once" view — one provider at a time via the dropdown.

## 11. Open questions

None blocking. The security posture is a conscious, documented acceptance (§5).
