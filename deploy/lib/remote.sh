# shellcheck shell=bash
: "${TICO_RUNNER:=_tico_real_runner}"
: "${TICO_SSH_OPTS:=-o BatchMode=yes -o ConnectTimeout=10}"
# Password-auth opts: BatchMode is deliberately absent — it would suppress the
# prompt sshpass feeds. Pubkey off + PreferredAuthentications=password forces
# the password path even on a box that also offers a key.
: "${TICO_SSH_OPTS_PASSWORD:=-o PreferredAuthentications=password -o PubkeyAuthentication=no -o ConnectTimeout=10}"

# True when the active auth method drives SSH through sshpass (password or ask).
# Both share the same remote mechanics; only the password source differs.
_tico_uses_password() {
  case "${AUTH_METHOD:-key}" in password|ask) return 0 ;; *) return 1 ;; esac
}

# ControlMaster multiplexing opts, shared by every SSH invocation (runner + push).
# The first connection authenticates and opens a master socket; every later call
# in the run — and back-to-back runs within ControlPersist — reuses it, so
# password/ask auth prompts once and key auth skips repeated handshakes. `%C`
# hashes (host,port,user,...) into a short, collision-free socket name.
_tico_mux_opts() {
  local dir="${TICO_CONTROL_DIR:-$HOME/.ssh}"
  # A caller-provided TICO_CONTROL_DIR (e.g. a temp dir) is ours to create; the
  # default $HOME/.ssh is assumed to already exist and is left untouched.
  if [ -n "${TICO_CONTROL_DIR:-}" ]; then
    mkdir -p "$dir" 2>/dev/null || true
    chmod 700 "$dir" 2>/dev/null || true
  fi
  printf -- '-o ControlMaster=auto -o ControlPath=%s/tico-cm-%%C -o ControlPersist=10m' "$dir"
}

# Echo the ssh option string for the active auth method, with multiplexing opts
# appended. AUTH_METHOD is set by config_load; default (unset) is key auth.
_tico_ssh_opts() {
  local base
  if _tico_uses_password; then
    base="$TICO_SSH_OPTS_PASSWORD"
  else
    base="$TICO_SSH_OPTS"
  fi
  printf '%s %s' "$base" "$(_tico_mux_opts)"
}

_tico_real_runner() { # host, command...
  local host="$1"; shift
  local opts; opts="$(_tico_ssh_opts)"
  _tico_mux_arm  # tear the master down when the run exits
  if _tico_uses_password; then
    # shellcheck disable=SC2086,SC2029
    sshpass -e ssh $opts "$host" "$@"
  else
    # shellcheck disable=SC2086,SC2029
    ssh $opts "$host" "$@"
  fi
}

# Arm a one-shot EXIT trap that closes the multiplexing master. Only the real
# runner calls this, so a run that overrides TICO_RUNNER (the test seam) never
# arms it. Idempotent — the trap is installed once per process.
_tico_mux_arm() {
  [ -z "${_TICO_MUX_ARMED:-}" ] || return 0
  _TICO_MUX_ARMED=1
  trap '_tico_mux_close' EXIT
}

# Close the master socket for the current target, if one was opened. Best-effort:
# a missing socket or already-closed master is not an error.
_tico_mux_close() {
  [ -n "${_TICO_MUX_ARMED:-}" ] || return 0
  [ -n "${SSH_HOST:-}" ] || return 0
  local target="${SSH_HOST}" opts
  [ -n "${SSH_USER:-}" ] && target="${SSH_USER}@${SSH_HOST}"
  opts="$(_tico_ssh_opts)"
  # shellcheck disable=SC2086
  ssh $opts -O exit "$target" >/dev/null 2>&1 || true
}

remote()      { "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" "$@"; }

remote_sudo() {
  # With a sudo password (password auth against a box that lacks NOPASSWD), feed
  # it to `sudo -S` on stdin ahead of any caller-supplied script. Without one
  # (key mode / NOPASSWD), keep the original plain `sudo`.
  #
  # NOTE: `--prompt=`, never `-p ''`. ssh flattens argv into a single string
  # for the remote shell, and an empty '' argument collapses to nothing — so
  # `sudo -S -p '' bash -c ...` arrives as `sudo -S -p bash -c ...`, where `-p`
  # swallows `bash` and `-c` becomes an invalid sudo option (usage error).
  # `--prompt=` is a single word, so it survives the flattening and sets an
  # empty prompt. The prompt must be silenced: it is not "harmless" on stderr —
  # a visible `[sudo] password for ...:` during a quiet stretch (mysql, the
  # verify window) bait an operator into typing the password into the local
  # tty, which echoes it in plaintext (nothing on this side disables echo).
  if [ -n "${SUDO_PASSWORD:-}" ]; then
    if [ -t 0 ]; then
      # Plain-args call, no piped script: send just the password.
      printf '%s\n' "$SUDO_PASSWORD" | "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" sudo -S --prompt= "$@"
    else
      # Heredoc/piped stdin: password first, then the caller's script — `sudo -S`
      # consumes the first line, the invoked `bash -s` reads the rest.
      { printf '%s\n' "$SUDO_PASSWORD"; cat; } | "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" sudo -S --prompt= "$@"
    fi
  else
    "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" sudo "$@"
  fi
}

push() { # local, remote-path
  local opts; opts="$(_tico_ssh_opts)"
  if _tico_uses_password; then
    rsync -az -e "sshpass -e ssh $opts" "$1" "${SSH_USER}@${SSH_HOST}:$2"
  else
    rsync -az -e "ssh $opts" "$1" "${SSH_USER}@${SSH_HOST}:$2"
  fi
}

remote_health() {
  # Result is compared exactly to "200"; strip any CR the ssh channel appends.
  remote 'curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:1234/api/health || true' | tr -d '\r'
}

remote_fresh_stream_count() {
  # /srv/${PROVIDER}/streams — namespaced like every other on-box
  # path. All callers (cmd_deploy's baseline, cmd_status) run after
  # config_load has exported PROVIDER, so it's in scope here.
  # Result is used in numeric -ge/-gt comparisons; strip spaces AND any CR so a
  # stray carriage return can't turn the count into a non-integer.
  remote "find /srv/${PROVIDER}/streams -name '*.ts' -mmin -1 2>/dev/null | wc -l | tr -d ' '" | tr -d '\r'
}

remote_fresh_after_marker() {
  # Like remote_fresh_stream_count, but counts only segments written AFTER the
  # deploy marker (touched by the swap heredoc immediately before systemctl
  # restart). Post-swap verification must use this, never the -mmin window:
  # during the ~60s verify window the OLD process's segments are still <1min
  # old, so a dead new process could pass verification on them. `-newer` the
  # marker cannot be satisfied by anything written before the restart.
  # No marker on the box (pre-marker tooling, manual restart) counts as 0 —
  # fail-safe: verification never passes on output it can't attribute to the
  # new process. Same hygiene as above: strip spaces AND any CR so the result
  # survives numeric comparison.
  remote "if [ -f /srv/${PROVIDER}/.tico-deploy-marker ]; then find /srv/${PROVIDER}/streams -name '*.ts' -newer /srv/${PROVIDER}/.tico-deploy-marker 2>/dev/null | wc -l | tr -d ' '; else echo 0; fi" | tr -d '\r'
}
