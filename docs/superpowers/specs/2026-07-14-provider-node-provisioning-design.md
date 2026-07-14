# Provider Node Provisioning & Deployment — Design

**Date:** 2026-07-14
**Status:** Approved (design); implementation plan pending
**Scope:** Subsystems #2 and #3 of provider onboarding. Excludes package sync (**spec B**) and the stream control plane / Admin UI (**spec C**).

---

## 1. Problem

Onboarding a provider today is a hand-run sequence out of `DEPLOYMENT.md`: slow, unrepeatable, and error-prone. There is also no defined way to **update** a running client without dropping their channels.

**Goal:** make onboarding a new consumer repeatable and its updates safe. **Reproduce what production already runs** — do not change how the app behaves. Anything that would require QA of new application behavior is out of scope here.

Explicitly deferred, by decision, to keep that scope tight:

- **Hangfire dashboard auth.** It is currently unauthenticated (`DashboardNoAuthorizationFilter.Authorize()` returns `true`) and reachable through nginx. Left as-is for now; a provider node is no more exposed than `main`. Its own ticket later.
- **`net6.0` end-of-life.** Real, but a future concern. This work runs net6.0 exactly as production does.
- **Rotating the committed secrets** (RDS password, JWT private key, pepper, panel API key). Separate ticket. Note that per-client config (§4.2) means a new provider never receives `main`'s RDS connection string regardless — it gets its own.

## 2. Context: the restream model

Ticolinea transcodes the real sources. A provider node runs FFmpeg against **Ticolinea's already-transcoded HLS** and copies chunks to its own disk; the provider's Android TVs then pull from *that* node. Bandwidth cost sits with the provider.

This needs **no new code** — `fuente_stream` is passed straight to FFmpeg as `-i` (`Services/StreamingService.cs:319`).

Two consequences that constrain everything below:

- The node needs **outbound** reach to the Ticolinea origin (`tv.play-latino.com:27701`). This is the content path, it is easy to leave off a firewall request, and nothing works without it.
- On a provider node, **re-encoding is a bug** — it should be on the copy path.

## 3. Approach

Bare metal, reproducing the production stack. No Docker, no container registry, no cloud credential on the client's box.

**Publish framework-dependent, exactly as production runs.** `dotnet publish -c Release` produces the same `ticolinea.stream.service.dll` that `main` and `fibraencasa` run today, executed by an installed `aspnetcore-runtime-6.0` — the runtime install `DEPLOYMENT.md` already documents and existing nodes already use. This is the lowest-risk choice precisely because it introduces nothing new to trust: identical runtime, identical behavior. (A self-contained publish would remove the runtime-install step, but it is a different artifact shape that would need its own QA; deferred with the net6.0 question.)

**Pin FFmpeg by distro.** Require **Ubuntu 22.04** and `apt install ffmpeg`, then `apt-mark hold ffmpeg` so it cannot drift under us. `probe` records each client's `ffmpeg -version` and **warns if it differs from what `main` runs** — so divergence surfaces during onboarding, not as a broken channel later. (`FfmpegPath` is already configurable in `Constantes/Global.cs:31`, so a pinned binary is an option later if needed.)

The host therefore needs **nginx**, **MariaDB**, and the **`aspnetcore-runtime-6.0`** — the same three things production depends on.

## 4. On the client's server

### 4.1 nginx — reproduce production

Production fronts the node with nginx: **27701 proxies to the node on `localhost:1234`**, and **27703 serves `.ts` segments statically**. Dynamic playlists come from the app; segments come off disk. Provider nodes reproduce this **exactly** — the config below is production's, verbatim except for the one path noted.

Note this means **`DEPLOYMENT.md` is stale**: it sets `ASPNETCORE_URLS=http://0.0.0.0:27701`, which would collide with nginx's own listener. The node listens on **1234**.

```nginx
# 27701 — middleware
server {
    listen 27701;
    location / {
        proxy_pass http://127.0.0.1:1234;
        proxy_http_version 1.1;
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection "";
        proxy_set_header Host       $http_host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header X-Real-IP  $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        client_max_body_size 50M;
    }
}

# 27703 — static segments
server {
    listen 27703;
    root /srv/ticolinea;          # main uses /var/www/html; see note below

    location ~ ^/streams/.*\.ts$ {
        types { video/mp2t ts; }
        add_header Access-Control-Allow-Origin * always;
        add_header Cache-Control "public, max-age=60, s-maxage=60, immutable" always;
    }
    location / { return 404; }
}
```

The only deliberate deviation from `main`: `root` points at `/srv/ticolinea` rather than `/var/www/html`, keeping high-churn stream data inside the one directory tree this tool owns. **The URL path is unchanged** (`/streams/*.ts`), so nothing downstream cares — including where segments are written.

`Streaming.SegmentBaseUrl` = `http://<provider-host>:27703`, matching `main`.

`location /` proxies everything, so `/hangfire` and `/swagger` are reachable through 27701 — same as production. Left as-is per the deferral in §1.

The node binds `127.0.0.1:1234` (`ASPNETCORE_URLS=http://127.0.0.1:1234`), matching production's `proxy_pass http://localhost:1234`. nginx is the public listener; the app is not exposed directly.

### 4.2 Layout

```
/opt/ticolinea/
  releases/1.4.1/        # publish output (ticolinea.stream.service.dll + deps)
  releases/1.4.2/
  current -> releases/1.4.2
  config/
    appsettings.Production.json
    appsettings.<slug>.json     # per-client: its own local DB connection string
    jwt-public.pem              # panel's RSA PUBLIC key only
  nginx/node.conf
  secrets/                      # 0600 root: db password

/srv/ticolinea/                 # data — high churn
  streams/  epg/  movies/  series/  raw-movies/  logs/
```

Service user `ticolinea`. The last 5 releases are kept; older ones pruned.

Each client gets its **own** `appsettings.<slug>.json`, generated from a template with that client's local DB connection string, provider slug, and `SegmentBaseUrl`. It is not copied from `main` — a new provider connects to its *own* local MariaDB (§4.3), so it never receives, and has no use for, `main`'s RDS connection string.

### 4.3 Database

Local MariaDB, following the `fibraencasa` pattern: **bound to `127.0.0.1` only.** Never a network listener, never a firewall rule. `bootstrap` installs it, creates `<slug>-streaming`, creates a least-privilege `streamingservice` user with a **generated random password** written to `secrets/db-password`, and applies the schema.

**Schema comes from `ticolinea.panel`'s EF Core migrations.** They already define the node's tables — `streams_tl`, `epg_tl`, `paquete_tv`, `paquete_tv_streams`, `canal` (`Infrastructure/Migrations/`). The panel's model *is* the node's schema; there is nothing to hand-maintain.

CI runs `dotnet ef migrations script --idempotent -o schema.sql` and ships the result inside the release artifact. `bootstrap` applies it with `mysql < schema.sql`; `deploy` re-applies it, so schema changes travel with the code that needs them. **The client's box needs no EF tooling and no source checkout** — it receives a plain SQL file. Because the script is idempotent, re-running it is a no-op, which is what lets `bootstrap` and `deploy` both apply it safely.

The migration set also creates panel-only tables (`clients`, `admin_users`, `providers`, …). On a node these stay **empty and unused** — the node authenticates against the panel and never writes them. Applying the full set is deliberate: filtering to a subset would mean maintaining a second, divergent schema, which is a far worse problem than a few unused tables.

Until **spec B** lands, the database has no channel rows and must be seeded manually. A provisioned node will start cleanly and serve nothing. Expected — and the reason spec B should follow immediately.

### 4.4 systemd unit

```ini
[Unit]
Description=Ticolinea Streaming Node
After=network-online.target mariadb.service
Wants=network-online.target

[Service]
Type=simple
User=ticolinea
WorkingDirectory=/opt/ticolinea/current
ExecStart=/usr/bin/dotnet /opt/ticolinea/current/ticolinea.stream.service.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
TimeoutStopSec=30

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:1234
Environment=PROVIDER=<slug>

# Capacity. The defaults silently cap FFmpeg spawning under load.
LimitNOFILE=65535
TasksMax=4096

[Install]
WantedBy=multi-user.target
```

Two deliberate corrections to the unit currently in `DEPLOYMENT.md`:

- **`Type=simple`, not `Type=notify`.** `Microsoft.Extensions.Hosting.Systemd` is not referenced and `UseSystemd()` is never called, so the app never sends a readiness notification. With `Type=notify`, systemd waits for one that never arrives, times out, and marks the unit failed. **This is a live bug in the documented unit** — worth checking whether the running nodes already worked around it.
- **`LimitNOFILE` / `TasksMax`.** `StreamingService` comments reference "155+ runtime processes"; each FFmpeg process holds many file descriptors and churns segment files. systemd's defaults will cap this well below what the service expects. These only *raise* ceilings — they cannot change app behavior, so they carry no QA risk while directly serving "runs smoothly under load".

No sandboxing directives (`ProtectSystem`, `PrivateTmp`, etc.) — production does not use them, and adding them would change the process's view of the filesystem, which is exactly the kind of untested behavior change this work avoids.

## 5. Build and ship

**CI (GitHub Actions, on push to `master`):**

1. `dotnet publish -c Release` (framework-dependent — the same DLL production runs)
2. `dotnet ef migrations script --idempotent -o schema.sql` (from `ticolinea.panel`)
3. Attach both as a **release artifact** tagged `<version>` (semver from a `VERSION` file at the repo root).

No cloud credentials in CI — it builds and uploads, nothing more.

Note this couples the two repos: the node's schema is generated from `ticolinea.panel`'s migrations, so CI needs both checked out. Simplest handling is a second checkout step pinned to the panel's `master`.

This requires migrating `ticolineapanel` from Bitbucket to GitHub, alongside the other two repos. Bitbucket Pipelines' 50 free minutes/month will not comfortably carry a .NET publish; GitHub gives 2,000.

**CI does not deploy.** It cannot — the client's box is reachable only over WireGuard from a peer. That is a property of the topology, and it is also what we want: a human decides when a given client takes a build.

**Deploy** fetches the artifact and `rsync`s it to the node over the tunnel. The client's server never talks to GitHub or any registry — no Ticolinea build credential is stored on it.

## 6. The tool

`deploy/` in the middleware repo. Bash, `set -euo pipefail`, `shellcheck`-clean. One config file per provider; secrets gitignored.

```
./tico probe     <slug>   # SSH in; report distro, cpu, disk, ffmpeg version, egress. Changes NOTHING.
./tico bootstrap <slug>   # idempotent: aspnetcore-runtime-6.0, nginx, mariadb(127.0.0.1),
                          #   ffmpeg(+hold), user, dirs, schema, generated config, systemd unit
./tico deploy    <slug> --tag <v>   # preflight → swap → verify → auto-rollback on failure
./tico rollback  <slug>   # previous symlink + restart
./tico status    <slug>   # health, stream count, uptime, current release
./tico ports     <slug>   # print the firewall request to hand the client
```

Two rules the implementation must honor:

- **`probe` before `bootstrap`.** Never mutate a stranger's server before reporting what was found on it.
- **`bootstrap` is idempotent.** Assume it runs twice, and once on a box where the previous run died halfway.

## 7. Safe updates

### 7.1 The success criterion

Restarting the node kills every FFmpeg process, so no new HLS segments are written. Viewers do not stall immediately — they drain their buffer first, and that buffer is roughly `hls_time × hls_list_size` (a stream's `Intervalo` and `Segmentos`): on the order of **~30 seconds**.

> **If the node is back and producing fresh segments within the playlist window, viewers never notice. If it is not, every channel stalls at once.**

Everything below protects that number.

### 7.2 Deploy sequence

Nothing destructive happens until the risky parts are already proven.

1. **Preflight — zero downtime.** Assert `/health` is *currently* green (never deploy onto an already-broken node). Check disk headroom. **rsync the new release into `releases/<v>/` now**, while the old one is still serving. A failed transfer at this point costs nothing.
2. **Baseline.** Record the current release and how many streams are running with fresh segments. You cannot know whether you broke something without knowing what "working" looked like a minute ago.
3. **Swap.** Atomically repoint `current` (`ln -sfn` to a temp name, then `mv -T` — a real atomic rename, not delete-and-recreate) and `systemctl restart`. This is the only downtime.
4. **Verify.** Poll `/health`, then verify **streams actually recovered**: segment mtimes advancing, stream count back to baseline. *`/health` returning 200 while every channel is dead is exactly the failure this step exists to catch.*
5. **Auto-rollback.** On failure, repoint `current` to the previous release and restart. Nothing is re-downloaded, so this takes seconds. Then report loudly.

`--dry-run` prints every command without executing.

### 7.3 RUNBOOK.md

Written as part of this work: how to update a client, what to check first, what "healthy" looks like, how to roll back **by hand if the tool itself is broken**, and what to tell the client if their channels do go down. Note there that prime-time is a bad hour to restart FFmpeg.

## 8. Ports — the request handed to the client

| Direction | Port / Host | Why |
|---|---|---|
| Inbound, **public** | `27701/tcp` | nginx → node. Playlists and the M3U/HLS API. End-user Android TVs are on the open internet, **not** on the VPN. |
| Inbound, **public** | `27703/tcp` | nginx → static `.ts` segments. **This is where the video bytes actually come from** — 27701 alone serves nothing watchable. |
| Inbound, **tunnel only** | `22/tcp` | SSH over WireGuard. Not public. |
| **Never** | `1234/tcp` | The node itself. Bound to `127.0.0.1` — nginx is the only thing that talks to it. |
| Inbound (client side) | `51820/udp` | WireGuard, so the operator can peer in. |
| **Outbound** | `tv.play-latino.com:27702` | Token introspect/refresh + activity tracking. Blocking this breaks auth silently. |
| **Outbound** | `tv.play-latino.com:27701` | **The restream source pull — the actual content path.** |
| **Never** | MySQL | Loopback only. No firewall rule, ever. |

The node needs **no registry egress at all** — only the operator fetches artifacts.

## 9. Testing

- `shellcheck` over all scripts.
- **Idempotency:** run `bootstrap` twice against a throwaway Ubuntu 22.04 VM (multipass); assert a clean second run and a green `/health`.
- **Schema:** `bootstrap` on a clean VM yields a node that starts — the only real proof the generated `schema.sql` is complete. Applying it twice must be a no-op.
- **Rollback is tested, not merely written.** Deploy a deliberately broken release; assert the tool detects it, rolls back, and the node returns to baseline.
- **Config points at the local DB:** assert the generated `appsettings.<slug>.json` connection string targets `127.0.0.1`, not `main`'s RDS. (Correctness, not hardening — a node pointed at the wrong DB is a broken node.)
- **Database is not reachable off-box:** assert the MariaDB port refuses connections on the public interface.

Testing stays scoped to the *provisioning and deploy scripts* — it does not re-QA the application, which runs unchanged. Scripts by `tico-devops`; tests by `tico-qa`; FFmpeg version policy validated by `tico-stream`.

## 10. Prerequisites and risks

**Do first:**
- **Revoke the GitHub PAT** embedded in plaintext in the `StreamTV` and `ticolinea.panel` git remote URLs. The repo migration in §5 should not be done with a leaked credential.

**Risks:**
- **Ubuntu 22.04 is assumed.** A client on another distro means a different `apt` FFmpeg version (tolerable — `probe` will say so) and possibly no `aspnetcore-runtime-6.0` package for that distro (not tolerable without extra work). `probe` must check both.
- **`aspnetcore-runtime-6.0` availability.** It is EOL. The Microsoft `packages.microsoft.com` feed still carries it for 22.04, and existing nodes run it — but if a future onboarding cannot obtain it, the fallback is the deferred self-contained publish. `probe`/`bootstrap` should fail loudly if the runtime cannot be installed, rather than proceeding.

Deferred (own tickets, noted in §1): Hangfire dashboard auth, net6.0 upgrade, rotating the committed secrets.

## 11. Open questions

None blocking. Deferred by design: **spec B** (package sync). Until it exists, a provisioned node's database must be seeded by hand and will serve nothing without it.
