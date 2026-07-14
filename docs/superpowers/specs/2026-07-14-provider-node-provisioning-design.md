# Provider Node Provisioning, CI/CD, and Safe Updates — Design

**Date:** 2026-07-14
**Status:** Approved (design); implementation plan pending
**Scope:** Subsystems #2 and #3 of the provider-onboarding work. Excludes package sync (spec B) and the stream control plane / Admin UI (spec C).

---

## 1. Problem

Onboarding a new provider is currently a hand-run sequence out of `DEPLOYMENT.md`: install a .NET runtime, install FFmpeg, `useradd`, `mkdir`, `scp -r ./publish`, hand-edit an `appsettings.{provider}.json`, write a systemd unit. It is slow, unrepeatable, and unsafe in three specific ways:

1. **`scp -r ./publish` ships Ticolinea's secrets to a third party.** The repo commits the live RDS password, the JWT *private* key, the password pepper, and the panel API key.
2. **The Hangfire dashboard is unauthenticated.** `DashboardNoAuthorizationFilter.Authorize()` returns `true` unconditionally. The moment 27701 is publicly reachable, so is the dashboard. Swashbuckle's Swagger UI is exposed on the same port.
3. **FFmpeg's version is whatever the client's distro shipped.** HLS muxing behavior differs across FFmpeg majors, so a channel that is fine on one client can misbehave on another, for reasons nobody chose.

Additionally, `net6.0` is end-of-life and `dotnet-runtime-6.0` is increasingly hard to obtain from the Microsoft feed on Ubuntu 22.04.

There is also no defined way to **update** a running client without taking their channels down.

## 2. Context: the restream model

A provider node does not pull raw sources. Ticolinea transcodes the real sources and serves them; the provider node runs FFmpeg against **Ticolinea's already-transcoded HLS** and copies chunks to its own disk. The provider's Android TVs then pull HLS **from the provider's server**.

Bandwidth cost therefore sits with the provider; Ticolinea serves one origin pull per channel rather than one per viewer.

This needs **no new code**: `fuente_stream` is passed straight to FFmpeg as `-i` (`Services/StreamingService.cs:319`). A Ticolinea HLS URL is just another input.

Two consequences that constrain this design:
- The node must have **outbound** reach to the Ticolinea origin node on **`tv.play-latino.com:27701`** (the host `PanelController` already points at; confirm against the actual `fuente_stream` values a provider will be given). This is the content path. It is easy to omit from a firewall request, and nothing works without it.
- On a provider node, **re-encoding is a bug** — the node should be on the copy path. If it is burning CPU on video encode, something forced it off.

## 3. Goals / Non-goals

**Goals**
- One command to probe a client's server; one to bootstrap it; one to deploy; one to roll back.
- Identical FFmpeg on every provider, chosen by Ticolinea.
- No .NET runtime installed on the client's host.
- No Ticolinea secret ever copied to a client's box.
- The Hangfire dashboard and Swagger unreachable from the public internet.
- A client update that, when it succeeds, viewers do not notice — and that rolls itself back when it fails.
- An explicit, minimal firewall request to hand the client.

**Non-goals**
- Migrating the existing `main` and `fibraencasa` nodes. They stay bare-metal/systemd. New providers get containers. Migration is a later, separate decision.
- Package/channel sync into the node's database (**spec B**).
- Operator UI for stream status and control (**spec C**).
- Zero-downtime (blue/green) updates. Rejected for now: it doubles source pull and CPU during the swap, to close a ~30s window that the HLS buffer already absorbs.

## 4. Architecture

### 4.1 Artifact

`dotnet publish -c Release -r linux-x64 --self-contained` produces a binary that carries its own .NET runtime. That single change removes the `net6.0`-EOL runtime-install problem from every target forever.

The artifact is baked into a Docker image together with a **pinned FFmpeg**:

```
ticolinea/node:<version>     = self-contained linux-x64 publish + FFmpeg <pinned>
```

`<version>` is semver, read from a `VERSION` file at the repo root. CI tags each image both `<version>` and `master-<short-sha>`.

**Which FFmpeg version to pin — pin to what production runs today, not to the newest.**
The point of pinning is to remove *unchosen* variance, not to introduce a *chosen* one. Containerizing and upgrading FFmpeg in the same change would mean that if a channel breaks, we would not know which decision broke it. So the initial pin is the version currently running on `main` (verify with `ffmpeg -version` on that node; `DEPLOYMENT.md` implies Ubuntu 22.04's 4.4.x, but this must be confirmed, not assumed). The container must reproduce today's behavior exactly.

Upgrading FFmpeg is then a **separate, deliberate change**, validated by `tico-stream` against real streams, shipped as a new base image on its own.

Docker is what makes FFmpeg identical across clients; the self-contained publish is what makes the image thin and the fallback cheap.

**Bare-metal fallback:** because the artifact is self-contained, a client who refuses Docker can take the same build as a tarball plus a systemd unit — no separate build path. This fallback is **documented in the RUNBOOK, not automated in v1.** Adding a `--bare-metal` mode to the tool is deferred until a client actually refuses Docker.

### 4.2 On the client's server

**nginx owns 27701. The node hides behind it.** The container binds `127.0.0.1:8080`; nginx is the only public listener.

```nginx
listen 27701;
location /Streams/  { proxy_pass http://127.0.0.1:8080; }   # M3U/HLS API — public
location /streams/  { alias /srv/ticolinea/streams/; }      # segments, served as static files
location /hangfire  { allow <wireguard-subnet>; deny all; proxy_pass http://127.0.0.1:8080; }
location /swagger   { deny all; }
```

This solves three problems in one place: the dashboard becomes tunnel-only, Swagger is closed, and because nginx serves segments straight off disk, **port 27703 is no longer needed at all**.

`Streaming.SegmentBaseUrl` therefore becomes `http://<provider-host>:27701/streams`.

**Folder layout** — split by lifecycle:

```
/opt/ticolinea/                 # tool-managed, root-owned
  compose.yml
  .env                          # PROVIDER=<slug>  IMAGE_TAG=<version>  PREVIOUS_IMAGE_TAG=<version>
  config/
    appsettings.Production.json
    appsettings.<slug>.json     # GENERATED from template — never copied from main
    jwt-public.pem              # panel's RSA PUBLIC key only
  nginx/node.conf
  secrets/                      # 0600, root: db password, scoped IAM credentials

/srv/ticolinea/                 # data — high churn, ideally its own volume
  streams/  epg/  movies/  series/  raw-movies/  logs/
```

Service user `ticolinea` with a **fixed UID (10001)**, so the container's writes and the host bind-mount's ownership agree. Segment data is a **bind mount**, never the container's overlay layer.

Config is **generated from a template**. This is the control that prevents shipping `main`'s secrets to a third party, and it is not optional.

### 4.2.1 The node's database

The node reads its channel list (`streams_tl`) from a **local** MariaDB, following the `fibraencasa` pattern — `127.0.0.1` only, never a network listener, never a firewall rule. `bootstrap` therefore must:

1. Install MariaDB and **bind it to `127.0.0.1`**.
2. Create the database `<slug>-streaming`.
3. Create the application user `streamingservice` with least privilege on that database only, and a **generated random password** written to `/opt/ticolinea/secrets/db-password` (0600, root) and referenced from the generated `appsettings.<slug>.json`.
4. Apply the schema.

**The schema is a gap in the current repo.** There is no migration tooling and no checked-in DDL for the node's database — the existing nodes' schemas were created by hand. Producing a canonical `schema.sql` (dumped from `main`, reviewed, stripped of data) is **in scope for this work**, because without it `bootstrap` cannot produce a node that starts.

Until **spec B** (package sync) lands, the resulting database is *empty of channels* and must be seeded manually. A provisioned node with no `streams_tl` rows will start cleanly and serve nothing. This is expected, and is the reason spec B should follow immediately.

### 4.3 Registry

**AWS ECR**, `us-east-1` (alongside the existing RDS). Private repo `tico/node`, immutable tags, lifecycle policy expiring all but the last ~10 images (keeps storage under ~$1/month).

- **Pull credential:** each provider gets its **own IAM user, pull-only, scoped to the one ECR repo.** A compromised client box means revoking one user, not re-keying every provider.
- **Token refresh:** `amazon-ecr-credential-helper` wired into the node's `~/.docker/config.json`. It refreshes the 12-hour ECR token transparently. **No AWS CLI and no cron on the client's box.**

### 4.4 CI

Migrate `ticolineapanel` from Bitbucket to **GitHub**, joining the other two repos. On push to `master`, GitHub Actions:

1. `dotnet publish -c Release -r linux-x64 --self-contained`
2. `docker build` (app + pinned FFmpeg)
3. `docker push` to ECR, tagged `<version>` and `master-<short-sha>`

Authentication to AWS is via **OIDC role assumption** (`permissions: id-token: write`). **No long-lived AWS key is stored in CI.** This is the main reason to prefer GitHub over Bitbucket Pipelines here; the 2,000 free minutes/month (vs Bitbucket's 50) is the secondary reason.

**CI does not deploy.** It cannot: a client's box is reachable only over WireGuard from a peer, and CI is not one. This is a property of the topology, not a limitation — and it is also *desirable*, per §6.

## 5. The tool

A CLI in the middleware repo under `deploy/`. One config file per provider; secrets gitignored.

```
./tico probe     <slug>   # SSH in, report distro / docker / cpu / disk / egress. Changes NOTHING.
./tico bootstrap <slug>   # idempotent. Installs: docker, nginx, MariaDB (127.0.0.1 only),
                          #   ECR credential helper. Creates: user ticolinea(10001), folder tree,
                          #   database + least-priv user + generated password, schema, generated
                          #   appsettings.<slug>.json, jwt-public.pem, nginx/node.conf, compose.yml
./tico deploy    <slug> --tag <v>   # preflight → swap → verify → auto-rollback on failure
./tico rollback  <slug>   # restore PREVIOUS_IMAGE_TAG, re-up
./tico status    <slug>   # health, stream count, uptime, current tag
./tico ports     <slug>   # print the exact firewall request to hand the client
```

Implemented as `bash` with `set -euo pipefail`, linted with `shellcheck`. Ansible would be the better tool at ~10+ providers; at two, bash is more debuggable by this team and adds no operator-side dependency. Revisit if provider count grows.

Two rules the implementation must honor:

- **`probe` before `bootstrap`.** Never mutate a stranger's server before reporting what was found on it.
- **`bootstrap` is idempotent.** Assume it will be run twice, and once on a box where the previous run died halfway.

## 6. Safe client updates

### 6.1 The success criterion

Restarting the node kills every FFmpeg process, so no new HLS segments are written. Viewers do not stall immediately — they drain their buffer first, and that buffer is approximately `hls_time × hls_list_size` (the stream's `Intervalo` and `Segmentos`), on the order of **~30 seconds**.

> **If the node is back and producing fresh segments within the playlist window, viewers never notice. If it is not, every channel stalls at once.**

Every step below exists to protect that number.

### 6.2 Deploy sequence

Nothing destructive happens until the risky parts are already proven.

1. **Preflight (zero downtime).** Assert `/health` is *currently* green — never deploy onto an already-broken node. Check disk headroom. Confirm the tag exists in ECR. **Pull the image now.** A failed pull at this point costs nothing; a failed pull after the container is stopped costs an outage.
2. **Baseline.** Record the current `IMAGE_TAG`, and how many streams are running with fresh segments. You cannot know whether you broke something without knowing what "working" looked like a minute ago.
3. **Swap.** `docker compose up -d`. This is the only moment of downtime.
4. **Verify.** Poll `/health` (`HealthController`), then verify that **streams actually recovered**: segment mtimes advancing, stream count back to baseline. *`/health` returning 200 while every channel is dead is precisely the failure this step exists to catch.*
5. **Auto-rollback.** On verification failure, restore `IMAGE_TAG` from `PREVIOUS_IMAGE_TAG` in `.env` and re-up. The previous image is still in the local Docker cache, so this is seconds, not a re-pull. Then report loudly.

`deploy` writes the outgoing tag to `PREVIOUS_IMAGE_TAG` at step 2, before anything is swapped. That is what makes both automatic and manual rollback possible without consulting ECR.

### 6.3 Guardrails

- **`--canary`** — roll one provider, observe, then the rest. Never update all clients at once.
- **Peak-hours guard** — refuse to deploy inside the provider's configured peak window unless `--force`. Default **18:00–23:00 America/Costa_Rica**, overridable per provider in its config file. Restarting FFmpeg at 8pm is the mistake a tired operator makes.
- **`--dry-run`** — print every command without executing it.
- **`status` before `deploy`** — always.

### 6.4 RUNBOOK.md

Written as part of this work: how to update a client, what to check first, what "healthy" looks like, how to roll back **by hand if the tool itself is broken**, and what to tell the client if their channels do go down.

## 7. Ports — the request handed to the client

| Direction | Port / Host | Why |
|---|---|---|
| Inbound, **public** | `27701/tcp` | End-user Android TVs pull HLS. They are on the open internet, **not** on the VPN. |
| Inbound, **public** | `443/tcp` | Only if TLS is terminated on the node. |
| Inbound, **tunnel only** | `22/tcp` | SSH, over WireGuard. Not public. |
| Inbound (client side) | `51820/udp` | WireGuard, so the operator can peer in. |
| **Outbound** | `tv.play-latino.com:27702` | Token introspect/refresh + activity tracking. Blocking this breaks auth silently. |
| **Outbound** | `tv.play-latino.com:27701` | **The restream source pull — the actual content path.** Confirm against the `fuente_stream` values the provider is issued. |
| **Outbound** | `api.ecr.us-east-1.amazonaws.com:443` | ECR auth. |
| **Outbound** | `*.dkr.ecr.us-east-1.amazonaws.com:443` | ECR manifest. |
| **Outbound** | `prod-us-east-1-starport-layer-bucket.s3.us-east-1.amazonaws.com:443` | **ECR image layers are served from S3.** Hostname-allowlisting clients fail confusingly without this. |
| **Never** | MySQL | Loopback only (`127.0.0.1`). No firewall rule, ever. |

## 8. Testing

- `shellcheck` over all tool scripts.
- **Idempotency:** run `bootstrap` twice against a throwaway Ubuntu VM (multipass); assert a clean second run and a green `/health`.
- **Schema:** `bootstrap` against a clean VM produces a node that starts and answers `/health` — proving the extracted `schema.sql` is actually complete, which is the only way to find out.
- **Database is not reachable off-box:** from the VM's public interface, assert the MariaDB port refuses connections.
- **Rollback is tested, not merely written.** Deploy a deliberately broken tag; assert the tool detects it, rolls back, and the node returns to the baseline stream count.
- **Secret leakage:** assert the generated `appsettings.<slug>.json` contains no value present in `appsettings.main.json`'s connection string or the panel's private key.
- **Exposure:** from off-tunnel, assert `/hangfire` and `/swagger` are refused on 27701 while `/Streams/` succeeds.

Owned by `tico-qa`; provisioning scripts by `tico-devops`; FFmpeg pinning validated by `tico-stream`.

## 9. Prerequisites and risks

**Do first, before any of this:**
- **Revoke the GitHub PAT** currently embedded in plaintext in the `StreamTV` and `ticolinea.panel` git remote URLs. Move remotes to SSH. The repo migration in §4.4 should not be done with a leaked credential.

**Risks / to verify early:**
- **Segment IO under Docker.** HLS writes many small files at high churn. Must land on the bind mount, not the overlay layer. Verify throughput before committing.
- **`Mikrotik.Net` is a dependency.** If the node needs to reach LAN devices on the client's network, the container's network mode needs review. Unknown today — `probe` should surface it.
- **Rotating the committed secrets** (RDS password, JWT private key, pepper, panel API key) is out of scope here but becomes more urgent with each client onboarded. It should be its own ticket.
- The image is ~500MB. First pull on a slow client link is slow; subsequent pulls are layer-deltas.

## 10. Open questions

None blocking. Deferred by design: package sync (spec B) — until it exists, a provisioned node's database must be seeded by hand, and the node will serve nothing without it. Spec B should follow immediately.
