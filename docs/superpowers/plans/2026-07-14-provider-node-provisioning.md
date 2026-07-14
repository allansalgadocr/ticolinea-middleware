# Provider Node Provisioning & Deployment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a bash CLI (`./tico`) plus a GitHub Actions build that provisions, deploys to, and safely updates a Ticolinea streaming provider node on a client's Ubuntu 22.04 server over SSH/WireGuard — reproducing the production stack, with no application code changes.

**Architecture:** A single `deploy/tico` dispatcher sources focused libraries under `deploy/lib/`. Pure logic (config parsing, template rendering, the ports report, ffmpeg version comparison) is unit-tested with `bats`. All remote effects go through one injectable runner (`remote`/`push`), so deploy orchestration — preflight, swap, verify, auto-rollback — is tested with a mock runner that records commands, without a live box. Provisioning correctness is verified end-to-end against a throwaway multipass VM.

**Tech Stack:** Bash (`set -euo pipefail`), `bats-core` (tests), `shellcheck` (lint), `ssh`/`rsync`/`scp` over WireGuard, MariaDB, nginx, `aspnetcore-runtime-6.0`, GitHub Actions, `dotnet ef migrations script`.

## Global Constraints

- **Reproduce production; change no application behavior.** The node runs the same `ticolinea.stream.service.dll` under installed `aspnetcore-runtime-6.0`. Framework-dependent publish — never `--self-contained` in this plan.
- **Target OS:** Ubuntu 22.04 only. `probe`/`bootstrap` must fail loudly on any other distro or if `aspnetcore-runtime-6.0` cannot be installed.
- **Health endpoint:** `GET /api/health` on the node — 200 = healthy, 503 = unhealthy. It also exercises the DB, so a 200 confirms the local DB is reachable.
- **Node binds `127.0.0.1:1234`.** nginx is the only public listener: 27701 → `127.0.0.1:1234`, 27703 → static `.ts` segments from `/srv/ticolinea/streams`.
- **MariaDB is loopback-only** (`127.0.0.1`), database `<slug>-streaming`, app user `streamingservice`, password generated on the box into `/opt/ticolinea/secrets/db-password` (0600 root). No firewall rule for MySQL, ever.
- **Schema** is the panel's EF migrations rendered idempotently: `dotnet ef migrations script --idempotent`. Applied with `mysql < schema.sql`. Re-applying must be a no-op.
- **No secret is committed.** The shared `Jwt.PublicKey` and `Jwt.PanelApiKey` come from a gitignored `deploy/secrets/shared.env`; per-provider files and `deploy/secrets/` are gitignored.
- **Idempotent.** `bootstrap` must be safe to run twice and after a half-failed run.
- **Deploy is manual**, run by an operator over the tunnel. CI builds and uploads only; it never deploys.
- **`probe` mutates nothing.** It only reads and reports.
- Plan/spec source of truth: `docs/superpowers/specs/2026-07-14-provider-node-provisioning-design.md`.

---

## File Structure

```
deploy/
  tico                                  # entrypoint dispatcher (chmod +x)
  lib/
    common.sh                           # logging, die, set -euo pipefail bootstrap
    config.sh                           # provider config load + validation (pure)
    template.sh                         # render_template (pure)
    ports.sh                            # build_ports_report (pure)
    ffmpeg.sh                           # ffmpeg_version_warning (pure)
    remote.sh                           # injectable ssh/rsync/scp runner (effectful)
    commands/
      probe.sh                          # cmd_probe
      bootstrap.sh                      # cmd_bootstrap
      deploy.sh                         # cmd_deploy (+ verify/rollback helpers)
      rollback.sh                       # cmd_rollback
      status.sh                         # cmd_status
      ports.sh                          # cmd_ports (thin wrapper over lib/ports.sh)
  templates/
    appsettings.provider.json.tmpl
    ticolinea-streaming.service.tmpl
    nginx-node.conf.tmpl
  providers/
    .gitignore                          # *  (ignore all real provider configs)
    example.conf                        # committed reference
  secrets/
    .gitignore                          # *  (ignore everything here)
    shared.env.example                  # committed reference
  tests/
    helpers.bash                        # bats setup + mock runner
    config.bats
    template.bats
    ports.bats
    ffmpeg.bats
    deploy.bats                         # mock-runner orchestration tests
  RUNBOOK.md
VERSION                                 # semver, repo root
.github/workflows/build-node.yml
```

Provider config (`deploy/providers/<slug>.conf`) is a flat `KEY=value` file (never sourced as bash — parsed, so a config can't execute code). Keys:

```
SSH_HOST=10.8.0.4            # node address over WireGuard
SSH_USER=ubuntu             # sudo-capable
PROVIDER=acme               # slug; must match normalized provider_name in panel
PROVIDER_NAME=Acme TV
PUBLIC_HOST=iptv.acme.cr    # public hostname clients hit
SEGMENT_BASE_URL=http://iptv.acme.cr:27703/   # SEE Task 3 note — confirm per client
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2   # baseline for probe drift warning; from `ffmpeg -version` on main
```

---

### Task 1: Scaffold `deploy/`, the `tico` dispatcher, and the test/lint toolchain

**Files:**
- Create: `deploy/tico`
- Create: `deploy/lib/common.sh`
- Create: `deploy/providers/.gitignore`, `deploy/providers/example.conf`
- Create: `deploy/secrets/.gitignore`, `deploy/secrets/shared.env.example`
- Create: `deploy/tests/helpers.bash`
- Create: `VERSION`

**Interfaces:**
- Produces: `deploy/tico <command> <slug> [args]` dispatcher; `common.sh` providing `log`, `warn`, `die`, `TICO_ROOT`.
- Produces: `deploy/tests/helpers.bash` providing `load_lib <name>` and the `mock_runner` recorder used by later tasks.

- [ ] **Step 1: Install the dev toolchain**

Run:
```bash
brew install bats-core shellcheck
bats --version && shellcheck --version
```
Expected: versions print (bats ≥ 1.10, shellcheck ≥ 0.9).

- [ ] **Step 2: Create `VERSION` and `common.sh`**

`VERSION`:
```
1.0.0
```

`deploy/lib/common.sh`:
```bash
# shellcheck shell=bash
# Sourced by tico and by tests. No side effects beyond defining functions/vars.
: "${TICO_ROOT:="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"}"

log()  { printf '\033[0;34m[tico]\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[0;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }
```

- [ ] **Step 3: Create the `tico` dispatcher**

`deploy/tico`:
```bash
#!/usr/bin/env bash
set -euo pipefail

TICO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export TICO_ROOT
# shellcheck source=lib/common.sh
source "$TICO_ROOT/lib/common.sh"

usage() {
  cat >&2 <<'EOF'
Usage: tico <command> <slug> [options]

Commands:
  probe     <slug>              Report the target server; changes nothing
  bootstrap <slug>              Idempotently provision the server
  deploy    <slug> --tag <v>    Deploy a release; auto-rolls-back on failure
  rollback  <slug>              Revert to the previous release
  status    <slug>              Health, stream count, uptime, current release
  ports     <slug>              Print the firewall request for this client
EOF
  exit 2
}

main() {
  [ $# -ge 1 ] || usage
  local cmd="$1"; shift
  case "$cmd" in
    probe|bootstrap|deploy|rollback|status|ports)
      # shellcheck source=/dev/null
      source "$TICO_ROOT/lib/commands/$cmd.sh"
      "cmd_$cmd" "$@"
      ;;
    -h|--help|help) usage ;;
    *) die "unknown command: $cmd (try: tico help)" ;;
  esac
}

main "$@"
```

Run:
```bash
chmod +x deploy/tico
./deploy/tico help; echo "exit=$?"
```
Expected: usage prints, `exit=2`.

- [ ] **Step 4: Create gitignores and committed examples**

`deploy/providers/.gitignore`:
```
*
!.gitignore
!example.conf
```

`deploy/providers/example.conf`:
```
SSH_HOST=10.8.0.4
SSH_USER=ubuntu
PROVIDER=acme
PROVIDER_NAME=Acme TV
PUBLIC_HOST=iptv.acme.cr
SEGMENT_BASE_URL=http://iptv.acme.cr:27703/
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
```

`deploy/secrets/.gitignore`:
```
*
!.gitignore
!shared.env.example
```

`deploy/secrets/shared.env.example`:
```
# Copy to shared.env and fill from the panel. shared.env is gitignored.
# JWT public key (PEM, newlines as \n) — matches ticolinea.panel's signing key.
JWT_PUBLIC_KEY=-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n
# Shared node<->panel API key.
PANEL_API_KEY=replace-me
```

- [ ] **Step 5: Create the test helper**

`deploy/tests/helpers.bash`:
```bash
# shellcheck shell=bash
TICO_ROOT="$(cd "$(dirname "${BATS_TEST_FILENAME}")/.." && pwd)"
export TICO_ROOT

load_lib() { source "$TICO_ROOT/lib/$1"; }

# Mock runner: records every remote invocation into MOCK_CALLS, prints $MOCK_OUT.
# Used by deploy.bats to test orchestration without a live host.
mock_runner_reset() { MOCK_CALLS=(); MOCK_OUT=""; MOCK_FAIL_ON=""; }
mock_runner() {
  local host="$1"; shift
  MOCK_CALLS+=("$*")
  if [ -n "$MOCK_FAIL_ON" ] && [[ "$*" == *"$MOCK_FAIL_ON"* ]]; then
    return 1
  fi
  printf '%s' "$MOCK_OUT"
}
mock_calls_joined() { printf '%s\n' "${MOCK_CALLS[@]}"; }
```

- [ ] **Step 6: Lint and commit**

Run:
```bash
shellcheck deploy/tico deploy/lib/common.sh
```
Expected: no output (clean).

```bash
git add deploy/ VERSION
git commit -m "feat(deploy): scaffold tico dispatcher, toolchain, gitignored config/secrets"
```

---

### Task 2: Provider config loading and validation (`lib/config.sh`)

**Files:**
- Create: `deploy/lib/config.sh`
- Test: `deploy/tests/config.bats`

**Interfaces:**
- Consumes: `common.sh` (`die`).
- Produces:
  - `config_load <slug>` — parses `deploy/providers/<slug>.conf`, exports each `KEY`, validates required keys, derives `DB_NAME="<PROVIDER>-streaming"` and `DB_USER="streamingservice"`. `die`s on missing file or missing required key.
  - `config_get <slug> <key>` — echoes a single value without exporting (pure read).

- [ ] **Step 1: Write the failing tests**

`deploy/tests/config.bats`:
```bash
#!/usr/bin/env bats
load helpers

setup() {
  load_lib common.sh
  load_lib config.sh
  TMP="$(mktemp -d)"
  CONF_DIR="$TICO_ROOT/providers"
  cat > "$TMP/good.conf" <<'EOF'
SSH_HOST=10.8.0.4
SSH_USER=ubuntu
PROVIDER=acme
PROVIDER_NAME=Acme TV
PUBLIC_HOST=iptv.acme.cr
SEGMENT_BASE_URL=http://iptv.acme.cr:27703/
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
EOF
}
teardown() { rm -rf "$TMP"; }

@test "config_get reads one value without exporting" {
  run config_get_file "$TMP/good.conf" PROVIDER
  [ "$status" -eq 0 ]
  [ "$output" = "acme" ]
}

@test "config_load derives DB_NAME and DB_USER" {
  config_load_file "$TMP/good.conf"
  [ "$DB_NAME" = "acme-streaming" ]
  [ "$DB_USER" = "streamingservice" ]
  [ "$SSH_HOST" = "10.8.0.4" ]
}

@test "config_load dies on missing required key" {
  grep -v '^PROVIDER=' "$TMP/good.conf" > "$TMP/bad.conf"
  run config_load_file "$TMP/bad.conf"
  [ "$status" -ne 0 ]
  [[ "$output" == *"PROVIDER"* ]]
}

@test "config_get ignores comments and blank lines" {
  printf '\n# a comment\nPROVIDER=acme\n' > "$TMP/c.conf"
  run config_get_file "$TMP/c.conf" PROVIDER
  [ "$output" = "acme" ]
}
```

*(Note: tests target `*_file` variants that take an explicit path; the slug-based `config_load`/`config_get` are thin wrappers resolving `providers/<slug>.conf`, tested in the VM integration task.)*

- [ ] **Step 2: Run to verify failure**

Run: `bats deploy/tests/config.bats`
Expected: FAIL — `config_get_file: command not found`.

- [ ] **Step 3: Implement `lib/config.sh`**

`deploy/lib/config.sh`:
```bash
# shellcheck shell=bash
CONFIG_REQUIRED_KEYS=(SSH_HOST SSH_USER PROVIDER PROVIDER_NAME PUBLIC_HOST SEGMENT_BASE_URL PANEL_API_URL MAIN_FFMPEG_VERSION)

config_get_file() { # file, key
  local file="$1" key="$2"
  [ -f "$file" ] || die "config not found: $file"
  # Match KEY=..., ignore comments/blanks, take value after first '='.
  local line
  line="$(grep -E "^${key}=" "$file" | head -n1)" || true
  [ -n "$line" ] || return 1
  printf '%s' "${line#*=}"
}

config_load_file() { # file  -> exports all keys + derived
  local file="$1" key val
  [ -f "$file" ] || die "config not found: $file"
  for key in "${CONFIG_REQUIRED_KEYS[@]}"; do
    if ! val="$(config_get_file "$file" "$key")"; then
      die "config $file: missing required key $key"
    fi
    export "$key=$val"
  done
  export DB_NAME="${PROVIDER}-streaming"
  export DB_USER="streamingservice"
}

_provider_conf() { printf '%s/providers/%s.conf' "$TICO_ROOT" "$1"; }
config_get()  { config_get_file  "$(_provider_conf "$1")" "$2"; }
config_load() { config_load_file "$(_provider_conf "$1")"; }
```

- [ ] **Step 4: Run to verify pass**

Run: `bats deploy/tests/config.bats`
Expected: 4 passing.

- [ ] **Step 5: Lint and commit**

Run: `shellcheck deploy/lib/config.sh`
Expected: clean.
```bash
git add deploy/lib/config.sh deploy/tests/config.bats
git commit -m "feat(deploy): provider config loading and validation"
```

---

### Task 3: Template rendering + the three templates (`lib/template.sh`, `templates/`)

**Files:**
- Create: `deploy/lib/template.sh`
- Create: `deploy/templates/appsettings.provider.json.tmpl`
- Create: `deploy/templates/ticolinea-streaming.service.tmpl`
- Create: `deploy/templates/nginx-node.conf.tmpl`
- Test: `deploy/tests/template.bats`

**Interfaces:**
- Consumes: exported vars from `config_load` plus `DB_PASSWORD`, `JWT_PUBLIC_KEY`, `PANEL_API_KEY`, `RELEASE_TAG`.
- Produces: `render_template <template-path>` — echoes the template with `${VAR}` placeholders substituted from the environment, failing if any placeholder is unset.

> **Note — SegmentBaseUrl discrepancy (verified):** `appsettings.fibraencasa.json` sets `SegmentBaseUrl` to `:27701/`, but `main` serves `.ts` segments via nginx on `:27703`. These disagree between the two production nodes. This plan does **not** guess: `SEGMENT_BASE_URL` is a required per-provider config value (Task 2). Default the `example.conf` to `:27703/` (matching main's segment-serving nginx), and confirm the correct value with the operator per client before first deploy.

- [ ] **Step 1: Write the failing tests**

`deploy/tests/template.bats`:
```bash
#!/usr/bin/env bats
load helpers

setup() {
  load_lib common.sh
  load_lib template.sh
  TMP="$(mktemp -d)"
}
teardown() { rm -rf "$TMP"; }

@test "render_template substitutes set vars" {
  echo 'hello ${PROVIDER_NAME} on ${PUBLIC_HOST}' > "$TMP/t.tmpl"
  PROVIDER_NAME="Acme TV" PUBLIC_HOST="iptv.acme.cr" run render_template "$TMP/t.tmpl"
  [ "$status" -eq 0 ]
  [ "$output" = "hello Acme TV on iptv.acme.cr" ]
}

@test "render_template fails on an unset placeholder" {
  echo 'db=${DB_PASSWORD}' > "$TMP/t.tmpl"
  run render_template "$TMP/t.tmpl"
  [ "$status" -ne 0 ]
  [[ "$output" == *"DB_PASSWORD"* ]]
}

@test "appsettings template renders valid JSON with local DB" {
  export SSH_HOST=x SSH_USER=x PROVIDER=acme PROVIDER_NAME="Acme TV" \
         PUBLIC_HOST=iptv.acme.cr SEGMENT_BASE_URL="http://iptv.acme.cr:27703/" \
         PANEL_API_URL="http://tv.play-latino.com:27702/api/v2" \
         DB_NAME=acme-streaming DB_USER=streamingservice DB_PASSWORD=s3cret \
         JWT_PUBLIC_KEY='-----BEGIN PUBLIC KEY-----\nAAA\n-----END PUBLIC KEY-----\n' \
         PANEL_API_KEY=apikey123
  render_template "$TICO_ROOT/templates/appsettings.provider.json.tmpl" > "$TMP/out.json"
  run node -e "JSON.parse(require('fs').readFileSync('$TMP/out.json','utf8'))"
  [ "$status" -eq 0 ]
  grep -q 'server=127.0.0.1' "$TMP/out.json"
  grep -q '"ProviderId": "acme"' "$TMP/out.json"
  ! grep -q 'rds.amazonaws.com' "$TMP/out.json"
}
```

- [ ] **Step 2: Run to verify failure**

Run: `bats deploy/tests/template.bats`
Expected: FAIL — `render_template: command not found`.

- [ ] **Step 3: Implement `lib/template.sh`**

`deploy/lib/template.sh`:
```bash
# shellcheck shell=bash
# Renders ${VAR} placeholders from the environment. Fails if any are unset.
render_template() { # template-path
  local tmpl="$1"
  [ -f "$tmpl" ] || die "template not found: $tmpl"
  # Collect referenced ${VAR} names; verify each is set before substituting.
  local missing=() name
  while IFS= read -r name; do
    if [ -z "${!name+x}" ]; then missing+=("$name"); fi
  done < <(grep -oE '\$\{[A-Z_][A-Z0-9_]*\}' "$tmpl" | sed -E 's/\$\{([A-Z0-9_]+)\}/\1/' | sort -u)
  if [ "${#missing[@]}" -gt 0 ]; then
    die "template $tmpl: unset variables: ${missing[*]}"
  fi
  # Substitute only the exact set of names we validated (no eval of file contents).
  local out; out="$(cat "$tmpl")"
  for name in $(grep -oE '\$\{[A-Z_][A-Z0-9_]*\}' "$tmpl" | sed -E 's/\$\{([A-Z0-9_]+)\}/\1/' | sort -u); do
    out="${out//\$\{$name\}/${!name}}"
  done
  printf '%s\n' "$out"
}
```

- [ ] **Step 4: Create `templates/appsettings.provider.json.tmpl`**

Mirrors `appsettings.fibraencasa.json`; Database → local, Streaming → per-provider, Jwt → shared (from secrets). Folders live under `/srv/ticolinea`.
```json
{
  "Database": {
    "ConnectionString": "server=127.0.0.1;Port=3306;uid=${DB_USER};pwd=${DB_PASSWORD};database=${DB_NAME};Allow User Variables=True;SSLMode=None;Pooling=true;Min Pool Size=50;Max Pool Size=500;Connection Lifetime=0;AllowPublicKeyRetrieval=true"
  },
  "Streaming": {
    "ProviderId": "${PROVIDER}",
    "ProviderName": "${PROVIDER_NAME}",
    "StreamsFolder": "/srv/ticolinea/streams/",
    "EpgFolder": "/srv/ticolinea/epg/",
    "MoviesFolder": "/srv/ticolinea/movies/",
    "SeriesFolder": "/srv/ticolinea/series/",
    "MoviesRawFolder": "/srv/ticolinea/raw-movies/",
    "FfmpegPath": "ffmpeg",
    "FfprobePath": "ffprobe",
    "EnableStreamExecution": true,
    "EnableFfmpegProcesses": true,
    "EnableStreamManagement": true,
    "SegmentBaseUrl": "${SEGMENT_BASE_URL}",
    "StreamsBaseUrl": "http://${PUBLIC_HOST}:27701"
  },
  "Jwt": {
    "Issuer": "ticolinea.panel",
    "Audience": "streaming-node",
    "PublicKey": "${JWT_PUBLIC_KEY}",
    "NodeProviderId": "${PROVIDER}",
    "PanelApiUrl": "${PANEL_API_URL}",
    "PanelApiKey": "${PANEL_API_KEY}",
    "IntrospectCacheSeconds": 60,
    "AccessTokenExpiryMinutes": 60,
    "RefreshTokenExpiryDays": 364
  }
}
```

- [ ] **Step 5: Create `templates/ticolinea-streaming.service.tmpl`**

```ini
[Unit]
Description=Ticolinea Streaming Node (${PROVIDER})
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
Environment=PROVIDER=${PROVIDER}

LimitNOFILE=65535
TasksMax=4096

[Install]
WantedBy=multi-user.target
```

- [ ] **Step 6: Create `templates/nginx-node.conf.tmpl`**

Reproduces production verbatim except `root` → `/srv/ticolinea`.
```nginx
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

server {
    listen 27703;
    root /srv/ticolinea;

    location ~ ^/streams/.*\.ts$ {
        types { video/mp2t ts; }
        add_header Access-Control-Allow-Origin * always;
        add_header Cache-Control "public, max-age=60, s-maxage=60, immutable" always;
    }
    location / { return 404; }
}
```
*(This template has no `${VAR}` placeholders — it is uploaded as-is. Kept in `templates/` for colocation.)*

- [ ] **Step 7: Run tests + lint + commit**

Run: `bats deploy/tests/template.bats`
Expected: 3 passing. (Requires `node` for the JSON-parse assertion; it is present per environment.)

Run: `shellcheck deploy/lib/template.sh`
Expected: clean.
```bash
git add deploy/lib/template.sh deploy/templates deploy/tests/template.bats
git commit -m "feat(deploy): config/systemd/nginx templates and safe renderer"
```

---

### Task 4: The ports report (`lib/ports.sh` + `cmd_ports`)

**Files:**
- Create: `deploy/lib/ports.sh`
- Create: `deploy/lib/commands/ports.sh`
- Test: `deploy/tests/ports.bats`

**Interfaces:**
- Consumes: `config_load` exports (`PUBLIC_HOST`, `PANEL_API_URL`).
- Produces: `build_ports_report` — echoes the firewall request text. `cmd_ports <slug>` — loads config, prints it.

- [ ] **Step 1: Write the failing test**

`deploy/tests/ports.bats`:
```bash
#!/usr/bin/env bats
load helpers

setup() { load_lib common.sh; load_lib ports.sh; }

@test "ports report names the public inbound ports and the outbound origin" {
  PUBLIC_HOST=iptv.acme.cr run build_ports_report
  [ "$status" -eq 0 ]
  [[ "$output" == *"27701/tcp"* ]]
  [[ "$output" == *"27703/tcp"* ]]
  [[ "$output" == *"51820/udp"* ]]
  [[ "$output" == *"tv.play-latino.com:27702"* ]]
  [[ "$output" == *"tv.play-latino.com:27701"* ]]
  [[ "$output" == *"MySQL"* ]]
}
```

- [ ] **Step 2: Run to verify failure**

Run: `bats deploy/tests/ports.bats`
Expected: FAIL — `build_ports_report: command not found`.

- [ ] **Step 3: Implement `lib/ports.sh`**

```bash
# shellcheck shell=bash
build_ports_report() {
  cat <<EOF
Firewall request for provider node ${PUBLIC_HOST:-<host>}

INBOUND — public (open to the internet):
  27701/tcp   nginx -> node. Playlists / HLS API. End-user devices.
  27703/tcp   nginx -> static .ts segments. The actual video bytes.

INBOUND — tunnel only (over WireGuard, not public):
  22/tcp      SSH for operator deploy/admin.

INBOUND — client side:
  51820/udp   WireGuard, so the operator can peer in.

OUTBOUND (the node must be able to reach):
  tv.play-latino.com:27702   Panel API — token introspect/refresh + activity.
  tv.play-latino.com:27701   Restream source pull — the content path.

NEVER open:
  MySQL/MariaDB — loopback only (127.0.0.1). No firewall rule, ever.
EOF
}
```

- [ ] **Step 4: Implement `cmd_ports`**

`deploy/lib/commands/ports.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/ports.sh"
cmd_ports() {
  [ $# -ge 1 ] || die "usage: tico ports <slug>"
  config_load "$1"
  build_ports_report
}
```

- [ ] **Step 5: Run + lint + commit**

Run: `bats deploy/tests/ports.bats`
Expected: 1 passing.
Run: `shellcheck deploy/lib/ports.sh deploy/lib/commands/ports.sh`
Expected: clean.
```bash
git add deploy/lib/ports.sh deploy/lib/commands/ports.sh deploy/tests/ports.bats
git commit -m "feat(deploy): tico ports — firewall request generator"
```

---

### Task 5: FFmpeg version drift warning (`lib/ffmpeg.sh`)

**Files:**
- Create: `deploy/lib/ffmpeg.sh`
- Test: `deploy/tests/ffmpeg.bats`

**Interfaces:**
- Produces:
  - `ffmpeg_parse_version <raw>` — extracts `X.Y.Z` (or `X.Y`) from `ffmpeg -version` output.
  - `ffmpeg_version_warning <client_version> <main_version>` — echoes a warning string if they differ, empty if equal. Never fails the run.

- [ ] **Step 1: Write the failing tests**

`deploy/tests/ffmpeg.bats`:
```bash
#!/usr/bin/env bats
load helpers

setup() { load_lib common.sh; load_lib ffmpeg.sh; }

@test "parses version from ffmpeg -version banner" {
  run ffmpeg_parse_version "ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright (c) 2000-2021"
  [ "$output" = "4.4.2" ]
}

@test "no warning when versions match" {
  run ffmpeg_version_warning "4.4.2" "4.4.2"
  [ "$status" -eq 0 ]
  [ -z "$output" ]
}

@test "warns when versions differ" {
  run ffmpeg_version_warning "6.1.1" "4.4.2"
  [ "$status" -eq 0 ]
  [[ "$output" == *"differs"* ]]
  [[ "$output" == *"6.1.1"* ]]
  [[ "$output" == *"4.4.2"* ]]
}
```

- [ ] **Step 2: Run to verify failure**

Run: `bats deploy/tests/ffmpeg.bats`
Expected: FAIL — `ffmpeg_parse_version: command not found`.

- [ ] **Step 3: Implement `lib/ffmpeg.sh`**

```bash
# shellcheck shell=bash
ffmpeg_parse_version() { # raw banner
  printf '%s' "$1" | grep -oE 'version [0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1 \
    | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?'
}

ffmpeg_version_warning() { # client, main
  local client="$1" main="$2"
  [ "$client" = "$main" ] && return 0
  printf 'FFmpeg version on client (%s) differs from main (%s) — HLS muxing behavior may differ; validate streams before go-live.' \
    "$client" "$main"
}
```

- [ ] **Step 4: Run + lint + commit**

Run: `bats deploy/tests/ffmpeg.bats`
Expected: 3 passing.
Run: `shellcheck deploy/lib/ffmpeg.sh`
Expected: clean.
```bash
git add deploy/lib/ffmpeg.sh deploy/tests/ffmpeg.bats
git commit -m "feat(deploy): ffmpeg version drift warning"
```

---

### Task 6: Injectable remote runner (`lib/remote.sh`)

**Files:**
- Create: `deploy/lib/remote.sh`
- Test: covered by `deploy/tests/deploy.bats` in Task 9 (via the mock runner).

**Interfaces:**
- Consumes: `SSH_HOST`, `SSH_USER` (from `config_load`); optional `TICO_SSH_OPTS`.
- Produces:
  - `TICO_RUNNER` — indirection point (defaults to real ssh; tests override with `mock_runner`).
  - `remote <command...>` — run a command on the node.
  - `remote_sudo <command...>` — run via `sudo`.
  - `push <local> <remote-path>` — rsync a file/dir to the node.
  - `remote_health` — echo the node's `/api/health` HTTP status code.
  - `remote_fresh_stream_count` — count `.ts` segments modified in the last minute.

- [ ] **Step 1: Implement `lib/remote.sh`**

```bash
# shellcheck shell=bash
: "${TICO_RUNNER:=_tico_real_runner}"
: "${TICO_SSH_OPTS:=-o BatchMode=yes -o ConnectTimeout=10}"

_tico_real_runner() { # host, command...
  local host="$1"; shift
  # shellcheck disable=SC2086
  ssh $TICO_SSH_OPTS "$host" -- "$@"
}

remote()      { "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" "$@"; }
remote_sudo() { "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" sudo "$@"; }

push() { # local, remote-path
  rsync -az -e "ssh $TICO_SSH_OPTS" "$1" "${SSH_USER}@${SSH_HOST}:$2"
}

remote_health() {
  remote 'curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:1234/api/health || true'
}

remote_fresh_stream_count() {
  remote 'find /srv/ticolinea/streams -name "*.ts" -mmin -1 2>/dev/null | wc -l | tr -d " "'
}
```

- [ ] **Step 2: Lint and commit**

Run: `shellcheck deploy/lib/remote.sh`
Expected: clean (the `SC2086` on `$TICO_SSH_OPTS` is intentionally disabled — word-splitting is desired for ssh options).
```bash
git add deploy/lib/remote.sh
git commit -m "feat(deploy): injectable ssh/rsync remote runner"
```

---

### Task 7: `tico probe` — read-only server report

**Files:**
- Create: `deploy/lib/commands/probe.sh`

**Interfaces:**
- Consumes: `config_load`, `remote`, `ffmpeg_parse_version`, `ffmpeg_version_warning`.
- Produces: `cmd_probe <slug>` — prints distro, CPU, disk, docker presence, ffmpeg version (+drift warning), aspnetcore-runtime presence, and outbound reachability of the panel and origin. Mutates nothing.

- [ ] **Step 1: Implement `cmd_probe`**

`deploy/lib/commands/probe.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/remote.sh"
source "$TICO_ROOT/lib/ffmpeg.sh"

cmd_probe() {
  [ $# -ge 1 ] || die "usage: tico probe <slug>"
  config_load "$1"
  log "Probing ${SSH_USER}@${SSH_HOST} (provider: $PROVIDER) — read-only"

  echo "== OS =="
  remote 'cat /etc/os-release | grep -E "^(NAME|VERSION)=" || true'

  echo "== CPU / RAM / Disk =="
  remote 'nproc; free -h | awk "/Mem:/{print \$2\" RAM\"}"; df -h /srv 2>/dev/null || df -h /'

  echo "== ffmpeg =="
  local banner client
  banner="$(remote 'ffmpeg -version 2>/dev/null | head -n1 || echo none')"
  echo "$banner"
  client="$(ffmpeg_parse_version "$banner")"
  if [ -n "$client" ]; then
    local w; w="$(ffmpeg_version_warning "$client" "$MAIN_FFMPEG_VERSION")"
    [ -n "$w" ] && warn "$w"
  else
    warn "ffmpeg not installed (bootstrap will install it)"
  fi

  echo "== aspnetcore-runtime-6.0 =="
  remote 'dotnet --list-runtimes 2>/dev/null | grep -i "AspNetCore.*6\." || echo "not installed (bootstrap will install)"'

  echo "== outbound reachability =="
  remote 'curl -s -o /dev/null -w "panel(27702): %{http_code}\n" --max-time 8 http://tv.play-latino.com:27702/ || echo "panel(27702): UNREACHABLE"'
  remote 'curl -s -o /dev/null -w "origin(27701): %{http_code}\n" --max-time 8 http://tv.play-latino.com:27701/ || echo "origin(27701): UNREACHABLE"'

  log "Probe complete. Nothing was changed."
}
```

- [ ] **Step 2: Lint and commit**

Run: `shellcheck -x deploy/lib/commands/probe.sh`
Expected: clean.
```bash
git add deploy/lib/commands/probe.sh
git commit -m "feat(deploy): tico probe — read-only server report"
```
*(Behavioral verification of `probe` happens against the VM in Task 12.)*

---

### Task 8: `tico bootstrap` — idempotent provisioning

**Files:**
- Create: `deploy/lib/commands/bootstrap.sh`

**Interfaces:**
- Consumes: `config_load`, `render_template`, `remote`, `remote_sudo`, `push`; `deploy/secrets/shared.env` (`JWT_PUBLIC_KEY`, `PANEL_API_KEY`).
- Produces: `cmd_bootstrap <slug>` — an idempotent sequence of ordered helpers. On success the box has: Ubuntu 22.04 verified, `aspnetcore-runtime-6.0`, `nginx`, `mariadb-server` (loopback), `ffmpeg` (held), user `ticolinea`, the `/opt/ticolinea` and `/srv/ticolinea` trees, the `<slug>-streaming` DB + `streamingservice` user with a generated password, the rendered `appsettings.<slug>.json`, nginx config, and the systemd unit (not yet started — `deploy` starts it once a release exists).

Each helper is written to be safe to re-run (guards before mutate).

- [ ] **Step 1: Implement `cmd_bootstrap`**

`deploy/lib/commands/bootstrap.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/template.sh"
source "$TICO_ROOT/lib/remote.sh"

_load_shared_secrets() {
  local f="$TICO_ROOT/secrets/shared.env"
  [ -f "$f" ] || die "missing $f (copy secrets/shared.env.example and fill it)"
  # shellcheck disable=SC1090
  set -a; source "$f"; set +a
  [ -n "${JWT_PUBLIC_KEY:-}" ] || die "shared.env: JWT_PUBLIC_KEY unset"
  [ -n "${PANEL_API_KEY:-}" ] || die "shared.env: PANEL_API_KEY unset"
}

_assert_ubuntu2204() {
  local v; v="$(remote 'source /etc/os-release; echo "$ID $VERSION_ID"')"
  [ "$v" = "ubuntu 22.04" ] || die "target is '$v'; this tool requires Ubuntu 22.04"
}

_install_packages() {
  log "Installing base packages (idempotent)"
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
if ! dpkg -s aspnetcore-runtime-6.0 >/dev/null 2>&1; then
  wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/ms-prod.deb
  dpkg -i /tmp/ms-prod.deb
  apt-get update -qq
  apt-get install -y -qq aspnetcore-runtime-6.0
fi
apt-get install -y -qq nginx mariadb-server ffmpeg rsync curl
apt-mark hold ffmpeg
REMOTE
  remote 'dotnet --list-runtimes | grep -qi "AspNetCore.*6\." ' \
    || die "aspnetcore-runtime-6.0 not available after install — see RUNBOOK (self-contained fallback)"
}

_create_user_and_dirs() {
  log "Creating service user and directory tree (idempotent)"
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
id ticolinea >/dev/null 2>&1 || useradd -r -m -d /home/ticolinea -s /usr/sbin/nologin ticolinea
mkdir -p /opt/ticolinea/releases /opt/ticolinea/config /opt/ticolinea/nginx /opt/ticolinea/secrets
mkdir -p /srv/ticolinea/streams /srv/ticolinea/epg /srv/ticolinea/movies /srv/ticolinea/series /srv/ticolinea/raw-movies /srv/ticolinea/logs
chown -R ticolinea:ticolinea /opt/ticolinea /srv/ticolinea
chmod 700 /opt/ticolinea/secrets
REMOTE
}

_setup_mariadb() {
  log "Configuring MariaDB (loopback) + database + app user (idempotent)"
  # Bind loopback.
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
sed -ri 's/^#?bind-address\s*=.*/bind-address = 127.0.0.1/' /etc/mysql/mariadb.conf.d/50-server.cnf
systemctl enable --now mariadb
REMOTE
  # Generate DB password once; store on box 0600.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
SECRET=/opt/ticolinea/secrets/db-password
if [ ! -s "\$SECRET" ]; then
  head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 32 > "\$SECRET"
  chmod 600 "\$SECRET"; chown ticolinea:ticolinea "\$SECRET"
fi
DBPASS="\$(cat \$SECRET)"
mysql -uroot <<SQL
CREATE DATABASE IF NOT EXISTS \\\`${DB_NAME}\\\`;
CREATE USER IF NOT EXISTS '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '\${DBPASS}';
ALTER USER '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '\${DBPASS}';
GRANT ALL PRIVILEGES ON \\\`${DB_NAME}\\\`.* TO '${DB_USER}'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
REMOTE
}

_render_and_upload_config() {
  log "Rendering and uploading appsettings + nginx + systemd unit"
  _load_shared_secrets
  export DB_PASSWORD; DB_PASSWORD="$(remote_sudo cat /opt/ticolinea/secrets/db-password)"
  local tmp; tmp="$(mktemp -d)"
  render_template "$TICO_ROOT/templates/appsettings.provider.json.tmpl" > "$tmp/appsettings.$PROVIDER.json"
  cp "$TICO_ROOT/templates/nginx-node.conf.tmpl" "$tmp/node.conf"
  render_template "$TICO_ROOT/templates/ticolinea-streaming.service.tmpl" > "$tmp/ticolinea-streaming.service"

  push "$tmp/appsettings.$PROVIDER.json" "/tmp/appsettings.$PROVIDER.json"
  push "$tmp/node.conf" "/tmp/node.conf"
  push "$tmp/ticolinea-streaming.service" "/tmp/ticolinea-streaming.service"
  rm -rf "$tmp"

  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
install -o ticolinea -g ticolinea -m 640 /tmp/appsettings.$PROVIDER.json /opt/ticolinea/config/appsettings.$PROVIDER.json
install -m 644 /tmp/node.conf /etc/nginx/sites-available/ticolinea-node.conf
ln -sfn /etc/nginx/sites-available/ticolinea-node.conf /etc/nginx/sites-enabled/ticolinea-node.conf
rm -f /etc/nginx/sites-enabled/default
install -m 644 /tmp/ticolinea-streaming.service /etc/systemd/system/ticolinea-streaming.service
rm -f /tmp/appsettings.$PROVIDER.json /tmp/node.conf /tmp/ticolinea-streaming.service
nginx -t
systemctl reload nginx
systemctl daemon-reload
systemctl enable ticolinea-streaming
REMOTE
}

_apply_schema() {
  log "Applying schema.sql (idempotent). Provide with --schema or place at releases dir on deploy."
  local schema="${TICO_SCHEMA_FILE:-}"
  if [ -z "$schema" ]; then
    warn "no schema file supplied to bootstrap; deploy will apply the one shipped in the release artifact"
    return 0
  fi
  push "$schema" "/tmp/schema.sql"
  remote_sudo "bash -c 'mysql ${DB_NAME} < /tmp/schema.sql && rm -f /tmp/schema.sql'"
}

cmd_bootstrap() {
  [ $# -ge 1 ] || die "usage: tico bootstrap <slug> [--schema path/to/schema.sql]"
  config_load "$1"; shift || true
  while [ $# -gt 0 ]; do case "$1" in
    --schema) TICO_SCHEMA_FILE="$2"; shift 2;;
    *) die "unknown option: $1";;
  esac; done

  _assert_ubuntu2204
  _install_packages
  _create_user_and_dirs
  _setup_mariadb
  _render_and_upload_config
  _apply_schema
  log "Bootstrap complete for $PROVIDER. Next: tico deploy $PROVIDER --tag <version>"
}
```

- [ ] **Step 2: Lint**

Run: `shellcheck -x deploy/lib/commands/bootstrap.sh`
Expected: clean (heredocs with remote-side `$VAR` may need `# shellcheck disable=SC2016` where single-quoted intentionally — add narrowly if flagged).

- [ ] **Step 3: Commit**

```bash
git add deploy/lib/commands/bootstrap.sh
git commit -m "feat(deploy): tico bootstrap — idempotent provisioning"
```
*(End-to-end idempotency is proven against the VM in Task 12.)*

---

### Task 9: `tico deploy` — preflight, swap, verify, auto-rollback

**Files:**
- Create: `deploy/lib/commands/deploy.sh`
- Test: `deploy/tests/deploy.bats`

**Interfaces:**
- Consumes: `config_load`, `remote`, `remote_sudo`, `push`, `remote_health`, `remote_fresh_stream_count`.
- Produces:
  - `cmd_deploy <slug> --tag <v> [--artifact <dir>] [--dry-run]`.
  - `deploy_verify` — returns 0 if `/api/health` is 200 AND fresh stream count ≥ baseline (or ≥1 on first deploy). Returns non-zero otherwise.
  - `deploy_rollback_to <previous-tag>` — repoint `current`, restart, used both by auto-rollback and Task 10.
- The deploy sequence follows §7 of the spec exactly: preflight (health green now, disk ok, artifact staged) → baseline → swap → verify → auto-rollback on failure.

- [ ] **Step 1: Write orchestration tests with the mock runner**

`deploy/tests/deploy.bats`:
```bash
#!/usr/bin/env bats
load helpers

setup() {
  load_lib common.sh
  load_lib remote.sh
  source "$TICO_ROOT/lib/commands/deploy.sh"
  mock_runner_reset
  TICO_RUNNER=mock_runner
  SSH_HOST=host SSH_USER=u PROVIDER=acme
}

@test "verify passes when health is 200 and streams are fresh" {
  MOCK_OUT="200"
  # health returns 200; stream count returns 200 too, but we stub separately:
  deploy_health() { echo 200; }
  deploy_fresh() { echo 5; }
  run deploy_verify 3
  [ "$status" -eq 0 ]
}

@test "verify fails when health is 503" {
  deploy_health() { echo 503; }
  deploy_fresh() { echo 5; }
  run deploy_verify 3
  [ "$status" -ne 0 ]
}

@test "verify fails when streams did not recover" {
  deploy_health() { echo 200; }
  deploy_fresh() { echo 0; }
  run deploy_verify 3
  [ "$status" -ne 0 ]
}

@test "auto-rollback repoints current to the previous tag on verify failure" {
  MOCK_OUT=""
  deploy_health() { echo 503; }   # force verify failure
  deploy_fresh() { echo 0; }
  PREVIOUS_TAG="1.0.0"
  run deploy_run_swap_and_verify "1.1.0" "1.0.0"
  [ "$status" -ne 0 ]
  # the previous tag must appear in a symlink command after failure
  mock_calls_joined | grep -q "releases/1.0.0"
}
```

- [ ] **Step 2: Run to verify failure**

Run: `bats deploy/tests/deploy.bats`
Expected: FAIL — functions not defined.

- [ ] **Step 3: Implement `cmd_deploy`**

`deploy/lib/commands/deploy.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/remote.sh"

# Seams so tests can stub the two observations independently of the runner.
deploy_health() { remote_health; }
deploy_fresh()  { remote_fresh_stream_count; }

deploy_verify() { # baseline_fresh_count
  local baseline="$1" tries=0 code fresh
  while [ "$tries" -lt 12 ]; do            # ~60s: 12 * 5s
    code="$(deploy_health)"
    if [ "$code" = "200" ]; then
      fresh="$(deploy_fresh)"
      local need=1; [ "$baseline" -gt 1 ] && need="$baseline"
      if [ "${fresh:-0}" -ge "$need" ]; then return 0; fi
    fi
    tries=$((tries+1)); sleep 5
  done
  return 1
}

deploy_rollback_to() { # previous_tag
  local prev="$1"
  [ -n "$prev" ] || die "no previous release to roll back to"
  warn "Rolling back to $prev"
  remote_sudo bash -c "ln -sfn /opt/ticolinea/releases/$prev /opt/ticolinea/current.tmp && mv -T /opt/ticolinea/current.tmp /opt/ticolinea/current && systemctl restart ticolinea-streaming"
}

deploy_run_swap_and_verify() { # new_tag, previous_tag
  local new="$1" prev="$2" baseline="${BASELINE_FRESH:-0}"
  remote_sudo bash -c "ln -sfn /opt/ticolinea/releases/$new /opt/ticolinea/current.tmp && mv -T /opt/ticolinea/current.tmp /opt/ticolinea/current && systemctl restart ticolinea-streaming"
  if deploy_verify "$baseline"; then
    log "Deploy $new verified."
    return 0
  fi
  warn "Verification failed for $new."
  deploy_rollback_to "$prev"
  return 1
}

cmd_deploy() {
  local slug="" tag="" artifact="" dry=0
  [ $# -ge 1 ] || die "usage: tico deploy <slug> --tag <v> [--artifact dir] [--dry-run]"
  slug="$1"; shift
  while [ $# -gt 0 ]; do case "$1" in
    --tag) tag="$2"; shift 2;;
    --artifact) artifact="$2"; shift 2;;
    --dry-run) dry=1; shift;;
    *) die "unknown option: $1";;
  esac; done
  [ -n "$tag" ] || die "--tag is required"
  config_load "$slug"

  # 1. Preflight — no downtime.
  log "Preflight: health, disk, staging release $tag"
  if [ "$(deploy_health)" != "200" ] && [ "$dry" -eq 0 ]; then
    remote 'systemctl is-active ticolinea-streaming' >/dev/null 2>&1 \
      && die "node is currently unhealthy — refusing to deploy onto a broken node" \
      || log "no running node yet (first deploy)"
  fi
  remote 'df --output=pcent /srv | tail -1 | tr -dc 0-9' | awk '{if ($1+0 > 90) exit 1}' \
    || die "disk on /srv is >90% full"

  # Stage the artifact into releases/<tag> while the old one still serves.
  [ -n "$artifact" ] || die "--artifact <dir> (unpacked release) is required"
  [ -f "$artifact/schema.sql" ] || die "artifact missing schema.sql"
  [ -f "$artifact/ticolinea.stream.service.dll" ] || die "artifact missing the published dll"
  if [ "$dry" -eq 1 ]; then log "[dry-run] would rsync $artifact -> releases/$tag and swap"; return 0; fi

  push "$artifact/" "/tmp/release-$tag/"
  remote_sudo bash -c "mkdir -p /opt/ticolinea/releases/$tag && cp -a /tmp/release-$tag/. /opt/ticolinea/releases/$tag/ && chown -R ticolinea:ticolinea /opt/ticolinea/releases/$tag && rm -rf /tmp/release-$tag && cp /opt/ticolinea/config/appsettings.$PROVIDER.json /opt/ticolinea/releases/$tag/ && cp /opt/ticolinea/config/appsettings.$PROVIDER.json /opt/ticolinea/releases/$tag/appsettings.Production.json 2>/dev/null || true"

  # Apply schema (idempotent) before swapping traffic.
  remote_sudo bash -c "mysql ${DB_NAME} < /opt/ticolinea/releases/$tag/schema.sql"

  # 2. Baseline.
  local previous baseline
  previous="$(remote 'readlink /opt/ticolinea/current 2>/dev/null | xargs -r basename || true')"
  baseline="$(deploy_fresh || echo 0)"; export BASELINE_FRESH="$baseline"
  log "Baseline: previous=${previous:-none}, fresh streams=${baseline}"

  # 3-5. Swap, verify, auto-rollback.
  if deploy_run_swap_and_verify "$tag" "$previous"; then
    remote_sudo bash -c "cd /opt/ticolinea/releases && ls -1dt */ | tail -n +6 | xargs -r rm -rf"  # keep last 5
    log "Deploy complete: $PROVIDER now on $tag"
  else
    die "Deploy failed and was rolled back to ${previous:-none}. Investigate before retrying."
  fi
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `bats deploy/tests/deploy.bats`
Expected: 4 passing.

- [ ] **Step 5: Lint and commit**

Run: `shellcheck -x deploy/lib/commands/deploy.sh`
Expected: clean.
```bash
git add deploy/lib/commands/deploy.sh deploy/tests/deploy.bats
git commit -m "feat(deploy): tico deploy — preflight, swap, verify, auto-rollback"
```

---

### Task 10: `tico rollback` and `tico status`

**Files:**
- Create: `deploy/lib/commands/rollback.sh`
- Create: `deploy/lib/commands/status.sh`

**Interfaces:**
- Consumes: `config_load`, `remote`, `remote_health`, `deploy_rollback_to` (from deploy.sh).
- Produces: `cmd_rollback <slug>` — repoint `current` to the previous release dir and restart. `cmd_status <slug>` — print health code, current release, uptime, and fresh stream count.

- [ ] **Step 1: Implement `cmd_rollback`**

`deploy/lib/commands/rollback.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/remote.sh"
source "$TICO_ROOT/lib/commands/deploy.sh"

cmd_rollback() {
  [ $# -ge 1 ] || die "usage: tico rollback <slug>"
  config_load "$1"
  local current previous
  current="$(remote 'readlink /opt/ticolinea/current | xargs basename')"
  previous="$(remote 'ls -1dt /opt/ticolinea/releases/*/ | grep -v "/'"$current"'/" | head -1 | xargs basename')"
  [ -n "$previous" ] || die "no previous release found to roll back to"
  log "Rolling back $PROVIDER: $current -> $previous"
  deploy_rollback_to "$previous"
  [ "$(remote_health)" = "200" ] && log "Rollback healthy." || warn "Node not healthy after rollback — see RUNBOOK."
}
```

- [ ] **Step 2: Implement `cmd_status`**

`deploy/lib/commands/status.sh`:
```bash
# shellcheck shell=bash
source "$TICO_ROOT/lib/config.sh"
source "$TICO_ROOT/lib/remote.sh"

cmd_status() {
  [ $# -ge 1 ] || die "usage: tico status <slug>"
  config_load "$1"
  echo "provider:      $PROVIDER ($SSH_HOST)"
  echo "health:        $(remote_health) (200=healthy)"
  echo "current:       $(remote 'readlink /opt/ticolinea/current 2>/dev/null | xargs -r basename || echo none')"
  echo "unit active:   $(remote 'systemctl is-active ticolinea-streaming 2>/dev/null || echo inactive')"
  echo "uptime(s):     $(remote 'systemctl show ticolinea-streaming -p ActiveEnterTimestampMonotonic --value 2>/dev/null | awk "{print int(\$1/1000000)}"')"
  echo "fresh streams: $(remote_fresh_stream_count)"
}
```

- [ ] **Step 3: Lint and commit**

Run: `shellcheck -x deploy/lib/commands/rollback.sh deploy/lib/commands/status.sh`
Expected: clean.
```bash
git add deploy/lib/commands/rollback.sh deploy/lib/commands/status.sh
git commit -m "feat(deploy): tico rollback and status"
```

---

### Task 11: GitHub Actions build workflow

**Files:**
- Create: `.github/workflows/build-node.yml`

**Interfaces:**
- Produces: on push to `master`, a release artifact containing the framework-dependent publish output plus `schema.sql`, named by `VERSION`.

- [ ] **Step 1: Create the workflow**

`.github/workflows/build-node.yml`:
```yaml
name: build-node
on:
  push:
    branches: [master]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout middleware
        uses: actions/checkout@v4

      - name: Checkout panel (for EF migrations)
        uses: actions/checkout@v4
        with:
          repository: allansalgadocr/ticolinea.panel
          ref: master
          path: _panel
          token: ${{ secrets.PANEL_REPO_TOKEN }}

      - name: Setup .NET 6
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '6.0.x'

      - name: Read version
        id: ver
        run: echo "version=$(cat VERSION)" >> "$GITHUB_OUTPUT"

      - name: Publish (framework-dependent)
        run: |
          dotnet publish Ticolinea.Streaming.Middleware/ticolinea.stream.service.csproj \
            -c Release -o publish

      - name: Generate idempotent schema
        run: |
          dotnet tool install --global dotnet-ef --version 6.*
          export PATH="$PATH:$HOME/.dotnet/tools"
          dotnet ef migrations script --idempotent \
            --project _panel/ticolinea.panel.Infrastructure \
            --startup-project _panel/ticolinea.panel.API \
            -o publish/schema.sql

      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: node-${{ steps.ver.outputs.version }}
          path: publish/
          retention-days: 30
```

- [ ] **Step 2: Validate YAML locally**

Run:
```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/build-node.yml')); print('valid')"
```
Expected: `valid`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/build-node.yml
git commit -m "ci: build node artifact (publish + idempotent schema) on master"
```
*(Prerequisite noted in RUNBOOK/spec: migrate this repo to GitHub and add `PANEL_REPO_TOKEN`. The EF `--project`/`--startup-project` paths must be confirmed against the panel repo layout during first CI run.)*

---

### Task 12: RUNBOOK.md

**Files:**
- Create: `deploy/RUNBOOK.md`

- [ ] **Step 1: Write the runbook**

`deploy/RUNBOOK.md`:
```markdown
# Ticolinea Provider Node — Runbook

## Onboard a new provider
1. Get WireGuard peering from the client; confirm you can `ssh <user>@<tunnel-ip>`.
2. `cp deploy/providers/example.conf deploy/providers/<slug>.conf` and fill it.
   Confirm `SEGMENT_BASE_URL` with the client — main uses :27703, fibraencasa's
   config shows :27701. Get the right one before first deploy.
3. `cp deploy/secrets/shared.env.example deploy/secrets/shared.env` and fill
   JWT_PUBLIC_KEY / PANEL_API_KEY from the panel.
4. `./deploy/tico probe <slug>` — read the report. Stop if it is not Ubuntu 22.04,
   if ffmpeg drift is flagged and unacceptable, or if outbound to
   tv.play-latino.com:27701/27702 is unreachable.
5. `./deploy/tico ports <slug>` — send the firewall request to the client.
6. `./deploy/tico bootstrap <slug>` — provision. Safe to re-run.
7. Register the provider in the panel (connection_url = http://<PUBLIC_HOST>:27701).
8. `./deploy/tico deploy <slug> --tag <version> --artifact <unpacked-artifact-dir>`.
9. A freshly-provisioned node has no channel rows yet (that is spec B). It will be
   healthy but serve nothing until the panel package sync exists or rows are seeded.

## Update a running client
- `./deploy/tico status <slug>` first. Do not update during prime-time viewing hours.
- `./deploy/tico deploy <slug> --tag <version> --artifact <dir>`.
- The tool stages, swaps, restarts, and verifies streams recovered within ~60s.
  On failure it auto-rolls-back. Viewers absorb ~30s (the HLS buffer) if it recovers
  in time.

## Roll back by hand (if the tool is broken)
```bash
ssh <user>@<tunnel-ip>
ls -1dt /opt/ticolinea/releases/*/          # find the previous good release
sudo ln -sfn /opt/ticolinea/releases/<prev> /opt/ticolinea/current.tmp
sudo mv -T /opt/ticolinea/current.tmp /opt/ticolinea/current
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
```

- [ ] **Step 2: Commit**

```bash
git add deploy/RUNBOOK.md
git commit -m "docs(deploy): provider node runbook"
```

---

### Task 13: End-to-end verification against a throwaway VM

**Files:**
- Create: `deploy/tests/integration.md` (manual checklist — VM provisioning is not run in CI)

**Interfaces:**
- Consumes: the whole tool.
- Produces: recorded evidence that `bootstrap` is idempotent, the schema applies cleanly, deploy/verify/rollback work, and MariaDB is not reachable off-box.

- [ ] **Step 1: Install multipass and launch a clean Ubuntu 22.04 VM**

Run:
```bash
brew install --cask multipass
multipass launch 22.04 --name tico-test --cpus 2 --memory 4G --disk 20G
multipass exec tico-test -- bash -c 'source /etc/os-release; echo $VERSION_ID'
```
Expected: `22.04`.

- [ ] **Step 2: Point a test provider config at the VM**

Run:
```bash
IP=$(multipass info tico-test | awk '/IPv4/{print $2}')
cat > deploy/providers/tico-test.conf <<EOF
SSH_HOST=$IP
SSH_USER=ubuntu
PROVIDER=ticotest
PROVIDER_NAME=Tico Test
PUBLIC_HOST=$IP
SEGMENT_BASE_URL=http://$IP:27703/
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
EOF
# multipass key auth:
multipass exec tico-test -- bash -c "echo '$(cat ~/.ssh/id_ed25519.pub)' >> ~/.ssh/authorized_keys"
```

- [ ] **Step 3: Probe, then bootstrap twice (idempotency)**

Run:
```bash
./deploy/tico probe tico-test
./deploy/tico bootstrap tico-test
./deploy/tico bootstrap tico-test    # second run must be clean, no errors
```
Expected: both bootstrap runs exit 0; the second changes nothing destructive.

- [ ] **Step 4: Verify DB is loopback-only and schema applied**

Run:
```bash
multipass exec tico-test -- bash -c 'ss -tlnp | grep 3306'          # expect 127.0.0.1:3306 only
nc -z -w3 "$IP" 3306 && echo "REACHABLE (BAD)" || echo "refused (good)"
# apply schema built from panel, then re-apply to prove idempotency:
# (assumes schema.sql available locally from a CI artifact or local dotnet ef run)
```
Expected: MariaDB bound to `127.0.0.1`; external connect refused.

- [ ] **Step 5: Deploy a real artifact, then force a rollback**

Run:
```bash
# Using a locally-built artifact dir with the dll + schema.sql:
./deploy/tico deploy tico-test --tag 1.0.0 --artifact ./publish
./deploy/tico status tico-test                       # health 200
# Simulate a bad release: deploy an artifact whose dll exits immediately, expect auto-rollback
./deploy/tico deploy tico-test --tag 1.0.1-broken --artifact ./broken || echo "rolled back as expected"
./deploy/tico status tico-test                       # still on 1.0.0, health 200
```
Expected: bad deploy auto-rolls-back; node returns to `1.0.0` and health 200.

- [ ] **Step 6: Record results and tear down**

Run:
```bash
multipass delete tico-test && multipass purge
rm -f deploy/providers/tico-test.conf
```
Write the observed outputs into `deploy/tests/integration.md`, then:
```bash
git add deploy/tests/integration.md
git commit -m "test(deploy): VM integration checklist and recorded results"
```

---

## Self-Review

**Spec coverage:**
- §3 framework-dependent publish + apt ffmpeg → Tasks 8, 11, Global Constraints. ✓
- §4.1 nginx (27701→1234, 27703 segments) → Task 3 template, Task 8 install. ✓
- §4.2 layout + per-client appsettings → Tasks 3, 8. ✓
- §4.3 loopback MariaDB, generated password, EF idempotent schema → Tasks 8, 11, 13. ✓
- §4.4 systemd (Type=simple, LimitNOFILE/TasksMax, dotnet dll) → Task 3 template. ✓
- §5 CI build → Task 11. ✓
- §6 tool commands → Tasks 4,7,8,9,10. ✓
- §7 safe update (preflight/baseline/swap/verify/rollback, dry-run) → Task 9. ✓
- §7.3 RUNBOOK → Task 12. ✓
- §8 ports → Task 4. ✓
- §9 testing (shellcheck, idempotency, schema, rollback, config-points-local, db-not-off-box) → per-task lint + Task 13. ✓

**Corrections captured vs. the design doc:** health route is `/api/health` (not `/health`); DB port defaults to 3306 (fibraencasa's 4447 was that node's own choice); `SegmentBaseUrl` is an explicit per-provider value because the two production nodes disagree; shared `Jwt` secrets come from a gitignored `shared.env` rather than a committed template.

**Type/name consistency:** `deploy_rollback_to`, `deploy_verify`, `deploy_run_swap_and_verify`, `remote`, `remote_sudo`, `push`, `remote_health`, `remote_fresh_stream_count`, `config_load`/`config_get`, `render_template`, `build_ports_report`, `ffmpeg_parse_version`/`ffmpeg_version_warning` are used consistently across tasks. `deploy_health`/`deploy_fresh` seams are defined in Task 9 and stubbed in its tests.

**Open items flagged, not blocking:** EF `--project`/`--startup-project` paths (Task 11) confirmed on first CI run; repo migration to GitHub + `PANEL_REPO_TOKEN` is a prerequisite documented in the spec.
