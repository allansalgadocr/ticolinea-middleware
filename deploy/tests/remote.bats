#!/usr/bin/env bats
load helpers

setup() {
  load_lib common.sh
  load_lib remote.sh
  FAKEBIN="$(mktemp -d)"
  cat > "$FAKEBIN/ssh" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*"
EOF
  chmod +x "$FAKEBIN/ssh"
  PATH="$FAKEBIN:$PATH"
}
teardown() { rm -rf "$FAKEBIN"; }

@test "_tico_real_runner passes host then command with no stray --" {
  run _tico_real_runner "u@h" echo hi
  [ "$status" -eq 0 ]
  [[ "$output" == *"u@h echo hi"* ]]
  [[ "$output" != *"-- echo"* ]]
}

@test "remote routes through the runner as user@host" {
  SSH_USER=u SSH_HOST=h run remote echo hi
  [ "$status" -eq 0 ]
  [[ "$output" == *"u@h echo hi"* ]]
}

@test "remote_sudo prepends sudo on the remote side" {
  SSH_USER=u SSH_HOST=h run remote_sudo systemctl restart x
  [ "$status" -eq 0 ]
  [[ "$output" == *"u@h sudo systemctl restart x"* ]]
}

@test "remote_recovered_stream_ids gates on the marker, selects -newer, extracts the ID set" {
  # The remote command must (a) yield an EMPTY set when the marker is absent —
  # fail-safe, no `if` branch emits anything — and (b) select only segments
  # strictly newer than the marker, never an -mmin window (which would still
  # see the OLD process's segments during the verify window), then reduce
  # basenames ({StreamId}_{seq}.ts) to the distinct stream-ID set. It carries
  # the same pipefail + dir-assert as the baseline capture (consistency;
  # errors surface) even though its failure direction is fail-safe.
  SSH_USER=u SSH_HOST=h PROVIDER=acme run remote_recovered_stream_ids
  [ "$status" -eq 0 ]
  [[ "$output" == *"set -o pipefail"* ]]
  [[ "$output" == *"[ -d /srv/acme/streams ] || exit 9"* ]]
  [[ "$output" == *"-f /srv/acme/.tico-deploy-marker"* ]]
  [[ "$output" == *"-newer /srv/acme/.tico-deploy-marker"* ]]
  [[ "$output" == *"-printf '%f\\n'"* ]]
  [[ "$output" == *"sed 's/_.*//'"* ]]
  [[ "$output" == *"sort -u"* ]]
  [[ "$output" != *"-mmin"* ]]
  [[ "$output" != *"wc -l"* ]]
  [[ "$output" != *"2>/dev/null"* ]]
}

@test "remote_fresh_stream_ids fails closed: pipefail, dir assert, no error suppression" {
  # Pre-swap baseline only: the -mmin window is correct BEFORE the restart
  # (the marker doesn't exist yet for this deploy) and must reduce segment
  # basenames to the distinct stream-ID set, not a count. Because an empty
  # baseline relaxes verification to health-only, capture failures must exit
  # nonzero instead of reading as an idle node: pipefail so a find failure
  # isn't laundered by sort, an explicit streams-dir assert, and no
  # 2>/dev/null anywhere — errors must surface.
  SSH_USER=u SSH_HOST=h PROVIDER=acme run remote_fresh_stream_ids
  [ "$status" -eq 0 ]
  [[ "$output" == *"set -o pipefail"* ]]
  [[ "$output" == *"[ -d /srv/acme/streams ] || exit 9"* ]]
  [[ "$output" == *"/srv/acme/streams"* ]]
  [[ "$output" == *"-mmin -1"* ]]
  [[ "$output" == *"-printf '%f\\n'"* ]]
  [[ "$output" == *"sed 's/_.*//'"* ]]
  [[ "$output" == *"sort -u"* ]]
  [[ "$output" != *"wc -l"* ]]
  [[ "$output" != *"2>/dev/null"* ]]
}

@test "remote_fresh_stream_ids propagates a remote failure as its own exit status" {
  # The local capture-then-emit shape must not launder the remote exit code
  # through `| tr` (a pipeline's status comes from its last command). A
  # failing remote means a failing helper — that is what cmd_deploy's
  # fail-closed guard keys on.
  failing_runner() { return 9; }
  TICO_RUNNER=failing_runner SSH_USER=u SSH_HOST=h PROVIDER=acme run remote_fresh_stream_ids
  [ "$status" -ne 0 ]
  [ -z "$output" ]
}

@test "password auth routes through sshpass -e ssh with the password opts" {
  cat > "$FAKEBIN/sshpass" <<'EOF'
#!/usr/bin/env bash
printf 'sshpass %s\n' "$*"
EOF
  chmod +x "$FAKEBIN/sshpass"
  AUTH_METHOD=password run _tico_real_runner u@h echo hi
  [ "$status" -eq 0 ]
  # Base password opts stay contiguous; multiplexing opts are appended after them.
  [[ "$output" == *"sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no -o ConnectTimeout=10 "* ]]
  [[ "$output" == *"u@h echo hi"* ]]
}

@test "ask auth routes through sshpass with the password opts (same mechanics as password)" {
  cat > "$FAKEBIN/sshpass" <<'EOF'
#!/usr/bin/env bash
printf 'sshpass %s\n' "$*"
EOF
  chmod +x "$FAKEBIN/sshpass"
  AUTH_METHOD=ask run _tico_real_runner u@h echo hi
  [ "$status" -eq 0 ]
  [[ "$output" == *"sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no -o ConnectTimeout=10 "* ]]
  [[ "$output" == *"u@h echo hi"* ]]
}

@test "computed ssh opts include the ControlMaster multiplexing trio (key mode)" {
  TICO_CONTROL_DIR="$FAKEBIN/cm" run _tico_ssh_opts
  [ "$status" -eq 0 ]
  [[ "$output" == *"-o BatchMode=yes -o ConnectTimeout=10"* ]]
  [[ "$output" == *"-o ControlMaster=auto"* ]]
  [[ "$output" == *"-o ControlPath=$FAKEBIN/cm/tico-cm-%C"* ]]
  [[ "$output" == *"-o ControlPersist=10m"* ]]
}

@test "computed ssh opts include the multiplexing trio in password mode too" {
  AUTH_METHOD=password TICO_CONTROL_DIR="$FAKEBIN/cm" run _tico_ssh_opts
  [ "$status" -eq 0 ]
  [[ "$output" == *"-o PreferredAuthentications=password"* ]]
  [[ "$output" == *"-o ControlMaster=auto"* ]]
  [[ "$output" == *"-o ControlPath=$FAKEBIN/cm/tico-cm-%C"* ]]
  [[ "$output" == *"-o ControlPersist=10m"* ]]
}

@test "remote_sudo password mode uses sudo -S --prompt= with the password before the script" {
  # One line per arg so the exact argv is observable. There must be NO `-p ''`:
  # ssh flattens argv into a string and an empty '' collapses, which would make
  # `-p` swallow the next token on the remote (usage error). `--prompt=` is a
  # single word, so it survives the flattening AND silences sudo's prompt —
  # the visible `[sudo] password for ...:` baited operators into typing the
  # password into the local echoing tty. See remote.sh.
  fake_runner() { shift; for a in "$@"; do printf 'ARG:%s\n' "$a"; done; cat; }
  TICO_RUNNER=fake_runner SSH_USER=u SSH_HOST=h SUDO_PASSWORD=topsecret
  output="$(printf 'echo remote-hi\n' | remote_sudo 'bash -s')"
  # argv is exactly: sudo -S --prompt= bash -s  (no -p, no empty arg)
  [[ "$output" == *$'ARG:sudo\nARG:-S\nARG:--prompt=\nARG:bash -s\n'* ]]
  [[ "$output" != *$'ARG:-p\n'* ]]
  # the password line precedes the caller's script on stdin
  pw_line="$(printf '%s\n' "$output" | grep -n '^topsecret$' | cut -d: -f1)"
  script_line="$(printf '%s\n' "$output" | grep -n '^echo remote-hi$' | cut -d: -f1)"
  [ "$pw_line" -lt "$script_line" ]
}

@test "remote_sudo key mode sends plain sudo with no -S and no password on stdin" {
  fake_runner() { shift; printf 'ARGS[%s]\n' "$*"; cat; }
  TICO_RUNNER=fake_runner SSH_USER=u SSH_HOST=h
  output="$(printf '' | remote_sudo systemctl restart x)"
  [[ "$output" == *"ARGS[sudo systemctl restart x]"* ]]
  [[ "$output" != *"-S"* ]]
  [[ "$output" != *"-p ''"* ]]
}

@test "runner retries once on ssh exit 255 and succeeds" {
  # Production pattern: a WireGuard flap kills one connection mid-run
  # (Permission denied / transport error, ssh exit 255) while the calls
  # before and after are fine. One delayed retry heals it.
  cat > "$FAKEBIN/ssh" <<'FAKE'
#!/usr/bin/env bash
marker="${TICO_TEST_MARKER:?}"
if [ ! -f "$marker" ]; then touch "$marker"; exit 255; fi
printf 'attempt2 %s\n' "$*"
FAKE
  chmod +x "$FAKEBIN/ssh"
  export TICO_TEST_MARKER="$FAKEBIN/flap-once"
  run _tico_real_runner u@h echo hi
  [ "$status" -eq 0 ]
  [[ "$output" == *"attempt2"* ]]
  [[ "$output" == *"u@h echo hi"* ]]
}

@test "runner retry replays piped stdin (sudo password + script survive the flap)" {
  cat > "$FAKEBIN/ssh" <<'FAKE'
#!/usr/bin/env bash
marker="${TICO_TEST_MARKER:?}"
if [ ! -f "$marker" ]; then cat >/dev/null; touch "$marker"; exit 255; fi
printf 'ARGS:%s\n' "$*"; sed 's/^/STDIN:/'
FAKE
  chmod +x "$FAKEBIN/ssh"
  export TICO_TEST_MARKER="$FAKEBIN/flap-stdin"
  output="$(printf 'secret\necho remote-script\n' | TICO_STDIN_PAYLOAD=1 _tico_real_runner u@h sudo -S bash -s)"
  [[ "$output" == *"ARGS:"*"u@h sudo -S bash -s"* ]]
  [[ "$output" == *"STDIN:secret"* ]]
  [[ "$output" == *"STDIN:echo remote-script"* ]]
}

@test "runner does not retry a remote command's own nonzero exit" {
  cat > "$FAKEBIN/ssh" <<'FAKE'
#!/usr/bin/env bash
count="${TICO_TEST_MARKER:?}.count"
echo x >> "$count"
exit 7
FAKE
  chmod +x "$FAKEBIN/ssh"
  export TICO_TEST_MARKER="$FAKEBIN/no-retry"
  run _tico_real_runner u@h false
  [ "$status" -eq 7 ]
  [ "$(wc -l < "$FAKEBIN/no-retry.count" | tr -d ' ')" -eq 1 ]
}
