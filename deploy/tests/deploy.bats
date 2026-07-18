#!/usr/bin/env bats
# Fixture globals below (SSH_HOST, PROVIDER, TICO_VERIFY_TRIES, ...) are read
# by the sourced lib/*.sh functions, not this file, and the deploy_health /
# deploy_fresh stubs are invoked indirectly from deploy.sh — both patterns
# read as "unused" to a single-file shellcheck pass.
# shellcheck disable=SC2034,SC2329
load helpers

setup() {
  load_lib common.sh
  load_lib remote.sh
  # shellcheck source=/dev/null
  source "$TICO_ROOT/lib/commands/deploy.sh"
  mock_runner_reset
  TICO_RUNNER=mock_runner
  SSH_HOST=host SSH_USER=u PROVIDER=acme
  # This fixture bypasses config_load (it sets PROVIDER directly), so
  # TICO_RELEASES_DIR/TICO_CURRENT_LINK — normally resolved by
  # _tico_resolve_paths right after config_load — must be resolved here too,
  # for the tests below that call deploy_run_swap_and_verify/deploy_rollback_to
  # directly instead of going through cmd_deploy.
  _tico_resolve_paths
  # Keep the verify retry loop instant in tests; production defaults (12x5s)
  # only apply when these are unset.
  TICO_VERIFY_TRIES=1
  TICO_VERIFY_SLEEP=0
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

@test "swap sends the atomic symlink+restart sequence over stdin, targeting the new tag" {
  # The swap now goes over stdin (heredoc), so its script lands in the recorded
  # call via mock_runner's stdin capture — not in argv. The safety-critical
  # ordering (stage a .tmp link, atomically mv -T it into place, then restart the
  # unit) must still be exactly this, and it must target the NEW tag.
  MOCK_OUT=""
  deploy_health() { echo 200; }
  deploy_fresh() { echo 5; }
  status=0
  deploy_run_swap_and_verify "1.2.0" "1.1.0" || status=$?
  [ "$status" -eq 0 ]
  local calls; calls="$(mock_calls_joined)"
  echo "$calls" | grep -q 'ln -sfn /opt/acme/releases/1.2.0 /opt/acme/current.tmp'
  echo "$calls" | grep -q 'mv -T /opt/acme/current.tmp /opt/acme/current'
  echo "$calls" | grep -q 'systemctl restart ticolinea-streaming'
}

@test "auto-rollback repoints current to the previous tag on verify failure" {
  MOCK_OUT=""
  deploy_health() { echo 503; }   # force verify failure
  deploy_fresh() { echo 0; }
  PREVIOUS_TAG="1.0.0"
  # Not `run`: bats `run` captures via command substitution, which forks a
  # subshell — MOCK_CALLS mutations made by mock_runner inside it would be
  # lost, making the grep below always fail regardless of behavior. Capture
  # the status manually so the call log survives in this shell.
  status=0
  deploy_run_swap_and_verify "1.1.0" "1.0.0" || status=$?
  [ "$status" -ne 0 ]
  # The rollback script now arrives over stdin (heredoc), captured by mock_runner.
  # It must repoint current at the PREVIOUS tag using the same atomic
  # ln -sfn .tmp -> mv -T -> restart sequence the forward swap uses.
  local calls; calls="$(mock_calls_joined)"
  echo "$calls" | grep -q 'ln -sfn /opt/acme/releases/1.0.0 /opt/acme/current.tmp'
  echo "$calls" | grep -q 'mv -T /opt/acme/current.tmp /opt/acme/current'
  echo "$calls" | grep -q 'systemctl restart ticolinea-streaming'
}

@test "dry-run previews the plan even when the node reports unhealthy" {
  # FIX A: --dry-run must reach the preview regardless of node health.
  deploy_health() { echo 503; }
  deploy_fresh() { echo 0; }
  local art; art="$(mktemp -d)"
  : > "$art/schema.sql"
  : > "$art/ticolinea.stream.service.dll"
  # slug 'example' resolves to the committed providers/example.conf fixture.
  run cmd_deploy example --tag 9.9.9 --artifact "$art" --dry-run
  rm -rf "$art"
  [ "$status" -eq 0 ]
  [[ "$output" == *"[dry-run] would"* ]]
}

@test "staging + schema apply arrive over stdin (tag dir, appsettings layering, chown, mysql pipe)" {
  # Drive a full (non-dry) cmd_deploy with the network I/O stubbed out: push()
  # is a no-op (no real rsync), health/fresh are stubbed so verify passes and no
  # rollback fires. The staging block and the schema-apply now go over stdin, so
  # assert their content via mock_runner's stdin capture.
  push() { :; }
  deploy_health() { echo 200; }
  deploy_fresh() { echo 5; }
  MOCK_OUT=""
  local art; art="$(mktemp -d)"
  : > "$art/schema.sql"
  : > "$art/ticolinea.stream.service.dll"
  status=0
  cmd_deploy example --tag 2.0.0 --artifact "$art" || status=$?
  rm -rf "$art"
  [ "$status" -eq 0 ]
  local calls; calls="$(mock_calls_joined)"
  # Staging steps (each on its own line under the heredoc):
  echo "$calls" | grep -q 'mkdir -p /opt/acme/releases/2.0.0'
  echo "$calls" | grep -q 'cp -a /tmp/release-2.0.0/. /opt/acme/releases/2.0.0/'
  echo "$calls" | grep -q 'cp /opt/acme/config/appsettings.acme.json /opt/acme/releases/2.0.0/appsettings.acme.json'
  echo "$calls" | grep -q 'chown -R ticolinea:ticolinea /opt/acme/releases/2.0.0'
  # Schema apply pipes schema.sql into mysql for the provider DB:
  echo "$calls" | grep -q 'mysql -uroot acme-streaming < /opt/acme/releases/2.0.0/schema.sql'
}

@test "regression guard: no arg-form 'remote_sudo bash -c' remains in deploy.sh" {
  # The bug was `remote_sudo bash -c "A && B"` — three argv words that real ssh
  # flattens, breaking the `-c` payload. Every multi-command remote call must be
  # the stdin heredoc form. This grep must find nothing.
  ! grep -n 'remote_sudo bash -c' "$TICO_ROOT/lib/commands/deploy.sh"
}

@test "deploy_select_prunable keeps newest 5 and never prunes current" {
  # newest-first: v9..v1; current = v9 (in top 5) -> prune v4 v3 v2 v1
  run bash -c 'printf "v9\nv8\nv7\nv6\nv5\nv4\nv3\nv2\nv1\n" | { source deploy/lib/commands/deploy.sh; deploy_select_prunable v9; }'
  [ "$status" -eq 0 ]
  [ "$output" = "$(printf "v4\nv3\nv2\nv1")" ]
}

@test "deploy_select_prunable protects current when it aged out of the top 5 (post-rollback)" {
  # current = v1 (oldest, rolled back to) -> keep newest 5 + v1 -> prune only v4 v3 v2
  run bash -c 'printf "v9\nv8\nv7\nv6\nv5\nv4\nv3\nv2\nv1\n" | { source deploy/lib/commands/deploy.sh; deploy_select_prunable v1; }'
  [ "$status" -eq 0 ]
  [ "$output" = "$(printf "v4\nv3\nv2")" ]
}
