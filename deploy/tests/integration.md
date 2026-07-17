# End-to-End VM Integration Checklist

This document describes manual verification steps for proving that the bootstrap and deployment tooling works correctly on a fresh Ubuntu 22.04 VM. The test exercises bootstrap idempotency, schema application, MariaDB loopback-only configuration, and deploy/verify/rollback workflows.

**Prerequisites:**
- macOS host with Homebrew and `multipass` installed
- SSH key pair (uses `~/.ssh/id_ed25519.pub` for key auth to the VM)
- `nc` (netcat) installed for connectivity testing
- Local copy of this repository with `deploy/tico` script

**Estimated time:** 30–45 minutes
**Artifacts:** None (VM is destroyed at end); all outputs recorded in this document.

---

## Step 1: Install multipass and launch a clean Ubuntu 22.04 VM

**Objective:** Verify the hypervisor setup and OS version match the tool's requirements.

### Checklist

- [ ] Install multipass
  ```bash
  brew install --cask multipass
  ```

- [ ] Launch a clean Ubuntu 22.04 VM
  ```bash
  multipass launch 22.04 --name tico-test --cpus 2 --memory 4G --disk 20G
  ```
  Expected: `Launched: tico-test`

- [ ] Verify OS version
  ```bash
  multipass exec tico-test -- bash -c 'source /etc/os-release; echo $VERSION_ID'
  ```
  **Expected output:** `22.04`

### Recorded Output

```
[paste output here]
```

---

## Step 2: Point a test provider config at the VM

**Objective:** Set up SSH credentials and the provider configuration file that the tool will use to reach the VM.

### Checklist

- [ ] Capture the VM's IPv4 address
  ```bash
  IP=$(multipass info tico-test | awk '/IPv4/{print $2}')
  echo "VM IP: $IP"
  ```

- [ ] Create the test provider config file
  ```bash
  cat > deploy/providers/tico-test.conf <<EOF
SSH_HOST=$IP
SSH_USER=ubuntu
PROVIDER=ticotest
PROVIDER_NAME=Tico Test
PUBLIC_HOST=$IP
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
EOF
  ```

- [ ] Verify the config was written
  ```bash
  cat deploy/providers/tico-test.conf
  ```
  **Expected:** All keys present with values populated from $IP.

- [ ] Set up passwordless SSH key authentication
  ```bash
  multipass exec tico-test -- bash -c "echo '$(cat ~/.ssh/id_ed25519.pub)' >> ~/.ssh/authorized_keys"
  ```

- [ ] Test SSH access
  ```bash
  ssh ubuntu@$IP "echo 'SSH access OK'"
  ```
  **Expected:** `SSH access OK` (no password prompt).

### Recorded Output

```
[paste output here]
```

---

## Step 3: Probe, then bootstrap twice (idempotency)

**Objective:** Verify that the probe command works and that bootstrap is idempotent (running it twice produces the same result with no destructive changes on the second run).

### Checklist

- [ ] Probe the VM (read-only; no changes)
  ```bash
  ./deploy/tico probe tico-test
  ```
  **Expected:** Shows OS, CPU/RAM/disk, ffmpeg version, aspnetcore-runtime-6.0 status, and outbound connectivity.

- [ ] First bootstrap (full provisioning)
  ```bash
  ./deploy/tico bootstrap tico-test
  ```
  **Expected:** Exit code 0; messages like "Installing base packages", "Creating service user", "Configuring MariaDB", "Rendering and uploading config", "Bootstrap complete".

- [ ] Second bootstrap (must be idempotent)
  ```bash
  ./deploy/tico bootstrap tico-test
  ```
  **Expected:** Exit code 0; second run must not fail or make destructive changes. Common patterns:
    - `dpkg -s aspnetcore-runtime-6.0` already present → skip install
    - `id ticolinea` succeeds → skip useradd
    - Database and user exist → `ALTER USER` and `GRANT` succeed (MySQL idempotent operations)
    - MariaDB already bound to 127.0.0.1 → no change

### Recorded Output

**First bootstrap:**
```
[paste output here]
```

**Second bootstrap:**
```
[paste output here]
```

---

## Step 4: Verify DB is loopback-only and schema can be applied

**Objective:** Confirm MariaDB is bound to 127.0.0.1 (loopback-only) and cannot be reached from off-box. Prove schema application is idempotent.

### Checklist

- [ ] Verify MariaDB is listening on loopback only (on the VM)
  ```bash
  multipass exec tico-test -- bash -c 'ss -tlnp | grep 3306'
  ```
  **Expected:** Only one line; address column shows `127.0.0.1:3306` (not `0.0.0.0:3306` or any other address).

- [ ] Verify port 3306 is unreachable from your host machine
  ```bash
  IP=$(multipass info tico-test | awk '/IPv4/{print $2}')
  nc -z -w3 "$IP" 3306 && echo "REACHABLE (BAD)" || echo "refused (good)"
  ```
  **Expected:** `refused (good)` — the connection should time out or be refused.

- [ ] Verify current release is set to "none" (no app deployed yet)
  ```bash
  ./deploy/tico status tico-test
  ```
  **Expected:**
    - `health: 000 (200=healthy)` — no running app yet
    - `current: none` — no release deployed
    - `unit active: inactive` — systemd unit exists but inactive

### Recorded Output

**MariaDB binding:**
```
[paste output here]
```

**External connectivity test:**
```
[paste output here]
```

**Status before deploy:**
```
[paste output here]
```

---

## Step 5: Deploy a real artifact, verify health, then test rollback

**Objective:** Prove that deploy, verify, and rollback work end-to-end. Simulate a failed deployment and verify auto-rollback.

### Prerequisites for this step

You must have a built artifact directory with:
- `ticolinea.stream.service.dll` (the compiled .NET 6 application)
- `schema.sql` (the EF Core-generated database schema)

These typically come from:
1. **CI build artifact:** from GitHub Actions on a successful commit to the panel repo
2. **Local build:** `dotnet publish` on the backend + `dotnet ef database script` for schema

For this checklist, assume:
- `./publish/` is your locally-built artifact directory
- It contains both `ticolinea.stream.service.dll` and `schema.sql`

### Checklist

- [ ] Verify the artifact has required files
  ```bash
  ls -la ./publish/ticolinea.stream.service.dll ./publish/schema.sql
  ```
  **Expected:** Both files exist and are non-empty.

- [ ] Deploy version 1.0.0
  ```bash
  ./deploy/tico deploy tico-test --tag 1.0.0 --artifact ./publish
  ```
  **Expected:** Exit code 0; output includes:
    - "Preflight: health, disk, staging release 1.0.0"
    - "Baseline: previous=none, fresh streams=0"
    - "Deploy 1.0.0 verified."
    - "Deploy complete: ticotest now on 1.0.0"

- [ ] Verify health after successful deploy
  ```bash
  ./deploy/tico status tico-test
  ```
  **Expected:**
    - `health: 200 (200=healthy)` — app is running and responding
    - `current: 1.0.0` — correct version deployed
    - `unit active: active (running)` — systemd unit is active

- [ ] Confirm no main secret on the box: the deploy must have stripped the
      non-provider configs that `dotnet publish` bundles (which carry the live
      prod RDS password + panel API key)
  ```bash
  ssh <node> 'ls /opt/<slug>/current/ | grep -c appsettings.main.json'
  ```
  **Expected:** `0` — `appsettings.main.json` is absent from the running release
      (also confirm `appsettings.fibraencasa.json` and `appsettings.Development.json` are gone;
      only `appsettings.<slug>.json` / `appsettings.Production.json` remain)

- [ ] Record the fresh streams baseline
  ```bash
  BASELINE=$(./deploy/tico status tico-test | grep 'fresh streams:' | awk '{print $NF}')
  echo "Baseline fresh streams: $BASELINE"
  ```

- [ ] Create a broken artifact (simulate a bad release)
  ```bash
  mkdir -p ./broken
  # Copy the schema but use a broken/exiting-immediately dll
  cp ./publish/schema.sql ./broken/schema.sql
  # Create a minimal broken DLL (or use an old/incompatible one if you have it)
  touch ./broken/ticolinea.stream.service.dll
  ```

- [ ] Attempt deploy of broken version (expect auto-rollback)
  ```bash
  ./deploy/tico deploy tico-test --tag 1.0.1-broken --artifact ./broken
  ```
  **Expected:** Exit code non-zero (fail); output includes:
    - "Deploy 1.0.1-broken verified." fails after retries
    - "Rolling back to 1.0.0"
    - "Deploy failed and was rolled back to 1.0.0. Investigate before retrying."

- [ ] Verify rollback succeeded (should be back on 1.0.0 and healthy)
  ```bash
  ./deploy/tico status tico-test
  ```
  **Expected:**
    - `health: 200 (200=healthy)` — rolled back successfully
    - `current: 1.0.0` — still on the good version
    - `fresh streams:` — at least as many as before (or baseline if set)

- [ ] Verify you can deploy and rollback using the explicit rollback command
  ```bash
  ./deploy/tico rollback tico-test
  ```
  **Expected:** (if there was a previous version before 1.0.0) rolls back to it. In this test, output may be "no previous release found" since we only have 1.0.0.

### Recorded Output

**Artifact verification:**
```
[paste output here]
```

**Deploy 1.0.0:**
```
[paste output here]
```

**Status after 1.0.0:**
```
[paste output here]
```

**Deploy 1.0.1-broken (expect failure):**
```
[paste output here]
```

**Status after rollback:**
```
[paste output here]
```

---

## Step 6: Record results and tear down

**Objective:** Clean up the throwaway VM and the test provider config.

### Checklist

- [ ] Delete the VM
  ```bash
  multipass delete tico-test && multipass purge
  ```
  **Expected:** `tico-test` and its disk deleted.

- [ ] Remove the test provider config
  ```bash
  rm -f deploy/providers/tico-test.conf
  ```

- [ ] Verify the config is gone
  ```bash
  [ ! -f deploy/providers/tico-test.conf ] && echo "Config cleaned up"
  ```

### Recorded Output

```
[paste output here]
```

---

## Package sync (spec B)

**Objective:** Prove the `PackageSyncService` DB writes (upsert / ensure `streams_info` / disable) work against a live MySQL — this path has no unit-test harness; the pure decision logic is covered separately by `PackageSyncPlanTests`.

### Checklist

- [ ] After bootstrap+deploy, trigger a sync (restart the node so the boot job runs).
- [ ] `mysql <db> -e "SELECT COUNT(*) FROM streams_tl WHERE sincronizado=1;"` → matches the package's channel count.
- [ ] `mysql <db> -e "SELECT COUNT(*) FROM streams_info si JOIN streams_tl s ON s.id=si.stream_id WHERE s.sincronizado=1;"` → same count (a streams_info row per synced channel).
- [ ] Set a channel's `iniciado=0` by hand; restart; confirm it stays 0 (sync does not overwrite iniciado).
- [ ] Remove a channel from the package in the panel; wait for / trigger a sync; confirm that channel is `habilitado=0` and its row remains.

---

## Summary of Results

**Date:** _______________  
**Operator:** _______________  
**VM IP (captured at start):** _______________  

### Bootstrap Idempotency
- [ ] First run: exit 0, all steps completed
- [ ] Second run: exit 0, no destructive changes

### Database Isolation
- [ ] MariaDB bound to 127.0.0.1 only
- [ ] External port 3306 unreachable from host

### Deploy/Verify/Rollback
- [ ] Deploy v1.0.0: exit 0, health 200
- [ ] Deploy v1.0.1-broken: auto-rollback to v1.0.0, health restored
- [ ] Post-rollback: current=1.0.0, health 200

### Cleanup
- [ ] VM destroyed
- [ ] Provider config removed

---

## Known Issues and Troubleshooting

### SSH authentication fails

**Symptom:** `Permission denied (publickey)` when running `./deploy/tico probe tico-test`

**Cause:** SSH key was not added to the VM's `authorized_keys`.

**Fix:**
```bash
IP=$(multipass info tico-test | awk '/IPv4/{print $2}')
multipass exec tico-test -- bash -c "echo '$(cat ~/.ssh/id_ed25519.pub)' >> ~/.ssh/authorized_keys"
ssh-keyscan -H $IP >> ~/.ssh/known_hosts 2>/dev/null
```

### MariaDB bind-address edit failed

**Symptom:** Bootstrap completes but `ss -tlnp | grep 3306` shows `0.0.0.0:3306`.

**Cause:** The sed pattern in `_setup_mariadb()` didn't match the exact config file format.

**Fix (manual):** SSH to the VM and edit `/etc/mysql/mariadb.conf.d/50-server.cnf` directly:
```bash
ssh ubuntu@$IP
sudo sed -ri 's/^#?bind-address\s*=.*/bind-address = 127.0.0.1/' /etc/mysql/mariadb.conf.d/50-server.cnf
sudo systemctl restart mariadb
```

### Deploy fails with "disk on /srv is >90% full"

**Symptom:** `tico deploy` exits with disk-full error.

**Cause:** The 20GB VM disk filled during testing or earlier deploys.

**Fix:** Increase VM disk size or clean up old release artifacts. To increase disk size, recreate the VM:
```bash
multipass delete tico-test && multipass purge
multipass launch 22.04 --name tico-test --cpus 2 --memory 4G --disk 50G
```

### Deploy verification fails (times out waiting for health 200)

**Symptom:** `Verification failed for 1.0.0` after deploy.

**Cause:** 
- The .NET app failed to start (check `systemctl status ticolinea-streaming` on the VM).
- Network connectivity issue to the health endpoint.
- Schema application failed (check `mysql` logs on the VM).

**Fix:** SSH to the VM and investigate:
```bash
ssh ubuntu@$IP
sudo systemctl status ticolinea-streaming
sudo journalctl -u ticolinea-streaming -n 50
sudo mysql -uroot -e "SHOW DATABASES; DESCRIBE \`ticotest-streaming\`.* LIMIT 1;" 
curl http://127.0.0.1:1234/api/health
```

---

## References

- **Tool:** `./deploy/tico` — wrapper script for all operations
- **Config format:** `deploy/providers/<slug>.conf` (SSH credentials, provider identity, URLs)
- **Bootstrap:** `./deploy/lib/commands/bootstrap.sh` — installs packages, creates users, configures MariaDB and nginx
- **Deploy:** `./deploy/lib/commands/deploy.sh` — stages, applies schema, swaps symlinks, verifies health, auto-rolls-back on failure
- **Health endpoint:** `http://127.0.0.1:1234/api/health` — internal loopback; returns HTTP 200 when the app is running
- **Database:** `<PROVIDER>-streaming` (e.g., `ticotest-streaming`); user `streamingservice@127.0.0.1`
- **RUNBOOK:** See `deploy/RUNBOOK.md` for post-failure troubleshooting and manual recovery
