#!/usr/bin/env bats
bats_require_minimum_version 1.5.0
load helpers

setup() {
  load_lib common.sh
  load_lib template.sh
  TMP="$(mktemp -d)"
}
teardown() { rm -rf "$TMP"; }

@test "render_template substitutes set vars" {
  # shellcheck disable=SC2016
  echo 'hello ${PROVIDER_NAME} on ${PUBLIC_HOST}' > "$TMP/t.tmpl"
  PROVIDER_NAME="Acme TV" PUBLIC_HOST="iptv.acme.cr" run render_template "$TMP/t.tmpl"
  [ "$status" -eq 0 ]
  [ "$output" = "hello Acme TV on iptv.acme.cr" ]
}

@test "render_template fails on an unset placeholder" {
  # shellcheck disable=SC2016
  echo 'db=${DB_PASSWORD}' > "$TMP/t.tmpl"
  run render_template "$TMP/t.tmpl"
  [ "$status" -ne 0 ]
  [[ "$output" == *"DB_PASSWORD"* ]]
}

@test "appsettings template renders valid JSON with local DB" {
  export SSH_HOST=x SSH_USER=x PROVIDER=acme PROVIDER_NAME="Acme TV" \
         PUBLIC_HOST=iptv.acme.cr \
         PANEL_API_URL="http://tv.play-latino.com:27702/api/v2" \
         DB_NAME=acme-streaming DB_USER=streamingservice DB_PASSWORD=s3cret \
         JWT_PUBLIC_KEY='-----BEGIN PUBLIC KEY-----\nAAA\n-----END PUBLIC KEY-----\n' \
         PANEL_API_KEY=apikey123 \
         CONSOLE_SEED_PASSWORD=Seed123Password
  render_template "$TICO_ROOT/templates/appsettings.provider.json.tmpl" > "$TMP/out.json"
  run node -e "JSON.parse(require('fs').readFileSync('$TMP/out.json','utf8'))"
  [ "$status" -eq 0 ]
  grep -q 'server=127.0.0.1' "$TMP/out.json"
  grep -q '"ProviderId": "acme"' "$TMP/out.json"
  # log4net LogDir override — bootstrap creates /srv/<slug>/logs for the service user
  grep -q '"Directory": "/srv/acme/logs"' "$TMP/out.json"
  run ! grep -q 'rds.amazonaws.com' "$TMP/out.json"
  # SegmentBaseUrl serves the m3u8/API on 27701; StreamsBaseUrl serves .ts segments on 27703.
  # These must not invert (see LiveController vs StreamsController usage).
  grep -Eq '"SegmentBaseUrl": "http://iptv\.acme\.cr:27701"' "$TMP/out.json"
  grep -Eq '"StreamsBaseUrl": "http://iptv\.acme\.cr:27703"' "$TMP/out.json"
  # Output-progress watchdog: enabled on provider nodes (OutputWatchdogService).
  run node -e "process.exit(JSON.parse(require('fs').readFileSync('$TMP/out.json','utf8')).Watchdog.Enabled===true?0:1)"
  [ "$status" -eq 0 ]
  # Node console bootstrap credential — without it the owner cannot reach /admin,
  # since ConsoleSchema falls back to a random password logged only once.
  grep -q '"SeedPassword": "Seed123Password"' "$TMP/out.json"
  # FfmpegManagedDiscontinuities: enabled fleet-wide (operator decision 2026-07-18);
  # revert per node via its on-box appsettings if devices misbehave (see RUNBOOK).
  grep -q '"FfmpegManagedDiscontinuities": true' "$TMP/out.json"
}

@test "render_template preserves special characters in values" {
  # shellcheck disable=SC2016
  echo 'pw=${DB_PASSWORD};key=${JWT_PUBLIC_KEY}' > "$TMP/s.tmpl"
  DB_PASSWORD='a/b+c=d&e' JWT_PUBLIC_KEY='-----BEGIN-----\nX/Y+Z=\n-----END-----\n' \
    run render_template "$TMP/s.tmpl"
  [ "$status" -eq 0 ]
  [ "$output" = 'pw=a/b+c=d&e;key=-----BEGIN-----\nX/Y+Z=\n-----END-----\n' ]
}

@test "render_template fails on an empty (set-but-blank) placeholder" {
  # shellcheck disable=SC2016
  echo 'db=${DB_PASSWORD}' > "$TMP/e.tmpl"
  DB_PASSWORD="" run render_template "$TMP/e.tmpl"
  [ "$status" -ne 0 ]
  [[ "$output" == *"DB_PASSWORD"* ]]
}
