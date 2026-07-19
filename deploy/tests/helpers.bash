# shellcheck shell=bash
TICO_ROOT="$(cd "$(dirname "${BATS_TEST_FILENAME}")/.." && pwd)"
export TICO_ROOT

# shellcheck disable=SC1090
load_lib() { source "$TICO_ROOT/lib/$1"; }

# Mock runner: records every remote invocation into MOCK_CALLS, prints $MOCK_OUT.
# Used by deploy.bats to test orchestration without a live host.
#
# For heredoc-form calls (`remote_sudo 'bash -s' <<REMOTE ...`), ssh passes the
# script over STDIN, so the observable payload is no longer in argv — the argv is
# just `sudo bash -s`. When the argv shows `bash -s`, drain stdin and fold the
# script into the recorded call so tests can assert on its content. We only cat
# on `bash -s` calls so the many plain `remote "cmd"` calls (no stdin redirect)
# never block on an empty/inherited stdin.
# MOCK_LOG (optional): a file path to ALSO append every record to. bats `run`
# captures via a subshell, so MOCK_CALLS mutations made under `run` are lost to
# the test shell — a file survives. Set it in tests that must assert on calls
# made (or not made) by a function they can only invoke via `run` (e.g. one
# that die()s, which would exit the test shell if called directly).
mock_runner_reset() { MOCK_CALLS=(); MOCK_OUT=""; MOCK_FAIL_ON=""; MOCK_LOG=""; }
mock_runner() {
  shift  # discard host arg
  local rec="$*"
  case "$*" in
    *"bash -s"*) rec="$rec"$'\n'"$(cat)";;
  esac
  MOCK_CALLS+=("$rec")
  if [ -n "${MOCK_LOG:-}" ]; then
    printf '%s\n' "$rec" >> "$MOCK_LOG"
  fi
  if [ -n "$MOCK_FAIL_ON" ] && [[ "$rec" == *"$MOCK_FAIL_ON"* ]]; then
    return 1
  fi
  printf '%s' "$MOCK_OUT"
}
mock_calls_joined() { printf '%s\n' "${MOCK_CALLS[@]}"; }
