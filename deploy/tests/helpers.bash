# shellcheck shell=bash
TICO_ROOT="$(cd "$(dirname "${BATS_TEST_FILENAME}")/.." && pwd)"
export TICO_ROOT

# shellcheck disable=SC1090
load_lib() { source "$TICO_ROOT/lib/$1"; }

# Mock runner: records every remote invocation into MOCK_CALLS, prints $MOCK_OUT.
# Used by deploy.bats to test orchestration without a live host.
mock_runner_reset() { MOCK_CALLS=(); MOCK_OUT=""; MOCK_FAIL_ON=""; }
mock_runner() {
  shift  # discard host arg
  MOCK_CALLS+=("$*")
  if [ -n "$MOCK_FAIL_ON" ] && [[ "$*" == *"$MOCK_FAIL_ON"* ]]; then
    return 1
  fi
  printf '%s' "$MOCK_OUT"
}
mock_calls_joined() { printf '%s\n' "${MOCK_CALLS[@]}"; }
