# shellcheck shell=bash
: "${TICO_RUNNER:=_tico_real_runner}"
: "${TICO_SSH_OPTS:=-o BatchMode=yes -o ConnectTimeout=10}"

_tico_real_runner() { # host, command...
  local host="$1"; shift
  # shellcheck disable=SC2086,SC2029
  ssh $TICO_SSH_OPTS "$host" "$@"
}

remote()      { "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" "$@"; }
remote_sudo() { "$TICO_RUNNER" "${SSH_USER}@${SSH_HOST}" sudo "$@"; }

push() { # local, remote-path
  rsync -az -e "ssh $TICO_SSH_OPTS" "$1" "${SSH_USER}@${SSH_HOST}:$2"
}

remote_health() {
  remote 'curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:1234/api/health || true'
}

remote_fresh_stream_count() {
  remote 'find /srv/ticolinea/streams -name "*.ts" -mmin -1 2>/dev/null | wc -l | tr -d " "'
}
