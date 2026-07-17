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
  # the previous tag must appear in a symlink command after failure
  mock_calls_joined | grep -q "releases/1.0.0"
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
