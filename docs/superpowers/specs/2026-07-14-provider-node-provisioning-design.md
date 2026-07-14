# Provider Node Provisioning & Deployment — Design

**Date:** 2026-07-14
**Status:** Approved (design); implementation plan pending
**Scope:** Subsystems #2 and #3 of provider onboarding. Excludes package sync (**spec B**) and the stream control plane / Admin UI (**spec C**).

---

## 1. Problem

Onboarding a provider today is a hand-run sequence out of `DEPLOYMENT.md`. It is slow, unrepeatable, and unsafe in three specific ways:

1. **`scp -r ./publish` ships Ticolinea's secrets to a third party.** The repo commits the live RDS password, the JWT *private* key, the password pepper, and the panel API key.
2. **The Hangfire dashboard is unauthenticated.** `DashboardNoAuthorizationFilter.Authorize()` returns `true` unconditionally. The moment 27701 is public, so is the dashboard — and so is Swagger.
3. **`net6.0` is end-of-life**, and `dotnet-runtime-6.0` is increasingly hard to obtain from the Microsoft feed.

There is also no defined way to **update** a running client without dropping their channels.

## 2. Context: the restream model

Ticolinea transcodes the real sources. A provider node runs FFmpeg against **Ticolinea's already-transcoded HLS** and copies chunks to its own disk; the provider's Android TVs then pull from *that* node. Bandwidth cost sits with the provider.

This needs **no new code** — `fuente_stream` is passed straight to FFmpeg as `-i` (`Services/StreamingService.cs:319`).

Two consequences that constrain everything below:

- The node needs **outbound** reach to the Ticolinea origin (`tv.play-latino.com:27701`). This is the content path, it is easy to leave off a firewall request, and nothing works without it.
- On a provider node, **re-encoding is a bug** — it should be on the copy path.

## 3. Approach

Bare metal. No Docker, no container registry, no cloud credential on the client's box.

Two decisions carry most of the weight:

**Publish self-contained.** `dotnet publish -c Release -r linux-x64 --self-contained` produces a native executable carrying its own .NET runtime. **Nothing installs .NET on the client's host** — which deletes the `net6.0`-EOL problem rather than working around it.

**Pin FFmpeg by distro, not by binary.** Require **Ubuntu 22.04** and `apt install ffmpeg`, then `apt-mark hold ffmpeg` so it cannot drift under us. `probe` records each client's `ffmpeg -version` and **warns if it differs from what `main` runs**. We don't get byte-identical FFmpeg across clients; we get *told* when a client diverges, instead of learning it from a broken channel. (Shipping a pinned static binary is possible later — `FfmpegPath` is already configurable in `Constantes/Global.cs:31`, and fibraencasa already points at a custom build — but it is not worth the management cost today.)

The host therefore needs only **nginx** and **MariaDB**.

## 4. On the client's server

### 4.1 nginx — reproduce production, plus the lockdown it is missing

Production already fronts the node with nginx: **27701 proxies to the node on `localhost:1234`**, and **27703 serves `.ts` segments statically**. Dynamic playlists come from the app; segments come off disk. Provider nodes reproduce this exactly — there is no reason for them to differ from `main`.

Note this means **`DEPLOYMENT.md` is stale**: it sets `ASPNETCORE_URLS=http://0.0.0.0:27701`, which would collide with nginx's own listener. The node listens on **1234**.

**The gap:** the production 27701 block is `location / { proxy_pass ... }` — it proxies *everything*, so `/hangfire` (unauthenticated) and `/swagger` are **currently reachable from the public internet**. The provisioned config closes that:

```nginx
# 27701 — middleware
server {
    listen 27701;

    location /hangfire { allow <wireguard-subnet>; deny all; proxy_pass http://127.0.0.1:1234; }
    location /swagger  { deny all; }

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

The only deliberate deviation from `main`'s config: `root` points at `/srv/ticolinea` rather than `/var/www/html`, keeping high-churn stream data out of the web root and inside the one directory tree this tool owns. **The URL path is unchanged** (`/streams/*.ts`), so nothing downstream cares.

`Streaming.SegmentBaseUrl` = `http://<provider-host>:27703`, matching `main`.

**The node must bind `127.0.0.1:1234`, not `0.0.0.0:1234`.** Binding all interfaces would leave the app directly reachable on 1234 — bypassing nginx, and with it the Hangfire and Swagger denials — if the client's firewall is ever permissive. `ASPNETCORE_URLS=http://127.0.0.1:1234`.

### 4.2 Layout

```
/opt/ticolinea/
  releases/1.4.1/        # self-contained publish
  releases/1.4.2/
  current -> releases/1.4.2
  config/
    appsettings.Production.json
    appsettings.<slug>.json     # GENERATED from template — never copied from main
    jwt-public.pem              # panel's RSA PUBLIC key only
  nginx/node.conf
  secrets/                      # 0600 root: db password

/srv/ticolinea/                 # data — high churn
  streams/  epg/  movies/  series/  raw-movies/  logs/
```

Service user `ticolinea`. The last 5 releases are kept; older ones pruned.

Config is **generated from a template**, never copied from `main`. This is the control that stops us shipping Ticolinea's secrets to a third party, and it is not optional.

### 4.3 Database

Local MariaDB, following the `fibraencasa` pattern: **bound to `127.0.0.1` only.** Never a network listener, never a firewall rule. `bootstrap` installs it, creates `<slug>-streaming`, creates a least-privilege `streamingservice` user with a **generated random password** written to `secrets/db-password`, and applies the schema.

**The schema is a gap.** There is no checked-in DDL and no migration tooling — the existing nodes were built by hand. Extracting a canonical `schema.sql` (dumped from `main`, reviewed, stripped of data) is **in scope**, because `bootstrap` has nothing to apply without it.

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
ExecStart=/opt/ticolinea/current/ticolinea.stream.service
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

# Hardening
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=/srv/ticolinea /opt/ticolinea

[Install]
WantedBy=multi-user.target
```

Three deliberate corrections to the unit currently in `DEPLOYMENT.md`:

- **`Type=simple`, not `Type=notify`.** `Microsoft.Extensions.Hosting.Systemd` is not referenced and `UseSystemd()` is never called, so the app never sends a readiness notification. With `Type=notify`, systemd waits for one that never arrives, times out, and marks the unit failed. **This is a live bug in the documented unit.**
- **`LimitNOFILE` / `TasksMax`.** `StreamingService` comments reference "155+ runtime processes"; each FFmpeg process holds many file descriptors and churns segment files. systemd's defaults will cap this well below what the service expects, and the failure mode is FFmpeg silently failing to spawn under load.
- **`ExecStart` is the executable**, not `dotnet <dll>` — a consequence of the self-contained publish.

## 5. Build and ship

**CI (GitHub Actions, on push to `master`):** `dotnet publish -c Release -r linux-x64 --self-contained`, then attach the output as a **release artifact** tagged `<version>` (semver from a `VERSION` file at the repo root). No cloud credentials in CI — it builds and uploads, nothing more.

This requires migrating `ticolineapanel` from Bitbucket to GitHub, alongside the other two repos. Bitbucket Pipelines' 50 free minutes/month will not comfortably carry a .NET publish; GitHub gives 2,000.

**CI does not deploy.** It cannot — the client's box is reachable only over WireGuard from a peer. That is a property of the topology, and it is also what we want: a human decides when a given client takes a build.

**Deploy** fetches the artifact and `rsync`s it to the node over the tunnel. The client's server never talks to GitHub, AWS, or any registry — **no Ticolinea credential is ever stored on it.**

## 6. The tool

`deploy/` in the middleware repo. Bash, `set -euo pipefail`, `shellcheck`-clean. One config file per provider; secrets gitignored.

```
./tico probe     <slug>   # SSH in; report distro, cpu, disk, ffmpeg version, egress. Changes NOTHING.
./tico bootstrap <slug>   # idempotent: nginx, mariadb(127.0.0.1), ffmpeg(+hold), user, dirs,
                          #   schema, generated config, systemd unit
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
- **Schema:** `bootstrap` on a clean VM yields a node that starts — the only real proof `schema.sql` is complete.
- **Rollback is tested, not merely written.** Deploy a deliberately broken release; assert the tool detects it, rolls back, and the node returns to baseline.
- **No secret leakage:** assert the generated `appsettings.<slug>.json` shares no value with `main`'s connection string or the panel's private key.
- **Exposure:** from off-tunnel, assert `/hangfire` and `/swagger` are refused on 27701 while `/Streams/` succeeds.
- **Database is not reachable off-box:** assert the MariaDB port refuses connections on the public interface.

Scripts by `tico-devops`; tests by `tico-qa`; FFmpeg version policy validated by `tico-stream`.

## 10. Prerequisites and risks

**Do first:**
- **Revoke the GitHub PAT** embedded in plaintext in the `StreamTV` and `ticolinea.panel` git remote URLs. The repo migration in §5 should not be done with a leaked credential.

**Risks:**
- **Rotating the committed secrets** (RDS password, JWT private key, pepper, panel API key) is out of scope but grows more urgent with each client onboarded. Own ticket.
- **Ubuntu 22.04 is assumed.** A client on another distro means either a different FFmpeg version (tolerable, and `probe` will say so) or a glibc too old for the self-contained runtime (not tolerable). `probe` must check both.

## 11. Open questions

None blocking. Deferred by design: **spec B** (package sync). Until it exists, a provisioned node's database must be seeded by hand and will serve nothing without it.
