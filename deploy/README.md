# Ticolinea Provider Node — Onboarding & Bare-Metal Requirements

This is what you need from a client to stand up a new **provider node** — a Ticolinea
streaming server that runs on the client's own bare-metal (or VM) Linux host, pulls
Ticolinea's transcoded channels, and serves them to that client's own Android TVs and apps.

The `./deploy/tico` tool does the provisioning. This page is the **requirements checklist**
to hand a client and to gate onboarding; the step-by-step commands live in
[RUNBOOK.md](RUNBOOK.md).

---

## What a provider node is

Ticolinea holds and transcodes the real channel sources. A provider node does **not**
re-transcode — it runs FFmpeg against Ticolinea's already-transcoded HLS, copies the chunks
to local disk, and its end-user devices pull from **that** node. The bandwidth cost lives
with the client; Ticolinea serves one origin pull per channel instead of one per viewer.

```
Ticolinea origin ──(pull, outbound)──▶ Provider node ──(HLS, public)──▶ client's TVs
 (transcoded HLS)                       (nginx + FFmpeg + MariaDB)
```

---

## 1. Server requirements (what the client provides)

| Requirement | Minimum | Notes |
|---|---|---|
| **OS** | **Ubuntu 22.04 or 24.04** | Hard requirement — `bootstrap` refuses anything else. A fresh/clean install is ideal. |
| **Architecture** | x86-64 (amd64) | The node runs a `linux-x64` .NET build. |
| **CPU** | 4 cores to start | The node copies (`-c copy`), it does not transcode, so CPU is modest per channel — but it runs one FFmpeg process **per channel** plus serves HLS to many local viewers. Scale cores with channel count. |
| **RAM** | 4 GB to start | ~150+ concurrent FFmpeg processes are expected at scale; size up with the package. |
| **Disk** | 40 GB+, SSD strongly preferred | HLS segments are many small files written and deleted constantly (high IOPS). Segment data lives under `/srv/<slug>`. |
| **Bandwidth** | Sized to peak viewers × bitrate | This is the client's cost centre — every viewer streams from this box. A 2 Mbps channel × 500 concurrent viewers ≈ 1 Gbps egress. Provision accordingly. |
| **Root / sudo** | Required | `bootstrap` installs packages, creates a service user, writes systemd/nginx config. |

> Sizing above is a **starting point**, not a benchmark. Right-size against the client's
> assigned channel count and expected concurrent viewers.

## 2. Software (installed automatically — nothing to pre-install)

`./deploy/tico bootstrap` installs and configures all of this; the client only needs a clean
Ubuntu 22.04 or 24.04 host:

- `aspnetcore-runtime-6.0` — runs the streaming node. On **22.04** it comes from Microsoft's apt
  feed; on **24.04** that feed has no .NET 6 (it is EOL), so bootstrap installs the ASP.NET Core 6.0
  runtime via Microsoft's official `dotnet-install.sh` into `/usr/share/dotnet` and symlinks it onto
  PATH — the same on-box layout, so systemd/deploy are identical across both OSes.
- `nginx` — the only public listener (fronts the node on 27701, serves segments on 27703)
- `mariadb-server` — the node's local channel database, **bound to 127.0.0.1 only**
- `ffmpeg` — pinned and held (`apt-mark hold`) so it can't drift under an upgrade (4.4.x on 22.04,
  6.x on 24.04)
- `rsync`, `curl` — deploy/health plumbing

The node process itself binds `127.0.0.1:1234` and is **never** exposed directly — nginx is
the only thing the outside world talks to.

## 3. Network — the firewall request

Hand the client this exact list (or run `./deploy/tico ports <slug>` to generate it for their host):

| Direction | Port / Host | Why |
|---|---|---|
| **Inbound, public** | `27701/tcp` | nginx → node. Playlists / HLS API for end-user devices. |
| **Inbound, public** | `27703/tcp` | nginx → static `.ts` segments. **The actual video bytes.** |
| **Inbound, tunnel only** | `22/tcp` | SSH for operator deploy/admin — over WireGuard, **not public**. |
| **Inbound (client side)** | `51820/udp` | WireGuard, so the operator can peer in. |
| **Outbound** | `tv.play-latino.com:27701` | **Restream source pull — the content path.** Nothing works without it. |
| **Outbound** | `tv.play-latino.com:27702` | Panel API — token validation + activity + channel-catalog sync. |
| **Never open** | MariaDB / MySQL | Loopback only (`127.0.0.1`). No firewall rule, ever. |

The two **outbound** rules are the ones clients forget — a blocked egress means the node comes
up healthy but serves nothing.

## 4. Operator access (WireGuard)

You (the operator) manage the node over the client's **WireGuard** VPN — SSH and database admin
ride the tunnel; they are never public. End-user devices are on the open internet, **not** on the VPN.

- The client adds your WireGuard **public key** as a peer and gives you: their endpoint
  (`host:51820`), the tunnel IP they assign you, and the AllowedIPs.
- One WireGuard keypair per operator machine; the private key never leaves it.

### SSH authentication modes

Every command (`probe`/`bootstrap`/`deploy`/`status`/`rollback`) reaches the box over SSH in one of three
ways, set by `AUTH_METHOD` in `deploy/providers/<slug>.conf` (unknown values fall back to `key`):

- **Key auth (default).** Nothing to configure — the behavior when `AUTH_METHOD` is unset or `key`.
  Uses your SSH agent/key and `BatchMode` (no prompts).
- **Password auth (`AUTH_METHOD=password`).** For client boxes that only give a password login (no key
  install, no NOPASSWD sudo). The password comes from a per-provider, gitignored secrets file
  `deploy/secrets/<slug>.env` (`SSH_PASSWORD=`, `SUDO_PASSWORD=`; see `deploy/secrets/provider.env.example`)
  — or, if that file is absent/blank, tico prompts for it. `SUDO_PASSWORD` defaults to `SSH_PASSWORD`.
- **Ask auth (`AUTH_METHOD=ask`).** Same remote mechanics as `password`, but tico **always prompts** and
  **writes nothing to disk** — it never reads `deploy/secrets/<slug>.env`. The password lives only in the
  process env for that one run. Use this when policy forbids storing the client's password anywhere.

`password` and `ask` require **`sshpass`** on your machine (`brew install hudochenkov/sshpass/sshpass`).
Passwords are never echoed or committed. SSH connections are **multiplexed** (a single authenticated
master is reused for the whole run and for back-to-back runs within ~10 min), so you get **one password
prompt per `tico` command** — and every mode gets faster by skipping repeated handshakes.

## 5. What you need from the client — checklist

- [ ] Clean **Ubuntu 22.04 or 24.04** host meeting the sizing above
- [ ] **sudo-capable SSH user** reachable over the WireGuard tunnel
- [ ] **WireGuard peering** (their endpoint + your assigned tunnel IP)
- [ ] A **public hostname / DNS** for the node (becomes the URL clients hit)
- [ ] Confirmation the **firewall** opens the ports in §3 (esp. the two outbound rules)

## 6. Onboarding flow (overview)

Full commands in [RUNBOOK.md](RUNBOOK.md). In short:

```bash
cp deploy/providers/example.conf deploy/providers/<slug>.conf   # fill in host, slug, URLs
cp deploy/secrets/shared.env.example deploy/secrets/shared.env  # JWT public key + panel API key
./deploy/tico probe     <slug>   # read-only: OS, ffmpeg, reachability — changes nothing
./deploy/tico ports     <slug>   # firewall request to send the client
./deploy/tico bootstrap <slug>   # provision the host (idempotent)
# register the provider + assign a package in the panel
./deploy/tico deploy    <slug> --tag <version> --artifact <dir>
./deploy/tico status    <slug>   # health, current release, stream count
```

Once package sync is deployed, the node pulls its assigned channel catalog from the panel
(on boot and every 6 hours) and populates its local database automatically — no manual channel
seeding.

---

**On-box layout** (created by `bootstrap`, for reference):

```
/opt/<slug>/     # tool-managed: releases, config, nginx, secrets (root-owned)
/srv/<slug>/     # high-churn data: streams, epg, movies, series, logs
```

Paths are namespaced by provider slug (`<slug>` = the `PROVIDER` value in `deploy/providers/<slug>.conf`).
This is namespacing for tidiness and blast-radius containment — it is **not** multi-tenancy: one box
still runs exactly one provider, one `ticolinea-streaming` systemd unit, and one nginx config, on the
same ports (27701/27703).

Service user: `ticolinea`. Node behind nginx on `127.0.0.1:1234`. Database on `127.0.0.1` only.
