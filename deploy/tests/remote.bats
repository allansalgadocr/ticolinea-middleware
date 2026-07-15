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
