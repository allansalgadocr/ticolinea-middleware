#!/usr/bin/env bats
# Fixture globals (SSH_HOST, PROVIDER, ...) are read by the sourced lib/*.sh
# functions, and the update_fetch_release / cmd_deploy stubs are invoked
# indirectly from update.sh — both read as "unused" to a single-file shellcheck.
# shellcheck disable=SC2034,SC2329
load helpers

setup() {
  load_lib common.sh
  load_lib remote.sh
  # shellcheck source=/dev/null
  source "$TICO_ROOT/lib/commands/update.sh"
  mock_runner_reset
  TICO_RUNNER=mock_runner
  SSH_HOST=host SSH_USER=u PROVIDER=acme
  _tico_resolve_paths
}

# ---------- pure naming ----------

@test "release tag and asset match what build-node.yml publishes" {
  [ "$(update_release_tag 1.5.3)" = "node-v1.5.3" ]
  [ "$(update_release_asset 1.5.3)" = "node-1.5.3.zip" ]
}

@test "default version is this checkout's VERSION, whitespace stripped" {
  run update_default_version
  [ "$status" -eq 0 ]
  [ "$output" = "$(tr -d ' \t\n\r' < "$TICO_ROOT/../VERSION")" ]
}

@test "repo slug is derived from origin, not hardcoded" {
  run update_repo_slug
  [ "$status" -eq 0 ]
  [ "$output" = "allansalgadocr/ticolinea-middleware" ]
}

# ---------- artifact validation ----------

@test "a directory with both schema.sql and the dll validates" {
  local d="$BATS_TEST_TMPDIR/ok"
  mkdir -p "$d"; touch "$d/schema.sql" "$d/ticolinea.stream.service.dll"
  run update_validate_artifact "$d"
  [ "$status" -eq 0 ]
}

@test "a publish output with no schema.sql is refused" {
  local d="$BATS_TEST_TMPDIR/nodb"
  mkdir -p "$d"; touch "$d/ticolinea.stream.service.dll"
  run update_validate_artifact "$d"
  [ "$status" -ne 0 ]
  [[ "$output" == *"schema.sql"* ]]
}

@test "a source-code ZIP with no dll is refused" {
  local d="$BATS_TEST_TMPDIR/src"
  mkdir -p "$d"; touch "$d/schema.sql"
  run update_validate_artifact "$d"
  [ "$status" -ne 0 ]
  [[ "$output" == *"dll"* ]]
}

@test "a missing directory is refused" {
  run update_validate_artifact "$BATS_TEST_TMPDIR/nope"
  [ "$status" -ne 0 ]
}

# ---------- orchestration ----------

@test "update hands the staged artifact to deploy under the requested version" {
  local d="$BATS_TEST_TMPDIR/staged"
  mkdir -p "$d"; touch "$d/schema.sql" "$d/ticolinea.stream.service.dll"
  config_load() { :; }
  deploy_current_release() { echo 1.5.2; }
  cmd_deploy() { printf '%s\n' "$*" > "$BATS_TEST_TMPDIR/deploy-args"; return 0; }

  run cmd_update acme --version 1.5.3 --artifact "$d"
  [ "$status" -eq 0 ]
  run cat "$BATS_TEST_TMPDIR/deploy-args"
  [[ "$output" == *"--tag 1.5.3"* ]]
  [[ "$output" == *"--artifact $d"* ]]
}

@test "--dry-run is forwarded to deploy" {
  local d="$BATS_TEST_TMPDIR/staged"
  mkdir -p "$d"; touch "$d/schema.sql" "$d/ticolinea.stream.service.dll"
  config_load() { :; }
  deploy_current_release() { echo 1.5.2; }
  cmd_deploy() { printf '%s\n' "$*" > "$BATS_TEST_TMPDIR/deploy-args"; return 0; }

  run cmd_update acme --version 1.5.3 --artifact "$d" --dry-run
  [ "$status" -eq 0 ]
  run cat "$BATS_TEST_TMPDIR/deploy-args"
  [[ "$output" == *"--dry-run"* ]]
}

@test "an operator-supplied --artifact survives a successful update" {
  local d="$BATS_TEST_TMPDIR/mine"
  mkdir -p "$d"; touch "$d/schema.sql" "$d/ticolinea.stream.service.dll"
  config_load() { :; }
  deploy_current_release() { echo 1.5.2; }
  cmd_deploy() { return 0; }

  run cmd_update acme --version 1.5.3 --artifact "$d"
  [ "$status" -eq 0 ]
  [ -d "$d" ]
}

@test "a failed update keeps the staged artifact and names it" {
  local d="$BATS_TEST_TMPDIR/keepme"
  mkdir -p "$d"; touch "$d/schema.sql" "$d/ticolinea.stream.service.dll"
  config_load() { :; }
  deploy_current_release() { echo 1.5.2; }
  cmd_deploy() { return 1; }

  run cmd_update acme --version 1.5.3 --artifact "$d"
  [ "$status" -ne 0 ]
  [[ "$output" == *"$d"* ]]
  [ -d "$d" ]
}

@test "an unusable --artifact fails before deploy is ever called" {
  local d="$BATS_TEST_TMPDIR/empty"
  mkdir -p "$d"
  config_load() { :; }
  deploy_current_release() { echo 1.5.2; }
  cmd_deploy() { printf 'called' > "$BATS_TEST_TMPDIR/deploy-called"; return 0; }

  run cmd_update acme --version 1.5.3 --artifact "$d"
  [ "$status" -ne 0 ]
  [ ! -f "$BATS_TEST_TMPDIR/deploy-called" ]
}

@test "update refuses an unknown option rather than guessing" {
  config_load() { :; }
  run cmd_update acme --nope
  [ "$status" -ne 0 ]
  [[ "$output" == *"unknown option"* ]]
}
