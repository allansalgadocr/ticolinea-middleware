#!/usr/bin/env bats
# Fixture globals below (SSH_HOST, PROVIDER, TICO_VERIFY_TRIES, ...) are read
# by the sourced lib/*.sh functions, not this file, and the deploy_health /
# deploy_baseline_ids / deploy_recovered_ids stubs are invoked indirectly from
# deploy.sh — both patterns read as "unused" to a single-file shellcheck pass.
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
  # Default identity stub: behave like a real box — `current` resolves to the
  # last swap the mock recorded. Tests that need a divergent box state
  # (swap "succeeded" but didn't take) override this per-test.
  deploy_current_release() {
    mock_calls_joined 2>/dev/null | sed -n 's|.*ln -sfn [^ ]*/releases/\([^ ]*\) .*|\1|p' | tail -1
  }
}

@test "verify passes when health is 200 and every baseline stream recovered" {
  MOCK_OUT="200"
  # health returns 200; the post-restart ID set is stubbed separately:
  deploy_health() { echo 200; }
  deploy_recovered_ids() { printf '10\n20\n30\n'; }
  run deploy_verify "10 20 30"
  [ "$status" -eq 0 ]
}

@test "verify fails when health is 503 even if every stream recovered" {
  deploy_health() { echo 503; }
  deploy_recovered_ids() { printf '10\n20\n30\n'; }
  run deploy_verify "10 20 30"
  [ "$status" -ne 0 ]
}

@test "verify fails when no baseline stream recovered (empty post-marker set)" {
  # Also THE marker regression: right after swap+restart the OLD process's
  # segments are still <1min old, but they are older than the marker, so the
  # recovered set is empty — a dead new process can never pass on stale output.
  deploy_health() { echo 200; }
  deploy_recovered_ids() { :; }
  run deploy_verify "10 20 30"
  [ "$status" -ne 0 ]
  [[ "$output" == *"recovered=0/3"* ]]
}

@test "per-channel: baseline recovers with fewer total segments than the old aggregate needed" {
  # Finding 3b (false-rollback regression): with 6s segments a 60s-window
  # baseline saw ~10 segments/channel, but the post-restart observation window
  # observes at most ~9 — an aggregate count gate rolled back HEALTHY deploys
  # by construction. Per-channel, one post-marker segment per baseline ID is
  # enough: 3 recovered segments total here, where the old aggregate rule
  # (post-marker total >= baseline total) would have demanded far more.
  deploy_health() { echo 200; }
  deploy_recovered_ids() { printf 'A\nB\nC\n'; }  # one segment each, all channels alive
  run deploy_verify "A B C"
  [ "$status" -eq 0 ]
}

@test "per-channel: one dead channel fails verification however busy the others are" {
  # Finding 3a: an aggregate count let fast channels mask a dead one. The
  # recovered SET contains only A and B — C never wrote a post-marker segment,
  # so verification must fail and name it.
  deploy_health() { echo 200; }
  deploy_recovered_ids() { printf 'A\nB\n'; }
  run deploy_verify "A B C"
  [ "$status" -ne 0 ]
  [[ "$output" == *"recovered=2/3"* ]]
  [[ "$output" == *"missing: C"* ]]
}

@test "verify passes health-only on an empty baseline (first deploy / spec B)" {
  # RUNBOOK spec B: a freshly-provisioned node has no channel rows, so it is
  # healthy but serves nothing. An empty baseline set must verify on health
  # alone — the recovered set (also empty) is not consulted.
  deploy_health() { echo 200; }
  deploy_recovered_ids() { :; }
  run deploy_verify ""
  [ "$status" -eq 0 ]
}

@test "verify health-only still requires health: empty baseline with 503 fails" {
  deploy_health() { echo 503; }
  deploy_recovered_ids() { :; }
  run deploy_verify ""
  [ "$status" -ne 0 ]
}

@test "verify logs health per failed attempt" {
  deploy_health() { echo 503; }
  deploy_recovered_ids() { :; }
  run deploy_verify "A"
  [ "$status" -ne 0 ]
  [[ "$output" == *"health=503"* ]]
  [[ "$output" == *"recovered=?/1"* ]]
}

@test "verify caps the reported missing list at 5 IDs" {
  deploy_health() { echo 200; }
  deploy_recovered_ids() { :; }
  run deploy_verify "1 2 3 4 5 6 7"
  [ "$status" -ne 0 ]
  [[ "$output" == *"recovered=0/7"* ]]
  [[ "$output" == *"missing: 1 2 3 4 5 (+2 more)"* ]]
  [[ "$output" != *"missing: 1 2 3 4 5 6"* ]]
}

@test "deploy_missing_ids matches whole IDs, never substrings" {
  # ID 5 must not be satisfied by ID 55's segments (or vice versa).
  run deploy_missing_ids "5 55" "55"
  [ "$status" -eq 0 ]
  [ "$output" = "5" ]
}

@test "deploy_missing_ids accepts newline-separated sets (sort -u output shape)" {
  run deploy_missing_ids "$(printf 'A\nB\nC\n')" "$(printf 'A\nC\n')"
  [ "$status" -eq 0 ]
  [ "$output" = "B" ]
}

@test "deploy_missing_ids is empty when baseline is a subset of recovered" {
  run deploy_missing_ids "A B" "$(printf 'A\nB\nEXTRA\n')"
  [ "$status" -eq 0 ]
  [ -z "$output" ]
}

@test "swap sends the atomic symlink+restart sequence over stdin, targeting the new tag" {
  # The swap now goes over stdin (heredoc), so its script lands in the recorded
  # call via mock_runner's stdin capture — not in argv. The safety-critical
  # ordering (stage a .tmp link, atomically mv -T it into place, restart the
  # unit, then touch the verify marker) must still be exactly this, and it
  # must target the NEW tag.
  MOCK_OUT=""
  deploy_health() { echo 200; }
  deploy_recovered_ids() { printf 'A\n'; }
  BASELINE_IDS="A"
  status=0
  deploy_run_swap_and_verify "1.2.0" "1.1.0" || status=$?
  [ "$status" -eq 0 ]
  local calls; calls="$(mock_calls_joined)"
  echo "$calls" | grep -q 'ln -sfn /opt/acme/releases/1.2.0 /opt/acme/current.tmp'
  echo "$calls" | grep -q 'mv -T /opt/acme/current.tmp /opt/acme/current'
  echo "$calls" | grep -q 'systemctl restart ticolinea-streaming'
  echo "$calls" | grep -q 'touch /srv/acme/.tico-deploy-marker'
  # The restart must precede the touch — the old service's ffmpeg children
  # keep writing until systemctl stops the cgroup, so a marker touched before
  # the restart would count the old process's final segments as post-restart
  # recovery evidence (verification could pass on a dead new process).
  # index()-based, so the literal path needs no regex escaping.
  printf '%s\n' "$calls" | awk '
    index($0, "touch /srv/acme/.tico-deploy-marker") { if (!t) t = NR }
    index($0, "systemctl restart ticolinea-streaming") { if (!r) r = NR }
    END { exit !(t && r && r < t) }'
}

@test "auto-rollback repoints current to the previous tag on verify failure" {
  MOCK_OUT=""
  deploy_health() { echo 503; }   # force verify failure
  deploy_recovered_ids() { :; }
  BASELINE_IDS="A B"
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

@test "first deploy (no previous): healthy node with zero streams verifies" {
  MOCK_OUT=""
  deploy_health() { echo 200; }
  deploy_recovered_ids() { :; }
  BASELINE_IDS=""
  status=0
  deploy_run_swap_and_verify "1.0.0" "" || status=$?
  [ "$status" -eq 0 ]
}

@test "redeploy of a zero-baseline node verifies health-only (spec B window persists)" {
  # A node past its first deploy can still have no channel rows (spec B lasts
  # until the panel sync exists). Updating it must not demand a stream that
  # has never existed — an empty baseline SET, not just "no previous",
  # relaxes verification to health-only.
  MOCK_OUT=""
  deploy_health() { echo 200; }
  deploy_recovered_ids() { :; }
  BASELINE_IDS=""
  status=0
  deploy_run_swap_and_verify "1.0.1" "1.0.0" || status=$?
  [ "$status" -eq 0 ]
}

@test "first-deploy verify failure skips rollback and leaves the release in place" {
  MOCK_OUT=""
  deploy_health() { echo 503; }
  deploy_recovered_ids() { :; }
  BASELINE_IDS=""
  status=0
  deploy_run_swap_and_verify "1.0.0" "" 2>"$BATS_TEST_TMPDIR/stderr" || status=$?
  [ "$status" -ne 0 ]
  grep -q 'no previous release' "$BATS_TEST_TMPDIR/stderr"
  # Exactly one restart (the forward swap). A rollback attempt would add a
  # second one — and the old bug was dying inside deploy_rollback_to instead
  # of returning, which kills this test before the assertions run.
  local calls; calls="$(mock_calls_joined)"
  [ "$(printf '%s\n' "$calls" | grep -c 'systemctl restart ticolinea-streaming')" -eq 1 ]
}

@test "cmd_deploy first-deploy failure says the new tag was left in place (no bogus rollback claim)" {
  push() { :; }
  deploy_health() { echo 503; }
  deploy_baseline_ids() { :; }   # pre-swap baseline: empty ID set
  deploy_recovered_ids() { :; }  # post-swap verification: nothing recovered
  MOCK_OUT=""
  # Preflight: health != 200 must land in the "no running node yet" branch,
  # so the systemctl is-active probe has to fail.
  MOCK_FAIL_ON="is-active"
  local art; art="$(mktemp -d)"
  : > "$art/schema.sql"
  : > "$art/ticolinea.stream.service.dll"
  run cmd_deploy example --tag 1.0.0 --artifact "$art"
  rm -rf "$art"
  [ "$status" -ne 0 ]
  [[ "$output" != *"rolled back to none"* ]]
  [[ "$output" == *"left in place"* ]]
  [[ "$output" == *"tico status"* ]]
}

@test "cmd_deploy fails closed when baseline capture fails: dies before any swap" {
  # Round-3 finding: `|| true` on the capture converted ssh drops, permission
  # errors, and a missing streams dir into an EMPTY baseline — which verify
  # treats as the legitimate spec B "serves nothing" case and passes on
  # health alone. A capture failure on a busy node must refuse to deploy.
  push() { :; }
  deploy_health() { echo 200; }
  deploy_baseline_ids() { return 9; }  # capture failure, NOT an empty set
  deploy_recovered_ids() { :; }
  MOCK_OUT=""
  MOCK_LOG="$BATS_TEST_TMPDIR/calls.log"
  local art; art="$(mktemp -d)"
  : > "$art/schema.sql"
  : > "$art/ticolinea.stream.service.dll"
  run cmd_deploy example --tag 3.0.0 --artifact "$art"
  rm -rf "$art"
  [ "$status" -ne 0 ]
  [[ "$output" == *"could not capture active-stream baseline"* ]]
  [[ "$output" == *"refusing to deploy"* ]]
  # The die precedes deploy_run_swap_and_verify: the persisted call log
  # (staging calls land in it, so it exists) must show no symlink swap and no
  # restart — the serving release was left untouched.
  [ -f "$MOCK_LOG" ]
  ! grep -q 'ln -sfn' "$MOCK_LOG"
  ! grep -q 'systemctl restart' "$MOCK_LOG"
}

@test "cmd_deploy: empty-but-successful baseline still verifies health-only (spec B intact)" {
  # The fail-closed capture must not break the legitimate case: exit 0 with
  # an empty set means the node genuinely serves nothing, and the deploy
  # verifies on health alone.
  push() { :; }
  deploy_health() { echo 200; }
  deploy_baseline_ids() { :; }   # exit 0, empty set
  deploy_recovered_ids() { :; }
  MOCK_OUT=""
  local art; art="$(mktemp -d)"
  : > "$art/schema.sql"
  : > "$art/ticolinea.stream.service.dll"
  run cmd_deploy example --tag 3.1.0 --artifact "$art"
  rm -rf "$art"
  [ "$status" -eq 0 ]
  [[ "$output" == *"Deploy complete"* ]]
}

@test "dry-run previews the plan even when the node reports unhealthy" {
  # FIX A: --dry-run must reach the preview regardless of node health.
  deploy_health() { echo 503; }
  deploy_baseline_ids() { :; }
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
  deploy_baseline_ids() { printf '7\n9\n'; }   # pre-swap baseline (mmin window — ID set)
  deploy_recovered_ids() { printf '7\n9\n'; }  # post-swap: every baseline ID recovered
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

@test "a failed swap aborts the deploy: no false success, no rollback" {
  # Production incident: ssh auth died exactly at the swap step; the function
  # (called from an if-condition, so set -e suppressed) fell through to a
  # health-only verify that attested the OLD release -> 'Deploy verified'.
  MOCK_OUT=""
  MOCK_FAIL_ON="ln -sfn /opt/acme/releases/2.0.0"
  deploy_health() { echo 200; }
  BASELINE_IDS=""
  status=0
  deploy_run_swap_and_verify "2.0.0" "1.0.0" 2>"$BATS_TEST_TMPDIR/err" || status=$?
  [ "$status" -ne 0 ]
  grep -q "Swap step FAILED" "$BATS_TEST_TMPDIR/err"
  # And no rollback: the old release is still serving untouched.
  ! mock_calls_joined | grep -q 'ln -sfn /opt/acme/releases/1.0.0'
}

@test "identity mismatch after swap fails the deploy without rollback" {
  # The swap command reported success but `current` still resolves elsewhere:
  # health alone must never bless the deploy, and rolling back would bounce a
  # healthy old release for nothing.
  MOCK_OUT=""
  deploy_health() { echo 200; }
  deploy_current_release() { echo "1.0.0"; }
  BASELINE_IDS=""
  status=0
  deploy_run_swap_and_verify "2.0.0" "1.0.0" 2>"$BATS_TEST_TMPDIR/err" || status=$?
  [ "$status" -ne 0 ]
  grep -q "swap did not take" "$BATS_TEST_TMPDIR/err"
  # Exactly one ln -sfn (the attempted swap) — no second one for a rollback.
  [ "$(mock_calls_joined | grep -c 'ln -sfn')" -eq 1 ]
}
