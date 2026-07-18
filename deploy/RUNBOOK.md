# Ticolinea Provider Node — Runbook

## Onboard a new provider

1. Get WireGuard peering from the client; confirm you can `ssh <user>@<tunnel-ip>`.
2. `cp deploy/providers/example.conf deploy/providers/<slug>.conf` and fill it.
   The node's API (27701) and segment (27703) URLs are derived from `PUBLIC_HOST` at render time
   using the tool's fixed ports — nothing to configure per client.
3. `cp deploy/secrets/shared.env.example deploy/secrets/shared.env` and fill
   JWT_PUBLIC_KEY / PANEL_API_KEY from the panel.
4. `./deploy/tico probe <slug>` — read the report. Stop if it is not Ubuntu 22.04 or 24.04,
   if ffmpeg drift is flagged and unacceptable, or if outbound to
   tv.play-latino.com:27701 (restream source) and tv.play-latino.com:27702 (panel API) is unreachable.
5. `./deploy/tico ports <slug>` — send the firewall request to the client.
6. `./deploy/tico bootstrap <slug> [--schema path/to/schema.sql]` — provision. Safe to re-run.
   `--schema` is optional; without it, `deploy` applies the schema shipped in the release artifact.
   Note: re-running `bootstrap` rotates the DB password (it resets the MariaDB user, the on-box
   secret, and the rendered appsettings in one consistent run). After a re-run, redeploy/restart
   the node (`./deploy/tico deploy <slug> ...`) so its live appsettings match the new password.
7. Register the provider in the panel (connection_url = http://<PUBLIC_HOST>:27701).
8. `./deploy/tico deploy <slug> --tag <version> --artifact <unpacked-artifact-dir>`.
9. A freshly-provisioned node has no channel rows yet (that is spec B). It will be
   healthy but serve nothing until the panel package sync exists or rows are seeded.
   Accordingly, any deploy to a node that was serving nothing (first deploy, or an
   update inside this window) verifies **health only**; the fresh-stream check only
   gates updates of a node that was already serving ("recovered to baseline").

## Pilot flags

- `Streaming:FfmpegManagedDiscontinuities` (default **false** everywhere): FFmpeg-managed HLS
  discontinuities — adds `-hls_start_number_source epoch` and skips the app-side
  `#EXT-X-DISCONTINUITY` injection. Pilot on ONE node only: edit
  `/opt/<slug>/config/appsettings.<slug>.json` (set it `true` in the `Streaming` section),
  then `sudo systemctl restart ticolinea-streaming`. Do not flip it in the template.

## Update a running client

- `./deploy/tico status <slug>` first. Do not update during prime-time viewing hours.
- When unsure, dry-run first — it previews every action and changes nothing:

```bash
./deploy/tico deploy <slug> --tag <version> --artifact <dir> --dry-run   # preview only, changes nothing
```

- Then run for real:

```bash
./deploy/tico deploy <slug> --tag <version> --artifact <dir>
```

- The tool stages, swaps, restarts, and verifies streams recovered within ~60s,
  logging `verify: health=... fresh=...` per attempt. On failure it auto-rolls-back
  (a first deploy has nothing to roll back to — the new release stays in place,
  reported as unverified). Viewers absorb ~30s (the HLS buffer) if it recovers in time.

## Roll back a bad release (normal path)

```bash
./deploy/tico rollback <slug>
# Repoints `current` to the previous release, restarts, and reports health.
```

## Roll back by hand (only if `tico rollback` is unavailable)

```bash
ssh <user>@<tunnel-ip>
ls -1dt /opt/<slug>/releases/*/          # find the previous good release
sudo ln -sfn /opt/<slug>/releases/<prev> /opt/<slug>/current.tmp
sudo mv -T /opt/<slug>/current.tmp /opt/<slug>/current
sudo systemctl restart ticolinea-streaming
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:1234/api/health   # expect 200
```

## If you took a client's channels down

- Roll back (above). Confirm `status` shows health 200 and fresh streams > 0.
- Tell the client: brief interruption during a software update, service restored,
  root cause under review.

## Known deferred items (do not treat as bugs here)

- Hangfire dashboard is reachable through nginx and unauthenticated — same as main.
- net6.0 is EOL; the node runs it exactly as production does.
- Committed secrets (RDS password, JWT private key, panel API key) — separate ticket.
