# Package Sync (Spec B) — Design

**Date:** 2026-07-15
**Status:** Approved (design); implementation plan pending
**Depends on:** the provider-node provisioning tool (2026-07-14-provider-node-provisioning-design.md). A provisioned node is healthy but serves no channels until this lands.
**Spans two repos:** `ticolinea.panel` (new endpoint + EF migrations) and `Ticolinea.Streaming.Middleware` (the sync job).

---

## 1. Problem

A freshly-provisioned provider node runs, answers `/api/health`, and serves nothing — its local `streams_tl` has no channel rows. Ticolinea assigns each provider a **package** (a set of channels) in the panel, but there is no path for that assignment to reach the node's database. Package sync closes that gap: the node pulls its assigned catalog from the panel and populates its local tables so its own FFmpeg supervision starts the channels and its Android TV clients can watch.

## 2. Model (as defined by the product owner)

**The panel owns the catalog. The node is a dumb consumer.** The panel exposes, per provider, the channels in that provider's assigned package — each as a full stream row (name, logo, `fuente_stream` URL, and playback params). The node pulls this catalog **every 6 hours (plus once on boot)**, diffs it against its local state, and reconciles: insert new channels, update changed ones, disable ones no longer in the package.

The node does **not** interpret, rewrite, or validate the `fuente_stream` URL — whatever the panel puts there is what FFmpeg receives as `-i`. How that URL is produced and what it points at (transcoded HLS origin, etc.) is entirely the panel/catalog side's concern and is out of scope for this spec.

## 3. Goals / Non-goals

**Goals**
- One panel endpoint that returns a provider's full live-channel catalog, gated by the existing shared API key.
- A node sync job that makes the local DB match the catalog every 6h and on boot, keeping the panel's stream `id` so EPG and channel ordering match the origin exactly.
- A panel outage never blacks out a running provider.
- A bad/empty catalog response can never disable every channel.
- The node schema actually contains the tables the sync writes (`streams_info` gap, §7).

**Non-goals (deferred)**
- **VOD** (movies/series, `tipo` ≠ 1). v1 syncs live channels only.
- **EPG program-data refresh cadence.** `canal_epg` linkage is carried per channel (so EPG *works* if EPG data is present), but syncing the EPG XML itself is a separate concern.
- Panel→node **push**. This is pull-only, by prior decision — outbound-only from the node, self-healing, no inbound path into the client's network.
- Real-time propagation. 6h (plus boot) is the agreed cadence; a package change can take up to 6h to reach a node.

## 4. Architecture

```
Panel  (ticolinea.panel.API)                     Provider node  (Ticolinea.Streaming.Middleware)
─────────────────────────────                    ─────────────────────────────────────────────
GET /api/v2/providers/{slug}/catalog             PackageSyncJob (Hangfire recurring, 6h + on boot)
  header X-Auth-API-Key: <PanelApiKey>   ◄──────    1. GET catalog (named "PanelApi" HttpClient + key)
  → resolve provider's package                      2. guard: reachable? sane size?
  → join paquete_tv_streams ⋈ streams_tl            3. upsert streams_tl (by panel id, sincronizado=1, habilitado=1)
  → return full live rows (tipo=1)                  4. ensure streams_info row exists (iniciado defaults 1)  ← §7
                                                    5. reconcile: habilitado=0 on managed rows not in catalog
                                                    (all steps in one transaction)
```

Auth needs nothing new: the middleware's `Jwt.PanelApiKey` (`appsettings.json`) is byte-identical to the panel's `Constantes/Global.cs` `Global.Secret`, and `[CustomAuthorization(Global.Secret)]` already protects `ProvidersController`/`AuthV2Controller`. The new endpoint is gated the same way and is reachable with the node's existing credential.

## 5. Component 1 — Panel catalog endpoint

**Route:** `GET /api/v2/providers/{slug}/catalog`, `[CustomAuthorization(Global.Secret)]`.
`{slug}` is the normalized provider name / `NodeProviderId` the node already knows (`PROVIDER` env).

**Package resolution** reuses the existing priority logic (do not reimplement it):
- Provider `is_external = true` → **all** enabled live streams (no package filter).
- Otherwise → the provider's `default_paquete_tv_id`; join `paquete_tv_streams` to select that package's streams.

This mirrors `Helpers/PackageResolver.cs` / `AuthTokenService`'s resolution so a node's catalog matches what its clients would be entitled to.

**Filter:** `habilitado = 1 AND es_bajodemanda = 0 AND tipo = 1` (live only, v1).

**Response:** a JSON array of the full stream row per channel — reuse/extend the existing `StreamDTO` (`Application/DTOs/StreamDTO.cs`, already carries `FuenteStream`, `CanalId`, `CanalEpg`). Fields:
`id, nombre_stream, imagen_stream, fuente_stream, canal_id, canal_epg, tipo, intervalo, segmentos, transcode, transcode_audio, bitrate, resolucion, probesize_ondemand, es_bajodemanda, framerate, cgop, gop, orden`.

One call returns the whole package. Errors: unknown slug → 404; unauthorized → 401 (same as sibling controllers).

## 6. Component 2 — Node sync job

A new service (e.g. `Services/PackageSyncService.cs`) plus a Hangfire recurring registration in `Jobs.cs`/`Program.cs`:
- **Schedule:** every 6h (interval read from `appsettings` `PackageSync:IntervalHours`, default 6) **and** a one-shot run at startup so a fresh node populates immediately.
- **Fetch:** the named `"PanelApi"` `HttpClient` (already registered) with `X-Auth-API-Key`. URL from `PanelApiUrl` + `/providers/{PROVIDER}/catalog`.

Per cycle, in a **single transaction**:
1. **Upsert `streams_tl`** keyed on the panel `id` (`INSERT … ON DUPLICATE KEY UPDATE`), setting `sincronizado = 1` and `habilitado = 1`. Keeping the panel id means EPG (`canal_epg`) and ordering (`canal_id`) stay identical to origin.
2. **Ensure a `streams_info` row exists** for each channel — `INSERT IGNORE INTO streams_info (stream_id, iniciado) VALUES (?, 1)` (or equivalent insert-if-absent). **The sync never writes `iniciado` after creation.** `iniciado` is a node-local operational flag (defaults to `1` so a freshly-synced channel runs); it is deliberately **not** sync-managed, so an operator who pauses a channel (`iniciado = 0`) is not silently un-paused by the next 6h cycle. The row must merely *exist*, because the active-streams query is `streams_tl INNER JOIN streams_info ON habilitado = 1 AND iniciado = 1` (`Jobs.cs`, `BatchDatabaseHelper.cs`) — no row means the stream never runs.
3. **Reconcile removals:** for every row with `sincronizado = 1` whose `id` is not in the current catalog, set `habilitado = 0` **only**. Leave `streams_info.iniciado` untouched — `habilitado = 0` alone stops the stream (the query needs both flags). The row is kept (product decision); if the channel returns, step 1 sets `habilitado = 1` and it runs again. Rows with `sincronizado = 0` (added locally, out of band) are **never touched**.

**Package membership is expressed solely through `habilitado`** (sync-owned). `iniciado` stays node-owned. This clean split is what lets the sync be safely idempotent without ever fighting an operator's runtime decisions.

**The `sincronizado` marker** distinguishes panel-managed rows from anything created locally on the node, so reconcile can safely mass-disable without clobbering local additions. Added as a `BOOLEAN NOT NULL DEFAULT 0` column on `streams_tl` via an EF migration (§7), so it ships through the same `schema.sql` pipeline provisioning already uses.

## 7. Schema changes (EF migrations on the panel model)

Two migrations, because the node's schema is generated from the panel's EF migrations (`dotnet ef migrations script --idempotent`, per the provisioning spec):

1. **Add `sincronizado`** (`BOOLEAN NOT NULL DEFAULT 0`) to `streams_tl` / the `Stream` entity. Unused on the main panel DB (harmless, consistent with "panel-only tables sit empty on nodes"); load-bearing on provider nodes.

2. **Create the `streams_info` table via a migration.** **This is a real gap, with a subtlety:** the `StreamInfo` entity and its `streams_info` mapping already exist in the panel model (`StreamInfoConfiguration`, `DbContext.StreamInfos`, the model snapshot) — so `dotnet ef migrations add` generates an *empty* migration for it (EF thinks the table exists). But **no migration ever `CreateTable`s it** — existing nodes got the table database-first. A freshly-provisioned node, whose schema comes only from `dotnet ef migrations script`, therefore **lacks `streams_info` entirely**, and the sync's step 2 would fail. The fix is a **hand-written migration** running raw `CREATE TABLE IF NOT EXISTS streams_info (...)`: it creates the table on fresh nodes and is a safe no-op on existing hand-built nodes and the main panel DB.

## 8. Failure handling — protects the running provider

- **Panel unreachable, timeout, or non-2xx → do nothing and keep serving.** Log a warning, skip the cycle, retry next interval. A panel outage must never disable or wipe a provider's channels. This is the single most important safety property.
- **Undersized-catalog guard:** if the returned catalog has far fewer channels than currently managed (`count < 0.5 × current sincronizado=1 count`, threshold configurable), treat the response as suspect: **apply upserts but skip the reconcile-disable step**, and log loudly. One bad/empty response can never black out every channel. (An empty catalog for a genuinely emptied package is rare and recoverable by an operator; silently disabling everything is not an acceptable automatic action.)
- **Transactional:** the whole cycle commits or rolls back atomically; a mid-sync error leaves the last-good state intact.
- **Concurrency:** guard against overlapping runs (a slow sync + the 6h tick) with a simple run-lock so two cycles can't interleave.

## 9. Data flow example

1. Panel: provider `acme` (`is_external = false`, `default_paquete_tv_id = P1`); P1 has channels 10, 11, 12.
2. Node boots → sync runs → `GET /providers/acme/catalog` → `[{id:10,…},{id:11,…},{id:12,…}]`.
3. Node upserts `streams_tl` 10/11/12 (`sincronizado=1, habilitado=1`) and inserts `streams_info` rows (`iniciado=1` by default). Supervision starts FFmpeg per channel; `/api/health` still 200; channels now watchable.
4. 6h later P1 dropped channel 12, added 13 → catalog `[10,11,13]`. Node upserts 10/11/13, and reconcile sets channel 12 `habilitado=0` (row kept, its `streams_info.iniciado` left as-is).
5. Panel down at the next tick → node logs, changes nothing, keeps serving 10/11/13.

## 10. Testing

**Panel (xUnit in the panel test project):**
- Package resolution: external → all live; non-external → `default_paquete_tv_id` set; unknown slug → 404; missing key → 401.
- DTO shape carries every field in §5, live-only filter applied.

**Node (`Ticolinea.Streaming.Middleware.Tests`, xUnit):**
- Upsert idempotency: same catalog twice → no row churn, `sincronizado=1`.
- A `streams_info` row is created (`iniciado=1`) for a newly-synced channel that had none.
- **`iniciado` is never overwritten:** operator sets a synced channel's `iniciado=0`; a subsequent sync leaves it `0` (does not un-pause).
- Reconcile sets `habilitado=0` on exactly the dropped channels, leaves their `streams_info.iniciado` untouched, and leaves `sincronizado=0` (local) rows entirely alone.
- **Panel-down keeps stale data:** fetch throws / non-2xx → zero DB mutations.
- **Undersized-catalog guard:** catalog with < 50% of managed count → upserts apply, no `habilitado=0` disables.
- Transaction rollback on mid-sync failure.

## 11. Prerequisites & risks

- **`streams_info` migration correctness (§7.2)** is the highest-risk item: it must create the table on fresh nodes yet be a safe no-op on the main panel DB and on existing hand-built nodes. Validate against a real node dump before shipping.
- **Provider-slug ↔ package wiring** must exist in the panel (the provider record has `default_paquete_tv_id` set, or `is_external=true`). A provider with neither gets the "all streams" fallback — confirm that's intended per provider at onboarding.
- Adds a recurring outbound dependency from node to panel; the §8 guards keep it non-fatal.
- Scope creep magnet: VOD and EPG-data sync will be requested. Hold them to their own specs.

## 12. Open questions

None blocking. Deferred by design: VOD catalog, EPG XML refresh cadence, and any move to push/near-real-time propagation.
