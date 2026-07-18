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
