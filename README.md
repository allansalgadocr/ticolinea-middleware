# Ticolinea Streaming Middleware

The Ticolinea **streaming node**: a .NET service that runs FFmpeg against Ticolinea's
already-transcoded HLS, copies the segments to local disk, and re-serves them to a
client's own devices (Android TVs, apps). One origin pull per channel instead of one
per viewer — the bandwidth cost lives on the node, not on Ticolinea's origin.

It runs in two roles from the **same codebase**:

- **Main node** (`tv.play-latino.com`) — the primary Ticolinea server, hand-configured.
- **Provider nodes** — a client's own Ubuntu box, provisioned and updated by the
  [`deploy/`](deploy/README.md) tool. Each serves that client's devices from its own copy.

```
Ticolinea origin ──(pull, outbound)──▶  Streaming node  ──(HLS, public)──▶  client's TVs
 (transcoded HLS)                        nginx + FFmpeg + MariaDB
```

The node never re-transcodes — FFmpeg runs `-c copy`. It re-streams and edge-caches.

---

## Architecture at a glance

| Piece | Detail |
|---|---|
| **Runtime** | .NET 6 (`net6.0`), `ticolinea.stream.service.csproj` |
| **Process** | Binds `127.0.0.1:1234` — **never** exposed directly; nginx is the only public listener |
| **Database** | MariaDB/MySQL on `127.0.0.1` only (raw MySqlConnector) |
| **Media** | FFmpeg / FFprobe via CliWrap, one FFmpeg process per live channel |
| **Jobs** | Hangfire (supervision loop, package sync) |
| **Logging** | log4net (`log4net.config`) |

### Ports

| Port | Exposure | Purpose |
|---|---|---|
| `27701/tcp` | public (via nginx) | Playlists / HLS API for end-user devices |
| `27703/tcp` | public (via nginx) | Static `.ts` segments — the actual video bytes |
| `1234` | loopback | The node process (nginx proxies to it) |
| `3306` | loopback | MariaDB — never public |
| `27702` | outbound | Panel API — token validation, activity, catalog sync |

---

## Key capabilities

- **Live restream** — `LiveController` / `StreamsController` / `EnVivoController` build the
  `.m3u8` playlists and rewrite segment URLs; VOD via `PeliculasController` / `SeriesController`;
  EPG, devices, health, and panel integration each have their own controller.
- **FFmpeg supervision** — a Hangfire loop keeps one FFmpeg process per enabled channel alive,
  with a storm guard / circuit breaker. `ForzarInicioInmediato` is the operator-restart primitive
  (bypasses the storm guard cleanly); `DetenerProceso` is the real stop.
- **Package catalog sync** — on boot and every `PackageSync:IntervalHours` (default 6h), the node
  pulls its assigned catalog from the panel and reconciles its local `streams_*` tables. Only the
  provider's owner (the panel) drives `habilitado`; `iniciado` (the pause flag) stays node-owned.
  An undersized-catalog guard refuses to act on a suspiciously small pull, so a panel hiccup never
  blacks out channels. See `Services/PackageSyncService.cs`, `Helpers/PackageSyncPlan.cs`.
- **Admin channel control** — `AdminController` (`/api/admin`, gated by an inbound `X-Auth-API-Key`
  filter) lets the panel list channels, start/stop/restart them, and read host CPU/RAM/disk/uptime.
  It wraps the existing supervision primitives — no new stream logic. See
  `Controllers/AdminController.cs`, `Attributes/NodeApiKeyAttribute.cs`.

---

## Configuration

Config lives in `appsettings.json` (base) plus environment-specific overlays
(`appsettings.main.json`, `appsettings.<provider>.json`). Provider deploys get their **own**
appsettings written by the deploy tool — they never receive the main node's file (the CI/deploy
pipeline strips `appsettings.main.json` and other non-provider configs from the artifact).

| Section | What it holds |
|---|---|
| `Database:ConnectionString` | Local MariaDB DSN (loopback) |
| `Streaming` | `ProviderId` / `ProviderName`, on-disk folders, FFmpeg paths, and the two base URLs below |
| `Jwt` | Panel `Issuer`/`Audience`, RSA `PublicKey` (token validation), `PanelApiUrl`, `PanelApiKey` (shared node↔panel key), cache/expiry |
| `PackageSync:IntervalHours` | Catalog sync cadence (default `6`) |

### ⚠️ The two base URLs are named backwards

A well-known gotcha — the field names are the opposite of what they do:

| Field | Actually serves | Port on a node |
|---|---|---|
| `SegmentBaseUrl` | the **playlist / API** URLs (`.m3u8`, `/Live/...`) | `27701` |
| `StreamsBaseUrl` | the **`.ts` segment** files | `27703` |

So: **playlists → `SegmentBaseUrl` (27701); segments → `StreamsBaseUrl` (27703).** The deploy tool
derives both from the node's public host at those fixed ports.

> **Secrets:** `appsettings.json` in this repo carries a JWT public key and the shared `PanelApiKey`
> for the historical main-node setup. Treat real credentials as environment-specific; provider nodes
> receive their own values at provisioning time.

---

## Build & run (local)

```bash
# build
dotnet build Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj

# run (expects a reachable local MariaDB + a valid appsettings)
dotnet run --project Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj

# publish a framework-dependent artifact
dotnet publish Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj -c Release -o publish
```

### Tests

```bash
dotnet test Ticolinea.Streaming.Middleware.Tests
```

Covers the catalog client, the package-sync plan, the `X-Auth-API-Key` filter, and a smoke test.
The provisioning tool has its own suite (`bats deploy/tests/` + shellcheck).

---

## Deployment

### Provider node (recommended path)

The [`deploy/`](deploy/README.md) directory holds the `tico` provisioning tool — bootstrap a clean
Ubuntu 22.04 host, deploy a release over SSH, roll back, and check status. Start with
[`deploy/README.md`](deploy/README.md) (onboarding + requirements) and
[`deploy/RUNBOOK.md`](deploy/RUNBOOK.md) (step-by-step commands).

```bash
cp deploy/providers/example.conf deploy/providers/<slug>.conf   # fill in host, slug, URLs
./deploy/tico probe     <slug>   # read-only server report
./deploy/tico bootstrap <slug>   # provision the host (idempotent)
./deploy/tico deploy    <slug> --tag <version> --artifact <dir>
./deploy/tico status    <slug>
```

### CI — the node artifact

`.github/workflows/build-node.yml` builds the release artifact on every push to `main`/`master`:
it publishes the node, strips non-provider configs, and generates an **idempotent `schema.sql`**
from the panel's EF migrations (the panel is the single source of truth for the node's DB schema).

That schema step checks out the private `ticolinea.panel` repo, so the workflow needs a
**`PANEL_REPO_TOKEN`** secret — a token with read access to `allansalgadocr/ticolinea.panel`.
The built-in `GITHUB_TOKEN` can't reach a second private repo.

---

## Related docs

- [`deploy/README.md`](deploy/README.md) — provider onboarding & bare-metal requirements
- [`deploy/RUNBOOK.md`](deploy/RUNBOOK.md) — provisioning/deploy commands
- [`Ticolinea.Streaming.Middleware/AUTHENTICATION.md`](Ticolinea.Streaming.Middleware/AUTHENTICATION.md) — JWT validation & token refresh
- [`Ticolinea.Streaming.Middleware/DEPLOYMENT.md`](Ticolinea.Streaming.Middleware/DEPLOYMENT.md) — multi-provider config model
- [`Ticolinea.Streaming.Middleware/KEY_PAIR_CONFIGURATION.md`](Ticolinea.Streaming.Middleware/KEY_PAIR_CONFIGURATION.md) — RSA key pair setup
- `docs/superpowers/specs/` — design specs (package sync, admin channel control)
