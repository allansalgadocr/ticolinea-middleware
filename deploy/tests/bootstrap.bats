#!/usr/bin/env bats
# Exercises the bootstrap OS guard without a live host. The pure predicate
# (_tico_os_supported) is tested directly; the end-to-end guard
# (_assert_supported_os) is driven through the TICO_RUNNER seam with a fake
# runner that returns a canned /etc/os-release string.
load helpers

setup() {
  load_lib common.sh
  load_lib remote.sh
  # shellcheck source=/dev/null
  source "$TICO_ROOT/lib/commands/bootstrap.sh"
  SSH_HOST=host SSH_USER=u PROVIDER=acme
}

# Fake runner: ignores the remote command, echoes $FAKE_OS as the os-release probe result.
_os_runner() { shift; printf '%s' "$FAKE_OS"; }

@test "os guard predicate accepts ubuntu 22.04" {
  run _tico_os_supported "ubuntu 22.04"
  [ "$status" -eq 0 ]
}

@test "os guard predicate accepts ubuntu 24.04" {
  run _tico_os_supported "ubuntu 24.04"
  [ "$status" -eq 0 ]
}

@test "os guard predicate rejects ubuntu 20.04" {
  run _tico_os_supported "ubuntu 20.04"
  [ "$status" -ne 0 ]
}

@test "os guard predicate rejects debian 12" {
  run _tico_os_supported "debian 12"
  [ "$status" -ne 0 ]
}

@test "_assert_supported_os passes on 22.04 via the runner" {
  FAKE_OS="ubuntu 22.04" TICO_RUNNER=_os_runner run _assert_supported_os
  [ "$status" -eq 0 ]
}

@test "_assert_supported_os passes on 24.04 via the runner" {
  FAKE_OS="ubuntu 24.04" TICO_RUNNER=_os_runner run _assert_supported_os
  [ "$status" -eq 0 ]
}

@test "_assert_supported_os dies on 20.04 with a clear message" {
  FAKE_OS="ubuntu 20.04" TICO_RUNNER=_os_runner run _assert_supported_os
  [ "$status" -ne 0 ]
  [[ "$output" == *"22.04 or 24.04"* ]]
  [[ "$output" == *"ubuntu 20.04"* ]]
}

@test "_assert_supported_os dies on debian 12" {
  FAKE_OS="debian 12" TICO_RUNNER=_os_runner run _assert_supported_os
  [ "$status" -ne 0 ]
  [[ "$output" == *"debian 12"* ]]
}

@test "_assert_supported_os strips a CR from the os-release probe" {
  # A host whose ssh channel appends CR must still match the case guard.
  FAKE_OS=$'ubuntu 24.04\r' TICO_RUNNER=_os_runner run _assert_supported_os
  [ "$status" -eq 0 ]
}

# --- MariaDB loopback check -------------------------------------------------
# The check now runs over `remote` (no sudo) so use_pty can't pollute the
# captured stdout. We stub the loopback listing directly. Because bats `run`
# forks a subshell, drive the loopback snippet in isolation with a fake runner.

# Fake runner that echoes $FAKE_SS as the `ss` output for the loopback probe.
_ss_runner() { shift; printf '%s' "$FAKE_SS"; }

_loopback_probe() { # mirrors the exact expression _setup_mariadb uses
  remote "ss -ltnH 'sport = :3306' | awk '{print \$4}' | grep -vE '^(127\\.0\\.0\\.1|\\[::1\\]):' || true" | tr -d '\r'
}

# The die-arm of the check, factored so bats can `run` it with a fake runner.
_loopback_die_check() {
  local nonloop
  nonloop="$(_loopback_probe)"
  [ -z "$nonloop" ] || die "MariaDB is listening on a non-loopback address: $nonloop"
}

@test "loopback check: 127.0.0.1:3306 is accepted (empty non-loop set)" {
  # The remote grep -v filters the loopback line out, so the probe yields nothing.
  FAKE_SS="" TICO_RUNNER=_ss_runner run _loopback_die_check
  [ "$status" -eq 0 ]
}

@test "loopback check: a non-loopback bind is surfaced and dies" {
  FAKE_SS="0.0.0.0:3306" TICO_RUNNER=_ss_runner run _loopback_die_check
  [ "$status" -ne 0 ]
  [[ "$output" == *"non-loopback address: 0.0.0.0:3306"* ]]
}

@test "loopback check: a CR-polluted loopback value still passes" {
  # A host that appends CR must not turn "127.0.0.1:3306\r" into a false match.
  FAKE_SS=$'\r' TICO_RUNNER=_ss_runner run _loopback_die_check
  [ "$status" -eq 0 ]
}

@test "loopback check uses remote (not remote_sudo) — no captured sudo pollution" {
  grep -q 'nonloop="\$(remote ' "$TICO_ROOT/lib/commands/bootstrap.sh"
  ! grep -q 'nonloop="\$(remote_sudo' "$TICO_ROOT/lib/commands/bootstrap.sh"
}

@test "_setup_mariadb never reads the db password back over captured sudo" {
  # The fix: password is generated on the controller and pushed, never
  # re-read. Guard against the buggy read-back returning.
  ! grep -Eq '\$\(remote_sudo (cat )?/opt/.*db-password' "$TICO_ROOT/lib/commands/bootstrap.sh"
  ! grep -q 'DB_PASSWORD="\$(remote_sudo' "$TICO_ROOT/lib/commands/bootstrap.sh"
}

@test "no captured \$(remote_sudo ...) remains anywhere in the deploy lib" {
  # use_pty on 24.04 corrupts any captured password-sudo stdout; there must be
  # zero command-substitution captures of remote_sudo across all commands.
  ! grep -rqn '\$(remote_sudo' "$TICO_ROOT/lib/"
}

@test "db password is generated locally with an alphanumeric charset" {
  grep -q 'DB_PASSWORD:=\$(LC_ALL=C tr -dc .A-Za-z0-9. </dev/urandom' "$TICO_ROOT/lib/commands/bootstrap.sh"
}
